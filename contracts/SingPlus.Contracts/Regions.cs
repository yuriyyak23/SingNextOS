namespace SingPlus.Contracts;

public enum RegionState
{
    Allocated = 0,
    Owned = 1,
    Loaned = 2,
    Transferred = 3,
    Released = 4
}

public readonly record struct RegionOwner(DomainId DomainId, ulong ProcessGeneration);

public sealed record RegionDescriptor(
    RegionHandle Handle,
    RegionOwner Owner,
    long ByteLength,
    string ElementType,
    RegionState State);
