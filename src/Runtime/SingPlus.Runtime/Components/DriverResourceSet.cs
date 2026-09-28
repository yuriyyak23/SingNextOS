using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed record DriverMmioResourcePlan(
    string CapabilityResourceId,
    long Offset,
    long Length,
    PlatformMmioAccess Access);

public sealed record DriverIrqResourcePlan(string CapabilityResourceId);

public sealed class DriverDmaPolicy
{
    private readonly PlatformDmaDirection[] _allowedDirections;

    public DriverDmaPolicy(long maximumTransferBytes, IEnumerable<PlatformDmaDirection> allowedDirections)
    {
        if (maximumTransferBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumTransferBytes));
        ArgumentNullException.ThrowIfNull(allowedDirections);
        _allowedDirections = allowedDirections.Distinct().OrderBy(static value => value).ToArray();
        if (_allowedDirections.Length == 0 || _allowedDirections.Any(static value => !Enum.IsDefined(value)))
            throw new ArgumentException("DMA policy requires at least one defined direction.", nameof(allowedDirections));
        MaximumTransferBytes = maximumTransferBytes;
    }

    public long MaximumTransferBytes { get; }
    public IReadOnlyList<PlatformDmaDirection> AllowedDirections => _allowedDirections;

    internal bool Allows(PlatformDmaDirection direction, long length) =>
        Enum.IsDefined(direction) &&
        length > 0 &&
        length <= MaximumTransferBytes &&
        Array.IndexOf(_allowedDirections, direction) >= 0;
}

public sealed class ComponentDriverResourcePlan
{
    private readonly DriverMmioResourcePlan[] _mmio;
    private readonly DriverIrqResourcePlan[] _irqs;

    public ComponentDriverResourcePlan(
        string deviceResourceId,
        PlatformDeviceRights deviceRights,
        IEnumerable<DriverMmioResourcePlan>? mmio = null,
        IEnumerable<DriverIrqResourcePlan>? irqs = null,
        DriverDmaPolicy? dmaPolicy = null)
    {
        if (string.IsNullOrWhiteSpace(deviceResourceId) || deviceResourceId.Length > 256)
            throw new ArgumentException("Driver device identity must be a bounded semantic resource id.", nameof(deviceResourceId));
        if (deviceRights == PlatformDeviceRights.None ||
            (deviceRights & ~(PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure)) != 0)
            throw new ArgumentOutOfRangeException(nameof(deviceRights));

        _mmio = (mmio ?? []).OrderBy(static item => item.CapabilityResourceId, StringComparer.Ordinal).ToArray();
        _irqs = (irqs ?? []).OrderBy(static item => item.CapabilityResourceId, StringComparer.Ordinal).ToArray();
        if (_mmio.Select(static item => item.CapabilityResourceId).Distinct(StringComparer.Ordinal).Count() != _mmio.Length)
            throw new ArgumentException("Driver MMIO resources must be unique.", nameof(mmio));
        if (_irqs.Select(static item => item.CapabilityResourceId).Distinct(StringComparer.Ordinal).Count() != _irqs.Length)
            throw new ArgumentException("Driver IRQ resources must be unique.", nameof(irqs));

        foreach (var resource in _mmio)
        {
            if (string.IsNullOrWhiteSpace(resource.CapabilityResourceId) || resource.Offset < 0 || resource.Length <= 0 ||
                resource.Access == PlatformMmioAccess.None ||
                (resource.Access & ~(PlatformMmioAccess.Read | PlatformMmioAccess.Write)) != 0)
                throw new ArgumentException("Driver MMIO plans require an exact semantic resource, bounded range, and defined access.", nameof(mmio));
        }
        if (_irqs.Any(static resource => string.IsNullOrWhiteSpace(resource.CapabilityResourceId)))
            throw new ArgumentException("Driver IRQ plans require exact semantic capability resources.", nameof(irqs));

        DeviceResourceId = deviceResourceId;
        DeviceRights = deviceRights;
        DmaPolicy = dmaPolicy;
    }

    public string DeviceResourceId { get; }
    public PlatformDeviceRights DeviceRights { get; }
    public IReadOnlyList<DriverMmioResourcePlan> Mmio => _mmio;
    public IReadOnlyList<DriverIrqResourcePlan> Irqs => _irqs;
    public DriverDmaPolicy? DmaPolicy { get; }
}

public sealed record MmioResourceGrant(
    string CapabilityResourceId,
    CapabilityId CapabilityId,
    PlatformMmioLease Lease);

public sealed record IrqResourceGrant(
    string CapabilityResourceId,
    CapabilityId CapabilityId,
    PlatformIrqBinding Binding,
    KernelEventEndpoint EventEndpoint);

public sealed record DriverMmioResourceSnapshot(
    string CapabilityResourceId,
    string RegionResourceId,
    long Offset,
    long Length,
    PlatformMmioAccess Access);

public sealed record DriverIrqResourceSnapshot(
    string CapabilityResourceId,
    string SourceResourceId,
    IrqTriggerMode Trigger,
    KernelEventEndpoint EventEndpoint);

public sealed record DeviceResourceSetSnapshot(
    string DeviceResourceId,
    PlatformDeviceRights DeviceRights,
    IReadOnlyList<DriverMmioResourceSnapshot> Mmio,
    IReadOnlyList<DriverIrqResourceSnapshot> Irqs,
    DriverDmaPolicy? DmaPolicy,
    int ActiveDmaGrantCount);

public sealed class DeviceResourceSet
{
    private readonly List<MmioResourceGrant> _mmio = [];
    private readonly List<IrqResourceGrant> _irqs = [];
    private readonly List<PlatformDmaGrant> _activeDmaGrants = [];

    internal DeviceResourceSet(
        ProcessHandle owner,
        string deviceResourceId,
        CapabilityId deviceCapabilityId,
        PlatformDeviceLease deviceLease,
        DriverDmaPolicy? dmaPolicy)
    {
        Owner = owner;
        DeviceResourceId = deviceResourceId;
        DeviceCapabilityId = deviceCapabilityId;
        DeviceLease = deviceLease;
        DmaPolicy = dmaPolicy;
    }

    public ProcessHandle Owner { get; }
    public string DeviceResourceId { get; }
    public CapabilityId DeviceCapabilityId { get; }
    public PlatformDeviceLease DeviceLease { get; }
    public IReadOnlyList<MmioResourceGrant> Mmio => _mmio;
    public IReadOnlyList<IrqResourceGrant> Irqs => _irqs;
    public DriverDmaPolicy? DmaPolicy { get; }
    public IReadOnlyList<PlatformDmaGrant> ActiveDmaGrants => _activeDmaGrants;

    internal void AddMmio(MmioResourceGrant grant) => _mmio.Add(grant);
    internal void AddIrq(IrqResourceGrant grant) => _irqs.Add(grant);
    internal void AddDma(PlatformDmaGrant grant) => _activeDmaGrants.Add(grant);
    internal void RemoveDma(PlatformDmaGrant grant) =>
        _activeDmaGrants.RemoveAll(candidate => candidate.GrantId == grant.GrantId);

    internal DeviceResourceSetSnapshot Snapshot
    {
        get
        {
            var mmio = _mmio.Select(static grant =>
            {
                CapabilityResourceIds.TryParseMmioRegion(grant.CapabilityResourceId, out var resource);
                return new DriverMmioResourceSnapshot(
                    grant.CapabilityResourceId,
                    resource.RegionResourceId,
                    grant.Lease.Range.Offset,
                    grant.Lease.Range.Length,
                    grant.Lease.Access);
            }).ToArray();
            var irqs = _irqs.Select(static grant =>
            {
                CapabilityResourceIds.TryParseIrq(grant.CapabilityResourceId, out var resource);
                return new DriverIrqResourceSnapshot(
                    grant.CapabilityResourceId,
                    resource.SourceResourceId,
                    resource.Trigger,
                    grant.EventEndpoint);
            }).ToArray();
            return new DeviceResourceSetSnapshot(
                DeviceResourceId,
                DeviceLease.Rights,
                mmio,
                irqs,
                DmaPolicy,
                _activeDmaGrants.Count);
        }
    }
}
