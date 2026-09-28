namespace SingPlus.Contracts;

public enum ResourceDimensionFamilyV1 : byte
{
    Time = 1,
    Throughput = 2,
    Occupancy = 3,
}

public enum ResourceClassV1 : ushort
{
    ComputeTime = 1,
    DmaThroughput = 2,
    NetworkThroughput = 3,
    FabricThroughput = 4,
    DeviceMemoryOccupancy = 5,
    QueueSlotOccupancy = 6,
    InflightOperationOccupancy = 7,
}

public enum ResourceUnitV1 : byte
{
    Nanoseconds = 1,
    BytesPerWindow = 2,
    Bytes = 3,
    Slots = 4,
    Operations = 5,
}

public enum ResourceAssuranceV1 : byte
{
    AccountingOnly = 1,
    RuntimeEnforced = 2,
    EnforcedUpperBound = 3,
    GuaranteedReservation = 4,
}

/// <summary>
/// Immutable semantic resource envelope. It is a descriptor, not authority or a
/// reservation. Version 1 deliberately rejects zero and UInt64.MaxValue rather
/// than assigning either value sentinel meaning.
/// </summary>
public readonly record struct ResourceEnvelopeV1(
    ushort Version,
    ResourceDimensionFamilyV1 Family,
    ResourceClassV1 ResourceClass,
    ResourceUnitV1 Unit,
    ulong Amount,
    ulong WindowNanoseconds,
    string SemanticScope)
{
    public const ushort CurrentVersion = 1;

    public ResourceEnvelopeV1 Canonicalize()
    {
        if (Version != CurrentVersion) throw new NotSupportedException("Unknown resource envelope version.");
        if (!Enum.IsDefined(Family) || !Enum.IsDefined(ResourceClass) || !Enum.IsDefined(Unit))
            throw new ArgumentOutOfRangeException(nameof(ResourceClass), "Unknown resource dimension, class, or unit.");
        if (Amount is 0 or ulong.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(Amount), "Resource amount cannot use zero or maximum sentinel values.");
        if (string.IsNullOrWhiteSpace(SemanticScope) || !string.Equals(SemanticScope, SemanticScope.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Semantic scope must be non-empty and canonical.", nameof(SemanticScope));

        var valid = (Family, ResourceClass, Unit, WindowNanoseconds) switch
        {
            (ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds, 0) => true,
            (ResourceDimensionFamilyV1.Throughput, ResourceClassV1.DmaThroughput or ResourceClassV1.NetworkThroughput or ResourceClassV1.FabricThroughput, ResourceUnitV1.BytesPerWindow, > 0 and < ulong.MaxValue) => true,
            (ResourceDimensionFamilyV1.Occupancy, ResourceClassV1.DeviceMemoryOccupancy, ResourceUnitV1.Bytes, 0) => true,
            (ResourceDimensionFamilyV1.Occupancy, ResourceClassV1.QueueSlotOccupancy, ResourceUnitV1.Slots, 0) => true,
            (ResourceDimensionFamilyV1.Occupancy, ResourceClassV1.InflightOperationOccupancy, ResourceUnitV1.Operations, 0) => true,
            _ => false,
        };
        if (!valid) throw new ArgumentException("Resource family, class, unit, and window are dimensionally incompatible.");
        return this;
    }

    public static bool IsSubset(ResourceEnvelopeV1 child, ResourceEnvelopeV1 parent)
    {
        try
        {
            child = child.Canonicalize();
            parent = parent.Canonicalize();
            return child.Family == parent.Family && child.ResourceClass == parent.ResourceClass &&
                   child.Unit == parent.Unit && child.WindowNanoseconds == parent.WindowNanoseconds &&
                   string.Equals(child.SemanticScope, parent.SemanticScope, StringComparison.Ordinal) &&
                   child.Amount <= parent.Amount;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        {
            return false;
        }
    }

    public ulong CheckedWindowQuantity()
    {
        var value = Canonicalize();
        if (value.Family != ResourceDimensionFamilyV1.Throughput)
            throw new InvalidOperationException("Only throughput envelopes have a window quantity.");
        return checked(value.Amount * value.WindowNanoseconds);
    }
}

/// <summary>Pure monotonic constraint model for later integration into CapabilityAuthority.</summary>
public readonly record struct ResourceUseConstraintV1(
    ushort Version,
    ResourceEnvelopeV1 Envelope,
    long NotBeforeUtcTicks,
    long ExpiresUtcTicks,
    ResourceAssuranceV1 AssuranceCeiling,
    ushort DelegationDepth)
{
    public const ushort CurrentVersion = 1;

    public ResourceUseConstraintV1 Canonicalize()
    {
        if (Version != CurrentVersion) throw new NotSupportedException("Unknown resource-use constraint version.");
        if (!Enum.IsDefined(AssuranceCeiling)) throw new ArgumentOutOfRangeException(nameof(AssuranceCeiling));
        if (NotBeforeUtcTicks < 0 || ExpiresUtcTicks <= NotBeforeUtcTicks)
            throw new ArgumentOutOfRangeException(nameof(ExpiresUtcTicks), "Validity interval is not canonical.");
        return this with { Envelope = Envelope.Canonicalize() };
    }

    public static bool IsSubset(ResourceUseConstraintV1 child, ResourceUseConstraintV1 parent)
    {
        try
        {
            child = child.Canonicalize();
            parent = parent.Canonicalize();
            return child.Version == parent.Version && ResourceEnvelopeV1.IsSubset(child.Envelope, parent.Envelope) &&
                   child.NotBeforeUtcTicks >= parent.NotBeforeUtcTicks && child.ExpiresUtcTicks <= parent.ExpiresUtcTicks &&
                   child.AssuranceCeiling <= parent.AssuranceCeiling && child.DelegationDepth <= parent.DelegationDepth;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        {
            return false;
        }
    }
}
