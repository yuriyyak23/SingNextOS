using System.Security.Cryptography;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal enum CapabilityRecordState { Active, Consumed, Revoked, Retired }

internal readonly record struct CapabilityAuthorityReference(
    AuthorityRealmId RealmId, CapabilityId CapabilityId, Guid Nonce);

internal sealed record CapabilityAuthorityLimits(int GlobalRecordCapacity, int PerSubjectLiveCapacity)
{
    internal static CapabilityAuthorityLimits Default { get; } = new(1_048_576, 65_536);
}

internal sealed record CapabilityAuthorityInspectionRecord(
    CapabilityDescriptorV1 Descriptor,
    string RealmFingerprint,
    ulong ResourceGeneration,
    CapabilityId? DelegatedFrom,
    CapabilityRecordState State)
{
    public bool Revoked => State is CapabilityRecordState.Revoked or CapabilityRecordState.Retired;
}

public sealed class CapabilityAuthority
{
    private readonly record struct SubjectIdentity(DomainId DomainId, ulong Generation);

    private sealed class QuotaAccount(QuotaAccountReference reference, ulong remaining)
    {
        private ulong _remaining = remaining;
        public QuotaAccountReference Reference { get; } = reference;
        public ulong Remaining => Volatile.Read(ref _remaining);

        public bool TryConsume(ulong amount)
        {
            if (amount == 0) return false;
            while (true)
            {
                ulong observed = Volatile.Read(ref _remaining);
                if (amount > observed) return false;
                if (Interlocked.CompareExchange(ref _remaining, observed - amount, observed) == observed) return true;
            }
        }
    }

    private sealed class CapabilityRecord(
        CapabilityId id, Guid nonce, DomainId issuerDomainId, DomainId subjectDomainId,
        ulong subjectGeneration, ResourceKind resourceKind, string resourceId,
        ulong resourceGeneration, CapabilityRights rights, ulong revocationEpoch,
        CapabilityId? delegatedFrom, EffectiveCapabilityConstraints constraints, QuotaAccount quotaAccount)
    {
        public CapabilityId Id { get; } = id;
        public Guid Nonce { get; } = nonce;
        public DomainId IssuerDomainId { get; } = issuerDomainId;
        public DomainId SubjectDomainId { get; } = subjectDomainId;
        public ulong SubjectGeneration { get; } = subjectGeneration;
        public ResourceKind ResourceKind { get; } = resourceKind;
        public string ResourceId { get; } = resourceId;
        public ulong ResourceGeneration { get; } = resourceGeneration;
        public CapabilityRights Rights { get; } = rights;
        public ulong RevocationEpoch { get; } = revocationEpoch;
        public CapabilityId? DelegatedFrom { get; } = delegatedFrom;
        public EffectiveCapabilityConstraints Constraints { get; } = constraints;
        public QuotaAccount QuotaAccount { get; } = quotaAccount;
        public ulong QuotaConsumed { get; set; }
        public CapabilityRecordState State { get; set; } = CapabilityRecordState.Active;

        public CapabilityDescriptorV1 ProjectV1() => new(
            Id, IssuerDomainId, SubjectDomainId, ResourceKind, ResourceId,
            Rights, SubjectGeneration, RevocationEpoch);

        public CapabilityInspectionDescriptorV2 ProjectV2(AuthorityRealmId realm) => new(
            new(CapabilityHandleV2Contract.Version, realm, Nonce), IssuerDomainId, SubjectDomainId,
            ResourceKind, ResourceId, Rights, SubjectGeneration, ResourceGeneration, RevocationEpoch);
    }

    private const int NonceCollisionRetryLimit = 8;
    private readonly object _gate = new();
    // Sole authoritative record store. Opaque identity is verified against the record
    // reached through the non-reusable monotonic V1 compatibility slot.
    private readonly Dictionary<CapabilityId, CapabilityRecord> _records = [];
    // Same authoritative facts retained independently of the routing index.
    private readonly Dictionary<CapabilityId, CapabilityRecord> _originalRecords = [];
    // Opaque-token lookup index only. All authority remains in _records.
    private readonly Dictionary<Guid, CapabilityId> _opaqueIndex = [];
    private readonly Dictionary<DomainId, ulong> _domainEpochs = [];
    private readonly Dictionary<SubjectIdentity, int> _subjectLiveCounts = [];
    private readonly HashSet<OperationAuthorityLeaseId> _operationLeases = [];
    private readonly CapabilityAuthorityLimits _limits;
    private readonly Func<Guid> _nonceFactory;
    private readonly TimeProvider _timeProvider;
    private readonly string _realmFingerprint;
    private ulong _nextId;
    private ulong _nextQuotaAccountId = 1;
    private ulong _nextOperationLeaseId = 1;
    private bool _identityExhausted;
    // Admission-only callback reentry guard, not capability authority or permission.
    private readonly ThreadLocal<bool> _issuanceCallbackInProgress = new();

    public CapabilityAuthority()
        : this(new AuthorityRealmId(Guid.NewGuid()), CapabilityAuthorityLimits.Default, 1, Guid.NewGuid, TimeProvider.System) { }

    internal CapabilityAuthority(
        AuthorityRealmId realmId, CapabilityAuthorityLimits limits,
        ulong initialCapabilityId = 1, Func<Guid>? nonceFactory = null, TimeProvider? timeProvider = null)
    {
        if (realmId.Value == Guid.Empty) throw new ArgumentException("Authority realm must be non-empty.", nameof(realmId));
        if (limits.GlobalRecordCapacity <= 0 || limits.PerSubjectLiveCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
        if (initialCapabilityId == 0) throw new ArgumentOutOfRangeException(nameof(initialCapabilityId));
        RealmId = realmId;
        _limits = limits;
        _nextId = initialCapabilityId;
        _nonceFactory = nonceFactory ?? Guid.NewGuid;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _realmFingerprint = Convert.ToHexString(SHA256.HashData(realmId.Value.ToByteArray()))[..16];
    }

    public AuthorityRealmId RealmId { get; }

    internal KernelResult<CapabilityDescriptorV1> Mint(
        DomainId issuerDomainId, DomainId subjectDomainId, ResourceKind resourceKind,
        string resourceId, CapabilityRights rights, ulong subjectGeneration,
        ulong resourceGeneration = 1,
        Func<Func<KernelResult<CapabilityDescriptorV1>>, KernelResult<CapabilityDescriptorV1>>? commitConsumer = null)
        => Mint(issuerDomainId, subjectDomainId, resourceKind, resourceId, rights, subjectGeneration,
            resourceGeneration, range: null, session: null, quota: ulong.MaxValue, delegationDepth: 16,
            commitConsumer: commitConsumer);

    internal KernelResult<CapabilityDescriptorV1> Mint(
        DomainId issuerDomainId, DomainId subjectDomainId, ResourceKind resourceKind,
        string resourceId, CapabilityRights rights, ulong subjectGeneration,
        ulong resourceGeneration, RangeConstraint? range, EndpointSessionHandle? session,
        ulong quota, ushort delegationDepth,
        CapabilityConstraintSchema schema = CapabilityConstraintSchema.V1,
        IEnumerable<CapabilityOperation>? operations = null,
        long notBeforeUtcTicks = 0, long expiresUtcTicks = long.MaxValue,
        ResourceUseConstraintV1? resourceUse = null,
        Func<Func<KernelResult<CapabilityDescriptorV1>>, KernelResult<CapabilityDescriptorV1>>? commitConsumer = null)
    {
        if (rights == CapabilityRights.None) throw new ArgumentOutOfRangeException(nameof(rights));
        if (_issuanceCallbackInProgress.Value || (commitConsumer is not null && Monitor.IsEntered(_gate)))
            return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapacityExhausted, "Capability issuance cannot reenter an issuance callback.");
        if (string.IsNullOrWhiteSpace(resourceId)) throw new ArgumentException("Resource id is required.", nameof(resourceId));
        if (subjectGeneration == 0) throw new ArgumentOutOfRangeException(nameof(subjectGeneration));
        if (resourceGeneration == 0) throw new ArgumentOutOfRangeException(nameof(resourceGeneration));
        SubjectIdentity subject = new SubjectIdentity(subjectDomainId, subjectGeneration);
        ulong subjectEpoch;
        QuotaAccountReference preparedQuota;
        lock (_gate)
        {
            if (_nextQuotaAccountId == 0)
                return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapacityExhausted, "Capability quota-account identity space is exhausted.");
            KernelResult capacity = EnsureCapacity(subject);
            if (!capacity.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(capacity.Error, capacity.Message!);
            subjectEpoch = CurrentEpoch(subjectDomainId);
            preparedQuota = new(_nextQuotaAccountId);
        }
        EffectiveCapabilityConstraints constraints;
        _issuanceCallbackInProgress.Value = true;
        try
        {
            try
            {
                // Caller collections/canonicalization are preparation, outside
                // the source owner gate. No quota identity is reserved here.
                constraints = new EffectiveCapabilityConstraints(schema, new(rights),
                    new(resourceKind, resourceId, resourceGeneration, null, null),
                    new(operations ?? OperationsFor(rights)), range,
                    new(notBeforeUtcTicks, expiresUtcTicks), new(null, 0, (rights & CapabilityRights.Delegate) != 0),
                    new(session), new(delegationDepth), new(preparedQuota, quota), resourceUse).Canonicalize();
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
            {
                return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.DelegationDenied, exception.Message);
            }
            for (int attempt = 0; attempt < NonceCollisionRetryLimit; attempt++)
            {
                lock (_gate)
                {
                    if (subjectEpoch != CurrentEpoch(subjectDomainId))
                        return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapabilityRevoked, "Subject domain changed during capability preparation.");
                    KernelResult capacity = EnsureCapacity(subject);
                    if (!capacity.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(capacity.Error, capacity.Message!);
                    if (_nextQuotaAccountId == 0 || _identityExhausted || _records.ContainsKey(new CapabilityId(_nextId)))
                        return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapacityExhausted, "Capability issuance identity space is exhausted or colliding.");
                }
                Guid nonce = _nonceFactory();
                if (nonce == Guid.Empty) continue;
                bool collided = false;
                KernelResult<CapabilityDescriptorV1> CommitSource()
                {
                    lock (_gate)
                    {
                        if (subjectEpoch != CurrentEpoch(subjectDomainId))
                            return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapabilityRevoked, "Subject domain changed during capability preparation.");
                        KernelResult capacity = EnsureCapacity(subject);
                        if (!capacity.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(capacity.Error, capacity.Message!);
                        if (_nextQuotaAccountId == 0 || _identityExhausted || _records.ContainsKey(new CapabilityId(_nextId)))
                            return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapacityExhausted, "Capability issuance identity space is exhausted or colliding.");
                        if (_opaqueIndex.ContainsKey(nonce))
                        {
                            collided = true;
                            return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapacityExhausted, "Capability nonce collision requires fresh preparation.");
                        }
                        CapabilityId id = new CapabilityId(_nextId);
                        QuotaAccountReference quotaReference = new QuotaAccountReference(_nextQuotaAccountId);
                        // Fresh authoritative quota identity replaces prepared
                        // canonical metadata; frozen inputs invoke no callbacks.
                        EffectiveCapabilityConstraints committed = constraints with { Quota = new(quotaReference, quota) };
                        QuotaAccount account = new QuotaAccount(quotaReference, quota);
                        CapabilityRecord record = new CapabilityRecord(id, nonce, issuerDomainId, subjectDomainId, subjectGeneration,
                            resourceKind, resourceId, resourceGeneration, rights, subjectEpoch, null, committed, account);
                        _originalRecords.EnsureCapacity(_originalRecords.Count + 1);
                        _records.EnsureCapacity(_records.Count + 1);
                        _originalRecords.Add(id, record);
                        _records.Add(id, record);
                        _opaqueIndex.Add(nonce, id);
                        IncrementLive(record);
                        if (_nextId == ulong.MaxValue) _identityExhausted = true;
                        else _nextId++;
                        _nextQuotaAccountId = _nextQuotaAccountId == ulong.MaxValue ? 0 : _nextQuotaAccountId + 1;
                        return KernelResult<CapabilityDescriptorV1>.Ok(record.ProjectV1());
                    }
                }
                KernelResult<CapabilityDescriptorV1> issued = commitConsumer is null ? CommitSource() : commitConsumer(CommitSource);
                if (!collided) return issued;
            }
            return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapacityExhausted, "Opaque identity allocation failed closed after bounded collision retries.");
        }
        finally { _issuanceCallbackInProgress.Value = false; }
    }

    internal KernelResult<CapabilityDescriptorV1> Delegate(
        CapabilityId sourceId, DomainId delegatorDomain, DomainId targetDomain,
        CapabilityRights rights, ulong targetGeneration)
        => Delegate(sourceId, delegatorDomain, targetDomain, rights, targetGeneration, null);

    internal KernelResult<CapabilityDescriptorV1> Delegate(
        CapabilityId sourceId, DomainId delegatorDomain, DomainId targetDomain,
        CapabilityRights rights, ulong targetGeneration,
        Func<EffectiveCapabilityConstraints, EffectiveCapabilityConstraints>? derive,
        Func<Func<KernelResult<CapabilityDescriptorV1>>, KernelResult<CapabilityDescriptorV1>>? commitConsumer = null)
    {
        if (_issuanceCallbackInProgress.Value || (commitConsumer is not null && Monitor.IsEntered(_gate)))
            return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapacityExhausted, "Capability issuance cannot reenter an issuance callback.");
        CapabilityRecord source;
        ulong targetEpoch;
        lock (_gate)
        {
            if (!TryResolveRecord(sourceId, out source!))
                return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapabilityNotFound, $"Capability {sourceId} does not exist.");
            KernelResult validation = ValidateRecord(source, delegatorDomain, source.SubjectGeneration, CapabilityRights.Delegate, null);
            if (!validation.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(validation.Error, validation.Message!);
            if ((source.Rights & rights) != rights || rights == CapabilityRights.None)
                return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.DelegationDenied, "Delegated rights must be a non-empty subset of the source capability.");
            KernelResult capacity = EnsureCapacity(new SubjectIdentity(targetDomain, targetGeneration));
            if (!capacity.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(capacity.Error, capacity.Message!);
            if (source.Constraints.DelegationDepth.RemainingDepth == 0)
                return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.DelegationDenied, "Delegation depth is exhausted.");
            targetEpoch = CurrentEpoch(targetDomain);
        }
        EffectiveCapabilityConstraints child;
        try
        {
            // Immutable metadata only: caller derivation/canonicalization runs
            // outside the authoritative source gate and grants no permission.
            EffectiveCapabilityConstraints baseline = source.Constraints with
            {
                Rights = new(rights), Operations = new(OperationsFor(rights)),
                TargetSubject = new(targetDomain, targetGeneration,
                    source.Constraints.TargetSubject.AllowsRetarget && (rights & CapabilityRights.Delegate) != 0),
                DelegationDepth = new((ushort)(source.Constraints.DelegationDepth.RemainingDepth - 1))
            };
            child = (derive?.Invoke(baseline) ?? baseline).Canonicalize();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        {
            return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.DelegationDenied, exception.Message);
        }
        if (child.TargetSubject.DomainId != targetDomain || child.TargetSubject.Generation != targetGeneration || child.Rights.Rights != rights)
            return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.DelegationDenied, "Child subject and rights must match the requested delegation.");
        if (!EffectiveCapabilityConstraints.IsSubset(child, source.Constraints))
            return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.DelegationDenied, "Child constraints are not a canonical subset of the parent.");
        KernelResult ValidateFreshSource()
        {
            if (!TryResolveRecord(sourceId, out CapabilityRecord? current) || !ReferenceEquals(current, source))
                return KernelResult.Fail(KernelError.CapabilityNotFound, "Captured source is no longer the authoritative record.");
            KernelResult validation = ValidateRecord(current, delegatorDomain, current.SubjectGeneration, CapabilityRights.Delegate, null);
            if (!validation.IsSuccess) return validation;
            if (targetEpoch != CurrentEpoch(targetDomain))
                return KernelResult.Fail(KernelError.CapabilityRevoked, "Target domain changed during capability preparation.");
            KernelResult capacity = EnsureCapacity(new SubjectIdentity(targetDomain, targetGeneration));
            if (!capacity.IsSuccess) return capacity;
            if (_identityExhausted || _records.ContainsKey(new CapabilityId(_nextId)))
                return KernelResult.Fail(KernelError.CapacityExhausted, "Capability identity space is exhausted or colliding.");
            return KernelResult.Ok();
        }
        for (int attempt = 0; attempt < NonceCollisionRetryLimit; attempt++)
        {
            lock (_gate)
            {
                KernelResult validation = ValidateFreshSource();
                if (!validation.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(validation.Error, validation.Message!);
            }
            Guid nonce;
            _issuanceCallbackInProgress.Value = true;
            try { nonce = _nonceFactory(); }
            finally { _issuanceCallbackInProgress.Value = false; }
            if (nonce == Guid.Empty) continue;
            bool collided = false;
            KernelResult<CapabilityDescriptorV1> CommitSource()
            {
                lock (_gate)
                {
                    KernelResult validation = ValidateFreshSource();
                    if (!validation.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(validation.Error, validation.Message!);
                    if (_opaqueIndex.ContainsKey(nonce))
                    {
                        collided = true;
                        return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapacityExhausted, "Capability nonce collision requires fresh preparation.");
                    }
                    CapabilityId id = new CapabilityId(_nextId);
                    CapabilityRecord record = new CapabilityRecord(id, nonce, delegatorDomain, targetDomain, targetGeneration,
                        source.ResourceKind, source.ResourceId, source.ResourceGeneration, rights,
                        targetEpoch, sourceId, child, source.QuotaAccount);
                    _originalRecords.EnsureCapacity(_originalRecords.Count + 1);
                    _records.EnsureCapacity(_records.Count + 1);
                    _originalRecords.Add(id, record);
                    _records.Add(id, record);
                    _opaqueIndex.Add(nonce, id);
                    IncrementLive(record);
                    if (_nextId == ulong.MaxValue) _identityExhausted = true;
                    else _nextId++;
                    return KernelResult<CapabilityDescriptorV1>.Ok(record.ProjectV1());
                }
            }
            KernelResult<CapabilityDescriptorV1> delegated = commitConsumer is null ? CommitSource() : commitConsumer(CommitSource);
            if (!collided) return delegated;
        }
        return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapacityExhausted, "Opaque identity allocation failed closed after bounded collision retries.");
    }
    public KernelResult<CapabilityDescriptorV1> Validate(
        CapabilityId id, DomainId subject, ulong generation, CapabilityRights requiredRights,
        ulong? expectedResourceGeneration = null)
    {
        lock (_gate)
        {
            if (!TryResolveRecord(id, out CapabilityRecord? record))
                return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapabilityNotFound, $"Capability {id} does not exist.");
            return ValidateAndProject(record, subject, generation, requiredRights, expectedResourceGeneration);
        }
    }

    internal KernelResult<ResourceUseConstraintV1> ValidateResourceUse(
        CapabilityId id, DomainId subject, ulong subjectGeneration,
        ulong expectedResourceGeneration, ResourceEnvelopeV1 requestedEnvelope)
    {
        KernelResult<long> time = ReadResourceUseClock(id, subject, subjectGeneration, expectedResourceGeneration);
        if (!time.IsSuccess) return KernelResult<ResourceUseConstraintV1>.Fail(time.Error, time.Message!);
        lock (_gate)
            return ValidateResourceUseCore(id, subject, subjectGeneration, expectedResourceGeneration, requestedEnvelope, time.Value);
    }

    private KernelResult<long> ReadResourceUseClock(CapabilityId id, DomainId subject,
        ulong subjectGeneration, ulong expectedResourceGeneration)
    {
        lock (_gate)
        {
            if (!TryResolveRecord(id, out CapabilityRecord? source))
                return KernelResult<long>.Fail(KernelError.CapabilityNotFound, "Resource-use grant does not exist.");
            KernelResult live = ValidateRecord(source, subject, subjectGeneration, CapabilityRights.None, expectedResourceGeneration);
            if (!live.IsSuccess) return KernelResult<long>.Fail(live.Error, live.Message!);
            if (source.Constraints.ResourceUse is null)
                return KernelResult<long>.Fail(KernelError.InsufficientRights, "Capability has no resource-use grant.");
        }
        try { return KernelResult<long>.Ok(_timeProvider.GetUtcNow().UtcTicks); }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return KernelResult<long>.Fail(KernelError.PlatformFaulted, "Resource-use capability clock failed.");
        }
    }

    // Caller holds the existing capability owner gate; this validation has no clock callbacks.
    private KernelResult<ResourceUseConstraintV1> ValidateResourceUseCore(
        CapabilityId id, DomainId subject, ulong subjectGeneration, ulong expectedResourceGeneration,
        ResourceEnvelopeV1 requestedEnvelope, long now)
    {
        if (!TryResolveRecord(id, out CapabilityRecord? record))
            return KernelResult<ResourceUseConstraintV1>.Fail(KernelError.CapabilityNotFound, "Resource-use grant does not exist.");
        KernelResult live = ValidateRecord(record, subject, subjectGeneration, CapabilityRights.None, expectedResourceGeneration);
        if (!live.IsSuccess) return KernelResult<ResourceUseConstraintV1>.Fail(live.Error, live.Message!);
        if (record.Constraints.ResourceUse is not { } grant)
            return KernelResult<ResourceUseConstraintV1>.Fail(KernelError.InsufficientRights, "Capability has no resource-use grant.");
        if (now < grant.NotBeforeUtcTicks || now >= grant.ExpiresUtcTicks)
            return KernelResult<ResourceUseConstraintV1>.Fail(KernelError.DeadlineExpired, "Resource-use grant is outside its validity interval.");
        ResourceUseConstraintV1 requested;
        try { requested = grant with { Envelope = requestedEnvelope.Canonicalize() }; }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        {
            return KernelResult<ResourceUseConstraintV1>.Fail(KernelError.InvalidMessage, exception.Message);
        }
        return ResourceUseConstraintV1.IsSubset(requested, grant)
            ? KernelResult<ResourceUseConstraintV1>.Ok(grant)
            : KernelResult<ResourceUseConstraintV1>.Fail(KernelError.InsufficientRights, "Requested resource envelope exceeds the live grant.");
    }

    internal KernelResult CommitResourceDonationPublication(CapabilityId source, DomainId callerDomain,
        ulong callerGeneration, ulong sourceResourceGeneration, CapabilityId derived, DomainId serviceDomain,
        ulong serviceGeneration, ulong derivedResourceGeneration, ResourceEnvelopeV1 envelope,
        Func<Func<KernelResult>, KernelResult> ownerCommit, Func<KernelResult> publish)
    {
        KernelResult<long> time = ReadResourceUseClock(derived, serviceDomain, serviceGeneration, derivedResourceGeneration);
        if (!time.IsSuccess) return KernelResult.Fail(time.Error, time.Message!);
        return ownerCommit(() =>
        {
            lock (_gate)
            {
                KernelResult<ResourceUseConstraintV1> original = ValidateResourceUseCore(source, callerDomain, callerGeneration,
                    sourceResourceGeneration, envelope, time.Value);
                if (!original.IsSuccess) return KernelResult.Fail(original.Error, original.Message!);
                KernelResult<ResourceUseConstraintV1> delegated = ValidateResourceUseCore(derived, serviceDomain, serviceGeneration,
                    derivedResourceGeneration, envelope, time.Value);
                if (!delegated.IsSuccess) return KernelResult.Fail(delegated.Error, delegated.Message!);
                if (!TryResolveRecord(derived, out CapabilityRecord? record) || record.DelegatedFrom != source)
                    return KernelResult.Fail(KernelError.DelegationDenied, "Donation derived grant lost exact source lineage.");
                return publish(); // Trusted local commits only.
            }
        });
    }

    internal KernelResult CommitDonationConsumption(CapabilityId grant, DomainId subject,
        ulong subjectGeneration, ulong resourceGeneration, ResourceEnvelopeV1 envelope,
        Func<Func<KernelResult>, KernelResult> ownerCommit, Func<KernelResult> consume)
    {
        KernelResult<long> time = ReadResourceUseClock(grant, subject, subjectGeneration, resourceGeneration);
        if (!time.IsSuccess) return KernelResult.Fail(time.Error, time.Message!);
        return ownerCommit(() =>
        {
            lock (_gate)
            {
                KernelResult<ResourceUseConstraintV1> permission = ValidateResourceUseCore(grant, subject, subjectGeneration, resourceGeneration, envelope, time.Value);
                return permission.IsSuccess ? consume() : KernelResult.Fail(permission.Error, permission.Message!);
            }
        });
    }

    internal KernelResult<ResourceUseAuthorityLease> AcquireResourceUseAuthority(
        CapabilityId id, DomainId subject, ulong subjectGeneration,
        ulong expectedResourceGeneration, ResourceEnvelopeV1 requestedEnvelope,
        Func<KernelResult>? revalidateConsumer = null)
    {
        KernelResult<long> time = ReadResourceUseClock(id, subject, subjectGeneration, expectedResourceGeneration);
        if (!time.IsSuccess) return KernelResult<ResourceUseAuthorityLease>.Fail(time.Error, time.Message!);
        if (revalidateConsumer is not null)
        {
            lock (_gate)
            {
                KernelResult<ResourceUseConstraintV1> before = ValidateResourceUseCore(id, subject, subjectGeneration, expectedResourceGeneration, requestedEnvelope, time.Value);
                if (!before.IsSuccess) return KernelResult<ResourceUseAuthorityLease>.Fail(before.Error, before.Message!);
            }
            KernelResult consumer = revalidateConsumer();
            if (!consumer.IsSuccess) return KernelResult<ResourceUseAuthorityLease>.Fail(consumer.Error, consumer.Message!);
        }
        lock (_gate)
        {
            KernelResult<ResourceUseConstraintV1> validation = ValidateResourceUseCore(id, subject, subjectGeneration, expectedResourceGeneration, requestedEnvelope, time.Value);
            if (!validation.IsSuccess) return KernelResult<ResourceUseAuthorityLease>.Fail(validation.Error, validation.Message!);
            if (_nextOperationLeaseId == 0)
                return KernelResult<ResourceUseAuthorityLease>.Fail(KernelError.CapacityExhausted, "Resource-use authority lease identity space is exhausted.");
            OperationAuthorityLeaseId leaseId = new OperationAuthorityLeaseId(_nextOperationLeaseId++);
            _operationLeases.Add(leaseId);
            return KernelResult<ResourceUseAuthorityLease>.Ok(new ResourceUseAuthorityLease(
                this, leaseId, id, subject, subjectGeneration, expectedResourceGeneration, validation.Value!));
        }
    }
    internal KernelResult<CapabilityDescriptorV1> Validate(
        CapabilityAuthorityReference reference, DomainId subject, ulong generation,
        CapabilityRights requiredRights, ulong? expectedResourceGeneration = null)
    {
        lock (_gate)
        {
            if (reference.RealmId != RealmId)
                return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.WrongAuthorityRealm, "Capability belongs to another runtime authority realm.");
            if (!TryResolveRecord(reference.CapabilityId, out CapabilityRecord? record))
                return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapabilityNotFound, "Capability does not exist in this authority realm.");
            if (record.Nonce != reference.Nonce)
                return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.ForgedCapability, "Capability opaque identity is invalid.");
            return ValidateAndProject(record, subject, generation, requiredRights, expectedResourceGeneration);
        }
    }

    internal KernelResult<CapabilityAuthorityReference> GetReference(CapabilityId id)
    {
        lock (_gate)
            return TryResolveRecord(id, out CapabilityRecord? record)
                ? KernelResult<CapabilityAuthorityReference>.Ok(new(RealmId, id, record.Nonce))
                : KernelResult<CapabilityAuthorityReference>.Fail(KernelError.CapabilityNotFound, "Capability does not exist.");
    }

    internal KernelResult<CapabilityHandleV2> GetHandleV2(
        CapabilityId id, DomainId subject, ulong subjectGeneration)
    {
        lock (_gate)
        {
            if (!TryResolveRecord(id, out CapabilityRecord? record))
                return KernelResult<CapabilityHandleV2>.Fail(KernelError.CapabilityNotFound, "Capability does not exist.");
            KernelResult validation = ValidateRecord(record, subject, subjectGeneration, CapabilityRights.None, null);
            return validation.IsSuccess
                ? KernelResult<CapabilityHandleV2>.Ok(record.ProjectV2(RealmId).Handle)
                : KernelResult<CapabilityHandleV2>.Fail(validation.Error, validation.Message!);
        }
    }

    internal KernelResult<CapabilityId> ResolveCapabilityId(CapabilityHandleV2 handle)
    {
        lock (_gate)
        {
            KernelResult<CapabilityRecord> resolved = ResolveV2(handle);
            return resolved.IsSuccess
                ? KernelResult<CapabilityId>.Ok(resolved.Value!.Id)
                : KernelResult<CapabilityId>.Fail(resolved.Error, resolved.Message!);
        }
    }

    internal KernelResult<CapabilityInspectionDescriptorV2> Validate(
        CapabilityHandleV2 handle, DomainId subject, ulong subjectGeneration,
        CapabilityRights requiredRights, ulong? expectedResourceGeneration = null)
    {
        lock (_gate)
        {
            KernelResult<CapabilityRecord> resolved = ResolveV2(handle);
            if (!resolved.IsSuccess)
                return KernelResult<CapabilityInspectionDescriptorV2>.Fail(resolved.Error, resolved.Message!);
            KernelResult validation = ValidateRecord(resolved.Value!, subject, subjectGeneration, requiredRights, expectedResourceGeneration);
            return validation.IsSuccess
                ? KernelResult<CapabilityInspectionDescriptorV2>.Ok(resolved.Value!.ProjectV2(RealmId))
                : KernelResult<CapabilityInspectionDescriptorV2>.Fail(validation.Error, validation.Message!);
        }
    }

    internal KernelResult<CapabilityHandleV2> Delegate(
        CapabilityHandleV2 source, DomainId delegatorDomain, DomainId targetDomain,
        CapabilityRights rights, ulong targetGeneration)
    {
        CapabilityId sourceId;
        lock (_gate)
        {
            KernelResult<CapabilityRecord> resolved = ResolveV2(source);
            if (!resolved.IsSuccess)
                return KernelResult<CapabilityHandleV2>.Fail(resolved.Error, resolved.Message!);
            sourceId = resolved.Value!.Id;
        }
        KernelResult<CapabilityDescriptorV1> delegated = Delegate(sourceId, delegatorDomain, targetDomain, rights, targetGeneration);
        if (!delegated.IsSuccess)
            return KernelResult<CapabilityHandleV2>.Fail(delegated.Error, delegated.Message!);
        lock (_gate)
        {
            if (!TryResolveRecord(delegated.Value!.CapabilityId, out CapabilityRecord? child))
                return KernelResult<CapabilityHandleV2>.Fail(KernelError.CapabilityNotFound, "Delegation committed but projection record is unavailable.");
            return KernelResult<CapabilityHandleV2>.Ok(child.ProjectV2(RealmId).Handle);
        }
    }
    internal KernelResult Revoke(CapabilityHandleV2 handle)
    {
        lock (_gate)
        {
            KernelResult<CapabilityRecord> resolved = ResolveV2(handle);
            return resolved.IsSuccess ? Revoke(resolved.Value!.Id) : KernelResult.Fail(resolved.Error, resolved.Message!);
        }
    }

    public KernelResult Revoke(CapabilityId id)
    {
        lock (_gate)
        {
            if (!TryResolveRecord(id, out CapabilityRecord? record))
                return KernelResult.Fail(KernelError.CapabilityNotFound, $"Capability {id} does not exist.");
            TransitionOutOfActive(record, CapabilityRecordState.Revoked);
            return KernelResult.Ok();
        }
    }

    internal bool DependsOnCapability(CapabilityId id, CapabilityId ancestor)
    {
        lock (_gate)
        {
            HashSet<CapabilityId> visited = new HashSet<CapabilityId>();
            while (true)
            {
                if (id == ancestor) return true;
                // Missing/cyclic lineage cannot prove independence during pending admission.
                if (!visited.Add(id) || !TryResolveRecord(id, out CapabilityRecord? record)) return true;
                if (record.DelegatedFrom is not { } parent) return false;
                id = parent;
            }
        }
    }

    internal bool HasOriginalRevocation(CapabilityId id)
    {
        lock (_gate)
            return TryResolveRecord(id, out CapabilityRecord? record) && record.State is CapabilityRecordState.Revoked or CapabilityRecordState.Retired;
    }

    internal KernelResult<T> CommitBudgetAdministration<T>(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult<T>> commit)
    {
        lock (_gate)
        {
            KernelResult<CapabilityDescriptorV1> valid = Validate(id, subject, generation, CapabilityRights.Configure);
            if (!valid.IsSuccess) return KernelResult<T>.Fail(valid.Error, valid.Message!);
            if (valid.Value!.ResourceKind != ResourceKind.KernelService ||
                valid.Value.ResourceId != CapabilityResourceIds.BudgetAdministration)
                return KernelResult<T>.Fail(KernelError.SupervisorDenied, "Budget administration requires the exact kernel budget capability.");
            // Trusted quantitative owner commit with a private array only. No caller collection,
            // provider, clock, trace or lifecycle callback may run while this gate is held.
            return commit();
        }
    }

    internal KernelResult CommitProcessAdmission(DomainId subject, ulong generation,
        CapabilityRequirementV1[] requirements, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            // The real kernel consumer supplies a private requirement array and
            // holds lifecycle scope. Descriptors/snapshots grant no admission.
            foreach (CapabilityRequirementV1 requirement in requirements)
            {
                if (!Enum.IsDefined(requirement.ResourceKind) || string.IsNullOrWhiteSpace(requirement.ResourceId) ||
                    requirement.Rights == CapabilityRights.None)
                    return KernelResult.Fail(KernelError.MissingCapability, "Captured mandatory capability requirement is invalid.");
                bool satisfied = _records.Values.Any(record =>
                    record.SubjectDomainId == subject && record.SubjectGeneration == generation &&
                    record.ResourceKind == requirement.ResourceKind &&
                    string.Equals(record.ResourceId, requirement.ResourceId, StringComparison.Ordinal) &&
                    ValidateRecord(record, subject, generation, requirement.Rights, null).IsSuccess);
                if (!satisfied)
                    return KernelResult.Fail(KernelError.MissingCapability,
                        $"Missing capability {requirement.ResourceKind}:{requirement.ResourceId} ({requirement.Rights}).");
            }
            // Trusted local state mutation only: no provider, clock, collection,
            // trace or caller callback may enter this owner commit.
            return commit();
        }
    }
    internal KernelResult CommitSupervisorRecordPublication(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult> publish)
    {
        lock (_gate)
        {
            KernelResult valid = CommitSupervisorTelemetryPublication(id, subject, generation);
            // Trusted local registry/graph mutation or snapshot only; caller holds the supervisor gate.
            // No clock, factory, kernel lifecycle, provider or trace callback here.
            return valid.IsSuccess ? publish() : valid;
        }
    }

    internal KernelResult CommitSupervisorComponentAllocation(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult> allocate)
    {
        lock (_gate)
        {
            KernelResult valid = CommitSupervisorTelemetryPublication(id, subject, generation);
            // Caller holds lifecycle/component scopes. Trusted local budget hierarchy
            // and component record mutation only; no lifecycle/provider/clock callback.
            return valid.IsSuccess ? allocate() : valid;
        }
    }

    internal KernelResult<T> CommitSupervisorProcessCreation<T>(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult<T>> create)
    {
        lock (_gate)
        {
            KernelResult valid = CommitSupervisorTelemetryPublication(id, subject, generation);
            // Caller holds lifecycle scope. Trusted ProcessRegistry/DomainRegistry
            // metadata mutation only, with no provider/clock/trace callback.
            return valid.IsSuccess ? create() : KernelResult<T>.Fail(valid.Error, valid.Message!);
        }
    }

    internal KernelResult<ServiceEndpointDescriptor> CommitSupervisorServiceRegistration(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult<ServiceEndpointDescriptor>> register)
    {
        lock (_gate)
        {
            KernelResult valid = CommitSupervisorTelemetryPublication(id, subject, generation);
            // Caller holds lifecycle/service scopes. Prepared local registry mutation only.
            return valid.IsSuccess ? register() : KernelResult<ServiceEndpointDescriptor>.Fail(valid.Error, valid.Message!);
        }
    }

    internal KernelResult CommitSupervisorIrqBinding(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            KernelResult valid = CommitSupervisorTelemetryPublication(id, subject, generation);
            return valid.IsSuccess ? commit() : valid;
        }
    }

    internal KernelResult<KernelEventEndpoint> CommitSupervisorIrqEndpoint(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult<KernelEventEndpoint>> create)
    {
        lock (_gate)
        {
            KernelResult valid = CommitSupervisorTelemetryPublication(id, subject, generation);
            // Trusted KernelEventRegistry local creation only; no callback.
            return valid.IsSuccess ? create() : KernelResult<KernelEventEndpoint>.Fail(valid.Error, valid.Message!);
        }
    }

    internal KernelResult CommitSupervisorMmioBinding(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            KernelResult valid = CommitSupervisorTelemetryPublication(id, subject, generation);
            // Trusted local MMIO reservation/receipt mutation only.
            return valid.IsSuccess ? commit() : valid;
        }
    }

    internal KernelResult CommitSupervisorDeviceBinding(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            KernelResult valid = CommitSupervisorTelemetryPublication(id, subject, generation);
            // Trusted device pending/receipt commit only; no provider callbacks.
            return valid.IsSuccess ? commit() : valid;
        }
    }

    internal KernelResult CommitSupervisorExecution(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            KernelResult valid = CommitSupervisorTelemetryPublication(id, subject, generation);
            // Trusted local admission/state/publication only; provider/trace outside.
            return valid.IsSuccess ? commit() : valid;
        }
    }

    internal KernelResult CommitSupervisorPlatformBinding(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            KernelResult valid = CommitSupervisorTelemetryPublication(id, subject, generation);
            // Trusted local pending/attachment mutation only; no provider callback.
            return valid.IsSuccess ? commit() : valid;
        }
    }

    internal KernelResult CommitSupervisorProcessAdmission(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult> admit)
    {
        lock (_gate)
        {
            KernelResult valid = CommitSupervisorTelemetryPublication(id, subject, generation);
            // Trusted mandatory-capability check and local process state commit only.
            return valid.IsSuccess ? admit() : valid;
        }
    }

    internal KernelResult<CapabilityDescriptorV1> CommitSupervisorGrantMint(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult<CapabilityDescriptorV1>> mint)
    {
        lock (_gate)
        {
            KernelResult valid = CommitSupervisorTelemetryPublication(id, subject, generation);
            // Only the prepared authority mint commit enters here. Nonce preparation,
            // trace sinks and component callbacks remain outside owner locks.
            return valid.IsSuccess ? mint() : KernelResult<CapabilityDescriptorV1>.Fail(valid.Error, valid.Message!);
        }
    }

    internal KernelResult<T> CommitSupervisorSessionAdmission<T>(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult<T>> commit)
    {
        lock (_gate)
        {
            KernelResult valid = CommitSupervisorTelemetryPublication(id, subject, generation);
            // Trusted lifetime-free local channel/session creation only. The caller
            // holds process-lifecycle and exact service admission gates. No native,
            // provider, clock, caller collection or supervisor callback here.
            return valid.IsSuccess ? commit() : KernelResult<T>.Fail(valid.Error, valid.Message!);
        }
    }

    // Trusted local channel/session publication with private owner snapshots only.
    internal KernelResult<T> CommitSessionPublication<T>(DomainId subject, ulong generation,
        CapabilityId[] capabilities, CapabilityRequirementV1[] requirements, Func<KernelResult<T>> publish)
    {
        lock (_gate)
        {
            foreach (CapabilityRequirementV1 requirement in requirements)
            {
                bool found = capabilities.Any(id => TryResolveRecord(id, out CapabilityRecord? record) &&
                    ValidateRecord(record, subject, generation, requirement.Rights, null).IsSuccess &&
                    record.ResourceKind == requirement.ResourceKind && record.ResourceId == requirement.ResourceId);
                if (!found) return KernelResult<T>.Fail(KernelError.MissingCapability,
                    $"Session requires {requirement.ResourceKind}:{requirement.ResourceId} ({requirement.Rights}).");
            }
            return publish();
        }
    }

    internal KernelResult CommitSupervisorTelemetryPublication(CapabilityId id, DomainId subject, ulong generation)
    {
        lock (_gate)
        {
            KernelResult<CapabilityDescriptorV1> valid = Validate(id, subject, generation, CapabilityRights.Configure | CapabilityRights.Execute);
            if (!valid.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, valid.Message!);
            return valid.Value!.ResourceKind == ResourceKind.KernelService &&
                valid.Value.ResourceId == CapabilityResourceIds.ServiceSupervisor
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.SupervisorDenied, "Capability does not authorize service-supervisor control.");
        }
    }

    internal KernelResult CommitCheckpointPublication(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            KernelResult<CapabilityDescriptorV1> valid = Validate(id, subject, generation, CapabilityRights.Configure);
            if (!valid.IsSuccess) return KernelResult.Fail(valid.Error, valid.Message!);
            if (valid.Value!.ResourceKind != ResourceKind.KernelService ||
                valid.Value.ResourceId != CapabilityResourceIds.CheckpointAdministration)
                return KernelResult.Fail(KernelError.SupervisorDenied, "Checkpoint publication requires exact administration permission.");
            return commit(); // Trusted local checkpoint owner commit only; no external callbacks.
        }
    }

    internal KernelResult CommitAuthorityInspectionPublication(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            KernelResult<CapabilityDescriptorV1> valid = Validate(id, subject, generation, CapabilityRights.Read);
            if (!valid.IsSuccess) return KernelResult.Fail(KernelError.ProjectionDenied, valid.Message!);
            if (valid.Value!.ResourceKind != ResourceKind.KernelService ||
                valid.Value.ResourceId != CapabilityResourceIds.AuthorityInspector)
                return KernelResult.Fail(KernelError.ProjectionDenied, "The capability does not authorize authority inspection.");
            return commit(); // Trusted pure detached snapshot publication only.
        }
    }

    internal KernelResult CommitTelemetryPublication(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            KernelResult<CapabilityDescriptorV1> valid = Validate(id, subject, generation, CapabilityRights.Read);
            if (!valid.IsSuccess) return KernelResult.Fail(valid.Error, valid.Message!);
            if (valid.Value!.ResourceKind != ResourceKind.KernelService ||
                valid.Value.ResourceId != CapabilityResourceIds.TelemetryInspection)
                return KernelResult.Fail(KernelError.ProjectionDenied, "The capability does not authorize telemetry inspection.");
            // Trusted local publication only: no clock, provider, trace or budget callbacks here.
            return commit();
        }
    }

    internal KernelResult CommitVirtualDomainEffect(CapabilityId id, DomainId subject,
        ulong generation, CapabilityRights rights, string resourceId, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            KernelResult<CapabilityDescriptorV1> valid = Validate(id, subject, generation, rights);
            if (!valid.IsSuccess) return KernelResult.Fail(valid.Error, valid.Message!);
            if (valid.Value!.ResourceKind != ResourceKind.Virtualization || valid.Value.ResourceId != resourceId)
                return KernelResult.Fail(KernelError.WrongCapabilityResource, "Virtual effect source changed.");
            return commit(); // Trusted pure local owner mutation only.
        }
    }

    internal KernelResult CommitGuestRegionSource(CapabilityId id, DomainId subject,
        ulong generation, CapabilityRights rights, string resourceId, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            KernelResult<CapabilityDescriptorV1> valid = Validate(id, subject, generation, rights);
            if (!valid.IsSuccess) return KernelResult.Fail(valid.Error, valid.Message!);
            if (valid.Value!.ResourceKind != ResourceKind.MemoryRegion || valid.Value.ResourceId != resourceId)
                return KernelResult.Fail(KernelError.WrongCapabilityResource, "Guest Region source changed.");
            return commit(); // Pure local admission/publication; never a provider callback.
        }
    }

    internal KernelResult CommitSecureDomainEffect(CapabilityId id, DomainId subject,
        ulong generation, string resourceId, Func<KernelResult> commit)
        => CommitSecureDomainEffect(id, subject, generation, CapabilityRights.Configure, resourceId, commit);

    internal KernelResult CommitSecureDomainEffect(CapabilityId id, DomainId subject,
        ulong generation, CapabilityRights rights, string resourceId, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            KernelResult<CapabilityDescriptorV1> valid = Validate(id, subject, generation, rights);
            if (!valid.IsSuccess) return KernelResult.Fail(valid.Error, valid.Message!);
            if (valid.Value!.ResourceKind != ResourceKind.SecureCompute || valid.Value.ResourceId != resourceId)
                return KernelResult.Fail(KernelError.WrongCapabilityResource, "Secure composition source changed.");
            return commit(); // Trusted pure owner transition only, never provider code.
        }
    }

    internal KernelResult CommitVirtualDomainCreation(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            KernelResult<CapabilityDescriptorV1> valid = Validate(id, subject, generation, CapabilityRights.Configure);
            if (!valid.IsSuccess) return KernelResult.Fail(valid.Error, valid.Message!);
            if (valid.Value!.ResourceKind != ResourceKind.Virtualization ||
                valid.Value.ResourceId != VirtualizationResourceIds.Create)
                return KernelResult.Fail(KernelError.WrongCapabilityResource, "Virtual-domain creation source changed.");
            // Trusted local admission/publication only; never provider or caller callbacks.
            return commit();
        }
    }

    internal KernelResult<T> CommitVirtualDomainCreation<T>(CapabilityId id, DomainId subject,
        ulong generation, Func<KernelResult<T>> commit)
    {
        lock (_gate)
        {
            KernelResult<CapabilityDescriptorV1> valid = Validate(id, subject, generation, CapabilityRights.Configure);
            if (!valid.IsSuccess) return KernelResult<T>.Fail(valid.Error, valid.Message!);
            if (valid.Value!.ResourceKind != ResourceKind.Virtualization ||
                valid.Value.ResourceId != VirtualizationResourceIds.Create)
                return KernelResult<T>.Fail(KernelError.WrongCapabilityResource, "Virtual-domain creation source changed.");
            // Trusted local admission/publication only; never provider or caller callbacks.
            return commit();
        }
    }

    internal KernelResult CommitIrqBindingAdmission(CapabilityId id, DomainId subject,
        ulong generation, string resourceId, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            KernelResult<CapabilityDescriptorV1> valid = Validate(id, subject, generation, CapabilityRights.Signal);
            if (!valid.IsSuccess) return KernelResult.Fail(valid.Error, valid.Message!);
            if (valid.Value!.ResourceKind != ResourceKind.Irq || valid.Value.ResourceId != resourceId)
                return KernelResult.Fail(KernelError.WrongCapabilityResource, "IRQ admission source changed.");
            // Trusted local publication only; never call a provider while holding the capability gate.
            return commit();
        }
    }
    internal KernelResult CommitDeviceLeaseAdmission(CapabilityId id, DomainId subject,
        ulong generation, CapabilityRights rights, string resourceId, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            KernelResult<CapabilityDescriptorV1> valid = Validate(id, subject, generation, rights);
            if (!valid.IsSuccess) return KernelResult.Fail(valid.Error, valid.Message!);
            if (valid.Value!.ResourceKind != ResourceKind.Device || valid.Value.ResourceId != resourceId)
                return KernelResult.Fail(KernelError.WrongCapabilityResource, "Device admission source changed.");
            // The trusted commit only publishes local owner state; it must not call a provider.
            return commit();
        }
    }

    internal KernelResult CommitMmioAdmission(CapabilityId id, DomainId subject,
        ulong generation, CapabilityRights rights, string resourceId, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            KernelResult<CapabilityDescriptorV1> valid = Validate(id, subject, generation, rights);
            if (!valid.IsSuccess) return KernelResult.Fail(valid.Error, valid.Message!);
            if (valid.Value!.ResourceKind != ResourceKind.MmioRegion || valid.Value.ResourceId != resourceId)
                return KernelResult.Fail(KernelError.WrongCapabilityResource, "MMIO admission source changed.");
            // Trusted local admission/publication only; provider callbacks stay outside this gate.
            return commit();
        }
    }

    internal KernelResult Retire(CapabilityId id)
    {
        lock (_gate)
        {
            if (!TryResolveRecord(id, out CapabilityRecord? record))
                return KernelResult.Fail(KernelError.CapabilityNotFound, $"Capability {id} does not exist.");
            if (record.State == CapabilityRecordState.Active) TransitionOutOfActive(record, CapabilityRecordState.Retired);
            else record.State = CapabilityRecordState.Retired;
            return KernelResult.Ok();
        }
    }

    public void RevokeAllForDomain(DomainId domainId)
    {
        lock (_gate)
        {
            ulong epoch = CurrentEpoch(domainId);
            _domainEpochs[domainId] = epoch == ulong.MaxValue ? ulong.MaxValue : epoch + 1;
            foreach (CapabilityRecord? record in _originalRecords.Values.Where(record => record.SubjectDomainId == domainId))
                TransitionOutOfActive(record, CapabilityRecordState.Revoked);
        }
    }

    public IReadOnlyList<CapabilityDescriptorV1> SnapshotForDomain(DomainId domainId)
    {
        lock (_gate)
            return _originalRecords.Values
                .Where(record => record.State == CapabilityRecordState.Active &&
                                 record.SubjectDomainId == domainId &&
                                 CurrentEpoch(domainId) != ulong.MaxValue &&
                                 record.RevocationEpoch == CurrentEpoch(domainId) &&
                                 ValidateLineage(record).IsSuccess)
                .Select(static record => record.ProjectV1())
                .OrderBy(static descriptor => descriptor.CapabilityId.Value)
                .ToArray();
    }

    internal CapabilityAuthorityInspectionRecord[] InspectionSnapshot()
    {
        lock (_gate)
            return _originalRecords.Values
                .Select(record => new CapabilityAuthorityInspectionRecord(record.ProjectV1(), _realmFingerprint,
                    record.ResourceGeneration, record.DelegatedFrom, record.State))
                .OrderBy(static record => record.Descriptor.CapabilityId.Value)
                .ToArray();
    }

    internal KernelResult ConsumeQuota(CapabilityId id, DomainId subject, ulong generation, ulong amount)
    {
        lock (_gate)
        {
            if (!TryResolveRecord(id, out CapabilityRecord? record))
                return KernelResult.Fail(KernelError.CapabilityNotFound, "Capability does not exist.");
            KernelResult validation = ValidateRecord(record, subject, generation, CapabilityRights.None, null);
            if (!validation.IsSuccess) return validation;
            if (amount == 0 || amount > record.Constraints.Quota.PerHandleCeiling - record.QuotaConsumed)
                return KernelResult.Fail(KernelError.BudgetExceeded, "Capability per-handle quota ceiling is exhausted.");
            if (!record.QuotaAccount.TryConsume(amount))
                return KernelResult.Fail(KernelError.BudgetExceeded, "Capability lineage quota is exhausted.");
            record.QuotaConsumed += amount;
            return KernelResult.Ok();
        }
    }

    internal KernelResult ConsumeQuota(CapabilityHandleV2 handle, DomainId subject, ulong generation, ulong amount)
    {
        lock (_gate)
        {
            KernelResult<CapabilityRecord> resolved = ResolveV2(handle);
            return resolved.IsSuccess
                ? ConsumeQuota(resolved.Value!.Id, subject, generation, amount)
                : KernelResult.Fail(resolved.Error, resolved.Message!);
        }
    }

    internal KernelResult<OperationAuthorityLease> AcquireOperationAuthority(
        CapabilityId id, DomainId subject, ulong subjectGeneration,
        ResourceKind resourceKind, string resourceId, ulong resourceGeneration,
        CapabilityOperation operation, EndpointSessionHandle? session = null,
        ulong quotaAmount = 0, bool oneShot = false, Func<KernelResult>? revalidateConsumer = null,
        Func<Func<KernelResult<OperationAuthorityLease>>, KernelResult<OperationAuthorityLease>>? commitConsumer = null)
    {
        KernelResult<long> time = ReadOperationAdmissionClock(id, subject, subjectGeneration, resourceKind,
            resourceId, resourceGeneration, operation, session);
        if (!time.IsSuccess) return KernelResult<OperationAuthorityLease>.Fail(time.Error, time.Message!);
        if (revalidateConsumer is not null)
        {
            lock (_gate)
            {
                if (!TryResolveRecord(id, out CapabilityRecord? source))
                    return KernelResult<OperationAuthorityLease>.Fail(KernelError.CapabilityNotFound, "Capability does not exist.");
                KernelResult valid = ValidateOperationAdmission(source, subject, subjectGeneration, resourceKind,
                    resourceId, resourceGeneration, operation, session, time.Value);
                if (!valid.IsSuccess) return KernelResult<OperationAuthorityLease>.Fail(valid.Error, valid.Message!);
            }
            // Actual consumer revalidation is preparation, outside the capability owner gate.
            KernelResult consumer = revalidateConsumer();
            if (!consumer.IsSuccess) return KernelResult<OperationAuthorityLease>.Fail(consumer.Error, consumer.Message!);
        }
        // Actual consumer may freeze its existing local lifecycle/session owners
        // around the pure source commit. Clock/callback preparation stays above.
        return commitConsumer is null ? CommitSource() : commitConsumer(CommitSource);

        KernelResult<OperationAuthorityLease> CommitSource()
        {
            lock (_gate)
            {
                if (!TryResolveRecord(id, out CapabilityRecord? record))
                    return KernelResult<OperationAuthorityLease>.Fail(KernelError.CapabilityNotFound, "Capability does not exist.");
                KernelResult validation = ValidateOperationAdmission(record, subject, subjectGeneration,
                    resourceKind, resourceId, resourceGeneration, operation, session, time.Value);
                if (!validation.IsSuccess) return KernelResult<OperationAuthorityLease>.Fail(validation.Error, validation.Message!);
                if (_nextOperationLeaseId == 0)
                    return KernelResult<OperationAuthorityLease>.Fail(KernelError.CapacityExhausted, "Operation-authority lease identity space is exhausted.");
                if (quotaAmount != 0)
                {
                    if (quotaAmount > record.Constraints.Quota.PerHandleCeiling - record.QuotaConsumed ||
                        !record.QuotaAccount.TryConsume(quotaAmount))
                        return KernelResult<OperationAuthorityLease>.Fail(KernelError.BudgetExceeded, "Capability operation quota is exhausted.");
                    record.QuotaConsumed += quotaAmount;
                }
                if (oneShot) TransitionOutOfActive(record, CapabilityRecordState.Consumed);
                OperationAuthorityLeaseId leaseId = new OperationAuthorityLeaseId(_nextOperationLeaseId++);
                _operationLeases.Add(leaseId);
                OperationAuthorityLease lease = new OperationAuthorityLease(this, leaseId, id, subject, subjectGeneration,
                    record.Constraints.Resource, operation, session, RevocationPolicyFor(operation));
                return KernelResult<OperationAuthorityLease>.Ok(lease);
            }
        }
    }

    internal KernelResult<OperationAuthorityLease> AcquireOperationAuthority(
        CapabilityHandleV2 handle, DomainId subject, ulong subjectGeneration,
        ResourceKind resourceKind, string resourceId, ulong resourceGeneration,
        CapabilityOperation operation, EndpointSessionHandle? session = null,
        ulong quotaAmount = 0, bool oneShot = false)
    {
        CapabilityId id;
        lock (_gate)
        {
            KernelResult<CapabilityRecord> resolved = ResolveV2(handle);
            if (!resolved.IsSuccess) return KernelResult<OperationAuthorityLease>.Fail(resolved.Error, resolved.Message!);
            id = resolved.Value!.Id;
        }
        return AcquireOperationAuthority(id, subject, subjectGeneration, resourceKind, resourceId,
            resourceGeneration, operation, session, quotaAmount, oneShot);
    }

    internal KernelResult ValidateOperationAdmission(CapabilityId id, DomainId subject, ulong subjectGeneration,
        ResourceKind resourceKind, string resourceId, ulong resourceGeneration,
        CapabilityOperation operation, EndpointSessionHandle? session = null)
    {
        KernelResult<long> time = ReadOperationAdmissionClock(id, subject, subjectGeneration, resourceKind,
            resourceId, resourceGeneration, operation, session);
        if (!time.IsSuccess) return KernelResult.Fail(time.Error, time.Message!);
        lock (_gate)
            return TryResolveRecord(id, out CapabilityRecord? record)
                ? ValidateOperationAdmission(record, subject, subjectGeneration, resourceKind, resourceId,
                    resourceGeneration, operation, session, time.Value)
                : KernelResult.Fail(KernelError.CapabilityNotFound, "Capability does not exist.");
    }

    private KernelResult<long> ReadOperationAdmissionClock(CapabilityId id, DomainId subject, ulong subjectGeneration,
        ResourceKind resourceKind, string resourceId, ulong resourceGeneration,
        CapabilityOperation operation, EndpointSessionHandle? session)
    {
        lock (_gate)
        {
            if (!TryResolveRecord(id, out CapabilityRecord? source))
                return KernelResult<long>.Fail(KernelError.CapabilityNotFound, "Capability does not exist.");
            KernelResult valid = ValidateOperationAdmission(source, subject, subjectGeneration, resourceKind,
                resourceId, resourceGeneration, operation, session, null);
            if (!valid.IsSuccess) return KernelResult<long>.Fail(valid.Error, valid.Message!);
        }
        try { return KernelResult<long>.Ok(_timeProvider.GetUtcNow().UtcTicks); }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return KernelResult<long>.Fail(KernelError.PlatformFaulted, "Capability operation clock failed.");
        }
    }

    private KernelResult ValidateOperationAdmission(CapabilityRecord record, DomainId subject, ulong subjectGeneration,
        ResourceKind resourceKind, string resourceId, ulong resourceGeneration,
        CapabilityOperation operation, EndpointSessionHandle? session, long? now)
    {
        KernelResult validation = ValidateRecord(record, subject, subjectGeneration, CapabilityRights.None, resourceGeneration);
        if (!validation.IsSuccess) return validation;
        if (record.ResourceKind != resourceKind || !string.Equals(record.ResourceId, resourceId, StringComparison.Ordinal))
            return KernelResult.Fail(KernelError.WrongCapabilityResource, "Capability does not authorize the exact resource.");
        if (!Enum.IsDefined(operation) || !record.Constraints.Operations.Operations.Contains(operation))
            return KernelResult.Fail(KernelError.InsufficientRights, "Capability does not authorize the exact operation.");
        if (now is null) return KernelResult.Ok();
        if (now < record.Constraints.Lifetime.NotBeforeUtcTicks || now >= record.Constraints.Lifetime.ExpiresUtcTicks)
            return KernelResult.Fail(KernelError.DeadlineExpired, "Capability lifetime does not admit this operation.");
        return record.Constraints.Session.Session is { } constrained && constrained != session
            ? KernelResult.Fail(KernelError.WrongSessionOwner, "Capability is bound to another endpoint session.")
            : KernelResult.Ok();
    }

    internal void ReleaseOperationAuthority(OperationAuthorityLeaseId lease)
    {
        lock (_gate) _operationLeases.Remove(lease);
    }

    internal int ActiveOperationLeaseCount
    {
        get { lock (_gate) return _operationLeases.Count; }
    }

    internal (EffectiveCapabilityConstraints Constraints, ulong SharedRemaining, ulong HandleConsumed)? InspectConstraints(CapabilityId id)
    {
        lock (_gate)
            return TryResolveRecord(id, out CapabilityRecord? record)
                ? (record.Constraints, record.QuotaAccount.Remaining, record.QuotaConsumed)
                : null;
    }

    private bool TryResolveRecord(CapabilityId id, out CapabilityRecord record)
    {
        if (_records.TryGetValue(id, out CapabilityRecord? routed) &&
            _originalRecords.TryGetValue(id, out CapabilityRecord? original) &&
            ReferenceEquals(routed, original) && original.Id == id)
        {
            record = original;
            return true;
        }
        record = null!;
        return false;
    }
    private KernelResult EnsureCapacity(SubjectIdentity subject)
    {
        if (CurrentEpoch(subject.DomainId) == ulong.MaxValue)
            return KernelResult.Fail(KernelError.CapabilityRevoked, "Capability domain revocation epoch is terminal.");
        if (_originalRecords.Count >= _limits.GlobalRecordCapacity)
            return KernelResult.Fail(KernelError.CapacityExhausted, "The global capability record table is exhausted.");
        if (_subjectLiveCounts.GetValueOrDefault(subject) >= _limits.PerSubjectLiveCapacity)
            return KernelResult.Fail(KernelError.CapacityExhausted, "The subject live-capability quota is exhausted.");
        return KernelResult.Ok();
    }

    private KernelResult<CapabilityRecord> ResolveV2(CapabilityHandleV2 handle)
    {
        if (handle.Version != CapabilityHandleV2Contract.Version)
            return KernelResult<CapabilityRecord>.Fail(KernelError.InvalidMessage, "Capability handle schema version is unsupported.");
        if (handle.RealmId != RealmId)
            return KernelResult<CapabilityRecord>.Fail(KernelError.WrongAuthorityRealm, "Capability belongs to another runtime authority realm.");
        if (handle.OpaqueToken == Guid.Empty || !_opaqueIndex.TryGetValue(handle.OpaqueToken, out CapabilityId id) ||
            !TryResolveRecord(id, out CapabilityRecord? record) || record.Nonce != handle.OpaqueToken)
            return KernelResult<CapabilityRecord>.Fail(KernelError.ForgedCapability, "Capability opaque identity is invalid.");
        return KernelResult<CapabilityRecord>.Ok(record);
    }

    private KernelResult<CapabilityDescriptorV1> ValidateAndProject(
        CapabilityRecord record, DomainId subject, ulong generation,
        CapabilityRights requiredRights, ulong? expectedResourceGeneration)
    {
        KernelResult result = ValidateRecord(record, subject, generation, requiredRights, expectedResourceGeneration);
        return result.IsSuccess
            ? KernelResult<CapabilityDescriptorV1>.Ok(record.ProjectV1())
            : KernelResult<CapabilityDescriptorV1>.Fail(result.Error, result.Message!);
    }

    private KernelResult ValidateRecord(
        CapabilityRecord record, DomainId subject, ulong generation,
        CapabilityRights requiredRights, ulong? expectedResourceGeneration)
    {
        if (!TryResolveRecord(record.Id, out CapabilityRecord? original) || !ReferenceEquals(original, record))
            return KernelResult.Fail(KernelError.CapabilityNotFound, "Capability route is not the original owner record.");
        if (record.SubjectDomainId != subject)
            return KernelResult.Fail(KernelError.WrongCapabilitySubject, "Capability subject does not match the caller domain.");
        if (record.SubjectGeneration != generation)
            return KernelResult.Fail(KernelError.StaleGeneration, "Capability subject generation is stale.");
        if (expectedResourceGeneration is { } expected && record.ResourceGeneration != expected)
            return KernelResult.Fail(KernelError.StaleGeneration, "Capability resource generation is stale.");
        if (record.State != CapabilityRecordState.Active || CurrentEpoch(subject) == ulong.MaxValue ||
            record.RevocationEpoch != CurrentEpoch(subject))
            return KernelResult.Fail(KernelError.CapabilityRevoked, "Capability is not active.");
        KernelResult lineage = ValidateLineage(record);
        if (!lineage.IsSuccess) return lineage;
        if ((record.Rights & requiredRights) != requiredRights)
            return KernelResult.Fail(KernelError.InsufficientRights, "Capability does not provide the required rights.");
        return KernelResult.Ok();
    }

    private void IncrementLive(CapabilityRecord record)
    {
        SubjectIdentity subject = new SubjectIdentity(record.SubjectDomainId, record.SubjectGeneration);
        _subjectLiveCounts[subject] = _subjectLiveCounts.GetValueOrDefault(subject) + 1;
    }

    private void TransitionOutOfActive(CapabilityRecord record, CapabilityRecordState target)
    {
        if (record.State != CapabilityRecordState.Active) return;
        record.State = target;
        SubjectIdentity subject = new SubjectIdentity(record.SubjectDomainId, record.SubjectGeneration);
        int remaining = _subjectLiveCounts.GetValueOrDefault(subject) - 1;
        if (remaining <= 0) _subjectLiveCounts.Remove(subject);
        else _subjectLiveCounts[subject] = remaining;
    }

    private ulong CurrentEpoch(DomainId domainId) => _domainEpochs.TryGetValue(domainId, out ulong epoch) ? epoch : 0;

    private KernelResult ValidateLineage(CapabilityRecord record)
    {
        int remaining = 17;
        CapabilityRecord current = record;
        while (current.DelegatedFrom is { } parentId)
        {
            if (--remaining == 0)
                return KernelResult.Fail(KernelError.DelegationDenied, "Capability lineage exceeds its deterministic bound.");
            if (!TryResolveRecord(parentId, out CapabilityRecord? parent) || parent.State != CapabilityRecordState.Active ||
                CurrentEpoch(parent.SubjectDomainId) == ulong.MaxValue ||
                parent.RevocationEpoch != CurrentEpoch(parent.SubjectDomainId))
                return KernelResult.Fail(KernelError.CapabilityRevoked, "A capability ancestor is no longer active.");
            current = parent;
        }
        return KernelResult.Ok();
    }

    private static EffectRevocationPolicy RevocationPolicyFor(CapabilityOperation operation) => operation switch
    {
        CapabilityOperation.Read => EffectRevocationPolicy.GrandfatherAdmitted,
        CapabilityOperation.Write => EffectRevocationPolicy.AdmissionOnly,
        CapabilityOperation.Map => EffectRevocationPolicy.CancelIfPossible,
        CapabilityOperation.Signal => EffectRevocationPolicy.AdmissionOnly,
        CapabilityOperation.Configure => EffectRevocationPolicy.AdmissionOnly,
        CapabilityOperation.Transfer => EffectRevocationPolicy.GrandfatherAdmitted,
        CapabilityOperation.Delegate => EffectRevocationPolicy.AdmissionOnly,
        CapabilityOperation.Execute => EffectRevocationPolicy.CancelIfPossible,
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static IEnumerable<CapabilityOperation> OperationsFor(CapabilityRights rights)
    {
        if ((rights & CapabilityRights.Read) != 0) yield return CapabilityOperation.Read;
        if ((rights & CapabilityRights.Write) != 0) yield return CapabilityOperation.Write;
        if ((rights & CapabilityRights.Map) != 0) yield return CapabilityOperation.Map;
        if ((rights & CapabilityRights.Signal) != 0) yield return CapabilityOperation.Signal;
        if ((rights & CapabilityRights.Configure) != 0) yield return CapabilityOperation.Configure;
        if ((rights & CapabilityRights.Transfer) != 0) yield return CapabilityOperation.Transfer;
        if ((rights & CapabilityRights.Delegate) != 0) yield return CapabilityOperation.Delegate;
        if ((rights & CapabilityRights.Execute) != 0) yield return CapabilityOperation.Execute;
    }
}







