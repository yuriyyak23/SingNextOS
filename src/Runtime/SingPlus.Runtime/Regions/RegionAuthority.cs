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

public sealed class RegionAuthority
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
        public ITransferableOwnedPayload? Payload { get; set; }
        public bool PlatformMappingReserved { get; set; }
        public bool ExternalBorrowReadGrantReserved { get; set; }
        public RegionBackingLeaseDescriptor? BackingLease { get; set; }
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
        scope.ProviderGeneration < highWatermark.ProviderGeneration ||
        scope.FailureDomainGeneration < highWatermark.FailureDomainGeneration ||
        observationSequence <= highWatermark.ObservationSequence;

    internal KernelResult<RegionDamageDescriptorV1> QuarantineSubrange(
        RegionHandle handle,
        RegionOwner owner,
        ProviderHealthEvidenceV1 evidence)
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
        if (!_regions.TryGetValue(handle.RegionId, out var record))
            return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            var validation = ValidateCore(record, handle, owner, RegionState.Owned);
            if (!validation.IsSuccess)
                return KernelResult<RegionDamageDescriptorV1>.Fail(validation.Error, validation.Message!);
            if (!IsValidRange(record, range))
                return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.InvalidRegionState,
                    "Provider-health affected range is outside the exact Region bounds.");
            var domainKey = (consequence.Scope.ProviderId, consequence.Scope.FailureDomainId);
            if (record.DamageEvidenceHighWatermarks.TryGetValue(domainKey, out var highWatermark) &&
                IsStaleDamageEvidence(consequence.Scope, consequence.EvidenceObservationSequence,
                    highWatermark))
                return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.StaleGeneration,
                    "Provider-health evidence sequence is stale or replayed for this failure domain.");
            var identity = Interlocked.Increment(ref _nextDamageId);
            if (identity <= 0)
                return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.CapacityExhausted,
                    "Region damage identity space is exhausted.");
            var mutation = AdvanceMutation(record, invalidateActiveUses: false);
            if (!mutation.IsSuccess)
                return KernelResult<RegionDamageDescriptorV1>.Fail(mutation.Error, mutation.Message!);
            foreach (var use in record.Uses.Values.Where(use =>
                         use.State == RegionUseState.Active && RangesOverlap(use.Range, range)))
                use.State = RegionUseState.Invalidated;
            var damage = new RegionDamageDescriptorV1(
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
        foreach (var record in OrderedRecords())
        {
            lock (record.Gate)
            {
                if (!record.Damage.TryGetValue(damageHandle.DamageId, out var damage)) continue;
                if (damage.Handle != damageHandle)
                    return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.StaleGeneration,
                        "Region damage generation is stale.");
                var domainKey = (nextScope.ProviderId, nextScope.FailureDomainId);
                if (!StringComparer.Ordinal.Equals(damage.FailureDomain.ProviderId, nextScope.ProviderId) ||
                    !StringComparer.Ordinal.Equals(damage.FailureDomain.FailureDomainId, nextScope.FailureDomainId) ||
                    damage.FailureDomain.ProviderGeneration == ulong.MaxValue ||
                    damage.FailureDomain.FailureDomainGeneration == ulong.MaxValue ||
                    nextScope.ProviderGeneration != damage.FailureDomain.ProviderGeneration + 1 ||
                    nextScope.FailureDomainGeneration != damage.FailureDomain.FailureDomainGeneration + 1 ||
                    evidenceObservationSequence <= damage.EvidenceObservationSequence ||
                    (record.DamageEvidenceHighWatermarks.TryGetValue(domainKey, out var highWatermark) &&
                     IsStaleDamageEvidence(nextScope, evidenceObservationSequence, highWatermark)) ||
                    damage.Handle.Generation == ulong.MaxValue)
                    return KernelResult<RegionDamageDescriptorV1>.Fail(KernelError.StaleGeneration,
                        "Reconfiguration must advance the exact provider, failure-domain, evidence, and damage generations.");
                var mutation = AdvanceMutation(record, invalidateActiveUses: false);
                if (!mutation.IsSuccess)
                    return KernelResult<RegionDamageDescriptorV1>.Fail(mutation.Error, mutation.Message!);
                var rebound = damage with
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
        foreach (var record in OrderedRecords())
        {
            lock (record.Gate)
            {
            if (!record.Damage.TryGetValue(evidence.Damage.DamageId, out var damage)) continue;
            var liveRegion = new RegionHandle(record.Id, record.Generation);
            var validation = ValidateCore(record, liveRegion, owner, RegionState.Owned);
            if (!validation.IsSuccess)
                return KernelResult<RegionBackingReplacementReceiptV1>.Fail(validation.Error, validation.Message!);
            if (damage.Handle != evidence.Damage || damage.Region != liveRegion)
                return KernelResult<RegionBackingReplacementReceiptV1>.Fail(
                    KernelError.StaleGeneration, "Replacement evidence identifies a stale damage or Region generation.");
            var next = evidence.NextFailureDomain;
            var domainKey = (next.ProviderId, next.FailureDomainId);
            if (damage.Range != evidence.Range ||
                !StringComparer.Ordinal.Equals(damage.FailureDomain.ProviderId, next.ProviderId) ||
                !StringComparer.Ordinal.Equals(damage.FailureDomain.FailureDomainId, next.FailureDomainId) ||
                damage.FailureDomain.ProviderGeneration == ulong.MaxValue ||
                damage.FailureDomain.FailureDomainGeneration == ulong.MaxValue ||
                next.ProviderGeneration != damage.FailureDomain.ProviderGeneration + 1 ||
                next.FailureDomainGeneration != damage.FailureDomain.FailureDomainGeneration + 1 ||
                evidence.EvidenceObservationSequence <= damage.EvidenceObservationSequence ||
                (record.DamageEvidenceHighWatermarks.TryGetValue(domainKey, out var highWatermark) &&
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
            var mutation = AdvanceMutation(record, invalidateActiveUses: false);
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
        if (!_regions.TryGetValue(handle.RegionId, out var record))
            return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            var validation = ValidateCore(record, handle, owner, RegionState.Owned);
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
        var orderedIds = inputs.Append(output).Select(static item => item.RegionId).Distinct()
            .OrderBy(static item => item.Value).ToArray();
        var records = new List<RegionRecord>(orderedIds.Length);
        foreach (var id in orderedIds)
        {
            if (!_regions.TryGetValue(id, out var record))
                return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.RegionNotFound, "Protected propagation Region was not found.");
            records.Add(record);
        }
        foreach (var record in records) Monitor.Enter(record.Gate);
        try
        {
            DataLabelV1? joined = null;
            foreach (var handle in inputs)
            {
                var record = _regions[handle.RegionId];
                var validation = ValidateCore(record, handle, owner, RegionState.Owned);
                if (!validation.IsSuccess) return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(validation.Error, validation.Message!);
                if (record.ProtectedLabel is not { } input)
                    return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.PlatformDenied,
                        "An unlabeled input cannot enter the protected propagation contour.");
                joined = joined is null ? input.Label : DataLabelLatticeV1.Join(joined.Value, input.Label);
            }
            var outputRecord = _regions[output.RegionId];
            var outputValidation = ValidateCore(outputRecord, output, owner, RegionState.Owned);
            if (!outputValidation.IsSuccess) return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(outputValidation.Error, outputValidation.Message!);
            if (HasUnreleasedUse(outputRecord))
                return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.RegionUseConflict,
                    "A protected output label cannot change while the Region has an active use.");
            if (outputRecord.ProtectedLabel is { } current)
                joined = DataLabelLatticeV1.Join(joined!.Value, current.Label);
            var nextGeneration = outputRecord.ProtectedLabel is { } prior
                ? checked(prior.LabelGeneration + 1) : 1UL;
            outputRecord.ProtectedLabel = new(output, joined!.Value, nextGeneration);
            return KernelResult<ProtectedRegionLabelDescriptorV1>.Ok(outputRecord.ProtectedLabel);
        }
        catch (OverflowException)
        { return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.CapacityExhausted, "Protected label generation is exhausted."); }
        finally { for (var index = records.Count - 1; index >= 0; index--) Monitor.Exit(records[index].Gate); }
    }

    internal KernelResult<ProtectedRegionLabelDescriptorV1> ApplyProtectedLabelTransition(
        ProtectedRegionLabelDescriptorV1 expected, RegionOwner owner, InformationFlowTransitionV1 transition)
    {
        try { transition.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.InvalidMessage, exception.Message); }
        if (!_regions.TryGetValue(expected.Region.RegionId, out var record))
            return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            var validation = ValidateCore(record, expected.Region, owner, RegionState.Owned);
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
        if (!_regions.TryGetValue(handle.RegionId, out var record))
            return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            var validation = ValidateCore(record, handle, owner, RegionState.Owned);
            if (!validation.IsSuccess) return KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(validation.Error, validation.Message!);
            return record.ProtectedLabel is { } label
                ? KernelResult<ProtectedRegionLabelDescriptorV1>.Ok(label)
                : KernelResult<ProtectedRegionLabelDescriptorV1>.Fail(KernelError.PlatformBindingNotFound, "Protected Region label was not found.");
        }
    }

    public KernelResult<RegionBackingLeaseDescriptor> ReserveBacking(RegionHandle handle, RegionOwner owner)
    {
        if (!_regions.TryGetValue(handle.RegionId, out var record))
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        var validation = ValidateCore(record, handle, owner, RegionState.Owned);
        if (!validation.IsSuccess) return KernelResult<RegionBackingLeaseDescriptor>.Fail(validation.Error, validation.Message!);
        if (record.Damage.Count != 0)
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.Quarantined,
                "Damaged Region cannot acquire a new backing lease before authoritative replacement.");
        if (record.Uses.Values.Any(use => use.State == RegionUseState.Invalidated))
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.RegionUseConflict,
                "An invalidated unreleased use forbids a new backing lease.");
        if (record.BackingLease is not null)
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.PlatformBindingActive, "The region already has an active backing lease.");
        var identity = Interlocked.Increment(ref _nextBackingLeaseId);
        if (identity <= 0)
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.CapacityExhausted, "Backing lease identity space is exhausted.");
        var lease = new RegionBackingLeaseDescriptor(new(new(checked((ulong)identity)), 1), handle, owner, record.ByteLength);
        record.BackingLease = lease;
        return KernelResult<RegionBackingLeaseDescriptor>.Ok(lease);
        }
    }

    public KernelResult<RegionBackingLeaseDescriptor> ValidateBacking(RegionBackingLeaseHandle handle, RegionOwner owner)
    {
        var record = _regions.Values.FirstOrDefault(item =>
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
        return KernelResult<RegionBackingLeaseDescriptor>.Ok(lease);
        }
    }

    public KernelResult ReleaseBacking(RegionBackingLeaseHandle handle, RegionOwner owner)
    {
        var validation = ValidateBacking(handle, owner);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        var record = _regions[validation.Value!.Region.RegionId];
        lock (record.Gate)
        {
            if (record.BackingLease?.Handle != handle)
                return KernelResult.Fail(KernelError.StaleGeneration, "Region backing lease changed before release.");
            record.BackingLease = null;
        }
        return KernelResult.Ok();
    }

    internal RegionDescriptor Allocate(RegionOwner owner, long byteLength, string elementType)
    {
        if (byteLength <= 0) throw new ArgumentOutOfRangeException(nameof(byteLength));
        var identity = Interlocked.Increment(ref _nextRegionId);
        if (identity <= 0) throw new InvalidOperationException("Region identity space is exhausted.");
        var id = new RegionId(checked((ulong)identity));
        var record = new RegionRecord
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
        if (!_regions.TryGetValue(handle.RegionId, out var record)) return KernelResult<RegionDescriptor>.Fail(KernelError.RegionNotFound, $"Region {handle.RegionId.Value} was not found.");
        lock (record.Gate) return ValidateCore(record, handle, owner, requiredState);
    }

    private static KernelResult<RegionDescriptor> ValidateCore(RegionRecord record, RegionHandle handle, RegionOwner owner, RegionState requiredState)
    {
        if (record.Generation != handle.Generation) return KernelResult<RegionDescriptor>.Fail(KernelError.StaleGeneration, "Region generation is stale.");
        if (record.Owner != owner) return KernelResult<RegionDescriptor>.Fail(KernelError.WrongRegionOwner, "Region owner does not match.");
        if (record.State != requiredState) return KernelResult<RegionDescriptor>.Fail(KernelError.InvalidRegionState, $"Expected {requiredState}, got {record.State}.");
        return KernelResult<RegionDescriptor>.Ok(Descriptor(record));
    }

    public KernelResult<BorrowLeaseHandle> Loan(RegionHandle handle, RegionOwner owner, RegionOwner borrower)
    {
        var acquired = AcquireLoan(handle, owner, borrower);
        return acquired.IsSuccess
            ? KernelResult<BorrowLeaseHandle>.Ok(acquired.Value!.Handle)
            : KernelResult<BorrowLeaseHandle>.Fail(acquired.Error, acquired.Message!);
    }

    internal KernelResult<BorrowLeaseGrant> AcquireLoan(RegionHandle handle, RegionOwner owner, RegionOwner borrower)
    {
        if (owner == borrower) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.InvalidRegionState, "A region cannot be loaned to its owner.");
        if (!_regions.TryGetValue(handle.RegionId, out var record))
            return KernelResult<BorrowLeaseGrant>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        var validation = ValidateCore(record, handle, owner, RegionState.Owned);
        if (!validation.IsSuccess) return KernelResult<BorrowLeaseGrant>.Fail(validation.Error, validation.Message!);
        if (record.PlatformMappingReserved) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.PlatformBindingActive, "An owned region with an active platform mapping cannot be loaned.");
        if (record.ExternalBorrowReadGrantReserved) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.PlatformBindingActive, "An owned region with an active external borrow read grant cannot be loaned.");
        if (record.BackingLease is not null) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.PlatformBindingActive, "An owned region with an active backing lease cannot be loaned.");
        if (record.Uses.Values.Any(use => use.State == RegionUseState.Invalidated))
            return KernelResult<BorrowLeaseGrant>.Fail(KernelError.RegionUseConflict,
                "An owned region with an invalidated unreleased use cannot be loaned.");
        if (HasActiveExclusiveUse(record)) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.RegionUseConflict, "An owned region with an active exclusive use cannot be loaned.");
        if (record.BorrowGeneration.Value == ulong.MaxValue) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.CapacityExhausted, "Borrow lease generation is exhausted.");
        var mutation = AdvanceMutation(record);
        if (!mutation.IsSuccess) return KernelResult<BorrowLeaseGrant>.Fail(mutation.Error, mutation.Message!);

        var generation = new BorrowLeaseGeneration(record.BorrowGeneration.Value + 1);
        var lifetime = new BorrowLeaseLifetime();
        var lease = new BorrowLeaseHandle(new RegionHandle(record.Id, record.Generation), generation);
        record.BorrowGeneration = generation;
        record.Borrower = borrower;
        record.BorrowLifetime = lifetime;
        record.State = RegionState.Loaned;
        return KernelResult<BorrowLeaseGrant>.Ok(new BorrowLeaseGrant(lease, lifetime));
        }
    }

    internal KernelResult<BorrowLeaseAuthoritySnapshot> ValidateBorrowLease(
        BorrowLeaseHandle lease,
        RegionOwner owner,
        RegionOwner borrower)
    {
        if (!_regions.TryGetValue(lease.Region.RegionId, out var record))
            return KernelResult<BorrowLeaseAuthoritySnapshot>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        if (record.Generation != lease.Region.Generation)
            return KernelResult<BorrowLeaseAuthoritySnapshot>.Fail(KernelError.StaleGeneration, "Region generation is stale.");
        if (record.BorrowGeneration != lease.Generation)
            return KernelResult<BorrowLeaseAuthoritySnapshot>.Fail(KernelError.StaleGeneration, "Borrow lease generation is stale.");
        if (record.Owner != owner)
            return KernelResult<BorrowLeaseAuthoritySnapshot>.Fail(KernelError.WrongRegionOwner, "Region owner does not match the borrow lease.");
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
        if (!_regions.TryGetValue(lease.Region.RegionId, out var record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        var validation = ValidateBorrowLease(lease, owner, borrower);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
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
        if (!_regions.TryGetValue(lease.Region.RegionId, out var record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        var validation = ValidateBorrowLease(lease, owner, borrower);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        if (!ReferenceEquals(record.BorrowLifetime, expectedLifetime))
            return KernelResult.Fail(KernelError.StaleGeneration, "Borrow lease lifetime is stale.");
        if (!record.ExternalBorrowReadGrantReserved)
            return KernelResult.Fail(KernelError.PlatformBindingNotFound, "The borrow lease does not have an active external read grant reservation.");
        record.ExternalBorrowReadGrantReserved = false;
        return KernelResult.Ok();
        }
    }

    public KernelResult ReturnLoan(BorrowLeaseHandle lease, RegionOwner borrower)
    {
        if (!_regions.TryGetValue(lease.Region.RegionId, out var record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        if (record.Generation != lease.Region.Generation) return KernelResult.Fail(KernelError.StaleGeneration, "Region generation is stale.");
        if (record.BorrowGeneration != lease.Generation) return KernelResult.Fail(KernelError.StaleGeneration, "Borrow lease generation is stale.");
        if (record.State != RegionState.Loaned || record.Borrower != borrower || record.BorrowLifetime is null) return KernelResult.Fail(KernelError.InvalidRegionState, "Borrow lease is not active for the specified borrower.");
        if (record.ExternalBorrowReadGrantReserved) return KernelResult.Fail(KernelError.PlatformBindingActive, "The CPU borrow cannot complete while its external read grant is active or draining.");
        var mutation = AdvanceMutation(record);
        if (!mutation.IsSuccess) return mutation;
        record.BorrowLifetime.InvalidateForRuntime();
        record.BorrowLifetime = null;
        record.Borrower = null;
        record.State = RegionState.Owned;
        return KernelResult.Ok();
        }
    }

    public KernelResult RevokeLoan(BorrowLeaseHandle lease, RegionOwner owner)
    {
        if (!_regions.TryGetValue(lease.Region.RegionId, out var record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        if (record.Generation != lease.Region.Generation) return KernelResult.Fail(KernelError.StaleGeneration, "Region generation is stale.");
        if (record.BorrowGeneration != lease.Generation) return KernelResult.Fail(KernelError.StaleGeneration, "Borrow lease generation is stale.");
        if (record.Owner != owner) return KernelResult.Fail(KernelError.WrongRegionOwner, "Region owner does not match.");
        if (record.State != RegionState.Loaned || record.Borrower is null || record.BorrowLifetime is null) return KernelResult.Fail(KernelError.InvalidRegionState, "Region does not have an active borrow lease.");
        if (record.ExternalBorrowReadGrantReserved) return KernelResult.Fail(KernelError.PlatformBindingActive, "The CPU borrow cannot be revoked while its external read grant is active or draining.");
        var mutation = AdvanceMutation(record);
        if (!mutation.IsSuccess) return mutation;
        record.BorrowLifetime.InvalidateForRuntime();
        record.BorrowLifetime = null;
        record.Borrower = null;
        record.State = RegionState.Owned;
        return KernelResult.Ok();
        }
    }

    internal KernelResult ReservePlatformMapping(RegionHandle handle, RegionOwner owner)
    {
        if (!_regions.TryGetValue(handle.RegionId, out var record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        var validation = ValidateCore(record, handle, owner, RegionState.Owned);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
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

    internal KernelResult ReleasePlatformMappingReservation(RegionHandle handle, RegionOwner owner)
    {
        if (!_regions.TryGetValue(handle.RegionId, out var record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        var validation = ValidateCore(record, handle, owner, RegionState.Owned);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        if (!record.PlatformMappingReserved) return KernelResult.Fail(KernelError.PlatformBindingNotFound, "The owned region does not have an active platform mapping.");
        record.PlatformMappingReserved = false;
        return KernelResult.Ok();
        }
    }

    internal bool HasPlatformMappingReservation(RegionHandle handle, RegionOwner owner)
    {
        if (!_regions.TryGetValue(handle.RegionId, out var record)) return false;
        lock (record.Gate)
            return ValidateCore(record, handle, owner, RegionState.Owned).IsSuccess && record.PlatformMappingReserved;
    }

    internal KernelResult<RegionHandle> Transfer(RegionHandle handle, RegionOwner source, RegionOwner target)
    {
        if (!_regions.TryGetValue(handle.RegionId, out var record))
            return KernelResult<RegionHandle>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        var validation = ValidateCore(record, handle, source, RegionState.Owned);
        if (!validation.IsSuccess) return KernelResult<RegionHandle>.Fail(validation.Error, validation.Message!);
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
        var mutation = AdvanceMutation(record);
        if (!mutation.IsSuccess) return KernelResult<RegionHandle>.Fail(mutation.Error, mutation.Message!);
        record.State = RegionState.Transferred;
        record.Owner = target;
        record.Generation = new RegionGeneration(record.Generation.Value + 1);
        if (record.ProtectedLabel is { } protectedLabel)
            record.ProtectedLabel = protectedLabel with
            {
                Region = new RegionHandle(record.Id, record.Generation),
                LabelGeneration = protectedLabel.LabelGeneration + 1,
            };
        record.State = RegionState.Owned;
        return KernelResult<RegionHandle>.Ok(new RegionHandle(record.Id, record.Generation));
        }
    }

    internal KernelResult Release(RegionHandle handle, RegionOwner owner)
    {
        if (!_regions.TryGetValue(handle.RegionId, out var record))
            return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        var validation = ValidateCore(record, handle, owner, RegionState.Owned);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
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
        var mutation = AdvanceMutation(record);
        if (!mutation.IsSuccess) return mutation;
        record.State = RegionState.Released;
        record.Payload = null;
        return KernelResult.Ok();
        }
    }

    internal void RegisterPayload(RegionHandle handle, ITransferableOwnedPayload payload)
    {
        var record = _regions[handle.RegionId];
        lock (record.Gate) record.Payload = payload;
    }

    internal void ReplacePayload(RegionHandle oldHandle, RegionHandle newHandle, ITransferableOwnedPayload payload)
    {
        var record = _regions[newHandle.RegionId];
        lock (record.Gate)
        {
        if (record.Generation != newHandle.Generation || oldHandle.RegionId != newHandle.RegionId)
            throw new InvalidOperationException("Region payload handle does not match the authoritative record.");
        record.Payload = payload;
        }
    }

    internal void InvalidateLocalTokensForProcessExit(RegionOwner principal)
    {
        foreach (var record in OrderedRecords())
        {
            lock (record.Gate)
            {
                if (record.Owner == principal)
                    record.Payload?.InvalidateOwnerAccessForRuntime();
                if (record.Borrower == principal)
                    record.BorrowLifetime?.InvalidateLocalAccessForRuntime();
            }
        }
    }

    public KernelResult<RegionUseDescriptor> AcquireUse(
        RegionHandle handle,
        RegionOwner principal,
        RegionUseMode mode,
        RegionUseRange range)
    {
        if (!_regions.TryGetValue(handle.RegionId, out var record))
            return KernelResult<RegionUseDescriptor>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        var validation = ValidateCore(record, handle, principal, RegionState.Owned);
        if (!validation.IsSuccess)
            return KernelResult<RegionUseDescriptor>.Fail(validation.Error, validation.Message!);

        return AcquireUseCore(record, principal, mode, range);
        }
    }

    internal KernelResult<RegionUseDescriptor> AcquireUseForExternalOperation(
        RegionHandle handle, RegionOwner principal, RegionUseMode mode,
        RegionUseRange range, ExternalOperationHandle operation)
    {
        if (!_regions.TryGetValue(handle.RegionId, out var record))
            return KernelResult<RegionUseDescriptor>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            var validation = ValidateCore(record, handle, principal, RegionState.Owned);
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

        if (!_regions.TryGetValue(handle.RegionId, out var record))
            return KernelResult<RegionUseDescriptor>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
            var validation = ValidateCore(record, handle, principal, RegionState.Owned);
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
        if (!_regions.TryGetValue(handle.RegionId, out var record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        var validation = ValidateCore(record, handle, principal, RegionState.Owned);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        return ValidateUseRequest(record, mode, range);
        }
    }

    internal KernelResult ProbeUseForExactPlatformMapping(RegionHandle handle, RegionOwner principal,
        RegionUseMode mode, RegionUseRange range)
    {
        if (!_regions.TryGetValue(handle.RegionId, out var record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        var validation = ValidateCore(record, handle, principal, RegionState.Owned);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        return ValidateUseRequest(record, mode, range, allowPlatformMappingReservation: true);
        }
    }

    internal KernelResult<RegionUseDescriptor> AcquireUseForExactPlatformMapping(RegionHandle handle,
        RegionOwner principal, RegionUseMode mode, RegionUseRange range,
        ExternalOperationHandle operation)
    {
        if (!_regions.TryGetValue(handle.RegionId, out var record))
            return KernelResult<RegionUseDescriptor>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        var validation = ValidateCore(record, handle, principal, RegionState.Owned);
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
        if (!_regions.TryGetValue(lease.Region.RegionId, out var record))
            return KernelResult<RegionUseDescriptor>.Fail(KernelError.RegionNotFound, "Region was not found.");
        lock (record.Gate)
        {
        var validation = ValidateBorrowLease(lease, owner, borrower);
        if (!validation.IsSuccess)
            return KernelResult<RegionUseDescriptor>.Fail(validation.Error, validation.Message!);
        if (IsWriteMode(mode))
        {
            return KernelResult<RegionUseDescriptor>.Fail(
                KernelError.InsufficientRights,
                "A read-only borrow lease cannot authorize a writable region use.");
        }

        return AcquireUseCore(record, borrower, mode, range);
        }
    }

    public KernelResult<RegionUseDescriptor> ValidateUse(
        RegionUseHandle handle,
        RegionOwner principal)
    {
        var lookup = FindUse(handle);
        if (!lookup.IsSuccess)
            return KernelResult<RegionUseDescriptor>.Fail(lookup.Error, lookup.Message!);

        var (region, use) = lookup.Value!;
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

    public KernelResult ReleaseUse(RegionUseHandle handle, RegionOwner principal)
    {
        var lookup = FindUse(handle);
        if (!lookup.IsSuccess) return KernelResult.Fail(lookup.Error, lookup.Message!);

        var (region, use) = lookup.Value!;
        lock (region.Gate)
        {
        if (use.Principal != principal)
            return KernelResult.Fail(KernelError.WrongRegionOwner, "Region use principal does not match.");
        if (use.State == RegionUseState.Released) return KernelResult.Ok();
        if (use.ReleaseOwnerOperation is not null)
            return KernelResult.Fail(KernelError.RegionUseConflict,
                "An external operation owns release of this Region use.");

        if (IsWriteMode(use.Mode))
        {
            var mutation = AdvanceMutation(region, invalidateActiveUses: false);
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
        var targets = new List<(RegionRecord Region, RegionUseRecord Use)>(handles.Count);
        var seen = new HashSet<RegionUseHandle>();
        foreach (var handle in handles)
        {
            if (!seen.Add(handle))
                return KernelResult.Fail(KernelError.InvalidRegionState,
                    "A Region use appears more than once in the release set.");
            var lookup = FindUse(handle);
            if (!lookup.IsSuccess) return KernelResult.Fail(lookup.Error, lookup.Message!);
            targets.Add(lookup.Value!);
        }

        var regions = targets.Select(static target => target.Region).Distinct()
            .OrderBy(static region => region.Id.Value).ToArray();
        var locked = 0;
        try
        {
            foreach (var region in regions)
            {
                global::System.Threading.Monitor.Enter(region.Gate);
                locked++;
            }
            return ValidateAndRelease();
        }
        finally
        {
            for (var index = locked - 1; index >= 0; index--)
                global::System.Threading.Monitor.Exit(regions[index].Gate);
        }

        KernelResult ValidateAndRelease()
        {
            foreach (var (region, use) in targets)
            {
                if (!region.Uses.TryGetValue(use.Handle.UseId, out var current) ||
                    !ReferenceEquals(current, use) || use.Principal != principal ||
                    use.ReleaseOwnerOperation != operation)
                    return KernelResult.Fail(KernelError.StaleGeneration,
                        "A Region use changed before the release set could commit.");
            }
            foreach (var region in regions)
            {
                var writes = (ulong)targets.Count(target => ReferenceEquals(target.Region, region) &&
                    target.Use.State != RegionUseState.Released && IsWriteMode(target.Use.Mode));
                if (region.MutationEpoch.Value > ulong.MaxValue - writes)
                    return KernelResult.Fail(KernelError.CapacityExhausted,
                        "Region mutation epoch is exhausted; no use in the release set was changed.");
            }
            foreach (var (region, use) in targets)
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
        var lookup = FindUse(handle);
        if (!lookup.IsSuccess) return KernelResult.Fail(lookup.Error, lookup.Message!);

        var (region, use) = lookup.Value!;
        lock (region.Gate)
        {
        if (use.Principal != principal)
            return KernelResult.Fail(KernelError.WrongRegionOwner, "Region use principal does not match.");
        if (use.State != RegionUseState.Active) return KernelResult.Ok();

        if (IsWriteMode(use.Mode))
        {
            var mutation = AdvanceMutation(region, invalidateActiveUses: false);
            if (!mutation.IsSuccess) return mutation;
        }
        use.State = RegionUseState.Invalidated;
        return KernelResult.Ok();
        }
    }

    internal KernelResult InvalidateUseAfterProviderLoss(
        RegionUseHandle handle, RegionOwner principal, ExternalOperationHandle operation)
    {
        var lookup = FindUse(handle);
        if (!lookup.IsSuccess) return KernelResult.Fail(lookup.Error, lookup.Message!);
        var (region, use) = lookup.Value!;
        lock (region.Gate)
        {
            if (use.Principal != principal || use.ReleaseOwnerOperation != operation)
                return KernelResult.Fail(KernelError.StaleGeneration,
                    "Provider-loss invalidation requires the exact operation-owned Region use.");
            if (use.State != RegionUseState.Active) return KernelResult.Ok();
            if (IsWriteMode(use.Mode) && region.MutationEpoch.Value != ulong.MaxValue)
            {
                var mutation = AdvanceMutation(region, invalidateActiveUses: false);
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

    internal IReadOnlyList<RegionHandle> ReturnAllLoansForBorrowerDomain(DomainId borrowerDomainId)
    {
        var returned = new List<RegionHandle>();
        foreach (var record in OrderedRecords())
        {
            lock (record.Gate)
            {
            if (record.State != RegionState.Loaned || record.Borrower?.DomainId != borrowerDomainId) continue;
            if (record.ExternalBorrowReadGrantReserved)
                throw new InvalidOperationException("External borrow read grants must reach verified closure before borrower-domain loan reclaim.");
            returned.Add(new RegionHandle(record.Id, record.Generation));
            AdvanceMutationOrThrow(record);
            record.BorrowLifetime?.InvalidateForRuntime();
            record.BorrowLifetime = null;
            record.Borrower = null;
            record.State = RegionState.Owned;
            }
        }
        return returned.OrderBy(static h => h.RegionId.Value).ToArray();
    }

    internal IReadOnlyList<RegionHandle> ReclaimAllForDomain(DomainId domainId)
    {
        var records = OrderedRecords();
        var reclaimed = new List<RegionHandle>();
        var locked = 0;
        try
        {
            foreach (var record in records)
            {
                Monitor.Enter(record.Gate);
                locked++;
            }
            var targets = records.Where(record => record.Owner.DomainId == domainId &&
                record.State is RegionState.Owned or RegionState.Loaned).ToArray();
            // Validate the whole domain before changing any Region. A later pin
            // must not leave earlier backing reclaimed during a failed teardown.
            foreach (var record in targets)
            {
                if (record.PlatformMappingReserved) throw new InvalidOperationException("Platform-mapped regions must be revoked before domain reclaim.");
                if (record.ExternalBorrowReadGrantReserved) throw new InvalidOperationException("External borrow read grants must be revoked before domain reclaim.");
                if (record.BackingLease is not null) throw new InvalidOperationException("External backing leases must reach verified closure before domain reclaim.");
                if (record.Damage.Count != 0) throw new InvalidOperationException("Damaged Region subranges must remain quarantined until authoritative replacement.");
                if (record.Uses.Values.Any(use => use.State != RegionUseState.Released &&
                        (use.State == RegionUseState.Invalidated || use.ReleaseOwnerOperation is not null)))
                    throw new InvalidOperationException(
                        "Invalidated or external-operation-bound Region uses must reach owner release before domain reclaim.");
                if (record.MutationEpoch.Value == ulong.MaxValue)
                    throw new InvalidOperationException("Region mutation epoch is exhausted before domain reclaim.");
            }
            foreach (var record in targets)
            {
                reclaimed.Add(new RegionHandle(record.Id, record.Generation));
                AdvanceMutationOrThrow(record);
                record.BorrowLifetime?.InvalidateForRuntime();
                record.BorrowLifetime = null;
                record.Payload?.InvalidateForRuntime();
                record.Payload = null;
                record.Borrower = null;
                record.State = RegionState.Released;
            }
        }
        finally
        {
            for (var index = locked - 1; index >= 0; index--)
                Monitor.Exit(records[index].Gate);
        }
        return reclaimed.OrderBy(static h => h.RegionId.Value).ToArray();
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
        ExternalOperationHandle? releaseOwnerOperation = null)
    {
        var requestValidation = ValidateUseRequest(record, mode, range, allowPlatformMappingReservation);
        if (!requestValidation.IsSuccess)
            return KernelResult<RegionUseDescriptor>.Fail(requestValidation.Error, requestValidation.Message!);
        var identity = Interlocked.Increment(ref _nextRegionUseId);
        if (identity <= 0)
            return KernelResult<RegionUseDescriptor>.Fail(KernelError.CapacityExhausted, "Region use identity space is exhausted.");

        if (IsWriteMode(mode))
        {
            var mutation = AdvanceMutation(record, invalidateActiveUses: false);
            if (!mutation.IsSuccess)
                return KernelResult<RegionUseDescriptor>.Fail(mutation.Error, mutation.Message!);
        }

        var use = new RegionUseRecord
        {
            Handle = new RegionUseHandle(new RegionUseId(checked((ulong)identity)), 1),
            Region = new RegionHandle(record.Id, record.Generation),
            Principal = principal,
            Range = range,
            Mode = mode,
            MutationEpoch = record.MutationEpoch,
            State = RegionUseState.Active,
            ReleaseOwnerOperation = releaseOwnerOperation
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

        var activeUses = record.Uses.Values.Where(use =>
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

        if (_useIndex.TryGetValue(handle.UseId, out var region))
        {
            lock (region.Gate)
            {
                if (region.Uses.TryGetValue(handle.UseId, out var use) && use.Handle == handle)
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
        var leftEnd = checked(left.Offset + left.Length);
        var rightEnd = checked(right.Offset + right.Length);
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
            foreach (var use in record.Uses.Values.Where(static use => use.State == RegionUseState.Active))
                use.State = RegionUseState.Invalidated;
        }
        record.MutationEpoch = new MutationEpoch(record.MutationEpoch.Value + 1);
        return KernelResult.Ok();
    }

    private static void AdvanceMutationOrThrow(RegionRecord record)
    {
        var result = AdvanceMutation(record);
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
