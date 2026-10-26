using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Virtualization;

public sealed class OriginalLocalGuestBudgetTests
{
    [Theory]
    [InlineData("exact")] [InlineData("capacity")] [InlineData("unbudgeted")]
    [InlineData("owner-copy")] [InlineData("owner-history")]
    [InlineData("charge-copy")] [InlineData("charge-history")]
    [InlineData("unknown-region")] [InlineData("unknown-budget")]
    [InlineData("region-overlap")] [InlineData("partial-explicit-close")]
    public void ActualModelMappingOwnsQuantitativeAdmissionAndPartialCleanup(string mode)
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 789, 1133);
        ProcessHandle admin = TestFixtures.Create(kernel, 99, 999).Handle;
        CapabilityId budgetCapability = kernel.MintCapability(new(999), admin, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle? budget = mode == "unbudgeted" ? null : kernel.AdmitProcessBudget(admin, budgetCapability, owner, "local-guest",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 1024),
             new(ServiceBudgetDimension.GuestMemoryBytes, mode == "capacity" ? 511UL : 1024UL), new(ServiceBudgetDimension.RegionUses, 8)]).Value!.ProcessBudget;
        CapabilityId create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        CapabilityId capability = kernel.MintCapability(process.DomainId, owner, ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        KernelResult<GuestRegionMapping> mapped = kernel.MapGuestRegion(owner, domain.Domain, domain.MemoryCapability, capability, region.Handle, new(0, 512), GuestMemoryAccess.Read);
        VirtualDomainAuthority authority = (VirtualDomainAuthority)Field(kernel, "_virtualDomains");
        VirtualDomainAuthority.Record record = Assert.Single(authority.ForOwner(owner));
        Assert.Null(record.ChildBinding);
        if (mode == "capacity")
        {
            Assert.False(mapped.IsSuccess);
            Assert.Empty(record.Mappings);
            Assert.False(kernel.Regions.HasPlatformMappingReservation(region.Handle, new(process.DomainId, owner.Generation)));
            Assert.Equal(0UL, Used(kernel, budget!.Value, ServiceBudgetDimension.GuestMemoryBytes));
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            return;
        }
        Assert.True(mapped.IsSuccess, mapped.Message);
        if (budget is { } admitted)
        {
            Assert.Equal(512UL, Used(kernel, admitted, ServiceBudgetDimension.GuestMemoryBytes));
            Assert.Equal(512UL, Used(kernel, admitted, ServiceBudgetDimension.PinnedMappedMemoryBytes));
        }
        IDictionary owners = (IDictionary)Property(record, "LocalGuestLifetimes")!;
        GuestRegionMappingId id = mapped.Value!.Mapping.MappingId;
        object lifetime = owners[id]!;
        object? reservation = Property(lifetime, "Reservation");
        object? charge = Property(lifetime, "ChargeOwner");
        IDictionary reservations = (IDictionary)Field(kernel.Budgets, "_reservations");
        if (mode == "exact")
        {
            OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(owner, 512).Value!;
            CapabilityId foreignCapability = kernel.MintCapability(process.DomainId, owner, ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(foreign.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
            Assert.True(kernel.MapGuestRegion(owner, domain.Domain, domain.MemoryCapability, foreignCapability, foreign.Handle, new(512, 512), GuestMemoryAccess.Read).IsSuccess);
            Assert.True(kernel.CloseGuestRegionMapping(owner, domain.Domain, domain.MemoryCapability, mapped.Value.Mapping).IsSuccess);
            Assert.Single(record.Mappings);
            Assert.Equal(512UL, Used(kernel, budget!.Value, ServiceBudgetDimension.GuestMemoryBytes));
            Assert.True(kernel.Regions.HasPlatformMappingReservation(foreign.Handle, new(process.DomainId, owner.Generation)));
            Assert.True(kernel.ReleaseRegion(owner, region).IsSuccess);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == foreign.Handle).State);
        }
        else if (mode == "region-overlap")
        {
            KernelResult<GuestRegionMapping> overlap = kernel.MapGuestRegion(owner, domain.Domain, domain.MemoryCapability, capability, region.Handle, new(512, 512), GuestMemoryAccess.Read);
            Assert.False(overlap.IsSuccess);
            Assert.Single(record.Mappings);
            Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle, new(process.DomainId, owner.Generation)));
            Assert.Equal(512UL, Used(kernel, budget!.Value, ServiceBudgetDimension.GuestMemoryBytes));
        }
        else if (mode == "partial-explicit-close")
        {
            BudgetReservationHandle exact = (BudgetReservationHandle)reservation!;
            reservations[exact.ReservationId] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(charge, null);
            Assert.False(kernel.CloseGuestRegionMapping(owner, domain.Domain, domain.MemoryCapability, mapped.Value.Mapping).IsSuccess);
            Assert.True((bool)Property(lifetime, "RegionReleased")!);
            Assert.False(authority.ResolveMapping(record, mapped.Value.Mapping).IsSuccess);
            Assert.Equal(512UL, Used(kernel, budget!.Value, ServiceBudgetDimension.GuestMemoryBytes));
            Assert.False(kernel.CloseGuestRegionMapping(owner, domain.Domain, domain.MemoryCapability, mapped.Value.Mapping).IsSuccess);
            reservations[exact.ReservationId] = charge;
            Assert.True(kernel.CloseGuestRegionMapping(owner, domain.Domain, domain.MemoryCapability, mapped.Value.Mapping).IsSuccess);
            Assert.Empty(record.Mappings);
            Assert.Equal(0UL, Used(kernel, budget.Value, ServiceBudgetDimension.GuestMemoryBytes));
        }
        else if (mode != "unbudgeted")
        {
            if (mode == "owner-copy") owners[id] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(lifetime, null);
            if (mode == "owner-history") owners.Remove(id);
            if (mode == "charge-copy") reservations[((BudgetReservationHandle)reservation!).ReservationId] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(charge, null);
            if (mode == "charge-history") reservations.Remove(((BudgetReservationHandle)reservation!).ReservationId);
            if (mode == "unknown-region") Set(lifetime, "RegionInvocationKnown", false);
            if (mode == "unknown-budget") { Set(lifetime, "AdmissionKnown", false); Set(lifetime, "Reservation", null); }
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(512UL, Used(kernel, budget!.Value, ServiceBudgetDimension.GuestMemoryBytes));
            Assert.Equal(mode is "charge-copy" or "charge-history" or "unknown-budget", (bool)Property(lifetime, "RegionReleased")!);
            owners[id] = lifetime;
            if (reservation is BudgetReservationHandle exact) reservations[exact.ReservationId] = charge;
            Set(lifetime, "RegionInvocationKnown", true); Set(lifetime, "AdmissionKnown", true); Set(lifetime, "Reservation", reservation);
        }
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
        Assert.Equal(1, provider.RevokeVirtualDomainCallCount);
        if (budget is { } completed) Assert.All(kernel.QueryBudget(completed).Value!.Usage, usage => Assert.Equal(0UL, usage.Used));
    }

    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Field(object owner, string name) => owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static object? Property(object owner, string name) => owner.GetType().GetProperty(name, Private | BindingFlags.Public)!.GetValue(owner);
    private static void Set(object owner, string name, object? value) => owner.GetType().GetProperty(name, Private | BindingFlags.Public)!.SetValue(owner, value);
    private static ulong Used(RuntimeKernel kernel, BudgetAccountHandle budget, ServiceBudgetDimension dimension) => kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == dimension).Used;
}
