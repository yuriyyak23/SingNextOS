using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class Phase504ExecutableAdapterTests
{
    [Fact]
    public void ExplicitAdapterCompositionPublishesExecutableChildAndIoButNotNestedOrTrap()
    {
        var provider = new HybridCpuPlatformAuthorityProvider(
            new NeutralDomainRuntimeFacade(), new HybridCpuExecutableChildAdapter());
        PlatformFeatureManifest features = provider.QueryFeatures();

        Assert.Equal(PlatformFeatureAvailability.Executable,
            features.Resolve(PlatformFeatureFamily.VirtualizationDomains).Availability);
        Assert.Equal(PlatformFeatureAvailability.Executable,
            features.Resolve(PlatformFeatureFamily.ChildExecutableArtifact).Availability);
        Assert.Equal(PlatformFeatureAvailability.Executable,
            features.Resolve(PlatformFeatureFamily.BoundedVirtualIo).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            features.Resolve(PlatformFeatureFamily.NestedDomains).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            features.Resolve(PlatformFeatureFamily.ChildTrapDelivery).Availability);
        Assert.Equal(PlatformFeatureAvailability.Executable,
            features.Resolve(PlatformFeatureFamily.GuestMemory).Availability);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission,
            features.Resolve(PlatformFeatureFamily.VirtualEvents).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            features.Resolve(PlatformFeatureFamily.VirtualTraps).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            features.Resolve(PlatformFeatureFamily.VmxCompatibility).Availability);
        Assert.Equal(HybridCpuChildDomainAdapterReadiness.Ready,
            provider.QueryChildDomainAdapterStatus().Readiness);
    }

    [Fact]
    public void DefaultCompositionRemainsExternalBlockedAndCannotGainExecutableClaims()
    {
        var provider = new HybridCpuPlatformAuthorityProvider();
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            provider.QueryFeatures().Resolve(PlatformFeatureFamily.VirtualizationDomains).Availability);
        Assert.Equal(HybridCpuChildDomainAdapterReadiness.ExternalBlocked,
            provider.QueryChildDomainAdapterStatus().Readiness);
    }

    [Fact]
    public void ArtifactClaimWithoutBoundedIoCannotPromoteVirtualizationDomains()
    {
        var provider = new HybridCpuPlatformAuthorityProvider(
            new NeutralDomainRuntimeFacade(), new IncompleteExecutableChildProvider());

        Assert.Equal(PlatformFeatureAvailability.Executable,
            provider.QueryFeatures().Resolve(PlatformFeatureFamily.ChildExecutableArtifact).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            provider.QueryFeatures().Resolve(PlatformFeatureFamily.BoundedVirtualIo).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            provider.QueryFeatures().Resolve(PlatformFeatureFamily.VirtualizationDomains).Availability);
    }

    private sealed class IncompleteExecutableChildProvider : INeutralChildDomainProvider, INeutralRuntimeFeatureProvider
    {
        public NeutralRuntimeFeatureManifest QueryNeutralFeatures() => new(
        [
            new(NeutralRuntimeFeatureFamily.ChildExecutableArtifact,
                NeutralChildExecutionContract.ContractVersion,
                NeutralRuntimeFeatureAvailability.Executable),
        ]);

        public NeutralVirtualizationResult<NeutralChildDomainLease> CreateChildDomain(
            NeutralDomainBindingLease parentLease, NeutralChildDomainIntent intent) =>
            new(NeutralVirtualizationStatus.Unsupported, default, "test-only incomplete provider");

        public NeutralVirtualizationResult TransitionChildDomain(
            NeutralChildDomainLease lease, NeutralChildDomainTransition transition) =>
            new(NeutralVirtualizationStatus.Unsupported, "test-only incomplete provider");

        public NeutralVirtualizationResult<NeutralChildDomainCloseReceipt> CloseChildDomain(
            NeutralChildDomainLease lease) =>
            new(NeutralVirtualizationStatus.Unsupported, default, "test-only incomplete provider");
    }
}
