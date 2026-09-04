namespace SingPlus.Contracts;

public readonly record struct DomainId(ulong Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct ProcessId(ulong Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct CapabilityId(ulong Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct RegionId(ulong Value);
public readonly record struct RegionGeneration(ulong Value);
public readonly record struct BorrowLeaseGeneration(ulong Value);
public readonly record struct ChannelId(ulong Value);
public readonly record struct EndpointId(ulong Value);

public enum MemoryProfile
{
    KernelNoHeap = 0,
    SipRegion = 1,
    ManagedGc = 2
}

public enum ExecutionRole
{
    Kernel = 0,
    Sip = 1,
    Driver = 2,
    User = 3
}

public enum ProcessState
{
    Created = 0,
    Admitted = 1,
    Runnable = 2,
    Running = 3,
    Parked = 4,
    Exiting = 5,
    Exited = 6,
    Faulted = 7
}

[Flags]
public enum CapabilityRights
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    Map = 1 << 2,
    Signal = 1 << 3,
    Configure = 1 << 4,
    Transfer = 1 << 5,
    Delegate = 1 << 6,
    Execute = 1 << 7
}

public enum ResourceKind
{
    KernelService = 0,
    MemoryRegion = 1,
    ChannelEndpoint = 2,
    Device = 3,
    MmioRegion = 4,
    Irq = 5,
    Dma = 6,
    Compute = 7
}

public readonly record struct CapabilityRequirementV1(ResourceKind ResourceKind, string ResourceId, CapabilityRights Rights);

public readonly record struct ResourceLimitsV1(
    long MaxMemoryBytes,
    int MaxRegions,
    int MaxChannels,
    int MaxPendingMessages)
{
    public static ResourceLimitsV1 Default { get; } = new(16 * 1024 * 1024, 128, 64, 256);
}

public readonly record struct ProcessHandle(ProcessId ProcessId, ulong Generation);
public readonly record struct RegionHandle(RegionId RegionId, RegionGeneration Generation);
public readonly record struct BorrowLeaseHandle(RegionHandle Region, BorrowLeaseGeneration Generation);
public readonly record struct ChannelEndpointHandle(ChannelId ChannelId, EndpointId EndpointId, ulong Generation);
