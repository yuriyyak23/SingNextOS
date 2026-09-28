using System.Reflection;
using System.Security.Cryptography;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;

[Flags]
internal enum BootBackendCapabilitiesV1 : ulong
{
    None = 0,
    PciConfiguration = 1UL << 0,
    CxlMailbox = 1UL << 1,
    TemporaryDecoder = 1UL << 2,
    MonotonicClock = 1UL << 3,
    Watchdog = 1UL << 4,
    ProtectedStore = 1UL << 5,
    LocalRecovery = 1UL << 6,
    DmaIsolation = 1UL << 7,
    Iommu = 1UL << 8,
    PersistentCapacity = 1UL << 9
}

internal enum BootBackendClaimV1 { Unsupported, ModelOnly, ExecutableAdapter, Hardware }
internal enum BootBackendOperationV1 { ReadPciConfiguration, CxlMailbox, CreateTemporaryDecoder, ReadMonotonicClock, ArmWatchdog, ReadProtectedState, ReadLocalRecovery, ConfigureDmaIsolation, ReadPersistentCapacity }
internal enum BootBackendOpenStatusV1 { Ready, Unsupported, VersionMismatch, UnsupportedCapability, MissingRequiredCapability, ClaimNotQualified }

internal sealed record BootBackendDescriptorV1(
    ushort MajorVersion,
    ushort MinorVersion,
    string BackendId,
    BootBackendCapabilitiesV1 Capabilities,
    BootBackendClaimV1 Claim);

internal readonly record struct BootBackendOpenResultV1(BootBackendOpenStatusV1 Status, string Detail)
{
    public bool IsReady => Status == BootBackendOpenStatusV1.Ready;
}

internal interface IBootPlatformBackendV1
{
    BootBackendDescriptorV1 Describe();
    BootBackendOpenResultV1 Open(BootBackendOperationV1 operation);
}

internal sealed class UnsupportedBootPlatformBackendV1 : IBootPlatformBackendV1
{
    public BootBackendDescriptorV1 Describe() => new(1, 0, "unsupported", BootBackendCapabilitiesV1.None, BootBackendClaimV1.Unsupported);
    public BootBackendOpenResultV1 Open(BootBackendOperationV1 operation) =>
        new(BootBackendOpenStatusV1.Unsupported, $"Boot backend operation {operation} is not available.");
}

internal static class BootBackendQualificationV1
{
    public const BootBackendCapabilitiesV1 ProductionRequired =
        BootBackendCapabilitiesV1.PciConfiguration | BootBackendCapabilitiesV1.CxlMailbox |
        BootBackendCapabilitiesV1.TemporaryDecoder | BootBackendCapabilitiesV1.MonotonicClock |
        BootBackendCapabilitiesV1.Watchdog | BootBackendCapabilitiesV1.ProtectedStore |
        BootBackendCapabilitiesV1.LocalRecovery | BootBackendCapabilitiesV1.DmaIsolation |
        BootBackendCapabilitiesV1.Iommu | BootBackendCapabilitiesV1.PersistentCapacity;

    public static BootBackendOpenResultV1 ValidateProductionDescriptor(BootBackendDescriptorV1 descriptor)
    {
        if (descriptor.MajorVersion != 1 || descriptor.MinorVersion != 0)
            return new(BootBackendOpenStatusV1.VersionMismatch, "Backend version is unsupported.");
        if ((descriptor.Capabilities & ~ProductionRequired) != 0)
            return new(BootBackendOpenStatusV1.UnsupportedCapability, "Backend declares unknown v1 capability bits.");
        if (string.IsNullOrWhiteSpace(descriptor.BackendId) || (descriptor.Capabilities & ProductionRequired) != ProductionRequired)
            return new(BootBackendOpenStatusV1.MissingRequiredCapability, "Backend lacks a required production capability.");
        return new(BootBackendOpenStatusV1.ClaimNotQualified,
            descriptor.Claim == BootBackendClaimV1.Hardware
                ? "A self-declared hardware descriptor is insufficient; independent hardware qualification evidence is required."
                : "Only an independently qualified hardware backend can open the production profile.");
    }
}

internal sealed record LocalAdapterArtifactEvidence(string AssemblyName, long Length, string Sha384, IReadOnlyList<string> References);

internal static class LocalAdapterArtifactQualifier
{
    public static LocalAdapterArtifactEvidence Qualify(Assembly assembly)
    {
        var path = assembly.Location;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new InvalidOperationException("Adapter assembly has no local artifact.");
        var bytes = File.ReadAllBytes(path);
        return new(assembly.GetName().Name ?? string.Empty, bytes.LongLength, Convert.ToHexString(SHA384.HashData(bytes)),
            assembly.GetReferencedAssemblies().Select(static x => x.Name ?? string.Empty).Order(StringComparer.Ordinal).ToArray());
    }
}
