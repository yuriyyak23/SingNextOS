using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformBackendResetEpochTests
{
    [Fact]
    [Trait("Category", "Runtime")]
    public void ResetMakesExistingDomainAndMappingStaleAndKeepsReservationPinned()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 1201, 12010);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        var capability = kernel.MintCapability(
            process.DomainId,
            owner,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read).Value!;
        var previousEpoch = kernel.PlatformAuthority.BackendEpoch;

        var reset = kernel.ObservePlatformBackendReset();

        Assert.True(reset.IsSuccess, reset.Message);
        Assert.Equal(previousEpoch, reset.Value!.PreviousEpoch);
        Assert.Equal(previousEpoch.Value + 1, reset.Value.CurrentEpoch.Value);
        Assert.Equal(1, reset.Value.QuarantinedDomains);
        Assert.Equal(1, reset.Value.FaultPinnedMappings);
        Assert.Equal(0, provider.RevokeDomainCallCount);
        Assert.Equal(0, provider.RevokeRegionMappingCallCount);

        var mappingClose = kernel.RevokePlatformRegionMapping(owner, mapping);
        Assert.Equal(KernelError.StaleGeneration, mappingClose.Error);
        var domainClose = kernel.RevokePlatformDomain(owner, binding);
        Assert.Equal(KernelError.StaleGeneration, domainClose.Error);

        var diagnostics = kernel.QueryPlatformAuthorityDiagnostics(owner);
        Assert.True(diagnostics.IsSuccess, diagnostics.Message);
        Assert.Equal(ReclaimExternalState.Quarantined, diagnostics.Value!.DomainState);
        var mappedResource = Assert.Single(
            diagnostics.Value.Resources,
            resource => resource.Kind == ReclaimDependencyKind.PlatformRegionMapping);
        Assert.Equal(ReclaimExternalState.Quarantined, mappedResource.ExternalState);
        Assert.True(mappedResource.ReclaimBlocking);

        var (_, target) = TestFixtures.Create(kernel, 1202, 12020);
        var transfer = kernel.TransferRegion(owner, target, region);
        Assert.Equal(KernelError.PlatformBindingActive, transfer.Error);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ResetQuarantinesLocalVirtualDomainAndStalesItsPlatformBinding()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 1211, 12110);
        var createCapability = kernel.MintCapability(
            process.DomainId,
            owner,
            ResourceKind.Virtualization,
            VirtualizationResourceIds.Create,
            CapabilityRights.Configure).Value!.CapabilityId;
        var authority = kernel.CreateVirtualDomain(
            owner,
            createCapability,
            new VirtualDomainProfile(2, 16 * 1024 * 1024)).Value!;

        var reset = kernel.ObservePlatformBackendReset();

        Assert.True(reset.IsSuccess, reset.Message);
        Assert.Equal(1, reset.Value!.InvalidatedVirtualDomains);
        Assert.Equal(
            VirtualDomainState.Quarantined,
            kernel.QueryVirtualDomain(owner, authority.Domain).Value);

        var destroy = kernel.DestroyVirtualDomain(
            owner,
            authority.Domain,
            authority.ConfigureCapability);
        Assert.Equal(KernelError.PlatformFaulted, destroy.Error);
        Assert.Equal(
            VirtualDomainState.Quarantined,
            kernel.QueryVirtualDomain(owner, authority.Domain).Value);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ResetWithoutConfiguredProviderFailsWithoutAdvancingEpoch()
    {
        var kernel = new RuntimeKernel();
        var before = kernel.PlatformAuthority.BackendEpoch;

        var reset = kernel.ObservePlatformBackendReset();

        Assert.Equal(KernelError.PlatformUnavailable, reset.Error);
        Assert.Equal(before, kernel.PlatformAuthority.BackendEpoch);
    }
}
