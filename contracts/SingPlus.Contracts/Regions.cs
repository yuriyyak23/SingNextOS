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

public readonly record struct RegionUseRange(long Offset, long Length);

public readonly record struct RegionBackingLeaseId(ulong Value);
public readonly record struct RegionBackingLeaseHandle(RegionBackingLeaseId LeaseId, ulong Generation);
public sealed record RegionBackingLeaseDescriptor(
    RegionBackingLeaseHandle Handle,
    RegionHandle Region,
    RegionOwner Owner,
    long ByteLength);

public enum RegionUseMode
{
    ReadOnly = 0,
    ExclusiveWrite = 1,
    StagedOutput = 2,
    DirectCoherentWrite = 3,
    DevicePrivate = 4,
    SharedReadMostly = 5
}

public enum RegionUseState
{
    Active = 0,
    Invalidated = 1,
    Released = 2
}

public sealed record RegionUseDescriptor(
    RegionUseHandle Handle,
    RegionHandle Region,
    RegionOwner Principal,
    RegionUseRange Range,
    RegionUseMode Mode,
    MutationEpoch MutationEpoch,
    RegionUseState State);

public sealed record RegionDescriptor(
    RegionHandle Handle,
    RegionOwner Owner,
    long ByteLength,
    string ElementType,
    RegionState State,
    MutationEpoch MutationEpoch);
