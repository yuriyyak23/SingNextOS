using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformFeatureDiscoveryTests
{
    [Fact]
    [Trait("Category", "Runtime")]
    public void HostProviderPublishesTypedVersionedSemanticFeatures()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        PlatformFeatureManifest manifest = provider.QueryFeatures();

        Assert.True(manifest.Supports(
            PlatformFeatureFamily.NeutralDomains,
            PlatformDomainContract.ContractVersion,
            PlatformFeatureAvailability.RuntimeAdmission));
        Assert.Equal(
            PlatformDomainContract.ContractVersion,
            manifest.Resolve(PlatformFeatureFamily.NeutralDomains).ContractVersion);
        Assert.True(manifest.Supports(
            PlatformFeatureFamily.OwnedRegionMapping,
            1,
            PlatformFeatureAvailability.RuntimeAdmission));
        Assert.True(manifest.Supports(
            PlatformFeatureFamily.ExecutionPolicy,
            PlatformExecutionPolicyContract.ContractVersion,
            PlatformFeatureAvailability.ModelOnly));
        Assert.False(manifest.Supports(
            PlatformFeatureFamily.ExecutionPolicy,
            PlatformExecutionPolicyContract.ContractVersion,
            PlatformFeatureAvailability.Executable));

        PlatformFeatureDescriptor virtualization = manifest.Resolve(PlatformFeatureFamily.VirtualizationDomains);
        Assert.Equal(PlatformVirtualizationContract.ContractVersion, virtualization.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.ModelOnly, virtualization.Availability);
        PlatformFeatureDescriptor nested = manifest.Resolve(PlatformFeatureFamily.NestedDomains);
        Assert.Equal(PlatformFeatureAvailability.Unavailable, nested.Availability);
        Assert.Equal(0u, nested.ContractVersion);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void AvailabilityClassesAreExactEvidenceClassesNotNumericPrivilegeLevels()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider(
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

        PlatformFeatureManifest manifest = provider.QueryFeatures();

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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider(
            PlatformAuthorityFeatures.NeutralDomainBinding);

        PlatformFeatureDescriptor unsupported = provider.QueryFeatures().Resolve(
            PlatformFeatureFamily.OwnedRegionMapping);
        PlatformAuthorityResult<PlatformProviderDomainLease> denied = provider.BindDomain(
            new PlatformDomainIdentity(
                new DomainId(7),
                new ProcessHandle(new ProcessId(8), 0)));

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
    public void BareLegacyFeatureBitsRemainV1AndAreRejectedBeforeAuthorityCall()
    {
        RuntimeKernel kernel = new RuntimeKernel(new LegacyV1Provider());
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 8, 80);

        PlatformFeatureManifest manifest = kernel.QueryPlatformFeatures();
        KernelResult<PlatformDomainBinding> bind = kernel.BindPlatformDomain(owner);

        Assert.True(manifest.Supports(
            PlatformFeatureFamily.NeutralDomains,
            1,
            PlatformFeatureAvailability.RuntimeAdmission));
        Assert.Equal(
            1u,
            manifest.Resolve(PlatformFeatureFamily.NeutralDomains).ContractVersion);
        Assert.Equal(
            PlatformFeatureAvailability.Unavailable,
            manifest.Resolve(PlatformFeatureFamily.OwnedRegionMapping).Availability);
        Assert.Equal(
            PlatformFeatureAvailability.Unavailable,
            manifest.Resolve(PlatformFeatureFamily.ExecutionPolicy).Availability);
        Assert.False(bind.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, bind.Error);
    }

    [Theory]
    [Trait("Category", "Runtime")]
    [InlineData(PlatformFeatureAvailability.ModelOnly)]
    [InlineData(PlatformFeatureAvailability.ProjectionOnly)]
    [InlineData(PlatformFeatureAvailability.ProductionSecure)]
    public void NonAdmissionAvailabilityCannotMaterializeDomainAuthority(
        PlatformFeatureAvailability availability)
    {
        NonAdmissionDomainProvider provider = new NonAdmissionDomainProvider(availability);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 9, 90);

        KernelResult<PlatformDomainBinding> bind = kernel.BindPlatformDomain(owner);

        Assert.False(bind.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, bind.Error);
        Assert.Equal(0, provider.BindCalls);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void FeaturePresenceNeverBypassesLocalCapabilityValidation()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 123).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;

        Assert.True(kernel.QueryPlatformFeatures().Supports(
            PlatformFeatureFamily.OwnedRegionMapping,
            1,
            PlatformFeatureAvailability.RuntimeAdmission));

        KernelResult<PlatformRegionMapping> mapping = kernel.MapPlatformOwnedRegion(
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
        RuntimeKernel kernel = new RuntimeKernel();

        PlatformFeatureManifest manifest = kernel.QueryPlatformFeatures();

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

    private sealed class NonAdmissionDomainProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider
    {
        private readonly PlatformFeatureManifest _manifest;

        public NonAdmissionDomainProvider(PlatformFeatureAvailability availability)
        {
            _manifest = new PlatformFeatureManifest(new[]
            {
                new PlatformFeatureDescriptor(
                    PlatformFeatureFamily.NeutralDomains,
                    PlatformDomainContract.ContractVersion,
                    availability),
            });
        }

        public int BindCalls { get; private set; }

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("non-admission-domain"),
            PlatformDomainContract.ContractVersion,
            PlatformAuthorityFeatures.NeutralDomainBinding);

        public PlatformFeatureManifest QueryFeatures() => _manifest;

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject)
        {
            BindCalls++;
            throw new InvalidOperationException("Non-admission evidence must not invoke authority operations.");
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) =>
            throw new InvalidOperationException("Non-admission evidence must not invoke authority operations.");

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access) =>
            throw new InvalidOperationException("Non-admission evidence must not invoke authority operations.");

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) =>
            throw new InvalidOperationException("Non-admission evidence must not invoke authority operations.");
    }
}
