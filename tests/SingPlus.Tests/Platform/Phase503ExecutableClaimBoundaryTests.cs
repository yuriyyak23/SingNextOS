using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class Phase503ExecutableClaimBoundaryTests
{
    [Fact]
    public void ExecutableChildLifecycleWithoutArtifactAdmissionDoesNotEnterEntrylessChildPath()
    {
        var provider = new ExecutableWithoutArtifactProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 10, 100);
        CapabilityId create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;

        KernelResult<VirtualDomainAuthoritySet> result =
            kernel.CreateVirtualDomain(owner, create, new VirtualDomainProfile(1, 4096));

        Assert.False(result.IsSuccess);
        Assert.Equal(0, provider.BindDomainCalls);
        Assert.Equal(0, provider.CreateChildCalls);
    }

    private sealed class ExecutableWithoutArtifactProvider : IPlatformAuthorityProvider, IPlatformFeatureProvider,
        IPlatformChildDomainProvider
    {
        public int BindDomainCalls { get; private set; }
        public int CreateChildCalls { get; private set; }

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("test.executable-without-artifact"),
            2,
            PlatformAuthorityFeatures.NeutralDomainBinding);

        public PlatformFeatureManifest QueryFeatures() => new(
        [
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.ChildDomainLifecycle,
                PlatformChildDomainContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
        ]);

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
        {
            BindDomainCalls++;
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Entryless executable admission must not bind a parent domain.");
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) =>
            PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Unsupported, "Not used by this boundary test.");

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access) =>
            PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Not used by this boundary test.");

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) =>
            PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Unsupported, "Not used by this boundary test.");

        public PlatformAuthorityResult<PlatformProviderChildDomainLease> CreateChildDomain(
            PlatformProviderDomainLease parentLease,
            PlatformChildDomainIntent intent)
        {
            CreateChildCalls++;
            return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Entryless executable child creation is forbidden.");
        }

        public PlatformAuthorityResult TransitionChildDomain(
            PlatformProviderChildDomainLease lease,
            PlatformChildDomainTransition transition) =>
            PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Unsupported, "Not used by this boundary test.");

        public PlatformAuthorityResult<PlatformChildDomainClosureReceipt> CloseChildDomain(
            PlatformProviderChildDomainLease lease) =>
            PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Not used by this boundary test.");
    }
}
