using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformAuthorityBridgeChildDomainTests
{
    // Existing standalone bridge fixtures model trusted caller admission; no Kernel capability claim.
    private static KernelResult<PlatformDeviceLease> BindDeviceForModel(
        PlatformAuthorityBridge bridge, PlatformDomainBinding binding, PlatformDomainIdentity owner,
        CapabilityId capability, PlatformDeviceIdentity device, PlatformDeviceRights rights) =>
        bridge.BindDevice(binding, owner, capability, device, rights, static () => KernelResult.Ok(), static (_, commit) => commit());

    [Fact]
    public void DamageInsideMappingCallbackPreservesReceiptButRevokesLocalAuthorization()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 2, 20);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var capability = kernel.MintCapability(process.DomainId, owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var regionOwner = new RegionOwner(process.DomainId, owner.Generation);
        provider.BeforeMapOwnedRegionReturn = () => Assert.True(kernel.Regions.QuarantineSubrange(
            region.Handle, regionOwner, new(1, new("test-provider", "bank-0", 1, 1), 1,
                ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8))).IsSuccess);

        var mapped = kernel.MapPlatformOwnedRegion(owner, binding, capability, region.Handle, PlatformMemoryAccess.Read);
        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.Equal(KernelError.PlatformBindingRevoked,
            kernel.PlatformAuthority.ValidateMapping(mapped.Value!, binding.Subject).Error);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        Assert.Equal(KernelError.Quarantined, kernel.ReleaseRegion(owner, region).Error);
        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapped.Value!).IsSuccess);
        Assert.False(kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        Assert.Single(kernel.Regions.SnapshotDamage());
    }

    [Fact]
    public void ProcessExitInsideMappingCallbackTracksLateMappingForExactTeardown()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 2, 20);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var capability = kernel.MintCapability(process.DomainId, owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        provider.BeforeMapOwnedRegionReturn = () =>
            Assert.Equal(KernelError.PlatformBindingDraining, kernel.TerminateProcess(owner).Error);

        var mapped = kernel.MapPlatformOwnedRegion(owner, binding, capability, region.Handle,
            PlatformMemoryAccess.Read);

        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.Equal(1, provider.RegionRevocationBeginCalls);
        Assert.Equal(1, provider.RevokeDomainCalls);
    }

    [Fact]
    public void LostMappingReceiptBlocksProcessReclaim()
    {
        var provider = new ChildProvider
        {
            BeforeMapOwnedRegionReturn = () => throw new InvalidOperationException("Mapping receipt lost."),
        };
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 3, 30);
        var admin = TestFixtures.Create(kernel, 93, 930).Handle;
        var administration = kernel.MintCapability(new DomainId(930), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "lost-map",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var capability = kernel.MintCapability(process.DomainId, owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var binding = kernel.BindPlatformDomain(owner).Value!;

        Assert.Equal(KernelError.PlatformFaulted,
            kernel.MapPlatformOwnedRegion(owner, binding, capability, region.Handle,
                PlatformMemoryAccess.Read).Error);
        Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);
        Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle,
            new RegionOwner(process.DomainId, owner.Generation)));
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
            usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
    }

    [Fact]
    public void ResetFaultedMappingKeepsBudgetAndRegionPinnedAcrossTeardownRetries()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 4, 40);
        var admin = TestFixtures.Create(kernel, 94, 940).Handle;
        var administration = kernel.MintCapability(new DomainId(940), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "reset-map",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var capability = kernel.MintCapability(process.DomainId, owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        provider.BeforeMapOwnedRegionReturn = () =>
            Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);

        var mapped = kernel.MapPlatformOwnedRegion(owner, binding, capability, region.Handle,
            PlatformMemoryAccess.Read);

        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.Equal(KernelError.StaleGeneration,
            kernel.QueryPlatformRegionMappingLifecycle(owner, mapped.Value!).Error);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle,
            new RegionOwner(process.DomainId, owner.Generation)));
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
            usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Fact]
    public void ResetBeforeNotAcceptedMappingReplyDoesNotReleaseRegion()
    {
        var provider = new ChildProvider { MapStatus = PlatformAuthorityStatus.NotAccepted };
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 5, 50);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var capability = kernel.MintCapability(process.DomainId, owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        provider.BeforeMapOwnedRegionReturn = () =>
            Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);

        Assert.Equal(KernelError.PlatformFaulted,
            kernel.MapPlatformOwnedRegion(owner, binding, capability, region.Handle,
                PlatformMemoryAccess.Read).Error);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle,
            new RegionOwner(process.DomainId, owner.Generation)));
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
    }

    [Fact]
    public void StableNotAcceptedMappingReplyReleasesLocalReservation()
    {
        var provider = new ChildProvider { MapStatus = PlatformAuthorityStatus.NotAccepted };
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 6, 60);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var capability = kernel.MintCapability(process.DomainId, owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var binding = kernel.BindPlatformDomain(owner).Value!;

        Assert.False(kernel.MapPlatformOwnedRegion(owner, binding, capability, region.Handle,
            PlatformMemoryAccess.Read).IsSuccess);
        Assert.False(kernel.Regions.HasPlatformMappingReservation(region.Handle,
            new RegionOwner(process.DomainId, owner.Generation)));
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
    }

    [Fact]
    public void MappingCallbackCannotRevokeParentBeforePublication()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var binding = bridge.BindDomain(owner).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(1), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        provider.BeforeMapOwnedRegionReturn = () =>
            Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(binding, owner).Error);

        var mapping = bridge.MapOwnedRegion(binding, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read);

        Assert.True(mapping.IsSuccess, mapping.Message);
        Assert.Equal(1, provider.MapCalls);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(binding, owner).Error);
    }

    [Fact]
    public void MappingReceiptLossPinsParentAndLocalReservation()
    {
        var provider = new ChildProvider
        {
            BeforeMapOwnedRegionReturn = () => throw new InvalidOperationException("Mapping receipt lost."),
        };
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var binding = bridge.BindDomain(owner).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(1), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);

        var mapping = bridge.MapOwnedRegion(binding, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read, out var retainReservation);

        Assert.Equal(KernelError.PlatformFaulted, mapping.Error);
        Assert.True(retainReservation);
        Assert.Equal(KernelError.PlatformFaulted, bridge.RevokeDomain(binding, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Fact]
    public void BackendResetInsideMappingCallbackFaultPinsLateLease()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var binding = bridge.BindDomain(owner).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(1), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        var epoch = bridge.BackendEpoch;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        var originalDomain = domains[binding.BindingId];
        provider.BeforeMapOwnedRegionReturn = () =>
        {
            Assert.True(bridge.ObserveBackendReset().IsSuccess);
            Assert.NotEqual(epoch, bridge.BackendEpoch);
        };

        var mapped = bridge.MapOwnedRegion(binding, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read, out var retainReservation);

        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.False(retainReservation);
        Assert.NotEqual(epoch, bridge.BackendEpoch);
        Assert.NotSame(originalDomain, domains[binding.BindingId]);
        Assert.Equal(true, domains[binding.BindingId]!.GetType()
            .GetProperty("MappingMayHaveEffect")!.GetValue(domains[binding.BindingId]));
        var mappings = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_mappings", global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge)!;
        var record = mappings[mapped.Value!.MappingId]!;
        Assert.Equal(epoch, record.GetType().GetProperty("BackendEpoch")!.GetValue(record));
        Assert.Equal(PlatformExternalClosureState.Faulted,
            record.GetType().GetProperty("ClosureState")!.GetValue(record));
        Assert.Equal(KernelError.StaleGeneration,
            bridge.QueryRegionMappingLifecycle(mapped.Value!, owner).Error);
        Assert.Equal(KernelError.StaleGeneration, bridge.RevokeDomain(binding, owner).Error);
    }

    [Fact]
    public void ParentRevokeCallbackRejectsNewMappingBeforeProvider()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var binding = bridge.BindDomain(owner).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(1), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        provider.BeforeDomainRevoke = () =>
            Assert.Equal(KernelError.PlatformBindingActive,
                bridge.MapOwnedRegion(binding, owner, new CapabilityId(8), region,
                    PlatformMemoryAccess.Read).Error);

        Assert.True(bridge.RevokeDomain(binding, owner).IsSuccess);
        Assert.Equal(0, provider.MapCalls);
        Assert.Equal(1, provider.RevokeDomainCalls);
    }

    [Fact]
    public void ProcessExitInsideRootBindCallbackRevokesPublishedBindingBeforeReclaim()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel, 7, 70).Handle;
        provider.BeforeDomainBindReturn = () =>
            Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);

        Assert.True(kernel.BindPlatformDomain(owner).IsSuccess);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.RevokeDomainCalls);
        Assert.False(kernel.Processes.Resolve(owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetInsideRootBindCallbackPinsSubjectWithoutInventingLease(bool terminalEpoch)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        if (terminalEpoch)
            typeof(PlatformAuthorityBridge).GetField("_backendEpoch",
                global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic)!.SetValue(bridge, ulong.MaxValue);
        provider.BeforeDomainBindReturn = () =>
        {
            var reset = bridge.ObserveBackendReset();
            Assert.Equal(terminalEpoch ? KernelError.CapacityExhausted : KernelError.None, reset.Error);
        };

        Assert.Equal(KernelError.PlatformFaulted, bridge.BindDomain(owner).Error);
        Assert.True(bridge.HasUnresolvedDomainBindEffect(owner));
        Assert.False(bridge.TryGetQuarantinedDomainBinding(owner, out _));
        Assert.False(bridge.BindDomain(owner).IsSuccess);
        Assert.Equal(1, provider.DomainBindCalls);
    }

    [Fact]
    public void LostRootBindReceiptPinsProcessReclaimWithoutProviderLease()
    {
        var provider = new ChildProvider
        {
            BeforeDomainBindReturn = () => throw new InvalidOperationException("Domain materialized without lease receipt."),
        };
        var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel, 6, 60).Handle;

        Assert.Equal(KernelError.PlatformFaulted, kernel.BindPlatformDomain(owner).Error);
        Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.Equal(1, provider.DomainBindCalls);
    }

    [Fact]
    public void RootBindCallbackRejectsDuplicateAdmissionBeforeProvider()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        provider.BeforeDomainBindReturn = () => Assert.Equal(KernelError.PlatformBindingActive,
            bridge.BindDomain(owner).Error);

        Assert.True(bridge.BindDomain(owner).IsSuccess);
        Assert.Equal(1, provider.DomainBindCalls);
    }

    [Fact]
    public void RuntimeKernelUsesChildLedgerWithoutPublishingProviderLeaseAuthority()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel, 4, 40).Handle;
        CapabilityId create = kernel.MintCapability(new DomainId(40), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;

        VirtualDomainAuthoritySet authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);
        Assert.True(kernel.StartVirtualDomain(owner, authority.Domain, authority.ExecuteCapability).IsSuccess);
        KernelEventEndpoint endpoint = kernel.CreateKernelEventEndpoint(owner).Value!;
        Assert.True(kernel.InjectVirtualEvent(owner, authority.Domain, authority.EventCapability, endpoint).IsSuccess);
        Assert.True(kernel.ParkVirtualDomain(owner, authority.Domain, authority.ExecuteCapability).IsSuccess);
        Assert.True(kernel.DestroyVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);

        Assert.Equal(3, provider.TransitionCalls);
        Assert.Equal(1, provider.CloseCalls);
        Assert.Equal(KernelError.VirtualDomainNotFound, kernel.QueryVirtualDomain(owner, authority.Domain).Error);
    }

    [Fact]
    public void RuntimeKernelPinsVirtualAuthorityAfterAmbiguousChildClose()
    {
        var provider = new ChildProvider { MalformedClose = true };
        var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel, 5, 50).Handle;
        CapabilityId create = kernel.MintCapability(new DomainId(50), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);

        Assert.Equal(KernelError.PlatformFaulted,
            kernel.DestroyVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).Error);
        Assert.Equal(VirtualDomainState.Quarantined, kernel.QueryVirtualDomain(owner, authority.Domain).Value);
        Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
    }

    [Fact]
    public void ChildLedgerKeepsLocalGenerationDistinctAndRejectsWrongOwnerBeforeProvider()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        PlatformChildBinding child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;

        var stale = child with { Generation = new(child.Generation.Value + 1) };
        var wrongOwner = child with { Owner = new ProcessHandle(new ProcessId(99), 1) };

        Assert.NotEqual(provider.LastChildLease.Generation.Value, child.Generation.Value);
        Assert.Equal(KernelError.StaleGeneration,
            bridge.TransitionChildDomain(stale, PlatformChildDomainTransition.Start).Error);
        Assert.Equal(KernelError.WrongPlatformDomain,
            bridge.TransitionChildDomain(wrongOwner, PlatformChildDomainTransition.Start).Error);
        Assert.Equal(0, provider.TransitionCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildTransitionCallbackFaultOrResetCannotPublishLateState(bool reset)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        provider.BeforeChildTransition = reset
            ? () => Assert.True(bridge.ObserveBackendReset().IsSuccess)
            : () => throw new InvalidOperationException("Transition receipt lost after callback.");

        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Start).Error);
        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Start).Error);
        Assert.Equal(1, provider.TransitionCalls);
        Assert.False(bridge.CloseChildDomain(child).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentChildTransitionHasOneProviderCallback()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        provider.BeforeChildTransition = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        var first = Task.Run(() => bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Start));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingActive,
                bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Start).Error);
            Assert.Equal(KernelError.PlatformBindingActive, bridge.CloseChildDomain(child).Error);
        }
        finally { release.Set(); }
        Assert.True((await first).IsSuccess);
        Assert.Equal(1, provider.TransitionCalls);
    }

    [Fact]
    public void TransitionCallbackRejectsGuestMapEventAndTrapBeforeProvider()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        var parentMapping = bridge.MapOwnedRegion(parent, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read).Value!;
        provider.BeforeChildTransition = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive,
                bridge.MapChildGuestRegion(child, parentMapping, new(0, 4096),
                    PlatformGuestMemoryAccess.Read).Error);
            Assert.Equal(KernelError.PlatformBindingActive,
                bridge.InjectChildEvent(child, PlatformVirtualEventClass.Timer, "timer:0").Error);
            Assert.Equal(KernelError.PlatformBindingActive, bridge.ObserveChildTrap(child).Error);
        };

        Assert.True(bridge.TransitionChildDomain(child,
            PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.Equal(0, provider.GuestMapCalls);
        Assert.Equal(0, provider.EventCalls);
        Assert.Equal(0, provider.TrapCalls);
    }

    [Fact]
    public void VirtualIoBindCallbackRejectsOverlappingChildTransition()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var device = BindDeviceForModel(bridge, parent, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        provider.AfterVirtualIoBind = () => Assert.Equal(KernelError.PlatformBindingActive,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);

        Assert.True(bridge.BindChildVirtualIo(child, device,
            new(PlatformDeviceRights.Read, 512)).IsSuccess);
        Assert.Equal(0, provider.TransitionCalls);
    }

    [Fact]
    public void ChildTransitionCallbackRejectsVirtualIoBindBeforeProvider()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var device = BindDeviceForModel(bridge, parent, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        provider.AfterVirtualIoBind = () => Assert.Fail("Virtual-I/O provider callback must not run.");
        provider.BeforeChildTransition = () => Assert.Equal(KernelError.PlatformBindingActive,
            bridge.BindChildVirtualIo(child, device,
                new(PlatformDeviceRights.Read, 512)).Error);

        Assert.True(bridge.TransitionChildDomain(child,
            PlatformChildDomainTransition.BeginDrain).IsSuccess);
    }

    [Theory]
    [InlineData("guest-map")]
    [InlineData("event")]
    [InlineData("trap")]
    public void ChildEffectCallbackRejectsOverlappingTransition(string effect)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        Action probe = () => Assert.Equal(KernelError.PlatformBindingActive,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
        if (effect == "guest-map")
        {
            var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
                new(new DomainId(1), owner.ProcessGeneration), 4096);
            var parentMapping = bridge.MapOwnedRegion(parent, owner, new CapabilityId(8), region,
                PlatformMemoryAccess.Read).Value!;
            provider.AfterGuestMap = probe;
            Assert.True(bridge.MapChildGuestRegion(child, parentMapping, new(0, 4096),
                PlatformGuestMemoryAccess.Read).IsSuccess);
        }
        else if (effect == "event")
        {
            provider.BeforeChildEvent = probe;
            Assert.True(bridge.InjectChildEvent(child, PlatformVirtualEventClass.Timer, "timer:0").IsSuccess);
        }
        else
        {
            provider.BeforeChildTrap = probe;
            Assert.True(bridge.ObserveChildTrap(child).IsSuccess);
        }
        Assert.Equal(0, provider.TransitionCalls);
        Assert.True(bridge.TransitionChildDomain(child,
            PlatformChildDomainTransition.BeginDrain).IsSuccess);
    }

    [Theory]
    [InlineData("event", false)]
    [InlineData("event", true)]
    [InlineData("trap", false)]
    [InlineData("trap", true)]
    public void VirtualEffectCallbackFaultOrResetCannotPublishLateEvidence(string effect, bool reset)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        Action fault = reset
            ? () => Assert.True(bridge.ObserveBackendReset().IsSuccess)
            : () => throw new InvalidOperationException("Virtual effect receipt lost.");
        if (effect == "event")
        {
            provider.BeforeChildEvent = fault;
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.InjectChildEvent(child, PlatformVirtualEventClass.Timer, "timer:0").Error);
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.InjectChildEvent(child, PlatformVirtualEventClass.Timer, "timer:0").Error);
            Assert.Equal(1, provider.EventCalls);
        }
        else
        {
            provider.BeforeChildTrap = fault;
            Assert.Equal(KernelError.PlatformFaulted, bridge.ObserveChildTrap(child).Error);
            Assert.Equal(KernelError.PlatformFaulted, bridge.ObserveChildTrap(child).Error);
            Assert.Equal(1, provider.TrapCalls);
        }
    }

    [Fact]
    public void EventAndTrapEvidenceAreSequencedAndNeverCloseChildAuthority()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        PlatformChildBinding child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Start).IsSuccess);

        Assert.True(bridge.InjectChildEvent(child, PlatformVirtualEventClass.Timer, "timer:0").IsSuccess);
        Assert.True(bridge.ObserveChildTrap(child).IsSuccess);
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Park).IsSuccess);
    }

    [Fact]
    public void AmbiguousChildCloseQuarantinesAndDoesNotPermitRetryOrReclaim()
    {
        var provider = new ChildProvider { MalformedClose = true };
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        PlatformChildBinding child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);

        KernelResult close = bridge.CloseChildDomain(child);
        KernelResult retry = bridge.CloseChildDomain(child);

        Assert.Equal(KernelError.PlatformFaulted, close.Error);
        Assert.Equal(KernelError.PlatformFaulted, retry.Error);
        Assert.Equal(1, provider.CloseCalls);
    }

    [Fact]
    public void ReentrantChildCloseCannotInvokeProviderTwice()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        Assert.True(bridge.TransitionChildDomain(child,
            PlatformChildDomainTransition.BeginDrain).IsSuccess);
        provider.BeforeChildClose = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive, bridge.CloseChildDomain(child).Error);
            Assert.Equal(1, provider.CloseCalls);
            Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(parent, owner).Error);
        };

        Assert.True(bridge.CloseChildDomain(child).IsSuccess);
        Assert.Equal(1, provider.CloseCalls);
        Assert.Equal(KernelError.PlatformBindingRevoked, bridge.CloseChildDomain(child).Error);
    }

    [Fact]
    public void ParentDomainWaitsForExactPublishedChildClosure()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;

        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(parent, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.True(bridge.CloseChildDomain(child).IsSuccess);
        Assert.True(bridge.RevokeDomain(parent, owner).IsSuccess);
    }

    [Fact]
    public void ChildCreateCallbackPinsParentBeforeChildBindingPublication()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        provider.BeforeChildCreateReturn = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(parent, owner).Error);
            Assert.Equal(0, provider.RevokeDomainCalls);
        };

        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        Assert.Equal(1, provider.ChildCreateCalls);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(parent, owner).Error);
        Assert.True(bridge.TransitionChildDomain(child,
            PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.True(bridge.CloseChildDomain(child).IsSuccess);
        Assert.True(bridge.RevokeDomain(parent, owner).IsSuccess);
    }

    [Fact]
    public void ParentRevokeCallbackRejectsChildCreateBeforeProvider()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        provider.BeforeDomainRevoke = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive,
                bridge.CreateChildDomain(parent, owner, Intent()).Error);
            Assert.Equal(0, provider.ChildCreateCalls);
        };

        Assert.True(bridge.RevokeDomain(parent, owner).IsSuccess);
        Assert.Equal(1, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ChildCreateReceiptLossOrAmbiguousStatusPinsParent(bool throws)
    {
        var provider = new ChildProvider
        {
            BeforeChildCreateReturn = throws
                ? () => throw new InvalidOperationException("Child created without receipt.")
                : null,
            ChildCreateStatus = throws ? null : PlatformAuthorityStatus.Faulted,
        };
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;

        Assert.False(bridge.CreateChildDomain(parent, owner, Intent()).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(parent, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            bridge.CreateChildDomain(parent, owner, Intent()).Error);
    }

    [Fact]
    public void BackendResetPreservesUnknownChildCreatePin()
    {
        var provider = new ChildProvider
        {
            BeforeChildCreateReturn = () => throw new InvalidOperationException("Child created without receipt."),
        };
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        Assert.Equal(KernelError.PlatformFaulted,
            bridge.CreateChildDomain(parent, owner, Intent()).Error);

        Assert.True(bridge.ObserveBackendReset().IsSuccess);
        Assert.True(bridge.TryGetQuarantinedDomainBinding(owner, out var current));
        Assert.Equal(KernelError.StaleGeneration, bridge.RevokeDomain(parent, owner).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(current, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetInsideChildCreateCallbackPinsNewParentGeneration(bool terminalEpoch)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        if (terminalEpoch)
            typeof(PlatformAuthorityBridge).GetField("_backendEpoch",
                global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic)!.SetValue(bridge, ulong.MaxValue);
        provider.BeforeChildCreateReturn = () =>
        {
            var reset = bridge.ObserveBackendReset();
            Assert.Equal(terminalEpoch ? KernelError.CapacityExhausted : KernelError.None, reset.Error);
        };

        Assert.Equal(KernelError.PlatformFaulted,
            bridge.CreateChildDomain(parent, owner, Intent()).Error);
        Assert.True(bridge.TryGetQuarantinedDomainBinding(owner, out var current));
        Assert.NotEqual(parent.Generation, current.Generation);
        Assert.Equal(KernelError.StaleGeneration, bridge.RevokeDomain(parent, owner).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(current, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
        Assert.Equal(1, provider.ChildCreateCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedChildCreateRequiresExactCleanupBeforeParentClose(bool cleanupFails)
    {
        var provider = new ChildProvider
        {
            MalformedCreateParent = true,
            ChildCreateCleanupStatus = cleanupFails ? PlatformAuthorityStatus.Faulted : null,
        };
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;

        Assert.Equal(KernelError.PlatformFaulted,
            bridge.CreateChildDomain(parent, owner, Intent()).Error);
        Assert.Equal(1, provider.CloseCalls);
        if (cleanupFails)
        {
            Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(parent, owner).Error);
            Assert.Equal(0, provider.RevokeDomainCalls);
        }
        else
        {
            Assert.True(bridge.RevokeDomain(parent, owner).IsSuccess);
            Assert.Equal(1, provider.RevokeDomainCalls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChildCloseCallbackResetOrThrowCannotCloseOrRetry(bool throws)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        provider.BeforeChildClose = throws
            ? () => throw new InvalidOperationException("after possible effect")
            : () => Assert.True(bridge.ObserveBackendReset().IsSuccess);

        var closed = bridge.CloseChildDomain(child);
        var retry = bridge.CloseChildDomain(child);

        Assert.Equal(KernelError.PlatformFaulted, closed.Error);
        Assert.Equal(KernelError.PlatformFaulted, retry.Error);
        Assert.Equal(1, provider.CloseCalls);
    }

    [Fact]
    public void ChildClosureRequiresTerminalGuestAndIoReceipts()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        PlatformChildBinding child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        PlatformRegionMapping parentMapping = bridge.MapOwnedRegion(parent, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        PlatformGuestMapping guest = bridge.MapChildGuestRegion(child, parentMapping,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        PlatformDeviceLease device = BindDeviceForModel(bridge, parent, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        PlatformVirtualIoBinding io = bridge.BindChildVirtualIo(child, device,
            new(PlatformDeviceRights.Read, 512)).Value!;
        Assert.True(bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).IsSuccess);

        Assert.Equal(KernelError.PlatformBindingActive, bridge.CloseChildDomain(child).Error);
        Assert.True(bridge.RevokeChildVirtualIo(io).IsSuccess);
        Assert.True(bridge.UnmapChildGuestRegion(guest).IsSuccess);
        Assert.True(bridge.CloseChildDomain(child).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GuestMapReceiptLossOrAmbiguousStatusPinsChildAndParentMapping(bool throws)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        var mapping = bridge.MapOwnedRegion(root, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        provider.ThrowAfterGuestMap = throws;
        provider.GuestMapStatus = throws ? null : PlatformAuthorityStatus.Denied;

        Assert.False(bridge.MapChildGuestRegion(child, mapping,
            new(0, 4096), PlatformGuestMemoryAccess.Read).IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(root, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Fact]
    public void ParentMappingRevocationWaitsForExactGuestClosure()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        var parent = bridge.MapOwnedRegion(root, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var guest = bridge.MapChildGuestRegion(child, parent,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;

        Assert.Equal(KernelError.PlatformBindingActive,
            bridge.BeginRegionMappingRevocation(parent, owner, PlatformRegionRevocationPolicy.DrainBeforeRevoke).Error);
        Assert.True(bridge.UnmapChildGuestRegion(guest).IsSuccess);
        Assert.True(bridge.BeginRegionMappingRevocation(parent, owner,
            PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
    }

    [Fact]
    public async Task ParentMappingRevocationWaitsForInFlightGuestAdmission()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        var parent = bridge.MapOwnedRegion(root, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        provider.AfterGuestMap = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var admission = Task.Run(() => bridge.MapChildGuestRegion(child, parent,
            new(0, 4096), PlatformGuestMemoryAccess.Read));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingActive,
                bridge.BeginRegionMappingRevocation(parent, owner,
                    PlatformRegionRevocationPolicy.DrainBeforeRevoke).Error);
        }
        finally { release.Set(); }
        Assert.True((await admission).IsSuccess);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VirtualIoBindReceiptLossOrAmbiguousStatusPinsChildAndDevice(bool throws)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;
        var device = BindDeviceForModel(bridge, root, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        provider.ThrowAfterVirtualIoBind = throws;
        provider.VirtualIoBindStatus = throws ? null : PlatformAuthorityStatus.Denied;

        Assert.False(bridge.BindChildVirtualIo(child, device,
            new(PlatformDeviceRights.Read, 512)).IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, bridge.RevokeDevice(device, owner).Error);
        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(root, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BackendResetDuringGuestOrVirtualIoAdmissionCannotPublishChild(bool virtualIo)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;
        provider.AfterGuestMap = virtualIo ? null : () => Assert.True(bridge.ObserveBackendReset().IsSuccess);
        provider.AfterVirtualIoBind = virtualIo ? () => Assert.True(bridge.ObserveBackendReset().IsSuccess) : null;

        if (virtualIo)
        {
            var device = BindDeviceForModel(bridge, root, owner, new CapabilityId(9),
                new("device:test"), PlatformDeviceRights.Read).Value!;
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.BindChildVirtualIo(child, device,
                    new(PlatformDeviceRights.Read, 512)).Error);
            Assert.Equal(KernelError.StaleGeneration, bridge.RevokeDevice(device, owner).Error);
        }
        else
        {
            var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
                new(new DomainId(1), owner.ProcessGeneration), 4096);
            var mapping = bridge.MapOwnedRegion(root, owner, new CapabilityId(8), region,
                PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.MapChildGuestRegion(child, mapping,
                    new(0, 4096), PlatformGuestMemoryAccess.Read).Error);
        }
        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void MalformedGuestOrVirtualIoAdmissionNeedsExactCompensation(
        bool virtualIo, bool cleanupFails)
    {
        var provider = new ChildProvider
        {
            MalformedGuestMap = !virtualIo,
            MalformedVirtualIoBind = virtualIo,
            GuestUnmapStatus = !virtualIo && cleanupFails ? PlatformAuthorityStatus.Faulted : null,
            VirtualIoRevokeStatus = virtualIo && cleanupFails ? PlatformAuthorityStatus.Faulted : null,
        };
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;

        if (virtualIo)
        {
            var device = BindDeviceForModel(bridge, root, owner, new CapabilityId(9),
                new("device:test"), PlatformDeviceRights.Read).Value!;
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.BindChildVirtualIo(child, device,
                    new(PlatformDeviceRights.Read, 512)).Error);
            Assert.Equal(1, provider.VirtualIoRevokeCalls);
            Assert.Equal(cleanupFails ? KernelError.PlatformFaulted : KernelError.None,
                bridge.RevokeDevice(device, owner).Error);
        }
        else
        {
            var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
                new(new DomainId(1), owner.ProcessGeneration), 4096);
            var mapping = bridge.MapOwnedRegion(root, owner, new CapabilityId(8), region,
                PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.MapChildGuestRegion(child, mapping,
                    new(0, 4096), PlatformGuestMemoryAccess.Read).Error);
            Assert.Equal(1, provider.GuestUnmapCalls);
            Assert.Equal(cleanupFails ? KernelError.PlatformFaulted : KernelError.None,
                bridge.ValidateMapping(mapping, owner).Error);
        }

        Assert.Equal(cleanupFails ? KernelError.PlatformFaulted : KernelError.None,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParentAuthorizationRevokedInsideChildAdmissionPinsPossibleEffect(bool virtualIo)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;

        if (virtualIo)
        {
            var device = BindDeviceForModel(bridge, root, owner, new CapabilityId(9),
                new("device:test"), PlatformDeviceRights.Read).Value!;
            provider.AfterVirtualIoBind = () =>
                Assert.Single(bridge.BeginDeviceCapabilityRevocation(new CapabilityId(9), static id => id == new CapabilityId(9)));

            Assert.Equal(KernelError.PlatformFaulted,
                bridge.BindChildVirtualIo(child, device,
                    new(PlatformDeviceRights.Read, 512)).Error);
            Assert.Equal(KernelError.PlatformFaulted, bridge.RevokeDevice(device, owner).Error);
        }
        else
        {
            var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
                new(new DomainId(1), owner.ProcessGeneration), 4096);
            var mapping = bridge.MapOwnedRegion(root, owner, new CapabilityId(8), region,
                PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
            provider.AfterGuestMap = () =>
                Assert.Single(bridge.BeginCapabilityRevocation(new CapabilityId(8), static id => id == new CapabilityId(8)));

            Assert.Equal(KernelError.PlatformFaulted,
                bridge.MapChildGuestRegion(child, mapping,
                    new(0, 4096), PlatformGuestMemoryAccess.Read).Error);
            Assert.Equal(KernelError.PlatformBindingRevoked, bridge.ValidateMapping(mapping, owner).Error);
        }

        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(root, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProviderIncarnationDriftInsideChildAdmissionPinsParent(bool virtualIo)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = Subject(1, 10);
        var root = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(root, owner, Intent()).Value!;

        if (virtualIo)
        {
            var device = BindDeviceForModel(bridge, root, owner, new CapabilityId(9),
                new("device:test"), PlatformDeviceRights.Read).Value!;
            provider.AfterVirtualIoBind = () => provider.CurrentIncarnation = new(2);
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.BindChildVirtualIo(child, device,
                    new(PlatformDeviceRights.Read, 512)).Error);
            Assert.Equal(KernelError.PlatformFaulted, bridge.RevokeDevice(device, owner).Error);
        }
        else
        {
            var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
                new(new DomainId(1), owner.ProcessGeneration), 4096);
            var mapping = bridge.MapOwnedRegion(root, owner, new CapabilityId(8), region,
                PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
            provider.AfterGuestMap = () => provider.CurrentIncarnation = new(2);
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.MapChildGuestRegion(child, mapping,
                    new(0, 4096), PlatformGuestMemoryAccess.Read).Error);
            Assert.Equal(KernelError.PlatformFaulted, bridge.ValidateMapping(mapping, owner).Error);
        }

        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(root, owner).Error);
        Assert.Equal(0, provider.RevokeDomainCalls);
    }

    [Fact]
    public void BackendResetQuarantinedVirtualIoCannotInvokeLateProviderRevoke()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var device = BindDeviceForModel(bridge, parent, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        var io = bridge.BindChildVirtualIo(child, device,
            new(PlatformDeviceRights.Read, 512)).Value!;

        Assert.True(bridge.ObserveBackendReset().IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, bridge.RevokeChildVirtualIo(io).Error);
        Assert.Equal(0, provider.VirtualIoRevokeCalls);
        Assert.Equal(KernelError.PlatformFaulted, bridge.RevokeChildVirtualIo(io).Error);
        Assert.Equal(0, provider.VirtualIoRevokeCalls);
    }

    [Fact]
    public void ReentrantVirtualIoRevokeCannotEnterProviderTwice()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var device = BindDeviceForModel(bridge, parent, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        var io = bridge.BindChildVirtualIo(child, device,
            new(PlatformDeviceRights.Read, 512)).Value!;
        provider.BeforeVirtualIoRevoke = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeChildVirtualIo(io).Error);
            Assert.Equal(1, provider.VirtualIoRevokeCalls);
        };

        Assert.True(bridge.RevokeChildVirtualIo(io).IsSuccess);
        Assert.Equal(1, provider.VirtualIoRevokeCalls);
        Assert.True(bridge.RevokeChildVirtualIo(io).IsSuccess);
        Assert.Equal(1, provider.VirtualIoRevokeCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BackendResetInsideClosureCallbackCannotTurnLateReceiptIntoClosure(bool virtualIo)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        var parentMapping = bridge.MapOwnedRegion(parent, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var guest = bridge.MapChildGuestRegion(child, parentMapping,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        var device = BindDeviceForModel(bridge, parent, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        var io = bridge.BindChildVirtualIo(child, device,
            new(PlatformDeviceRights.Read, 512)).Value!;
        if (virtualIo) provider.BeforeVirtualIoRevoke = () => Assert.True(bridge.ObserveBackendReset().IsSuccess);
        else provider.BeforeGuestUnmap = () => Assert.True(bridge.ObserveBackendReset().IsSuccess);

        var close = virtualIo ? bridge.RevokeChildVirtualIo(io) : bridge.UnmapChildGuestRegion(guest);

        Assert.Equal(KernelError.PlatformFaulted, close.Error);
        Assert.Equal(KernelError.PlatformFaulted, bridge.RevokeChildVirtualIo(io).Error);
        Assert.Equal(KernelError.PlatformFaulted, bridge.UnmapChildGuestRegion(guest).Error);
        Assert.Equal(virtualIo ? 1 : 0, provider.VirtualIoRevokeCalls);
        Assert.Equal(virtualIo ? 0 : 1, provider.GuestUnmapCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClosureCallbackExceptionPinsGuestOrVirtualIoWithoutRetry(bool virtualIo)
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(1, 10);
        var parent = bridge.BindDomain(owner).Value!;
        var child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), owner.ProcessGeneration), 4096);
        var mapping = bridge.MapOwnedRegion(parent, owner, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var guest = bridge.MapChildGuestRegion(child, mapping,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        var device = BindDeviceForModel(bridge, parent, owner, new CapabilityId(9),
            new("device:test"), PlatformDeviceRights.Read).Value!;
        var io = bridge.BindChildVirtualIo(child, device,
            new(PlatformDeviceRights.Read, 512)).Value!;
        if (virtualIo) provider.BeforeVirtualIoRevoke = () => throw new InvalidOperationException("after effect");
        else provider.BeforeGuestUnmap = () => throw new InvalidOperationException("after effect");

        var first = virtualIo ? bridge.RevokeChildVirtualIo(io) : bridge.UnmapChildGuestRegion(guest);
        var retry = virtualIo ? bridge.RevokeChildVirtualIo(io) : bridge.UnmapChildGuestRegion(guest);

        Assert.Equal(KernelError.PlatformFaulted, first.Error);
        Assert.Equal(KernelError.PlatformFaulted, retry.Error);
        Assert.Equal(virtualIo ? 1 : 0, provider.VirtualIoRevokeCalls);
        Assert.Equal(virtualIo ? 0 : 1, provider.GuestUnmapCalls);
    }

    [Fact]
    public void StaleRuntimeVmGenerationMakesZeroChildProviderCalls()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel, 6, 60).Handle;
        CapabilityId create = kernel.MintCapability(new DomainId(60), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);
        var stale = authority.Domain with { Generation = new(authority.Domain.Generation.Value + 1) };

        KernelResult start = kernel.StartVirtualDomain(owner, stale, authority.ExecuteCapability);

        Assert.Equal(KernelError.StaleGeneration, start.Error);
        Assert.Equal(0, provider.TransitionCalls);
    }

    [Fact]
    public void RuntimeKernelClosesChildAndParentGuestMappingBeforeLocalReclaim()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 7, 70);
        CapabilityId create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        CapabilityId regionCapability = kernel.MintCapability(process.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        GuestRegionMapping mapping = kernel.MapGuestRegion(owner, authority.Domain, authority.MemoryCapability,
            regionCapability, region.Handle, new(0, 4096), GuestMemoryAccess.Read | GuestMemoryAccess.Write).Value!;

        Assert.True(kernel.DestroyVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);
        Assert.True(kernel.ReleaseRegion(owner, region).IsSuccess);
        Assert.Equal(1, provider.GuestUnmapCalls);
        Assert.Equal(1, provider.RegionRevocationBeginCalls);
        Assert.Equal(1, provider.CompletionCalls);
        Assert.Equal(1, provider.RevokeDomainCalls);
    }

    [Fact]
    public void ProcessTeardownUsesDeterministicChildGuestParentClosureOrder()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 8, 80);
        CapabilityId create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);
        var region = kernel.AllocateRegion(owner, 4096).Value!;
        CapabilityId regionCapability = kernel.MintCapability(process.DomainId, owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        Assert.True(kernel.MapGuestRegion(owner, authority.Domain, authority.MemoryCapability, regionCapability,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);

        provider.CallLog.Clear();
        KernelResult terminate = kernel.TerminateProcess(owner);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(new[] { "guest-unmap", "parent-map-begin", "parent-map-observe", "child-drain", "child-close", "parent-close" },
            provider.CallLog);
    }

    [Fact]
    public void BackendResetFaultPinsChildAndGuestLedgersBeforeAnyFurtherProviderCall()
    {
        var provider = new ChildProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity owner = Subject(9, 90);
        PlatformDomainBinding parent = bridge.BindDomain(owner).Value!;
        PlatformChildBinding child = bridge.CreateChildDomain(parent, owner, Intent()).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(9), new RegionGeneration(1)),
            new(new DomainId(9), owner.ProcessGeneration), 4096);
        PlatformRegionMapping parentMapping = bridge.MapOwnedRegion(parent, owner, new CapabilityId(99), region,
            PlatformMemoryAccess.Read).Value!;
        PlatformGuestMapping guest = bridge.MapChildGuestRegion(child, parentMapping,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        int transitions = provider.TransitionCalls;
        int unmaps = provider.GuestUnmapCalls;

        var reset = bridge.ObserveBackendReset();
        KernelResult transition = bridge.TransitionChildDomain(child, PlatformChildDomainTransition.Start);
        KernelResult unmap = bridge.UnmapChildGuestRegion(guest);

        Assert.True(reset.IsSuccess, reset.Message);
        Assert.Equal(1, reset.Value!.InvalidatedVirtualDomains);
        Assert.Equal(KernelError.PlatformFaulted, transition.Error);
        Assert.Equal(KernelError.PlatformFaulted, unmap.Error);
        Assert.Equal(transitions, provider.TransitionCalls);
        Assert.Equal(unmaps, provider.GuestUnmapCalls);
    }

    [Fact]
    public async Task VirtualIoGuestEventRequiresExactPublishedOperation()
    {
        var provider = new ChildProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 10, 100);
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var authority = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, authority.Domain, authority.ConfigureCapability).IsSuccess);

        var parent = new PlatformDomainBinding(new(1), new(1),
            new(process.DomainId, owner));
        var deviceCapability = kernel.MintCapability(process.DomainId, owner, ResourceKind.Device,
            "device:virtual-io-event", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        var device = kernel.BindPlatformDevice(owner, parent, deviceCapability.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write).Value!;
        var virtualIoResult = kernel.BindVirtualIo(owner, authority.Domain, device,
            new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 4096));
        Assert.True(virtualIoResult.IsSuccess, virtualIoResult.Message);
        var virtualIo = virtualIoResult.Value;
        var currentIo = kernel.RevalidateVirtualIo(owner, virtualIo);
        Assert.True(currentIo.IsSuccess, currentIo.Message);

        var input = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var dependencies = new OperationDependencySnapshot(1, 1, 1, 1);
        var prepared = kernel.PrepareExternalOperation(owner,
            [new(input.Handle, RegionUseMode.ReadOnly, new(0, 8)),
             new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
        Assert.True(kernel.AdmitExternalOperation(owner, prepared.Operation, dependencies).IsSuccess);
        var submitted = kernel.RecordExternalOperationSubmission(owner, prepared.Operation, dependencies).Value!;
        var endpoint = kernel.CreateKernelEventEndpoint(owner).Value!;

        Assert.Equal(KernelError.InvalidTransition,
            kernel.PublishVirtualIoEvent(owner, virtualIo, prepared.Operation,
                authority.EventCapability, endpoint).Error);
        Assert.Equal(0, provider.EventCalls);

        Assert.True(kernel.RecordExternalOperationCompletion(owner,
            new(submitted, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(kernel.RecordExternalOperationVisibility(owner,
            new(submitted, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        Assert.True(kernel.PublishExternalOperation(owner, prepared.Operation, dependencies,
            new(ExternalPublicationPolicy.Staged), () => input.Span.CopyTo(output.Span)).IsSuccess);
        Assert.True(kernel.PublishVirtualIoEvent(owner, virtualIo, prepared.Operation,
            authority.EventCapability, endpoint).IsSuccess);

        var delivered = await kernel.WaitForKernelEventAsync(owner, endpoint);
        Assert.True(delivered.IsSuccess, delivered.Message);
        Assert.Equal(1, provider.EventCalls);
        Assert.Equal(input.Span.ToArray(), output.Span.ToArray());
    }

    private static PlatformDomainIdentity Subject(ulong domain, ulong process) =>
        new(new DomainId(domain), new(new ProcessId(process), 1));

    private static PlatformChildDomainIntent Intent() => new(
        new(1, 4096),
        new(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.Execution |
                PlatformChildAuthorityClass.GuestMemory | PlatformChildAuthorityClass.Events |
                PlatformChildAuthorityClass.Traps | PlatformChildAuthorityClass.Io,
            PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.Execution |
                PlatformChildAuthorityClass.GuestMemory | PlatformChildAuthorityClass.Events |
                PlatformChildAuthorityClass.Traps | PlatformChildAuthorityClass.Io));

    private sealed class ChildProvider : IPlatformAuthorityProvider, IPlatformFeatureProvider,
        IPlatformChildDomainProvider, IPlatformGuestMemoryProvider, IPlatformVirtualEventProvider,
        IPlatformVirtualTrapProvider, IPlatformVirtualIoProvider, IPlatformDeviceLeaseProvider,
        IPlatformRegionRevocationProvider, IPlatformCompletionProvider,
        IPlatformProviderIncarnationSource
    {
        private ulong next = 20;
        public bool MalformedClose { get; init; }
        public int TransitionCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public int GuestUnmapCalls { get; private set; }
        public int RegionRevocationBeginCalls { get; private set; }
        public int CompletionCalls { get; private set; }
        public int RevokeDomainCalls { get; private set; }
        public int DomainBindCalls { get; private set; }
        public int MapCalls { get; private set; }
        public Action? BeforeMapOwnedRegionReturn { get; set; }
        public PlatformAuthorityStatus? MapStatus { get; set; }
        public Action? BeforeDomainBindReturn { get; set; }
        public int ChildCreateCalls { get; private set; }
        public Action? BeforeDomainRevoke { get; set; }
        public int EventCalls { get; private set; }
        public int GuestMapCalls { get; private set; }
        public int TrapCalls { get; private set; }
        public Action? BeforeChildEvent { get; set; }
        public Action? BeforeChildTrap { get; set; }
        public int VirtualIoRevokeCalls { get; private set; }
        public Action? BeforeVirtualIoRevoke { get; set; }
        public Action? BeforeGuestUnmap { get; set; }
        public Action? BeforeChildClose { get; set; }
        public Action? BeforeChildCreateReturn { get; set; }
        public Action? BeforeChildTransition { get; set; }
        public PlatformAuthorityStatus? ChildCreateStatus { get; set; }
        public bool MalformedCreateParent { get; set; }
        public PlatformAuthorityStatus? ChildCreateCleanupStatus { get; set; }
        public bool ThrowAfterGuestMap { get; set; }
        public PlatformAuthorityStatus? GuestMapStatus { get; set; }
        public bool ThrowAfterVirtualIoBind { get; set; }
        public PlatformAuthorityStatus? VirtualIoBindStatus { get; set; }
        public Action? AfterGuestMap { get; set; }
        public Action? AfterVirtualIoBind { get; set; }
        public bool MalformedGuestMap { get; set; }
        public bool MalformedVirtualIoBind { get; set; }
        public PlatformAuthorityStatus? GuestUnmapStatus { get; set; }
        public PlatformAuthorityStatus? VirtualIoRevokeStatus { get; set; }
        public List<string> CallLog { get; } = [];
        public PlatformProviderChildDomainLease LastChildLease { get; private set; }
        public PlatformProviderIncarnation CurrentIncarnation { get; set; } = new(1);
        public PlatformProviderDescriptor Descriptor { get; } = new(new("test.child"), 2,
            PlatformAuthorityFeatures.NeutralDomainBinding | PlatformAuthorityFeatures.DirectOwnedRegionMapping);
        public PlatformFeatureManifest QueryFeatures() => new(Enum.GetValues<PlatformFeatureFamily>()
            .Where(f => f is PlatformFeatureFamily.NeutralDomains or PlatformFeatureFamily.OwnedRegionMapping or
                PlatformFeatureFamily.ChildDomainLifecycle or
                PlatformFeatureFamily.ChildGuestMemory or PlatformFeatureFamily.ChildEventDelivery or
                PlatformFeatureFamily.ChildTrapDelivery or PlatformFeatureFamily.BoundedVirtualIo)
            .Select(f => new PlatformFeatureDescriptor(f,
                f is PlatformFeatureFamily.NeutralDomains or PlatformFeatureFamily.ChildDomainLifecycle or
                    PlatformFeatureFamily.OwnedRegionMapping ? 2u : 1u,
                f == PlatformFeatureFamily.BoundedVirtualIo
                    ? PlatformFeatureAvailability.Executable
                    : PlatformFeatureAvailability.RuntimeAdmission)));
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
        {
            DomainBindCalls++;
            var lease = new PlatformProviderDomainLease(new(next++), new(7), subject);
            BeforeDomainBindReturn?.Invoke();
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            RevokeDomainCalls++;
            CallLog.Add("parent-close");
            BeforeDomainRevoke?.Invoke();
            return PlatformAuthorityResult.Ok();
        }
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease, PlatformRegionIdentity region, PlatformMemoryAccess access)
        {
            MapCalls++;
            BeforeMapOwnedRegionReturn?.Invoke();
            if (MapStatus is { } status)
                return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                    status, "Mapping rejected after callback.");
            return PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(
                new(new(next++), new(11), domainLease, region, access));
        }
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
            PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy)
        {
            RegionRevocationBeginCalls++;
            CallLog.Add("parent-map-begin");
            var operation = new PlatformOperationIdentity(new(next++), new(1), mapping.DomainLease);
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(new(mapping.MappingId, mapping.Generation, operation));
        }
        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(PlatformOperationIdentity operation)
        {
            CompletionCalls++;
            CallLog.Add("parent-map-observe");
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(new(operation.OperationId, operation.Generation,
                operation.DomainLease, PlatformCompletionState.Closed));
        }
        public PlatformAuthorityResult<PlatformProviderChildDomainLease> CreateChildDomain(
            PlatformProviderDomainLease parentLease, PlatformChildDomainIntent intent)
        {
            ChildCreateCalls++;
            var returnedParent = MalformedCreateParent
                ? parentLease with { Generation = new(parentLease.Generation.Value + 1) }
                : parentLease;
            LastChildLease = new(new(next++), new(23), returnedParent, intent);
            BeforeChildCreateReturn?.Invoke();
            if (ChildCreateStatus is { } status)
                return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(status,
                    "Injected ambiguous child creation response.");
            return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Ok(LastChildLease);
        }
        public PlatformAuthorityResult TransitionChildDomain(PlatformProviderChildDomainLease lease,
            PlatformChildDomainTransition transition)
        {
            TransitionCalls++;
            BeforeChildTransition?.Invoke();
            if (transition == PlatformChildDomainTransition.BeginDrain) CallLog.Add("child-drain");
            return PlatformAuthorityResult.Ok();
        }
        public PlatformAuthorityResult<PlatformChildDomainClosureReceipt> CloseChildDomain(PlatformProviderChildDomainLease lease)
        {
            CloseCalls++;
            CallLog.Add("child-close");
            BeforeChildClose?.Invoke();
            if (ChildCreateCleanupStatus is { } status)
                return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Fail(status,
                    "Injected child creation cleanup failure.");
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Ok(new(lease.LeaseId,
                MalformedClose ? new(lease.Generation.Value + 1) : lease.Generation,
                lease.ParentDomainLease.LeaseId, lease.ParentDomainLease.Generation,
                PlatformChildDomainClosureDisposition.Closed));
        }
        public PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> MapGuestRegion(PlatformGuestRegionMappingRequest request)
        {
            GuestMapCalls++;
            var range = MalformedGuestMap
                ? request.GuestRange with { ByteLength = request.GuestRange.ByteLength - 1 }
                : request.GuestRange;
            var lease = new PlatformProviderGuestRegionMappingLease(new(next++), new(31), request.ChildLease,
                request.ParentMapping.Lease, range, request.Access);
            AfterGuestMap?.Invoke();
            if (ThrowAfterGuestMap) throw new InvalidOperationException("Guest map receipt lost.");
            if (GuestMapStatus is { } status)
                return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(status,
                    "Injected ambiguous guest mapping status.");
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Ok(lease);
        }
        public PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt> UnmapGuestRegion(PlatformProviderGuestRegionMappingLease lease)
        {
            GuestUnmapCalls++;
            CallLog.Add("guest-unmap");
            BeforeGuestUnmap?.Invoke();
            if (GuestUnmapStatus is { } status)
                return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(status,
                    "Injected guest cleanup failure.");
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation,
                lease.ChildLease.LeaseId, lease.ChildLease.Generation, lease.ChildLease.ParentDomainLease.LeaseId,
                lease.ChildLease.ParentDomainLease.Generation, true));
        }
        public PlatformAuthorityResult<PlatformVirtualEventReceipt> InjectVirtualEvent(PlatformVirtualEventRequest request)
        {
            EventCalls++;
            BeforeChildEvent?.Invoke();
            return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Ok(new(request.ChildLease.LeaseId,
                request.ChildLease.Generation, request.ChildLease.ParentDomainLease.LeaseId,
                request.ChildLease.ParentDomainLease.Generation, (ulong)EventCalls, request.EventClass, request.SourceResourceId));
        }
        public PlatformAuthorityResult<PlatformVirtualTrapEvidence> ObserveVirtualTrap(PlatformProviderChildDomainLease lease)
        {
            TrapCalls++;
            BeforeChildTrap?.Invoke();
            return PlatformAuthorityResult<PlatformVirtualTrapEvidence>.Ok(new(lease.LeaseId, lease.Generation,
                lease.ParentDomainLease.LeaseId, lease.ParentDomainLease.Generation, (ulong)TrapCalls,
                PlatformVirtualTrapKind.Timer));
        }
        public PlatformAuthorityResult<PlatformProviderVirtualIoLease> BindVirtualIo(PlatformVirtualIoRequest request)
        {
            var profile = MalformedVirtualIoBind
                ? request.Profile with { MaximumDmaTransferBytes = request.Profile.MaximumDmaTransferBytes - 1 }
                : request.Profile;
            var lease = new PlatformProviderVirtualIoLease(new(next++), new(41), request.ChildLease,
                request.ParentDeviceLease, profile);
            AfterVirtualIoBind?.Invoke();
            if (ThrowAfterVirtualIoBind) throw new InvalidOperationException("Virtual-I/O bind receipt lost.");
            if (VirtualIoBindStatus is { } status)
                return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Fail(status,
                    "Injected ambiguous virtual-I/O status.");
            return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Ok(lease);
        }
        public PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> RevokeVirtualIo(PlatformProviderVirtualIoLease lease)
        {
            VirtualIoRevokeCalls++;
            BeforeVirtualIoRevoke?.Invoke();
            if (VirtualIoRevokeStatus is { } status)
                return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Fail(status,
                    "Injected virtual-I/O cleanup failure.");
            return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation,
                lease.ChildLease.LeaseId, lease.ChildLease.Generation, lease.ChildLease.ParentDomainLease.LeaseId,
                lease.ChildLease.ParentDomainLease.Generation, true));
        }
        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease domainLease,
            PlatformDeviceIdentity device, PlatformDeviceRights rights) =>
            PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(new(new(next++), new(37), domainLease, device, rights));
        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease) => PlatformAuthorityResult.Ok();
    }
}
