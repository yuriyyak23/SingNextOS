using System.Reflection;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class HybridCpuFeatureTranslationTests
{
    [Fact]
    public void ProviderTranslatesMixedFamiliesIndependentlyAndNeverExecutesDma()
    {
        var runtime = FeatureRuntimeProxy.Create(
            NeutralRuntimeImplementationProfile.ExecutableAdapter,
            new NeutralRuntimeFeatureManifest(
            [
                new(NeutralRuntimeFeatureFamily.DomainLifecycle, 1, NeutralRuntimeFeatureAvailability.Executable),
                new(NeutralRuntimeFeatureFamily.OwnedRegionMapping, 1, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
                new(NeutralRuntimeFeatureFamily.DmaAdmission, 1, NeutralRuntimeFeatureAvailability.Executable),
            ]));

        var features = new HybridCpuPlatformAuthorityProvider(runtime).QueryFeatures();
        Assert.Equal(PlatformFeatureAvailability.Executable,
            features.Resolve(PlatformFeatureFamily.NeutralDomains).Availability);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission,
            features.Resolve(PlatformFeatureFamily.OwnedRegionMapping).Availability);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission,
            features.Resolve(PlatformFeatureFamily.DmaMapping).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            features.Resolve(PlatformFeatureFamily.MmioMapping).Availability);
    }

    [Fact]
    public void DowngradeOrOldFamilyVersionRemovesStaleExecutableClaim()
    {
        var executable = FeatureRuntimeProxy.Create(
            NeutralRuntimeImplementationProfile.ExecutableAdapter,
            new NeutralRuntimeFeatureManifest(
                [new(NeutralRuntimeFeatureFamily.DomainLifecycle, 1, NeutralRuntimeFeatureAvailability.Executable)]));
        var downgraded = FeatureRuntimeProxy.Create(
            NeutralRuntimeImplementationProfile.ExecutableAdapter,
            NeutralRuntimeFeatureManifest.Empty);

        Assert.Equal(PlatformFeatureAvailability.Executable,
            new HybridCpuPlatformAuthorityProvider(executable).QueryFeatures()
                .Resolve(PlatformFeatureFamily.NeutralDomains).Availability);
        var result = new HybridCpuPlatformAuthorityProvider(downgraded).QueryFeatures()
            .Resolve(PlatformFeatureFamily.NeutralDomains);
        Assert.Equal(PlatformFeatureAvailability.Unavailable, result.Availability);
        Assert.Equal(0u, result.ContractVersion);
    }

    [Fact]
    public void ExecutableOrdinaryDomainFeatureCannotSatisfyChildVirtualizationGate()
    {
        var runtime = FeatureRuntimeProxy.Create(
            NeutralRuntimeImplementationProfile.ExecutableAdapter,
            new NeutralRuntimeFeatureManifest(
                [new(NeutralRuntimeFeatureFamily.DomainLifecycle, 1, NeutralRuntimeFeatureAvailability.Executable)]));

        var provider = new HybridCpuPlatformAuthorityProvider(runtime);
        var features = provider.QueryFeatures();

        Assert.Equal(PlatformFeatureAvailability.Executable,
            features.Resolve(PlatformFeatureFamily.NeutralDomains).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            features.Resolve(PlatformFeatureFamily.VirtualizationDomains).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            features.Resolve(PlatformFeatureFamily.NestedDomains).Availability);
        Assert.IsNotAssignableFrom<IPlatformVirtualizationProvider>(provider);
    }

    [Fact]
    public void ModelOnlyProfileCannotPromoteDefectiveExecutableManifestClaims()
    {
        var runtime = FeatureRuntimeProxy.Create(
            NeutralRuntimeImplementationProfile.ModelOnly,
            new NeutralRuntimeFeatureManifest(
            [
                new(NeutralRuntimeFeatureFamily.DomainLifecycle, 1, NeutralRuntimeFeatureAvailability.Executable),
                new(NeutralRuntimeFeatureFamily.OwnedRegionMapping, 1, NeutralRuntimeFeatureAvailability.Executable),
            ]));

        var features = new HybridCpuPlatformAuthorityProvider(runtime).QueryFeatures();

        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission,
            features.Resolve(PlatformFeatureFamily.NeutralDomains).Availability);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission,
            features.Resolve(PlatformFeatureFamily.OwnedRegionMapping).Availability);
        Assert.DoesNotContain(features.Features,
            feature => feature.Availability == PlatformFeatureAvailability.Executable);
    }

    private class FeatureRuntimeProxy : DispatchProxy
    {
        private NeutralRuntimeImplementationProfile _profile;
        private NeutralRuntimeFeatureManifest _manifest = NeutralRuntimeFeatureManifest.Empty;

        public static INeutralDomainRuntime Create(
            NeutralRuntimeImplementationProfile profile,
            NeutralRuntimeFeatureManifest manifest)
        {
            var runtime = Create<INeutralDomainRuntime, FeatureRuntimeProxy>();
            var proxy = (FeatureRuntimeProxy)(object)runtime;
            proxy._profile = profile;
            proxy._manifest = manifest;
            return runtime;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_ImplementationProfile" => _profile,
            nameof(INeutralRuntimeFeatureProvider.QueryNeutralFeatures) => _manifest,
            _ => throw new InvalidOperationException("This feature-only test double cannot execute runtime operations."),
        };
    }
}
