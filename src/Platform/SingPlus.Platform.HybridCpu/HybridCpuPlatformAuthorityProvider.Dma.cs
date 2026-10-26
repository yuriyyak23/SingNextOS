using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformDmaGrantProvider, IPlatformDmaGrantAdmissionNoEffectProvider, IPlatformDmaGrantRevocationNoEffectProvider, IPlatformDmaGrantRevocationResolutionProvider, IPlatformDmaGrantRevocationResolutionNoEffectProvider, IPlatformOriginalDmaGrantProvider
{
    private sealed class DmaBindFailure
    {
        public DmaBindFailure(PlatformProviderDmaGrantId invocationId, PlatformDmaGrantRequest request, DeviceLeaseRecord device, ProviderMappingRecord mapping, DomainRecord parent)
        { OwnerSelf = this; InvocationId = invocationId; Request = request; Device = device; Mapping = mapping; Parent = parent; ParentFactory = parent.BindSource; DeviceFactory = device.AdmissionSource; MappingFactory = mapping.MapAdmissionSource; }
        public DmaBindFailure OwnerSelf { get; }
        public PlatformProviderDmaGrantId InvocationId { get; }
        public PlatformDmaGrantRequest Request { get; }
        public DeviceLeaseRecord Device { get; }
        public ProviderMappingRecord Mapping { get; }
        public DomainRecord Parent { get; }
        public RootBindRecord? ParentFactory { get; }
        public DeviceBindFailure? DeviceFactory { get; }
        public OwnedMapFailure? MappingFactory { get; }
        public PlatformOriginalDmaGrantBindRequest? OriginalRequest { get; init; }
        public bool NativeInvoked { get; set; }
        public bool NoEffectConsumed { get; set; }
        public DmaGrantRecord? OriginalGrant { get; set; }
        public NeutralDmaGrantResult? Observation { get; set; }
        public NeutralOriginalDmaGrantRequest? NativeRequest { get; set; }
        public NeutralOriginalDmaGrantObservation? NativeObservation { get; set; }
    }
    private sealed record DmaSource(DeviceLeaseRecord Device, ProviderMappingRecord Mapping, DomainRecord Parent);
    private sealed class DmaGrantRecord
    {
        public DmaGrantRecord(PlatformProviderDmaGrant grant, NeutralDmaGrant hybridCpuGrant)
        { OwnerSelf = this; Grant = grant; HybridCpuGrant = hybridCpuGrant; }
        public DmaGrantRecord OwnerSelf { get; }
        public PlatformProviderDmaGrant Grant { get; }
        public NeutralDmaGrant HybridCpuGrant { get; }
        public DmaBindFailure? AdmissionSource { get; init; }
        public bool CleanupOnly { get; set; }
        public List<DmaNativeCloseInvocation> NativeCloseHistory { get; } = [];
        public DmaNativeCloseInvocation? NativeClose { get; set; }
        public DmaSource? BindSource { get; init; }
        public bool Revoked { get; set; }
        public bool VisibilityInFlight { get; set; }
        public bool VisibilityUnknown { get; set; }
        public DmaVisibilityInvocation? VisibilityInvocation { get; set; }
        public List<DmaVisibilityInvocation> VisibilityHistory { get; } = [];
        public ProviderDmaVisibilityState? VisibilityState { get; set; }
        public bool CloseInFlight { get; set; }
        public bool CloseUncertain { get; set; }
        public NeutralDmaGrantCloseResult? CloseObservation { get; set; }
        public DmaSource? CloseSource { get; set; }
        public ProviderCopyFact? CopyFact { get; set; }
        public List<ProviderCopyFact> CopyHistory { get; } = [];
        public ulong CopyHistoryIssued { get; set; }
    }

    private sealed class DmaNativeCloseInvocation
    {
        public DmaNativeCloseInvocation(DmaGrantRecord source, NeutralOriginalDmaGrantCloseRequest request)
        { OwnerSelf = this; Source = source; Request = request; }
        public DmaNativeCloseInvocation OwnerSelf { get; }
        public DmaGrantRecord Source { get; }
        public NeutralOriginalDmaGrantCloseRequest Request { get; }
        public bool Invoked { get; set; }
        public bool NoEffectConsumed { get; set; }
        public NeutralOriginalDmaGrantCloseObservation? RawObservation { get; set; }
    }

    private readonly Dictionary<PlatformProviderDmaGrantId, DmaGrantRecord> _dmaGrants = [];
    private readonly Dictionary<PlatformProviderDmaGrantId, DmaGrantRecord> _originalDmaGrants = [];
    private ulong _nextProviderDmaGrantId = 1;

    public PlatformAuthorityResult<PlatformProviderDmaGrant> BindDmaGrant(PlatformDmaGrantRequest request) => BindDmaGrantCore(request, null);
    public PlatformAuthorityResult<PlatformProviderDmaGrant> BindDmaGrant(PlatformOriginalDmaGrantBindRequest original) => BindDmaGrantCore(original.Request, original);
    private PlatformAuthorityResult<PlatformProviderDmaGrant> BindDmaGrantCore(PlatformDmaGrantRequest request, PlatformOriginalDmaGrantBindRequest? callerRequest)
    {
        PlatformAuthorityResult requestValidation = PlatformDmaGrantContract.ValidateRequest(request);
        if (!requestValidation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                requestValidation.Status,
                requestValidation.Message ?? "The DMA grant admission request is invalid.");
        }

        DeviceLeaseRecord device;
        ProviderMappingRecord mapping;
        DomainRecord parent;
        PlatformProviderDmaGrantId reserved;
        DmaBindFailure originalAdmission;
        lock (_childCreateCommitGate)
        {
            PlatformAuthorityResult dv = ValidateProviderDeviceLease(request.DeviceLease);
            PlatformAuthorityResult mv = ValidateProviderMapping(request.MappingLease, request.MappingSlice);
            if (!dv.IsSuccess) return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(dv.Status, dv.Message!);
            if (!mv.IsSuccess) return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(mv.Status, mv.Message!);
            device = _deviceLeases[request.DeviceLease.LeaseId];
            mapping = _providerMappings[request.MappingLease.MappingId];
            parent = _domains[request.DeviceLease.DomainLease.LeaseId];
            if (device.CloseInFlight || device.CloseUncertain || device.CloseObservation is not null ||
                device.DmaBindInFlight || mapping.DmaBindInFlight || device.FailedDmaBind is not null || mapping.FailedDmaBind is not null ||
                device.OriginalDmaAdmissions.Any(old => !old.NoEffectConsumed && (old.OriginalGrant is null || !old.OriginalGrant.Revoked)) ||
                callerRequest is not null && device.OriginalDmaAdmissions.Any(old => ReferenceEquals(old.OriginalRequest, callerRequest)) ||
                mapping.VisibilityInFlight || mapping.VisibilityUncertain)
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original DMA source is pending, retained or already granted; repeat admission refused.");
            if (_originalDmaGrants.Values.Any(r => !r.Revoked && r.Grant.MappingLease.MappingId == request.MappingLease.MappingId))
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(PlatformAuthorityStatus.Denied, "Mapping already has a live DMA grant.");
            if (_nextProviderDmaGrantId is 0 or ulong.MaxValue)
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(PlatformAuthorityStatus.Faulted,
                    "DMA grant identity capacity exhausted before neutral effect.");
            reserved = new(_nextProviderDmaGrantId++);
            _originalDmaGrants.EnsureCapacity(_originalDmaGrants.Count + 1);
            _dmaGrants.EnsureCapacity(_originalDmaGrants.Count + 1);
            originalAdmission = new(reserved, request, device, mapping, parent) { OriginalRequest = callerRequest };
            device.OriginalDmaAdmissions.Add(originalAdmission);
            device.FailedDmaBind = mapping.FailedDmaBind = originalAdmission; // Intent before effect.
            device.DmaBindInFlight = mapping.DmaBindInFlight = true;
        }
        NeutralDmaRange range = new NeutralDmaRange(request.Range.Offset, request.Range.Length);
        NeutralDmaDirection direction = ToNeutralDmaDirection(request.Direction);
        void Retain(NeutralDmaGrantResult? observation)
        {
            originalAdmission.Observation = observation;
            device.FailedDmaBind = mapping.FailedDmaBind = originalAdmission;
        }
        try
        {
            NeutralDmaGrantResult external;
            try
            {
                if (_runtime is INeutralOriginalDmaGrantProvider original && _originalNativeMappingIncarnation is { } incarnation &&
                    parent.BindSource?.NativeRequest is { } root && mapping.MapAdmissionSource?.NativeRequest is { } map)
                {
                    NeutralOriginalDmaGrantRequest nativeRequest = new NeutralOriginalDmaGrantRequest(callerRequest?.InvocationId ?? Guid.NewGuid(), incarnation, root.InvocationId, map.InvocationId,
                        device.HybridCpuLease, mapping.HybridCpuLease, range, direction);
                    lock (_childCreateCommitGate) originalAdmission.NativeRequest = nativeRequest;
                    lock (_childCreateCommitGate) originalAdmission.NativeInvoked = true;
                    NeutralOriginalDmaGrantObservation raw = original.BindDmaGrant(nativeRequest);
                    lock (_childCreateCommitGate) { originalAdmission.NativeObservation = raw; originalAdmission.Observation = raw.Observation; }
                    if (raw.Request != nativeRequest || raw.Observation is null || !Enum.IsDefined(raw.Observation.Decision) ||
                        raw.NoEffectGuaranteed && (raw.Observation.IsGranted || raw.Observation.Grant != default) ||
                        !raw.SourceFenceMatched && !raw.NoEffectGuaranteed)
                        return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(PlatformAuthorityStatus.Faulted, "Malformed original DMA Bind outcome remains pinned.");
                    if (raw.NoEffectGuaranteed)
                    {
                        PlatformAuthorityResult freshNoEffectSource = ValidateOriginalNativeMappingSource();
                        if (!freshNoEffectSource.IsSuccess) return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(freshNoEffectSource.Status,freshNoEffectSource.Message!);
                        lock (_childCreateCommitGate)
                        {
                            if (!IsCanonicalDmaAdmission(originalAdmission))
                                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(PlatformAuthorityStatus.Faulted,"Original no-effect source changed before consumption.");
                            originalAdmission.NoEffectConsumed = true;
                            if (ReferenceEquals(device.FailedDmaBind,originalAdmission)) device.FailedDmaBind = null;
                            if (ReferenceEquals(mapping.FailedDmaBind,originalAdmission)) mapping.FailedDmaBind = null;
                        }
                        return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(PlatformAuthorityStatus.NotAccepted, "Exact original DMA Bind no-effect.");
                    }
                    external = raw.Observation;
                }
                else
                {
                    lock (_childCreateCommitGate) originalAdmission.NativeInvoked = true;
                    external = _runtime.BindDmaGrant(device.HybridCpuLease, mapping.HybridCpuLease, range, direction);
                }
            }
            catch { lock (_childCreateCommitGate) Retain(originalAdmission.Observation); throw; }
            lock (_childCreateCommitGate) originalAdmission.Observation = external;
            if (originalAdmission.NativeRequest is not null)
            {
                PlatformAuthorityResult source = ValidateOriginalNativeMappingSource();
                if (!source.IsSuccess) return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(source.Status, source.Message!);
            }
            lock (_childCreateCommitGate)
            {
                if (external is null || !Enum.IsDefined(external.Decision) ||
                    !external.IsGranted || external.Decision != NeutralDmaGrantDecision.Granted ||
                    external.Grant.Handle.Value == 0 || external.Grant.Epoch.Value == 0 ||
                    external.Grant.DeviceLease != device.HybridCpuLease || external.Grant.MappingLease != mapping.HybridCpuLease ||
                    external.Grant.Range != range || external.Grant.Direction != direction)
                {
                    // Only exact known pre-mutation refusals with no returned grant are no-effect.
                    if (external is not null && !external.IsGranted && external.Grant == default &&
                        external.Decision is NeutralDmaGrantDecision.InvalidRange or NeutralDmaGrantDecision.InvalidDirection or
                            NeutralDmaGrantDecision.InsufficientDeviceRights or NeutralDmaGrantDecision.InsufficientMappingAccess or
                            NeutralDmaGrantDecision.WrongDomain or NeutralDmaGrantDecision.AlreadyGranted)
                    {
                        if (!IsCanonicalDmaAdmission(originalAdmission))
                            return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(PlatformAuthorityStatus.Faulted,"Legacy no-effect source changed before consumption.");
                        originalAdmission.NoEffectConsumed = true;
                        if (ReferenceEquals(device.FailedDmaBind,originalAdmission)) device.FailedDmaBind = null;
                        if (ReferenceEquals(mapping.FailedDmaBind,originalAdmission)) mapping.FailedDmaBind = null;
                        return FromNeutralDmaGrantFailure(external);
                    }
                    Retain(external);
                    return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(PlatformAuthorityStatus.Faulted,
                        "Original DMA invocation outcome retained without closure; returned-object compensation is not original closure.");
                }
                if (!_deviceLeases.TryGetValue(request.DeviceLease.LeaseId, out DeviceLeaseRecord? currentDevice) || !ReferenceEquals(currentDevice, device) ||
                    !_providerMappings.TryGetValue(request.MappingLease.MappingId, out ProviderMappingRecord? currentMapping) || !ReferenceEquals(currentMapping, mapping) ||
                    !_domains.TryGetValue(parent.Lease.LeaseId, out DomainRecord? currentParent) || !ReferenceEquals(currentParent, parent) ||
                    !ValidateProviderDeviceLease(request.DeviceLease).IsSuccess || !ValidateProviderMapping(request.MappingLease, request.MappingSlice).IsSuccess ||
                    device.CloseInFlight || device.CloseUncertain || device.CloseObservation is not null)
                {
                    Retain(external);
                    return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(PlatformAuthorityStatus.Faulted,
                        "Exact original DMA grant retained without current source publication.");
                }
                Retain(external);
                PlatformProviderDmaGrant grant = new PlatformProviderDmaGrant(reserved, new(1), request.DeviceLease, request.MappingLease,
                    request.Range, request.Direction);
                DmaGrantRecord originalRecord = new DmaGrantRecord(grant, external.Grant) { BindSource = new(device, mapping, parent), AdmissionSource = originalAdmission };
                originalAdmission.OriginalGrant = originalRecord;
                _originalDmaGrants.Add(reserved, originalRecord);
                _dmaGrants.Add(reserved, originalRecord);
                device.FailedDmaBind = mapping.FailedDmaBind = null;
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Ok(grant);
            }
        }
        finally { lock (_childCreateCommitGate) device.DmaBindInFlight = mapping.DmaBindInFlight = false; }
    }

    public PlatformAuthorityResult<PlatformOriginalDmaGrantBindResolution> ResolveDmaGrantBind(PlatformOriginalDmaGrantBindRequest request)
    {
        DmaBindFailure fact;
        lock (_childCreateCommitGate)
        {
            if (!_originalDeviceLeases.TryGetValue(request.Request.DeviceLease.LeaseId, out DeviceLeaseRecord? device))
                return PlatformAuthorityResult<PlatformOriginalDmaGrantBindResolution>.Fail(PlatformAuthorityStatus.Faulted, "Original DMA admission owner unavailable.");
            DmaBindFailure[] facts = device.OriginalDmaAdmissions.Where(old => ReferenceEquals(old.OriginalRequest, request)).ToArray();
            if (facts.Length != 1 || facts[0].Request != request.Request)
                return PlatformAuthorityResult<PlatformOriginalDmaGrantBindResolution>.Fail(PlatformAuthorityStatus.Faulted, "Original DMA request reference is missing/foreign.");
            fact = facts[0];
            if (!ReferenceEquals(fact.OwnerSelf, fact)) return PlatformAuthorityResult<PlatformOriginalDmaGrantBindResolution>.Fail(PlatformAuthorityStatus.Faulted, "Original DMA admission factory was copied.");
            if (device.DmaBindInFlight || fact.Mapping.DmaBindInFlight)
                return PlatformAuthorityResult<PlatformOriginalDmaGrantBindResolution>.Fail(PlatformAuthorityStatus.Faulted, "Original DMA admission remains in flight.");
        }
        PlatformAuthorityResult fresh = ValidateOriginalNativeMappingSource();
        if (!fresh.IsSuccess) return PlatformAuthorityResult<PlatformOriginalDmaGrantBindResolution>.Fail(fresh.Status, fresh.Message!);
        if (fact.NativeObservation is null && fact.NativeRequest is { } nativeRequest && _runtime is INeutralOriginalDmaGrantProvider original)
        {
            NeutralOriginalDmaGrantObservation? raw = original.ResolveDmaGrantBind(nativeRequest);
            lock (_childCreateCommitGate) { fact.NativeObservation = raw; if (raw is { } known) fact.Observation = known.Observation; }
        }
        PlatformAuthorityResult after = ValidateOriginalNativeMappingSource();
        if (!after.IsSuccess) return PlatformAuthorityResult<PlatformOriginalDmaGrantBindResolution>.Fail(after.Status, after.Message!);
        lock (_childCreateCommitGate)
        {
            if (!IsCanonicalDmaAdmission(fact) || !_deviceLeases.TryGetValue(request.Request.DeviceLease.LeaseId, out DeviceLeaseRecord? device) || !ReferenceEquals(device, fact.Device) ||
                !_originalDeviceLeases.TryGetValue(device.Lease.LeaseId, out DeviceLeaseRecord? canonical) || !ReferenceEquals(device, canonical) ||
                !device.OriginalDmaAdmissions.Any(old => ReferenceEquals(old, fact)) || !ReferenceEquals(fact.OriginalRequest, request) ||
                !_providerMappings.TryGetValue(request.Request.MappingLease.MappingId, out ProviderMappingRecord? mapping) || !ReferenceEquals(mapping, fact.Mapping) ||
                !_domains.TryGetValue(device.Lease.DomainLease.LeaseId, out DomainRecord? parent) || !ReferenceEquals(parent, fact.Parent) ||
                fact.NativeRequest is not { } expected || fact.NativeObservation is not { } raw || raw.Request != expected ||
                raw.Observation is null || !Enum.IsDefined(raw.Observation.Decision) || !raw.SourceFenceMatched && !raw.NoEffectGuaranteed)
                return PlatformAuthorityResult<PlatformOriginalDmaGrantBindResolution>.Fail(PlatformAuthorityStatus.Faulted, "Original DMA admission source/outcome unknown.");
            if (raw.NoEffectGuaranteed && !raw.Observation.IsGranted && raw.Observation.Grant == default && fact.OriginalGrant is null)
            {
                fact.NoEffectConsumed = true;
                if (ReferenceEquals(device.FailedDmaBind, fact)) device.FailedDmaBind = null;
                if (ReferenceEquals(mapping.FailedDmaBind, fact)) mapping.FailedDmaBind = null;
                return PlatformAuthorityResult<PlatformOriginalDmaGrantBindResolution>.Ok(new(request, true, null));
            }
            NeutralDmaGrant nativeGrant = raw.Observation.Grant;
            if (raw.NoEffectGuaranteed || !raw.Observation.IsGranted || raw.Observation.Decision != NeutralDmaGrantDecision.Granted ||
                nativeGrant.Handle.Value == 0 || nativeGrant.Epoch.Value == 0 || nativeGrant.DeviceLease != device.HybridCpuLease || nativeGrant.MappingLease != mapping.HybridCpuLease ||
                nativeGrant.Range != expected.Range || nativeGrant.Direction != expected.Direction)
                return PlatformAuthorityResult<PlatformOriginalDmaGrantBindResolution>.Fail(PlatformAuthorityStatus.Faulted, "Original DMA grant outcome malformed.");
            DmaGrantRecord? grant = fact.OriginalGrant;
            if (grant is null)
            {
                PlatformProviderDmaGrant lease = new PlatformProviderDmaGrant(fact.InvocationId, new(1), fact.Request.DeviceLease, fact.Request.MappingLease, fact.Request.Range, fact.Request.Direction);
                grant = new(lease, nativeGrant) { BindSource = new(device, mapping, parent), AdmissionSource = fact, CleanupOnly = true };
                fact.OriginalGrant = grant; _originalDmaGrants.Add(lease.GrantId, grant); _dmaGrants.Add(lease.GrantId, grant);
            }
            if (!ReferenceEquals(grant.OwnerSelf, grant) || !ReferenceEquals(grant.AdmissionSource, fact) ||
                !_originalDmaGrants.TryGetValue(grant.Grant.GrantId, out DmaGrantRecord? originalGrant) || !ReferenceEquals(originalGrant, grant) ||
                !_dmaGrants.TryGetValue(grant.Grant.GrantId, out DmaGrantRecord? routed) || !ReferenceEquals(routed, grant))
                return PlatformAuthorityResult<PlatformOriginalDmaGrantBindResolution>.Fail(PlatformAuthorityStatus.Faulted, "Original DMA cleanup grant route is foreign.");
            if (ReferenceEquals(device.FailedDmaBind, fact)) device.FailedDmaBind = null;
            if (ReferenceEquals(mapping.FailedDmaBind, fact)) mapping.FailedDmaBind = null;
            return PlatformAuthorityResult<PlatformOriginalDmaGrantBindResolution>.Ok(new(request, false, grant.Grant)); // Cleanup only, never DMA admission.
        }
    }

    private bool IsCanonicalDmaAdmission(DmaBindFailure fact) => ReferenceEquals(fact.OwnerSelf,fact) &&
        _deviceLeases.TryGetValue(fact.Request.DeviceLease.LeaseId,out DeviceLeaseRecord? device) && ReferenceEquals(device,fact.Device) &&
        _originalDeviceLeases.TryGetValue(device.Lease.LeaseId,out DeviceLeaseRecord? originalDevice) && ReferenceEquals(device,originalDevice) && ReferenceEquals(device.OwnerSelf,device) &&
        device.OriginalDmaAdmissions.Count(old => ReferenceEquals(old,fact)) == 1 &&
        _providerMappings.TryGetValue(fact.Request.MappingLease.MappingId,out ProviderMappingRecord? mapping) && ReferenceEquals(mapping,fact.Mapping) &&
        _originalProviderMappings.TryGetValue(mapping.Mapping.Lease.MappingId,out ProviderMappingRecord? originalMapping) && ReferenceEquals(mapping,originalMapping) &&
        _domains.TryGetValue(fact.Parent.Lease.LeaseId,out DomainRecord? parent) && ReferenceEquals(parent,fact.Parent) &&
        ReferenceEquals(parent.BindSource,fact.ParentFactory) && ReferenceEquals(device.AdmissionSource,fact.DeviceFactory) &&
        ReferenceEquals(mapping.MapAdmissionSource,fact.MappingFactory);

    // Pure lifetime correlation check under the existing local commit gate.
    private bool IsCurrentDmaBindSource(DmaGrantRecord record) => record.BindSource is { } source &&
        _deviceLeases.TryGetValue(record.Grant.DeviceLease.LeaseId, out DeviceLeaseRecord? device) && ReferenceEquals(device, source.Device) &&
        _providerMappings.TryGetValue(record.Grant.MappingLease.MappingId, out ProviderMappingRecord? mapping) && ReferenceEquals(mapping, source.Mapping) &&
        _domains.TryGetValue(record.Grant.DeviceLease.DomainLease.LeaseId, out DomainRecord? parent) && ReferenceEquals(parent, source.Parent);

    public PlatformAuthorityResult RevokeDmaGrant(PlatformProviderDmaGrant grant) => RevokeDmaGrantCore(grant, false);
    public PlatformAuthorityResult ResolveDmaGrantRevocation(PlatformProviderDmaGrant grant) => RevokeDmaGrantCore(grant, true);
    private PlatformAuthorityResult RevokeDmaGrantCore(PlatformProviderDmaGrant grant, bool resolutionOnly)
    {
        DmaGrantRecord record;
        DmaSource source;
        lock (_childCreateCommitGate)
        {
            if (!_dmaGrants.TryGetValue(grant.GrantId, out record!))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "The provider DMA grant does not exist.");
            if (!ReferenceEquals(record.OwnerSelf, record) || !_originalDmaGrants.TryGetValue(grant.GrantId, out DmaGrantRecord? original) || !ReferenceEquals(original, record))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original DMA owner is not current before close.");
            if (record.Grant.Generation != grant.Generation)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "The provider DMA grant generation is stale.");
            if (record.Grant != grant)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "The provider DMA grant identity is malformed.");
            if (resolutionOnly && (record.NativeClose is not { Invoked: true } || _runtime is not INeutralOriginalDmaGrantProvider))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original native DMA Close invocation is unavailable; lookup cannot effect.");
            if (record.Revoked)
                return resolutionOnly && record.NativeClose is { } completed && ReferenceEquals(completed.OwnerSelf, completed) &&
                    ReferenceEquals(completed.Source, record) && record.NativeCloseHistory.Any(old => ReferenceEquals(old, completed)) &&
                    completed.RawObservation is { SourceFenceMatched: true, NoEffectGuaranteed: false } raw && raw.Request == completed.Request &&
                    raw.Observation is { Decision: NeutralDmaGrantCloseDecision.Closed } && record.AdmissionSource?.NativeRequest is { } admitted &&
                    completed.Request.DmaBindInvocationId == admitted.InvocationId && completed.Request.Grant == record.HybridCpuGrant
                    ? PlatformAuthorityResult.Ok() : PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Revoked, "The provider DMA grant has already been revoked.");
            if (record.CloseInFlight || record.CloseUncertain && record.NativeClose is null || record.VisibilityInFlight)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original DMA close is pending or unknown; repeat refused.");
            if (record.CopyFact is { } copy && (copy.InFlight || copy.Raw is not { NoFurtherByteUseGuaranteed: true, Completed: true } && !copy.NoEffectConsumed && !copy.LifetimeConsumed))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original copy lifetime is in flight or unknown; grant Close cannot infer containment.");
            if (!ValidateProviderDeviceLease(grant.DeviceLease).IsSuccess ||
                !ValidateProviderMapping(grant.MappingLease, expectedSlice: null).IsSuccess)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original DMA sources are not current before close.");
            if (!IsCurrentDmaBindSource(record))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original DMA bind sources changed before close.");
            DeviceLeaseRecord device = _deviceLeases[grant.DeviceLease.LeaseId];
            ProviderMappingRecord mapping = _providerMappings[grant.MappingLease.MappingId];
            DomainRecord parent = _domains[grant.DeviceLease.DomainLease.LeaseId];
            source = record.CloseSource ?? new(device, mapping, parent);
            if (!ReferenceEquals(source.Device, device) || !ReferenceEquals(source.Mapping, mapping) ||
                !ReferenceEquals(source.Parent, parent) || device.CloseInFlight || device.CloseUncertain || device.CloseObservation is not null)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original DMA close source owner has changed or is closing.");
            record.CloseSource = source;
            record.CloseInFlight = true;
        }
        try
        {
            NeutralDmaGrantCloseResult external;
            try
            {
                if (_runtime is INeutralOriginalDmaGrantProvider original && record.AdmissionSource?.NativeRequest is { } bind &&
                    _originalNativeMappingIncarnation is { } incarnation)
                {
                    DmaNativeCloseInvocation fact; bool invoke;
                    lock (_childCreateCommitGate)
                    {
                        fact = record.NativeClose ?? new(record, new(Guid.NewGuid(), incarnation, bind.InvocationId, record.HybridCpuGrant));
                        if (record.NativeClose is null) { record.NativeCloseHistory.Add(fact); record.NativeClose = fact; }
                        if (!ReferenceEquals(fact.OwnerSelf, fact) || !ReferenceEquals(fact.Source, record) || !record.NativeCloseHistory.Any(old => ReferenceEquals(old, fact)))
                            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original DMA Close owner was substituted.");
                        invoke = !fact.Invoked;
                        if (resolutionOnly && invoke) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original DMA lookup cannot initiate Close.");
                        if (invoke) fact.Invoked = true;
                    }
                    NeutralOriginalDmaGrantCloseObservation? raw = fact.RawObservation ?? (invoke ? original.CloseDmaGrant(fact.Request) : original.ResolveDmaGrantClose(fact.Request));
                    lock (_childCreateCommitGate)
                    {
                        fact.RawObservation = raw; // Raw before source callbacks/publication.
                        if (raw is not { } value || value.Request != fact.Request || value.Observation is null ||
                            value.NoEffectGuaranteed && value.Observation.Decision != NeutralDmaGrantCloseDecision.Faulted ||
                            !value.SourceFenceMatched && !value.NoEffectGuaranteed)
                        { record.CloseUncertain = true; return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original DMA Close outcome unknown/malformed; no repeat."); }
                        external = value.Observation;
                    }
                    PlatformAuthorityResult fresh = ValidateOriginalNativeMappingSource();
                    if (!fresh.IsSuccess) { lock (_childCreateCommitGate) record.CloseUncertain = true; return fresh; }
                    if (raw is { NoEffectGuaranteed: true })
                    {
                        lock (_childCreateCommitGate)
                        {
                            if (!_dmaGrants.TryGetValue(grant.GrantId,out DmaGrantRecord? current) || !ReferenceEquals(current,record) ||
                                !_originalDmaGrants.TryGetValue(grant.GrantId,out DmaGrantRecord? canonical) || !ReferenceEquals(canonical,record) ||
                                !ReferenceEquals(record.OwnerSelf,record) || !IsCurrentDmaBindSource(record) ||
                                record.AdmissionSource is not { } admission || !IsCanonicalDmaAdmission(admission) ||
                                !ReferenceEquals(record.NativeClose,fact) || record.NativeCloseHistory.Count(old => ReferenceEquals(old,fact)) != 1)
                            { record.CloseUncertain = true; return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,"Original Close no-effect source changed before consumption."); }
                            fact.NoEffectConsumed = true; record.NativeClose = null; record.CloseUncertain = false;
                            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted,"Exact original DMA Close no-effect; grant remains open.");
                        }
                    }
                }
                else external = record.CloseObservation ?? _runtime.CloseDmaGrant(record.HybridCpuGrant);
            }
            catch { lock (_childCreateCommitGate) record.CloseUncertain = true; throw; }
            lock (_childCreateCommitGate)
            {
                record.CloseObservation = external;
                if (external is null || external.Decision != NeutralDmaGrantCloseDecision.Closed)
                {
                    record.CloseUncertain = true;
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                        "Original DMA close has no exact Closed outcome; grant and sources remain pinned.");
                }
                if (!_dmaGrants.TryGetValue(grant.GrantId, out DmaGrantRecord? current) || !ReferenceEquals(current, record) ||
                    !_originalDmaGrants.TryGetValue(grant.GrantId, out DmaGrantRecord? original) || !ReferenceEquals(original, record) ||
                    !_deviceLeases.TryGetValue(grant.DeviceLease.LeaseId, out DeviceLeaseRecord? device) || !ReferenceEquals(device, source.Device) ||
                    !_providerMappings.TryGetValue(grant.MappingLease.MappingId, out ProviderMappingRecord? mapping) || !ReferenceEquals(mapping, source.Mapping) ||
                    !_domains.TryGetValue(grant.DeviceLease.DomainLease.LeaseId, out DomainRecord? parent) || !ReferenceEquals(parent, source.Parent) ||
                    !ValidateProviderDeviceLease(grant.DeviceLease).IsSuccess || !ValidateProviderMapping(grant.MappingLease, expectedSlice: null).IsSuccess ||
                    source.Device.CloseInFlight || source.Device.CloseUncertain || source.Device.CloseObservation is not null ||
                    record.HybridCpuGrant.DeviceLease != source.Device.HybridCpuLease ||
                    record.HybridCpuGrant.MappingLease != source.Mapping.HybridCpuLease)
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                        "Exact original DMA close retained without current owner/source publication.");
                record.Revoked = true;
                return PlatformAuthorityResult.Ok();
            }
        }
        finally { lock (_childCreateCommitGate) record.CloseInFlight = false; }
    }

    internal bool HasActiveProviderDmaGrants(PlatformProviderDeviceLease lease) =>
        (_originalDeviceLeases.TryGetValue(lease.LeaseId, out DeviceLeaseRecord? owner) &&
            (owner.DmaBindInFlight || owner.OriginalDmaAdmissions.Any(old => !old.NoEffectConsumed && (old.OriginalGrant is null || !old.OriginalGrant.Revoked)))) ||
        _originalDmaGrants.Values.Any(r => !r.Revoked && r.Grant.DeviceLease == lease);

    internal bool HasActiveProviderDmaGrants(PlatformProviderRegionMappingLease lease) =>
        (_originalProviderMappings.TryGetValue(lease.MappingId, out ProviderMappingRecord? owner) &&
            (owner.DmaBindInFlight || _originalDeviceLeases.Values.Any(device => device.OriginalDmaAdmissions.Any(old => ReferenceEquals(old.Mapping, owner) && !old.NoEffectConsumed && (old.OriginalGrant is null || !old.OriginalGrant.Revoked))))) ||
        _originalDmaGrants.Values.Any(r => !r.Revoked && r.Grant.MappingLease == lease);

    private static NeutralDmaDirection ToNeutralDmaDirection(PlatformDmaDirection direction) =>
        direction switch
        {
            PlatformDmaDirection.DeviceReadsMemory => NeutralDmaDirection.DeviceReadsMemory,
            PlatformDmaDirection.DeviceWritesMemory => NeutralDmaDirection.DeviceWritesMemory,
            PlatformDmaDirection.Bidirectional => NeutralDmaDirection.Bidirectional,
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };

    private static PlatformAuthorityResult<PlatformProviderDmaGrant> FromNeutralDmaGrantFailure(
        NeutralDmaGrantResult result)
    {
        PlatformAuthorityStatus status = result.Decision switch
        {
            NeutralDmaGrantDecision.InvalidRange => PlatformAuthorityStatus.Denied,
            NeutralDmaGrantDecision.InvalidDirection => PlatformAuthorityStatus.Denied,
            NeutralDmaGrantDecision.InsufficientDeviceRights => PlatformAuthorityStatus.Denied,
            NeutralDmaGrantDecision.InsufficientMappingAccess => PlatformAuthorityStatus.Denied,
            NeutralDmaGrantDecision.WrongDomain => PlatformAuthorityStatus.WrongDomain,
            NeutralDmaGrantDecision.AlreadyGranted => PlatformAuthorityStatus.Denied,
            NeutralDmaGrantDecision.Revoked => PlatformAuthorityStatus.Revoked,
            NeutralDmaGrantDecision.Stale => PlatformAuthorityStatus.Faulted,
            NeutralDmaGrantDecision.NotFound => PlatformAuthorityStatus.Faulted,
            NeutralDmaGrantDecision.Faulted => PlatformAuthorityStatus.Faulted,
            _ => PlatformAuthorityStatus.Faulted,
        };

        return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(status, result.Reason);
    }
}
