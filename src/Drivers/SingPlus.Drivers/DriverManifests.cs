using SingPlus.Contracts;

namespace SingPlus.Drivers;

public static class DriverManifests
{
    public static DriverManifestV1 Console { get; } = new(
        "SingPlus.Driver.Console",
        new[] { new CapabilityRequirementV1(ResourceKind.Device, "console", CapabilityRights.Write | CapabilityRights.Configure) });

    public static DriverManifestV1 Timer { get; } = new(
        "SingPlus.Driver.Timer",
        new[] { new CapabilityRequirementV1(ResourceKind.KernelService, "timer", CapabilityRights.Read | CapabilityRights.Signal) });
}
