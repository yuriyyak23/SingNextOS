namespace SingPlus.Contracts;

[Flags]
public enum ResourceChargeEventV1 : ushort
{
    None = 0,
    Admitted = 1 << 0,
    Issued = 1 << 1,
    Executed = 1 << 2,
    Squashed = 1 << 3,
    ReplayedOrRetried = 1 << 4,
    Retired = 1 << 5,
    ProviderOverhead = 1 << 6,
    Residency = 1 << 7,
    StalledOrBlocked = 1 << 8,
}

public readonly record struct MeasurementContractIdentityV1(
    ushort Version,
    string Name,
    string ContractVersion,
    ResourceClassV1 ResourceClass,
    ResourceUnitV1 Unit)
{
    public const ushort CurrentVersion = 1;

    public MeasurementContractIdentityV1 Canonicalize()
    {
        if (Version != CurrentVersion) throw new NotSupportedException($"Measurement identity version {Version} is unsupported.");
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(ContractVersion) ||
            !Enum.IsDefined(ResourceClass) || !Enum.IsDefined(Unit))
            throw new ArgumentException("Measurement contract identity is incomplete or invalid.");
        return this with { Name = Name.Trim(), ContractVersion = ContractVersion.Trim() };
    }
}

public readonly record struct ResourceUsageBreakdownV1(
    ushort Version,
    ulong Admitted,
    ulong Issued,
    ulong Executed,
    ulong Squashed,
    ulong ReplayedOrRetried,
    ulong Retired,
    ulong ProviderOverhead,
    ulong Residency,
    ulong StalledOrBlocked)
{
    public const ushort CurrentVersion = 1;
}

public readonly record struct ResourceChargeabilityPolicyV1(
    ushort Version,
    ResourceClassV1 ResourceClass,
    ResourceUnitV1 Unit,
    ResourceChargeEventV1 ChargedEvents)
{
    public const ushort CurrentVersion = 1;
    public bool IsAuthority => false;
    public bool IsReservationGuarantee => false;

    public ulong Normalize(ResourceUsageBreakdownV1 usage)
    {
        if (Version != CurrentVersion || usage.Version != ResourceUsageBreakdownV1.CurrentVersion ||
            !Enum.IsDefined(ResourceClass) || !Enum.IsDefined(Unit) ||
            (ChargedEvents & ~AllEvents) != 0)
            throw new NotSupportedException("Chargeability policy or usage version/class is unsupported.");
        ulong total = 0;
        checked
        {
            if (ChargedEvents.HasFlag(ResourceChargeEventV1.Admitted)) total += usage.Admitted;
            if (ChargedEvents.HasFlag(ResourceChargeEventV1.Issued)) total += usage.Issued;
            if (ChargedEvents.HasFlag(ResourceChargeEventV1.Executed)) total += usage.Executed;
            if (ChargedEvents.HasFlag(ResourceChargeEventV1.Squashed)) total += usage.Squashed;
            if (ChargedEvents.HasFlag(ResourceChargeEventV1.ReplayedOrRetried)) total += usage.ReplayedOrRetried;
            if (ChargedEvents.HasFlag(ResourceChargeEventV1.Retired)) total += usage.Retired;
            if (ChargedEvents.HasFlag(ResourceChargeEventV1.ProviderOverhead)) total += usage.ProviderOverhead;
            if (ChargedEvents.HasFlag(ResourceChargeEventV1.Residency)) total += usage.Residency;
            if (ChargedEvents.HasFlag(ResourceChargeEventV1.StalledOrBlocked)) total += usage.StalledOrBlocked;
        }
        return total;
    }

    private const ResourceChargeEventV1 AllEvents = ResourceChargeEventV1.Admitted |
        ResourceChargeEventV1.Issued | ResourceChargeEventV1.Executed | ResourceChargeEventV1.Squashed |
        ResourceChargeEventV1.ReplayedOrRetried | ResourceChargeEventV1.Retired |
        ResourceChargeEventV1.ProviderOverhead | ResourceChargeEventV1.Residency |
        ResourceChargeEventV1.StalledOrBlocked;
}

public static class ResourceChargeabilityMatrixV1
{
    public static ResourceChargeabilityPolicyV1 For(ResourceClassV1 resourceClass) => resourceClass switch
    {
        ResourceClassV1.ComputeTime => Policy(resourceClass, ResourceUnitV1.Nanoseconds,
            ResourceChargeEventV1.Executed | ResourceChargeEventV1.Squashed |
            ResourceChargeEventV1.ReplayedOrRetried | ResourceChargeEventV1.ProviderOverhead),
        ResourceClassV1.DmaThroughput or ResourceClassV1.NetworkThroughput or ResourceClassV1.FabricThroughput =>
            Policy(resourceClass, ResourceUnitV1.BytesPerWindow,
                ResourceChargeEventV1.Executed | ResourceChargeEventV1.ReplayedOrRetried),
        ResourceClassV1.DeviceMemoryOccupancy => Policy(resourceClass, ResourceUnitV1.Bytes,
            ResourceChargeEventV1.Residency),
        ResourceClassV1.QueueSlotOccupancy => Policy(resourceClass, ResourceUnitV1.Slots,
            ResourceChargeEventV1.Residency),
        ResourceClassV1.InflightOperationOccupancy => Policy(resourceClass, ResourceUnitV1.Operations,
            ResourceChargeEventV1.Residency),
        _ => throw new ArgumentOutOfRangeException(nameof(resourceClass)),
    };

    private static ResourceChargeabilityPolicyV1 Policy(ResourceClassV1 resourceClass,
        ResourceUnitV1 unit, ResourceChargeEventV1 events) => new(1, resourceClass, unit, events);
}
