using System.Collections.Concurrent;
using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

internal readonly record struct BorrowLeaseGrant(BorrowLeaseHandle Handle, BorrowLeaseLifetime Lifetime);

internal readonly record struct BorrowLeaseAuthoritySnapshot(
    BorrowLeaseHandle Handle,
    RegionOwner Owner,
    RegionOwner Borrower,
    long ByteLength,
    BorrowLeaseLifetime Lifetime);

internal sealed record RegionAuthorityInspectionRecord(
    RegionDescriptor Region,
    BorrowLeaseHandle? Borrow,
    RegionOwner? Borrower,
    RegionBackingLeaseDescriptor? BackingLease,
    bool PlatformMappingReserved,
    bool ExternalBorrowReadGrantReserved,
    IReadOnlyList<RegionUseDescriptor> Uses,
    IReadOnlyList<RegionDamageDescriptorV1> Damage);

public sealed partial class RegionAuthority
{
    private sealed class RegionUseRecord
    {
        public required RegionUseHandle Handle { get; init; }
        public required RegionHandle Region { get; init; }
        public required RegionOwner Principal { get; init; }
        public required RegionUseRange Range { get; init; }
        public required RegionUseMode Mode { get; init; }
        public required MutationEpoch MutationEpoch { get; init; }
        public required RegionUseState State { get; set; }
        public ExternalOperationHandle? ReleaseOwnerOperation { get; init; }
        public MmioBufferPayload? ReleaseOwnerMmio { get; init; }
        public BorrowLeaseHandle? OriginalBorrowLease { get; init; }
    }

    private sealed class RegionRecord
    {
        public object Gate { get; } = new();
        public required RegionId Id { get; init; }
        public required RegionGeneration Generation { get; set; }
        public required RegionOwner Owner { get; set; }
        public required long ByteLength { get; init; }
        public required string ElementType { get; init; }
        public required RegionState State { get; set; }
        public required MutationEpoch MutationEpoch { get; set; }
        public RegionOwner? Borrower { get; set; }
        public BorrowLeaseGeneration BorrowGeneration { get; set; }
        public BorrowLeaseLifetime? BorrowLifetime { get; set; }
        public BorrowLeaseLifetime? OriginalBorrowLifetime { get; set; }
        public BorrowLeaseLifetime? OriginalWithdrawnBorrow { get; set; }
        public ITransferableOwnedPayload? Payload { get; set; }
        public bool OriginalPayloadExpected { get; set; }
        public ITransferableOwnedPayload? OriginalPayload { get; set; }
        public ITransferableOwnedPayload? OriginalWithdrawnPayload { get; set; }
        public bool PlatformMappingReserved { get; set; }
        public bool ExternalBorrowReadGrantReserved { get; set; }
        public RegionBackingLeaseDescriptor? BackingLease { get; set; }
        public bool BackingClosureStarted { get; set; }
        public int PendingBackingCreations { get; set; }
        public Dictionary<RegionUseId, RegionUseRecord> Uses { get; } = [];
        public Dictionary<ulong, RegionDamageDescriptorV1> Damage { get; } = [];
        public Dictionary<(string ProviderId, string FailureDomainId), DamageEvidenceHighWatermark> DamageEvidenceHighWatermarks { get; } = [];
        public ProtectedRegionLabelDescriptorV1? ProtectedLabel { get; set; }
    }

    private readonly ConcurrentDictionary<RegionId, RegionRecord> _regions = [];
    private readonly ConcurrentDictionary<RegionUseId, RegionRecord> _useIndex = [];
    private long _nextRegionId;
    private long _nextRegionUseId;
    private long _nextBackingLeaseId;
    private long _nextDamageId;

    private readonly record struct DamageEvidenceHighWatermark(
        ulong ProviderGeneration,
        ulong FailureDomainGeneration,
        ulong ObservationSequence);

    private static bool IsStaleDamageEvidence(
        FailureDomainScopeV1 scope,
        ulong observationSequence,
        DamageEvidenceHighWatermark highWatermark) =>
        // This consumer retains a monotonic namespace sequence across generation
        // changes. A provider sequence reset needs a separately admitted contour;
        // generation advance alone cannot erase replay history or close damage.
        scope.ProviderGeneration < highWatermark.ProviderGeneration ||
        scope.FailureDomainGeneration < highWatermark.FailureDomainGeneration ||
        observationSequence <= highWatermark.ObservationSequence;

    internal KernelResult<RegionDamageDescriptorV1> QuarantineSubrange(
        RegionHandle handle,
        RegionOwner owner,
        ProviderHealthEvidenceV1 evidence, RegionBackingLeaseHandle? requiredBacking = null)
    {
        FailureConsequenceV1 consequence;
        try { consequence = FailureConsequencePolicyV1.Project(evidence); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.InvalidMessage, exception.Message);
        }
        if (consequence.Kind != FailureConsequenceKindV1.RegionSubrangeQuarantine ||
            consequence.AffectedRange is not { } range)
            return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.InvalidMessage,
                "Provider evidence does not project to a Region subrange quarantine.");
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record))
            return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<RegionDescriptor> validation = ValidateCore(record, handle, owner, RegionState.Owned);
            if (!validation.IsSuccess)
                return KernelResult<RegionDamageDescriptorV1>.Fail(validation.Error, validation.Message!);
            if (requiredBacking is { } exact &&
                (record.BackingLease is not { } backing || backing.Handle != exact ||
                 backing.Region != handle || backing.Owner != owner || record.BackingClosureStarted))
                return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.StaleGeneration,
                    "Exact live backing is required at health consequence commit.");
            if (!IsValidRange(record, range))
                return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.InvalidRegionState,
                    "Provider-health affected range is outside the exact Region bounds.");
            (string ProviderId, string FailureDomainId) domainKey = (consequence.Scope.ProviderId, consequence.Scope.FailureDomainId);
            if (record.DamageEvidenceHighWatermarks.TryGetValue(domainKey, out DamageEvidenceHighWatermark highWatermark) &&
                IsStaleDamageEvidence(consequence.Scope, consequence.EvidenceObservationSequence,
                    highWatermark))
                return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.StaleGeneration,
                    "Provider-health evidence sequence is stale or replayed for this failure domain.");
            long identity = Interlocked.Increment(ref _nextDamageId);
            if (identity <= 0)
                return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.CapacityExhausted,
                    "Region damage identity space is exhausted.");
            KernelResult mutation = AdvanceMutation(record, invalidateActiveUses: false);
            if (!mutation.IsSuccess)
                return KernelResult<RegionDamageDescriptorV1>.Fail(mutation.Error, mutation.Message!);
            foreach (RegionUseRecord? use in record.Uses.Values.Where(use =>
                         use.State == RegionUseState.Active && RangesOverlap(use.Range, range)))
                use.State = RegionUseState.Invalidated;
            RegionDamageDescriptorV1 damage = new RegionDamageDescriptorV1(
                new RegionDamageHandle(checked((ulong)identity), 1), handle, consequence.Scope,
                consequence.EvidenceObservationSequence, range, RegionDamageStateV1.Quarantined);
            record.Damage.Add(damage.Handle.DamageId, damage);
            record.DamageEvidenceHighWatermarks[domainKey] = new(
                damage.FailureDomain.ProviderGeneration,
                damage.FailureDomain.FailureDomainGeneration,
                damage.EvidenceObservationSequence);
            return KernelResult<RegionDamageDescriptorV1>.Ok(damage);
        }
    }

    internal KernelResult<RegionDamageDescriptorV1> RecordFailureDomainReconfiguration(
        RegionDamageHandle damageHandle,
        FailureDomainScopeV1 nextScope,
        ulong evidenceObservationSequence)
    {
        try { nextScope.Validate(); }
        catch (ArgumentException exception)
        {
            return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.InvalidMessage, exception.Message);
        }
        foreach (RegionRecord record in OrderedRecords())
        {
            lock (record.Gate)
            {
                if (!record.Damage.TryGetValue(damageHandle.DamageId, out RegionDamageDescriptorV1? damage)) continue;
                if (damage.Handle != damageHandle)
                    return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.StaleGeneration,
                        "Region damage generation is stale.");
                (string ProviderId, string FailureDomainId) domainKey = (nextScope.ProviderId, nextScope.FailureDomainId);
                if (!StringComparer.Ordinal.Equals(damage.FailureDomain.ProviderId, nextScope.ProviderId) ||
                    !StringComparer.Ordinal.Equals(damage.FailureDomain.FailureDomainId, nextScope.FailureDomainId) ||
                    damage.FailureDomain.ProviderGeneration == ulong.MaxValue ||
                    damage.FailureDomain.FailureDomainGeneration == ulong.MaxValue ||
                    nextScope.ProviderGeneration != damage.FailureDomain.ProviderGeneration + 1 ||
                    nextScope.FailureDomainGeneration != damage.FailureDomain.FailureDomainGeneration + 1 ||
                    evidenceObservationSequence <= damage.EvidenceObservationSequence ||
                    (record.DamageEvidenceHighWatermarks.TryGetValue(domainKey, out DamageEvidenceHighWatermark highWatermark) &&
                     IsStaleDamageEvidence(nextScope, evidenceObservationSequence, highWatermark)) ||
                    damage.Handle.Generation == ulong.MaxValue)
                    return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.StaleGeneration,
                        "Reconfiguration must advance the exact provider, failure-domain, evidence, and damage generations.");
                KernelResult mutation = AdvanceMutation(record, invalidateActiveUses: false);
                if (!mutation.IsSuccess)
                    return KernelResult<RegionDamageDescriptorV1>.Fail(mutation.Error, mutation.Message!);
                RegionDamageDescriptorV1 rebound = damage with
                {
                    Handle = damage.Handle with { Generation = damage.Handle.Generation + 1 },
                    FailureDomain = nextScope,
                    EvidenceObservationSequence = evidenceObservationSequence,
                };
                record.Damage[damageHandle.DamageId] = rebound;
                record.DamageEvidenceHighWatermarks[domainKey] = new(
                    nextScope.ProviderGeneration, nextScope.FailureDomainGeneration,
                    evidenceObservationSequence);
                return KernelResult<RegionDamageDescriptorV1>.Ok(rebound);
            }
        }
        return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.RegionNotFound,
            "Region damage record was not found.");
    }

    internal KernelResult<RegionBackingReplacementReceiptV1> RecordAuthoritativeBackingReplacement(
        RegionOwner owner,
        RegionBackingReplacementEvidenceV1 evidence)
    {
        try { evidence.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return KernelResult<RegionBackingReplacementReceiptV1>.Fail(KernelError.InvalidMessage, exception.Message);
        }
        foreach (RegionRecord record in OrderedRecords())
        {
            lock (record.Gate)
            {
            if (!record.Damage.TryGetValue(evidence.Damage.DamageId, out RegionDamageDescriptorV1? damage)) continue;
                RegionHandle liveRegion = new RegionHandle(record.Id, record.Generation);
                KernelResult<RegionDescriptor> validation = ValidateCore(record, liveRegion, owner, RegionState.Owned);
            if (!validation.IsSuccess)
                return KernelResult<RegionBackingReplacementReceiptV1>.Fail(validation.Error, validation.Message!);
            if (damage.Handle != evidence.Damage || damage.Region != liveRegion)
                return KernelResult<RegionBackingReplacementReceiptV1>.Fail(
                    KernelError.StaleGeneration, "Replacement evidence identifies a stale damage or Region generation.");
                FailureDomainScopeV1 next = evidence.NextFailureDomain;
                (string ProviderId, string FailureDomainId) domainKey = (next.ProviderId, next.FailureDomainId);
            if (damage.Range != evidence.Range ||
                !StringComparer.Ordinal.Equals(damage.FailureDomain.ProviderId, next.ProviderId) ||
                !StringComparer.Ordinal.Equals(damage.FailureDomain.FailureDomainId, next.FailureDomainId) ||
                damage.FailureDomain.ProviderGeneration == ulong.MaxValue ||
                damage.FailureDomain.FailureDomainGeneration == ulong.MaxValue ||
                next.ProviderGeneration != damage.FailureDomain.ProviderGeneration + 1 ||
                next.FailureDomainGeneration != damage.FailureDomain.FailureDomainGeneration + 1 ||
                evidence.EvidenceObservationSequence <= damage.EvidenceObservationSequence ||
                (record.DamageEvidenceHighWatermarks.TryGetValue(domainKey, out DamageEvidenceHighWatermark highWatermark) &&
                 IsStaleDamageEvidence(next, evidence.EvidenceObservationSequence, highWatermark)))
                return KernelResult<RegionBackingReplacementReceiptV1>.Fail(
                    KernelError.StaleGeneration,
                    "Replacement must bind the exact range and advance provider, failure-domain, and evidence generations.");
            if (record.PlatformMappingReserved || record.ExternalBorrowReadGrantReserved || record.BackingLease is not null)
                return KernelResult<RegionBackingReplacementReceiptV1>.Fail(
                    KernelError.PlatformBindingActive,
                    "External Region reservations must close before authoritative backing replacement.");
            if (record.Uses.Values.Any(use => use.State != RegionUseState.Released &&
                    RangesOverlap(use.Range, damage.Range)))
                return KernelResult<RegionBackingReplacementReceiptV1>.Fail(
                    KernelError.ExternalEffectUncontained,
                    "Every overlapping Region use must be explicitly released before backing replacement.");
                KernelResult mutation = AdvanceMutation(record, invalidateActiveUses: false);
            if (!mutation.IsSuccess)
                return KernelResult<RegionBackingReplacementReceiptV1>.Fail(mutation.Error, mutation.Message!);

            record.Damage.Remove(damage.Handle.DamageId);
            record.DamageEvidenceHighWatermarks[domainKey] = new(
                next.ProviderGeneration, next.FailureDomainGeneration,
                evidence.EvidenceObservationSequence);
            return KernelResult<RegionBackingReplacementReceiptV1>.Ok(new(
                RegionBackingReplacementReceiptV1.CurrentVersion, damage.Handle, liveRegion,
                record.MutationEpoch, next, evidence.EvidenceObservationSequence,
                evidence.ReplacementBackingGeneration, damage.Range));
            }
        }
        return KernelResult<RegionBackingReplacementReceiptV1>.Fail(
            KernelError.RegionNotFound, "Region damage record was not found.");
    }

    internal IReadOnlyList<RegionDamageDescriptorV1> SnapshotDamage() => OrderedRecords()
        .SelectMany(record =>
        {
            lock (record.Gate) return record.Damage.Values.ToArray();
        })
        .OrderBy(static damage => damage.Handle.DamageId)
        .ToArray();

    internal KernelResult<ProtectedRegionLabelDescriptorV1> AttachProtectedLabel(
        RegionHandle handle, RegionOwner owner, DataLabelV1 label)
    {
        try { label.Validate(); }
        catch (NotSupportedException exception)
        { return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.InvalidMessage, exception.Message); }
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record))
            return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<RegionDescriptor> validation = ValidateCore(record, handle, owner, RegionState.Owned);
            if (!validation.IsSuccess) return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(validation.Error, validation.Message!);
            if (HasUnreleasedUse(record))
                return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.RegionUseConflict,
                    "A protected label cannot be attached while the Region has an active use.");
            if (record.ProtectedLabel is not null)
                return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.InvalidTransition,
                    "Protected Region label already exists; use propagation or an authorized transition.");
            record.ProtectedLabel = new(handle, label, 1);
            return KernelResult<ProtectedRegionLabelDescriptorV1>.Ok(record.ProtectedLabel);
        }
    }

    internal KernelResult<ProtectedRegionLabelDescriptorV1> PropagateProtectedLabel(
        IReadOnlyList<RegionHandle> inputs, RegionHandle output, RegionOwner owner)
    {
        if (inputs is null || inputs.Count == 0)
            return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.InvalidMessage, "Protected propagation requires at least one input.");
        RegionId[] orderedIds = inputs.Append(output).Select(static item => item.RegionId).Distinct()
            .OrderBy(static item => item.Value).ToArray();
        List<RegionRecord> records = new List<RegionRecord>(orderedIds.Length);
        foreach (RegionId id in orderedIds)
        {
            if (!_regions.TryGetValue(id, out RegionRecord? record))
                return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.RegionNotFound, "Protected propagation Region was not found.");
            records.Add(record);
        }
        foreach (RegionRecord record in records) Monitor.Enter(record.Gate);
        try
        {
            DataLabelV1? joined = null;
            foreach (RegionHandle handle in inputs)
            {
                RegionRecord record = _regions[handle.RegionId];
                KernelResult<RegionDescriptor> validation = ValidateCore(record, handle, owner, RegionState.Owned);
                if (!validation.IsSuccess) return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(validation.Error, validation.Message!);
                if (record.ProtectedLabel is not { } input)
                    return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.PlatformDenied,
                        "An unlabeled input cannot enter the protected propagation contour.");
                joined = joined is null ? input.Label : DataLabelLatticeV1.Join(joined.Value, input.Label);
            }
            RegionRecord outputRecord = _regions[output.RegionId];
            KernelResult<RegionDescriptor> outputValidation = ValidateCore(outputRecord, output, owner, RegionState.Owned);
            if (!outputValidation.IsSuccess) return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(outputValidation.Error, outputValidation.Message!);
            if (HasUnreleasedUse(outputRecord))
                return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.RegionUseConflict,
                    "A protected output label cannot change while the Region has an active use.");
            if (outputRecord.ProtectedLabel is { } current)
                joined = DataLabelLatticeV1.Join(joined!.Value, current.Label);
            ulong nextGeneration = outputRecord.ProtectedLabel is { } prior
                ? checked(prior.LabelGeneration + 1) : 1UL;
            outputRecord.ProtectedLabel = new(output, joined!.Value, nextGeneration);
            return KernelResult<ProtectedRegionLabelDescriptorV1>.Ok(outputRecord.ProtectedLabel);
        }
        catch (OverflowException)
        { return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.CapacityExhausted, "Protected label generation is exhausted."); }
        finally { for (int index = records.Count - 1; index >= 0; index--) Monitor.Exit(records[index].Gate); }
    }

    internal KernelResult<ProtectedRegionLabelDescriptorV1> ApplyProtectedLabelTransition(
        ProtectedRegionLabelDescriptorV1 expected, RegionOwner owner, InformationFlowTransitionV1 transition)
    {
        try { transition.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.InvalidMessage, exception.Message); }
        if (!_regions.TryGetValue(expected.Region.RegionId, out RegionRecord? record))
            return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<RegionDescriptor> validation = ValidateCore(record, expected.Region, owner, RegionState.Owned);
            if (!validation.IsSuccess) return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(validation.Error, validation.Message!);
            if (HasUnreleasedUse(record))
                return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.RegionUseConflict,
                    "A protected label cannot transition while the Region has an active use.");
            if (record.ProtectedLabel != expected || expected.Label != transition.Source)
                return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.StaleGeneration, "Protected label binding is stale.");
            if (expected.LabelGeneration == ulong.MaxValue)
                return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.CapacityExhausted, "Protected label generation is exhausted.");
            record.ProtectedLabel = expected with { Label = transition.Target, LabelGeneration = expected.LabelGeneration + 1 };
            return KernelResult<ProtectedRegionLabelDescriptorV1>.Ok(record.ProtectedLabel);
        }
    }

    internal KernelResult<ProtectedRegionLabelDescriptorV1> QueryProtectedLabel(RegionHandle handle, RegionOwner owner)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record))
            return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<RegionDescriptor> validation = ValidateCore(record, handle, owner, RegionState.Owned);
            if (!validation.IsSuccess) return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(validation.Error, validation.Message!);
            return record.ProtectedLabel is { } label
                ? KernelResult<ProtectedRegionLabelDescriptorV1>.Ok(label)
                : KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.PlatformBindingNotFound, "Protected Region label was not found.");
        }
    }

    public KernelResult<RegionBackingLeaseDescriptor> ReserveBacking(RegionHandle handle, RegionOwner owner)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record))
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<RegionDescriptor> validation = ValidateCore(record, handle, owner, RegionState.Owned);
        if (!validation.IsSuccess) return KernelResult<RegionBackingLeaseDescriptor>.Fail(validation.Error, validation.Message!);
        if (record.Damage.Count != 0)
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.Quarantined,
                "Damaged Region cannot acquire a new backing lease before authoritative replacement.");
        if (record.Uses.Values.Any(use => use.State == RegionUseState.Invalidated))
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.RegionUseConflict,
                "An invalidated unreleased use forbids a new backing lease.");
        if (record.BackingLease is not null)
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.PlatformBindingActive, "The region already has an active backing lease.");
            long identity = Interlocked.Increment(ref _nextBackingLeaseId);
        if (identity <= 0)
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.CapacityExhausted, "Backing lease identity space is exhausted.");
            RegionBackingLeaseDescriptor lease = new RegionBackingLeaseDescriptor(new(new(checked((ulong)identity)), 1), handle, owner, record.ByteLength);
        record.BackingLease = lease;
        return KernelResult<RegionBackingLeaseDescriptor>.Ok(lease);
        }
    }

    public KernelResult<RegionBackingLeaseDescriptor> ValidateBacking(RegionBackingLeaseHandle handle, RegionOwner owner)
        => ValidateBackingCore(handle, owner, allowClosure: false);

    private KernelResult<RegionBackingLeaseDescriptor> ValidateBackingCore(
        RegionBackingLeaseHandle handle, RegionOwner owner, bool allowClosure)
    {
        RegionRecord? record = _regions.Values.FirstOrDefault(item =>
        {
            lock (item.Gate) return item.BackingLease?.Handle.LeaseId == handle.LeaseId;
        });
        if (record is null)
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.PlatformBindingNotFound, "Region backing lease was not found.");
        lock (record.Gate)
        {
        if (record.BackingLease is not { } lease)
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.PlatformBindingNotFound, "Region backing lease was not found.");
        if (lease.Handle != handle || lease.Region.Generation != record.Generation)
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.StaleGeneration, "Region backing lease generation is stale.");
        if (lease.Owner != owner)
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.WrongRegionOwner, "Region backing lease owner does not match.");
        if (record.BackingClosureStarted && !allowClosure)
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.PlatformBindingDraining,
                "Region backing closure forbids new backing effects.");
        return KernelResult<RegionBackingLeaseDescriptor>.Ok(lease);
        }
    }

    // Local reservation admission only; this records no provider closure evidence.
    internal KernelResult<RegionBackingLeaseDescriptor> BeginBackingCreation(
        RegionBackingLeaseHandle handle, RegionOwner owner)
    {
        KernelResult<RegionBackingLeaseDescriptor> validation = ValidateBacking(handle, owner);
        if (!validation.IsSuccess) return validation;
        RegionRecord record = _regions[validation.Value!.Region.RegionId];
        lock (record.Gate)
        {
            if (record.BackingLease != validation.Value || record.BackingClosureStarted)
                return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.PlatformBindingDraining,
                    "Backing changed or closure started before creation admission.");
            if (record.PendingBackingCreations == int.MaxValue)
                return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.CapacityExhausted,
                    "Backing creation admission capacity is exhausted.");
            record.PendingBackingCreations++;
            return validation;
        }
    }

    internal void EndBackingCreation(RegionBackingLeaseDescriptor admitted)
    {
        RegionRecord record = _regions[admitted.Region.RegionId];
        lock (record.Gate)
        {
            if (record.BackingLease != admitted || record.PendingBackingCreations <= 0)
                throw new InvalidOperationException("Exact backing creation admission was not retained.");
            record.PendingBackingCreations--;
        }
    }

    internal bool HasPendingBackingCreations(RegionOwner owner)
    {
        foreach (RegionRecord record in _regions.Values)
            lock (record.Gate)
                if (record.BackingLease?.Owner == owner && record.PendingBackingCreations != 0) return true;
        return false;
    }

    internal KernelResult BeginBackingClosure(RegionBackingLeaseHandle handle, RegionOwner owner)
    {
        KernelResult<RegionBackingLeaseDescriptor> validation = ValidateBackingCore(handle, owner, allowClosure: true);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        RegionRecord record = _regions[validation.Value!.Region.RegionId];
        lock (record.Gate)
        {
            if (record.BackingLease is not { } lease || lease.Handle != handle ||
                lease.Owner != owner || lease.Region.Generation != record.Generation)
                return KernelResult.Fail(KernelError.StaleGeneration, "Region backing changed before closure admission.");
            if (record.PlatformMappingReserved || record.ExternalBorrowReadGrantReserved)
                return KernelResult.Fail(KernelError.PlatformBindingActive,
                    "Region mapping/grant reservation must close before backing closure admission.");
            if (record.PendingBackingCreations != 0)
                return KernelResult.Fail(KernelError.PlatformBindingDraining,
                    "In-flight backing creation must publish its result before closure admission.");
            record.BackingClosureStarted = true;
            return KernelResult.Ok();
        }
    }

    public KernelResult ReleaseBacking(RegionBackingLeaseHandle handle, RegionOwner owner)
    {
        KernelResult<RegionBackingLeaseDescriptor> validation = ValidateBackingCore(handle, owner, allowClosure: true);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        RegionRecord record = _regions[validation.Value!.Region.RegionId];
        lock (record.Gate)
        {
            if (record.BackingLease?.Handle != handle)
                return KernelResult.Fail(KernelError.StaleGeneration, "Region backing lease changed before release.");
            if (record.PendingBackingCreations != 0)
                return KernelResult.Fail(KernelError.PlatformBindingDraining,
                    "Backing creation is still in flight.");
            record.BackingLease = null;
            record.BackingClosureStarted = false;
        }
        return KernelResult.Ok();
    }

    internal RegionDescriptor Allocate(RegionOwner owner, long byteLength, string elementType)
    {
        if (byteLength <= 0) throw new ArgumentOutOfRangeException(nameof(byteLength));
        long identity = Interlocked.Increment(ref _nextRegionId);
        if (identity <= 0) throw new InvalidOperationException("Region identity space is exhausted.");
        RegionId id = new RegionId(checked((ulong)identity));
        RegionRecord record = new RegionRecord
        {
            Id = id,
            Generation = new RegionGeneration(1),
            Owner = owner,
            ByteLength = byteLength,
            ElementType = elementType,
            State = RegionState.Allocated,
            MutationEpoch = new MutationEpoch(1),
            BorrowGeneration = new BorrowLeaseGeneration(0)
        };
        if (!_regions.TryAdd(id, record))
            throw new InvalidOperationException("Region identity collision.");
        record.State = RegionState.Owned;
        return Descriptor(record);
    }

    public KernelResult<RegionDescriptor> Validate(RegionHandle handle, RegionOwner owner, RegionState requiredState = RegionState.Owned)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record)) return KernelResult<RegionDescriptor>.Fail(KernelError.RegionNotFound, $"Region {handle.RegionId.Value} was not found.");
        lock (record.Gate) return ValidateCore(record, handle, owner, requiredState);
    }

    // Exact overlay lifetime admission, not a byte-access grant or provider closure fact.
    internal KernelResult<Func<Func<KernelResult>, KernelResult>> PrepareSecureGuestAdmission(
        RegionHandle handle, RegionOwner owner, RegionUseRange range, IReadOnlyList<RegionBackingLeaseHandle> admittedBackings)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record))
            return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(KernelError.RegionNotFound, "Secure guest Region was not found.");
        lock (record.Gate)
        {
            RegionBackingLeaseDescriptor? backing = record.BackingLease;
            KernelResult Commit(Func<KernelResult> commit)
            {
                lock (record.Gate)
                {
                    KernelResult<RegionDescriptor> valid = ValidateCore(record, handle, owner, RegionState.Owned);
                    if (!valid.IsSuccess) return KernelResult.Fail(valid.Error, valid.Message!);
                    if (record.BackingLease != backing || record.BackingClosureStarted || record.PendingBackingCreations != 0 || !record.PlatformMappingReserved)
                        return KernelResult.Fail(KernelError.PlatformBindingDraining, "Exact secure guest Region backing/mapping stopped or changed.");
                    if (backing is { } lease && (lease.Region != handle || lease.Owner != owner ||
                        !admittedBackings.Contains(lease.Handle)))
                        return KernelResult.Fail(KernelError.PlatformDenied, "Secure guest backing has no exact admitted existing owner.");
                    if (!IsValidRange(record, range))
                        return KernelResult.Fail(KernelError.InvalidRegionState, "Secure guest range is outside the exact Region.");
                    if (record.Damage.Values.Any(damage => damage.State == RegionDamageStateV1.Quarantined && RangesOverlap(damage.Range, range)))
                        return KernelResult.Fail(KernelError.Quarantined, "Secure guest range intersects an authoritative Region consequence.");
                    return commit(); // Kernel-private pure owner mutation only.
                }
            }
            KernelResult initial = Commit(KernelResult.Ok);
            return initial.IsSuccess ? KernelResult<Func<Func<KernelResult>, KernelResult>>.Ok(Commit)
                : KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(initial.Error, initial.Message!);
        }
    }

    private static KernelResult<RegionDescriptor> ValidateCore(RegionRecord record, RegionHandle handle, RegionOwner owner, RegionState requiredState)
    {
        if (record.Generation != handle.Generation) return KernelResult<RegionDescriptor>.Fail(KernelError.StaleGeneration, "Region generation is stale.");
        if (record.Owner != owner) return KernelResult<RegionDescriptor>.Fail(KernelError.WrongRegionOwner, "Region owner does not match.");
        if (record.State != requiredState) return KernelResult<RegionDescriptor>.Fail(KernelError.InvalidRegionState, $"Expected {requiredState}, got {record.State}.");
        KernelResult custody = ValidatePayloadCustody(record);
        if (!custody.IsSuccess) return KernelResult<RegionDescriptor>.Fail(custody.Error, custody.Message!);
        return KernelResult<RegionDescriptor>.Ok(Descriptor(record));
    }

    private static KernelResult ValidatePayloadCustody(RegionRecord record)
    {
        if (!record.OriginalPayloadExpected)
            return record.Payload is null && record.OriginalPayload is null ? KernelResult.Ok() :
                KernelResult.Fail(KernelError.StaleHandle, "Unregistered Region payload cannot replace original custody.");
        return record.OriginalPayload is { IsOriginalForRuntime: true } original &&
            original.Handle == new RegionHandle(record.Id, record.Generation) && ReferenceEquals(record.Payload, original)
            ? KernelResult.Ok() : KernelResult.Fail(KernelError.StaleHandle,
                "Original Region payload is missing, copied or substituted; backing and charges remain pinned.");
    }

    private static KernelResult ValidateBorrowCustody(RegionRecord record) =>
        record.OriginalBorrowLifetime is { IsOriginal: true } original &&
        ReferenceEquals(original.OriginalRegionOwner, record) && ReferenceEquals(record.BorrowLifetime, original)
            ? KernelResult.Ok() : KernelResult.Fail(KernelError.StaleHandle,
                "Original borrow lifetime is missing, copied or substituted; loan and dependent resources remain pinned.");

    public KernelResult<BorrowLeaseHandle> Loan(RegionHandle handle, RegionOwner owner, RegionOwner borrower)
    {
        KernelResult<BorrowLeaseGrant> acquired = AcquireLoan(handle, owner, borrower);
        return acquired.IsSuccess
            ? KernelResult<BorrowLeaseHandle>.Ok(acquired.Value!.Handle)
            : KernelResult<BorrowLeaseHandle>.Fail(acquired.Error, acquired.Message!);
    }

    internal KernelResult<BorrowLeaseGrant> AcquireLoan(RegionHandle handle, RegionOwner owner, RegionOwner borrower)
    {
        if (owner == borrower) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.InvalidRegionState, "A region cannot be loaned to its owner.");
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record))
            return KernelResult<BorrowLeaseGrant>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<RegionDescriptor> validation = ValidateCore(record, handle, owner, RegionState.Owned);
        if (!validation.IsSuccess) return KernelResult<BorrowLeaseGrant>.Fail(validation.Error, validation.Message!);
        if (record.BorrowLifetime is not null || record.OriginalBorrowLifetime is not null)
            return KernelResult<BorrowLeaseGrant>.Fail(KernelError.StaleHandle, "Previous original borrow custody is unresolved.");
        if (record.PlatformMappingReserved) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.PlatformBindingActive, "An owned region with an active platform mapping cannot be loaned.");
        if (record.ExternalBorrowReadGrantReserved) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.PlatformBindingActive, "An owned region with an active external borrow read grant cannot be loaned.");
        if (record.BackingLease is not null) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.PlatformBindingActive, "An owned region with an active backing lease cannot be loaned.");
        if (record.Uses.Values.Any(use => use.State == RegionUseState.Invalidated))
            return KernelResult<BorrowLeaseGrant>.Fail(KernelError.RegionUseConflict,
                "An owned region with an invalidated unreleased use cannot be loaned.");
        if (HasActiveExclusiveUse(record)) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.RegionUseConflict, "An owned region with an active exclusive use cannot be loaned.");
        if (record.BorrowGeneration.Value == ulong.MaxValue) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.CapacityExhausted, "Borrow lease generation is exhausted.");
            BorrowLeaseGeneration generation = new BorrowLeaseGeneration(record.BorrowGeneration.Value + 1);
            BorrowLeaseLifetime lifetime = new BorrowLeaseLifetime(record);
            BorrowLeaseHandle lease = new BorrowLeaseHandle(new RegionHandle(record.Id, record.Generation), generation);
            KernelResult mutation = AdvanceMutation(record);
        if (!mutation.IsSuccess) return KernelResult<BorrowLeaseGrant>.Fail(mutation.Error, mutation.Message!);
        record.BorrowGeneration = generation;
        record.Borrower = borrower;
        record.BorrowLifetime = lifetime;
        record.OriginalBorrowLifetime = lifetime;
        record.State = RegionState.Loaned;
        return KernelResult<BorrowLeaseGrant>.Ok(new BorrowLeaseGrant(lease, lifetime));
        }
    }

    internal KernelResult<BorrowLeaseAuthoritySnapshot> ValidateBorrowLease(
        BorrowLeaseHandle lease,
        RegionOwner owner,
        RegionOwner borrower)
    {
        if (!_regions.TryGetValue(lease.Region.RegionId, out RegionRecord? record))
            return KernelResult<BorrowLeaseAuthoritySnapshot>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        if (record.Generation != lease.Region.Generation)
            return KernelResult<BorrowLeaseAuthoritySnapshot>.Fail(KernelError.StaleGeneration, "Region generation is stale.");
        if (record.BorrowGeneration != lease.Generation)
            return KernelResult<BorrowLeaseAuthoritySnapshot>.Fail(KernelError.StaleGeneration, "Borrow lease generation is stale.");
        if (record.Owner != owner)
            return KernelResult<BorrowLeaseAuthoritySnapshot>.Fail(KernelError.WrongRegionOwner, "Region owner does not match the borrow lease.");
            KernelResult custody = ValidateBorrowCustody(record);
        if (!custody.IsSuccess) return KernelResult<BorrowLeaseAuthoritySnapshot>.Fail(custody.Error, custody.Message!);
        if (record.State != RegionState.Loaned || record.Borrower != borrower || record.BorrowLifetime is null || !record.BorrowLifetime.IsActive)
            return KernelResult<BorrowLeaseAuthoritySnapshot>.Fail(KernelError.InvalidRegionState, "Borrow lease is not active for the specified borrower.");

        return KernelResult<BorrowLeaseAuthoritySnapshot>.Ok(
            new BorrowLeaseAuthoritySnapshot(
                lease,
                record.Owner,
                borrower,
                record.ByteLength,
                record.BorrowLifetime));
        }
    }

    internal KernelResult ReserveExternalBorrowReadGrant(
        BorrowLeaseHandle lease,
        RegionOwner owner,
        RegionOwner borrower)
    {
        if (!_regions.TryGetValue(lease.Region.RegionId, out RegionRecord? record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<BorrowLeaseAuthoritySnapshot> validation = ValidateBorrowLease(lease, owner, borrower);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        if (record.BackingClosureStarted)
            return KernelResult.Fail(KernelError.PlatformBindingDraining,
                "Backing closure forbids a new external borrow read grant.");
        if (record.PlatformMappingReserved)
            return KernelResult.Fail(KernelError.PlatformBindingActive, "The borrowed region already has an owned-region platform mapping reservation.");
        if (record.ExternalBorrowReadGrantReserved)
            return KernelResult.Fail(KernelError.PlatformBindingActive, "The borrow lease already has an active external read grant.");
        record.ExternalBorrowReadGrantReserved = true;
        return KernelResult.Ok();
        }
    }

    internal KernelResult ReleaseExternalBorrowReadGrantReservation(
        BorrowLeaseHandle lease,
        RegionOwner owner,
        RegionOwner borrower,
        BorrowLeaseLifetime expectedLifetime)
    {
        if (!_regions.TryGetValue(lease.Region.RegionId, out RegionRecord? record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<BorrowLeaseAuthoritySnapshot> validation = ValidateBorrowLease(lease, owner, borrower);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        if (!ReferenceEquals(record.BorrowLifetime, expectedLifetime))
            return KernelResult.Fail(KernelError.StaleGeneration, "Borrow lease lifetime is stale.");
        if (!record.ExternalBorrowReadGrantReserved)
            return KernelResult.Fail(KernelError.PlatformBindingNotFound, "The borrow lease does not have an active external read grant reservation.");
        record.ExternalBorrowReadGrantReserved = false;
        return KernelResult.Ok();
        }
    }

    // Existing owner gate only, never a permission or closure receipt. The final
    // mutation revalidates the exact lease/lifetime after the caller acquires it.
    internal int CountExternalBorrowReservationsForDomain(DomainId domain)
    {
        int count = 0;
        foreach (RegionRecord record in _regions.Values)
            lock (record.Gate)
                if (record.ExternalBorrowReadGrantReserved &&
                    (record.Owner.DomainId == domain || record.Borrower?.DomainId == domain))
                    count++;
        return count; // Existing reservation truth, not provider closure evidence.
    }

    internal KernelResult<object> GetExternalBorrowCompletionGate(BorrowLeaseAuthoritySnapshot expected)
    {
        if (!_regions.TryGetValue(expected.Handle.Region.RegionId, out RegionRecord? record))
            return KernelResult<object>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<BorrowLeaseAuthoritySnapshot> valid = ValidateBorrowLease(expected.Handle, expected.Owner, expected.Borrower);
            if (!valid.IsSuccess) return KernelResult<object>.Fail(valid.Error, valid.Message!);
            if (!ReferenceEquals(valid.Value!.Lifetime, expected.Lifetime) || !record.ExternalBorrowReadGrantReserved)
                return KernelResult<object>.Fail(KernelError.StaleGeneration, "Exact external borrow reservation/lifetime changed.");
            return KernelResult<object>.Ok(record.Gate);
        }
    }

    internal KernelResult ValidateExternalBorrowReaderPublicationLocked(BorrowLeaseAuthoritySnapshot expected)
    {
        if (!_regions.TryGetValue(expected.Handle.Region.RegionId, out RegionRecord? record))
            return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        if (!Monitor.IsEntered(record.Gate))
            return KernelResult.Fail(KernelError.PlatformFaulted, "Borrow reader publication requires its existing Region gate.");
        KernelResult<BorrowLeaseAuthoritySnapshot> valid = ValidateBorrowLease(expected.Handle, expected.Owner, expected.Borrower);
        if (!valid.IsSuccess) return KernelResult.Fail(valid.Error, valid.Message!);
        if (!ReferenceEquals(valid.Value!.Lifetime, expected.Lifetime) || !record.ExternalBorrowReadGrantReserved)
            return KernelResult.Fail(KernelError.StaleGeneration, "Borrow reader lost its exact lifetime/reservation.");
        if (record.Damage.Count != 0)
            return KernelResult.Fail(KernelError.Quarantined, "Borrow reader region has unresolved damage.");
        return record.BackingClosureStarted
            ? KernelResult.Fail(KernelError.PlatformBindingDraining, "Backing closure forbids reader publication.")
            : KernelResult.Ok();
    }

    internal KernelResult CompleteExternalBorrowReadGrantLocked(BorrowLeaseAuthoritySnapshot expected)
    {
        if (!_regions.TryGetValue(expected.Handle.Region.RegionId, out RegionRecord? record))
            return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        if (!Monitor.IsEntered(record.Gate))
            return KernelResult.Fail(KernelError.PlatformFaulted, "External borrow completion requires its existing Region owner gate.");
        KernelResult<BorrowLeaseAuthoritySnapshot> valid = ValidateBorrowLease(expected.Handle, expected.Owner, expected.Borrower);
        if (!valid.IsSuccess) return KernelResult.Fail(valid.Error, valid.Message!);
        if (!ReferenceEquals(valid.Value!.Lifetime, expected.Lifetime) || !record.ExternalBorrowReadGrantReserved)
            return KernelResult.Fail(KernelError.StaleGeneration, "Exact external borrow reservation/lifetime changed.");
        KernelResult mutation = AdvanceMutation(record);
        if (!mutation.IsSuccess) return mutation;
        record.ExternalBorrowReadGrantReserved = false;
        record.OriginalBorrowLifetime!.InvalidateForRuntime();
        record.BorrowLifetime = null;
        record.OriginalBorrowLifetime = null;
        record.Borrower = null;
        record.State = RegionState.Owned;
        return KernelResult.Ok();
    }

    public KernelResult ReturnLoan(BorrowLeaseHandle lease, RegionOwner borrower)
    {
        if (!_regions.TryGetValue(lease.Region.RegionId, out RegionRecord? record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        if (record.Generation != lease.Region.Generation) return KernelResult.Fail(KernelError.StaleGeneration, "Region generation is stale.");
        if (record.BorrowGeneration != lease.Generation) return KernelResult.Fail(KernelError.StaleGeneration, "Borrow lease generation is stale.");
        if (record.State != RegionState.Loaned || record.Borrower != borrower || record.BorrowLifetime is null) return KernelResult.Fail(KernelError.InvalidRegionState, "Borrow lease is not active for the specified borrower.");
            KernelResult custody = ValidateBorrowCustody(record);
        if (!custody.IsSuccess) return custody;
        if (record.ExternalBorrowReadGrantReserved) return KernelResult.Fail(KernelError.PlatformBindingActive, "The CPU borrow cannot complete while its external read grant is active or draining.");
            KernelResult mutation = AdvanceMutation(record);
        if (!mutation.IsSuccess) return mutation;
        record.OriginalBorrowLifetime!.InvalidateForRuntime();
        record.BorrowLifetime = null;
        record.OriginalBorrowLifetime = null;
        record.Borrower = null;
        record.State = RegionState.Owned;
        return KernelResult.Ok();
        }
    }

    public KernelResult RevokeLoan(BorrowLeaseHandle lease, RegionOwner owner)
    {
        if (!_regions.TryGetValue(lease.Region.RegionId, out RegionRecord? record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        if (record.Generation != lease.Region.Generation) return KernelResult.Fail(KernelError.StaleGeneration, "Region generation is stale.");
        if (record.BorrowGeneration != lease.Generation) return KernelResult.Fail(KernelError.StaleGeneration, "Borrow lease generation is stale.");
        if (record.Owner != owner) return KernelResult.Fail(KernelError.WrongRegionOwner, "Region owner does not match.");
        if (record.State != RegionState.Loaned || record.Borrower is null || record.BorrowLifetime is null) return KernelResult.Fail(KernelError.InvalidRegionState, "Region does not have an active borrow lease.");
            KernelResult custody = ValidateBorrowCustody(record);
        if (!custody.IsSuccess) return custody;
        if (record.ExternalBorrowReadGrantReserved) return KernelResult.Fail(KernelError.PlatformBindingActive, "The CPU borrow cannot be revoked while its external read grant is active or draining.");
            KernelResult mutation = AdvanceMutation(record);
        if (!mutation.IsSuccess) return mutation;
        record.OriginalBorrowLifetime!.InvalidateForRuntime();
        record.BorrowLifetime = null;
        record.OriginalBorrowLifetime = null;
        record.Borrower = null;
        record.State = RegionState.Owned;
        return KernelResult.Ok();
        }
    }

    internal KernelResult ReservePlatformMapping(RegionHandle handle, RegionOwner owner)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<RegionDescriptor> validation = ValidateCore(record, handle, owner, RegionState.Owned);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        if (record.BackingClosureStarted)
            return KernelResult.Fail(KernelError.PlatformBindingDraining,
                "Backing closure forbids a new platform mapping reservation.");
        if (record.Damage.Count != 0)
            return KernelResult.Fail(KernelError.Quarantined,
                "Damaged Region cannot acquire a platform mapping before authoritative replacement.");
        if (record.PlatformMappingReserved) return KernelResult.Fail(KernelError.PlatformBindingActive, "The owned region already has an active platform mapping.");
        if (record.ExternalBorrowReadGrantReserved) return KernelResult.Fail(KernelError.PlatformBindingActive, "The region has an active external borrow read grant.");
        if (record.Uses.Values.Any(use => use.State == RegionUseState.Invalidated))
            return KernelResult.Fail(KernelError.RegionUseConflict,
                "An invalidated unreleased use forbids a new platform mapping.");
        if (record.Uses.Values.Any(use => use.State == RegionUseState.Active &&
                use.MutationEpoch == record.MutationEpoch && use.Region.Generation == record.Generation &&
                use.Mode != RegionUseMode.DevicePrivate))
            return KernelResult.Fail(KernelError.RegionUseConflict, "The whole region has an incompatible active use and cannot acquire a separate platform mapping reservation.");
        record.PlatformMappingReserved = true;
        return KernelResult.Ok();
        }
    }

    internal KernelResult ValidatePlatformMappingRegionUsability(
        RegionHandle handle, RegionOwner owner, ulong? expectedMutationEpoch = null)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record))
            return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<RegionDescriptor> validation = ValidateCore(record, handle, owner, RegionState.Owned);
            if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
            if (record.Damage.Count != 0)
                return KernelResult.Fail(KernelError.Quarantined, "Region has unresolved damage.");
            return expectedMutationEpoch is { } expected && record.MutationEpoch.Value != expected
                ? KernelResult.Fail(KernelError.StaleGeneration, "DMA Region mutation epoch changed before provider submission.")
                : KernelResult.Ok();
        }
    }

    // Existing gate only; lookup is not mapping permission or closure evidence.
    internal KernelResult<object> GetPlatformMappingCompletionGate(RegionHandle handle, RegionOwner owner)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record))
            return KernelResult<object>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<RegionDescriptor> valid = ValidateCore(record, handle, owner, RegionState.Owned);
            return valid.IsSuccess ? KernelResult<object>.Ok(record.Gate)
                : KernelResult<object>.Fail(valid.Error, valid.Message!);
        }
    }

    internal KernelResult ValidatePlatformMappingRegionUsabilityLocked(RegionHandle handle, RegionOwner owner)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record))
            return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        if (!Monitor.IsEntered(record.Gate))
            return KernelResult.Fail(KernelError.PlatformFaulted, "Visibility publication requires its existing Region owner gate.");
        return ValidatePlatformMappingRegionUsability(handle, owner);
    }

    internal KernelResult ReleasePlatformMappingReservationLocked(RegionHandle handle, RegionOwner owner)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record))
            return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        if (!Monitor.IsEntered(record.Gate))
            return KernelResult.Fail(KernelError.PlatformFaulted, "Mapping release requires its existing Region owner gate.");
        return ReleasePlatformMappingReservation(handle, owner);
    }

    internal KernelResult ReleasePlatformMappingReservation(RegionHandle handle, RegionOwner owner)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<RegionDescriptor> validation = ValidateCore(record, handle, owner, RegionState.Owned);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        if (!record.PlatformMappingReserved) return KernelResult.Fail(KernelError.PlatformBindingNotFound, "The owned region does not have an active platform mapping.");
        record.PlatformMappingReserved = false;
        return KernelResult.Ok();
        }
    }

    internal bool HasPlatformMappingReservation(RegionHandle handle, RegionOwner owner)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record)) return false;
        lock (record.Gate)
            return ValidateCore(record, handle, owner, RegionState.Owned).IsSuccess && record.PlatformMappingReserved;
    }

    internal KernelResult<RegionHandle> Transfer(RegionHandle handle, RegionOwner source, RegionOwner target,
        ITransferableOwnedPayload? originalPayload = null,PreparedOwnedTransfer? prepared = null)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record))
            return KernelResult<RegionHandle>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        lock(prepared?.CustodyGate ?? record.Gate)
        {
                KernelResult<RegionDescriptor> validation = ValidateCore(record, handle, source, RegionState.Owned);
        if (!validation.IsSuccess) return KernelResult<RegionHandle>.Fail(validation.Error, validation.Message!);
        if(originalPayload is not null && !ReferenceEquals(record.Payload,originalPayload))
            return KernelResult<RegionHandle>.Fail(KernelError.StaleHandle,"Original Region payload changed before transfer effect.");
        if (record.Damage.Count != 0) return KernelResult<RegionHandle>.Fail(KernelError.Quarantined,
            "A Region with quarantined subrange damage cannot be transferred before authoritative replacement.");
        if (record.PlatformMappingReserved) return KernelResult<RegionHandle>.Fail(KernelError.PlatformBindingActive, "An owned region with an active platform mapping cannot be transferred.");
        if (record.ExternalBorrowReadGrantReserved) return KernelResult<RegionHandle>.Fail(KernelError.PlatformBindingActive, "A region with an active external borrow read grant cannot be transferred.");
        if (record.BackingLease is not null) return KernelResult<RegionHandle>.Fail(KernelError.PlatformBindingActive, "A region with an active backing lease cannot be transferred.");
        // A revoked use may still have an in-flight external effect against the
        // backing. Ownership transfer must not give a new owner that backing.
        if (record.Uses.Values.Any(use => use.State == RegionUseState.Invalidated))
            return KernelResult<RegionHandle>.Fail(KernelError.RegionUseConflict,
                "An owned region with an invalidated unreleased use cannot be transferred.");
        if (HasActiveExclusiveUse(record)) return KernelResult<RegionHandle>.Fail(KernelError.RegionUseConflict, "An owned region with an active exclusive use cannot be transferred.");
        if (record.Generation.Value == ulong.MaxValue) return KernelResult<RegionHandle>.Fail(KernelError.CapacityExhausted, "Region generation is exhausted.");
        if (record.ProtectedLabel?.LabelGeneration == ulong.MaxValue)
            return KernelResult<RegionHandle>.Fail(KernelError.CapacityExhausted, "Protected label generation is exhausted.");
        if(prepared is not null)
        {
            if(originalPayload is null || !ReferenceEquals(prepared.Source,originalPayload) || prepared.SourceHandle!=handle || prepared.Projection is not ITransferableOwnedPayload)
                return KernelResult<RegionHandle>.Fail(KernelError.StaleHandle,"Prepared transfer requires original payload/projection.");
            try { prepared.Validate(); }
            catch(InvalidOperationException exception){return KernelResult<RegionHandle>.Fail(KernelError.RegionUseConflict,exception.Message);}
        }
                RegionHandle nextHandle =new RegionHandle(record.Id,new RegionGeneration(record.Generation.Value+1));
                ProtectedRegionLabelDescriptorV1? nextLabel =record.ProtectedLabel is { } label ? label with
        {
            Region=nextHandle,
            LabelGeneration=label.LabelGeneration+1,
        } : null; // Allocate projection before irreversible owner/generation mutation.
                KernelResult mutation = AdvanceMutation(record);
        if (!mutation.IsSuccess) return KernelResult<RegionHandle>.Fail(mutation.Error, mutation.Message!);
        record.State = RegionState.Transferred;
        record.Owner = target;
        record.Generation = new RegionGeneration(record.Generation.Value + 1);
        record.ProtectedLabel=nextLabel;
        record.State = RegionState.Owned;
        if(prepared is not null)
        {
            prepared.Commit(nextHandle); // Trusted managed custody only, no external callbacks.
            record.Payload=(ITransferableOwnedPayload)prepared.Projection;
            record.OriginalPayload = record.Payload;
            record.OriginalPayloadExpected = true;
        }
        return KernelResult<RegionHandle>.Ok(nextHandle);
        }
        }
    }

    internal KernelResult Release(RegionHandle handle, RegionOwner owner, ITransferableOwnedPayload? originalPayload = null)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record))
            return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<RegionDescriptor> validation = ValidateCore(record, handle, owner, RegionState.Owned);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        if (originalPayload is not null && (record.Payload is null || !originalPayload.HasOriginalReleaseContinuityOf(record.Payload)))
            return KernelResult.Fail(KernelError.StaleHandle, "Original Region payload changed before release effect.");
        if (record.Damage.Count != 0) return KernelResult.Fail(KernelError.Quarantined,
            "A Region with quarantined subrange damage remains pinned until authoritative replacement.");
        if (record.PlatformMappingReserved) return KernelResult.Fail(KernelError.PlatformBindingActive, "An owned region with an active platform mapping cannot be released.");
        if (record.ExternalBorrowReadGrantReserved) return KernelResult.Fail(KernelError.PlatformBindingActive, "A region with an active external borrow read grant cannot be released.");
        if (record.BackingLease is not null) return KernelResult.Fail(KernelError.PlatformBindingActive, "A region with an active backing lease cannot be released.");
        // Invalidation revokes the use for new access; it does not prove that an
        // external operation has closed its possible read or write against backing.
        if (record.Uses.Values.Any(use => use.State != RegionUseState.Released))
            return KernelResult.Fail(KernelError.RegionUseConflict,
                "An owned region with an unreleased use cannot be reclaimed.");
            KernelResult mutation = AdvanceMutation(record);
        if (!mutation.IsSuccess) return mutation;
        record.State = RegionState.Released;
        record.Payload = null;
        record.OriginalPayload = null;
        return KernelResult.Ok();
        }
    }

    internal KernelResult CleanupWithdrawnBorrow(IOriginalBorrowPayload original,RegionOwner borrower)
    {
        BorrowLeaseHandle handle =original.RuntimeHandle;
        if(!_regions.TryGetValue(handle.Region.RegionId,out RegionRecord? record)) return KernelResult.Fail(KernelError.RegionNotFound,"Original loan owner absent.");
        lock(record.Gate)
        {
            if(!original.RuntimeIsOriginal || !original.RuntimeLifetime.IsOriginal ||
                !ReferenceEquals(original.RuntimeLifetime.OriginalRegionOwner,record))
                return KernelResult.Fail(KernelError.InvalidRegionState,"Original loan factory/Region owner changed.");
            if(record.Generation!=handle.Region.Generation || record.BorrowGeneration!=handle.Generation)
                return KernelResult.Fail(KernelError.StaleGeneration,"Original loan generation changed.");
            if(ReferenceEquals(record.OriginalWithdrawnBorrow,original.RuntimeLifetime) && record.State==RegionState.Owned) return KernelResult.Ok();
            if(!ReferenceEquals(record.BorrowLifetime,original.RuntimeLifetime) || record.Borrower!=borrower)
                return KernelResult.Fail(KernelError.InvalidRegionState,"Original loan continuity changed.");
            KernelResult result =ReturnLoan(handle,borrower);
            if(result.IsSuccess) record.OriginalWithdrawnBorrow=original.RuntimeLifetime;
            return result;
        }
    }

    internal KernelResult CleanupWithdrawnPayload(ITransferableOwnedPayload original,RegionOwner owner,object originalRegionOwner)
    {
        RegionHandle handle =original.Handle;
        if(!_regions.TryGetValue(handle.RegionId,out RegionRecord? record)) return KernelResult.Fail(KernelError.RegionNotFound,"Original payload owner absent.");
        lock(record.Gate)
        {
            if(!ReferenceEquals(record,originalRegionOwner))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original consumed Region owner was replaced.");
            if(record.Generation!=handle.Generation || record.Owner!=owner) return KernelResult.Fail(KernelError.StaleGeneration,"Original payload owner changed.");
            if(record.State==RegionState.Released && ReferenceEquals(record.OriginalWithdrawnPayload,original)) return KernelResult.Ok();
            if(!ReferenceEquals(record.Payload,original)) return KernelResult.Fail(KernelError.InvalidRegionState,"Original payload continuity changed.");
            record.OriginalWithdrawnPayload=original;
            KernelResult result =Release(handle,owner);
            if(result.IsSuccess) original.InvalidateForRuntime();
            return result;
        }
    }

    internal void RegisterPayload(RegionHandle handle, ITransferableOwnedPayload payload)
    {
        RegionRecord record = _regions[handle.RegionId];
        lock (record.Gate)
        {
            if (record.Generation != handle.Generation || record.OriginalPayloadExpected ||
                record.Payload is not null || record.OriginalPayload is not null || !payload.IsOriginalForRuntime || payload.Handle != handle)
                throw new InvalidOperationException("Region payload registration requires its first original factory result.");
            record.OriginalPayloadExpected = true;
            record.OriginalPayload = payload;
            record.Payload = payload;
        }
    }

    internal KernelResult<ITransferableOwnedPayload> CaptureTransferPayload(RegionHandle handle,RegionOwner owner)
    {
        if(!_regions.TryGetValue(handle.RegionId,out RegionRecord? record))
            return KernelResult<ITransferableOwnedPayload>.Fail(KernelError.RegionNotFound,"Transfer Region absent.");
        lock(record.Gate)
        {
            KernelResult<RegionDescriptor> valid =ValidateCore(record,handle,owner,RegionState.Owned);
            if(!valid.IsSuccess)return KernelResult<ITransferableOwnedPayload>.Fail(valid.Error,valid.Message!);
            return record.Payload is { } payload?KernelResult<ITransferableOwnedPayload>.Ok(payload):
                KernelResult<ITransferableOwnedPayload>.Fail(KernelError.InvalidRegionState,"Original transfer payload absent.");
        }
    }

    internal void ReplacePayload(RegionHandle oldHandle, RegionHandle newHandle, ITransferableOwnedPayload payload)
    {
        RegionRecord record = _regions[newHandle.RegionId];
        lock (record.Gate)
        {
        if (record.Generation != newHandle.Generation || oldHandle.RegionId != newHandle.RegionId)
            throw new InvalidOperationException("Region payload handle does not match the authoritative record.");
            KernelResult custody = ValidatePayloadCustody(record);
        if (!custody.IsSuccess || !payload.IsOriginalForRuntime || payload.Handle != newHandle)
            throw new InvalidOperationException("Original Region replacement custody changed.");
        record.OriginalPayloadExpected = true;
        record.OriginalPayload = payload;
        record.Payload = payload;
        }
    }

    internal void InvalidateLocalTokensForProcessExit(RegionOwner principal)
    {
        foreach (RegionRecord record in OrderedRecords())
        {
            lock (record.Gate)
            {
                if (record.Owner == principal)
                {
                    // Revoke only the retained factory original, never a mutable
                    // missing/copy/foreign routing payload. Reclaim still needs
                    // intact routing and independent use/backing closure.
                    if (record.OriginalPayloadExpected && record.OriginalPayload is { IsOriginalForRuntime: true } original &&
                        original.Handle == new RegionHandle(record.Id, record.Generation))
                        original.InvalidateOwnerAccessForRuntime();
                }
                if (record.Borrower == principal)
                {
                    if (record.OriginalBorrowLifetime is { IsOriginal: true } originalBorrow &&
                        ReferenceEquals(originalBorrow.OriginalRegionOwner, record))
                        originalBorrow.InvalidateLocalAccessForRuntime();
                }
            }
        }
    }

    public KernelResult<RegionUseDescriptor> AcquireUse(
        RegionHandle handle,
        RegionOwner principal,
        RegionUseMode mode,
        RegionUseRange range)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record))
            return KernelResult<RegionUseDescriptor>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<RegionDescriptor> validation = ValidateCore(record, handle, principal, RegionState.Owned);
        if (!validation.IsSuccess)
            return KernelResult<RegionUseDescriptor>.Fail(validation.Error, validation.Message!);

        return AcquireUseCore(record, principal, mode, range);
        }
    }

    internal KernelResult<RegionUseDescriptor> AcquireUseForExternalOperation(
        RegionHandle handle, RegionOwner principal, RegionUseMode mode,
        RegionUseRange range, ExternalOperationHandle operation)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record))
            return KernelResult<RegionUseDescriptor>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<RegionDescriptor> validation = ValidateCore(record, handle, principal, RegionState.Owned);
            if (!validation.IsSuccess)
                return KernelResult<RegionUseDescriptor>.Fail(validation.Error, validation.Message!);
            return AcquireUseCore(record, principal, mode, range,
                releaseOwnerOperation: operation);
        }
    }

    internal KernelResult<RegionUseDescriptor> AcquireTypedUse(
        RegionHandle handle,
        RegionOwner principal,
        RegionUseMode mode,
        long elementOffset,
        long elementLength,
        int elementSize,
        string elementType)
    {
        if (elementOffset < 0 || elementLength <= 0 || elementSize <= 0)
            return KernelResult<RegionUseDescriptor>.Fail(KernelError.InvalidRegionState, "Typed region-use range is invalid.");
        long byteOffset;
        long byteLength;
        try
        {
            byteOffset = checked(elementOffset * elementSize);
            byteLength = checked(elementLength * elementSize);
        }
        catch (OverflowException)
        {
            return KernelResult<RegionUseDescriptor>.Fail(KernelError.InvalidRegionState, "Typed region-use byte arithmetic overflowed.");
        }

        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record))
            return KernelResult<RegionUseDescriptor>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<RegionDescriptor> validation = ValidateCore(record, handle, principal, RegionState.Owned);
            if (!validation.IsSuccess)
                return KernelResult<RegionUseDescriptor>.Fail(validation.Error, validation.Message!);
            if (!StringComparer.Ordinal.Equals(record.ElementType, elementType) || record.ByteLength % elementSize != 0)
                return KernelResult<RegionUseDescriptor>.Fail(KernelError.InvalidRegionState, "Typed region-use element type or alignment does not match the Region.");
            return AcquireUseCore(record, principal, mode, new RegionUseRange(byteOffset, byteLength));
        }
    }

    public KernelResult ProbeUse(
        RegionHandle handle,
        RegionOwner principal,
        RegionUseMode mode,
        RegionUseRange range)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<RegionDescriptor> validation = ValidateCore(record, handle, principal, RegionState.Owned);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        return ValidateUseRequest(record, mode, range);
        }
    }

    internal KernelResult ProbeUseForExactPlatformMapping(RegionHandle handle, RegionOwner principal,
        RegionUseMode mode, RegionUseRange range)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<RegionDescriptor> validation = ValidateCore(record, handle, principal, RegionState.Owned);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        return ValidateUseRequest(record, mode, range, allowPlatformMappingReservation: true);
        }
    }

    internal KernelResult<RegionUseDescriptor> AcquireUseForExactPlatformMapping(RegionHandle handle,
        RegionOwner principal, RegionUseMode mode, RegionUseRange range,
        ExternalOperationHandle operation)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record))
            return KernelResult<RegionUseDescriptor>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<RegionDescriptor> validation = ValidateCore(record, handle, principal, RegionState.Owned);
        if (!validation.IsSuccess)
            return KernelResult<RegionUseDescriptor>.Fail(validation.Error, validation.Message!);
        return AcquireUseCore(record, principal, mode, range,
            allowPlatformMappingReservation: true, releaseOwnerOperation: operation);
        }
    }

    public KernelResult<RegionUseDescriptor> AcquireBorrowUse(
        BorrowLeaseHandle lease,
        RegionOwner owner,
        RegionOwner borrower,
        RegionUseMode mode,
        RegionUseRange range)
    {
        if (!_regions.TryGetValue(lease.Region.RegionId, out RegionRecord? record))
            return KernelResult<RegionUseDescriptor>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<BorrowLeaseAuthoritySnapshot> validation = ValidateBorrowLease(lease, owner, borrower);
        if (!validation.IsSuccess)
            return KernelResult<RegionUseDescriptor>.Fail(validation.Error, validation.Message!);
        if (IsWriteMode(mode))
        {
            return KernelResult<RegionUseDescriptor>.Fail(
                KernelError.InsufficientRights,
                "A read-only borrow lease cannot authorize a writable region use.");
        }

        return AcquireUseCore(record, borrower, mode, range, originalBorrowLease: lease);
        }
    }

    internal KernelResult<RegionUseDescriptor> ValidateBorrowUsePublication(
        BorrowLeaseHandle lease, RegionOwner owner, RegionOwner borrower, RegionUseHandle use)
    {
        if (!_regions.TryGetValue(lease.Region.RegionId, out RegionRecord? record))
            return KernelResult<RegionUseDescriptor>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            KernelResult<BorrowLeaseAuthoritySnapshot> valid = ValidateBorrowLease(lease, owner, borrower);
            if (!valid.IsSuccess) return KernelResult<RegionUseDescriptor>.Fail(valid.Error, valid.Message!);
            if (!record.Uses.TryGetValue(use.UseId, out RegionUseRecord? exact) || exact.Handle != use || exact.Region != lease.Region || exact.OriginalBorrowLease != lease)
                return KernelResult<RegionUseDescriptor>.Fail(KernelError.StaleGeneration, "Borrow publication lost its exact original Region use.");
            return ValidateUse(use, borrower);
        }
    }

    public KernelResult<RegionUseDescriptor> ValidateUse(
        RegionUseHandle handle,
        RegionOwner principal)
    {
        KernelResult<(RegionRecord Region, RegionUseRecord Use)> lookup = FindUse(handle);
        if (!lookup.IsSuccess)
            return KernelResult<RegionUseDescriptor>.Fail(lookup.Error, lookup.Message!);

        (RegionRecord? region, RegionUseRecord? use) = lookup.Value!;
        lock (region.Gate)
        {
        if (use.Principal != principal)
        {
            return KernelResult<RegionUseDescriptor>.Fail(
                KernelError.WrongRegionOwner,
                "Region use principal does not match.");
        }
        if (use.State == RegionUseState.Released)
        {
            return KernelResult<RegionUseDescriptor>.Fail(
                KernelError.InvalidRegionState,
                "Region use has been released.");
        }
        if (use.State == RegionUseState.Invalidated ||
            use.Region.Generation != region.Generation)
        {
            return KernelResult<RegionUseDescriptor>.Fail(
                KernelError.StaleGeneration,
                "Region use mutation or ownership generation is stale; reacquire instead of refreshing it.");
        }

        return KernelResult<RegionUseDescriptor>.Ok(UseDescriptor(use));
        }
    }

    internal KernelResult CommitPoolUsePublication(RegionUseHandle handle, RegionOwner principal, Func<KernelResult> commit)
    {
        KernelResult<(RegionRecord Region, RegionUseRecord Use)> lookup = FindUse(handle);
        if (!lookup.IsSuccess) return KernelResult.Fail(lookup.Error, lookup.Message!);
        (RegionRecord? region, RegionUseRecord? use) = lookup.Value!;
        lock (region.Gate)
        {
            KernelResult<RegionUseDescriptor> active = ValidateUse(handle, principal);
            if (!active.IsSuccess) return KernelResult.Fail(active.Error, active.Message!);
            if (region.Owner != principal || region.State != RegionState.Owned ||
                use.Mode != RegionUseMode.DevicePrivate || use.MutationEpoch != region.MutationEpoch)
                return KernelResult.Fail(KernelError.StaleGeneration, "Pool publication lost its exact owned Region-use lifetime.");
            return commit(); // caller supplies only a pure local manager mutation
        }
    }
    public KernelResult ReleaseUse(RegionUseHandle handle, RegionOwner principal)
    {
        KernelResult<(RegionRecord Region, RegionUseRecord Use)> lookup = FindUse(handle);
        if (!lookup.IsSuccess) return KernelResult.Fail(lookup.Error, lookup.Message!);

        (RegionRecord? region, RegionUseRecord? use) = lookup.Value!;
        lock (region.Gate)
        {
        if (use.Principal != principal)
            return KernelResult.Fail(KernelError.WrongRegionOwner, "Region use principal does not match.");
        if (use.State == RegionUseState.Released) return KernelResult.Ok();
        if (use.ReleaseOwnerOperation is not null || use.ReleaseOwnerMmio is not null)
            return KernelResult.Fail(KernelError.RegionUseConflict,
                "An external operation or original MMIO owns release of this Region use.");

        if (IsWriteMode(use.Mode))
        {
                KernelResult mutation = AdvanceMutation(region, invalidateActiveUses: false);
            if (!mutation.IsSuccess) return mutation;
        }
        use.State = RegionUseState.Released;
        return KernelResult.Ok();
        }
    }

    internal KernelResult ReleaseUsesAtomically(
        IReadOnlyList<RegionUseHandle> handles, RegionOwner principal,
        ExternalOperationHandle operation)
    {
        ArgumentNullException.ThrowIfNull(handles);
        List<(RegionRecord Region, RegionUseRecord Use)> targets = new List<(RegionRecord Region, RegionUseRecord Use)>(handles.Count);
        HashSet<RegionUseHandle> seen = new HashSet<RegionUseHandle>();
        foreach (RegionUseHandle handle in handles)
        {
            if (!seen.Add(handle))
                return KernelResult.Fail(KernelError.InvalidRegionState,
                    "A Region use appears more than once in the release set.");
            KernelResult<(RegionRecord Region, RegionUseRecord Use)> lookup = FindUse(handle);
            if (!lookup.IsSuccess) return KernelResult.Fail(lookup.Error, lookup.Message!);
            targets.Add(lookup.Value!);
        }

        RegionRecord[] regions = targets.Select(static target => target.Region).Distinct()
            .OrderBy(static region => region.Id.Value).ToArray();
        int locked = 0;
        try
        {
            foreach (RegionRecord? region in regions)
            {
                global::System.Threading.Monitor.Enter(region.Gate);
                locked++;
            }
            return ValidateAndRelease();
        }
        finally
        {
            for (int index = locked - 1; index >= 0; index--)
                global::System.Threading.Monitor.Exit(regions[index].Gate);
        }

        KernelResult ValidateAndRelease()
        {
            foreach ((RegionRecord? region, RegionUseRecord? use) in targets)
            {
                if (!region.Uses.TryGetValue(use.Handle.UseId, out RegionUseRecord? current) ||
                    !ReferenceEquals(current, use) || use.Principal != principal ||
                    use.ReleaseOwnerOperation != operation)
                    return KernelResult.Fail(KernelError.StaleGeneration,
                        "A Region use changed before the release set could commit.");
            }
            foreach (RegionRecord? region in regions)
            {
                ulong writes = (ulong)targets.Count(target => ReferenceEquals(target.Region, region) &&
                    target.Use.State != RegionUseState.Released && IsWriteMode(target.Use.Mode));
                if (region.MutationEpoch.Value > ulong.MaxValue - writes)
                    return KernelResult.Fail(KernelError.CapacityExhausted,
                        "Region mutation epoch is exhausted; no use in the release set was changed.");
            }
            foreach ((RegionRecord? region, RegionUseRecord? use) in targets)
            {
                if (use.State == RegionUseState.Released) continue;
                if (IsWriteMode(use.Mode))
                    region.MutationEpoch = new MutationEpoch(region.MutationEpoch.Value + 1);
                use.State = RegionUseState.Released;
            }
            return KernelResult.Ok();
        }
    }

    public KernelResult InvalidateUse(RegionUseHandle handle, RegionOwner principal)
    {
        KernelResult<(RegionRecord Region, RegionUseRecord Use)> lookup = FindUse(handle);
        if (!lookup.IsSuccess) return KernelResult.Fail(lookup.Error, lookup.Message!);

        (RegionRecord? region, RegionUseRecord? use) = lookup.Value!;
        lock (region.Gate)
        {
        if (use.Principal != principal)
            return KernelResult.Fail(KernelError.WrongRegionOwner, "Region use principal does not match.");
        if (use.State != RegionUseState.Active) return KernelResult.Ok();

        if (IsWriteMode(use.Mode))
        {
                KernelResult mutation = AdvanceMutation(region, invalidateActiveUses: false);
            if (!mutation.IsSuccess) return mutation;
        }
        use.State = RegionUseState.Invalidated;
        return KernelResult.Ok();
        }
    }

    internal KernelResult InvalidateUseAfterProviderLoss(
        RegionUseHandle handle, RegionOwner principal, ExternalOperationHandle operation)
    {
        KernelResult<(RegionRecord Region, RegionUseRecord Use)> lookup = FindUse(handle);
        if (!lookup.IsSuccess) return KernelResult.Fail(lookup.Error, lookup.Message!);
        (RegionRecord? region, RegionUseRecord? use) = lookup.Value!;
        lock (region.Gate)
        {
            if (use.Principal != principal || use.ReleaseOwnerOperation != operation)
                return KernelResult.Fail(KernelError.StaleGeneration,
                    "Provider-loss invalidation requires the exact operation-owned Region use.");
            if (use.State != RegionUseState.Active) return KernelResult.Ok();
            if (IsWriteMode(use.Mode) && region.MutationEpoch.Value != ulong.MaxValue)
            {
                KernelResult mutation = AdvanceMutation(region, invalidateActiveUses: false);
                if (!mutation.IsSuccess) return mutation;
            }
            // At the terminal epoch no successor can be minted. Invalidate the
            // local use without wrapping its generation; release remains pinned.
            use.State = RegionUseState.Invalidated;
            return KernelResult.Ok();
        }
    }

    public IReadOnlyList<RegionUseDescriptor> SnapshotUses() =>
        OrderedRecords().SelectMany(SnapshotUses).OrderBy(static use => use.Handle.UseId.Value).ToArray();

    internal KernelResult<IReadOnlyList<RegionHandle>> ReturnAllLoansForBorrowerDomain(DomainId borrowerDomainId)
    {
        RegionRecord[] records = OrderedRecords();
        List<RegionHandle> returned = new List<RegionHandle>();
        int locked = 0;
        try
        {
            foreach (RegionRecord record in records)
            {
                Monitor.Enter(record.Gate);
                locked++;
            }
            RegionRecord[] targets = records.Where(record => record.State == RegionState.Loaned &&
                record.Borrower?.DomainId == borrowerDomainId).ToArray();
            // Refuse the whole local batch before invalidating any lifetime/use.
            foreach (RegionRecord? record in targets)
            {
                KernelResult custody = ValidateBorrowCustody(record);
                if (!custody.IsSuccess) return KernelResult<IReadOnlyList<RegionHandle>>.Fail(custody.Error, custody.Message!);
                if (record.ExternalBorrowReadGrantReserved)
                    return KernelResult<IReadOnlyList<RegionHandle>>.Fail(KernelError.PlatformBindingDraining,
                        "External borrow read grants must reach verified closure before borrower-domain loan reclaim.");
                if (record.MutationEpoch.Value == ulong.MaxValue)
                    return KernelResult<IReadOnlyList<RegionHandle>>.Fail(KernelError.CapacityExhausted,
                        "Region mutation epoch is exhausted before borrower-domain loan return.");
            }
            foreach (RegionRecord? record in targets)
            {
                KernelResult mutation = AdvanceMutation(record);
                if (!mutation.IsSuccess)
                    return KernelResult<IReadOnlyList<RegionHandle>>.Fail(mutation.Error, mutation.Message!);
                returned.Add(new RegionHandle(record.Id, record.Generation));
                record.OriginalBorrowLifetime!.InvalidateForRuntime();
                record.BorrowLifetime = null;
                record.OriginalBorrowLifetime = null;
                record.Borrower = null;
                record.State = RegionState.Owned;
            }
            return KernelResult<IReadOnlyList<RegionHandle>>.Ok(returned);
        }
        finally
        {
            for (int index = locked - 1; index >= 0; index--) Monitor.Exit(records[index].Gate);
        }
    }

    // Actual final-domain-exit consumer only. This closes local borrow uses,
    // never provider grants or operation-owned possible effects/backing.
    internal void ReleaseLocalBorrowUsesForExitedDomain(DomainId domainId)
    {
        foreach (RegionRecord record in OrderedRecords())
        lock (record.Gate)
        {
            if (record.ExternalBorrowReadGrantReserved) continue;
            foreach (RegionUseRecord use in record.Uses.Values)
                if (use.Principal.DomainId == domainId && use.State != RegionUseState.Released &&
                    use.ReleaseOwnerOperation is null && use.ReleaseOwnerMmio is null && !IsWriteMode(use.Mode) &&
                    use.OriginalBorrowLease is { } original && original.Region == use.Region &&
                    original.Region.RegionId == record.Id)
                    use.State = RegionUseState.Released;
        }
    }

    internal IReadOnlyList<RegionHandle> ReclaimAllForDomain(DomainId domainId)
    {
        KernelResult<IReadOnlyList<RegionHandle>> reclaimed = TryReclaimAllForDomain(domainId);
        if (!reclaimed.IsSuccess) throw new InvalidOperationException(reclaimed.Message);
        return reclaimed.Value!;
    }

    internal KernelResult<IReadOnlyList<RegionHandle>> TryReclaimAllForDomain(DomainId domainId)
    {
        RegionRecord[] records = OrderedRecords();
        List<RegionHandle> reclaimed = new List<RegionHandle>();
        int locked = 0;
        try
        {
            foreach (RegionRecord record in records)
            {
                Monitor.Enter(record.Gate);
                locked++;
            }
            RegionRecord[] targets = records.Where(record => record.Owner.DomainId == domainId &&
                record.State is RegionState.Owned or RegionState.Loaned).ToArray();
            // Validate the whole domain before changing any Region. A later pin
            // must not leave earlier backing reclaimed during a failed teardown.
            foreach (RegionRecord? record in targets)
            {
                KernelResult custody = ValidatePayloadCustody(record);
                if (!custody.IsSuccess) return KernelResult<IReadOnlyList<RegionHandle>>.Fail(custody.Error, custody.Message!);
                if (record.State == RegionState.Loaned)
                {
                    KernelResult borrowCustody = ValidateBorrowCustody(record);
                    if (!borrowCustody.IsSuccess) return KernelResult<IReadOnlyList<RegionHandle>>.Fail(borrowCustody.Error, borrowCustody.Message!);
                }
                if (record.PlatformMappingReserved) return KernelResult<IReadOnlyList<RegionHandle>>.Fail(KernelError.PlatformBindingDraining, "Platform-mapped regions must be revoked before domain reclaim.");
                if (record.ExternalBorrowReadGrantReserved) return KernelResult<IReadOnlyList<RegionHandle>>.Fail(KernelError.PlatformBindingDraining, "External borrow read grants must be revoked before domain reclaim.");
                if (record.BackingLease is not null) return KernelResult<IReadOnlyList<RegionHandle>>.Fail(KernelError.PlatformBindingDraining, "External backing leases must reach verified closure before domain reclaim.");
                if (record.Damage.Count != 0) return KernelResult<IReadOnlyList<RegionHandle>>.Fail(KernelError.Quarantined, "Damaged Region subranges must remain quarantined until authoritative replacement.");
                if (record.Uses.Values.Any(use => use.State != RegionUseState.Released &&
                        (use.State == RegionUseState.Invalidated || use.ReleaseOwnerOperation is not null || use.ReleaseOwnerMmio is not null)))
                    return KernelResult<IReadOnlyList<RegionHandle>>.Fail(KernelError.PlatformBindingDraining,
                        "Invalidated or external-operation-bound Region uses must reach owner release before domain reclaim.");
                if (record.MutationEpoch.Value == ulong.MaxValue)
                    return KernelResult<IReadOnlyList<RegionHandle>>.Fail(KernelError.CapacityExhausted, "Region mutation epoch is exhausted before domain reclaim.");
            }
            foreach (RegionRecord? record in targets)
            {
                reclaimed.Add(new RegionHandle(record.Id, record.Generation));
                AdvanceMutationOrThrow(record);
                record.OriginalBorrowLifetime?.InvalidateForRuntime();
                record.BorrowLifetime = null;
                record.OriginalBorrowLifetime = null;
                record.OriginalPayload?.InvalidateForRuntime();
                record.Payload = null;
                record.OriginalPayload = null;
                record.Borrower = null;
                record.State = RegionState.Released;
            }
        }
        finally
        {
            for (int index = locked - 1; index >= 0; index--)
                Monitor.Exit(records[index].Gate);
        }
        return KernelResult<IReadOnlyList<RegionHandle>>.Ok(reclaimed.OrderBy(static h => h.RegionId.Value).ToArray());
    }

    // Accounting routing only: successful local Transfer ends the old ownership
    // generation. This does not prove closure of backing, mappings or current uses.
    internal bool HasEndedAccountingLifetime(RegionHandle handle)
    {
        if (handle.Generation.Value == 0 || !_regions.TryGetValue(handle.RegionId, out RegionRecord? record)) return false;
        lock (record.Gate)
            return record.Generation.Value > handle.Generation.Value ||
                (record.Generation == handle.Generation && record.State == RegionState.Released);
    }

    internal bool HasEndedOriginalBorrow(IOriginalBorrowPayload original)
    {
        if (!original.RuntimeIsOriginal || !original.RuntimeLifetime.IsOriginal ||
            !_regions.TryGetValue(original.RuntimeHandle.Region.RegionId, out RegionRecord? record)) return false;
        lock (record.Gate)
            return ReferenceEquals(original.RuntimeLifetime.OriginalRegionOwner, record) && !original.RuntimeLifetime.IsActive;
    }

    internal bool HasReleasedOriginalRegion(object original, RegionHandle handle, DomainId? domain = null)
    {
        if (original is not RegionRecord record || !_regions.TryGetValue(handle.RegionId, out RegionRecord? routed) ||
            !ReferenceEquals(routed, record)) return false;
        lock (record.Gate)
            return record.Generation == handle.Generation && record.State == RegionState.Released &&
                (domain is null || record.Owner.DomainId == domain);
    }

    internal object? CaptureOriginalAccountingOwner(RegionHandle handle)
    {
        if (!_regions.TryGetValue(handle.RegionId, out RegionRecord? record)) return null;
        lock (record.Gate)
            return record.Generation == handle.Generation ? record : null;
    }

    internal KernelResult<bool> HasEndedOriginalAccountingLifetime(object original, RegionHandle handle)
    {
        if (original is not RegionRecord record || !_regions.TryGetValue(handle.RegionId, out RegionRecord? routed) ||
            !ReferenceEquals(routed, record))
            return KernelResult<bool>.Fail(KernelError.PlatformFaulted, "Original Region accounting owner is absent or foreign.");
        lock (record.Gate)
            return KernelResult<bool>.Ok(record.Generation.Value > handle.Generation.Value ||
                record.Generation == handle.Generation && record.State == RegionState.Released);
    }
    public IReadOnlyList<RegionDescriptor> Snapshot() => OrderedRecords().Select(Snapshot).ToArray();

    internal RegionAuthorityInspectionRecord[] InspectionSnapshot() =>
        OrderedRecords()
            .Select(SnapshotInspection)
            .ToArray();

    private RegionRecord[] OrderedRecords() => _regions.Values.OrderBy(static record => record.Id.Value).ToArray();

    private static RegionDescriptor Snapshot(RegionRecord record)
    {
        lock (record.Gate) return Descriptor(record);
    }

    private static RegionUseDescriptor[] SnapshotUses(RegionRecord record)
    {
        lock (record.Gate) return record.Uses.Values.Select(UseDescriptor).ToArray();
    }

    private static RegionAuthorityInspectionRecord SnapshotInspection(RegionRecord record)
    {
        lock (record.Gate) return new RegionAuthorityInspectionRecord(
                Descriptor(record),
                record.State == RegionState.Loaned && record.Borrower is not null
                    ? new BorrowLeaseHandle(new RegionHandle(record.Id, record.Generation), record.BorrowGeneration)
                    : null,
                record.Borrower,
                record.BackingLease,
                record.PlatformMappingReserved,
                record.ExternalBorrowReadGrantReserved,
                record.Uses.Values.OrderBy(static use => use.Handle.UseId.Value).Select(UseDescriptor).ToArray(),
                record.Damage.Values.OrderBy(static damage => damage.Handle.DamageId).ToArray());
    }

    private KernelResult<RegionUseDescriptor> AcquireUseCore(
        RegionRecord record,
        RegionOwner principal,
        RegionUseMode mode,
        RegionUseRange range,
        bool allowPlatformMappingReservation = false,
        ExternalOperationHandle? releaseOwnerOperation = null,
        BorrowLeaseHandle? originalBorrowLease = null,
        MmioBufferPayload? releaseOwnerMmio = null)
    {
        KernelResult requestValidation = ValidateUseRequest(record, mode, range, allowPlatformMappingReservation);
        if (!requestValidation.IsSuccess)
            return KernelResult<RegionUseDescriptor>.Fail(requestValidation.Error, requestValidation.Message!);
        long identity = Interlocked.Increment(ref _nextRegionUseId);
        if (identity <= 0)
            return KernelResult<RegionUseDescriptor>.Fail(KernelError.CapacityExhausted, "Region use identity space is exhausted.");

        if (IsWriteMode(mode))
        {
            KernelResult mutation = AdvanceMutation(record, invalidateActiveUses: false);
            if (!mutation.IsSuccess)
                return KernelResult<RegionUseDescriptor>.Fail(mutation.Error, mutation.Message!);
        }

        RegionUseRecord use = new RegionUseRecord
        {
            Handle = new RegionUseHandle(new RegionUseId(checked((ulong)identity)), 1),
            Region = new RegionHandle(record.Id, record.Generation),
            Principal = principal,
            Range = range,
            Mode = mode,
            MutationEpoch = record.MutationEpoch,
            State = RegionUseState.Active,
            ReleaseOwnerOperation = releaseOwnerOperation,
            ReleaseOwnerMmio = releaseOwnerMmio,
            OriginalBorrowLease = originalBorrowLease
        };
        record.Uses.Add(use.Handle.UseId, use);
        if (!_useIndex.TryAdd(use.Handle.UseId, record))
            throw new InvalidOperationException("Region use identity collision.");
        return KernelResult<RegionUseDescriptor>.Ok(UseDescriptor(use));
    }

    private static KernelResult ValidateUseRequest(
        RegionRecord record,
        RegionUseMode mode,
        RegionUseRange range,
        bool allowPlatformMappingReservation = false)
    {
        if (!Enum.IsDefined(mode))
            return KernelResult.Fail(KernelError.InvalidRegionState, "Region use mode is invalid.");
        if (mode == RegionUseMode.SharedReadMostly)
            return KernelResult.Fail(KernelError.PlatformUnsupported, "SharedReadMostly is future-gated.");
        if (mode == RegionUseMode.DirectCoherentWrite)
            return KernelResult.Fail(KernelError.PlatformUnsupported, "DirectCoherentWrite is future-gated until CPU alias exclusion and symmetric coherent release are implemented.");
        if (!IsValidRange(record, range))
            return KernelResult.Fail(KernelError.InvalidRegionState, "Region use range is outside the exact region bounds or overflows them.");
        if (record.Damage.Values.Any(damage =>
                damage.State == RegionDamageStateV1.Quarantined && RangesOverlap(damage.Range, range)))
            return KernelResult.Fail(KernelError.Quarantined,
                "The requested Region subrange intersects a quarantined failure consequence.");
        if (record.PlatformMappingReserved && !allowPlatformMappingReservation)
            return KernelResult.Fail(KernelError.RegionUseConflict, "A separate platform mapping already reserves the whole region.");
        if (record.Uses.Values.Any(use => use.State == RegionUseState.Invalidated &&
                RangesOverlap(use.Range, range)))
            return KernelResult.Fail(KernelError.RegionUseConflict,
                "The requested subrange overlaps an invalidated unreleased use.");

        IEnumerable<RegionUseRecord> activeUses = record.Uses.Values.Where(use =>
            use.State == RegionUseState.Active &&
            use.Region.Generation == record.Generation);
        return activeUses.Any(existing => RangesOverlap(existing.Range, range) && !AreCompatible(existing.Mode, mode))
            ? KernelResult.Fail(KernelError.RegionUseConflict, "The requested subrange overlaps an incompatible active use.")
            : KernelResult.Ok();
    }

    private KernelResult<(RegionRecord Region, RegionUseRecord Use)> FindUse(RegionUseHandle handle)
    {
        if (handle.UseId.Value == 0 || handle.Generation != 1)
            return KernelResult<(RegionRecord, RegionUseRecord)>.Fail(KernelError.RegionUseNotFound, "Region use handle is invalid.");

        if (_useIndex.TryGetValue(handle.UseId, out RegionRecord? region))
        {
            lock (region.Gate)
            {
                if (region.Uses.TryGetValue(handle.UseId, out RegionUseRecord? use) && use.Handle == handle)
                    return KernelResult<(RegionRecord, RegionUseRecord)>.Ok((region, use));
            }
        }

        return KernelResult<(RegionRecord, RegionUseRecord)>.Fail(KernelError.RegionUseNotFound, "Region use was not found.");
    }

    private static bool IsValidRange(RegionRecord record, RegionUseRange range) =>
        range.Offset >= 0 &&
        range.Length > 0 &&
        range.Offset <= record.ByteLength - range.Length;

    private static bool RangesOverlap(RegionUseRange left, RegionUseRange right)
    {
        long leftEnd = checked(left.Offset + left.Length);
        long rightEnd = checked(right.Offset + right.Length);
        return left.Offset < rightEnd && right.Offset < leftEnd;
    }

    private static bool IsWriteMode(RegionUseMode mode) => mode is
        RegionUseMode.ExclusiveWrite or
        RegionUseMode.StagedOutput or
        RegionUseMode.DirectCoherentWrite or
        RegionUseMode.DevicePrivate;

    private static bool AreCompatible(RegionUseMode left, RegionUseMode right) =>
        left != RegionUseMode.ExclusiveRead && right != RegionUseMode.ExclusiveRead &&
        !IsWriteMode(left) && !IsWriteMode(right);

    private static bool HasUnreleasedUse(RegionRecord record) => record.Uses.Values.Any(use =>
        use.State != RegionUseState.Released);

    private static bool HasActiveWriteUse(RegionRecord record) => record.Uses.Values.Any(use =>
        use.State == RegionUseState.Active &&
        use.Region.Generation == record.Generation &&
        IsWriteMode(use.Mode));

    private static bool HasActiveExclusiveUse(RegionRecord record) => record.Uses.Values.Any(use =>
        use.State == RegionUseState.Active &&
        use.Region.Generation == record.Generation &&
        (use.Mode == RegionUseMode.ExclusiveRead || IsWriteMode(use.Mode)));

    private static KernelResult AdvanceMutation(RegionRecord record, bool invalidateActiveUses = true)
    {
        if (record.MutationEpoch.Value == ulong.MaxValue)
            return KernelResult.Fail(KernelError.CapacityExhausted, "Region mutation epoch is exhausted.");

        if (invalidateActiveUses)
        {
            foreach (RegionUseRecord? use in record.Uses.Values.Where(static use => use.State == RegionUseState.Active))
                use.State = RegionUseState.Invalidated;
        }
        record.MutationEpoch = new MutationEpoch(record.MutationEpoch.Value + 1);
        return KernelResult.Ok();
    }

    private static void AdvanceMutationOrThrow(RegionRecord record)
    {
        KernelResult result = AdvanceMutation(record);
        if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
    }

    private static RegionUseDescriptor UseDescriptor(RegionUseRecord use) => new(
        use.Handle,
        use.Region,
        use.Principal,
        use.Range,
        use.Mode,
        use.MutationEpoch,
        use.State);

    private static RegionDescriptor Descriptor(RegionRecord record) => new(
        new RegionHandle(record.Id, record.Generation),
        record.Owner,
        record.ByteLength,
        record.ElementType,
        record.State,
        record.MutationEpoch);
}
