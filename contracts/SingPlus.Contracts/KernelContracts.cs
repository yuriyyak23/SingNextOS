namespace SingPlus.Contracts;

public readonly record struct DomainId(uint Value);

public readonly record struct ProcessId(uint Value);

public readonly record struct CapabilityId(uint Value);

public enum MemoryProfile
{
    KernelNoHeap,
    SipRegion,
    ManagedGc
}

public readonly record struct SingProcessManifest(
    ProcessId ProcessId,
    DomainId DomainId,
    MemoryProfile MemoryProfile);
