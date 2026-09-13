namespace SingPlus.Contracts;

public readonly record struct VirtualDomainId(ulong Value);
public readonly record struct VirtualDomainGeneration(ulong Value);
public readonly record struct VirtualDomainHandle(VirtualDomainId DomainId, VirtualDomainGeneration Generation);
public readonly record struct VirtualAddressSpaceId(ulong Value);
public readonly record struct VirtualAddressSpaceGeneration(ulong Value);
public readonly record struct VirtualAddressSpaceHandle(VirtualAddressSpaceId AddressSpaceId, VirtualAddressSpaceGeneration Generation);
public readonly record struct GuestRegionMappingId(ulong Value);
public readonly record struct GuestRegionMappingGeneration(ulong Value);
public readonly record struct GuestRegionMappingHandle(GuestRegionMappingId MappingId, GuestRegionMappingGeneration Generation);

public enum VirtualDomainState { Created = 0, Configured, Running, Parked, Draining, Closed, Faulted, Quarantined }
[Flags] public enum GuestMemoryAccess { None = 0, Read = 1, Write = 2, Execute = 4 }
[Flags]
public enum VirtualDomainAuthorityClass
{
    None = 0,
    Lifecycle = 1 << 0,
    Execution = 1 << 1,
    GuestMemory = 1 << 2,
    Events = 1 << 3,
    Traps = 1 << 4,
    Io = 1 << 5,
}

public enum VirtualTrapKind
{
    MemoryFault = 0,
    IllegalInstruction,
    Hypercall,
    ExternalEvent,
    Timer,
    Preemption,
    DeviceOrIoFault,
}

public readonly record struct VirtualDomainProfile(int VirtualProcessorCount, long MaximumGuestMemoryBytes);
public readonly record struct GuestAddressRange(ulong GuestAddress, long ByteLength);
public readonly record struct VirtualDomainAuthoritySet(
    VirtualDomainHandle Domain,
    VirtualAddressSpaceHandle AddressSpace,
    CapabilityId ConfigureCapability,
    CapabilityId MemoryCapability,
    CapabilityId ExecuteCapability,
    CapabilityId EventCapability,
    CapabilityId TrapCapability);

public readonly record struct NestedVirtualDomainRequestProfile(
    VirtualDomainProfile Profile,
    VirtualDomainAuthorityClass Authority);

/// <summary>Semantic observation only; never capability, completion or reclaim authority.</summary>
public readonly record struct VirtualTrapObservation(
    VirtualDomainHandle Domain,
    ulong Sequence,
    VirtualTrapKind Kind);

public readonly record struct GuestRegionMapping(
    GuestRegionMappingHandle Mapping,
    VirtualDomainHandle Domain,
    VirtualAddressSpaceHandle AddressSpace,
    GuestAddressRange GuestRange,
    RegionHandle Region,
    GuestMemoryAccess Access);

public static class VirtualizationResourceIds
{
    public const string Create = "virtualization:create:v1";
    public static string Domain(VirtualDomainId id) => $"virtual-domain:{id.Value}";
    public static string Memory(VirtualDomainId id) => $"virtual-domain:{id.Value}:memory";
    public static string Events(VirtualDomainId id) => $"virtual-domain:{id.Value}:events";
    public static string Traps(VirtualDomainId id) => $"virtual-domain:{id.Value}:traps";
}
