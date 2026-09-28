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
                var observed = Volatile.Read(ref _remaining);
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
        ulong resourceGeneration = 1)
        => Mint(issuerDomainId, subjectDomainId, resourceKind, resourceId, rights, subjectGeneration,
            resourceGeneration, range: null, session: null, quota: ulong.MaxValue, delegationDepth: 16);

    internal KernelResult<CapabilityDescriptorV1> Mint(
        DomainId issuerDomainId, DomainId subjectDomainId, ResourceKind resourceKind,
        string resourceId, CapabilityRights rights, ulong subjectGeneration,
        ulong resourceGeneration, RangeConstraint? range, EndpointSessionHandle? session,
        ulong quota, ushort delegationDepth,
        CapabilityConstraintSchema schema = CapabilityConstraintSchema.V1,
        IEnumerable<CapabilityOperation>? operations = null,
        long notBeforeUtcTicks = 0, long expiresUtcTicks = long.MaxValue,
        ResourceUseConstraintV1? resourceUse = null)
    {
        lock (_gate)
        {
            if (rights == CapabilityRights.None) throw new ArgumentOutOfRangeException(nameof(rights));
            if (string.IsNullOrWhiteSpace(resourceId)) throw new ArgumentException("Resource id is required.", nameof(resourceId));
            if (subjectGeneration == 0) throw new ArgumentOutOfRangeException(nameof(subjectGeneration));
            if (resourceGeneration == 0) throw new ArgumentOutOfRangeException(nameof(resourceGeneration));
            if (_nextQuotaAccountId == 0)
                return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapacityExhausted, "Capability quota-account identity space is exhausted.");
            var subject = new SubjectIdentity(subjectDomainId, subjectGeneration);
            var capacity = EnsureCapacity(subject);
            if (!capacity.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(capacity.Error, capacity.Message!);
            var quotaAccountReference = new QuotaAccountReference(_nextQuotaAccountId);
            EffectiveCapabilityConstraints constraints;
            try
            {
                constraints = new EffectiveCapabilityConstraints(schema, new(rights),
                    new(resourceKind, resourceId, resourceGeneration, null, null),
                    new(operations ?? OperationsFor(rights)), range,
                    new(notBeforeUtcTicks, expiresUtcTicks), new(null, 0, (rights & CapabilityRights.Delegate) != 0),
                    new(session), new(delegationDepth), new(quotaAccountReference, quota), resourceUse).Canonicalize();
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
            {
                return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.DelegationDenied, exception.Message);
            }
            var identity = AllocateIdentity();
            if (!identity.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(identity.Error, identity.Message!);
            var (id, nonce) = identity.Value;
            var account = new QuotaAccount(quotaAccountReference, quota);
            _nextQuotaAccountId = _nextQuotaAccountId == ulong.MaxValue ? 0 : _nextQuotaAccountId + 1;
            var record = new CapabilityRecord(id, nonce, issuerDomainId, subjectDomainId, subjectGeneration,
                resourceKind, resourceId, resourceGeneration, rights, CurrentEpoch(subjectDomainId), null, constraints, account);
            _records.Add(id, record);
            _opaqueIndex.Add(nonce, id);
            IncrementLive(record);
            return KernelResult<CapabilityDescriptorV1>.Ok(record.ProjectV1());
        }
    }

    internal KernelResult<CapabilityDescriptorV1> Delegate(
        CapabilityId sourceId, DomainId delegatorDomain, DomainId targetDomain,
        CapabilityRights rights, ulong targetGeneration)
        => Delegate(sourceId, delegatorDomain, targetDomain, rights, targetGeneration, null);

    internal KernelResult<CapabilityDescriptorV1> Delegate(
        CapabilityId sourceId, DomainId delegatorDomain, DomainId targetDomain,
        CapabilityRights rights, ulong targetGeneration,
        Func<EffectiveCapabilityConstraints, EffectiveCapabilityConstraints>? derive)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(sourceId, out var source))
                return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapabilityNotFound, $"Capability {sourceId} does not exist.");
            var validation = ValidateRecord(source, delegatorDomain, source.SubjectGeneration, CapabilityRights.Delegate, null);
            if (!validation.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(validation.Error, validation.Message!);
            if ((source.Rights & rights) != rights || rights == CapabilityRights.None)
                return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.DelegationDenied, "Delegated rights must be a non-empty subset of the source capability.");
            var capacity = EnsureCapacity(new SubjectIdentity(targetDomain, targetGeneration));
            if (!capacity.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(capacity.Error, capacity.Message!);
            EffectiveCapabilityConstraints child;
            try
            {
                if (source.Constraints.DelegationDepth.RemainingDepth == 0)
                    return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.DelegationDenied, "Delegation depth is exhausted.");
                var baseline = source.Constraints with
                {
                    Rights = new(rights),
                    Operations = new(OperationsFor(rights)),
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
            var identity = AllocateIdentity();
            if (!identity.IsSuccess) return KernelResult<CapabilityDescriptorV1>.Fail(identity.Error, identity.Message!);
            var (id, nonce) = identity.Value;
            var record = new CapabilityRecord(id, nonce, delegatorDomain, targetDomain, targetGeneration,
                source.ResourceKind, source.ResourceId, source.ResourceGeneration, rights,
                CurrentEpoch(targetDomain), sourceId, child, source.QuotaAccount);
            _records.Add(id, record);
            _opaqueIndex.Add(nonce, id);
            IncrementLive(record);
            return KernelResult<CapabilityDescriptorV1>.Ok(record.ProjectV1());
        }
    }

    public KernelResult<CapabilityDescriptorV1> Validate(
        CapabilityId id, DomainId subject, ulong generation, CapabilityRights requiredRights,
        ulong? expectedResourceGeneration = null)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record))
                return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapabilityNotFound, $"Capability {id} does not exist.");
            return ValidateAndProject(record, subject, generation, requiredRights, expectedResourceGeneration);
        }
    }

    internal KernelResult<ResourceUseConstraintV1> ValidateResourceUse(
        CapabilityId id, DomainId subject, ulong subjectGeneration,
        ulong expectedResourceGeneration, ResourceEnvelopeV1 requestedEnvelope)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record))
                return KernelResult<ResourceUseConstraintV1>.Fail(KernelError.CapabilityNotFound, "Resource-use grant does not exist.");
            var live = ValidateRecord(record, subject, subjectGeneration, CapabilityRights.None, expectedResourceGeneration);
            if (!live.IsSuccess)
                return KernelResult<ResourceUseConstraintV1>.Fail(live.Error, live.Message!);
            if (record.Constraints.ResourceUse is not { } grant)
                return KernelResult<ResourceUseConstraintV1>.Fail(KernelError.InsufficientRights, "Capability has no resource-use grant.");
            var now = _timeProvider.GetUtcNow().UtcTicks;
            if (now < grant.NotBeforeUtcTicks || now >= grant.ExpiresUtcTicks)
                return KernelResult<ResourceUseConstraintV1>.Fail(KernelError.DeadlineExpired, "Resource-use grant is outside its validity interval.");
            ResourceUseConstraintV1 requested;
            try
            {
                requested = grant with { Envelope = requestedEnvelope.Canonicalize() };
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
            {
                return KernelResult<ResourceUseConstraintV1>.Fail(KernelError.InvalidMessage, exception.Message);
            }
            return ResourceUseConstraintV1.IsSubset(requested, grant)
                ? KernelResult<ResourceUseConstraintV1>.Ok(grant)
                : KernelResult<ResourceUseConstraintV1>.Fail(KernelError.InsufficientRights, "Requested resource envelope exceeds the live grant.");
        }
    }

    internal KernelResult<ResourceUseAuthorityLease> AcquireResourceUseAuthority(
        CapabilityId id, DomainId subject, ulong subjectGeneration,
        ulong expectedResourceGeneration, ResourceEnvelopeV1 requestedEnvelope)
    {
        lock (_gate)
        {
            var validation = ValidateResourceUse(id, subject, subjectGeneration,
                expectedResourceGeneration, requestedEnvelope);
            if (!validation.IsSuccess)
                return KernelResult<ResourceUseAuthorityLease>.Fail(validation.Error, validation.Message!);
            if (_nextOperationLeaseId == 0)
                return KernelResult<ResourceUseAuthorityLease>.Fail(KernelError.CapacityExhausted,
                    "Resource-use authority lease identity space is exhausted.");
            var leaseId = new OperationAuthorityLeaseId(_nextOperationLeaseId++);
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
            if (!_records.TryGetValue(reference.CapabilityId, out var record))
                return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.CapabilityNotFound, "Capability does not exist in this authority realm.");
            if (record.Nonce != reference.Nonce)
                return KernelResult<CapabilityDescriptorV1>.Fail(KernelError.ForgedCapability, "Capability opaque identity is invalid.");
            return ValidateAndProject(record, subject, generation, requiredRights, expectedResourceGeneration);
        }
    }

    internal KernelResult<CapabilityAuthorityReference> GetReference(CapabilityId id)
    {
        lock (_gate)
            return _records.TryGetValue(id, out var record)
                ? KernelResult<CapabilityAuthorityReference>.Ok(new(RealmId, id, record.Nonce))
                : KernelResult<CapabilityAuthorityReference>.Fail(KernelError.CapabilityNotFound, "Capability does not exist.");
    }

    internal KernelResult<CapabilityHandleV2> GetHandleV2(
        CapabilityId id, DomainId subject, ulong subjectGeneration)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record))
                return KernelResult<CapabilityHandleV2>.Fail(KernelError.CapabilityNotFound, "Capability does not exist.");
            var validation = ValidateRecord(record, subject, subjectGeneration, CapabilityRights.None, null);
            return validation.IsSuccess
                ? KernelResult<CapabilityHandleV2>.Ok(record.ProjectV2(RealmId).Handle)
                : KernelResult<CapabilityHandleV2>.Fail(validation.Error, validation.Message!);
        }
    }

    internal KernelResult<CapabilityId> ResolveCapabilityId(CapabilityHandleV2 handle)
    {
        lock (_gate)
        {
            var resolved = ResolveV2(handle);
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
            var resolved = ResolveV2(handle);
            if (!resolved.IsSuccess)
                return KernelResult<CapabilityInspectionDescriptorV2>.Fail(resolved.Error, resolved.Message!);
            var validation = ValidateRecord(resolved.Value!, subject, subjectGeneration, requiredRights, expectedResourceGeneration);
            return validation.IsSuccess
                ? KernelResult<CapabilityInspectionDescriptorV2>.Ok(resolved.Value!.ProjectV2(RealmId))
                : KernelResult<CapabilityInspectionDescriptorV2>.Fail(validation.Error, validation.Message!);
        }
    }

    internal KernelResult<CapabilityHandleV2> Delegate(
        CapabilityHandleV2 source, DomainId delegatorDomain, DomainId targetDomain,
        CapabilityRights rights, ulong targetGeneration)
    {
        lock (_gate)
        {
            var resolved = ResolveV2(source);
            if (!resolved.IsSuccess)
                return KernelResult<CapabilityHandleV2>.Fail(resolved.Error, resolved.Message!);
            var delegated = Delegate(resolved.Value!.Id, delegatorDomain, targetDomain, rights, targetGeneration);
            if (!delegated.IsSuccess)
                return KernelResult<CapabilityHandleV2>.Fail(delegated.Error, delegated.Message!);
            var child = _records[delegated.Value!.CapabilityId];
            return KernelResult<CapabilityHandleV2>.Ok(child.ProjectV2(RealmId).Handle);
        }
    }

    internal KernelResult Revoke(CapabilityHandleV2 handle)
    {
        lock (_gate)
        {
            var resolved = ResolveV2(handle);
            return resolved.IsSuccess ? Revoke(resolved.Value!.Id) : KernelResult.Fail(resolved.Error, resolved.Message!);
        }
    }

    public KernelResult Revoke(CapabilityId id)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record))
                return KernelResult.Fail(KernelError.CapabilityNotFound, $"Capability {id} does not exist.");
            TransitionOutOfActive(record, CapabilityRecordState.Revoked);
            return KernelResult.Ok();
        }
    }

    internal KernelResult Retire(CapabilityId id)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record))
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
            var epoch = CurrentEpoch(domainId);
            if (epoch == ulong.MaxValue)
                throw new InvalidOperationException("Capability revocation epoch is exhausted; the domain must remain denied.");
            _domainEpochs[domainId] = epoch + 1;
            foreach (var record in _records.Values.Where(record => record.SubjectDomainId == domainId))
                TransitionOutOfActive(record, CapabilityRecordState.Revoked);
        }
    }

    public IReadOnlyList<CapabilityDescriptorV1> SnapshotForDomain(DomainId domainId)
    {
        lock (_gate)
            return _records.Values
                .Where(record => record.State == CapabilityRecordState.Active &&
                                 record.SubjectDomainId == domainId &&
                                 record.RevocationEpoch == CurrentEpoch(domainId) &&
                                 ValidateLineage(record).IsSuccess)
                .Select(static record => record.ProjectV1())
                .OrderBy(static descriptor => descriptor.CapabilityId.Value)
                .ToArray();
    }

    internal CapabilityAuthorityInspectionRecord[] InspectionSnapshot()
    {
        lock (_gate)
            return _records.Values
                .Select(record => new CapabilityAuthorityInspectionRecord(record.ProjectV1(), _realmFingerprint,
                    record.ResourceGeneration, record.DelegatedFrom, record.State))
                .OrderBy(static record => record.Descriptor.CapabilityId.Value)
                .ToArray();
    }

    internal KernelResult ConsumeQuota(CapabilityId id, DomainId subject, ulong generation, ulong amount)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record))
                return KernelResult.Fail(KernelError.CapabilityNotFound, "Capability does not exist.");
            var validation = ValidateRecord(record, subject, generation, CapabilityRights.None, null);
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
            var resolved = ResolveV2(handle);
            return resolved.IsSuccess
                ? ConsumeQuota(resolved.Value!.Id, subject, generation, amount)
                : KernelResult.Fail(resolved.Error, resolved.Message!);
        }
    }

    internal KernelResult<OperationAuthorityLease> AcquireOperationAuthority(
        CapabilityId id, DomainId subject, ulong subjectGeneration,
        ResourceKind resourceKind, string resourceId, ulong resourceGeneration,
        CapabilityOperation operation, EndpointSessionHandle? session = null,
        ulong quotaAmount = 0, bool oneShot = false)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record))
                return KernelResult<OperationAuthorityLease>.Fail(KernelError.CapabilityNotFound, "Capability does not exist.");
            var validation = ValidateRecord(record, subject, subjectGeneration, CapabilityRights.None, resourceGeneration);
            if (!validation.IsSuccess) return KernelResult<OperationAuthorityLease>.Fail(validation.Error, validation.Message!);
            if (record.ResourceKind != resourceKind || !string.Equals(record.ResourceId, resourceId, StringComparison.Ordinal))
                return KernelResult<OperationAuthorityLease>.Fail(KernelError.WrongCapabilityResource, "Capability does not authorize the exact resource.");
            if (!Enum.IsDefined(operation) || !record.Constraints.Operations.Operations.Contains(operation))
                return KernelResult<OperationAuthorityLease>.Fail(KernelError.InsufficientRights, "Capability does not authorize the exact operation.");
            var now = _timeProvider.GetUtcNow().UtcTicks;
            if (now < record.Constraints.Lifetime.NotBeforeUtcTicks || now >= record.Constraints.Lifetime.ExpiresUtcTicks)
                return KernelResult<OperationAuthorityLease>.Fail(KernelError.DeadlineExpired, "Capability lifetime does not admit this operation.");
            if (record.Constraints.Session.Session is { } constrained && constrained != session)
                return KernelResult<OperationAuthorityLease>.Fail(KernelError.WrongSessionOwner, "Capability is bound to another endpoint session.");
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
            var leaseId = new OperationAuthorityLeaseId(_nextOperationLeaseId++);
            _operationLeases.Add(leaseId);
            var lease = new OperationAuthorityLease(this, leaseId, id, subject, subjectGeneration,
                record.Constraints.Resource, operation, session, RevocationPolicyFor(operation));
            return KernelResult<OperationAuthorityLease>.Ok(lease);
        }
    }

    internal KernelResult<OperationAuthorityLease> AcquireOperationAuthority(
        CapabilityHandleV2 handle, DomainId subject, ulong subjectGeneration,
        ResourceKind resourceKind, string resourceId, ulong resourceGeneration,
        CapabilityOperation operation, EndpointSessionHandle? session = null,
        ulong quotaAmount = 0, bool oneShot = false)
    {
        lock (_gate)
        {
            var resolved = ResolveV2(handle);
            return resolved.IsSuccess
                ? AcquireOperationAuthority(resolved.Value!.Id, subject, subjectGeneration,
                    resourceKind, resourceId, resourceGeneration, operation, session, quotaAmount, oneShot)
                : KernelResult<OperationAuthorityLease>.Fail(resolved.Error, resolved.Message!);
        }
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
            return _records.TryGetValue(id, out var record)
                ? (record.Constraints, record.QuotaAccount.Remaining, record.QuotaConsumed)
                : null;
    }

    private KernelResult EnsureCapacity(SubjectIdentity subject)
    {
        if (_records.Count >= _limits.GlobalRecordCapacity)
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
        if (handle.OpaqueToken == Guid.Empty || !_opaqueIndex.TryGetValue(handle.OpaqueToken, out var id) ||
            !_records.TryGetValue(id, out var record) || record.Nonce != handle.OpaqueToken)
            return KernelResult<CapabilityRecord>.Fail(KernelError.ForgedCapability, "Capability opaque identity is invalid.");
        return KernelResult<CapabilityRecord>.Ok(record);
    }

    private KernelResult<(CapabilityId Id, Guid Nonce)> AllocateIdentity()
    {
        if (_identityExhausted)
            return KernelResult<(CapabilityId, Guid)>.Fail(KernelError.CapacityExhausted, "Capability identity space is exhausted.");
        var id = new CapabilityId(_nextId);
        if (_records.ContainsKey(id))
            return KernelResult<(CapabilityId, Guid)>.Fail(KernelError.CapacityExhausted, "Capability identity collision detected.");
        Guid nonce = default;
        var allocated = false;
        for (var attempt = 0; attempt < NonceCollisionRetryLimit; attempt++)
        {
            nonce = _nonceFactory();
            if (nonce != Guid.Empty && _records.Values.All(record => record.Nonce != nonce))
            {
                allocated = true;
                break;
            }
        }
        if (!allocated)
            return KernelResult<(CapabilityId, Guid)>.Fail(KernelError.CapacityExhausted, "Opaque identity allocation failed closed after bounded collision retries.");
        if (_nextId == ulong.MaxValue) _identityExhausted = true;
        else _nextId++;
        return KernelResult<(CapabilityId, Guid)>.Ok((id, nonce));
    }

    private KernelResult<CapabilityDescriptorV1> ValidateAndProject(
        CapabilityRecord record, DomainId subject, ulong generation,
        CapabilityRights requiredRights, ulong? expectedResourceGeneration)
    {
        var result = ValidateRecord(record, subject, generation, requiredRights, expectedResourceGeneration);
        return result.IsSuccess
            ? KernelResult<CapabilityDescriptorV1>.Ok(record.ProjectV1())
            : KernelResult<CapabilityDescriptorV1>.Fail(result.Error, result.Message!);
    }

    private KernelResult ValidateRecord(
        CapabilityRecord record, DomainId subject, ulong generation,
        CapabilityRights requiredRights, ulong? expectedResourceGeneration)
    {
        if (record.SubjectDomainId != subject)
            return KernelResult.Fail(KernelError.WrongCapabilitySubject, "Capability subject does not match the caller domain.");
        if (record.SubjectGeneration != generation)
            return KernelResult.Fail(KernelError.StaleGeneration, "Capability subject generation is stale.");
        if (expectedResourceGeneration is { } expected && record.ResourceGeneration != expected)
            return KernelResult.Fail(KernelError.StaleGeneration, "Capability resource generation is stale.");
        if (record.State != CapabilityRecordState.Active || record.RevocationEpoch != CurrentEpoch(subject))
            return KernelResult.Fail(KernelError.CapabilityRevoked, "Capability is not active.");
        var lineage = ValidateLineage(record);
        if (!lineage.IsSuccess) return lineage;
        if ((record.Rights & requiredRights) != requiredRights)
            return KernelResult.Fail(KernelError.InsufficientRights, "Capability does not provide the required rights.");
        return KernelResult.Ok();
    }

    private void IncrementLive(CapabilityRecord record)
    {
        var subject = new SubjectIdentity(record.SubjectDomainId, record.SubjectGeneration);
        _subjectLiveCounts[subject] = _subjectLiveCounts.GetValueOrDefault(subject) + 1;
    }

    private void TransitionOutOfActive(CapabilityRecord record, CapabilityRecordState target)
    {
        if (record.State != CapabilityRecordState.Active) return;
        record.State = target;
        var subject = new SubjectIdentity(record.SubjectDomainId, record.SubjectGeneration);
        var remaining = _subjectLiveCounts.GetValueOrDefault(subject) - 1;
        if (remaining <= 0) _subjectLiveCounts.Remove(subject);
        else _subjectLiveCounts[subject] = remaining;
    }

    private ulong CurrentEpoch(DomainId domainId) => _domainEpochs.TryGetValue(domainId, out var epoch) ? epoch : 0;

    private KernelResult ValidateLineage(CapabilityRecord record)
    {
        var remaining = 17;
        var current = record;
        while (current.DelegatedFrom is { } parentId)
        {
            if (--remaining == 0)
                return KernelResult.Fail(KernelError.DelegationDenied, "Capability lineage exceeds its deterministic bound.");
            if (!_records.TryGetValue(parentId, out var parent) || parent.State != CapabilityRecordState.Active ||
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
