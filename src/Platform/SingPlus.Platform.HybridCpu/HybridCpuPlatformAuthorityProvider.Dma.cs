using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformDmaGrantProvider
{
    private sealed record DmaBindFailure(PlatformProviderDmaGrantId InvocationId, PlatformDmaGrantRequest Request,
        DeviceLeaseRecord Device, ProviderMappingRecord Mapping, DomainRecord Parent, NeutralDmaGrantResult? Observation);
    private sealed record DmaSource(DeviceLeaseRecord Device, ProviderMappingRecord Mapping, DomainRecord Parent);
    private sealed class DmaGrantRecord(
        PlatformProviderDmaGrant grant,
        NeutralDmaGrant hybridCpuGrant)
    {
        public PlatformProviderDmaGrant Grant { get; } = grant;
        public NeutralDmaGrant HybridCpuGrant { get; } = hybridCpuGrant;
        public DmaSource? BindSource { get; init; }
        public bool Revoked { get; set; }
        public bool VisibilityInFlight { get; set; }
        public bool VisibilityUnknown { get; set; }
        public DmaVisibilityInvocation? VisibilityInvocation { get; set; }
        public ProviderDmaVisibilityState? VisibilityState { get; set; }
        public bool CloseInFlight { get; set; }
        public bool CloseUncertain { get; set; }
        public NeutralDmaGrantCloseResult? CloseObservation { get; set; }
        public DmaSource? CloseSource { get; set; }
    }

    private readonly Dictionary<PlatformProviderDmaGrantId, DmaGrantRecord> _dmaGrants = [];
    private readonly Dictionary<PlatformProviderDmaGrantId, DmaGrantRecord> _originalDmaGrants = [];
    private ulong _nextProviderDmaGrantId = 1;

    public PlatformAuthorityResult<PlatformProviderDmaGrant> BindDmaGrant(
        PlatformDmaGrantRequest request)
    {
        var requestValidation = PlatformDmaGrantContract.ValidateRequest(request);
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
        lock (_childCreateCommitGate)
        {
            var dv = ValidateProviderDeviceLease(request.DeviceLease);
            var mv = ValidateProviderMapping(request.MappingLease, request.MappingSlice);
            if (!dv.IsSuccess) return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(dv.Status, dv.Message!);
            if (!mv.IsSuccess) return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(mv.Status, mv.Message!);
            device = _deviceLeases[request.DeviceLease.LeaseId];
            mapping = _providerMappings[request.MappingLease.MappingId];
            parent = _domains[request.DeviceLease.DomainLease.LeaseId];
            if (device.CloseInFlight || device.CloseUncertain || device.CloseObservation is not null ||
                device.DmaBindInFlight || mapping.DmaBindInFlight || device.FailedDmaBind is not null || mapping.FailedDmaBind is not null ||
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
            device.DmaBindInFlight = mapping.DmaBindInFlight = true;
        }
        var range = new NeutralDmaRange(request.Range.Offset, request.Range.Length);
        var direction = ToNeutralDmaDirection(request.Direction);
        void Retain(NeutralDmaGrantResult? observation)
        {
            var failure = new DmaBindFailure(reserved, request, device, mapping, parent, observation);
            device.FailedDmaBind = mapping.FailedDmaBind = failure;
        }
        try
        {
            NeutralDmaGrantResult external;
            try { external = _runtime.BindDmaGrant(device.HybridCpuLease, mapping.HybridCpuLease, range, direction); }
            catch { lock (_childCreateCommitGate) Retain(null); throw; }
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
                        return FromNeutralDmaGrantFailure(external);
                    Retain(external);
                    return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(PlatformAuthorityStatus.Faulted,
                        "Original DMA invocation outcome retained without closure; returned-object compensation is not original closure.");
                }
                if (!_deviceLeases.TryGetValue(request.DeviceLease.LeaseId, out var currentDevice) || !ReferenceEquals(currentDevice, device) ||
                    !_providerMappings.TryGetValue(request.MappingLease.MappingId, out var currentMapping) || !ReferenceEquals(currentMapping, mapping) ||
                    !_domains.TryGetValue(parent.Lease.LeaseId, out var currentParent) || !ReferenceEquals(currentParent, parent) ||
                    !ValidateProviderDeviceLease(request.DeviceLease).IsSuccess || !ValidateProviderMapping(request.MappingLease, request.MappingSlice).IsSuccess ||
                    device.CloseInFlight || device.CloseUncertain || device.CloseObservation is not null)
                {
                    Retain(external);
                    return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(PlatformAuthorityStatus.Faulted,
                        "Exact original DMA grant retained without current source publication.");
                }
                Retain(external);
                var grant = new PlatformProviderDmaGrant(reserved, new(1), request.DeviceLease, request.MappingLease,
                    request.Range, request.Direction);
                var originalRecord = new DmaGrantRecord(grant, external.Grant) { BindSource = new(device, mapping, parent) };
                _originalDmaGrants.Add(reserved, originalRecord);
                _dmaGrants.Add(reserved, originalRecord);
                device.FailedDmaBind = mapping.FailedDmaBind = null;
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Ok(grant);
            }
        }
        finally { lock (_childCreateCommitGate) device.DmaBindInFlight = mapping.DmaBindInFlight = false; }
    }

    // Pure lifetime correlation check under the existing local commit gate.
    private bool IsCurrentDmaBindSource(DmaGrantRecord record) => record.BindSource is { } source &&
        _deviceLeases.TryGetValue(record.Grant.DeviceLease.LeaseId, out var device) && ReferenceEquals(device, source.Device) &&
        _providerMappings.TryGetValue(record.Grant.MappingLease.MappingId, out var mapping) && ReferenceEquals(mapping, source.Mapping) &&
        _domains.TryGetValue(record.Grant.DeviceLease.DomainLease.LeaseId, out var parent) && ReferenceEquals(parent, source.Parent);

    public PlatformAuthorityResult RevokeDmaGrant(PlatformProviderDmaGrant grant)
    {
        DmaGrantRecord record;
        DmaSource source;
        lock (_childCreateCommitGate)
        {
            if (!_dmaGrants.TryGetValue(grant.GrantId, out record!))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "The provider DMA grant does not exist.");
            if (!_originalDmaGrants.TryGetValue(grant.GrantId, out var original) || !ReferenceEquals(original, record))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original DMA owner is not current before close.");
            if (record.Grant.Generation != grant.Generation)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "The provider DMA grant generation is stale.");
            if (record.Grant != grant)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "The provider DMA grant identity is malformed.");
            if (record.Revoked)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Revoked, "The provider DMA grant has already been revoked.");
            if (record.CloseInFlight || record.CloseUncertain || record.VisibilityInFlight)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original DMA close is pending or unknown; repeat refused.");
            if (!ValidateProviderDeviceLease(grant.DeviceLease).IsSuccess ||
                !ValidateProviderMapping(grant.MappingLease, expectedSlice: null).IsSuccess)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original DMA sources are not current before close.");
            if (!IsCurrentDmaBindSource(record))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original DMA bind sources changed before close.");
            var device = _deviceLeases[grant.DeviceLease.LeaseId];
            var mapping = _providerMappings[grant.MappingLease.MappingId];
            var parent = _domains[grant.DeviceLease.DomainLease.LeaseId];
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
            try { external = record.CloseObservation ?? _runtime.CloseDmaGrant(record.HybridCpuGrant); }
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
                if (!_dmaGrants.TryGetValue(grant.GrantId, out var current) || !ReferenceEquals(current, record) ||
                    !_originalDmaGrants.TryGetValue(grant.GrantId, out var original) || !ReferenceEquals(original, record) ||
                    !_deviceLeases.TryGetValue(grant.DeviceLease.LeaseId, out var device) || !ReferenceEquals(device, source.Device) ||
                    !_providerMappings.TryGetValue(grant.MappingLease.MappingId, out var mapping) || !ReferenceEquals(mapping, source.Mapping) ||
                    !_domains.TryGetValue(grant.DeviceLease.DomainLease.LeaseId, out var parent) || !ReferenceEquals(parent, source.Parent) ||
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
        (_originalDeviceLeases.TryGetValue(lease.LeaseId, out var owner) &&
            (owner.DmaBindInFlight || owner.FailedDmaBind is not null)) ||
        _originalDmaGrants.Values.Any(r => !r.Revoked && r.Grant.DeviceLease == lease);

    internal bool HasActiveProviderDmaGrants(PlatformProviderRegionMappingLease lease) =>
        (_originalProviderMappings.TryGetValue(lease.MappingId, out var owner) &&
            (owner.DmaBindInFlight || owner.FailedDmaBind is not null)) ||
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
        var status = result.Decision switch
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
