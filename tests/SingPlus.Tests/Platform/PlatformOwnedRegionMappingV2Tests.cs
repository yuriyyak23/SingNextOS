using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformOwnedRegionMappingV2Tests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ForeignCompletedRegionFactCannotConsumeOriginalOwnedMemoryCharge(int fault)
    {
        BudgetAccountHandle budget = default;
        var s = CreateMappedRegion(738, 1080, (kernel, owner) =>
        {
            var admin = TestFixtures.Create(kernel, 99, 999).Handle;
            var capability = kernel.MintCapability(new DomainId(999), admin,
                ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
                CapabilityRights.Configure).Value!.CapabilityId;
            budget = kernel.AdmitProcessBudget(admin, capability, owner, "original-region-owner",
                [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 256)]).Value!.ProcessBudget;
        });
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var routes = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_regionBudgetReservations", flags)!.GetValue(s.Kernel)!;
        var region = Assert.Single(s.Kernel.Regions.Snapshot()).Handle;
        var charge = ((ProcessHandle Owner, BudgetReservationHandle Reservation))routes[region]!;
        var reservations = (global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority).GetField("_reservations", flags)!.GetValue(s.Kernel.Budgets)!;
        var reservation = reservations[charge.Reservation.ReservationId]!;
        var source = reservation.GetType().GetProperty("OriginalRegionOwner")!;
        var original = source.GetValue(reservation)!;
        object? other;
        var originalHandle = reservation.GetType().GetProperty("OriginalRegion")!;
        if (fault == 1) other = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        else if (fault is 2 or 3) other = null;
        else
        {
            var foreign = CreateMappedRegion(738, 1080);
            Assert.True(foreign.Kernel.TerminateProcess(foreign.Owner).IsSuccess);
            Assert.Equal(region, Assert.Single(foreign.Kernel.Regions.Snapshot()).Handle);
            var foreignRecords = (global::System.Collections.IDictionary)typeof(RegionAuthority).GetField("_regions", flags)!.GetValue(foreign.Kernel.Regions)!;
            other = foreignRecords[region.RegionId]!;
        }
        source.SetValue(reservation, other);
        if (fault == 3) originalHandle.SetValue(reservation, null);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value.IsComplete);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(1024UL, s.Kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        source.SetValue(reservation, original);
        originalHandle.SetValue(reservation, region);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.All(s.Kernel.QueryBudget(budget).Value!.Usage, u => Assert.Equal(0UL, u.Used));
        Assert.Equal(1, s.Provider.BeginCalls);
        Assert.Equal(1, s.Provider.ObserveCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RegionChargeRouteLossCannotCompleteTeardownWithOriginalChargeHeld(bool foreignRoute)
    {
        BudgetAccountHandle budget = default;
        var s = CreateMappedRegion(737, 1070, (kernel, owner) =>
        {
            var admin = TestFixtures.Create(kernel, 99, 999).Handle;
            var capability = kernel.MintCapability(new DomainId(999), admin,
                ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
                CapabilityRights.Configure).Value!.CapabilityId;
            budget = kernel.AdmitProcessBudget(admin, capability, owner, "original-region-charge",
                [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 256)]).Value!.ProcessBudget;
        });
        var routes = (global::System.Collections.IDictionary)typeof(RuntimeKernel)
            .GetField("_regionBudgetReservations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        var region = Assert.Single(s.Kernel.Regions.Snapshot()).Handle;
        var original = routes[region]!;
        if (foreignRoute)
        {
            var other = s.Kernel.Budgets.Reserve(s.Owner, [new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 1)],
                BudgetReservationLifetime.LocalResource, AdmissionQosHint.None).Value!.Reservation;
            Assert.True(s.Kernel.ReleaseBudget(s.Owner, other).IsSuccess);
            routes[region] = (s.Owner, other);
        }
        else routes.Remove(region);
        var terminated = s.Kernel.TerminateProcess(s.Owner);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(!foreignRoute, terminated.IsSuccess);
        Assert.Equal(foreignRoute ? 1024UL : 0UL, s.Kernel.QueryBudget(budget).Value!.Usage
            .Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        if (foreignRoute)
        {
            Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value.IsComplete);
            routes[region] = original;
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.All(s.Kernel.QueryBudget(budget).Value!.Usage, u => Assert.Equal(0UL, u.Used));
        }
        Assert.Equal(1, s.Provider.BeginCalls);
        Assert.Equal(1, s.Provider.ObserveCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void LostMappingChargeIndexCannotConsumeOriginalQuantitativeFact(int fault)
    {
        BudgetAccountHandle budget = default;
        var s = CreateMappedRegion(735, 1050, (kernel, owner) =>
        {
            var admin = TestFixtures.Create(kernel, 99, 999).Handle;
            var administration = kernel.MintCapability(new DomainId(999), admin,
                ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
                CapabilityRights.Configure).Value!.CapabilityId;
            budget = kernel.AdmitProcessBudget(admin, administration, owner, "original-mapping-index",
                [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 256)]).Value!.ProcessBudget;
        });
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var charges = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_mappingBudgetReservations", flags)!.GetValue(s.Kernel)!;
        var original = charges[s.Mapping.Mapping.MappingId]!;
        if (fault == 1)
        {
            var other = s.Kernel.Budgets.Reserve(s.Owner, [new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 1)],
                BudgetReservationLifetime.LocalResource, AdmissionQosHint.None).Value!.Reservation;
            Assert.True(s.Kernel.ReleaseBudget(s.Owner, other).IsSuccess);
            charges[s.Mapping.Mapping.MappingId] = (s.Owner, other);
        }
        else charges.Remove(s.Mapping.Mapping.MappingId);
        if (fault == 2)
        {
            var mappings = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_mappings", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
            var record = mappings[s.Mapping.Mapping.MappingId]!;
            record.GetType().GetProperty("ExpectsBudgetReservation")!.SetValue(record, false);
        }
        var revoked = s.Kernel.RevokePlatformRegionMapping(s.Owner, s.Mapping);
        var ended = s.Kernel.TerminateProcess(s.Owner);
        Assert.Equal(128UL, s.Kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.False(revoked.IsSuccess);
        Assert.False(ended.IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        charges[s.Mapping.Mapping.MappingId] = original;
        var retry = s.Kernel.ObservePlatformRegionMappingRevocation(s.Owner, s.Mapping.Mapping);
        Assert.True(retry.IsSuccess, $"{retry.Error}: {retry.Message}");
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.All(s.Kernel.QueryBudget(budget).Value!.Usage, u => Assert.Equal(0UL, u.Used));
        Assert.Equal(1, s.Provider.BeginCalls);
        Assert.Equal(1, s.Provider.ObserveCalls);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ConsumedOriginalMappingChargeNeedsNoNewAdmissionButRejectsForeignFact(int fault)
    {
        BudgetAccountHandle budget = default;
        var s = CreateMappedRegion(736, 1060, (kernel, owner) =>
        {
            var admin = TestFixtures.Create(kernel, 99, 999).Handle;
            var administration = kernel.MintCapability(new DomainId(999), admin,
                ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
                CapabilityRights.Configure).Value!.CapabilityId;
            budget = kernel.AdmitProcessBudget(admin, administration, owner, "consumed-original-charge",
                [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 256)]).Value!.ProcessBudget;
        });
        Assert.True(s.Kernel.RevokePlatformRegionMapping(s.Owner, s.Mapping).IsSuccess);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var mappings = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_mappings", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        var mappingOwner = mappings[s.Mapping.Mapping.MappingId]!;
        var field = mappingOwner.GetType().GetProperty("OriginalBudgetReservationOwner")!;
        var original = field.GetValue(mappingOwner)!;
        var handle = (BudgetReservationHandle)original.GetType().GetProperty("Handle")!.GetValue(original)!;
        var routes = (global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority).GetField("_reservations", flags)!.GetValue(s.Kernel.Budgets)!;
        routes.Remove(handle.ReservationId);
        if (fault == 1)
        {
            var foreign = CreateMappedRegion(736, 1060, (kernel, owner) =>
            {
                var admin = TestFixtures.Create(kernel, 99, 999).Handle;
                var administration = kernel.MintCapability(new DomainId(999), admin,
                    ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
                    CapabilityRights.Configure).Value!.CapabilityId;
                Assert.True(kernel.AdmitProcessBudget(admin, administration, owner, "consumed-original-charge",
                    [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 256)]).IsSuccess);
            });
            Assert.True(foreign.Kernel.RevokePlatformRegionMapping(foreign.Owner, foreign.Mapping).IsSuccess);
            var foreignRoutes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_mappings", flags)!.GetValue(foreign.Kernel.PlatformAuthority)!;
            var foreignMappingOwner = foreignRoutes[foreign.Mapping.Mapping.MappingId]!;
            var other = field.GetValue(foreignMappingOwner)!;
            Assert.Equal(handle, other.GetType().GetProperty("Handle")!.GetValue(other));
            field.SetValue(mappingOwner, other);
        }
        if (fault == 2) field.SetValue(mappingOwner, typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null));
        if (fault == 3) field.SetValue(mappingOwner, null);
        s.Provider.Incarnation = new(2);
        s.Provider.BeforeIncarnationRead = () => throw new InvalidOperationException("Consumed lifetime does not require new admission.");
        var consumed = s.Kernel.ObservePlatformRegionMappingRevocation(s.Owner, s.Mapping.Mapping);
        Assert.Equal(fault == 0, consumed.IsSuccess);
        field.SetValue(mappingOwner, original);
        Assert.True(s.Kernel.ObservePlatformRegionMappingRevocation(s.Owner, s.Mapping.Mapping).IsSuccess);
        Assert.Equal(0UL, s.Kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(1, s.Provider.BeginCalls);
        Assert.Equal(1, s.Provider.ObserveCalls);
        // The parent lifetime remains unfinished and retains actual backing.
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void LostConfiguredProcessAttachmentCannotBecomeUnbudgetedMapping(bool exactSlice, bool foreignRoute)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel, 732, 1020).Handle;
        var foreignOwner = TestFixtures.Create(kernel, 733, 1030).Handle;
        var admin = TestFixtures.Create(kernel, 99, 999).Handle;
        var administration = kernel.MintCapability(new DomainId(999), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "configured-original",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 1024)]).Value!.ProcessBudget;
        var otherBudget = kernel.AdmitProcessBudget(admin, administration, foreignOwner, "foreign-attached",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 1024)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!.Handle;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region, CapabilityRights.Map | CapabilityRights.Read);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var routes = (global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority).GetField("_processAccounts", flags)!.GetValue(kernel.Budgets)!;
        if (foreignRoute) routes[owner] = otherBudget;
        else routes.Remove(owner);
        Assert.True(kernel.Budgets.HasProcessAccount(owner));
        Assert.False(kernel.AllocateBuffer<byte>(owner, 1).IsSuccess);
        Assert.Single(kernel.Regions.Snapshot());
        var error = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(owner, binding, capability, region, 16, 128, PlatformMemoryAccess.Read).Error
            : kernel.MapPlatformOwnedRegion(owner, binding, capability, region, PlatformMemoryAccess.Read).Error;
        Assert.NotEqual(KernelError.None, error);
        Assert.Equal(0, provider.MapCalls);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(0UL, kernel.QueryBudget(otherBudget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.False(kernel.Regions.HasPlatformMappingReservation(region, new RegionOwner(binding.Subject.DomainId, owner.Generation)));
        routes[owner] = budget;
        var mapped = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(owner, binding, capability, region, 16, 128, PlatformMemoryAccess.Read).Value!.Mapping
            : kernel.MapPlatformOwnedRegion(owner, binding, capability, region, PlatformMemoryAccess.Read).Value!;
        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapped).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.MapCalls);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LateAttachmentLossRefusesMappingUseButDoesNotRepinExactClosure(bool exactSlice)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel, 734, 1040).Handle;
        var admin = TestFixtures.Create(kernel, 99, 999).Handle;
        var administration = kernel.MintCapability(new DomainId(999), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "late-attachment",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 1024)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!.Handle;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region, CapabilityRights.Map | CapabilityRights.Read);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var routes = (global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority).GetField("_processAccounts", flags)!.GetValue(kernel.Budgets)!;
        provider.BeforeMapReturn = () => routes.Remove(owner);
        var mapping = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(owner, binding, capability, region, 16, 128, PlatformMemoryAccess.Read).Value!.Mapping
            : kernel.MapPlatformOwnedRegion(owner, binding, capability, region, PlatformMemoryAccess.Read).Value!;
        Assert.False(kernel.PlatformAuthority.ValidateMapping(mapping, binding.Subject).IsSuccess);
        Assert.True(kernel.Budgets.HasProcessAccount(owner));
        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapping).IsSuccess);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, u => Assert.Equal(0UL, u.Used));
        Assert.Equal(1, provider.MapCalls);
        Assert.Equal(1, provider.BeginCalls);
        Assert.Equal(1, provider.ObserveCalls);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    [InlineData(false, 3)] [InlineData(true, 3)]
    public void LateQuantitativeOwnerLossRetainsReceiptButRefusesMappingUse(bool exactSlice, int fault)
    {
        static (RuntimeKernel Kernel, ExactMappingProvider Provider, ProcessHandle Owner,
            RegionHandle Region, PlatformDomainBinding Binding, CapabilityId Capability) Create()
        {
            var provider = new ExactMappingProvider();
            var kernel = new RuntimeKernel(provider);
            var owner = TestFixtures.Create(kernel, 730, 1000).Handle;
            var admin = TestFixtures.Create(kernel, 99, 999).Handle;
            var administration = kernel.MintCapability(new DomainId(999), admin,
                ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
                CapabilityRights.Configure).Value!.CapabilityId;
            Assert.True(kernel.AdmitProcessBudget(admin, administration, owner, "late-original-budget",
                [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 1024)]).IsSuccess);
            var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!.Handle;
            return (kernel, provider, owner, region, kernel.BindPlatformDomain(owner).Value!,
                MintRegionCapability(kernel, owner, region, CapabilityRights.Map | CapabilityRights.Read));
        }
        PlatformRegionMapping Map((RuntimeKernel Kernel, ExactMappingProvider Provider, ProcessHandle Owner,
            RegionHandle Region, PlatformDomainBinding Binding, CapabilityId Capability) s) => exactSlice
            ? s.Kernel.MapPlatformOwnedRegionSlice(s.Owner, s.Binding, s.Capability, s.Region, 16, 128, PlatformMemoryAccess.Read).Value!.Mapping
            : s.Kernel.MapPlatformOwnedRegion(s.Owner, s.Binding, s.Capability, s.Region, PlatformMemoryAccess.Read).Value!;
        var first = Create();
        var other = Create();
        var foreignMapping = Map(other);
        Assert.True(other.Kernel.RevokePlatformRegionMapping(other.Owner, foreignMapping).IsSuccess);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        global::System.Collections.IDictionary Records(RuntimeKernel k, string field) =>
            (global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority).GetField(field, flags)!.GetValue(k.Budgets)!;
        object Reservation(RuntimeKernel k) => Records(k, "_reservations").Values.Cast<object>().Single(r =>
            (BudgetReservationLifetime)r.GetType().GetProperty("Lifetime")!.GetValue(r)! == BudgetReservationLifetime.PlatformMapping);
        var accounts = Records(first.Kernel, "_accounts");
        var rootKey = accounts.Keys.Cast<object>().Single(key =>
            (BudgetAccountLevel)accounts[key]!.GetType().GetProperty("Level")!.GetValue(accounts[key])! == BudgetAccountLevel.System);
        var originalRoot = accounts[rootKey]!;
        object? original = null;
        BudgetReservationHandle handle = default;
        var gate = typeof(ResourceBudgetAuthority).GetField("_gate", flags)!.GetValue(first.Kernel.Budgets)!;
        first.Provider.BeforeMapReturn = () =>
        {
            Assert.False(Monitor.IsEntered(gate));
            original = Reservation(first.Kernel);
            handle = (BudgetReservationHandle)original.GetType().GetProperty("Handle")!.GetValue(original)!;
            if (fault == 0) Records(first.Kernel, "_reservations")[handle.ReservationId] = Reservation(other.Kernel);
            if (fault == 1) Records(first.Kernel, "_reservations").Remove(handle.ReservationId);
            if (fault == 2) accounts[rootKey] = Records(other.Kernel, "_accounts")[rootKey];
            if (fault == 3) accounts.Remove(rootKey);
        };
        var mapping = Map(first);
        Assert.False(first.Kernel.PlatformAuthority.ValidateMapping(mapping, first.Binding.Subject).IsSuccess);
        var visibility = first.Kernel.PreparePlatformRegionMappingForConsumer(first.Owner,
            new PlatformOwnedRegionSliceMapping(mapping, exactSlice ? 16 : 0, exactSlice ? 128 : 1024),
            PlatformMemoryConsumerClass.ExternalExecutionDomain, PlatformMemoryVisibilityRequirement.PublicationFence);
        Assert.False(visibility.IsSuccess);
        Assert.Equal(0, first.Provider.VisibilityCalls);
        var originalUsage = (Dictionary<ServiceBudgetDimension, ulong>)originalRoot.GetType().GetProperty("Used")!.GetValue(originalRoot)!;
        Assert.Equal(1024UL, originalUsage[ServiceBudgetDimension.OwnedMemoryBytes]);
        Assert.Equal(exactSlice ? 128UL : 1024UL, originalUsage[ServiceBudgetDimension.PinnedMappedMemoryBytes]);
        Assert.True(first.Kernel.Regions.HasPlatformMappingReservation(first.Region,
            new RegionOwner(first.Binding.Subject.DomainId, first.Owner.Generation)));
        var charges = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_mappingBudgetReservations", flags)!.GetValue(first.Kernel)!;
        Assert.True(charges.Contains(mapping.MappingId));
        Assert.False(first.Kernel.RevokePlatformRegionMapping(first.Owner, mapping).IsSuccess);
        Assert.True(charges.Contains(mapping.MappingId));
        Assert.Equal(exactSlice ? 128UL : 1024UL, originalUsage[ServiceBudgetDimension.PinnedMappedMemoryBytes]);
        Records(first.Kernel, "_reservations")[handle.ReservationId] = original;
        accounts[rootKey] = originalRoot;
        Assert.True(first.Kernel.ObservePlatformRegionMappingRevocation(first.Owner, mapping).IsSuccess);
        Assert.False(charges.Contains(mapping.MappingId));
        Assert.True(first.Kernel.TerminateProcess(first.Owner).IsSuccess);
        Assert.All(originalUsage.Values, used => Assert.Equal(0UL, used));
        Assert.Equal(1, first.Provider.MapCalls);
        Assert.Equal(1, first.Provider.BeginCalls);
        Assert.Equal(1, first.Provider.ObserveCalls);
        Assert.Equal(RegionState.Released, Assert.Single(first.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FinalMappingPublicationWaitsForQuantitativeOwnerTransaction(bool exactSlice)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel, 731, 1010).Handle;
        var admin = TestFixtures.Create(kernel, 99, 999).Handle;
        var administration = kernel.MintCapability(new DomainId(999), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, owner, "mapping-owner-transaction",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 1024)]).IsSuccess);
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!.Handle;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region, CapabilityRights.Map | CapabilityRights.Read);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var gate = typeof(ResourceBudgetAuthority).GetField("_gate", flags)!.GetValue(kernel.Budgets)!;
        var records = (global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority).GetField("_reservations", flags)!.GetValue(kernel.Budgets)!;
        using var effectReturned = new ManualResetEventSlim();
        using var continueReturn = new ManualResetEventSlim();
        provider.BeforeMapReturn = () =>
        {
            Assert.False(Monitor.IsEntered(gate));
            effectReturned.Set();
            Assert.True(continueReturn.Wait(TimeSpan.FromSeconds(5)));
        };
        var worker = Task.Run(() => exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(owner, binding, capability, region, 16, 128, PlatformMemoryAccess.Read).Value!.Mapping
            : kernel.MapPlatformOwnedRegion(owner, binding, capability, region, PlatformMemoryAccess.Read).Value!);
        Assert.True(effectReturned.Wait(TimeSpan.FromSeconds(5)));
        bool completedInsideTransaction;
        lock (gate)
        {
            var original = records.Values.Cast<object>().Single(r =>
                (BudgetReservationLifetime)r.GetType().GetProperty("Lifetime")!.GetValue(r)! == BudgetReservationLifetime.PlatformMapping);
            var handle = (BudgetReservationHandle)original.GetType().GetProperty("Handle")!.GetValue(original)!;
            records.Remove(handle.ReservationId);
            try
            {
                continueReturn.Set();
                completedInsideTransaction = worker.Wait(TimeSpan.FromMilliseconds(100));
            }
            finally { records[handle.ReservationId] = original; }
        }
        Assert.False(completedInsideTransaction);
        Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
        var mapping = worker.GetAwaiter().GetResult();
        Assert.True(kernel.PlatformAuthority.ValidateMapping(mapping, binding.Subject).IsSuccess);
        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapping).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.MapCalls);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(1, 0)] [InlineData(2, 0)]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(2, 1)]
    [InlineData(0, 2)] [InlineData(1, 2)] [InlineData(2, 2)]
    public void NewMappingCannotAdmitAgainstForeignQuantitativeAccount(int level, int fault)
    {
        static (RuntimeKernel Kernel, ExactMappingProvider Provider, ProcessHandle Owner,
            PlatformOwnedRegionSliceMapping Mapping) Create() => CreateMappedRegion(729, 990, (kernel, owner) =>
        {
            var admin = TestFixtures.Create(kernel, 99, 999).Handle;
            var administration = kernel.MintCapability(new DomainId(999), admin,
                ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
                CapabilityRights.Configure).Value!.CapabilityId;
            Assert.True(kernel.AdmitProcessBudget(admin, administration, owner, "original-mapping-admission",
                [new(ServiceBudgetDimension.OwnedMemoryBytes, 2048), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 256)]).IsSuccess);
        });
        var first = Create();
        var other = Create();
        var region = first.Kernel.AllocateBuffer<byte>(first.Owner, 1024).Value!.Handle;
        Assert.True(other.Kernel.AllocateBuffer<byte>(other.Owner, 1024).IsSuccess);
        Assert.True(other.Kernel.RevokePlatformRegionMapping(other.Owner, other.Mapping).IsSuccess);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var accounts = (global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority).GetField("_accounts", flags)!.GetValue(first.Kernel.Budgets)!;
        var foreign = (global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority).GetField("_accounts", flags)!.GetValue(other.Kernel.Budgets)!;
        var key = accounts.Keys.Cast<object>().Single(key =>
            (int)(BudgetAccountLevel)accounts[key]!.GetType().GetProperty("Level")!.GetValue(accounts[key])! == level);
        var original = accounts[key]!;
        var originalUsed = (Dictionary<ServiceBudgetDimension, ulong>)original.GetType().GetProperty("Used")!.GetValue(original)!;
        var before = originalUsed.ToArray();
        if (fault == 0) accounts[key] = foreign[key];
        if (fault == 1) accounts.Remove(key);
        if (fault == 2) accounts[key] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        var binding = first.Mapping.Mapping.DomainBinding;
        var capability = MintRegionCapability(first.Kernel, first.Owner, region, CapabilityRights.Map | CapabilityRights.Read);
        var mapped = first.Kernel.MapPlatformOwnedRegionSlice(first.Owner, binding, capability,
            region, 160, 128, PlatformMemoryAccess.Read);
        Assert.False(mapped.IsSuccess);
        Assert.True(mapped.Error is KernelError.PlatformFaulted or KernelError.StaleGeneration, $"{mapped.Error}: {mapped.Message}");
        Assert.Equal(1, first.Provider.MapCalls);
        Assert.Equal(before, originalUsed.ToArray());
        var retained = (BudgetReservationSnapshot[])typeof(ResourceBudgetAuthority)
            .GetMethod("InspectionSnapshot", flags)!.Invoke(first.Kernel.Budgets, null)!;
        Assert.Equal(3, retained.Length);
        accounts[key] = original;
        Assert.True(first.Kernel.RevokePlatformRegionMapping(first.Owner, first.Mapping).IsSuccess);
        var retry = first.Kernel.MapPlatformOwnedRegionSlice(first.Owner, binding, capability,
            region, 160, 128, PlatformMemoryAccess.Read);
        Assert.True(retry.IsSuccess, retry.Message);
        Assert.Equal(2, first.Provider.MapCalls);
        Assert.True(first.Kernel.RevokePlatformRegionMapping(first.Owner, retry.Value!).IsSuccess);
        Assert.True(first.Kernel.TerminateProcess(first.Owner).IsSuccess);
        Assert.All(originalUsed.Values, used => Assert.Equal(0UL, used));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ForeignReleasedBudgetOwnerCannotConsumeOriginalMappingChargeAssociation(int fault)
    {
        (RuntimeKernel Kernel, ExactMappingProvider Provider, ProcessHandle Owner,
            PlatformOwnedRegionSliceMapping Mapping, Func<ulong[]> Usage) Create()
        {
            Func<ulong[]> usage = () => [];
            var s = CreateMappedRegion(728, 980, (kernel, owner) =>
            {
                var admin = TestFixtures.Create(kernel, 99, 990).Handle;
                var administration = kernel.MintCapability(new DomainId(990), admin,
                    ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
                    CapabilityRights.Configure).Value!.CapabilityId;
                var budget = kernel.AdmitProcessBudget(admin, administration, owner, "original-mapping-charge",
                    [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024),
                        new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 128)]).Value!.ProcessBudget;
                var accounts = (global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority)
                    .GetField("_accounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.Budgets)!;
                var originalAccount = accounts[budget.AccountId]!;
                var originalUsage = (Dictionary<ServiceBudgetDimension, ulong>)originalAccount.GetType()
                    .GetProperty("Used")!.GetValue(originalAccount)!;
                usage = () =>
                {
                    return [originalUsage[ServiceBudgetDimension.OwnedMemoryBytes],
                        originalUsage[ServiceBudgetDimension.PinnedMappedMemoryBytes]];
                };
            });
            return (s.Kernel, s.Provider, s.Owner, s.Mapping, usage);
        }
        var first = Create();
        var other = Create();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        global::System.Collections.IDictionary Reservations(RuntimeKernel kernel) =>
            (global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority)
                .GetField("_reservations", flags)!.GetValue(kernel.Budgets)!;
        object MappingReservation(RuntimeKernel kernel) => Reservations(kernel).Values.Cast<object>().Single(item =>
            (BudgetReservationLifetime)item.GetType().GetProperty("Lifetime")!.GetValue(item)! == BudgetReservationLifetime.PlatformMapping);
        var original = MappingReservation(first.Kernel);
        Assert.True(other.Kernel.RevokePlatformRegionMapping(other.Owner, other.Mapping).IsSuccess);
        var foreign = MappingReservation(other.Kernel);
        var handle = (BudgetReservationHandle)original.GetType().GetProperty("Handle")!.GetValue(original)!;
        Assert.Equal(handle, foreign.GetType().GetProperty("Handle")!.GetValue(foreign));
        Assert.Equal(BudgetReservationState.Released, foreign.GetType().GetProperty("State")!.GetValue(foreign));
        var accounts = (global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority)
            .GetField("_accounts", flags)!.GetValue(first.Kernel.Budgets)!;
        var otherAccounts = (global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority)
            .GetField("_accounts", flags)!.GetValue(other.Kernel.Budgets)!;
        var rootKey = accounts.Keys.Cast<object>().Single(key =>
            (BudgetAccountLevel)accounts[key]!.GetType().GetProperty("Level")!.GetValue(accounts[key])! == BudgetAccountLevel.System);
        var originalRoot = accounts[rootKey]!;
        if (fault == 0) Reservations(first.Kernel)[handle.ReservationId] = foreign;
        if (fault == 1) Reservations(first.Kernel).Remove(handle.ReservationId);
        if (fault == 2) Reservations(first.Kernel)[handle.ReservationId] = typeof(object)
            .GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        if (fault == 3) accounts[rootKey] = otherAccounts[rootKey];
        if (fault == 4) accounts.Remove(rootKey);
        if (fault is 3 or 4)
            Assert.False(first.Kernel.QueryBudget((BudgetAccountHandle)original.GetType().GetProperty("Account")!.GetValue(original)!).IsSuccess);
        var refused = first.Kernel.RevokePlatformRegionMapping(first.Owner, first.Mapping);
        var charges = (global::System.Collections.IDictionary)typeof(RuntimeKernel)
            .GetField("_mappingBudgetReservations", flags)!.GetValue(first.Kernel)!;
        Assert.Equal(new ulong[] { 1024, 128 }, first.Usage());
        Assert.True(charges.Contains(first.Mapping.Mapping.MappingId));
        Assert.False(refused.IsSuccess);
        Assert.True(first.Kernel.QueryPlatformRegionMappingLifecycle(first.Owner, first.Mapping.Mapping).Value.LocalReservationReleased);
        Reservations(first.Kernel)[handle.ReservationId] = original;
        accounts[rootKey] = originalRoot;
        Assert.True(first.Kernel.ObservePlatformRegionMappingRevocation(first.Owner, first.Mapping.Mapping).IsSuccess);
        Assert.Equal(new ulong[] { 1024, 0 }, first.Usage());
        Assert.True(first.Kernel.TerminateProcess(first.Owner).IsSuccess);
        Assert.Equal(new ulong[] { 0, 0 }, first.Usage());
        Assert.Equal(1, first.Provider.BeginCalls);
        Assert.Equal(1, first.Provider.ObserveCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PublicMapRootReadWaitsForOriginalOwnerTransaction(bool exactSlice)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 727, 970);
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!;
        var canonical = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeSubjects", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = canonical[binding.Subject]!;
        using var started = new ManualResetEventSlim();
        Task<KernelResult> worker;
        bool completedInsideTransaction;
        provider.BeforeMapReturn = () => Assert.False(Monitor.IsEntered(gate));
        provider.BeforeIncarnationRead = () => Assert.False(Monitor.IsEntered(gate));
        lock (gate)
        {
            canonical.Remove(binding.Subject);
            try
            {
                worker = Task.Factory.StartNew(() =>
                {
                    started.Set();
                    if (exactSlice)
                    {
                        var mapped = kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
                            region.Handle, 0, 1024, PlatformMemoryAccess.Read);
                        return mapped.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(mapped.Error, mapped.Message!);
                    }
                    var whole = kernel.MapPlatformOwnedRegion(owner, binding, capability,
                        region.Handle, PlatformMemoryAccess.Read);
                    return whole.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(whole.Error, whole.Message!);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
                completedInsideTransaction = worker.Wait(TimeSpan.FromMilliseconds(250));
                Assert.Equal(0, provider.MapCalls);
            }
            finally { canonical[binding.Subject] = original; }
        }
        Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(completedInsideTransaction);
        Assert.True(worker.Result.IsSuccess, worker.Result.Message);
        Assert.Equal(1, provider.MapCalls);
        Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Fact]
    public void PublicVisibilityReadCannotObserveIntermediateOwnerMetadataTransaction()
    {
        var s = CreateMappedRegion(726, 960);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var bridge = s.Kernel.PlatformAuthority;
        var gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(bridge)!;
        var metadata = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_exactMappingSlices", flags)!.GetValue(bridge)!;
        var original = metadata[s.Mapping.Mapping.MappingId]!;
        using var started = new ManualResetEventSlim();
        Task<KernelResult<PlatformRegionVisibilityEvidence>> worker;
        bool completedInsideTransaction;
        s.Provider.BeforeVisibilityReturn = () => Assert.False(Monitor.IsEntered(gate));
        lock (gate)
        {
            metadata.Remove(s.Mapping.Mapping.MappingId);
            try
            {
                worker = Task.Factory.StartNew(() =>
                {
                    started.Set();
                    return s.Kernel.PreparePlatformRegionMappingForConsumer(s.Owner, s.Mapping,
                        PlatformMemoryConsumerClass.ExternalExecutionDomain,
                        PlatformMemoryVisibilityRequirement.PublicationFence);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
                completedInsideTransaction = worker.Wait(TimeSpan.FromMilliseconds(250));
                Assert.Equal(0, s.Provider.VisibilityCalls);
            }
            finally { metadata[s.Mapping.Mapping.MappingId] = original; }
        }
        Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(completedInsideTransaction);
        Assert.True(worker.Result.IsSuccess, worker.Result.Message);
        Assert.Equal(1, s.Provider.VisibilityCalls);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void ForeignCompletedRootMappingOwnerCannotReleaseOriginalReservation(bool duringSourceRead, bool absent)
    {
        var first = CreateMappedRegion(724, 940);
        var other = CreateMappedRegion(724, 940);
        Assert.Equal(first.Mapping, other.Mapping);
        first.Provider.CompletionState = PlatformCompletionState.Draining;
        Assert.False(first.Kernel.RevokePlatformRegionMapping(first.Owner, first.Mapping).IsSuccess);
        Assert.True(other.Kernel.PlatformAuthority.BeginRegionMappingRevocation(other.Mapping.Mapping,
            other.Mapping.Mapping.DomainBinding.Subject, PlatformRegionRevocationPolicy.DrainBeforeRevoke).IsSuccess);
        Assert.True(other.Kernel.PlatformAuthority.ObserveRegionMappingRevocation(other.Mapping.Mapping,
            other.Mapping.Mapping.DomainBinding.Subject).IsSuccess);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var routes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_mappings", flags)!.GetValue(first.Kernel.PlatformAuthority)!;
        var foreignRoutes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_mappings", flags)!.GetValue(other.Kernel.PlatformAuthority)!;
        var original = routes[first.Mapping.Mapping.MappingId]!;
        var foreign = foreignRoutes[other.Mapping.Mapping.MappingId]!;
        Assert.NotSame(original, foreign);
        void LoseRoute() { if (absent) routes.Remove(first.Mapping.Mapping.MappingId); else routes[first.Mapping.Mapping.MappingId] = foreign; }
        if (duringSourceRead)
            first.Provider.BeforeIncarnationRead = () =>
            {
                first.Provider.BeforeIncarnationRead = null;
                LoseRoute();
            };
        else LoseRoute();
        Assert.False(first.Kernel.ObservePlatformRegionMappingRevocation(first.Owner, first.Mapping.Mapping).IsSuccess);
        Assert.True(first.Kernel.Regions.HasPlatformMappingReservation(first.Mapping.Mapping.Region,
            new RegionOwner(new DomainId(940), first.Owner.Generation)));
        Assert.False(first.Kernel.PlatformAuthority.RevokeDomain(first.Mapping.Mapping.DomainBinding,
            first.Mapping.Mapping.DomainBinding.Subject).IsSuccess);
        Assert.Equal(0, first.Provider.DomainRevokeCalls);
        first.Provider.BeforeIncarnationRead = null;
        routes[first.Mapping.Mapping.MappingId] = original;
        first.Provider.CompletionState = PlatformCompletionState.Closed;
        Assert.True(first.Kernel.ObservePlatformRegionMappingRevocation(first.Owner, first.Mapping.Mapping).IsSuccess);
        Assert.True(first.Kernel.TerminateProcess(first.Owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(first.Kernel.Regions.Snapshot()).State);
        Assert.Equal(1, first.Provider.BeginCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ClosedRootProjectionRequiresOriginalClosureFactAtActualRelease(int fault)
    {
        Func<ulong[]> usage = () => [];
        var s = CreateMappedRegion(725, 950, (kernel, owner) =>
        {
            var admin = TestFixtures.Create(kernel, 98, 980).Handle;
            var administration = kernel.MintCapability(new DomainId(980), admin,
                ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
                CapabilityRights.Configure).Value!.CapabilityId;
            var budget = kernel.AdmitProcessBudget(admin, administration, owner, "root-original-closure",
                [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024),
                    new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 128)]).Value!.ProcessBudget;
            usage = () =>
            {
                var actual = kernel.QueryBudget(budget).Value!.Usage;
                return [actual.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used,
                    actual.Single(u => u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used];
            };
        });
        s.Provider.CompletionState = PlatformCompletionState.Draining;
        Assert.False(s.Kernel.RevokePlatformRegionMapping(s.Owner, s.Mapping).IsSuccess);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var routes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_mappings", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        var original = routes[s.Mapping.Mapping.MappingId]!;
        var state = original.GetType().GetProperty("ClosureState")!;
        var receipt = original.GetType().GetProperty("KnownClosureReceipt")!;
        var begin = original.GetType().GetProperty("RevocationBeginObservation")!;
        var originalBegin = begin.GetValue(original);
        object? originalReceipt = null;
        if (fault == 0) state.SetValue(original, PlatformExternalClosureState.Closed);
        else
        {
            s.Provider.CompletionState = PlatformCompletionState.Closed;
            s.Provider.BeforeIncarnationRead = () =>
            {
                if ((PlatformExternalClosureState)state.GetValue(original)! != PlatformExternalClosureState.Closed) return;
                s.Provider.BeforeIncarnationRead = null;
                originalReceipt = receipt.GetValue(original);
                if (fault == 1) receipt.SetValue(original, null);
                else begin.SetValue(original, null);
            };
        }
        var refused = s.Kernel.ObservePlatformRegionMappingRevocation(s.Owner, s.Mapping.Mapping);
        Assert.True(s.Kernel.Regions.HasPlatformMappingReservation(s.Mapping.Mapping.Region,
            new RegionOwner(new DomainId(950), s.Owner.Generation)));
        Assert.False(refused.IsSuccess);
        Assert.Equal(new ulong[] { 1024, 128 }, usage());
        s.Provider.BeforeIncarnationRead = null;
        if (fault == 0) { state.SetValue(original, PlatformExternalClosureState.Draining); s.Provider.CompletionState = PlatformCompletionState.Closed; }
        else { receipt.SetValue(original, originalReceipt); begin.SetValue(original, originalBegin); }
        var observations = s.Provider.ObserveCalls;
        Assert.True(s.Kernel.ObservePlatformRegionMappingRevocation(s.Owner, s.Mapping.Mapping).IsSuccess);
        Assert.Equal(observations + (fault == 0 ? 1 : 0), s.Provider.ObserveCalls);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(new ulong[] { 0, 0 }, usage());
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(false, 3)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)] [InlineData(true, 3)]
    public void ReservedLastRootIdentitySurvivesUnknownMalformedAndLatePublicationOutcomes(bool exactSlice, int outcome)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 723, 930);
        var admin = TestFixtures.Create(kernel, 98, 980).Handle;
        var administration = kernel.MintCapability(new DomainId(980), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "root-slot-outcome",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var next = typeof(PlatformAuthorityBridge).GetField("_nextMappingId", flags)!;
        next.SetValue(kernel.PlatformAuthority, ulong.MaxValue - 1);
        provider.MapStatus = outcome == 1 ? PlatformAuthorityStatus.Denied : null;
        provider.ReturnMalformedMappingAccess = outcome == 2;
        provider.BeforeMapReturn = () =>
        {
            if (outcome == 0) throw new InvalidOperationException("Original mapping outcome unavailable.");
            if (outcome == 3) provider.ThrowIncarnation = true;
        };
        var success = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
                region.Handle, 0, 4096, PlatformMemoryAccess.Read).IsSuccess
            : kernel.MapPlatformOwnedRegion(owner, binding, capability,
                region.Handle, PlatformMemoryAccess.Read).IsSuccess;
        Assert.Equal(outcome == 3 || (exactSlice && outcome == 2), success);
        var mappings = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_mappings", flags)!.GetValue(kernel.PlatformAuthority)!;
        var retained = Assert.Single(mappings.Values.Cast<object>());
        var identity = (PlatformRegionMapping)retained.GetType().GetProperty("Mapping")!.GetValue(retained)!;
        Assert.Equal(ulong.MaxValue - 1, identity.MappingId.Value);
        Assert.Equal(ulong.MaxValue, next.GetValue(kernel.PlatformAuthority));
        Assert.False(kernel.PlatformAuthority.ValidateMapping(identity, binding.Subject).IsSuccess);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle,
            new RegionOwner(process.DomainId, owner.Generation)));
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(u =>
            u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(1, provider.MapCalls);
        provider.ThrowIncarnation = false;
        provider.BeforeMapReturn = null;
        Assert.Equal(outcome == 3, kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(outcome == 3 ? RegionState.Released : RegionState.Owned,
            kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
        Assert.Equal(ulong.MaxValue, next.GetValue(kernel.PlatformAuthority));
        Assert.Equal(1, provider.MapCalls);
    }

    [Theory]
    [InlineData(false, 0UL)] [InlineData(false, ulong.MaxValue)]
    [InlineData(true, 0UL)] [InlineData(true, ulong.MaxValue)]
    public void ExhaustedRootMappingIdentityRefusesBeforeEffectAndPreservesQuantitativeTruth(bool exactSlice, ulong seed)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 721, 910);
        var admin = TestFixtures.Create(kernel, 98, 980).Handle;
        var administration = kernel.MintCapability(new DomainId(980), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "root-identity-capacity",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var next = typeof(PlatformAuthorityBridge).GetField("_nextMappingId", flags)!;
        next.SetValue(kernel.PlatformAuthority, seed);
        var error = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
                region.Handle, 0, 4096, PlatformMemoryAccess.Read).Error
            : kernel.MapPlatformOwnedRegion(owner, binding, capability,
                region.Handle, PlatformMemoryAccess.Read).Error;
        Assert.Equal(KernelError.CapacityExhausted, error);
        Assert.Equal(0, provider.MapCalls);
        Assert.Equal(seed, next.GetValue(kernel.PlatformAuthority));
        Assert.False(kernel.Regions.HasPlatformMappingReservation(region.Handle,
            new RegionOwner(process.DomainId, owner.Generation)));
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(u =>
            u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(u =>
            u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        // Repair only injected capacity; normal receipt and actual reclaim remain usable.
        next.SetValue(kernel.PlatformAuthority, 1UL);
        Assert.True(kernel.MapPlatformOwnedRegion(owner, binding, capability,
            region.Handle, PlatformMemoryAccess.Read).IsSuccess);
        Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LastRootMappingIdentitySlotIsReservedBeforeCrossApiProviderEffect(bool firstExact)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 722, 920);
        var admin = TestFixtures.Create(kernel, 98, 980).Handle;
        var administration = kernel.MintCapability(new DomainId(980), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "root-last-slot",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8192),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 8192)]).Value!.ProcessBudget;
        var firstRegion = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var secondRegion = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var firstCapability = MintRegionCapability(kernel, owner, firstRegion.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var secondCapability = MintRegionCapability(kernel, owner, secondRegion.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var next = typeof(PlatformAuthorityBridge).GetField("_nextMappingId", flags)!;
        next.SetValue(kernel.PlatformAuthority, ulong.MaxValue - 1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var callbacks = 0;
        provider.BeforeMapReturn = () =>
        {
            if (Interlocked.Increment(ref callbacks) != 1) return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        var first = Task.Run(() =>
        {
            if (firstExact)
            {
                var mapped = kernel.MapPlatformOwnedRegionSlice(owner, binding, firstCapability,
                    firstRegion.Handle, 0, 4096, PlatformMemoryAccess.Read);
                Assert.True(mapped.IsSuccess, mapped.Message);
                return mapped.Value!.Mapping;
            }
            var full = kernel.MapPlatformOwnedRegion(owner, binding, firstCapability,
                firstRegion.Handle, PlatformMemoryAccess.Read);
            Assert.True(full.IsSuccess, full.Message);
            return full.Value!;
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var denied = firstExact
                ? kernel.MapPlatformOwnedRegion(owner, binding, secondCapability,
                    secondRegion.Handle, PlatformMemoryAccess.Read).Error
                : kernel.MapPlatformOwnedRegionSlice(owner, binding, secondCapability,
                    secondRegion.Handle, 0, 4096, PlatformMemoryAccess.Read).Error;
            Assert.Equal(KernelError.CapacityExhausted, denied);
            Assert.Equal(1, provider.MapCalls);
            Assert.Equal(ulong.MaxValue, next.GetValue(kernel.PlatformAuthority));
            var regionOwner = new RegionOwner(process.DomainId, owner.Generation);
            Assert.True(kernel.Regions.HasPlatformMappingReservation(firstRegion.Handle, regionOwner));
            Assert.False(kernel.Regions.HasPlatformMappingReservation(secondRegion.Handle, regionOwner));
            Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(u =>
                u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        }
        finally { release.Set(); }
        var admitted = await first;
        Assert.Equal(ulong.MaxValue - 1, admitted.MappingId.Value);
        Assert.Equal(ulong.MaxValue, next.GetValue(kernel.PlatformAuthority));
        Assert.True(kernel.RevokeCapability(firstCapability).IsSuccess);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(u =>
            u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.All(kernel.Regions.Snapshot(), r => Assert.Equal(RegionState.Released, r.State));
        Assert.Equal(1, callbacks);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(false, 3)]
    [InlineData(false, 4)] [InlineData(false, 5)] [InlineData(false, 6)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)] [InlineData(true, 3)]
    [InlineData(true, 4)] [InlineData(true, 5)] [InlineData(true, 6)]
    public void NotAcceptedMappingConsumptionRequiresOriginalFreshSourceAndDoesNotRepinCompletedFact(bool exactSlice, int fault)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 720, 900);
        var admin = TestFixtures.Create(kernel, 98, 980).Handle;
        var administration = kernel.MintCapability(new DomainId(980), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "root-no-effect-source",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = domains[binding.BindingId]!;
        var bridgeGate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!
            .GetValue(kernel.PlatformAuthority)!;
        var kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(kernel)!;
        var regionOwner = new RegionOwner(process.DomainId, owner.Generation);
        var regionGate = kernel.Regions.GetPlatformMappingCompletionGate(region.Handle, regionOwner).Value!;
        var sawSource = false;
        provider.MapStatus = PlatformAuthorityStatus.NotAccepted;
        provider.BeforeMapReturn = () =>
        {
            if (fault == 1) provider.ThrowIncarnation = true;
            if (fault == 2) provider.Incarnation = new(2);
            provider.BeforeIncarnationRead = () =>
            {
                provider.BeforeIncarnationRead = null;
                sawSource = true;
                Assert.False(Monitor.IsEntered(bridgeGate));
                Assert.False(Monitor.IsEntered(kernelGate));
                Assert.False(Monitor.IsEntered(regionGate));
                if (fault == 3) domains.Remove(binding.BindingId);
                if (fault == 4) domains[binding.BindingId] = original.GetType()
                    .GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                if (fault == 5) Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
                if (fault == 6)
                {
                    var worker = Task.Run(() => kernel.PlatformAuthority.RevokeDomain(binding, binding.Subject));
                    Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
                    Assert.Equal(KernelError.PlatformBindingActive, worker.Result.Error);
                }
            };
        };
        var error = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
                region.Handle, 0, 4096, PlatformMemoryAccess.Read).Error
            : kernel.MapPlatformOwnedRegion(owner, binding, capability,
                region.Handle, PlatformMemoryAccess.Read).Error;
        Assert.Equal(KernelError.PlatformFaulted, error);
        var pinned = fault is >= 1 and <= 5;
        Assert.Equal(pinned, kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        Assert.Equal(pinned ? 4096UL : 0UL, kernel.QueryBudget(budget).Value!.Usage.Single(u =>
            u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
        Assert.Equal(0, original.GetType().GetProperty("PendingMappings")!.GetValue(original));
        Assert.True(sawSource);
        Assert.Equal(1, provider.MapCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
        provider.BeforeIncarnationRead = null;
        provider.BeforeMapReturn = null;
        provider.ThrowIncarnation = false;
        if (fault != 5) domains[binding.BindingId] = original;
        if (pinned)
        {
            provider.Incarnation = new(1);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
            Assert.Equal(1, provider.MapCalls);
        }
        else
        {
            // A later epoch/source change cannot undo lawful completed no-effect consumption.
            Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
            provider.Incarnation = new(2);
            Assert.True(kernel.ReleaseRegion(owner, region).IsSuccess);
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
            Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(u =>
                u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    [InlineData(false, null)] [InlineData(true, null)]
    public void FailedRootMappingReplyWithLostRoutePinsActualBackingAndCharge(bool exactSlice, bool? foreign)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 719, 890);
        var admin = TestFixtures.Create(kernel, 98, 980).Handle;
        var administration = kernel.MintCapability(new DomainId(980), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "unknown-root-source",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = domains[binding.BindingId]!;
        provider.MapStatus = PlatformAuthorityStatus.Denied;
        provider.BeforeMapReturn = () =>
        {
            if (foreign == true) domains[binding.BindingId] = original.GetType()
                .GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            else if (foreign == false) domains.Remove(binding.BindingId);
        };
        var error = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
                region.Handle, 0, 4096, PlatformMemoryAccess.Read).Error
            : kernel.MapPlatformOwnedRegion(owner, binding, capability,
                region.Handle, PlatformMemoryAccess.Read).Error;
        Assert.Equal(foreign is null ? KernelError.PlatformDenied : KernelError.PlatformFaulted, error);
        Assert.Equal(1, provider.MapCalls);
        Assert.Equal(0, original.GetType().GetProperty("PendingMappings")!.GetValue(original));
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle,
            new RegionOwner(process.DomainId, owner.Generation)));
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(u =>
            u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        var mappings = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_mappings", flags)!.GetValue(kernel.PlatformAuthority)!;
        var retained = Assert.Single(mappings.Values.Cast<object>());
        Assert.Equal(PlatformExternalClosureState.Faulted, retained.GetType().GetProperty("ClosureState")!.GetValue(retained));
        domains[binding.BindingId] = original;
        provider.BeforeMapReturn = null;
        provider.MapStatus = null;
        Assert.False(kernel.MapPlatformOwnedRegion(owner, binding, capability,
            region.Handle, PlatformMemoryAccess.Read).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
        Assert.Equal(1, provider.MapCalls);
        Assert.Equal(0, provider.BeginCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(false, 3)] [InlineData(false, 4)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)] [InlineData(true, 3)] [InlineData(true, 4)]
    public void UnavailableRootMappingAdmissionSourceBalancesStagingWithoutProviderEffect(bool exactSlice, int fault)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 718, 880);
        var admin = TestFixtures.Create(kernel, 98, 980).Handle;
        var administration = kernel.MintCapability(new DomainId(980), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "root-source-no-effect",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        var subjects = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeSubjects", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = domains[binding.BindingId]!;
        provider.BeforeIncarnationRead = () =>
        {
            provider.BeforeIncarnationRead = null;
            if (fault == 0) provider.ThrowIncarnation = true;
            if (fault == 1) domains.Remove(binding.BindingId);
            if (fault is 2 or 3)
            {
                var foreign = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                domains[binding.BindingId] = foreign;
                if (fault == 3) subjects[binding.Subject] = foreign;
            }
            if (fault == 4) provider.Incarnation = new(2);
        };
        var error = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
                region.Handle, 0, 4096, PlatformMemoryAccess.Read).Error
            : kernel.MapPlatformOwnedRegion(owner, binding, capability,
                region.Handle, PlatformMemoryAccess.Read).Error;
        Assert.Equal(KernelError.PlatformFaulted, error);
        Assert.Equal(0, provider.MapCalls);
        Assert.False(kernel.Regions.HasPlatformMappingReservation(region.Handle,
            new RegionOwner(process.DomainId, owner.Generation)));
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(u =>
            u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(u =>
            u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Empty((global::System.Collections.IDictionary)typeof(RuntimeKernel)
            .GetField("_pendingPlatformMappingAdmissions", flags)!.GetValue(kernel)!);
        Assert.Empty((global::System.Collections.IEnumerable)typeof(RuntimeKernel)
            .GetField("_pendingPlatformMappingCapabilities", flags)!.GetValue(kernel)!);
        provider.ThrowIncarnation = false;
        provider.Incarnation = new(1);
        domains[binding.BindingId] = original;
        subjects[binding.Subject] = original;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    [InlineData(false, 3)] [InlineData(false, 4)] [InlineData(false, 5)]
    [InlineData(true, 3)] [InlineData(true, 4)] [InlineData(true, 5)]
    public void KnownRootMappingSurvivesLateSourceFaultWithoutPermissionOrFalseReclaim(bool exactSlice, int fault)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 717, 870);
        var admin = TestFixtures.Create(kernel, 98, 980).Handle;
        var administration = kernel.MintCapability(new DomainId(980), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "late-root-source",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = domains[binding.BindingId]!;
        var subjects = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeSubjects", flags)!.GetValue(kernel.PlatformAuthority)!;
        var gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!
            .GetValue(kernel.PlatformAuthority)!;
        var callbackOutside = false;
        void LoseSource()
        {
            callbackOutside = !Monitor.IsEntered(gate);
            if (fault == 0) provider.ThrowIncarnation = true;
            if (fault is 1 or 3) domains.Remove(binding.BindingId);
            if (fault is 2 or 4) domains[binding.BindingId] = original.GetType()
                .GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            if (fault == 5) subjects.Remove(binding.Subject);
        }
        provider.BeforeMapReturn = () =>
        {
            if (fault is 3 or 4) LoseSource();
            else provider.BeforeIncarnationRead = () =>
            {
                provider.BeforeIncarnationRead = null;
                LoseSource();
            };
        };
        PlatformRegionMapping mapping;
        if (exactSlice)
        {
            var result = kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
                region.Handle, 0, 4096, PlatformMemoryAccess.Read);
            Assert.True(result.IsSuccess, result.Message);
            mapping = result.Value!.Mapping;
        }
        else
        {
            var result = kernel.MapPlatformOwnedRegion(owner, binding, capability,
                region.Handle, PlatformMemoryAccess.Read);
            Assert.True(result.IsSuccess, result.Message);
            mapping = result.Value!;
        }
        Assert.True(callbackOutside);
        Assert.Equal(1, provider.MapCalls);
        Assert.Equal(0, original.GetType().GetProperty("PendingMappings")!.GetValue(original));
        var mappings = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_mappings", flags)!.GetValue(kernel.PlatformAuthority)!;
        var retained = mappings[mapping.MappingId]!;
        Assert.Equal(typeof(ExactMappingProvider).GetField("_mapping", flags)!.GetValue(provider),
            retained.GetType().GetProperty("ProviderLease")!.GetValue(retained));
        Assert.False(kernel.PlatformAuthority.ValidateMapping(mapping, binding.Subject).IsSuccess);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle,
            new RegionOwner(process.DomainId, owner.Generation)));
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(u =>
            u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(0, provider.BeginCalls);
        // Same-record repair can retry an unavailable query; it is not durable recovery.
        domains[binding.BindingId] = original;
        subjects[binding.Subject] = original;
        provider.ThrowIncarnation = false;
        provider.BeforeMapReturn = null;
        Assert.Equal(fault == 0, kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(fault == 0 ? RegionState.Released : RegionState.Owned,
            kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
        Assert.Equal(fault == 0 ? 0UL : 4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(u =>
            u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(1, provider.MapCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExhaustedRootMappingInterlockRefusesEffectAndReleasesOnlyStagedCharges(bool exactSlice)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 716, 860);
        var admin = TestFixtures.Create(kernel, 98, 980).Handle;
        var administration = kernel.MintCapability(new DomainId(980), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, owner, "root-interlock",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 4096)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 4096).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        var record = domains[binding.BindingId]!;
        var pending = record.GetType().GetProperty("PendingMappings")!;
        var next = typeof(PlatformAuthorityBridge).GetField("_nextMappingId", flags)!;
        var nextBefore = next.GetValue(kernel.PlatformAuthority);
        pending.SetValue(record, int.MaxValue);
        var denied = exactSlice
            ? kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
                region.Handle, 0, 4096, PlatformMemoryAccess.Read).Error
            : kernel.MapPlatformOwnedRegion(owner, binding, capability,
                region.Handle, PlatformMemoryAccess.Read).Error;
        Assert.Equal(KernelError.CapacityExhausted, denied);
        Assert.Equal(int.MaxValue, pending.GetValue(record));
        Assert.Equal(nextBefore, next.GetValue(kernel.PlatformAuthority));
        Assert.Equal(0, provider.MapCalls);
        Assert.False(kernel.Regions.HasPlatformMappingReservation(region.Handle,
            new RegionOwner(process.DomainId, owner.Generation)));
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(u =>
            u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(u =>
            u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Empty((global::System.Collections.IDictionary)typeof(RuntimeKernel)
            .GetField("_pendingPlatformMappingAdmissions", flags)!.GetValue(kernel)!);
        Assert.Empty((global::System.Collections.IEnumerable)typeof(RuntimeKernel)
            .GetField("_pendingPlatformMappingCapabilities", flags)!.GetValue(kernel)!);
        // Remove only injected saturation; consume a real mapping and its exact cleanup.
        pending.SetValue(record, 0);
        if (exactSlice)
            Assert.True(kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
                region.Handle, 0, 4096, PlatformMemoryAccess.Read).IsSuccess);
        else
            Assert.True(kernel.MapPlatformOwnedRegion(owner, binding, capability,
                region.Handle, PlatformMemoryAccess.Read).IsSuccess);
        Assert.Equal(1, provider.MapCalls);
        Assert.Equal(0, pending.GetValue(record));
        Assert.Equal(4096UL, kernel.QueryBudget(budget).Value!.Usage.Single(u =>
            u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(u =>
            u.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.Denied)]
    [InlineData(PlatformAuthorityStatus.Stale)]
    [InlineData(PlatformAuthorityStatus.NotAccepted)]
    public void FailedOriginalBeginPinsMappingAndTeardownWithoutRepeat(PlatformAuthorityStatus status)
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(723, 843);
        provider.BeginFailureAfterOperation = status;
        Assert.False(kernel.RevokePlatformRegionMapping(owner, mapping).IsSuccess);
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_mappings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        var record = records[mapping.Mapping.MappingId]!;
        var raw = record.GetType().GetProperty("RevocationBeginObservation")!.GetValue(record)!;
        Assert.Equal(status, raw.GetType().GetProperty("Status")!.GetValue(raw));
        Assert.Equal(PlatformExternalClosureState.Faulted, kernel.QueryPlatformRegionMappingLifecycle(owner, mapping.Mapping).Value.PlatformClosure);
        provider.BeginFailureAfterOperation = null;
        Assert.False(kernel.RevokePlatformRegionMapping(owner, mapping).IsSuccess);
        Assert.False(kernel.ObservePlatformRegionMappingRevocation(owner, mapping.Mapping).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(mapping.Region,
            new RegionOwner(mapping.Mapping.DomainBinding.Subject.DomainId, owner.Generation)));
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == mapping.Region).State);
        Assert.Equal(1, provider.BeginCalls);
        Assert.Equal(0, provider.ObserveCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
    }

    [Theory]
    [InlineData(false, "absent")][InlineData(false, "replacement")][InlineData(false, "pending")]
    [InlineData(true, "absent")][InlineData(true, "replacement")][InlineData(true, "pending")]
    public void MappingCleanupSourceOwnerLossRefusesCallbackAndRegionTransfer(bool observing, string fault)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 721, 841);
        var (_, target) = TestFixtures.Create(kernel, 722, 842);
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle, CapabilityRights.Map | CapabilityRights.Read);
        var mapping = kernel.MapPlatformOwnedRegionSlice(owner, binding, capability, region.Handle, 16, 128, PlatformMemoryAccess.Read).Value!;
        if (observing)
        {
            provider.CompletionState = PlatformCompletionState.Draining;
            Assert.False(kernel.RevokePlatformRegionMapping(owner, mapping).IsSuccess);
            Assert.Equal(1, provider.BeginCalls);
            Assert.Equal(1, provider.ObserveCalls);
            provider.CompletionState = PlatformCompletionState.Closed;
        }
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_mappings", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = records[mapping.Mapping.MappingId]!;
        provider.BeforeIncarnationRead = () =>
        {
            provider.BeforeIncarnationRead = null;
            if (fault == "absent") records.Remove(mapping.Mapping.MappingId);
            if (fault == "replacement") records[mapping.Mapping.MappingId] = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            if (fault == "pending") original.GetType().GetProperty("ClosureTransitionInFlight")!.SetValue(original, false);
        };
        var result = observing ? kernel.ObservePlatformRegionMappingRevocation(owner, mapping.Mapping)
            : kernel.RevokePlatformRegionMapping(owner, mapping);
        Assert.False(result.IsSuccess);
        Assert.Equal(observing ? 1 : 0, provider.BeginCalls);
        Assert.Equal(observing ? 1 : 0, provider.ObserveCalls);
        Assert.Equal(false, original.GetType().GetProperty("ClosureTransitionInFlight")!.GetValue(original));
        Assert.Null(original.GetType().GetProperty("KnownClosureReceipt")!.GetValue(original));
        Assert.True(kernel.Regions.HasPlatformMappingReservation(region.Handle, new RegionOwner(process.DomainId, owner.Generation)));
        Assert.False(kernel.TransferRegion(owner, target, region).IsSuccess);
        Assert.True(region.IsValid);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapabilityMappingClosureAllowsWorkerAndRetainsOriginalFault(bool fault)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 720, 840);
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var mapping = kernel.MapPlatformOwnedRegionSlice(owner, binding, capability,
            region.Handle, 16, 128, PlatformMemoryAccess.Read).Value!;
        var observed = false;
        provider.BeforeBegin = () =>
        {
            var worker = Task.Run(() => kernel.MintCapability(process.DomainId, owner,
                ResourceKind.Compute, "compute/mapping-closure", CapabilityRights.Execute));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(worker.Result.IsSuccess);
            Assert.Equal(KernelError.PlatformBindingRevoked,
                kernel.PlatformAuthority.ValidateMapping(mapping.Mapping, binding.Subject).Error);
            observed = true;
            if (fault) throw new InvalidOperationException("Injected mapping closure receipt loss.");
        };
        var result = kernel.RevokeCapability(capability);
        Assert.True(observed);
        Assert.Equal(1, provider.BeginCalls);
        Assert.Equal(!fault, result.IsSuccess);
        var regionOwner = new RegionOwner(process.DomainId, owner.Generation);
        Assert.Equal(fault, kernel.Regions.HasPlatformMappingReservation(region.Handle, regionOwner));
        if (fault)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            provider.BeforeBegin = null;
            Assert.False(kernel.RevokeCapability(capability).IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
            Assert.Equal(1, provider.BeginCalls);
            Assert.Equal(0, provider.DomainRevokeCalls);
        }
        else Assert.True(kernel.TerminateProcess(owner).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void VisibilitySourceContinuityRefusesLateEvidenceAndUsesActualCleanup(int fault)
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        var subject = mapping.Mapping.DomainBinding.Subject;
        var regionOwner = new RegionOwner(subject.DomainId, owner.Generation);
        var kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        var bridgeGate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        provider.BeforeIncarnationRead = () =>
        {
            Assert.False(Monitor.IsEntered(kernelGate));
            Assert.False(Monitor.IsEntered(bridgeGate));
        };
        if (fault == 4) provider.Incarnation = new(2);
        provider.BeforeVisibilityReturn = () =>
        {
            if (fault == 0) provider.Incarnation = new(2);
            if (fault == 1) provider.ThrowIncarnation = true;
            if (fault == 2) Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
            if (fault == 3) throw new InvalidOperationException("Visibility reply lost after effect.");
        };
        Assert.False(kernel.PreparePlatformRegionMappingForConsumer(owner, mapping,
            PlatformMemoryConsumerClass.ExternalExecutionDomain, PlatformMemoryVisibilityRequirement.PublicationFence).IsSuccess);
        Assert.Equal(fault == 4 ? 0 : 1, provider.VisibilityCalls);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(mapping.Region, regionOwner));
        provider.BeforeIncarnationRead = null;
        provider.BeforeVisibilityReturn = null;
        provider.ThrowIncarnation = false;
        if (fault is 1 or 3)
        {
            Assert.True(kernel.RevokePlatformRegionMapping(owner, mapping).IsSuccess);
            Assert.Equal(1, provider.BeginCalls);
            Assert.Equal(1, provider.ObserveCalls);
            Assert.False(kernel.Regions.HasPlatformMappingReservation(mapping.Region, regionOwner));
        }
        else
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.True(kernel.Regions.HasPlatformMappingReservation(mapping.Region, regionOwner));
            Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
            Assert.Equal(0, provider.BeginCalls);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task VisibilityPublicationWaitingForRegionRechecksBridgeWriter(bool reset)
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        var subject = mapping.Mapping.DomainBinding.Subject;
        var regionOwner = new RegionOwner(subject.DomainId, owner.Generation);
        var regionGate = kernel.Regions.GetPlatformMappingCompletionGate(mapping.Region, regionOwner).Value!;
        var kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task? holder = null;
        provider.BeforeVisibilityReturn = () =>
        {
            Assert.False(Monitor.IsEntered(kernelGate));
            Assert.False(Monitor.IsEntered(regionGate));
            holder = Task.Factory.StartNew(() =>
            {
                lock (regionGate) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        };
        var publication = Task.Run(() => kernel.PreparePlatformRegionMappingForConsumer(owner, mapping,
            PlatformMemoryConsumerClass.ExternalExecutionDomain, PlatformMemoryVisibilityRequirement.PublicationFence));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() =>
            {
                if (!Monitor.TryEnter(kernelGate)) return true;
                Monitor.Exit(kernelGate);
                return false;
            }, TimeSpan.FromSeconds(3)));
            Assert.False(publication.IsCompleted);
            if (reset) Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
            else Assert.True(kernel.PlatformAuthority.RevokeExactMappingLocalAuthorization(mapping.Mapping, subject).IsSuccess);
        }
        finally { release.Set(); }
        await holder!;
        Assert.False((await publication).IsSuccess);
        Assert.Equal(1, provider.VisibilityCalls);
        provider.BeforeVisibilityReturn = null;
        if (reset)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.True(kernel.Regions.HasPlatformMappingReservation(mapping.Region, regionOwner));
            Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        }
        else
        {
            Assert.True(kernel.RevokePlatformRegionMapping(owner, mapping).IsSuccess);
            Assert.False(kernel.Regions.HasPlatformMappingReservation(mapping.Region, regionOwner));
            Assert.Equal(1, provider.BeginCalls);
            Assert.Equal(1, provider.ObserveCalls);
        }
    }

    [Fact]
    public void VisibilityLocalCompositionRequiresActualRegionOwnerGate()
    {
        var (kernel, _, owner, mapping) = CreateMappedRegion(709, 790);
        var subject = mapping.Mapping.DomainBinding.Subject;
        Assert.False(kernel.PlatformAuthority.ValidateVisibilityPublication(mapping, subject, kernel.Regions).IsSuccess);
        var gate = kernel.Regions.GetPlatformMappingCompletionGate(mapping.Region,
            new RegionOwner(subject.DomainId, owner.Generation)).Value!;
        lock (gate) Assert.True(kernel.PlatformAuthority.ValidateVisibilityPublication(mapping, subject, kernel.Regions).IsSuccess);
        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapping).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void VisibilityReplyAfterExitOrDamageCannotPublishAndActualCleanupStillRuns(bool exits)
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        var subject = mapping.Mapping.DomainBinding.Subject;
        provider.BeforeVisibilityReturn = () =>
        {
            if (exits) Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            else Assert.True(kernel.Regions.QuarantineSubrange(mapping.Mapping.Region,
                new RegionOwner(subject.DomainId, owner.Generation),
                new(1, new("visibility-provider", "bank", 1, 1), 1,
                    ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8))).IsSuccess);
        };
        var result = kernel.PreparePlatformRegionMappingForConsumer(owner, mapping,
            PlatformMemoryConsumerClass.ExternalExecutionDomain, PlatformMemoryVisibilityRequirement.PublicationFence);
        Assert.False(result.IsSuccess);
        Assert.Equal(1, provider.VisibilityCalls);
        provider.BeforeVisibilityReturn = null;
        if (!exits) Assert.True(kernel.RevokePlatformRegionMapping(owner, mapping).IsSuccess);
        Assert.Equal(1, provider.BeginCalls);
        Assert.Equal(1, provider.ObserveCalls);
        if (exits) Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        else Assert.Single(kernel.Regions.SnapshotDamage());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DirectDomainDetachProviderPauseBlocksStartAndRebindButAllowsExit(bool exits)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 709, 790);
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(kernel)!;
        var bridgeGate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        provider.BeforeDomainRevoke = () =>
        {
            Assert.False(Monitor.IsEntered(kernelGate));
            Assert.False(Monitor.IsEntered(bridgeGate));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        var detach = Task.Run(() => kernel.RevokePlatformDomain(owner, binding));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await Task.Run(() =>
            {
                Assert.False(kernel.StartProcess(owner).IsSuccess);
                Assert.False(kernel.BindPlatformDomain(owner).IsSuccess);
                Assert.False(kernel.RevokePlatformDomain(owner, binding).IsSuccess);
                if (exits)
                {
                    Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                    Assert.True(kernel.QueryProcessTeardown(owner).IsSuccess);
                    Assert.True(kernel.ObserveProcessTeardown(owner).IsSuccess);
                    Assert.False(kernel.QueryProcessTeardown(owner).Value.LocalReclaimCompleted);
                }
                Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
            }).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { release.Set(); }
        Assert.Equal(!exits, (await detach).IsSuccess);
        provider.BeforeDomainRevoke = null;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.DomainRevokeCalls);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        var transactions = (global::System.Collections.IEnumerable)typeof(RuntimeKernel).GetField("_executionTransactions", flags)!.GetValue(kernel)!;
        Assert.Empty(transactions.Cast<object>());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DirectDomainDetachLostReplyOrResetKeepsUnknownEffectPinnedAndBalancesInterlock(bool reset)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 709, 790);
        _ = kernel.AllocateBuffer<byte>(owner, 1024).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        provider.AfterDomainRevoke = () =>
        {
            if (reset) Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
            else throw new IOException("Direct revoke response lost after effect");
        };
        Assert.False(kernel.RevokePlatformDomain(owner, binding).IsSuccess);
        provider.AfterDomainRevoke = null;
        Assert.False(kernel.RevokePlatformDomain(owner, binding).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.DomainRevokeCalls);
        Assert.True(provider.DomainClosed);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        var transactions = (global::System.Collections.IEnumerable)typeof(RuntimeKernel).GetField("_executionTransactions",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        Assert.Empty(transactions.Cast<object>());
    }

    [Fact]
    public async Task ProcessParentDomainPauseAllowsConcurrentQueriesBeforeActualClosureAndReclaim()
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(kernel)!;
        var bridgeGate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        provider.BeforeDomainRevoke = () =>
        {
            Assert.False(Monitor.IsEntered(kernelGate));
            Assert.False(Monitor.IsEntered(bridgeGate));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        var progression = Task.Run(() => kernel.TerminateProcess(owner));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await Task.Run(() =>
            {
                var snapshot = kernel.QueryProcessTeardown(owner).Value;
                Assert.False(snapshot.PlatformDomainClosed);
                Assert.False(snapshot.LocalReclaimCompleted);
                Assert.True(kernel.ObserveProcessTeardown(owner).IsSuccess);
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.False(kernel.BindPlatformDomain(owner).IsSuccess);
                Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
                Assert.False(provider.DomainClosed);
                Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
            }).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { release.Set(); }
        Assert.True((await progression).IsSuccess);
        Assert.True(provider.DomainClosed);
        Assert.False(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(1, provider.DomainRevokeCalls);
        Assert.Equal(1, provider.BeginCalls);
        Assert.Equal(1, provider.ObserveCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ProcessParentDomainUnknownReplyOrResetPinsBackingWithoutDuplicateRevoke(int fault)
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        var kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        Action callback = () =>
        {
            Assert.False(Monitor.IsEntered(kernelGate));
            Assert.True(kernel.QueryProcessTeardown(owner).IsSuccess);
            if (fault == 2) Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
            else throw new IOException("Parent-domain revoke reply unavailable");
        };
        if (fault == 0) provider.BeforeDomainRevoke = callback; else provider.AfterDomainRevoke = callback;
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        provider.BeforeDomainRevoke = null;
        provider.AfterDomainRevoke = null;
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        var snapshot = kernel.QueryProcessTeardown(owner).Value;
        Assert.False(snapshot.PlatformDomainClosed);
        Assert.False(snapshot.LocalReclaimCompleted);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(fault != 0, provider.DomainClosed);
        Assert.Equal(1, provider.DomainRevokeCalls);
        if (fault == 2)
        {
            var consumed = kernel.QueryPlatformRegionMappingLifecycle(owner, mapping.Mapping);
            Assert.True(consumed.IsSuccess);
            Assert.True(consumed.Value.LocalReservationReleased);
            // The parent's unknown closure still pins actual backing above.
        }
    }

    [Fact]
    public void ProcessParentDomainGenericFailurePinsOriginalLeaseWithoutUnprovenRetry()
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        provider.DomainRevokeStatus = PlatformAuthorityStatus.Denied;
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(provider.DomainClosed);
        Assert.False(kernel.QueryProcessTeardown(owner).Value.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        var cleanup = kernel.QueryPlatformRegionMappingLifecycle(owner, mapping.Mapping);
        Assert.True(cleanup.IsSuccess);
        Assert.True(cleanup.Value.LocalReservationReleased);
        Assert.False(kernel.PlatformAuthority.ValidateMapping(mapping.Mapping, mapping.Mapping.DomainBinding.Subject).IsSuccess);
        var stale = mapping.Mapping with { Generation = new(mapping.Mapping.Generation.Value + 1) };
        Assert.Equal(KernelError.StaleGeneration, kernel.QueryPlatformRegionMappingLifecycle(owner, stale).Error);
        provider.DomainRevokeStatus = null;
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(provider.DomainClosed);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(owner).Value.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(1, provider.DomainRevokeCalls);
        Assert.Equal(1, provider.BeginCalls);
        Assert.Equal(1, provider.ObserveCalls);
    }

    [Fact]
    public void AcceptedDomainClosureSurvivesLateCxlInventoryRefusalWithoutSecondProviderCall()
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        var participant = new InventoryOnlyCxlParticipant();
        provider.AfterDomainRevoke = () => typeof(RuntimeKernel).GetMethod("RegisterCxlTeardownParticipant",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(kernel, new object[] { participant });
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        var snapshot = kernel.QueryProcessTeardown(owner).Value;
        Assert.True(snapshot.PlatformDomainClosed);
        Assert.Equal(ProcessTeardownPhase.PlatformClosed, snapshot.Phase);
        Assert.False(snapshot.LocalReclaimCompleted);
        Assert.True(provider.DomainClosed);
        Assert.Equal(0, participant.Calls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        provider.AfterDomainRevoke = null;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, participant.Calls);
        Assert.Equal(1, provider.DomainRevokeCalls);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ChangedProcessDomainCorrelationRefusesReclaimButRetainsBridgeKnownClosure(bool tracking)
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        var original = mapping.Mapping.DomainBinding;
        var changed = original with { Generation = new(original.Generation.Value + 1) };
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_processTeardowns", flags)!.GetValue(kernel)!;
        var bindings = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_processPlatformBindings", flags)!.GetValue(kernel)!;
        provider.AfterDomainRevoke = () =>
        {
            if (tracking) bindings[owner] = changed;
            else records[owner]!.GetType().GetProperty("DomainBinding")!.SetValue(records[owner], changed);
        };
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(owner).Value.LocalReclaimCompleted);
        Assert.True(provider.DomainClosed);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        provider.AfterDomainRevoke = null;
        if (tracking) bindings[owner] = original;
        else records[owner]!.GetType().GetProperty("DomainBinding")!.SetValue(records[owner], original);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, provider.DomainRevokeCalls);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ProcessRetriesMappingBudgetFailureAfterActualRegionReleaseBeforeDomainReclaim(bool reset)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, 709, 790);
        var admin = TestFixtures.Create(kernel, 95, 950).Handle;
        var administration = kernel.MintCapability(new DomainId(950), admin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var account = kernel.AdmitProcessBudget(admin, administration, owner, "mapping-release-retry",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 1024)])
            .Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 1024).Value!;
        var binding = kernel.BindPlatformDomain(owner).Value!;
        var capability = MintRegionCapability(kernel, owner, region.Handle, CapabilityRights.Map | CapabilityRights.Read);
        var mapping = kernel.MapPlatformOwnedRegionSlice(owner, binding, capability, region.Handle, 0, 1024, PlatformMemoryAccess.Read).Value!;
        var records = (global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority).GetField("_reservations",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.Budgets)!;
        var record = records.Values.Cast<object>().Single(item =>
            (BudgetReservationLifetime)item.GetType().GetProperty("Lifetime")!.GetValue(item)! == BudgetReservationLifetime.PlatformMapping);
        // Controlled quantitative owner refusal, not closure/permission evidence.
        record.GetType().GetProperty("Owner")!.SetValue(record, owner with { Generation = owner.Generation + 1 });
        Assert.Equal(KernelError.WrongRegionOwner, kernel.TerminateProcess(owner).Error);
        Assert.True(kernel.QueryPlatformRegionMappingLifecycle(owner, mapping.Mapping).Value.LocalReservationReleased);
        Assert.False(kernel.Regions.HasPlatformMappingReservation(region.Handle, new RegionOwner(binding.Subject.DomainId, owner.Generation)));
        Assert.Equal(1024UL, kernel.QueryBudget(account).Value!.Usage.Single(item =>
            item.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        record.GetType().GetProperty("Owner")!.SetValue(record, owner);
        if (reset)
        {
            Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
            provider.ThrowIncarnation = true;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var routes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
                .GetField("_mappings", flags)!.GetValue(kernel.PlatformAuthority)!;
            var canonical = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
                .GetField("_activeSubjects", flags)!.GetValue(kernel.PlatformAuthority)!;
            var original = routes[mapping.Mapping.MappingId]!;
            var originalOwner = canonical[binding.Subject]!;
            var receipt = original.GetType().GetProperty("KnownClosureReceipt")!;
            var originalReceipt = receipt.GetValue(original);
            for (var fault = 0; fault < 4; fault++)
            {
                if (fault == 0) routes.Remove(mapping.Mapping.MappingId);
                if (fault == 1) routes[mapping.Mapping.MappingId] = typeof(object)
                    .GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                if (fault == 2) receipt.SetValue(original, null);
                if (fault == 3) canonical.Remove(binding.Subject);
                Assert.False(kernel.ObservePlatformRegionMappingRevocation(owner, mapping.Mapping).IsSuccess);
                Assert.Equal(1024UL, kernel.QueryBudget(account).Value!.Usage.Single(item =>
                    item.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
                routes[mapping.Mapping.MappingId] = original;
                receipt.SetValue(original, originalReceipt);
                canonical[binding.Subject] = originalOwner;
            }
            Assert.True(kernel.ObservePlatformRegionMappingRevocation(owner, mapping.Mapping).IsSuccess);
            Assert.True(kernel.QueryPlatformRegionMappingLifecycle(owner, mapping.Mapping).Value.LocalReservationReleased);
            Assert.Equal(0UL, kernel.QueryBudget(account).Value!.Usage.Single(item =>
                item.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
            Assert.Equal(1024UL, kernel.QueryBudget(account).Value!.Usage.Single(item =>
                item.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(1, provider.BeginCalls);
            Assert.Equal(1, provider.ObserveCalls);
            Assert.Equal(0, provider.DomainRevokeCalls);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
            return;
        }
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0UL, kernel.QueryBudget(account).Value!.Usage.Single(item =>
            item.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used);
        Assert.Equal(1, provider.BeginCalls);
        Assert.Equal(1, provider.ObserveCalls);
        Assert.Equal(1, provider.DomainRevokeCalls);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ProcessMappingProviderPauseAllowsConcurrentQueriesWithoutDuplicateClosure(bool observe)
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        if (observe)
        {
            provider.CompletionState = PlatformCompletionState.Draining;
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            provider.CompletionState = PlatformCompletionState.Closed;
        }
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(kernel)!;
        var bridgeGate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Action callback = () =>
        {
            Assert.False(Monitor.IsEntered(kernelGate));
            Assert.False(Monitor.IsEntered(bridgeGate));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        if (observe) provider.BeforeObserve = callback; else provider.BeforeBegin = callback;
        var progression = Task.Run(() => kernel.TerminateProcess(owner));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await Task.Run(() =>
            {
                Assert.True(kernel.QueryProcessTeardown(owner).IsSuccess);
                Assert.True(kernel.ObserveProcessTeardown(owner).IsSuccess);
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
                Assert.True(kernel.Regions.HasPlatformMappingReservation(mapping.Mapping.Region,
                    new RegionOwner(mapping.Mapping.DomainBinding.Subject.DomainId, owner.Generation)));
                Assert.Equal(0, provider.DomainRevokeCalls);
            }).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { release.Set(); }
        Assert.True((await progression).IsSuccess);
        Assert.False(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(1, provider.BeginCalls);
        Assert.Equal(observe ? 2 : 1, provider.ObserveCalls);
        Assert.Equal(1, provider.DomainRevokeCalls);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void ProcessMappingLostReplyOrResetRefusesDomainAndLocalReclaim(bool observe, bool reset)
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        if (observe)
        {
            provider.CompletionState = PlatformCompletionState.Draining;
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            provider.CompletionState = PlatformCompletionState.Closed;
        }
        var kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        Action callback = () =>
        {
            Assert.False(Monitor.IsEntered(kernelGate));
            Assert.True(kernel.QueryProcessTeardown(owner).IsSuccess);
            if (reset) Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
            else throw new IOException("Mapping response lost during process closure");
        };
        if (observe) provider.BeforeObserve = callback; else provider.BeforeBegin = callback;
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        provider.BeforeBegin = null;
        provider.BeforeObserve = null;
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(owner).Value.LocalReclaimCompleted);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(mapping.Mapping.Region,
            new RegionOwner(mapping.Mapping.DomainBinding.Subject.DomainId, owner.Generation)));
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ChangedCxlInventoryAfterMappingClosureDefersLocalReleaseAndVisitsFreshParticipant(bool observe)
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        if (observe)
        {
            provider.CompletionState = PlatformCompletionState.Draining;
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            provider.CompletionState = PlatformCompletionState.Closed;
        }
        var participant = new InventoryOnlyCxlParticipant();
        provider.AfterObserve = () => typeof(RuntimeKernel).GetMethod("RegisterCxlTeardownParticipant",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(kernel, new object[] { participant });
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0, participant.Calls);
        Assert.False(kernel.QueryPlatformRegionMappingLifecycle(owner, mapping.Mapping).Value.LocalReservationReleased);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(mapping.Mapping.Region,
            new RegionOwner(mapping.Mapping.DomainBinding.Subject.DomainId, owner.Generation)));
        Assert.Equal(0, provider.DomainRevokeCalls);
        provider.AfterObserve = null;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(1, participant.Calls);
        Assert.Equal(1, provider.BeginCalls);
        Assert.Equal(observe ? 2 : 1, provider.ObserveCalls);
        Assert.Equal(1, provider.DomainRevokeCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ProcessClosedMappingSourceStageRechecksActualOwnersBeforeReclaim(int fault)
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        var subject = mapping.Mapping.DomainBinding.Subject;
        var regionOwner = new RegionOwner(subject.DomainId, owner.Generation);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(kernel)!;
        var bridgeGate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!;
        var regionGate = kernel.Regions.GetPlatformMappingCompletionGate(mapping.Mapping.Region, regionOwner).Value!;
        var participant = new InventoryOnlyCxlParticipant();
        var closedReads = 0;
        var outside = true;
        provider.BeforeIncarnationRead = () =>
        {
            var state = kernel.PlatformAuthority.QueryRegionMappingLifecycle(mapping.Mapping, subject);
            if (!state.IsSuccess || state.Value.PlatformClosure != PlatformExternalClosureState.Closed) return;
            closedReads++;
            outside &= !Monitor.IsEntered(kernelGate) && !Monitor.IsEntered(bridgeGate) && !Monitor.IsEntered(regionGate);
            provider.BeforeIncarnationRead = null;
            if (fault == 0) provider.ThrowIncarnation = true;
            if (fault == 1) provider.Incarnation = new PlatformProviderIncarnation(2);
            if (fault == 2) kernel.ObservePlatformBackendReset();
            if (fault == 3) typeof(RuntimeKernel).GetMethod("RegisterCxlTeardownParticipant", flags)!
                .Invoke(kernel, new object[] { participant });
        };
        var first = kernel.TerminateProcess(owner);
        Assert.Equal(1, closedReads);
        Assert.True(outside);
        Assert.Equal(fault == 4, first.IsSuccess);
        if (fault != 4)
        {
            Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
            Assert.False(kernel.QueryProcessTeardown(owner).Value.LocalReclaimCompleted);
            Assert.True(kernel.Regions.HasPlatformMappingReservation(mapping.Mapping.Region, regionOwner));
            var lifecycle = kernel.QueryPlatformRegionMappingLifecycle(owner, mapping.Mapping);
            Assert.False(lifecycle.IsSuccess && lifecycle.Value.LocalReservationReleased);
            Assert.Equal(0, provider.DomainRevokeCalls);
            Assert.Equal(0, participant.Calls);
        }
        provider.BeforeIncarnationRead = null;
        provider.ThrowIncarnation = false;
        if (fault is 0 or 3)
        {
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(fault == 3 ? 1 : 0, participant.Calls);
        }
        if (fault is 0 or 3 or 4)
            Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        else
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        }
        Assert.Equal(1, provider.BeginCalls);
        Assert.Equal(1, provider.ObserveCalls);
    }

    [Fact]
    public async Task ProcessClosedMappingSourcePauseAllowsConcurrentQueriesWithoutReclaim()
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        var subject = mapping.Mapping.DomainBinding.Subject;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        provider.BeforeIncarnationRead = () =>
        {
            var state = kernel.PlatformAuthority.QueryRegionMappingLifecycle(mapping.Mapping, subject);
            if (!state.IsSuccess || state.Value.PlatformClosure != PlatformExternalClosureState.Closed) return;
            provider.BeforeIncarnationRead = null;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(6))) throw new TimeoutException("Controlled source pause expired");
        };
        var termination = Task.Run(() => kernel.TerminateProcess(owner));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await Task.Run(() =>
            {
                Assert.True(kernel.QueryProcessTeardown(owner).IsSuccess);
                Assert.True(kernel.ObserveProcessTeardown(owner).IsSuccess);
                Assert.False(kernel.TerminateProcess(owner).IsSuccess);
                Assert.True(kernel.Regions.HasPlatformMappingReservation(mapping.Mapping.Region,
                    new RegionOwner(subject.DomainId, owner.Generation)));
                Assert.Equal(0, provider.DomainRevokeCalls);
            }).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { release.Set(); }
        Assert.True((await termination).IsSuccess);
        Assert.Equal(1, provider.BeginCalls);
        Assert.Equal(1, provider.ObserveCalls);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }
    private sealed class InventoryOnlyCxlParticipant : ICxlTeardownParticipant
    {
        public int Calls { get; private set; }
        public KernelResult CloseForProcess(ProcessHandle process, RegionOwner owner)
        {
            Calls++;
            return KernelResult.Ok();
        }
    }

    [Fact]
    public void ExactClosedMappingReleaseRequiresActualRegionOwnerGate()
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        var subject = mapping.Mapping.DomainBinding.Subject;
        var regionOwner = new RegionOwner(subject.DomainId, owner.Generation);
        var closed = kernel.PlatformAuthority.BeginRegionMappingRevocation(mapping.Mapping, subject,
            PlatformRegionRevocationPolicy.DrainBeforeRevoke);
        Assert.True(closed.IsSuccess);
        Assert.Equal(PlatformExternalClosureState.Closed, closed.Value.PlatformClosure);
        Assert.Equal(KernelError.PlatformFaulted,
            kernel.PlatformAuthority.CommitRegionReservationRelease(mapping.Mapping, subject, kernel.Regions).Error);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(mapping.Mapping.Region, regionOwner));
        Assert.False(kernel.QueryPlatformRegionMappingLifecycle(owner, mapping.Mapping).Value.LocalReservationReleased);
        Assert.True(kernel.ObservePlatformRegionMappingRevocation(owner, mapping.Mapping).IsSuccess);
        Assert.False(kernel.Regions.HasPlatformMappingReservation(mapping.Mapping.Region, regionOwner));
        Assert.True(kernel.QueryPlatformRegionMappingLifecycle(owner, mapping.Mapping).Value.LocalReservationReleased);
        Assert.Equal(1, provider.BeginCalls);
        Assert.Equal(1, provider.ObserveCalls);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Fact]
    public void ForgedClosedObservationCannotReleaseActualActiveMapping()
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        var subject = mapping.Mapping.DomainBinding.Subject;
        var forged = new PlatformRegionMappingLifecycle(mapping.Mapping, true, PlatformExternalClosureState.Closed, true);
        Assert.Equal(KernelError.PlatformBindingDraining, FinalizeMapping(kernel, mapping.Mapping, subject, forged).Error);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(mapping.Mapping.Region,
            new RegionOwner(subject.DomainId, owner.Generation)));
        Assert.Equal(0, provider.BeginCalls);
        Assert.Equal(0, provider.ObserveCalls);
        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapping).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CapturedClosedObservationCannotReleaseChangedResetOrRegionGeneration(bool reset)
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        var subject = mapping.Mapping.DomainBinding.Subject;
        var regionOwner = new RegionOwner(subject.DomainId, owner.Generation);
        var closed = kernel.PlatformAuthority.BeginRegionMappingRevocation(mapping.Mapping, subject,
            PlatformRegionRevocationPolicy.DrainBeforeRevoke).Value;
        Assert.Equal(PlatformExternalClosureState.Closed, closed.PlatformClosure);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)typeof(RegionAuthority).GetField("_regions", flags)!.GetValue(kernel.Regions)!;
        var record = records.Values.Cast<object>().Single();
        if (reset) Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        else record.GetType().GetProperty("Generation")!.SetValue(record,
            new RegionGeneration(mapping.Mapping.Region.Generation.Value + 1));
        Assert.False(FinalizeMapping(kernel, mapping.Mapping, subject, closed).IsSuccess);
        Assert.True((bool)record.GetType().GetProperty("PlatformMappingReserved")!.GetValue(record)!);
        Assert.False(kernel.PlatformAuthority.QueryRegionMappingLifecycle(mapping.Mapping, subject).IsSuccess &&
            kernel.PlatformAuthority.QueryRegionMappingLifecycle(mapping.Mapping, subject).Value.LocalReservationReleased);
        Assert.Equal(1, provider.BeginCalls);
        Assert.Equal(1, provider.ObserveCalls);
        if (!reset) record.GetType().GetProperty("Generation")!.SetValue(record, mapping.Mapping.Region.Generation);
        if (!reset) Assert.True(kernel.ObservePlatformRegionMappingRevocation(owner, mapping.Mapping).IsSuccess);
        else Assert.False(kernel.TerminateProcess(owner).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentCapturedClosureConsumersReleaseOriginalRegionExactlyOnce()
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        var subject = mapping.Mapping.DomainBinding.Subject;
        var closed = kernel.PlatformAuthority.BeginRegionMappingRevocation(mapping.Mapping, subject,
            PlatformRegionRevocationPolicy.DrainBeforeRevoke).Value;
        var results = await Task.WhenAll(Task.Run(() => FinalizeMapping(kernel, mapping.Mapping, subject, closed)),
            Task.Run(() => FinalizeMapping(kernel, mapping.Mapping, subject, closed)));
        Assert.All(results, result => Assert.True(result.IsSuccess, result.Message));
        Assert.False(kernel.Regions.HasPlatformMappingReservation(mapping.Mapping.Region,
            new RegionOwner(subject.DomainId, owner.Generation)));
        Assert.True(kernel.QueryPlatformRegionMappingLifecycle(owner, mapping.Mapping).Value.LocalReservationReleased);
        Assert.Equal(1, provider.BeginCalls);
        Assert.Equal(1, provider.ObserveCalls);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Fact]
    public async Task ResetWhileCapturedClosureWaitsForRegionGateCannotReleaseOriginalPin()
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        var subject = mapping.Mapping.DomainBinding.Subject;
        var regionOwner = new RegionOwner(subject.DomainId, owner.Generation);
        var closed = kernel.PlatformAuthority.BeginRegionMappingRevocation(mapping.Mapping, subject,
            PlatformRegionRevocationPolicy.DrainBeforeRevoke).Value;
        var regionGate = kernel.Regions.GetPlatformMappingCompletionGate(mapping.Mapping.Region, regionOwner).Value!;
        var kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Factory.StartNew(() =>
        {
            lock (regionGate) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var finalize = Task.Run(() => FinalizeMapping(kernel, mapping.Mapping, subject, closed));
        try
        {
            Assert.True(SpinWait.SpinUntil(() =>
            {
                if (!Monitor.TryEnter(kernelGate)) return true;
                Monitor.Exit(kernelGate);
                return false;
            }, TimeSpan.FromSeconds(3)));
            Assert.False(finalize.IsCompleted);
            Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        }
        finally { release.Set(); }
        await holder;
        Assert.False((await finalize).IsSuccess);
        Assert.True(kernel.Regions.HasPlatformMappingReservation(mapping.Mapping.Region, regionOwner));
        Assert.Equal(1, provider.BeginCalls);
        Assert.Equal(1, provider.ObserveCalls);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ClosedMappingFinalReclaimRequiresFreshProviderSourceOutsideOwners(int fault)
    {
        var (kernel, provider, owner, mapping) = CreateMappedRegion(709, 790);
        var subject = mapping.Mapping.DomainBinding.Subject;
        var regionOwner = new RegionOwner(subject.DomainId, owner.Generation);
        var closed = kernel.PlatformAuthority.BeginRegionMappingRevocation(mapping.Mapping, subject,
            PlatformRegionRevocationPolicy.DrainBeforeRevoke).Value;
        Assert.Equal(PlatformExternalClosureState.Closed, closed.PlatformClosure);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(kernel)!;
        var bridgeGate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!;
        var regionGate = kernel.Regions.GetPlatformMappingCompletionGate(mapping.Mapping.Region, regionOwner).Value!;
        var reads = 0;
        var outsideOwners = true;
        provider.BeforeIncarnationRead = () =>
        {
            reads++;
            outsideOwners &= !Monitor.IsEntered(kernelGate) && !Monitor.IsEntered(bridgeGate) && !Monitor.IsEntered(regionGate);
            if (fault == 2) kernel.PlatformAuthority.ObserveBackendReset();
        };
        provider.ThrowIncarnation = fault == 0;
        if (fault == 1) provider.Incarnation = new PlatformProviderIncarnation(2);
        Assert.True(kernel.QueryPlatformRegionMappingLifecycle(owner, mapping.Mapping).IsSuccess);
        Assert.Equal(0, reads); // The observation-only query remains local.
        var finalized = FinalizeMapping(kernel, mapping.Mapping, subject, closed);
        Assert.Equal(fault == 3, finalized.IsSuccess);
        Assert.Equal(1, reads);
        Assert.True(outsideOwners);
        Assert.Equal(fault != 3, kernel.Regions.HasPlatformMappingReservation(mapping.Mapping.Region, regionOwner));
        provider.BeforeIncarnationRead = null;
        provider.ThrowIncarnation = false;
        if (fault == 0)
        {
            Assert.Equal(PlatformExternalClosureState.Closed,
                kernel.QueryPlatformRegionMappingLifecycle(owner, mapping.Mapping).Value.PlatformClosure);
            Assert.True(kernel.ObservePlatformRegionMappingRevocation(owner, mapping.Mapping).IsSuccess);
        }
        if (fault == 3)
        {
            provider.ThrowIncarnation = true;
            Assert.True(FinalizeMapping(kernel, mapping.Mapping, subject, closed).IsSuccess);
            provider.ThrowIncarnation = false; // Completed release is cleanup truth, no new source query.
        }
        Assert.Equal(1, provider.BeginCalls);
        Assert.Equal(1, provider.ObserveCalls);
        if (fault is 0 or 3)
        {
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        }
        else
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        }
    }
    private static KernelResult FinalizeMapping(RuntimeKernel kernel, PlatformRegionMapping mapping,
        PlatformDomainIdentity subject, PlatformRegionMappingLifecycle observation) =>
        (KernelResult)typeof(RuntimeKernel).GetMethod("FinalizePlatformRegionMappingClosure",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(kernel, new object[] { mapping, subject, observation })!;

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
        PlatformOwnedRegionSliceMapping Mapping) CreateMappedRegion(ulong processId, ulong domainId,
            Action<RuntimeKernel, ProcessHandle>? beforeAllocation = null)
    {
        var provider = new ExactMappingProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, owner) = TestFixtures.Create(kernel, processId, domainId);
        beforeAllocation?.Invoke(kernel, owner);
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
        IPlatformRegionRevocationProvider,
        IPlatformProviderIncarnationSource
    {
        private PlatformProviderDomainLease? _domain;
        private PlatformProviderRegionMappingLease? _mapping;
        private PlatformRegionSlice? _slice;
        private PlatformOperationIdentity? _operation;
        private ulong _nextMapping = 1;
        private ulong _nextOperation = 1;

        public int MapCalls { get; private set; }
        public PlatformProviderIncarnation Incarnation { get; set; } = new(1);
        public bool ThrowIncarnation { get; set; }
        public Action? BeforeIncarnationRead { get; set; }
        public PlatformProviderIncarnation CurrentIncarnation
        {
            get
            {
                BeforeIncarnationRead?.Invoke();
                if (ThrowIncarnation) throw new InvalidOperationException("Incarnation unavailable.");
                return Incarnation;
            }
        }
        public PlatformAuthorityStatus? BeginFailureAfterOperation { get; set; }
        public int BeginCalls { get; private set; }
        public int ObserveCalls { get; private set; }
        public int DomainRevokeCalls { get; private set; }
        public Action? BeforeDomainRevoke { get; set; }
        public Action? AfterDomainRevoke { get; set; }
        public PlatformAuthorityStatus? DomainRevokeStatus { get; set; }
        public bool DomainClosed { get; private set; }
        public Action? BeforeBegin { get; set; }
        public Action? BeforeObserve { get; set; }
        public Action? AfterObserve { get; set; }
        public Action? BeforeMapReturn { get; set; }
        public PlatformAuthorityStatus? MapStatus { get; set; }
        public bool ReturnMalformedMappingAccess { get; set; }
        public int VisibilityCalls { get; private set; }
        public Action? BeforeVisibilityReturn { get; set; }
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

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            DomainRevokeCalls++;
            BeforeDomainRevoke?.Invoke();
            if (DomainRevokeStatus is { } status) return PlatformAuthorityResult.Fail(status, "Controlled domain revoke refusal");
            DomainClosed = true;
            AfterDomainRevoke?.Invoke();
            return PlatformAuthorityResult.Ok();
        }

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
                new PlatformProviderOwnedRegionMapping(
                    ReturnMalformedMappingAccess ? lease with { Access = PlatformMemoryAccess.Write } : lease, slice));
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
            BeforeVisibilityReturn?.Invoke();
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
            BeginCalls++;
            BeforeBegin?.Invoke();
            if (_mapping != mapping)
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                    PlatformAuthorityStatus.Denied, "Wrong mapping.");
            var operation = new PlatformOperationIdentity(
                new PlatformOperationId(_nextOperation++), new PlatformOperationGeneration(1),
                mapping.DomainLease);
            _operation = operation;
            if (BeginFailureAfterOperation is { } status)
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(status, "Original begin reply unavailable after operation staging.");
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(
                new PlatformRegionRevocationTicket(mapping.MappingId, mapping.Generation, operation));
        }

        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
            PlatformOperationIdentity operation)
        {
            ObserveCalls++;
            BeforeObserve?.Invoke();
            if (_operation != operation)
                return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                    PlatformAuthorityStatus.Denied, "Wrong operation.");
            var generation = ReturnStaleCompletionGeneration
                ? new PlatformOperationGeneration(operation.Generation.Value + 1)
                : operation.Generation;
            AfterObserve?.Invoke();
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(
                new PlatformCompletionReceipt(operation.OperationId, generation,
                    operation.DomainLease, CompletionState));
        }
    }
}
