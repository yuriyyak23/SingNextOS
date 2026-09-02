namespace SingPlus.Contracts;

public static class CapabilityResourceIds
{
    public static string MemoryRegion(RegionId regionId) =>
        $"memory-region:{regionId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
