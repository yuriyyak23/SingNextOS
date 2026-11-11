using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformBackendResetEpochTests
{
    [Fact]
    [Trait("Category", "Runtime")]
    public void TerminalBackendEpochStillQuarantinesOldAuthorityAndDeniesNewBinding()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 1203, 12030);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        CapabilityId capability = kernel.MintCapability(process.DomainId, owner,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        PlatformRegionMapping mapping = kernel.MapPlatformOwnedRegion(owner, binding, capability,
            region.Handle, PlatformMemoryAccess.Read).Value!;
        CapabilityId createCapability = kernel.MintCapability(process.DomainId, owner,
            ResourceKind.Virtualization, VirtualizationResourceIds.Create,
            CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet virtualDomain = kernel.CreateVirtualDomain(owner, createCapability,
            new VirtualDomainProfile(2, 16 * 1024 * 1024)).Value!;
        FieldInfo field = typeof(PlatformAuthorityBridge).GetField("_backendEpoch",
            global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!;
        field.SetValue(kernel.PlatformAuthority, ulong.MaxValue);

        KernelResult<PlatformBackendResetSnapshot> reset = kernel.ObservePlatformBackendReset();

        Assert.Equal(KernelError.CapacityExhausted, reset.Error);
        Assert.Equal(ulong.MaxValue, kernel.PlatformAuthority.BackendEpoch.Value);
        Assert.Equal(KernelError.StaleGeneration,
            kernel.RevokePlatformDomain(owner, binding).Error);
        Assert.Equal(KernelError.StaleGeneration,
            kernel.RevokePlatformRegionMapping(owner, mapping).Error);
        Assert.Equal(ReclaimExternalState.Quarantined,
            kernel.QueryPlatformAuthorityDiagnostics(owner).Value!.DomainState);
        Assert.Equal(VirtualDomainState.Quarantined,
            kernel.QueryVirtualDomain(owner, virtualDomain.Domain).Value);
        (SingProcess _, ProcessHandle other) = TestFixtures.Create(kernel, 1204, 12040);
        Assert.Equal(KernelError.CapacityExhausted,
            kernel.BindPlatformDomain(other).Error);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ResetMakesExistingDomainAndMappingStaleAndKeepsReservationPinned()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 1201, 12010);
        OwnedRegion<int> region = kernel.AllocateRegion(owner, 4096).Value!;
        CapabilityId capability = kernel.MintCapability(
            process.DomainId,
            owner,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        PlatformRegionMapping mapping = kernel.MapPlatformOwnedRegion(
            owner,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read).Value!;
        PlatformBackendEpoch previousEpoch = kernel.PlatformAuthority.BackendEpoch;

        KernelResult<PlatformBackendResetSnapshot> reset = kernel.ObservePlatformBackendReset();

        Assert.True(reset.IsSuccess, reset.Message);
        Assert.Equal(previousEpoch, reset.Value!.PreviousEpoch);
        Assert.Equal(previousEpoch.Value + 1, reset.Value.CurrentEpoch.Value);
        Assert.Equal(1, reset.Value.QuarantinedDomains);
        Assert.Equal(1, reset.Value.FaultPinnedMappings);
        Assert.Equal(0, provider.RevokeDomainCallCount);
        Assert.Equal(0, provider.RevokeRegionMappingCallCount);

        KernelResult mappingClose = kernel.RevokePlatformRegionMapping(owner, mapping);
        Assert.Equal(KernelError.StaleGeneration, mappingClose.Error);
        KernelResult domainClose = kernel.RevokePlatformDomain(owner, binding);
        Assert.Equal(KernelError.StaleGeneration, domainClose.Error);

        KernelResult<PlatformAuthorityDiagnosticSnapshot> diagnostics = kernel.QueryPlatformAuthorityDiagnostics(owner);
        Assert.True(diagnostics.IsSuccess, diagnostics.Message);
        Assert.Equal(ReclaimExternalState.Quarantined, diagnostics.Value!.DomainState);
        PlatformResourceDiagnosticSnapshot mappedResource = Assert.Single(
            diagnostics.Value.Resources,
            resource => resource.Kind == ReclaimDependencyKind.PlatformRegionMapping);
        Assert.Equal(ReclaimExternalState.Quarantined, mappedResource.ExternalState);
        Assert.True(mappedResource.ReclaimBlocking);

        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 1202, 12020);
        KernelResult<OwnedRegion<int>> transfer = kernel.TransferRegion(owner, target, region);
        Assert.Equal(KernelError.PlatformBindingActive, transfer.Error);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ResetQuarantinesLocalVirtualDomainAndStalesItsPlatformBinding()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 1211, 12110);
        CapabilityId createCapability = kernel.MintCapability(
            process.DomainId,
            owner,
            ResourceKind.Virtualization,
            VirtualizationResourceIds.Create,
            CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet authority = kernel.CreateVirtualDomain(
            owner,
            createCapability,
            new VirtualDomainProfile(2, 16 * 1024 * 1024)).Value!;

        KernelResult<PlatformBackendResetSnapshot> reset = kernel.ObservePlatformBackendReset();

        Assert.True(reset.IsSuccess, reset.Message);
        Assert.Equal(1, reset.Value!.InvalidatedVirtualDomains);
        Assert.Equal(
            VirtualDomainState.Quarantined,
            kernel.QueryVirtualDomain(owner, authority.Domain).Value);

        KernelResult destroy = kernel.DestroyVirtualDomain(
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
        RuntimeKernel kernel = new RuntimeKernel();
        PlatformBackendEpoch before = kernel.PlatformAuthority.BackendEpoch;

        KernelResult<PlatformBackendResetSnapshot> reset = kernel.ObservePlatformBackendReset();

        Assert.Equal(KernelError.PlatformUnavailable, reset.Error);
        Assert.Equal(before, kernel.PlatformAuthority.BackendEpoch);
    }
}
