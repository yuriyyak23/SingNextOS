using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU_NeutralRuntime.Tests;

public sealed class NeutralRuntimeFeatureManifestTests
{
    [Fact]
    public void RejectsInvalidDuplicateAndVersionlessDescriptors()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NeutralRuntimeFeatureManifest(
            [new((NeutralRuntimeFeatureFamily)999, 1, NeutralRuntimeFeatureAvailability.RuntimeAdmission)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NeutralRuntimeFeatureManifest(
            [new(NeutralRuntimeFeatureFamily.DomainLifecycle, 0, NeutralRuntimeFeatureAvailability.RuntimeAdmission)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NeutralRuntimeFeatureManifest(
            [new(NeutralRuntimeFeatureFamily.DomainLifecycle, 1, NeutralRuntimeFeatureAvailability.Unavailable)]));
        Assert.Throws<ArgumentException>(() => new NeutralRuntimeFeatureManifest(
        [
            new(NeutralRuntimeFeatureFamily.DomainLifecycle, 1, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
            new(NeutralRuntimeFeatureFamily.DomainLifecycle, 1, NeutralRuntimeFeatureAvailability.Executable),
        ]));
    }

    [Fact]
    public void IsImmutableAndMissingFamilyResolvesUnavailable()
    {
        var source = new[]
        {
            new NeutralRuntimeFeatureDescriptor(
                NeutralRuntimeFeatureFamily.DomainLifecycle,
                NeutralRuntimeFeatureContracts.DomainLifecycleVersion,
                NeutralRuntimeFeatureAvailability.RuntimeAdmission),
        };
        var manifest = new NeutralRuntimeFeatureManifest(source);
        source[0] = new(NeutralRuntimeFeatureFamily.DomainLifecycle, 99, NeutralRuntimeFeatureAvailability.Executable);

        Assert.Equal(NeutralRuntimeFeatureContracts.DomainLifecycleVersion,
            manifest.Resolve(NeutralRuntimeFeatureFamily.DomainLifecycle).ContractVersion);
        Assert.Equal(NeutralRuntimeFeatureAvailability.Unavailable,
            manifest.Resolve(NeutralRuntimeFeatureFamily.MmioMapping).Availability);
        Assert.Equal(0u, manifest.Resolve(NeutralRuntimeFeatureFamily.MmioMapping).ContractVersion);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<NeutralRuntimeFeatureDescriptor>)manifest.Features).Add(default));
    }

    [Fact]
    public void ModelProfileDoesNotPromoteAnyFamilyToExecutable()
    {
        var runtime = new NeutralDomainRuntimeFacade();
        Assert.Equal(NeutralRuntimeImplementationProfile.ModelOnly, runtime.ImplementationProfile);
        Assert.All(runtime.QueryNeutralFeatures().Features, feature =>
            Assert.Equal(NeutralRuntimeFeatureAvailability.RuntimeAdmission, feature.Availability));
        Assert.Equal(NeutralRuntimeFeatureAvailability.RuntimeAdmission,
            runtime.QueryNeutralFeatures().Resolve(NeutralRuntimeFeatureFamily.DmaAdmission).Availability);
    }
}
