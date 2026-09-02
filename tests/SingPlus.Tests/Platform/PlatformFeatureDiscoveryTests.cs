using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformFeatureDiscoveryTests
{
    [Fact]
    [Trait("Category", "Runtime")]
    public void HostProviderPublishesTypedVersionedSemanticFeatures()
    {
        var provider = new HostPlatformAuthorityProvider();
        var manifest = provider.QueryFeatures();

        Assert.True(manifest.Supports(
            PlatformFeatureFamily.NeutralDomains,
            1,
            PlatformFeatureAvailability.RuntimeAdmission));
        Assert.True(manifest.Supports(
            PlatformFeatureFamily.OwnedRegionMapping,
            1,
            PlatformFeatureAvailability.RuntimeAdmission));

        var unsupported = manifest.Resolve(PlatformFeatureFamily.VirtualizationDomains);
        Assert.Equal(0u, unsupported.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.Unavailable, unsupported.Availability);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void AvailabilityClassesAreExactEvidenceClassesNotNumericPrivilegeLevels()
    {
        var provider = new HostPlatformAuthorityProvider(
            additionalFeatures:
            [
                new PlatformFeatureDescriptor(
                    PlatformFeatureFamily.VirtualizationDomains,
                    1,
                    PlatformFeatureAvailability.ProjectionOnly),
                new PlatformFeatureDescriptor(
                    PlatformFeatureFamily.SecureDomains,
                    1,
                    PlatformFeatureAvailability.ModelOnly)
            ]);

        var manifest = provider.QueryFeatures();

        Assert.True(manifest.Supports(
            PlatformFeatureFamily.VirtualizationDomains,
            1,
            PlatformFeatureAvailability.ProjectionOnly));
        Assert.False(manifest.Supports(
            PlatformFeatureFamily.VirtualizationDomains,
            1,
            PlatformFeatureAvailability.Executable));
        Assert.True(manifest.Supports(
            PlatformFeatureFamily.SecureDomains,
            1,
            PlatformFeatureAvailability.ModelOnly));
        Assert.False(manifest.Supports(
            PlatformFeatureFamily.SecureDomains,
            1,
            PlatformFeatureAvailability.ProductionSecure));
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void UnsupportedDiscoveryIsDistinctFromAuthorityDenial()
    {
        var provider = new HostPlatformAuthorityProvider(
            PlatformAuthorityFeatures.NeutralDomainBinding);

        var unsupported = provider.QueryFeatures().Resolve(
            PlatformFeatureFamily.OwnedRegionMapping);
        var denied = provider.BindDomain(
            new PlatformDomainIdentity(new DomainId(7), 0));

        Assert.Equal(PlatformFeatureAvailability.Unavailable, unsupported.Availability);
        Assert.Equal(0u, unsupported.ContractVersion);
        Assert.False(denied.IsSuccess);
        Assert.Equal(PlatformAuthorityStatus.Denied, denied.Status);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ManifestRejectsZeroVersionUnavailableAndDuplicateFamilies()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PlatformFeatureManifest(
            [
                new PlatformFeatureDescriptor(
                    PlatformFeatureFamily.NeutralDomains,
                    0,
                    PlatformFeatureAvailability.RuntimeAdmission)
            ]));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PlatformFeatureManifest(
            [
                new PlatformFeatureDescriptor(
                    PlatformFeatureFamily.NeutralDomains,
                    1,
                    PlatformFeatureAvailability.Unavailable)
            ]));

        Assert.Throws<ArgumentException>(() =>
            new PlatformFeatureManifest(
            [
                new PlatformFeatureDescriptor(
                    PlatformFeatureFamily.NeutralDomains,
                    1,
                    PlatformFeatureAvailability.RuntimeAdmission),
                new PlatformFeatureDescriptor(
                    PlatformFeatureFamily.NeutralDomains,
                    2,
                    PlatformFeatureAvailability.Executable)
            ]));
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void LegacyV1ProviderGetsSemanticDiscoveryWithoutNewAuthorityCalls()
    {
        var kernel = new RuntimeKernel(new LegacyV1Provider());

        var manifest = kernel.QueryPlatformFeatures();

        Assert.True(manifest.Supports(
            PlatformFeatureFamily.NeutralDomains,
            1,
            PlatformFeatureAvailability.RuntimeAdmission));
        Assert.Equal(
            PlatformFeatureAvailability.Unavailable,
            manifest.Resolve(PlatformFeatureFamily.OwnedRegionMapping).Availability);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void FeaturePresenceNeverBypassesLocalCapabilityValidation()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var region = kernel.AllocateRegion(owner, 123).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;

        Assert.True(kernel.QueryPlatformFeatures().Supports(
            PlatformFeatureFamily.OwnedRegionMapping,
            1,
            PlatformFeatureAvailability.RuntimeAdmission));

        var mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            new CapabilityId(999),
            region.Handle,
            PlatformMemoryAccess.Read);

        Assert.False(mapping.IsSuccess);
        Assert.Equal(KernelError.CapabilityNotFound, mapping.Error);
        Assert.Equal(0, provider.MapOwnedRegionCallCount);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void KernelWithoutProviderReportsAllSemanticFeaturesUnavailable()
    {
        var kernel = new RuntimeKernel();

        var manifest = kernel.QueryPlatformFeatures();

        Assert.Empty(manifest.Features);
        Assert.Equal(
            PlatformFeatureAvailability.Unavailable,
            manifest.Resolve(PlatformFeatureFamily.NeutralDomains).Availability);
    }

    private sealed class LegacyV1Provider : IPlatformAuthorityProvider
    {
        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("legacy-v1"),
            2,
            PlatformAuthorityFeatures.NeutralDomainBinding);

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject) =>
            throw new InvalidOperationException("Feature discovery must not invoke authority operations.");

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) =>
            throw new InvalidOperationException("Feature discovery must not invoke authority operations.");

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access) =>
            throw new InvalidOperationException("Feature discovery must not invoke authority operations.");

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) =>
            throw new InvalidOperationException("Feature discovery must not invoke authority operations.");
    }
}
