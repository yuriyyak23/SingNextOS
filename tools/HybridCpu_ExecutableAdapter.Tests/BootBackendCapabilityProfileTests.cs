using YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;
using Xunit;

namespace HybridCpu_ExecutableAdapter.Tests;

public sealed class BootBackendCapabilityProfileTests
{
    [Fact]
    public void UnsupportedBackendFailsClosedForEveryOperation()
    {
        IBootPlatformBackendV1 backend = new UnsupportedBootPlatformBackendV1();
        Assert.Equal(BootBackendClaimV1.Unsupported, backend.Describe().Claim);
        foreach (var operation in Enum.GetValues<BootBackendOperationV1>())
            Assert.Equal(BootBackendOpenStatusV1.Unsupported, backend.Open(operation).Status);
    }

    [Fact]
    public void ModelCannotSelfPromoteToProductionHardwareClaim()
    {
        var result = BootBackendQualificationV1.ValidateProductionDescriptor(new(1, 0, "deterministic-model",
            BootBackendQualificationV1.ProductionRequired, BootBackendClaimV1.ModelOnly));
        Assert.Equal(BootBackendOpenStatusV1.ClaimNotQualified, result.Status);
        var selfDeclaredHardware = BootBackendQualificationV1.ValidateProductionDescriptor(new(1, 0, "unqualified-hardware",
            BootBackendQualificationV1.ProductionRequired, BootBackendClaimV1.Hardware));
        Assert.Equal(BootBackendOpenStatusV1.ClaimNotQualified, selfDeclaredHardware.Status);
    }

    [Theory]
    [InlineData(0, (int)BootBackendOpenStatusV1.VersionMismatch)]
    [InlineData(1, (int)BootBackendOpenStatusV1.MissingRequiredCapability)]
    [InlineData(2, (int)BootBackendOpenStatusV1.VersionMismatch)]
    [InlineData(3, (int)BootBackendOpenStatusV1.UnsupportedCapability)]
    public void UnknownVersionCapabilityAndMissingCapabilityFailClosed(int kind, int expectedStatus)
    {
        var descriptor = kind switch
        {
            0 => new BootBackendDescriptorV1(2, 0, "future", BootBackendQualificationV1.ProductionRequired, BootBackendClaimV1.Hardware),
            1 => new BootBackendDescriptorV1(1, 0, "partial", BootBackendCapabilitiesV1.PciConfiguration, BootBackendClaimV1.Hardware),
            2 => new BootBackendDescriptorV1(1, 1, "future-minor", BootBackendQualificationV1.ProductionRequired, BootBackendClaimV1.Hardware),
            _ => new BootBackendDescriptorV1(1, 0, "unknown-bit", BootBackendQualificationV1.ProductionRequired | (BootBackendCapabilitiesV1)(1UL << 63), BootBackendClaimV1.Hardware)
        };
        var result = BootBackendQualificationV1.ValidateProductionDescriptor(descriptor);
        Assert.False(result.IsReady);
        Assert.Equal((BootBackendOpenStatusV1)expectedStatus, result.Status);
    }

    [Fact]
    public void LocallyBuiltAdapterArtifactIsReadableAndHasStableEvidenceShape()
    {
        var evidence = LocalAdapterArtifactQualifier.Qualify(typeof(UnsupportedBootPlatformBackendV1).Assembly);
        Assert.Equal("HybridCpu_ExecutableAdapter", evidence.AssemblyName);
        Assert.True(evidence.Length > 0);
        Assert.Equal(96, evidence.Sha384.Length);
        Assert.Contains("HybridCpu.Boot.Contracts", evidence.References);
        Assert.DoesNotContain(evidence.References, x => x.Contains("HybridCPU-v2", StringComparison.OrdinalIgnoreCase));
    }
}
