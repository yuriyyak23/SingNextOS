using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformMmioLeaseProvider
{
    private sealed record MmioMapFailure(PlatformProviderMmioLeaseId InvocationId,
        PlatformProviderDeviceLease Device, DomainRecord Parent, NeutralDeviceLease NeutralDevice,
        PlatformMmioRegionIdentity Region, PlatformMmioRange Range, PlatformMmioAccess Access, NeutralMmioMapResult? Observation);
    private sealed record MmioCloseSource(DeviceLeaseRecord Device, DomainRecord Parent);
    private sealed class MmioLeaseRecord(
        PlatformProviderMmioLease lease,
        NeutralMmioLease hybridCpuLease)
    {
        public PlatformProviderMmioLease Lease { get; } = lease;
        public NeutralMmioLease HybridCpuLease { get; } = hybridCpuLease;
        public MmioCloseSource? MapSource { get; init; }
        public bool Revoked { get; set; }
        public bool CloseInFlight { get; set; }
        public bool CloseUncertain { get; set; }
        public NeutralMmioCloseResult? CloseObservation { get; set; }
        public MmioCloseSource? CloseSource { get; set; }
    }

    private readonly Dictionary<PlatformProviderMmioLeaseId, MmioLeaseRecord> _mmioLeases = [];
    private readonly Dictionary<PlatformProviderMmioLeaseId, MmioLeaseRecord> _originalMmioLeases = [];
    private ulong _nextProviderMmioLeaseId = 1;

    public PlatformAuthorityResult<PlatformProviderMmioLease> MapMmio(
        PlatformProviderDeviceLease deviceLease,
        PlatformMmioRegionIdentity region,
        PlatformMmioRange range,
        PlatformMmioAccess access)
    {
        var request = PlatformMmioLeaseContract.ValidateRequest(region, range, access);
        if (!request.IsSuccess)
            return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(request.Status, request.Message!);
        DeviceLeaseRecord device;
        DomainRecord parent;
        PlatformProviderMmioLeaseId reserved;
        NeutralMmioMapResult? retained;
        var neutralRegion = new NeutralMmioRegionIdentity(region.ResourceId, region.ByteLength);
        var neutralRange = new NeutralMmioRange(range.Offset, range.Length);
        var neutralAccess = ToNeutralMmioAccess(access);
        bool Exact(NeutralMmioMapResult? result) => result is { IsMapped: true, Decision: NeutralMmioMapDecision.Mapped } &&
            result.Lease.Handle.Value != 0 && result.Lease.Epoch.Value != 0 &&
            result.Lease.DeviceLease == device.HybridCpuLease && result.Lease.Region == neutralRegion &&
            result.Lease.Range == neutralRange && result.Lease.Access == neutralAccess;
        lock (_childCreateCommitGate)
        {
            var validation = ValidateProviderDeviceLease(deviceLease);
            if (!validation.IsSuccess)
                return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(validation.Status, validation.Message!);
            device = _deviceLeases[deviceLease.LeaseId];
            parent = _domains[deviceLease.DomainLease.LeaseId];
            if (device.CloseInFlight || device.CloseUncertain || device.CloseObservation is not null || device.MmioMapInFlight)
                return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(PlatformAuthorityStatus.Faulted,
                    "Original MMIO source is closing, retained or pending; repeat refused.");
            if (device.FailedMmioMap is { } original)
            {
                if (original.Device != deviceLease || !ReferenceEquals(original.Parent, parent) ||
                    original.NeutralDevice != device.HybridCpuLease || original.Region != region || original.Range != range ||
                    original.Access != access || !Exact(original.Observation))
                    return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(PlatformAuthorityStatus.Faulted,
                        "Original MMIO mapping is pinned or belongs to a different invocation; repeat effect refused.");
                reserved = original.InvocationId;
                retained = original.Observation;
            }
            else
            {
                if (_nextProviderMmioLeaseId is 0 or ulong.MaxValue)
                    return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(PlatformAuthorityStatus.Faulted,
                        "MMIO lease capacity exhausted before neutral effect.");
                reserved = new(_nextProviderMmioLeaseId++);
                retained = null;
            }
            _originalMmioLeases.EnsureCapacity(_originalMmioLeases.Count + 1);
            _mmioLeases.EnsureCapacity(_originalMmioLeases.Count + 1);
            device.MmioMapInFlight = true;
        }
        void Retain(NeutralMmioMapResult? observation) => device.FailedMmioMap =
            new(reserved, deviceLease, parent, device.HybridCpuLease, region, range, access, observation);
        try
        {
            NeutralMmioMapResult external;
            try { external = retained ?? _runtime.MapMmio(device.HybridCpuLease, neutralRegion, neutralRange, neutralAccess); }
            catch { lock (_childCreateCommitGate) Retain(null); throw; }
            lock (_childCreateCommitGate)
            {
                if (!Exact(external))
                {
                    if (external is not null && !external.IsMapped && external.Lease == default &&
                        external.Decision is NeutralMmioMapDecision.InvalidRegion or NeutralMmioMapDecision.InvalidRange or
                            NeutralMmioMapDecision.InvalidAccess or NeutralMmioMapDecision.InsufficientDeviceRights or NeutralMmioMapDecision.AlreadyMapped)
                        return FromNeutralMmioMapFailure(external);
                    Retain(external);
                    return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(PlatformAuthorityStatus.Faulted,
                        "Original MMIO outcome retained without closure; no returned-object compensation.");
                }
                if (!_deviceLeases.TryGetValue(deviceLease.LeaseId, out var currentDevice) || !ReferenceEquals(currentDevice, device) ||
                    !_domains.TryGetValue(deviceLease.DomainLease.LeaseId, out var currentParent) || !ReferenceEquals(currentParent, parent) ||
                    !ValidateProviderDeviceLease(deviceLease).IsSuccess || device.CloseInFlight || device.CloseUncertain || device.CloseObservation is not null)
                {
                    Retain(external);
                    return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(PlatformAuthorityStatus.Faulted,
                        "Exact original MMIO lease retained without current source publication.");
                }
                Retain(external);
                var lease = new PlatformProviderMmioLease(reserved, new(1), deviceLease, region, range, access);
                var originalRecord = new MmioLeaseRecord(lease, external.Lease) { MapSource = new(device, parent) };
                _originalMmioLeases.Add(reserved, originalRecord);
                _mmioLeases.Add(reserved, originalRecord);
                device.FailedMmioMap = null;
                return PlatformAuthorityResult<PlatformProviderMmioLease>.Ok(lease);
            }
        }
        finally { lock (_childCreateCommitGate) device.MmioMapInFlight = false; }
    }

    public PlatformAuthorityResult RevokeMmio(PlatformProviderMmioLease lease)
    {
        MmioLeaseRecord record;
        MmioCloseSource source;
        lock (_childCreateCommitGate)
        {
            if (!_mmioLeases.TryGetValue(lease.LeaseId, out record!))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "The provider MMIO lease does not exist.");
            if (!_originalMmioLeases.TryGetValue(lease.LeaseId, out var original) || !ReferenceEquals(original, record))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original MMIO owner is not current before close.");
            if (record.Lease.Generation != lease.Generation)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "The provider MMIO lease generation is stale.");
            if (record.Lease != lease)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "The provider MMIO lease identity is malformed.");
            if (record.Revoked)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Revoked, "The provider MMIO lease has already been revoked.");
            if (record.CloseInFlight || record.CloseUncertain)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original MMIO close is pending or unknown; repeat refused.");
            if (!ValidateProviderDeviceLease(lease.DeviceLease).IsSuccess)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original MMIO device source is not current before close.");
            var device = _deviceLeases[lease.DeviceLease.LeaseId];
            var parent = _domains[lease.DeviceLease.DomainLease.LeaseId];
            if (record.MapSource is not { } admitted || !ReferenceEquals(admitted.Device, device) ||
                !ReferenceEquals(admitted.Parent, parent))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original MMIO map source is not current before close.");
            source = record.CloseSource ?? new(device, parent);
            if (!ReferenceEquals(source.Device, device) || !ReferenceEquals(source.Parent, parent) ||
                device.CloseInFlight || device.CloseUncertain || device.CloseObservation is not null)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original MMIO close source changed or is closing.");
            record.CloseSource = source;
            record.CloseInFlight = true;
        }
        try
        {
            NeutralMmioCloseResult external;
            try { external = record.CloseObservation ?? _runtime.CloseMmio(record.HybridCpuLease); }
            catch { lock (_childCreateCommitGate) record.CloseUncertain = true; throw; }
            lock (_childCreateCommitGate)
            {
                record.CloseObservation = external;
                if (external is null || external.Decision != NeutralMmioCloseDecision.Closed)
                {
                    record.CloseUncertain = true;
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                        "Original MMIO close lacks exact Closed outcome; mapping/device remain pinned.");
                }
                if (!_mmioLeases.TryGetValue(lease.LeaseId, out var current) || !ReferenceEquals(current, record) ||
                    !_originalMmioLeases.TryGetValue(lease.LeaseId, out var original) || !ReferenceEquals(original, record) ||
                    !_deviceLeases.TryGetValue(lease.DeviceLease.LeaseId, out var device) || !ReferenceEquals(device, source.Device) ||
                    !_domains.TryGetValue(lease.DeviceLease.DomainLease.LeaseId, out var parent) || !ReferenceEquals(parent, source.Parent) ||
                    !ValidateProviderDeviceLease(lease.DeviceLease).IsSuccess || source.Device.CloseInFlight ||
                    source.Device.CloseUncertain || source.Device.CloseObservation is not null ||
                    record.HybridCpuLease.DeviceLease != source.Device.HybridCpuLease ||
                    source.Device.HybridCpuLease.DomainLease != source.Parent.HybridCpuLease)
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                        "Exact original MMIO close retained without current owner/source publication.");
                record.Revoked = true;
                return PlatformAuthorityResult.Ok();
            }
        }
        finally { lock (_childCreateCommitGate) record.CloseInFlight = false; }
    }

    internal bool HasActiveProviderMmioLeases(PlatformProviderDeviceLease deviceLease) =>
        (_originalDeviceLeases.TryGetValue(deviceLease.LeaseId, out var source) &&
            (source.MmioMapInFlight || source.FailedMmioMap is not null)) ||
        _originalMmioLeases.Values.Any(record =>
            !record.Revoked && record.Lease.DeviceLease == deviceLease);

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
