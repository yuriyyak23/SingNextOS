using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformOwnedRegionMappingV2Tests
{
    [Fact]
    public void DamageAfterMappingPublicationDeniesVisibilityBeforeProvider()
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 708, 780);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var mapping = kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
            region.Handle, 0, 4096, PlatformMemoryAccess.Read).Value!;
        var regionOwner = new RegionOwner(process.DomainId, owner.Generation);
        Assert.True(kernel.Regions.QuarantineSubrange(region.Handle, regionOwner,
            new(1, new("test-provider", "bank-0", 1, 1), 1,
                ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8))).IsSuccess);

        var visibility = kernel.PreparePlatformRegionMappingForConsumer(owner, mapping,
            PlatformMemoryConsumerClass.ExternalExecutionDomain, PlatformMemoryVisibilityRequirement.PublicationFence);
        Assert.Equal(KernelError.Quarantined, visibility.Error);
        Assert.Equal(0, provider.VisibilityCalls);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapping).IsSuccess);
        Assert.Single(kernel.Regions.SnapshotDamage());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DamageInsideMappingCallbackPreservesBudgetUntilExactClosure(bool exactSlice, bool concurrent)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 705, 750);
        var admin = TestFixtures.Create(kernel, 95, 950).Handle;
        var administration = kernel.MintCapability(new DomainId(950), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "revoke-map",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        KernelResult<RegionDamageDescriptorV1> Damage() => kernel.Regions.QuarantineSubrange(
            region.Handle, new RegionOwner(process.DomainId, owner.Generation),
            new(1, new("test-provider", "bank-0", 1, 1), 1,
                ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8)));
        provider.BeforeMapReturn = () => Assert.True((concurrent
            ? Task.Run(Damage).GetAwaiter().GetResult() : Damage()).IsSuccess);
        var mapping = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
                region.Handle, 0, 4096, PlatformMemoryAccess.Read).Value!.Mapping
            : kernel.MapPlatformOwnedRegion(owner, binding, capability,
                region.Handle, PlatformMemoryAccess.Read).Value!;
        Assert.Equal(KernelError.PlatformBindingRevoked,
            kernel.PlatformAuthority.ValidateMapping(mapping, binding.Subject).Error);
        Assert.False(kernel.MapPlatformOwnedRegion(owner, binding, capability,
            region.Handle, PlatformMemoryAccess.Read).IsSuccess);
        Assert.Equal(1, provider.MapCalls);
        var regionOwner = new RegionOwner(process.DomainId, owner.Generation);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
            usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        provider.CompletionState = PlatformCompletionState.Draining;
        Assert.Equal(KernelError.PlatformBindingDraining, kernel.RevokePlatformRegionMapping(owner, mapping).Error);
        var stale = mapping with { Generation = new PlatformRegionMappingGeneration(mapping.Generation.Value + 1) };
        Assert.Equal(KernelError.StaleGeneration, kernel.RevokePlatformRegionMapping(owner, stale).Error);
        provider.CompletionState = PlatformCompletionState.Closed;
        provider.ReturnStaleCompletionGeneration = true;
        Assert.Equal(KernelError.StaleGeneration, kernel.RevokePlatformRegionMapping(owner, mapping).Error);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
            usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        provider.ReturnStaleCompletionGeneration = false;
        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapping).IsSuccess);
        Assert.False(kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
            usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(KernelError.PlatformBindingRevoked,
            kernel.RevokePlatformRegionMapping(owner, mapping).Error);
        Assert.Single(kernel.Regions.SnapshotDamage());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelegatedRootRevokeAfterMappingPublicationPreservesClosureHandle(bool exactSlice)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 705, 750);
        var admin = TestFixtures.Create(kernel, 95, 950).Handle;
        var administration = kernel.MintCapability(new DomainId(950), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "revoke-map",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var root = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Delegate);
        var capability = kernel.DelegateCapability(owner, owner, root,
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapping = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
                region.Handle, 0, 4096, PlatformMemoryAccess.Read).Value!.Mapping
            : kernel.MapPlatformOwnedRegion(owner, binding, capability,
                region.Handle, PlatformMemoryAccess.Read).Value!;
        provider.CompletionState = PlatformCompletionState.Draining;
        Assert.Equal(KernelError.PlatformBindingDraining, kernel.RevokeCapability(root).Error);
        Assert.Equal(KernelError.PlatformBindingRevoked,
            kernel.PlatformAuthority.ValidateMapping(mapping, binding.Subject).Error);
        Assert.False(kernel.MapPlatformOwnedRegion(owner, binding, capability,
            region.Handle, PlatformMemoryAccess.Read).IsSuccess);
        Assert.Equal(1, provider.MapCalls);
        var regionOwner = new RegionOwner(process.DomainId, owner.Generation);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
            usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        provider.CompletionState = PlatformCompletionState.Draining;
        Assert.Equal(KernelError.PlatformBindingDraining, kernel.RevokeCapability(root).Error);
        var stale = mapping with { Generation = new PlatformRegionMappingGeneration(mapping.Generation.Value + 1) };
        Assert.Equal(KernelError.StaleGeneration, kernel.RevokePlatformRegionMapping(owner, stale).Error);
        provider.CompletionState = PlatformCompletionState.Closed;
        provider.ReturnStaleCompletionGeneration = true;
        Assert.Equal(KernelError.StaleGeneration, kernel.RevokeCapability(root).Error);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
            usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        provider.ReturnStaleCompletionGeneration = false;
        Assert.True(kernel.RevokeCapability(root).IsSuccess);
        Assert.False(kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
            usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.True(kernel.RevokeCapability(root).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DelegatedRootRevokeInsideMappingCallbackPreservesClosureHandle(bool exactSlice, bool concurrent)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 705, 750);
        var admin = TestFixtures.Create(kernel, 95, 950).Handle;
        var administration = kernel.MintCapability(new DomainId(950), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "revoke-map",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var root = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Delegate);
        var capability = kernel.DelegateCapability(owner, owner, root,
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        provider.BeforeMapReturn = () =>
            Assert.Equal(KernelError.PlatformBindingDraining, (concurrent
                ? Task.Run(() => kernel.RevokeCapability(root)).GetAwaiter().GetResult()
                : kernel.RevokeCapability(root)).Error);
        var mapping = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
                region.Handle, 0, 4096, PlatformMemoryAccess.Read).Value!.Mapping
            : kernel.MapPlatformOwnedRegion(owner, binding, capability,
                region.Handle, PlatformMemoryAccess.Read).Value!;
        Assert.Equal(KernelError.PlatformBindingRevoked,
            kernel.PlatformAuthority.ValidateMapping(mapping, binding.Subject).Error);
        Assert.False(kernel.MapPlatformOwnedRegion(owner, binding, capability,
            region.Handle, PlatformMemoryAccess.Read).IsSuccess);
        Assert.Equal(1, provider.MapCalls);
        var regionOwner = new RegionOwner(process.DomainId, owner.Generation);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
            usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        provider.CompletionState = PlatformCompletionState.Draining;
        Assert.Equal(KernelError.PlatformBindingDraining, kernel.RevokeCapability(root).Error);
        var stale = mapping with { Generation = new PlatformRegionMappingGeneration(mapping.Generation.Value + 1) };
        Assert.Equal(KernelError.StaleGeneration, kernel.RevokePlatformRegionMapping(owner, stale).Error);
        provider.CompletionState = PlatformCompletionState.Closed;
        provider.ReturnStaleCompletionGeneration = true;
        Assert.Equal(KernelError.StaleGeneration, kernel.RevokeCapability(root).Error);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
            usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        provider.ReturnStaleCompletionGeneration = false;
        Assert.True(kernel.RevokeCapability(root).IsSuccess);
        Assert.False(kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
            usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.True(kernel.RevokeCapability(root).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CapabilityRevokeInsideMappingCallbackPreservesClosureHandle(bool exactSlice, bool concurrent)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 705, 750);
        var admin = TestFixtures.Create(kernel, 95, 950).Handle;
        var administration = kernel.MintCapability(new DomainId(950), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "revoke-map",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        provider.BeforeMapReturn = () =>
            Assert.Equal(KernelError.PlatformBindingDraining, (concurrent
                ? Task.Run(() => kernel.RevokeCapability(capability)).GetAwaiter().GetResult()
                : kernel.RevokeCapability(capability)).Error);
        var mapping = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
                region.Handle, 0, 4096, PlatformMemoryAccess.Read).Value!.Mapping
            : kernel.MapPlatformOwnedRegion(owner, binding, capability,
                region.Handle, PlatformMemoryAccess.Read).Value!;
        Assert.Equal(KernelError.PlatformBindingRevoked,
            kernel.PlatformAuthority.ValidateMapping(mapping, binding.Subject).Error);
        Assert.False(kernel.MapPlatformOwnedRegion(owner, binding, capability,
            region.Handle, PlatformMemoryAccess.Read).IsSuccess);
        Assert.Equal(1, provider.MapCalls);
        var regionOwner = new RegionOwner(process.DomainId, owner.Generation);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
            usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        provider.CompletionState = PlatformCompletionState.Draining;
        Assert.Equal(KernelError.PlatformBindingDraining, kernel.RevokeCapability(capability).Error);
        var stale = mapping with { Generation = new PlatformRegionMappingGeneration(mapping.Generation.Value + 1) };
        Assert.Equal(KernelError.StaleGeneration, kernel.RevokePlatformRegionMapping(owner, stale).Error);
        provider.CompletionState = PlatformCompletionState.Closed;
        provider.ReturnStaleCompletionGeneration = true;
        Assert.Equal(KernelError.StaleGeneration, kernel.RevokeCapability(capability).Error);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
            usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        provider.ReturnStaleCompletionGeneration = false;
        Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        Assert.False(kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
            usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DelegatedRootMappingWithLostReceiptCannotReportClosure(bool exactSlice, bool reset)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 706, 760);
        var admin = TestFixtures.Create(kernel, 96, 960).Handle;
        var administration = kernel.MintCapability(new DomainId(960), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "revoke-lost-map",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var root = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Delegate);
        var capability = kernel.DelegateCapability(owner, owner, root,
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        provider.BeforeMapReturn = () =>
        {
            Assert.Equal(KernelError.PlatformBindingDraining, kernel.RevokeCapability(root).Error);
            if (reset) Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
            throw new InvalidOperationException("Effect possible, receipt lost.");
        };
        var error = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
                region.Handle, 0, 4096, PlatformMemoryAccess.Read).Error
            : kernel.MapPlatformOwnedRegion(owner, binding, capability,
                region.Handle, PlatformMemoryAccess.Read).Error;
        Assert.Equal(KernelError.PlatformFaulted, error);
        for (var retry = 0; retry < 2; retry++)
        {
            Assert.False(kernel.RevokeCapability(root).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
            Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle,
                new RegionOwner(process.DomainId, owner.Generation)));
            Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
                usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        }
        Assert.Empty((global::System.Collections.IDictionary)typeof(RuntimeKernel)
            .GetField("_pendingPlatformMappingAdmissions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(kernel)!);
        Assert.Empty((global::System.Collections.IEnumerable)typeof(RuntimeKernel)
            .GetField("_pendingPlatformMappingCapabilities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(kernel)!);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RevokedMappingWithLostReceiptCannotReportClosure(bool exactSlice, bool reset)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 706, 760);
        var admin = TestFixtures.Create(kernel, 96, 960).Handle;
        var administration = kernel.MintCapability(new DomainId(960), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "revoke-lost-map",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        provider.BeforeMapReturn = () =>
        {
            Assert.Equal(KernelError.PlatformBindingDraining, kernel.RevokeCapability(capability).Error);
            if (reset) Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
            throw new InvalidOperationException("Effect possible, receipt lost.");
        };
        var error = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
                region.Handle, 0, 4096, PlatformMemoryAccess.Read).Error
            : kernel.MapPlatformOwnedRegion(owner, binding, capability,
                region.Handle, PlatformMemoryAccess.Read).Error;
        Assert.Equal(KernelError.PlatformFaulted, error);
        for (var retry = 0; retry < 2; retry++)
        {
            Assert.False(kernel.RevokeCapability(capability).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
            Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle,
                new RegionOwner(process.DomainId, owner.Generation)));
            Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
                usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        }
        Assert.Empty((global::System.Collections.IDictionary)typeof(RuntimeKernel)
            .GetField("_pendingPlatformMappingAdmissions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(kernel)!);
        Assert.Empty((global::System.Collections.IEnumerable)typeof(RuntimeKernel)
            .GetField("_pendingPlatformMappingCapabilities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(kernel)!);
    }
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void RevokeCallbackNegativeAndResetRepliesPreserveExactAccounting(bool exactSlice, int outcome)
    {
        var provider = new ExactMappingProvider
        {
            MapStatus = outcome == 1 ? null : PlatformAuthorityStatus.NotAccepted,
        };
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 709, 790);
        var admin = TestFixtures.Create(kernel, 97, 970).Handle;
        var administration = kernel.MintCapability(new DomainId(970), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "revoke-reset-map",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        provider.BeforeMapReturn = () =>
        {
            Assert.Equal(KernelError.PlatformBindingDraining, kernel.RevokeCapability(capability).Error);
            if (outcome != 0) Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        };
        if (exactSlice)
        {
            var result = kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
                region.Handle, 0, 4096, PlatformMemoryAccess.Read);
            Assert.Equal(outcome == 1, result.IsSuccess);
            if (outcome == 2) Assert.Equal(KernelError.PlatformFaulted, result.Error);
        }
        else
        {
            var result = kernel.MapPlatformOwnedRegion(owner, binding, capability,
                region.Handle, PlatformMemoryAccess.Read);
            Assert.Equal(outcome == 1, result.IsSuccess);
            if (outcome == 2) Assert.Equal(KernelError.PlatformFaulted, result.Error);
        }
        var pinned = outcome != 0;
        Assert.Equal(pinned, kernel.Regions.HasPlatformMappingReservation(region.Handle,
            new RegionOwner(process.DomainId, owner.Generation)));
        Assert.Equal(pinned ? 4096UL : 0UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
            usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(!pinned, kernel.RevokeCapability(capability).IsSuccess);
        var fresh = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        // A fresh capability does not rehabilitate the old domain generation after reset.
        if (pinned)
        {
            Assert.False(kernel.MapPlatformOwnedRegion(owner, binding, fresh,
                region.Handle, PlatformMemoryAccess.Read).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        }
        else Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.MapCalls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaturatedMappingAdmissionRejectsWithoutPartialMutation(bool exactSlice)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 713, 830);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var admissions = (Dictionary<ProcessHandle, int>)typeof(RuntimeKernel)
            .GetField("_pendingPlatformMappingAdmissions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(kernel)!;
        var capabilities = (List<CapabilityId>)typeof(RuntimeKernel)
            .GetField("_pendingPlatformMappingCapabilities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(kernel)!;
        admissions.Add(owner, int.MaxValue);
        var denied = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
                region.Handle, 0, 4096, PlatformMemoryAccess.Read).Error
            : kernel.MapPlatformOwnedRegion(owner, binding, capability,
                region.Handle, PlatformMemoryAccess.Read).Error;
        Assert.Equal(KernelError.CapacityExhausted, denied);
        Assert.Equal(int.MaxValue, admissions[owner]);
        Assert.Empty(capabilities);
        Assert.Equal(0, provider.MapCalls);
        Assert.False(kernel.Regions.HasPlatformMappingReservation(region.Handle,
            new RegionOwner(process.DomainId, owner.Generation)));
        // Remove only the injected test saturation; the normal admission must remain usable.
        admissions.Remove(owner);
        var mapped = kernel.MapPlatformOwnedRegion(owner, binding, capability,
            region.Handle, PlatformMemoryAccess.Read);
        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.Empty(admissions);
        Assert.Empty(capabilities);
        Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReusedProcessIdCannotReadmitOldMappingCapabilityOrBinding(bool exactSlice)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, oldOwner) = TestFixtures.Create(kernel, 714, 840);
        var oldRegion = kernel.AllocateBuffer<byte>(oldOwner, 4096).Value!;
        var oldBinding = kernel.BindPlatformDomain(oldOwner).Value!;
        var oldCapability = MintRegionCapability(kernel, oldOwner, oldRegion.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        Assert.True(kernel.TerminateProcess(oldOwner).IsSuccess);
        var (_, freshOwner) = TestFixtures.Create(kernel, 714, 840, generation: 2);
        var freshRegion = kernel.AllocateBuffer<byte>(freshOwner, 4096).Value!;
        var freshBinding = kernel.BindPlatformDomain(freshOwner).Value!;
        var freshCapability = MintRegionCapability(kernel, freshOwner, freshRegion.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var stale = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(oldOwner, oldBinding, oldCapability,
                oldRegion.Handle, 0, 4096, PlatformMemoryAccess.Read).Error
            : kernel.MapPlatformOwnedRegion(oldOwner, oldBinding, oldCapability,
                oldRegion.Handle, PlatformMemoryAccess.Read).Error;
        Assert.Equal(KernelError.StaleHandle, stale);
        Assert.False(kernel.MapPlatformOwnedRegion(freshOwner, oldBinding, freshCapability,
            freshRegion.Handle, PlatformMemoryAccess.Read).IsSuccess);
        Assert.False(kernel.MapPlatformOwnedRegion(freshOwner, freshBinding, oldCapability,
            freshRegion.Handle, PlatformMemoryAccess.Read).IsSuccess);
        Assert.Equal(0, provider.MapCalls);
        var admitted = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(freshOwner, freshBinding, freshCapability,
                freshRegion.Handle, 0, 4096, PlatformMemoryAccess.Read).IsSuccess
            : kernel.MapPlatformOwnedRegion(freshOwner, freshBinding, freshCapability,
                freshRegion.Handle, PlatformMemoryAccess.Read).IsSuccess;
        Assert.True(admitted);
        Assert.True(kernel.RevokeCapability(freshCapability).IsSuccess);
        Assert.True(kernel.TerminateProcess(freshOwner).IsSuccess);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BudgetDeniedMappingBalancesAdmissionAndDoesNotCallProvider(bool exactSlice)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 715, 850);
        var admin = TestFixtures.Create(kernel, 98, 980).Handle;
        var administration = kernel.MintCapability(new DomainId(980), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "denied-map",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4095)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var denied = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
                region.Handle, 0, 4096, PlatformMemoryAccess.Read).Error
            : kernel.MapPlatformOwnedRegion(owner, binding, capability,
                region.Handle, PlatformMemoryAccess.Read).Error;
        Assert.Equal(KernelError.BudgetExceeded, denied);
        Assert.Equal(0, provider.MapCalls);
        Assert.False(kernel.Regions.HasPlatformMappingReservation(region.Handle,
            new RegionOwner(process.DomainId, owner.Generation)));
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(usage =>
            usage.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Empty((global::System.Collections.IDictionary)typeof(RuntimeKernel)
            .GetField("_pendingPlatformMappingAdmissions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(kernel)!);
        Assert.Empty((global::System.Collections.IEnumerable)typeof(RuntimeKernel)
            .GetField("_pendingPlatformMappingCapabilities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(kernel)!);
        Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
    }
    [Fact]
    public void ResetBeforeNotAcceptedExactSliceReplyRetainsReservation()
    {
        var provider = new ExactMappingProvider { MapStatus = PlatformAuthorityStatus.NotAccepted };
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = new PlatformDomainIdentity(new DomainId(710),
            new ProcessHandle(new ProcessId(701), 1));
        var binding = bridge.BindDomain(owner).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(1), new RegionGeneration(1)),
            new(new DomainId(710), owner.ProcessGeneration), 4096);
        provider.BeforeMapReturn = () => Assert.True(bridge.ObserveBackendReset().IsSuccess);

        var mapped = bridge.MapOwnedRegionSlice(binding, owner, new CapabilityId(8),
            new PlatformRegionSlice(region, 0, 4096, PlatformMemoryAccess.Read),
            retainFaultedHandleOnMalformed: true, out var retainReservation);

        Assert.Equal(KernelError.PlatformFaulted, mapped.Error);
        Assert.True(retainReservation);
    }

    [Fact]
    public void StableNotAcceptedExactSliceReplyDoesNotRetainReservation()
    {
        var provider = new ExactMappingProvider { MapStatus = PlatformAuthorityStatus.NotAccepted };
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = new PlatformDomainIdentity(new DomainId(710),
            new ProcessHandle(new ProcessId(701), 1));
        var binding = bridge.BindDomain(owner).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(1), new RegionGeneration(1)),
            new(new DomainId(710), owner.ProcessGeneration), 4096);

        var mapped = bridge.MapOwnedRegionSlice(binding, owner, new CapabilityId(8),
            new PlatformRegionSlice(region, 0, 4096, PlatformMemoryAccess.Read),
            retainFaultedHandleOnMalformed: true, out var retainReservation);

        Assert.False(mapped.IsSuccess);
        Assert.False(retainReservation);
        Assert.True(bridge.RevokeDomain(binding, owner).IsSuccess);
    }

    [Fact]
    public void ProcessExitInsideExactSliceCallbackTracksLateMapping()
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 705, 750);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        provider.BeforeMapReturn = () =>
            Assert.Equal(KernelError.PlatformBindingDraining, kernel.TerminateProcess(owner).Error);

        var mapped = kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
            region.Handle, 0, 4096, PlatformMemoryAccess.Read);

        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.Equal(1, provider.MapCalls);
    }

    [Fact]
    public void ExactSliceCallbackBlocksParentRevokeUntilPublication()
    {
        var provider = new ExactMappingProvider();
        var bridge = new PlatformAuthorityBridge(provider);
        var owner = new PlatformDomainIdentity(new DomainId(710),
            new ProcessHandle(new ProcessId(701), 1));
        var binding = bridge.BindDomain(owner).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(1), new RegionGeneration(1)),
            new(new DomainId(710), owner.ProcessGeneration), 4096);
        provider.BeforeMapReturn = () => Assert.Equal(KernelError.PlatformBindingActive,
            bridge.RevokeDomain(binding, owner).Error);

        var mapped = bridge.MapOwnedRegionSlice(binding, owner, new CapabilityId(8),
            new PlatformRegionSlice(region, 0, 4096, PlatformMemoryAccess.Read),
            retainFaultedHandleOnMalformed: true);

        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.Equal(1, provider.MapCalls);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(binding, owner).Error);
    }

    [Fact]
    public void InvalidRangeOwnerAndGenerationAreRejectedBeforeProviderCall()
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 701, 710);
        var (_, other) = TestFixtures.Create(kernel, 702, 720);
        var region = kernel.AllocateBuffer<byte>(owner, 128).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);

        var outOfRange = kernel.MapPlatformOwnedRegionSlice(
            owner, binding, capability, region.Handle, 100, 29, PlatformMemoryAccess.Read);
        Assert.Equal(KernelError.PlatformDenied, outOfRange.Error);
        Assert.Equal(0, provider.MapCalls);

        var staleHandle = region.Handle with
        {
            Generation = new RegionGeneration(region.Handle.Generation.Value + 1),
        };
        var stale = kernel.MapPlatformOwnedRegionSlice(
            owner, binding, capability, staleHandle, 0, 64, PlatformMemoryAccess.Read);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.Equal(0, provider.MapCalls);

        var otherBinding = kernel.BindPlatformDomain(other).Value!;
        var wrongOwner = kernel.MapPlatformOwnedRegionSlice(
            other, otherBinding, capability, region.Handle, 0, 64, PlatformMemoryAccess.Read);
        Assert.Equal(KernelError.WrongCapabilitySubject, wrongOwner.Error);
        Assert.Equal(0, provider.MapCalls);
    }

    [Fact]
    public void ExactSliceIsCommittedAndBlocksOwnershipMutationUntilClosed()
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 703, 730);
        var (_, target) = TestFixtures.Create(kernel, 704, 740);
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write);

        var mapped = kernel.MapPlatformOwnedRegionSlice(
            owner, binding, capability, region.Handle, 64, 512,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.Equal(64, mapped.Value!.Offset);
        Assert.Equal(512, mapped.Value.Length);
        Assert.Equal(64, provider.LastSlice!.Value.Offset);
        Assert.Equal(512, provider.LastSlice.Value.Length);
        Assert.Equal(new RegionOwner(new DomainId(730), owner.Generation),
            provider.LastSlice.Value.Region.Owner);

        Assert.Equal(KernelError.PlatformBindingActive,
            kernel.TransferRegion(owner, target, region).Error);
        Assert.Equal(KernelError.PlatformBindingActive,
            kernel.Regions.Loan(
                region.Handle,
                new RegionOwner(new DomainId(730), owner.Generation),
                new RegionOwner(new DomainId(740), target.Generation)).Error);
        Assert.Equal(KernelError.PlatformBindingActive,
            kernel.ReleaseRegion(owner, region).Error);

        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapped.Value).IsSuccess);
        var moved = kernel.TransferRegion(owner, target, region);
        Assert.True(moved.IsSuccess, moved.Message);
        Assert.Equal(new RegionGeneration(2), moved.Value!.Handle.Generation);
    }

    [Fact]
    public void NonCoherentMappingRequiresSatisfiedPublicationFence()
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(705, 750);

        var coherent = kernel.PreparePlatformRegionMappingForConsumer(
            owner, mapping,
            PlatformMemoryConsumerClass.ExternalExecutionDomain,
            PlatformMemoryVisibilityRequirement.CoherentAccess);
        var fence = kernel.PreparePlatformRegionMappingForConsumer(
            owner, mapping,
            PlatformMemoryConsumerClass.ExternalExecutionDomain,
            PlatformMemoryVisibilityRequirement.PublicationFence);

        Assert.Equal(KernelError.PlatformUnsupported, coherent.Error);
        Assert.True(fence.IsSuccess, fence.Message);
        Assert.True(fence.Value!.IsSatisfied);
        Assert.Equal(PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied,
            fence.Value.Outcome);
        Assert.Equal(2, provider.VisibilityCalls);
    }

    [Fact]
    public void DrainingMappingRejectsNewVisibilityBeforeProviderCall()
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(706, 760);
        provider.CompletionState = PlatformCompletionState.Draining;

        var revoke = kernel.RevokePlatformRegionMapping(owner, mapping);
        Assert.Equal(KernelError.PlatformBindingDraining, revoke.Error);

        var calls = provider.VisibilityCalls;
        var visibility = kernel.PreparePlatformRegionMappingForConsumer(
            owner, mapping,
            PlatformMemoryConsumerClass.ExternalExecutionDomain,
            PlatformMemoryVisibilityRequirement.PublicationFence);
        Assert.Equal(KernelError.PlatformBindingDraining, visibility.Error);
        Assert.Equal(calls, provider.VisibilityCalls);

        provider.CompletionState = PlatformCompletionState.Closed;
        Assert.True(kernel.ObservePlatformRegionMappingRevocation(owner, mapping.Mapping).IsSuccess);
    }

    [Fact]
    public void StaleCompletionCannotReleaseReservation()
    {
        var provider = new ExactMappingProvider
        {
            CompletionState = PlatformCompletionState.Closed,
            ReturnStaleCompletionGeneration = true,
        };
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 707, 770);
        var (_, target) = TestFixtures.Create(kernel, 708, 780);
        var region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var mapping = kernel.MapPlatformOwnedRegionSlice(
            owner, binding, capability, region.Handle, 0, 64, PlatformMemoryAccess.Read).Value!;

        Assert.Equal(KernelError.StaleGeneration,
            kernel.RevokePlatformRegionMapping(owner, mapping).Error);
        Assert.Equal(KernelError.PlatformBindingActive,
            kernel.TransferRegion(owner, target, region).Error);

        provider.ReturnStaleCompletionGeneration = false;
        Assert.True(kernel.ObservePlatformRegionMappingRevocation(owner, mapping.Mapping).IsSuccess);
        Assert.True(kernel.TransferRegion(owner, target, region).IsSuccess);
    }

    [Fact]
    public void ForgedExactRangeIsRejectedBeforeVisibilityProviderCall()
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        var forged = mapping with { Offset = mapping.Offset + 1 };

        var result = kernel.PreparePlatformRegionMappingForConsumer(
            owner, forged,
            PlatformMemoryConsumerClass.ExternalExecutionDomain,
            PlatformMemoryVisibilityRequirement.PublicationFence);

        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, provider.VisibilityCalls);
    }

    [Fact]
    public void ExactMappingContractsExposeNoHardwareAuthorityIdentifiers()
    {
        var forbidden = new[]
        {
            "Physical", "Pte", "PageTable", "CacheLine", "Dma", "Iommu", "Vmcs", "Lane", "Opcode",
        };
        var surfaceTypes = new[]
        {
            typeof(PlatformRegionSlice),
            typeof(PlatformProviderOwnedRegionMapping),
            typeof(PlatformRegionVisibilityRequest),
            typeof(PlatformRegionVisibilityResult),
            typeof(PlatformOwnedRegionSliceMapping),
            typeof(PlatformRegionVisibilityEvidence),
        };

        foreach (var type in surfaceTypes)
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var signature = property.PropertyType.FullName ?? property.PropertyType.Name;
            foreach (var name in forbidden)
                Assert.DoesNotContain(name, signature, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static (RuntimeKernel Kernel, ExactMappingProvider Provider, ProcessHandle Owner,
        PlatformOwnedRegionSliceMapping Mapping) CreateMappedRegion(ulong processId, ulong domainId)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, processId, domainId);
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var mapping = kernel.MapPlatformOwnedRegionSlice(
            owner, binding, capability, region.Handle, 16, 128, PlatformMemoryAccess.Read).Value!;
        return (kernel, provider, owner, mapping);
    }

    private static CapabilityId MintRegionCapability(
        RuntimeKernel kernel,
        ProcessHandle subject,
        RegionHandle region,
        CapabilityRights rights)
    {
        var process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        var capability = kernel.MintCapability(
            process.Value!.DomainId, subject, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.RegionId), rights);
        Assert.True(capability.IsSuccess, capability.Message);
        return capability.Value!.CapabilityId;
    }

    private sealed class ExactMappingProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformOwnedRegionMappingProvider,
        IPlatformRegionVisibilityProvider,
        IPlatformRegionRevocationProvider
    {
        private PlatformProviderDomainLease? _domain;
        private PlatformProviderRegionMappingLease? _mapping;
        private PlatformRegionSlice? _slice;
        private PlatformOperationIdentity? _operation;
        private ulong _nextMapping = 1;
        private ulong _nextOperation = 1;

        public int MapCalls { get; private set; }
        public Action? BeforeMapReturn { get; set; }
        public PlatformAuthorityStatus? MapStatus { get; set; }
        public int VisibilityCalls { get; private set; }
        public PlatformRegionSlice? LastSlice { get; private set; }
        public PlatformCompletionState CompletionState { get; set; } = PlatformCompletionState.Closed;
        public bool ReturnStaleCompletionGeneration { get; set; }

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("exact-mapping-test"), 4,
            PlatformAuthorityFeatures.NeutralDomainBinding |
            PlatformAuthorityFeatures.DirectOwnedRegionMapping);

        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.NeutralDomains,
                PlatformDomainContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.OwnedRegionMapping,
                PlatformOwnedRegionMappingContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.ExplicitMemoryVisibility,
                PlatformRegionVisibilityContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
        });

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
        {
            var lease = new PlatformProviderDomainLease(
                new PlatformProviderDomainLeaseId(1), new PlatformProviderLeaseGeneration(1), subject);
            _domain = lease;
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) => PlatformAuthorityResult.Ok();

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease, PlatformRegionIdentity region, PlatformMemoryAccess access)
        {
            var exact = MapOwnedRegionSlice(domainLease,
                new PlatformRegionSlice(region, 0, region.ByteLength, access));
            return exact.IsSuccess
                ? PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(exact.Value!.Lease)
                : PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(exact.Status, exact.Message!);
        }

        public PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSlice(
            PlatformProviderDomainLease domainLease, PlatformRegionSlice slice)
        {
            MapCalls++;
            LastSlice = slice;
            if (_domain != domainLease)
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    PlatformAuthorityStatus.WrongDomain, "Wrong domain lease.");

            var lease = new PlatformProviderRegionMappingLease(
                new PlatformProviderRegionMappingId(_nextMapping++),
                new PlatformProviderLeaseGeneration(1), domainLease, slice.Region, slice.Access);
            _mapping = lease;
            _slice = slice;
            BeforeMapReturn?.Invoke();
            if (MapStatus is { } status)
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    status, "Exact mapping rejected after callback.");
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Ok(
                new PlatformProviderOwnedRegionMapping(lease, slice));
        }

        public PlatformAuthorityResult<PlatformRegionVisibilityResult> PrepareRegionMappingForConsumer(
            PlatformRegionVisibilityRequest request)
        {
            VisibilityCalls++;
            if (_mapping != request.Mapping || _slice != request.Slice)
                return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(
                    PlatformAuthorityStatus.Denied, "Wrong mapping visibility request.");

            var outcome = request.Requirement == PlatformMemoryVisibilityRequirement.PublicationFence
                ? PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied
                : PlatformMemoryVisibilityOutcome.Unsupported;
            return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Ok(
                new PlatformRegionVisibilityResult(
                    request.Mapping.MappingId, request.Mapping.Generation, request.Slice,
                    request.Consumer, request.Requirement, outcome));
        }

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy) =>
            PlatformAuthorityResult.Ok();

        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
            PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy)
        {
            if (_mapping != mapping)
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                    PlatformAuthorityStatus.Denied, "Wrong mapping.");
            var operation = new PlatformOperationIdentity(
                new PlatformOperationId(_nextOperation++), new PlatformOperationGeneration(1),
                mapping.DomainLease);
            _operation = operation;
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(
                new PlatformRegionRevocationTicket(mapping.MappingId, mapping.Generation, operation));
        }

        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
            PlatformOperationIdentity operation)
        {
            if (_operation != operation)
                return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                    PlatformAuthorityStatus.Denied, "Wrong operation.");
            var generation = ReturnStaleCompletionGeneration
                ? new PlatformOperationGeneration(operation.Generation.Value + 1)
                : operation.Generation;
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(
                new PlatformCompletionReceipt(operation.OperationId, generation,
                    operation.DomainLease, CompletionState));
        }
    }
}
