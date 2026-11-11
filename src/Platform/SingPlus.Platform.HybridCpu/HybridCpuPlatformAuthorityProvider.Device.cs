using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformDeviceLeaseProvider, IPlatformOriginalDeviceProvider, IPlatformDeviceAdmissionNoEffectProvider, IPlatformDeviceRevocationNoEffectProvider
{
    private sealed class DeviceBindFailure
    {
        public DeviceBindFailure(PlatformProviderDeviceLeaseId id,PlatformProviderDomainLease domain,NeutralDomainBindingLease nativeDomain,PlatformDeviceIdentity device,PlatformDeviceRights rights,DomainRecord parent,PlatformOriginalDeviceBindRequest? original)
        { OwnerSelf = this; InvocationId = id; Domain = domain; NeutralDomain = nativeDomain; Device = device; Rights = rights; Parent = parent; ParentFactory = parent.BindSource; OriginalRequest = original; }
        public DeviceBindFailure OwnerSelf { get; }
        public PlatformProviderDeviceLeaseId InvocationId { get; }
        public PlatformProviderDomainLease Domain { get; }
        public NeutralDomainBindingLease NeutralDomain { get; }
        public PlatformDeviceIdentity Device { get; }
        public PlatformDeviceRights Rights { get; }
        public DomainRecord Parent { get; }
        public RootBindRecord? ParentFactory { get; }
        public PlatformOriginalDeviceBindRequest? OriginalRequest { get; }
        public bool Invoked { get; set; }
        public bool NoEffectConsumed { get; set; }
        public NeutralDeviceBindResult? Observation { get; set; }
        public NeutralOriginalDeviceBindRequest? NativeRequest { get; set; }
        public NeutralOriginalDeviceBindObservation? NativeObservation { get; set; }
        public DeviceLeaseRecord? OriginalLease { get; set; }
    }
    private sealed class DeviceLeaseRecord
    {
        public DeviceLeaseRecord(PlatformProviderDeviceLease lease,NeutralDeviceLease hybridCpuLease, bool originalAdmissionRequired = false)
        { OwnerSelf = this; Lease = lease; HybridCpuLease = hybridCpuLease; OriginalAdmissionRequired = originalAdmissionRequired; }
        public bool OriginalAdmissionRequired { get; }
        public DeviceLeaseRecord OwnerSelf { get; }
        public PlatformProviderDeviceLease Lease { get; }
        public NeutralDeviceLease HybridCpuLease { get; }
        public DomainRecord? BindParent { get; init; }
        public DeviceBindFailure? AdmissionSource { get; init; }
        public bool CleanupOnly { get; init; }
        public bool Revoked { get; set; }
        public ulong IoAdmissionsIssued { get; set; }
        public ulong IoAdmissionsConsumed { get; set; }
        public bool IrqBindInFlight { get; set; }
        public IrqBindFailure? FailedIrqBind { get; set; }
        public bool MmioMapInFlight { get; set; }
        public List<MmioMapFailure> OriginalMmioAdmissions { get; } = [];
        public ulong OriginalMmioIssued { get; set; }
        public MmioMapFailure? FailedMmioMap { get; set; }
        public bool DmaBindInFlight { get; set; }
        public DmaBindFailure? FailedDmaBind { get; set; }
        public List<DmaBindFailure> OriginalDmaAdmissions { get; } = [];
        public bool CloseInFlight { get; set; }
        public bool CloseUncertain { get; set; }
        public NeutralDeviceCloseResult? CloseObservation { get; set; }
        public NeutralOriginalDeviceLifetimeClosure? LifetimeRaw, ValidatedLifetime;
        public bool LifetimeLookupInvoked;
        public DomainRecord? CloseParent { get; set; }
        public DeviceNativeCloseInvocation? NativeClose { get; set; }
        public List<DeviceNativeCloseInvocation> CloseHistory { get; } = [];
    }
    private sealed class DeviceNativeCloseInvocation
    {
        public DeviceNativeCloseInvocation(DeviceLeaseRecord source,NeutralOriginalDeviceCloseRequest request)
        { OwnerSelf = this; Source = source; Request = request; }
        public DeviceNativeCloseInvocation OwnerSelf { get; }
        public DeviceLeaseRecord Source { get; }
        public NeutralOriginalDeviceCloseRequest Request { get; }
        public bool Invoked { get; set; }
        public bool NoEffectConsumed { get; set; }
        public NeutralOriginalDeviceCloseObservation? Observation { get; set; }
    }

    private readonly Dictionary<PlatformProviderDeviceLeaseId, DeviceLeaseRecord> _deviceLeases = [];
    private readonly Dictionary<PlatformProviderDeviceLeaseId, DeviceLeaseRecord> _originalDeviceLeases = [];
    private ulong _nextProviderDeviceLeaseId = 1;

    public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease domainLease,PlatformDeviceIdentity device,PlatformDeviceRights rights) => BindOriginalDeviceCore(domainLease,device,rights,null);
    public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformOriginalDeviceBindRequest original) => BindOriginalDeviceCore(original.Domain,original.Device,original.Rights,original);
    private PlatformAuthorityResult<PlatformProviderDeviceLease> BindOriginalDeviceCore(PlatformProviderDomainLease domainLease,PlatformDeviceIdentity device,PlatformDeviceRights rights,PlatformOriginalDeviceBindRequest? request)
    {
        PlatformAuthorityResult validation = PlatformDeviceLeaseContract.ValidateRequest(device,rights);
        if (!validation.IsSuccess) return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(validation.Status,validation.Message!);
        DomainRecord parent; DeviceBindFailure fact;
        lock (_childCreateCommitGate)
        {
            PlatformAuthorityResult domain = ValidateDomain(domainLease);
            if (!domain.IsSuccess) return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(domain.Status,domain.Message!);
            parent = _domains[domainLease.LeaseId];
            if (!HasOriginalDeviceAdmissionHistory(parent) || parent.DeviceBindInFlight || request is not null && parent.OriginalDeviceAdmissions.Any(old => ReferenceEquals(old.OriginalRequest,request)))
                return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(PlatformAuthorityStatus.Faulted,"Original device Bind pending/already invoked; lookup only.");
            if (parent.FailedDeviceBind is { } prior)
            {
                if (request is not null || prior.Domain != domainLease || prior.Device != device || prior.Rights != rights || prior.Observation is not { IsBound: true })
                    return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(PlatformAuthorityStatus.Faulted,"Original device Bind outcome pending; no repeat.");
                fact = prior;
            }
            else
            {
                if (_nextProviderDeviceLeaseId is 0 or ulong.MaxValue || parent.OriginalDevicesIssued == ulong.MaxValue) return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(PlatformAuthorityStatus.Faulted,"Device capacity exhausted before effect.");
                parent.OriginalDeviceAdmissions.EnsureCapacity(parent.OriginalDeviceAdmissions.Count + 1);
                fact = new(new(_nextProviderDeviceLeaseId++),domainLease,parent.HybridCpuLease,device,rights,parent,request);
                parent.OriginalDeviceAdmissions.Add(fact); parent.OriginalDevicesIssued++; parent.FailedDeviceBind = fact; // Intent/count before effect.
            }
            _originalDeviceLeases.EnsureCapacity(_originalDeviceLeases.Count+1); _deviceLeases.EnsureCapacity(_deviceLeases.Count+1);
            parent.DeviceBindInFlight = true;
        }
        try
        {
            NeutralDeviceBindResult external;
            if (_runtime is INeutralOriginalDeviceProvider original && _originalNativeMappingIncarnation is { } incarnation && parent.BindSource?.NativeRequest is { } root)
            {
                lock (_childCreateCommitGate) fact.NativeRequest ??= new(request?.InvocationId ?? Guid.NewGuid(),incarnation,root.InvocationId,parent.HybridCpuLease,new(device.ResourceId),ToNeutralDeviceRights(rights));
                NeutralOriginalDeviceBindRequest nativeRequest = fact.NativeRequest.Value;
                lock (_childCreateCommitGate) { if (fact.Invoked && fact.NativeObservation is null) return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(PlatformAuthorityStatus.Faulted,"Original device Bind reply unknown; no repeat."); fact.Invoked = true; }
                NeutralOriginalDeviceBindObservation raw = fact.NativeObservation ?? original.BindDevice(nativeRequest);
                lock (_childCreateCommitGate) { fact.NativeObservation = raw; fact.Observation = raw.Observation; }
                if (raw.Request != nativeRequest || raw.Observation is null || !Enum.IsDefined(raw.Observation.Decision) || !raw.SourceFenceMatched && !raw.NoEffectGuaranteed || raw.NoEffectGuaranteed && (raw.Observation.IsBound || raw.Observation.Lease != default || raw.Observation.Decision==NeutralDeviceBindDecision.Bound))
                    return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(PlatformAuthorityStatus.Faulted,"Original device Bind raw foreign/malformed; pinned.");
                if (raw.NoEffectGuaranteed)
                {
                    lock (_childCreateCommitGate) { fact.NoEffectConsumed = true; if (ReferenceEquals(parent.FailedDeviceBind,fact)) parent.FailedDeviceBind = null; }
                    return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(PlatformAuthorityStatus.NotAccepted,"Exact original device Bind no-effect.");
                }
                external = raw.Observation;
                PlatformAuthorityResult fresh = ValidateOriginalNativeMappingSource();
                if (!fresh.IsSuccess) return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(fresh.Status,fresh.Message!);
            }
            else
            {
                lock (_childCreateCommitGate) { if (fact.Invoked && fact.Observation is null) return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(PlatformAuthorityStatus.Faulted,"Legacy original device Bind unknown; no repeat."); fact.Invoked = true; }
                external = fact.Observation ?? _runtime.BindDevice(parent.HybridCpuLease,new(device.ResourceId),ToNeutralDeviceRights(rights));
                lock (_childCreateCommitGate) fact.Observation = external;
            }
            lock (_childCreateCommitGate)
            {
                if (external is not { IsBound: true, Decision: NeutralDeviceBindDecision.Bound } || external.Lease.Handle.Value == 0 || external.Lease.Epoch.Value == 0 ||
                    external.Lease.DomainLease != parent.HybridCpuLease || external.Lease.Device != new NeutralDeviceIdentity(device.ResourceId) || external.Lease.Rights != ToNeutralDeviceRights(rights))
                {
                    if (external is not null && !external.IsBound && external.Lease == default && external.Decision is NeutralDeviceBindDecision.InvalidDevice or NeutralDeviceBindDecision.InvalidRights or NeutralDeviceBindDecision.AlreadyBound)
                    { fact.NoEffectConsumed = true; parent.FailedDeviceBind = null; return FromNeutralDeviceBindFailure(external); }
                    return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(PlatformAuthorityStatus.Faulted,"Original device outcome unknown/malformed; no returned-object compensation.");
                }
                if (!_domains.TryGetValue(domainLease.LeaseId,out DomainRecord? current) || !ReferenceEquals(current,parent) || !ReferenceEquals(parent.BindSource,fact.ParentFactory) || !ValidateDomain(domainLease).IsSuccess)
                    return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(PlatformAuthorityStatus.Faulted,"Original device source lost before publication.");
                PlatformProviderDeviceLease lease = new PlatformProviderDeviceLease(fact.InvocationId,new(1),domainLease,device,rights);
                DeviceLeaseRecord record = new DeviceLeaseRecord(lease,external.Lease, fact.NativeRequest is not null) { BindParent = parent, AdmissionSource = fact };
                fact.OriginalLease = record; _originalDeviceLeases.Add(lease.LeaseId,record); _deviceLeases.Add(lease.LeaseId,record);
                if (ReferenceEquals(parent.FailedDeviceBind,fact)) parent.FailedDeviceBind = null;
                return PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(lease);
            }
        }
        finally { lock (_childCreateCommitGate) parent.DeviceBindInFlight = false; }
    }

    public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease) => RevokeOriginalDeviceCore(lease,false);
    public PlatformAuthorityResult ResolveDeviceRevocation(PlatformProviderDeviceLease lease) => RevokeOriginalDeviceCore(lease,true);
    private PlatformAuthorityResult RevokeOriginalDeviceCore(PlatformProviderDeviceLease lease,bool resolutionOnly)
    {
        DeviceLeaseRecord record;
        lock (_childCreateCommitGate)
        {
        if (!_deviceLeases.TryGetValue(lease.LeaseId, out record!))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider device lease does not exist.");
        }

        if (!_originalDeviceLeases.TryGetValue(lease.LeaseId, out DeviceLeaseRecord? original) || !ReferenceEquals(original, record))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original device owner is not current before close.");

        if (record.Lease.Generation != lease.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider device lease generation is stale.");
        }

        if (record.Lease != lease)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The provider device lease identity is malformed.");
        }

        if (!ReferenceEquals(record.OwnerSelf,record) || !_originalDeviceLeases.TryGetValue(lease.LeaseId,out DeviceLeaseRecord? owner) || !ReferenceEquals(owner,record))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original device owner was replaced.");
        if (resolutionOnly && !(record.Revoked && HasConsumedOriginalProviderDevice(record)) &&
            !(record.LifetimeLookupInvoked && _runtime is INeutralOriginalDeviceLifetimeProvider) &&
            (record.NativeClose is not { Invoked: true } || _runtime is not INeutralOriginalDeviceProvider))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original device Close request unavailable; lookup cannot effect.");
        if (record.Revoked)
        {
            if (resolutionOnly && HasConsumedOriginalProviderDevice(record)) return PlatformAuthorityResult.Ok();
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The provider device lease has already been revoked.");
        }

        if (HasProviderIoPins(record.Lease))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Original virtual-I/O lifetime remains active or unknown.");
        if (HasActiveProviderDmaGrants(record.Lease))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "HybridCPU DMA grants must close before the provider device lease.");
        }

        if (HasActiveProviderInterruptBindings(record.Lease))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "HybridCPU interrupt bindings must close before the provider device lease.");
        }

        if (HasActiveProviderMmioLeases(record.Lease))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "HybridCPU MMIO leases must close before the provider device lease.");
        }

        if (record.CloseInFlight || record.CloseUncertain && record.NativeClose is null)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original device close is pending or unknown.");
            PlatformAuthorityResult domainValidation = ValidateDomain(lease.DomainLease);
        if (!domainValidation.IsSuccess) return domainValidation;
            DomainRecord parent = _domains[lease.DomainLease.LeaseId];
        if (!ReferenceEquals(record.BindParent, parent))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original device bind parent is not current before close.");
        if (record.CloseParent is not null && !ReferenceEquals(record.CloseParent, parent))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original device close parent owner has changed.");
        record.CloseParent ??= parent;
        record.CloseInFlight = true;
        }
        try
        {
        NeutralDeviceCloseResult external;
        try
        {
            bool lifetimeClosed = false;
            if (_runtime is INeutralOriginalDeviceLifetimeProvider lifetime && record.AdmissionSource?.NativeObservation is { } admitted)
            {
                lock (_childCreateCommitGate) record.LifetimeLookupInvoked = true; // Retained pure query intent before callback, never permission to Close.
                NeutralOriginalDeviceLifetimeClosure? raw = record.LifetimeRaw ?? lifetime.ResolveOriginalDeviceLifetime(admitted.Observation);
                lock (_childCreateCommitGate) record.LifetimeRaw ??= raw;
                if (raw is not null)
                {
                    bool canonical = raw.NoFurtherUse && ReferenceEquals(raw.Admission, admitted.Observation) &&
                        ReferenceEquals(raw, lifetime.ResolveOriginalDeviceLifetime(admitted.Observation));
                    PlatformAuthorityResult fresh = ValidateOriginalNativeMappingSource();
                    lock (_childCreateCommitGate)
                    {
                        if (!fresh.IsSuccess || !canonical || !ReferenceEquals(record.LifetimeRaw, raw) ||
                            !ReferenceEquals(record.AdmissionSource.NativeObservation?.Observation, admitted.Observation) ||
                            record.ValidatedLifetime is { } previous && !ReferenceEquals(previous, raw))
                            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original device lifetime custody/source unavailable.");
                        record.ValidatedLifetime = raw; lifetimeClosed = true;
                    }
                }
            }
            if (lifetimeClosed) external = new() { Decision = NeutralDeviceCloseDecision.Closed };
            else if (resolutionOnly && record.NativeClose is not { Invoked: true })
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original lifetime lookup has no canonical closure; pure resolution cannot Close.");
            else if (_runtime is INeutralOriginalDeviceProvider original && record.AdmissionSource?.NativeRequest is { } bind && _originalNativeMappingIncarnation is { } incarnation)
            {
                DeviceNativeCloseInvocation fact; bool invoke;
                lock (_childCreateCommitGate)
                {
                    fact = record.NativeClose ?? new(record,new(Guid.NewGuid(),incarnation,bind.InvocationId,record.HybridCpuLease));
                    if (record.NativeClose is null) { record.CloseHistory.Add(fact); record.NativeClose = fact; }
                    if (!ReferenceEquals(fact.OwnerSelf,fact) || !ReferenceEquals(fact.Source,record) || !record.CloseHistory.Any(old => ReferenceEquals(old,fact)))
                        return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original device Close factory was copied.");
                    invoke = !fact.Invoked;
                    if (resolutionOnly && invoke) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original device lookup cannot initiate Close.");
                    if (invoke) fact.Invoked = true;
                }
                    NeutralOriginalDeviceCloseObservation? raw = fact.Observation ?? (invoke ? original.CloseDevice(fact.Request) : original.ResolveDeviceClose(fact.Request));
                lock (_childCreateCommitGate)
                {
                    fact.Observation = raw;
                    if (raw is not { } value || value.Request != fact.Request || value.Observation is null || !Enum.IsDefined(value.Observation.Decision) ||
                        !value.SourceFenceMatched && !value.NoEffectGuaranteed || value.NoEffectGuaranteed && value.Observation.Decision == NeutralDeviceCloseDecision.Closed)
                    { record.CloseUncertain = true; return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original device Close response unknown/foreign; no repeat."); }
                    if (value.NoEffectGuaranteed)
                    {
                        if (resolutionOnly) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original no-effect is not device closure.");
                        fact.NoEffectConsumed = true; record.NativeClose = null; record.CloseUncertain = false;
                        return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted,"Exact original device Close no-effect; device remains open.");
                    }
                    external = value.Observation;
                }
                    PlatformAuthorityResult fresh = ValidateOriginalNativeMappingSource(); if (!fresh.IsSuccess) return fresh;
            }
            else external = record.CloseObservation ?? _runtime.CloseDevice(record.HybridCpuLease);
        }
        catch { lock (_childCreateCommitGate) record.CloseUncertain = true; throw; }
        lock (_childCreateCommitGate)
        {
        if (external is null)
        {
            record.CloseUncertain = true;
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original device close observation absent.");
        }
        record.CloseObservation = external;
        if (!_deviceLeases.TryGetValue(lease.LeaseId, out DeviceLeaseRecord? current) || !ReferenceEquals(current, record) ||
            !_originalDeviceLeases.TryGetValue(lease.LeaseId, out DeviceLeaseRecord? original) || !ReferenceEquals(original, record) ||
            !ValidateDomain(lease.DomainLease).IsSuccess ||
            !_domains.TryGetValue(lease.DomainLease.LeaseId, out DomainRecord? currentParent) || !ReferenceEquals(currentParent, record.CloseParent) ||
            HasActiveProviderDmaGrants(lease) ||
            HasActiveProviderInterruptBindings(lease) || HasActiveProviderMmioLeases(lease))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original device close retained without final source publication.");
        switch (external.Decision)
        {
            case NeutralDeviceCloseDecision.Closed:
                record.Revoked = true;
                return PlatformAuthorityResult.Ok();

            case NeutralDeviceCloseDecision.Revoked:
                record.Revoked = true;
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Revoked,
                    external.Reason);

            case NeutralDeviceCloseDecision.ActiveDependents:
                record.CloseObservation = null;
                record.CloseParent = null;
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    external.Reason);

            case NeutralDeviceCloseDecision.Stale:
            case NeutralDeviceCloseDecision.NotFound:
            case NeutralDeviceCloseDecision.Faulted:
            default:
                record.CloseUncertain = true;
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Faulted,
                    external.Reason);
        }
        }
        }
        finally { lock (_childCreateCommitGate) record.CloseInFlight = false; }
    }

    internal bool HasActiveProviderDeviceLeases(PlatformProviderDomainLease domainLease) =>
        _originalDeviceLeases.Values.Any(record =>
            !HasConsumedOriginalProviderDevice(record) && record.Lease.DomainLease == domainLease) ||
        _originalRootBinds.TryGetValue(domainLease.LeaseId,out RootBindRecord? root) && root.OriginalDomain is { } parent &&
        (!HasOriginalDeviceAdmissionHistory(parent) || parent.OriginalDeviceAdmissions.Any(fact => !fact.NoEffectConsumed && (fact.OriginalLease is null || !HasConsumedOriginalProviderDevice(fact.OriginalLease))));

    private static NeutralDeviceRights ToNeutralDeviceRights(PlatformDeviceRights rights)
    {
        NeutralDeviceRights result = NeutralDeviceRights.None;
        if ((rights & PlatformDeviceRights.Read) != 0)
            result |= NeutralDeviceRights.Read;
        if ((rights & PlatformDeviceRights.Write) != 0)
            result |= NeutralDeviceRights.Write;
        if ((rights & PlatformDeviceRights.Configure) != 0)
            result |= NeutralDeviceRights.Configure;
        return result;
    }

    private static PlatformAuthorityResult<PlatformProviderDeviceLease> FromNeutralDeviceBindFailure(
        NeutralDeviceBindResult result)
    {
        PlatformAuthorityStatus status = result.Decision switch
        {
            NeutralDeviceBindDecision.InvalidDevice => PlatformAuthorityStatus.Denied,
            NeutralDeviceBindDecision.InvalidRights => PlatformAuthorityStatus.Denied,
            NeutralDeviceBindDecision.AlreadyBound => PlatformAuthorityStatus.Denied,
            NeutralDeviceBindDecision.NotFound => PlatformAuthorityStatus.Faulted,
            NeutralDeviceBindDecision.Stale => PlatformAuthorityStatus.Faulted,
            NeutralDeviceBindDecision.Revoked => PlatformAuthorityStatus.Revoked,
            NeutralDeviceBindDecision.Faulted => PlatformAuthorityStatus.Faulted,
            _ => PlatformAuthorityStatus.Faulted,
        };

        return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(
            status,
            result.Reason);
    }
}
