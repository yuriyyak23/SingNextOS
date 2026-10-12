using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Platform.HybridCpu;
public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformOriginalMmioProvider, IPlatformMmioRevocationNoEffectProvider
{
    private sealed class MmioMapFailure
    {
        public MmioMapFailure(PlatformProviderMmioLeaseId id,DeviceLeaseRecord device,DomainRecord parent,PlatformMmioRegionIdentity region,PlatformMmioRange range,PlatformMmioAccess access,PlatformOriginalMmioMapRequest? original)
        { OwnerSelf = this; InvocationId = id; Device = device.Lease; DeviceSource = device; Parent = parent; DeviceFactory = device.AdmissionSource; ParentFactory = parent.BindSource; NeutralDevice = device.HybridCpuLease; Region = region; Range = range; Access = access; OriginalRequest = original; }
        public MmioMapFailure OwnerSelf { get; }
        public PlatformProviderMmioLeaseId InvocationId { get; }
        public PlatformProviderDeviceLease Device { get; }
        public DeviceLeaseRecord DeviceSource { get; }
        public DomainRecord Parent { get; }
        public DeviceBindFailure? DeviceFactory { get; }
        public RootBindRecord? ParentFactory { get; }
        public NeutralDeviceLease NeutralDevice { get; }
        public PlatformMmioRegionIdentity Region { get; }
        public PlatformMmioRange Range { get; }
        public PlatformMmioAccess Access { get; }
        public PlatformOriginalMmioMapRequest? OriginalRequest { get; }
        public bool Invoked { get; set; }
        public bool NoEffectConsumed { get; set; }
        public NeutralOriginalMmioMapRequest? NativeRequest { get; set; }
        public NeutralOriginalMmioMapObservation? NativeObservation { get; set; }
        public NeutralMmioMapResult? Observation { get; set; }
        public MmioLeaseRecord? OriginalLease { get; set; }
    }
    private sealed class MmioCloseInvocation
    {
        public MmioCloseInvocation(MmioLeaseRecord source,NeutralOriginalMmioCloseRequest request) { OwnerSelf = this; Source = source; Request = request; }
        public MmioCloseInvocation OwnerSelf { get; }
        public MmioLeaseRecord Source { get; }
        public NeutralOriginalMmioCloseRequest Request { get; }
        public bool Invoked { get; set; }
        public bool NoEffectConsumed { get; set; }
        public NeutralOriginalMmioCloseObservation? Observation { get; set; }
    }
    private sealed class MmioLeaseRecord
    {
        public MmioLeaseRecord(PlatformProviderMmioLease lease,NeutralMmioLease native,MmioMapFailure source)
        { OwnerSelf = this; Lease = lease; HybridCpuLease = native; MapSource = source; }
        public MmioLeaseRecord OwnerSelf { get; }
        public PlatformProviderMmioLease Lease { get; }
        public NeutralMmioLease HybridCpuLease { get; }
        public MmioMapFailure MapSource { get; }
        public bool CleanupOnly { get; init; }
        public bool Revoked { get; set; }
        public bool CloseInFlight { get; set; }
        public bool CloseUncertain { get; set; }
        public bool LegacyCloseInvoked { get; set; }
        public NeutralMmioCloseResult? CloseObservation { get; set; }
        public MmioCloseInvocation? OriginalClose { get; set; }
        public List<MmioCloseInvocation> CloseHistory { get; } = [];
    }
    private readonly Dictionary<PlatformProviderMmioLeaseId,MmioLeaseRecord> _mmioLeases = [];
    private readonly Dictionary<PlatformProviderMmioLeaseId,MmioLeaseRecord> _originalMmioLeases = [];
    private ulong _nextProviderMmioLeaseId = 1;
    public PlatformAuthorityResult<PlatformProviderMmioLease> MapMmio(PlatformProviderDeviceLease device,PlatformMmioRegionIdentity region,PlatformMmioRange range,PlatformMmioAccess access) => MapMmioCore(device,region,range,access,null);
    public PlatformAuthorityResult<PlatformProviderMmioLease> MapMmio(PlatformOriginalMmioMapRequest request) => MapMmioCore(request.Device,request.Region,request.Range,request.Access,request);
    private PlatformAuthorityResult<PlatformProviderMmioLease> MapMmioCore(PlatformProviderDeviceLease lease,PlatformMmioRegionIdentity region,PlatformMmioRange range,PlatformMmioAccess access,PlatformOriginalMmioMapRequest? request)
    {
        var valid = PlatformMmioLeaseContract.ValidateRequest(region,range,access);
        if (!valid.IsSuccess) return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(valid.Status,valid.Message!);
        DeviceLeaseRecord device; MmioMapFailure fact;
        lock (_childCreateCommitGate)
        {
            valid = ValidateProviderDeviceLease(lease); if (!valid.IsSuccess) return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(valid.Status,valid.Message!);
            device = _deviceLeases[lease.LeaseId]; var parent = _domains[lease.DomainLease.LeaseId];
            if ((ulong)device.OriginalMmioAdmissions.Count != device.OriginalMmioIssued || device.OriginalMmioAdmissions.Any(old => !ReferenceEquals(old.OwnerSelf,old))) return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(PlatformAuthorityStatus.Faulted,"Original MMIO history lost/copied; no repeat.");
            if (device.MmioMapInFlight || device.CloseInFlight || device.CloseUncertain || request is not null && device.OriginalMmioAdmissions.Any(old => ReferenceEquals(old.OriginalRequest,request))) return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(PlatformAuthorityStatus.Faulted,"Original MMIO pending/already invoked; pure lookup only.");
            if (device.FailedMmioMap is { } previous)
            {
                if (request is not null || previous.Device != lease || previous.Region != region || previous.Range != range || previous.Access != access || previous.Observation is not { IsMapped: true }) return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(PlatformAuthorityStatus.Faulted,"Original MMIO outcome unknown; no repeat.");
                fact = previous;
            }
            else
            {
                if (_nextProviderMmioLeaseId is 0 or ulong.MaxValue || device.OriginalMmioIssued == ulong.MaxValue) return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(PlatformAuthorityStatus.Faulted,"MMIO capacity exhausted before effect.");
                fact = new(new(_nextProviderMmioLeaseId++),device,parent,region,range,access,request);
                device.OriginalMmioAdmissions.Add(fact); device.OriginalMmioIssued++; device.FailedMmioMap = fact; // Same original intent before effect.
            }
            _originalMmioLeases.EnsureCapacity(_originalMmioLeases.Count+1); _mmioLeases.EnsureCapacity(_mmioLeases.Count+1); device.MmioMapInFlight = true;
        }
        try
        {
            NeutralMmioMapResult external;
            if (_runtime is INeutralOriginalMmioProvider original && fact.DeviceFactory?.NativeRequest is { } bind && _originalNativeMappingIncarnation is { } incarnation)
            {
                fact.NativeRequest ??= new(Guid.NewGuid(),incarnation,bind.InvocationId,fact.NeutralDevice,new(region.ResourceId,region.ByteLength),new(range.Offset,range.Length),ToNeutralMmioAccess(access));
                if (fact.Invoked && fact.NativeObservation is null) return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(PlatformAuthorityStatus.Faulted,"Original MMIO reply unknown; pure lookup only.");
                fact.Invoked = true; var raw = fact.NativeObservation ?? original.MapMmio(fact.NativeRequest.Value);
                lock (_childCreateCommitGate) { fact.NativeObservation = raw; fact.Observation = raw.Observation; }
                if (raw.Request != fact.NativeRequest || raw.Observation is null || !Enum.IsDefined(raw.Observation.Decision) || !raw.SourceFenceMatched && !raw.NoEffectGuaranteed || raw.NoEffectGuaranteed && (raw.Observation.IsMapped || raw.Observation.Lease != default)) return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(PlatformAuthorityStatus.Faulted,"Original MMIO raw missing/foreign/malformed.");
                if (raw.NoEffectGuaranteed) { lock (_childCreateCommitGate) { fact.NoEffectConsumed = true; if (ReferenceEquals(device.FailedMmioMap,fact)) device.FailedMmioMap = null; } return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(PlatformAuthorityStatus.NotAccepted,"Exact original MMIO Map no-effect."); }
                external = raw.Observation;
                var fresh = ValidateOriginalNativeMappingSource(); if (!fresh.IsSuccess) return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(fresh.Status,fresh.Message!);
                if (!original.ValidateMmioAdmission(fact.NativeRequest.Value)) return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(PlatformAuthorityStatus.Faulted,"Original native MMIO admission source changed before publication.");
            }
            else
            {
                if (fact.Invoked && fact.Observation is null) return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(PlatformAuthorityStatus.Faulted,"Original legacy MMIO outcome unknown; no repeat.");
                fact.Invoked = true; external = fact.Observation ?? _runtime.MapMmio(fact.NeutralDevice,new(region.ResourceId,region.ByteLength),new(range.Offset,range.Length),ToNeutralMmioAccess(access));
                lock (_childCreateCommitGate)
                {
                    fact.Observation = external;
                    if (external is { IsMapped: false } && external.Lease == default && external.Decision is NeutralMmioMapDecision.InvalidRegion or NeutralMmioMapDecision.InvalidRange or NeutralMmioMapDecision.InvalidAccess or NeutralMmioMapDecision.InsufficientDeviceRights or NeutralMmioMapDecision.AlreadyMapped)
                    { fact.NoEffectConsumed = true; if (ReferenceEquals(device.FailedMmioMap,fact)) device.FailedMmioMap = null; return FromNeutralMmioMapFailure(external); }
                }
            }
            lock (_childCreateCommitGate)
            {
                if (!ExactMmioMap(fact,external) || !CurrentMmioSources(fact)) return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(PlatformAuthorityStatus.Faulted,"Original MMIO outcome/source retained without publication.");
                var result = AdoptMmio(fact,external.Lease,false); device.FailedMmioMap = null; return PlatformAuthorityResult<PlatformProviderMmioLease>.Ok(result.Lease);
            }
        }
        finally { lock (_childCreateCommitGate) device.MmioMapInFlight = false; }
    }
    public PlatformAuthorityResult<PlatformOriginalMmioMapResolution> ResolveMmioMap(PlatformOriginalMmioMapRequest request)
    {
        MmioMapFailure fact;
        lock (_childCreateCommitGate)
        {
            if (!_originalDeviceLeases.TryGetValue(request.Device.LeaseId,out var device) || device.MmioMapInFlight) return PlatformAuthorityResult<PlatformOriginalMmioMapResolution>.Fail(PlatformAuthorityStatus.Faulted,"Original MMIO parent pending/absent.");
            var facts = device.OriginalMmioAdmissions.Where(old => ReferenceEquals(old.OriginalRequest,request)).ToArray(); if (facts.Length != 1) return PlatformAuthorityResult<PlatformOriginalMmioMapResolution>.Fail(PlatformAuthorityStatus.Faulted,"Original MMIO request absent/foreign."); fact = facts[0];
        }
        var fresh = ValidateOriginalNativeMappingSource(); if (!fresh.IsSuccess) return PlatformAuthorityResult<PlatformOriginalMmioMapResolution>.Fail(fresh.Status,fresh.Message!);
        if (fact.NativeObservation is null && fact.NativeRequest is { } native && _runtime is INeutralOriginalMmioProvider original)
        { var raw = original.ResolveMmioMap(native); lock (_childCreateCommitGate) { fact.NativeObservation = raw; if (raw is { } value) fact.Observation = value.Observation; } }
        fresh = ValidateOriginalNativeMappingSource(); if (!fresh.IsSuccess) return PlatformAuthorityResult<PlatformOriginalMmioMapResolution>.Fail(fresh.Status,fresh.Message!);
        if (fact.NativeObservation is { NoEffectGuaranteed: false } && fact.NativeRequest is { } admittedRequest && _runtime is INeutralOriginalMmioProvider producing && producing.ResolveMmioMap(admittedRequest) != fact.NativeObservation)
            return PlatformAuthorityResult<PlatformOriginalMmioMapResolution>.Fail(PlatformAuthorityStatus.Faulted,"Original native MMIO producing owner is not current for cleanup adoption.");
        lock (_childCreateCommitGate)
        {
            if (!CurrentMmioSources(fact) || !ReferenceEquals(fact.OriginalRequest,request) || fact.NativeRequest is not { } expectedNative || fact.NativeObservation is not { } raw || raw.Request != expectedNative || raw.Observation is null || !Enum.IsDefined(raw.Observation.Decision) || !raw.SourceFenceMatched && !raw.NoEffectGuaranteed) return PlatformAuthorityResult<PlatformOriginalMmioMapResolution>.Fail(PlatformAuthorityStatus.Faulted,"Original MMIO lookup unknown/source changed.");
            if (raw.NoEffectGuaranteed && !raw.Observation.IsMapped && raw.Observation.Lease == default && fact.OriginalLease is null)
            { fact.NoEffectConsumed = true; if (ReferenceEquals(fact.DeviceSource.FailedMmioMap,fact)) fact.DeviceSource.FailedMmioMap = null; return PlatformAuthorityResult<PlatformOriginalMmioMapResolution>.Ok(new(request,true,null)); }
            if (raw.NoEffectGuaranteed || !ExactMmioMap(fact,raw.Observation)) return PlatformAuthorityResult<PlatformOriginalMmioMapResolution>.Fail(PlatformAuthorityStatus.Faulted,"Original MMIO lookup malformed.");
            var owner = AdoptMmio(fact,raw.Observation.Lease,true);
            if (!_mmioLeases.TryGetValue(owner.Lease.LeaseId,out var route) || !ReferenceEquals(route,owner) || !_originalMmioLeases.TryGetValue(owner.Lease.LeaseId,out var originalOwner) || !ReferenceEquals(originalOwner,owner) || !ReferenceEquals(owner.OwnerSelf,owner)) return PlatformAuthorityResult<PlatformOriginalMmioMapResolution>.Fail(PlatformAuthorityStatus.Faulted,"Original MMIO route foreign.");
            if (ReferenceEquals(fact.DeviceSource.FailedMmioMap,fact)) fact.DeviceSource.FailedMmioMap = null;
            return PlatformAuthorityResult<PlatformOriginalMmioMapResolution>.Ok(new(request,false,owner.Lease));
        }
    }
    private MmioLeaseRecord AdoptMmio(MmioMapFailure fact,NeutralMmioLease native,bool cleanup)
    {
        if (fact.OriginalLease is { } known) return known;
        var lease = new PlatformProviderMmioLease(fact.InvocationId,new(1),fact.Device,fact.Region,fact.Range,fact.Access);
        var owner = new MmioLeaseRecord(lease,native,fact) { CleanupOnly = cleanup }; fact.OriginalLease = owner;
        _originalMmioLeases.Add(lease.LeaseId,owner); _mmioLeases.Add(lease.LeaseId,owner); return owner;
    }
    private bool CurrentMmioSources(MmioMapFailure fact) => ReferenceEquals(fact.OwnerSelf,fact) && ReferenceEquals(fact.DeviceSource.OwnerSelf,fact.DeviceSource) &&
        _deviceLeases.TryGetValue(fact.Device.LeaseId,out var device) && ReferenceEquals(device,fact.DeviceSource) && _originalDeviceLeases.TryGetValue(fact.Device.LeaseId,out var original) && ReferenceEquals(original,device) &&
        device.Lease == fact.Device && device.HybridCpuLease == fact.NeutralDevice && ReferenceEquals(device.AdmissionSource,fact.DeviceFactory) && device.OriginalMmioAdmissions.Any(old => ReferenceEquals(old,fact)) &&
        _domains.TryGetValue(fact.Device.DomainLease.LeaseId,out var parent) && ReferenceEquals(parent,fact.Parent) && ReferenceEquals(parent.BindSource,fact.ParentFactory) &&
        ValidateProviderDeviceLease(fact.Device).IsSuccess && !device.CloseInFlight && !device.CloseUncertain && device.CloseObservation is null;
    private static bool ExactMmioMap(MmioMapFailure fact,NeutralMmioMapResult? raw) => raw is { IsMapped: true,Decision: NeutralMmioMapDecision.Mapped } && raw.Lease.Handle.Value != 0 && raw.Lease.Epoch.Value != 0 &&
        raw.Lease.DeviceLease == fact.NeutralDevice && raw.Lease.Region == new NeutralMmioRegionIdentity(fact.Region.ResourceId,fact.Region.ByteLength) && raw.Lease.Range == new NeutralMmioRange(fact.Range.Offset,fact.Range.Length) && raw.Lease.Access == ToNeutralMmioAccess(fact.Access);
    public PlatformAuthorityResult RevokeMmio(PlatformProviderMmioLease lease) => RevokeMmioCore(lease,false);
    public PlatformAuthorityResult ResolveMmioRevocation(PlatformProviderMmioLease lease) => RevokeMmioCore(lease,true);
    private PlatformAuthorityResult RevokeMmioCore(PlatformProviderMmioLease lease,bool lookup)
    {
        MmioLeaseRecord record;
        lock (_childCreateCommitGate)
        {
            if (!_mmioLeases.TryGetValue(lease.LeaseId,out record!) || !_originalMmioLeases.TryGetValue(lease.LeaseId,out var originalOwner) || !ReferenceEquals(originalOwner,record) || !ReferenceEquals(record.OwnerSelf,record) || record.Lease != lease) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original MMIO owner absent/foreign.");
            if (record.CloseInFlight || lookup && record.OriginalClose is not { Invoked: true }) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original MMIO Close pending/not invoked.");
            if (record.Revoked) return lookup && HasConsumedNativeMmio(record) ? PlatformAuthorityResult.Ok() : PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Revoked,"Original MMIO revoked.");
            if (!CurrentMmioSources(record.MapSource)) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original MMIO source changed.");
            record.CloseInFlight = true;
        }
        try
        {
            NeutralMmioCloseResult result;
            if (_runtime is INeutralOriginalMmioProvider original && record.MapSource.NativeRequest is { } map && _originalNativeMappingIncarnation is { } incarnation)
            {
                MmioCloseInvocation fact; bool invoke;
                lock (_childCreateCommitGate)
                {
                    fact = record.OriginalClose ?? new(record,new(Guid.NewGuid(),incarnation,map.InvocationId,record.HybridCpuLease));
                    if (record.OriginalClose is null) { record.OriginalClose = fact; record.CloseHistory.Add(fact); }
                    if (!ReferenceEquals(fact.OwnerSelf,fact) || !ReferenceEquals(fact.Source,record) || !record.CloseHistory.Any(old => ReferenceEquals(old,fact))) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original MMIO Close factory foreign.");
                    invoke = !fact.Invoked; if (lookup && invoke) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Pure MMIO lookup cannot initiate effect."); if (invoke) fact.Invoked = true;
                }
                var raw = fact.Observation ?? (invoke ? original.CloseMmio(fact.Request) : original.ResolveMmioClose(fact.Request));
                lock (_childCreateCommitGate)
                {
                    fact.Observation = raw;
                    if (raw is not { } value || value.Request != fact.Request || value.Observation is null || !Enum.IsDefined(value.Observation.Decision) || !value.SourceFenceMatched && !value.NoEffectGuaranteed || value.NoEffectGuaranteed && value.Observation.Decision == NeutralMmioCloseDecision.Closed) { record.CloseUncertain = true; return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original MMIO Close raw unknown/foreign."); }
                    if (value.NoEffectGuaranteed)
                    { if (lookup) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Exact MMIO no-effect is not closure."); fact.NoEffectConsumed = true; record.OriginalClose = null; record.CloseUncertain = false; return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted,"Exact MMIO Close no-effect."); }
                    result = value.Observation; record.CloseObservation = result;
                }
                var fresh = ValidateOriginalNativeMappingSource(); if (!fresh.IsSuccess) return fresh;
                if (original.ResolveMmioClose(fact.Request) != fact.Observation) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original native MMIO Closed factory is not current for consumption.");
            }
            else
            {
                if (record.LegacyCloseInvoked && record.CloseObservation is null) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original legacy MMIO Close unknown; no repeat.");
                if (lookup) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original MMIO lookup unsupported.");
                record.LegacyCloseInvoked = true; result = record.CloseObservation ?? _runtime.CloseMmio(record.HybridCpuLease); lock (_childCreateCommitGate) record.CloseObservation = result;
            }
            lock (_childCreateCommitGate)
            {
                if (result is not { Decision: NeutralMmioCloseDecision.Closed } || !CurrentMmioSources(record.MapSource) || !_mmioLeases.TryGetValue(lease.LeaseId,out var route) || !ReferenceEquals(route,record) || !_originalMmioLeases.TryGetValue(lease.LeaseId,out var originalOwner) || !ReferenceEquals(originalOwner,record)) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original MMIO Closed retained without final source commit.");
                record.Revoked = true; record.CloseUncertain = false; return PlatformAuthorityResult.Ok();
            }
        }
        catch { lock (_childCreateCommitGate) record.CloseUncertain = true; throw; }
        finally { lock (_childCreateCommitGate) record.CloseInFlight = false; }
    }
    private static bool HasConsumedNativeMmio(MmioLeaseRecord record) => ReferenceEquals(record.OwnerSelf,record) && ReferenceEquals(record.MapSource.OwnerSelf,record.MapSource) && ReferenceEquals(record.MapSource.OriginalLease,record) && ExactMmioMap(record.MapSource,record.MapSource.Observation) &&
        (record.OriginalClose is { } fact && ReferenceEquals(fact.OwnerSelf,fact) && ReferenceEquals(fact.Source,record) && record.CloseHistory.Any(old => ReferenceEquals(old,fact)) &&
         fact.Observation is { SourceFenceMatched: true,NoEffectGuaranteed: false } raw && raw.Request == fact.Request && raw.Observation.Decision == NeutralMmioCloseDecision.Closed && fact.Request.Mapping == record.HybridCpuLease && fact.Request.MapInvocationId == record.MapSource.NativeRequest?.InvocationId ||
         record.MapSource.NativeRequest is null && record.Revoked && record.CloseObservation is { Decision: NeutralMmioCloseDecision.Closed });
    private static bool HasConsumedProviderMmioMap(MmioMapFailure fact) => ReferenceEquals(fact.OwnerSelf,fact) &&
        (fact.NoEffectConsumed && fact.OriginalLease is null &&
            (fact.NativeRequest is { } request && fact.NativeObservation is { NoEffectGuaranteed: true } raw && raw.Request == request && !raw.Observation.IsMapped && raw.Observation.Lease == default ||
             fact.NativeRequest is null && fact.Observation is { IsMapped: false } known && known.Lease == default && known.Decision is NeutralMmioMapDecision.InvalidRegion or NeutralMmioMapDecision.InvalidRange or NeutralMmioMapDecision.InvalidAccess or NeutralMmioMapDecision.InsufficientDeviceRights or NeutralMmioMapDecision.AlreadyMapped) ||
         fact.OriginalLease is { } record && ReferenceEquals(record.MapSource,fact) && HasConsumedNativeMmio(record));
    internal bool HasActiveProviderMmioLeases(PlatformProviderDeviceLease deviceLease) =>
        _originalMmioLeases.Values.Any(record => record.Lease.DeviceLease == deviceLease && !HasConsumedNativeMmio(record)) ||
        _originalDeviceLeases.TryGetValue(deviceLease.LeaseId,out var device) && (device.MmioMapInFlight || (ulong)device.OriginalMmioAdmissions.Count != device.OriginalMmioIssued || device.OriginalMmioAdmissions.Any(fact => !HasConsumedProviderMmioMap(fact)));

    private PlatformAuthorityResult ValidateProviderDeviceLease(PlatformProviderDeviceLease lease)
    {
        if (!_deviceLeases.TryGetValue(lease.LeaseId, out var record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider device lease does not exist.");
        }

        if (!_originalDeviceLeases.TryGetValue(lease.LeaseId, out var original) || !ReferenceEquals(original, record))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original device owner is not current before admission.");

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

        if (!ReferenceEquals(record.OwnerSelf,record) || record.CleanupOnly)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied,"Original cleanup device cannot admit use.");

        if (record.Revoked)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The provider device lease has already been revoked.");
        }

        var domainValidation = ValidateDomain(lease.DomainLease);
        if (!domainValidation.IsSuccess) return domainValidation;
        if (!ReferenceEquals(record.BindParent, _domains[lease.DomainLease.LeaseId]))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original device bind parent changed before admission.");
        return PlatformAuthorityResult.Ok();
    }

    private static NeutralMmioAccess ToNeutralMmioAccess(PlatformMmioAccess access)
    {
        var result = NeutralMmioAccess.None;
        if ((access & PlatformMmioAccess.Read) != 0)
            result |= NeutralMmioAccess.Read;
        if ((access & PlatformMmioAccess.Write) != 0)
            result |= NeutralMmioAccess.Write;
        return result;
    }

    private static PlatformAuthorityResult<PlatformProviderMmioLease> FromNeutralMmioMapFailure(
        NeutralMmioMapResult result)
    {
        var status = result.Decision switch
        {
            NeutralMmioMapDecision.InvalidRegion => PlatformAuthorityStatus.Denied,
            NeutralMmioMapDecision.InvalidRange => PlatformAuthorityStatus.Denied,
            NeutralMmioMapDecision.InvalidAccess => PlatformAuthorityStatus.Denied,
            NeutralMmioMapDecision.InsufficientDeviceRights => PlatformAuthorityStatus.Denied,
            NeutralMmioMapDecision.AlreadyMapped => PlatformAuthorityStatus.Denied,
            NeutralMmioMapDecision.NotFound => PlatformAuthorityStatus.Faulted,
            NeutralMmioMapDecision.Stale => PlatformAuthorityStatus.Faulted,
            NeutralMmioMapDecision.Revoked => PlatformAuthorityStatus.Revoked,
            NeutralMmioMapDecision.Faulted => PlatformAuthorityStatus.Faulted,
            _ => PlatformAuthorityStatus.Faulted,
        };

        return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(
            status,
            result.Reason);
    }
}
