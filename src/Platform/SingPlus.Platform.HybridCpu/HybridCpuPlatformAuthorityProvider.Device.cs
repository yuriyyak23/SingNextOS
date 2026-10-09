using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformDeviceLeaseProvider
{
    private sealed record DeviceBindFailure(PlatformProviderDeviceLeaseId InvocationId,
        PlatformProviderDomainLease Domain, NeutralDomainBindingLease NeutralDomain, PlatformDeviceIdentity Device,
        PlatformDeviceRights Rights, NeutralDeviceBindResult? Observation);
    private sealed class DeviceLeaseRecord(
        PlatformProviderDeviceLease lease,
        NeutralDeviceLease hybridCpuLease)
    {
        public PlatformProviderDeviceLease Lease { get; } = lease;
        public NeutralDeviceLease HybridCpuLease { get; } = hybridCpuLease;
        public DomainRecord? BindParent { get; init; }
        public bool Revoked { get; set; }
        public bool IrqBindInFlight { get; set; }
        public IrqBindFailure? FailedIrqBind { get; set; }
        public bool MmioMapInFlight { get; set; }
        public MmioMapFailure? FailedMmioMap { get; set; }
        public bool DmaBindInFlight { get; set; }
        public DmaBindFailure? FailedDmaBind { get; set; }
        public bool CloseInFlight { get; set; }
        public bool CloseUncertain { get; set; }
        public NeutralDeviceCloseResult? CloseObservation { get; set; }
        public DomainRecord? CloseParent { get; set; }
    }

    private readonly Dictionary<PlatformProviderDeviceLeaseId, DeviceLeaseRecord> _deviceLeases = [];
    private readonly Dictionary<PlatformProviderDeviceLeaseId, DeviceLeaseRecord> _originalDeviceLeases = [];
    private ulong _nextProviderDeviceLeaseId = 1;

    public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(
        PlatformProviderDomainLease domainLease,
        PlatformDeviceIdentity device,
        PlatformDeviceRights rights)
    {
        var request = PlatformDeviceLeaseContract.ValidateRequest(device, rights);
        if (!request.IsSuccess)
            return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(request.Status, request.Message!);
        DomainRecord parent;
        PlatformProviderDeviceLeaseId reserved;
        NeutralDeviceBindResult? retained;
        var neutralDevice = new NeutralDeviceIdentity(device.ResourceId);
        var neutralRights = ToNeutralDeviceRights(rights);
        bool Exact(NeutralDeviceBindResult? result) => result is { IsBound: true, Decision: NeutralDeviceBindDecision.Bound } &&
            result.Lease.Handle.Value != 0 && result.Lease.Epoch.Value != 0 &&
            result.Lease.DomainLease == parent.HybridCpuLease && result.Lease.Device == neutralDevice && result.Lease.Rights == neutralRights;
        lock (_childCreateCommitGate)
        {
            var validation = ValidateDomain(domainLease);
            if (!validation.IsSuccess)
                return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(validation.Status, validation.Message!);
            parent = _domains[domainLease.LeaseId];
            if (parent.DeviceBindInFlight)
                return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(PlatformAuthorityStatus.Faulted, "Original device bind is pending.");
            if (parent.FailedDeviceBind is { } original)
            {
                if (original.Domain != domainLease || original.NeutralDomain != parent.HybridCpuLease ||
                    original.Device != device || original.Rights != rights || !Exact(original.Observation))
                    return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(PlatformAuthorityStatus.Faulted,
                        "Original device bind is pinned or belongs to a different invocation; repeat effect refused.");
                reserved = original.InvocationId;
                retained = original.Observation;
            }
            else
            {
                if (_nextProviderDeviceLeaseId is 0 or ulong.MaxValue)
                    return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(PlatformAuthorityStatus.Faulted,
                        "Device lease capacity exhausted before neutral effect.");
                reserved = new(_nextProviderDeviceLeaseId++);
                retained = null;
            }
            _originalDeviceLeases.EnsureCapacity(_originalDeviceLeases.Count + 1);
            _deviceLeases.EnsureCapacity(_originalDeviceLeases.Count + 1);
            parent.DeviceBindInFlight = true;
        }
        void Retain(NeutralDeviceBindResult? observation) => parent.FailedDeviceBind =
            new(reserved, domainLease, parent.HybridCpuLease, device, rights, observation);
        try
        {
            NeutralDeviceBindResult external;
            try { external = retained ?? _runtime.BindDevice(parent.HybridCpuLease, neutralDevice, neutralRights); }
            catch { lock (_childCreateCommitGate) Retain(null); throw; }
            lock (_childCreateCommitGate)
            {
                if (!Exact(external))
                {
                    if (external is not null && !external.IsBound && external.Lease == default &&
                        external.Decision is NeutralDeviceBindDecision.InvalidDevice or NeutralDeviceBindDecision.InvalidRights or NeutralDeviceBindDecision.AlreadyBound)
                        return FromNeutralDeviceBindFailure(external);
                    Retain(external);
                    return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(PlatformAuthorityStatus.Faulted,
                        "Original device bind outcome retained without closure; no returned-object compensation.");
                }
                if (!_domains.TryGetValue(domainLease.LeaseId, out var current) || !ReferenceEquals(current, parent) ||
                    !ValidateDomain(domainLease).IsSuccess)
                {
                    Retain(external);
                    return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(PlatformAuthorityStatus.Faulted,
                        "Exact original device lease retained without current parent publication.");
                }
                Retain(external);
                var lease = new PlatformProviderDeviceLease(reserved, new(1), domainLease, device, rights);
                var originalRecord = new DeviceLeaseRecord(lease, external.Lease) { BindParent = parent };
                _originalDeviceLeases.Add(reserved, originalRecord);
                _deviceLeases.Add(reserved, originalRecord);
                parent.FailedDeviceBind = null;
                return PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(lease);
            }
        }
        finally { lock (_childCreateCommitGate) parent.DeviceBindInFlight = false; }
    }

    public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease)
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

        if (!_originalDeviceLeases.TryGetValue(lease.LeaseId, out var original) || !ReferenceEquals(original, record))
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

        if (record.Revoked)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The provider device lease has already been revoked.");
        }

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

        if (record.CloseInFlight || record.CloseUncertain)
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original device close is pending or unknown.");
        var domainValidation = ValidateDomain(lease.DomainLease);
        if (!domainValidation.IsSuccess) return domainValidation;
        var parent = _domains[lease.DomainLease.LeaseId];
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
        try { external = record.CloseObservation ?? _runtime.CloseDevice(record.HybridCpuLease); }
        catch { lock (_childCreateCommitGate) record.CloseUncertain = true; throw; }
        lock (_childCreateCommitGate)
        {
        if (external is null)
        {
            record.CloseUncertain = true;
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original device close observation absent.");
        }
        record.CloseObservation = external;
        if (!_deviceLeases.TryGetValue(lease.LeaseId, out var current) || !ReferenceEquals(current, record) ||
            !_originalDeviceLeases.TryGetValue(lease.LeaseId, out var original) || !ReferenceEquals(original, record) ||
            !ValidateDomain(lease.DomainLease).IsSuccess ||
            !_domains.TryGetValue(lease.DomainLease.LeaseId, out var currentParent) || !ReferenceEquals(currentParent, record.CloseParent) ||
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
            !record.Revoked && record.Lease.DomainLease == domainLease);

    private static NeutralDeviceRights ToNeutralDeviceRights(PlatformDeviceRights rights)
    {
        var result = NeutralDeviceRights.None;
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
        var status = result.Decision switch
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
