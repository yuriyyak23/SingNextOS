using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class SecureExecutionBindingTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void VirtualType2OriginalIoRevisionRefusesLateSubmitAndRetainsActualReleaseCleanup(bool aba)
    {
        int effects = 0;
        ComposedWorkload c = CreateComposedWorkload(afterSubmit: (kernel, _, _) =>
        {
            effects++;
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            IDictionary routes = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_virtualIo", flags)!.GetValue(kernel)!;
            object record = routes.Values.Cast<object>().Single(); PropertyInfo state = record.GetType().GetProperty("State", flags)!;
            object active = state.GetValue(record)!;
            if (aba) state.SetValue(record, Enum.Parse(state.PropertyType, "Quarantined"));
            state.SetValue(record, active);
        });
        Assert.Equal(1, effects);
        Assert.False(c.SubmissionResult.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, c.SubmissionResult.Error);
        Assert.False(c.Bridge.ReleaseFabric(c.Fabric).IsSuccess);
        Assert.False(c.Memory.Close(c.OutputPlacement.PlacementId).IsSuccess);
        Assert.True(c.Manager.BeginReconfiguration(c.Fabric).IsSuccess);
        KernelResult<CxlFabricManagedBinding> replacement = c.Manager.CompleteReconfiguration(c.Fabric.BindingId);
        Assert.True(replacement.IsSuccess, replacement.Message);
        CloseComposedWorkload(c, replacement.Value!.Binding);
        Assert.Equal(1, effects);
        Assert.Equal(1, c.Base.Provider.VirtualIoCloseCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void VirtualType2OriginalIoRevisionRefusesPublicationAfterCallbackAndKeepsConsequencesPinned(bool aba)
    {
        ComposedWorkload c = CreateComposedWorkload(); int callbacks = 0;
        KernelResult<ExternalOperationSnapshot> result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () =>
        {
            callbacks++;
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            IDictionary routes = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_virtualIo", flags)!.GetValue(c.Base.Kernel)!;
            object record = routes[c.VirtualIo.Id]!; PropertyInfo state = record.GetType().GetProperty("State", flags)!;
            object active = state.GetValue(record)!;
            if (aba) state.SetValue(record, Enum.Parse(state.PropertyType, "Quarantined"));
            state.SetValue(record, active);
        });
        Assert.Equal(1, callbacks); Assert.False(result.IsSuccess);
        ExternalOperationSnapshot operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.NotEqual(ExternalOperationState.Published, operation.State);
        Assert.NotEqual(ExternalOperationState.Released, operation.State);
        Assert.False(c.Memory.Close(c.OutputPlacement.PlacementId).IsSuccess);
        Assert.Equal(0, c.Base.Provider.EventEffects);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void VirtualIoCompletedOriginalHandoffAndMutableRevalidationKeepTheirBoundaries(int fault)
    {
        Scenario s = Create(); SecureExecutionBinding execution = Bind(s).Value;
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        CapabilityDescriptorV1 cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.Device,
            "device:secure-virtual-type2", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        PlatformDeviceLease device = s.Kernel.BindPlatformDevice(s.Owner, s.Parent, cap.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        VirtualIoBinding io = s.Kernel.BindVirtualIo(s.Owner, s.Virtual.Domain, device,
            new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 4096), execution).Value;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_virtualIo", flags)!.GetValue(s.Kernel)!;
        object original = routes[io.Id]!; PropertyInfo state = original.GetType().GetProperty("State", flags)!;
        if (fault is 0 or 1)
        {
            PlatformVirtualIoBinding platform = (PlatformVirtualIoBinding)original.GetType().GetProperty("Platform", flags)!.GetValue(original)!;
            Assert.True(s.Kernel.PlatformAuthority.RevokeChildVirtualIo(platform).IsSuccess);
            IDictionary bridgeRoutes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_virtualIoBindings", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
            object key = bridgeRoutes.Keys.Cast<object>().Single();
            if (fault == 0) bridgeRoutes.Remove(key);
            else bridgeRoutes[key] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(bridgeRoutes[key], null)!;
        }
        else
        {
            s.Provider.DuringIncarnation = () =>
            {
                object active = state.GetValue(original)!;
                if (fault == 3) state.SetValue(original, Enum.Parse(state.PropertyType, "Quarantined"));
                state.SetValue(original, active);
            };
            Assert.False(s.Kernel.RevalidateVirtualIo(s.Owner, io).IsSuccess);
            s.Provider.DuringIncarnation = null;
            Assert.Equal(0, s.Provider.VirtualIoCloseCalls);
        }
        Assert.True(s.Kernel.CloseVirtualIo(io).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.VirtualIoCloseCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalOverlayAndVirtualIoClosureConsumeAttachedChargeOnlyAfterExactLocalCleanup(bool overlayFault)
    {
        BudgetAccountHandle account = default;
        (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) o = OverlayScenario(beforeAllocation: s =>
        {
            ProcessHandle admin = TestFixtures.Create(s.Kernel, 98, 998).Handle;
            CapabilityId cap = s.Kernel.MintCapability(new(998), admin, ResourceKind.KernelService,
                CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
            account = s.Kernel.AdmitProcessBudget(admin, cap, s.Owner, "composed-original-cleanup",
                [new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!.ProcessBudget;
        });
        Scenario s = o.S;
        SecureGuestRegionBinding overlay = s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).Value;
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        CapabilityDescriptorV1 cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.Device,
            "device:secure-virtual-type2", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        PlatformDeviceLease device = s.Kernel.BindPlatformDevice(s.Owner, s.Parent, cap.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        VirtualIoBinding io = s.Kernel.BindVirtualIo(s.Owner, s.Virtual.Domain, device,
            new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 4096), o.Execution).Value;
        if (overlayFault) s.Kernel.BeforeSecureGuestRegionClosureLocalCommit = () => throw new IOException("overlay local consumption lost");
        else s.Kernel.BeforeVirtualIoClosureLocalCommit = () => throw new IOException("Io local consumption lost");
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(64UL, s.Kernel.QueryBudget(account).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == o.Mapping.Region).State);
        s.Kernel.BeforeSecureGuestRegionClosureLocalCommit = null; s.Kernel.BeforeVirtualIoClosureLocalCommit = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.VirtualIoCloseCalls);
        Assert.Equal(1, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == o.Mapping.Region).State);
        Assert.Equal(0UL, s.Kernel.QueryBudget(account).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    [InlineData(12)] [InlineData(13)]
    public void OriginalVirtualIoCloseRetainsUnknownAndConsumesKnownLocalFaultWithoutRepeatingEffect(int fault)
    {
        Scenario s = Create(); SecureExecutionBinding execution = Bind(s).Value;
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        CapabilityDescriptorV1 cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.Device,
            "device:secure-virtual-type2", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        PlatformDeviceLease device = s.Kernel.BindPlatformDevice(s.Owner, s.Parent, cap.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        VirtualIoBinding io = s.Kernel.BindVirtualIo(s.Owner, s.Virtual.Domain, device,
            new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 4096), execution).Value;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object Clone(object value) => typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(value, null)!;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_virtualIo", flags)!.GetValue(s.Kernel)!;
        object local = routes[io.Id]!; PropertyInfo state = local.GetType().GetProperty("State", flags)!;
        IDictionary bridgeRoutes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_virtualIoBindings", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        object bridgeRecord = bridgeRoutes.Values.Cast<object>().Single();
        if (fault <= 3) s.Provider.VirtualIoClosureFault = fault;
        else s.Kernel.BeforeVirtualIoClosureLocalCommit = () =>
        {
            Assert.False(s.Kernel.CloseVirtualIo(io).IsSuccess);
            if (fault == 4) throw new IOException("local commit lost");
            if (fault == 5) state.SetValue(local, state.GetValue(local));
            if (fault == 6) { state.SetValue(local, Enum.Parse(state.PropertyType, "Active")); state.SetValue(local, Enum.Parse(state.PropertyType, "Closing")); }
            if (fault == 7) routes[io.Id] = Clone(local);
            if (fault >= 8) throw new IOException("retain original closure for corruption test");
        };
        Assert.False(s.Kernel.CloseVirtualIo(io).IsSuccess);
        Assert.False((bool)local.GetType().GetProperty("CloseInFlight", flags)!.GetValue(local)!);
        routes[io.Id] = local; s.Kernel.BeforeVirtualIoClosureLocalCommit = null; s.Provider.VirtualIoClosureFault = -1;
        object close = bridgeRecord.GetType().GetProperty("CloseInvocation")!.GetValue(bridgeRecord)!;
        if (fault == 8) bridgeRecord.GetType().GetProperty("CloseInvocation")!.SetValue(bridgeRecord, Clone(close));
        if (fault == 9) bridgeRecord.GetType().GetProperty("CloseInvocation")!.SetValue(bridgeRecord, null);
        if (fault == 10) close.GetType().GetProperty("Observation")!.SetValue(close, null);
        if (fault == 11) state.SetValue(local, Enum.Parse(state.PropertyType, "Closed"));
        if (fault == 12) local.GetType().GetProperty("SourceRevision", flags)!.SetValue(local, long.MaxValue);
        bool canConsume = fault is 4 or 5 or 6 or 7 or 12 or 13;
        Assert.Equal(canConsume, s.Kernel.CloseVirtualIo(io).IsSuccess);
        Assert.Equal(canConsume, s.Kernel.CloseSecureExecution(execution).IsSuccess);
        Assert.Equal(canConsume, s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.VirtualIoCloseCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void OriginalVirtualIoDependencyAndClosureCannotBeReplacedByRouteOrClosedProjection(int fault)
    {
        Scenario s = Create(); SecureExecutionBinding execution = Bind(s).Value;
        OwnedBuffer<byte> buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        CapabilityDescriptorV1 cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.Device,
            "device:secure-virtual-type2", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        PlatformDeviceLease device = s.Kernel.BindPlatformDevice(s.Owner, s.Parent, cap.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        VirtualIoBinding io = s.Kernel.BindVirtualIo(s.Owner, s.Virtual.Domain, device,
            new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 4096), execution).Value;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object Clone(object value) => typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(value, null)!;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_virtualIo", flags)!.GetValue(s.Kernel)!;
        object original = routes[io.Id]!;
        PropertyInfo state = original.GetType().GetProperty("State", flags)!;
        if (fault == 0) state.SetValue(original, Enum.Parse(state.PropertyType, "Closed"));
        if (fault == 1) routes.Remove(io.Id);
        if (fault is 2 or 3)
        {
            object copy = Clone(original); if (fault == 3) state.SetValue(copy, Enum.Parse(state.PropertyType, "Closed")); routes[io.Id] = copy;
        }
        if (fault is 4 or 5)
        {
            IDictionary bridgeRoutes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_virtualIoBindings", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
            object key = bridgeRoutes.Keys.Cast<object>().Single(); object record = bridgeRoutes[key]!;
            if (fault == 4) bridgeRoutes[key] = Clone(record);
            else record.GetType().GetProperty("Closure")!.SetValue(record, PlatformExternalClosureState.Closed);
        }
        Assert.False(s.Kernel.CloseVirtualIo(io).IsSuccess);
        Assert.False(s.Kernel.CloseSecureExecution(execution).IsSuccess);
        Assert.Equal(0, s.Provider.CloseCalls);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public void OriginalSecureGuestClosureAndPublicationCarryLocalRevisionAndExactConsumption(int fault)
    {
        (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) o = OverlayScenario(); Scenario s = o.S;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureGuestRegions", flags)!.GetValue(s.Kernel)!;
        object Clone(object value) => typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(value, null)!;
        if (fault is 0 or 1 or 2)
        {
            s.Provider.DuringSecureRegionBind = () =>
            {
                object record = routes.Values.Cast<object>().Single(); PropertyInfo state = record.GetType().GetProperty("State", flags)!;
                object admitted = state.GetValue(record)!;
                if (fault == 1) state.SetValue(record, Enum.Parse(state.PropertyType, "Active"));
                if (fault == 2) record.GetType().GetProperty("SourceRevision", flags)!.SetValue(record, long.MaxValue);
                state.SetValue(record, admitted);
            };
            Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
            s.Provider.DuringSecureRegionBind = null;
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.SecureRegionBindCalls);
            Assert.Equal(1, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
            Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == o.Mapping.Region).State);
            return;
        }
        SecureGuestRegionBinding overlay = s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).Value;
        object original = routes[overlay.Id]!; PropertyInfo localState = original.GetType().GetProperty("State", flags)!;
        if (fault == 3) original.GetType().GetProperty("SourceRevision", flags)!.SetValue(original, long.MaxValue);
        if (fault is 4 or 5 or 6 or 7)
        {
            s.Kernel.BeforeSecureGuestRegionClosureLocalCommit = () =>
            {
                Assert.False(s.Kernel.CloseSecureGuestRegion(overlay).IsSuccess);
                if (fault == 4) localState.SetValue(original, localState.GetValue(original));
                if (fault == 5) { localState.SetValue(original, Enum.Parse(localState.PropertyType, "Active")); localState.SetValue(original, Enum.Parse(localState.PropertyType, "Closing")); }
                if (fault == 6) routes[overlay.Id] = Clone(original);
                if (fault == 7) throw new IOException("local consumption interrupted");
            };
            Assert.False(s.Kernel.CloseSecureGuestRegion(overlay).IsSuccess);
            Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == o.Mapping.Region).State);
            Assert.False((bool)original.GetType().GetProperty("CloseInFlight", flags)!.GetValue(original)!);
            routes[overlay.Id] = original; s.Kernel.BeforeSecureGuestRegionClosureLocalCommit = null;
        }
        Assert.True(s.Kernel.CloseSecureGuestRegion(overlay).IsSuccess);
        Assert.True((bool)original.GetType().GetProperty("ClosureConsumed", flags)!.GetValue(original)!);
        if (fault == 8) routes.Remove(overlay.Id);
        if (fault == 9) { object copy = Clone(original); localState.SetValue(copy, Enum.Parse(localState.PropertyType, "Active")); routes[overlay.Id] = copy; }
        if (fault == 10) localState.SetValue(original, Enum.Parse(localState.PropertyType, "Active"));
        Assert.True(s.Kernel.CloseSecureExecution(o.Execution).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == o.Mapping.Region).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void OriginalSecureGuestOverlayCannotHideItsDependencyOrTransferClosureToRouteCopy(int fault)
    {
        (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) o = OverlayScenario(); Scenario s = o.S;
        SecureGuestRegionBinding overlay = s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).Value;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureGuestRegions", flags)!.GetValue(s.Kernel)!;
        object original = routes[overlay.Id]!;
        PropertyInfo state = original.GetType().GetProperty("State", flags)!;
        if (fault == 0) state.SetValue(original, Enum.Parse(state.PropertyType, "Closed"));
        if (fault == 1) routes.Remove(overlay.Id);
        if (fault is 2 or 3)
        {
            object copy = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            if (fault == 3) state.SetValue(copy, Enum.Parse(state.PropertyType, "Closed"));
            routes[overlay.Id] = copy;
        }
        if (fault == 2) Assert.False(s.Kernel.CloseSecureGuestRegion(overlay).IsSuccess);
        Assert.False(s.Kernel.CloseSecureExecution(o.Execution).IsSuccess);
        Assert.Equal(0, s.Provider.CloseCalls);
        Assert.Empty(s.Provider.OverlayCloseOrder);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == o.Mapping.Region).State);
    }

    [Fact]
    public void SecureGuestOriginalClosedBindingCannotConsumeAnEqualTupleNewBinding()
    {
        (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) o = OverlayScenario(); Scenario s = o.S;
        SecureGuestRegionBinding overlay = s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).Value;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary overlays = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureGuestRegions", flags)!.GetValue(s.Kernel)!;
        object local = overlays[overlay.Id]!;
        PlatformRegionMapping parent = (PlatformRegionMapping)local.GetType().GetProperty("Parent", flags)!.GetValue(local)!;
        PlatformAuthorityBridge bridge = s.Kernel.PlatformAuthority;
        IDictionary domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_secureDomains", flags)!.GetValue(bridge)!;
        object domain = domains.Values.Cast<object>().Single();
        PlatformAuthorityBridge.SecureDomainBinding binding = (PlatformAuthorityBridge.SecureDomainBinding)domain.GetType().GetProperty("Binding")!.GetValue(domain)!;
        object oldFact = local.GetType().GetProperty("OriginalRegionBindingSource", flags)!.GetValue(local)!;
        Assert.True(bridge.UnbindSecureRegion(binding, parent).IsSuccess);
        Assert.True(bridge.BindSecureRegion(binding, parent, PlatformSecureRegionClass.Private).IsSuccess);
        IDictionary owners = (global::System.Collections.IDictionary)domain.GetType().GetProperty("RegionBindingOwners")!.GetValue(domain)!;
        object newerFact = owners[parent.MappingId]!;
        Assert.NotSame(oldFact, newerFact);
        Assert.Equal(oldFact.GetType().GetProperty("Observation")!.GetValue(oldFact), newerFact.GetType().GetProperty("Observation")!.GetValue(newerFact));
        Assert.True(s.Kernel.CloseSecureGuestRegion(overlay).IsSuccess);
        Assert.Same(newerFact, owners[parent.MappingId]);
        Assert.Equal(1, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == o.Mapping.Region).State);
        Assert.True(bridge.UnbindSecureRegion(binding, parent).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(2, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == o.Mapping.Region).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void OriginalSecureExecutionClosureSurvivesPartialConsumptionAndRejectsClosedProjection(int fault)
    {
        Scenario s = Create(); OwnedBuffer<byte> buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 128).Value!;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureExecutions", flags)!.GetValue(s.Kernel)!;
        object Clone(object value) => typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(value, null)!;
        if (fault is 7 or 8)
        {
            object? admitted = null;
            s.Provider.DuringBind = () =>
            {
                admitted = routes[s.Provider.LastRequest.Binding.Id]!;
                if (fault == 7) routes[s.Provider.LastRequest.Binding.Id] = Clone(admitted);
                else routes.Remove(s.Provider.LastRequest.Binding.Id);
            };
            Assert.False(Bind(s).IsSuccess);
            Assert.Equal(1, s.Provider.BindCalls); Assert.Equal(0, s.Provider.CloseCalls);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.NotNull(admitted!.GetType().GetField("BindObservation", flags)!.GetValue(admitted));
            routes[s.Provider.LastRequest.Binding.Id] = admitted; s.Provider.DuringBind = null;
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.CloseCalls);
            Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
            return;
        }
        SecureExecutionBinding binding = Bind(s).Value; object original = routes[binding.Id]!;
        FieldInfo state = original.GetType().GetField("State", flags)!;
        if (fault is 5 or 6)
        {
            Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
            if (fault == 5) routes.Remove(binding.Id);
            else
            {
                object foreign = Clone(original); state.SetValue(foreign, Enum.Parse(state.FieldType, "Active")); routes[binding.Id] = foreign;
            }
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.CloseCalls);
            Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
            return;
        }
        if (fault == 0) s.Provider.ClosureFault = 0;
        else s.Provider.DuringClose = () =>
        {
            Assert.True((bool)original.GetType().GetField("CloseInvoked", flags)!.GetValue(original)!);
            Assert.True((bool)original.GetType().GetField("CloseInFlight", flags)!.GetValue(original)!);
            object fact = original.GetType().GetField("CloseInvocation", flags)!.GetValue(original)!;
            Assert.Null(fact.GetType().GetField("Observation", flags)!.GetValue(fact));
            if (fault == 1)
            {
                state.SetValue(original, Enum.Parse(state.FieldType, "Closed"));
                Assert.False(s.Kernel.CloseSecureExecution(binding).IsSuccess);
            }
            else routes[binding.Id] = Clone(original);
        };
        Assert.False(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.False((bool)original.GetType().GetField("CloseInFlight", flags)!.GetValue(original)!);
        routes[binding.Id] = original; s.Provider.DuringClose = null; s.Provider.ClosureFault = -1;
        FieldInfo factField = original.GetType().GetField("CloseInvocation", flags)!; object retained = factField.GetValue(original)!;
        if (fault == 0) state.SetValue(original, Enum.Parse(state.FieldType, "Closed"));
        if (fault == 2) factField.SetValue(original, Clone(retained));
        if (fault == 3) factField.SetValue(original, null);
        if (fault == 4) retained.GetType().GetField("Observation", flags)!.SetValue(retained, null);
        if (fault != 1)
        {
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        }
        if (fault is 1 or 2 or 3)
        {
            factField.SetValue(original, retained);
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        }
        else Assert.False(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void OriginalSecureExecutionRoutingCannotReplaceOrHideBackingDependency(int fault)
    {
        Scenario s = Create();
        ProcessHandle admin = TestFixtures.Create(s.Kernel, 990, 991).Handle;
        CapabilityId capability = s.Kernel.MintCapability(new(991), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = s.Kernel.AdmitProcessBudget(admin, capability, s.Owner, "secure-owner", [new(ServiceBudgetDimension.OwnedMemoryBytes, 128)]).Value!.ProcessBudget;
        OwnedBuffer<byte> buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 128).Value!;
        SecureExecutionBinding binding = Bind(s).Value;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureExecutions", flags)!.GetValue(s.Kernel)!;
        object original = routes[binding.Id]!;
        object Clone() => typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        if (fault == 0) routes[binding.Id] = Clone();
        if (fault == 1) routes.Remove(binding.Id);
        if (fault == 2)
        {
            object foreign = Clone(); FieldInfo state = foreign.GetType().GetField("State", flags)!;
            state.SetValue(foreign, Enum.Parse(state.FieldType, "Closed")); routes[binding.Id] = foreign;
        }
        if (fault == 3) s.Provider.DuringRevalidation = () => routes[binding.Id] = Clone();
        if (fault != 4) Assert.False(s.Kernel.RevalidateSecureExecution(binding).IsSuccess);
        else
        {
            s.Provider.DuringClose = () => routes[binding.Id] = Clone();
            Assert.False(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        }
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.Equal(128UL, s.Kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        routes[binding.Id] = original; s.Provider.DuringRevalidation = null; s.Provider.DuringClose = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.Equal(0UL, s.Kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void OriginalUnknownSecureExecutionCloseMustRemainPinnedWithoutRepeatingProviderEffect(int fault)
    {
        Scenario s = Create(); OwnedBuffer<byte> buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 128).Value!; SecureExecutionBinding binding = Bind(s).Value;
        s.Provider.ClosureFault = fault;
        Assert.False(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        s.Provider.ClosureFault = -1;
        Assert.False(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
    }

    private static object OriginalSecureLocal(RuntimeKernel kernel, SecureDomainHandle domain) =>
        ((global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureDomainRecords", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!)[domain.DomainId]!;
    private static void MutateSecureLocal(object record, string property, bool aba)
    {
        PropertyInfo member = record.GetType().GetProperty(property)!; object original = member.GetValue(record)!;
        if (aba) member.SetValue(record, property == "State" ? (object)SecureDomainState.Parked : (ulong)original + 1);
        member.SetValue(record, original);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AClonedLocalSecureRouteCannotBecomeAnOriginalCompositionOwner(bool late)
    {
        Scenario s = Create(); OwnedBuffer<byte> region = s.Kernel.AllocateBuffer<byte>(s.Owner, 128).Value!;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureDomainRecords", flags)!.GetValue(s.Kernel)!;
        object original = routes[s.Secure.Domain.DomainId]!;
        void Replace() => routes[s.Secure.Domain.DomainId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
        if (late) s.Provider.DuringBind = Replace; else Replace();
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.BindCalls);
        Assert.False(Bind(s).IsSuccess); Assert.Equal(late ? 1 : 0, s.Provider.BindCalls);
        routes[s.Secure.Domain.DomainId] = original; s.Provider.DuringBind = null;
        Assert.Equal(late ? 1 : 0, s.Provider.CloseCalls);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
    }

    [Fact]
    public void ExhaustedSecureRevisionRefusesAdmissionButDoesNotInvalidateExactLifetimeCleanup()
    {
        Scenario s = Create(); OwnedBuffer<byte> region = s.Kernel.AllocateBuffer<byte>(s.Owner, 128).Value!;
        object local = OriginalSecureLocal(s.Kernel, s.Secure.Domain);
        local.GetType().GetField("_sourceRevision", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(local, long.MaxValue);
        MutateSecureLocal(local, "State", false);
        Assert.False(Bind(s).IsSuccess); Assert.Equal(0, s.Provider.BindCalls);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
    }

    [Fact]
    public void ActualVirtualIoBindCannotRecaptureAnEqualSecureRevisionAfterItsProviderEffect()
    {
        Scenario s = Create(); SecureExecutionBinding execution = Bind(s).Value;
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        CapabilityDescriptorV1 cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.Device,
            "device:secure-virtual-type2", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        PlatformDeviceLease device = s.Kernel.BindPlatformDevice(s.Owner, s.Parent, cap.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        int calls = 0;
        s.Provider.DuringVirtualIoBind = () => { calls++; MutateSecureLocal(OriginalSecureLocal(s.Kernel, s.Secure.Domain), "State", false); };
        Assert.False(s.Kernel.BindVirtualIo(s.Owner, s.Virtual.Domain, device,
            new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 4096), execution).IsSuccess);
        Assert.Equal(1, calls); s.Provider.DuringVirtualIoBind = null;
        Assert.True(s.Kernel.CloseSecureExecution(execution).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Fact]
    public void ActualType2SubmitRetainsItsOriginalSecureBaselineAfterKnownAcceptance()
    {
        int calls = 0;
        ComposedWorkload c = CreateComposedWorkload(afterSubmit: (kernel, _, _) =>
        {
            calls++;
            IDictionary routes = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureDomainRecords", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
            MutateSecureLocal(Assert.Single(routes.Values.Cast<object>()), "ProtectionGeneration", true);
        });
        Assert.Equal(1, calls); Assert.False(c.SubmissionResult.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, c.SubmissionResult.Error);
        Assert.False(c.Bridge.ReleaseFabric(c.Fabric).IsSuccess);
        Assert.False(c.Memory.Close(c.OutputPlacement.PlacementId).IsSuccess);
        Assert.True(c.Manager.BeginReconfiguration(c.Fabric).IsSuccess);
        KernelResult<CxlFabricManagedBinding> replacement = c.Manager.CompleteReconfiguration(c.Fabric.BindingId);
        Assert.True(replacement.IsSuccess, replacement.Message);
        CloseComposedWorkload(c, replacement.Value!.Binding);
        Assert.All(c.Base.Kernel.Regions.Snapshot(), region => Assert.Equal(RegionState.Released, region.State));
    }

    [Theory]
    [InlineData("State", false)] [InlineData("PolicyGeneration", true)]
    public void ActualGuestEventPublicationKeepsTheOriginalComposedSecureRevision(string property, bool aba)
    {
        Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
        OwnedBuffer<byte> region = s.Kernel.AllocateBuffer<byte>(s.Owner, 128).Value!;
        KernelEventEndpoint endpoint = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;
        s.Provider.DuringEvent = () => MutateSecureLocal(OriginalSecureLocal(s.Kernel, s.Secure.Domain), property, aba);
        Assert.False(s.Kernel.InjectVirtualEvent(s.Owner, s.Virtual.Domain, s.Virtual.EventCapability, endpoint).IsSuccess);
        Assert.Equal(1, s.Provider.EventEffects);
        s.Provider.DuringEvent = null;
        Assert.False(s.Kernel.InjectVirtualEvent(s.Owner, s.Virtual.Domain, s.Virtual.EventCapability, endpoint).IsSuccess);
        Assert.Equal(1, s.Provider.EventEffects);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData("State", false)] [InlineData("State", true)]
    [InlineData("PolicyGeneration", false)] [InlineData("PolicyGeneration", true)]
    [InlineData("ProtectionGeneration", false)] [InlineData("ProtectionGeneration", true)]
    public void ActualComputePublicationKeepsOriginalSecureRevisionAcrossItsServiceCallback(string property, bool aba)
    {
        ComposedWorkload c = CreateComposedWorkload(); Scenario s = c.Base;
        object local = OriginalSecureLocal(s.Kernel, s.Secure.Domain);
        s.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = () => MutateSecureLocal(local, property, aba);
        bool published = false;
        KernelResult<ExternalOperationSnapshot> result;
        try { result = c.Service.CompleteVisiblePublish(s.Owner, c.Execution, [c.Candidate], () => published = true); }
        finally { s.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = null; }
        Assert.False(result.IsSuccess); Assert.False(published);
        ExternalOperationSnapshot operation = s.Kernel.QueryExternalOperation(s.Owner, c.Execution.Operation).Value!;
        Assert.Equal(ExternalOperationState.Released, operation.State);
        Assert.NotEqual(ExternalOperationDisposition.Published, operation.Disposition);
        Assert.Equal(0, s.Provider.EventEffects);
        CloseComposedWorkload(c, c.Fabric);
        Assert.All(s.Kernel.Regions.Snapshot(), region => Assert.Equal(RegionState.Released, region.State));
    }

    [Theory]
    [InlineData(false, "State")] [InlineData(true, "State")]
    [InlineData(false, "ProtectionGeneration")] [InlineData(true, "ProtectionGeneration")]
    public void SecureGuestPublicationRequiresItsCapturedOriginalSecureSource(bool late, string property)
    {
        (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) o = OverlayScenario(); Scenario s = o.S;
        object local = OriginalSecureLocal(s.Kernel, s.Secure.Domain);
        void Change() => MutateSecureLocal(local, property, true);
        if (late) s.Kernel.BeforeSecureGuestRegionPublication = Change; else s.Kernel.BeforeSecureGuestRegionAdmission = Change;
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
        AssertOverlayPendingBalanced(s);
        s.Kernel.BeforeSecureGuestRegionPublication = null; s.Kernel.BeforeSecureGuestRegionAdmission = null;
        if (late) Assert.True(s.Kernel.CloseSecureGuestRegion(RetainedOverlay(s)).IsSuccess);
        Assert.True(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, o.Mapping.Mapping).IsSuccess);
        Assert.True(s.Kernel.CloseSecureExecution(o.Execution).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.All(s.Kernel.Regions.Snapshot(), region => Assert.Equal(RegionState.Released, region.State));
    }

    [Theory]
    [InlineData(false, "State")] [InlineData(true, "State")]
    [InlineData(false, "PolicyGeneration")] [InlineData(true, "ProtectionGeneration")]
    public void SecureExecutionBindAndRevalidationRefuseLocalEqualWriteOrAba(bool revalidate, string property)
    {
        Scenario s = Create(); OwnedBuffer<byte> region = s.Kernel.AllocateBuffer<byte>(s.Owner, 128).Value!;
        object local = OriginalSecureLocal(s.Kernel, s.Secure.Domain);
        void Change() => MutateSecureLocal(local, property, true);
        if (revalidate)
        {
            SecureExecutionBinding bound = Bind(s).Value;
            s.Provider.DuringRevalidation = Change;
            Assert.False(s.Kernel.RevalidateSecureExecution(bound).IsSuccess);
            s.Provider.DuringRevalidation = null;
            Assert.True(s.Kernel.CloseSecureExecution(bound).IsSuccess);
        }
        else
        {
            s.Provider.DuringBind = Change;
            Assert.False(Bind(s).IsSuccess); s.Provider.DuringBind = null;
        }
        Assert.Equal(1, s.Provider.BindCalls); Assert.Equal(1, s.Provider.CloseCalls);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualVirtualComputePublicationRefusesAnEqualLocalRevisionWrite(bool publicationBit)
    {
        ComposedWorkload c = CreateComposedWorkload();
        VirtualDomainAuthority authority = (VirtualDomainAuthority)typeof(RuntimeKernel).GetField("_virtualDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.Base.Kernel)!;
        VirtualDomainAuthority.Record local = Assert.Single(authority.ForOwner(c.Base.Owner));
        c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = () =>
        { if (publicationBit) local.AuthorityPublished = local.AuthorityPublished; else local.State = local.State; };
        bool published = false;
        KernelResult<ExternalOperationSnapshot> result;
        try { result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () => published = true); }
        finally { c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = null; }
        Assert.False(result.IsSuccess);
        Assert.False(published);
        ExternalOperationSnapshot operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.NotEqual(ExternalOperationDisposition.Published, operation.Disposition);
        Assert.Equal(ExternalOperationState.Released, operation.State);
        Assert.Equal(0, c.Base.Provider.EventEffects);
        CloseComposedWorkload(c, c.Fabric);
        Assert.All(c.Base.Kernel.Regions.Snapshot(), region => Assert.Equal(RegionState.Released, region.State));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SecureGuestBindKeepsOriginalVirtualRevisionThroughLocalPublication(bool late)
    {
        (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) o = OverlayScenario(); Scenario s = o.S;
        VirtualDomainAuthority authority = (VirtualDomainAuthority)typeof(RuntimeKernel).GetField("_virtualDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        VirtualDomainAuthority.Record local = Assert.Single(authority.ForOwner(s.Owner));
        void Change() => local.State = local.State;
        if (late) s.Kernel.BeforeSecureGuestRegionPublication = Change; else s.Kernel.BeforeSecureGuestRegionAdmission = Change;
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
        AssertOverlayPendingBalanced(s);
        s.Kernel.BeforeSecureGuestRegionPublication = null; s.Kernel.BeforeSecureGuestRegionAdmission = null;
        if (late) Assert.True(s.Kernel.CloseSecureGuestRegion(RetainedOverlay(s)).IsSuccess);
        Assert.True(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, o.Mapping.Mapping).IsSuccess);
        Assert.True(s.Kernel.CloseSecureExecution(o.Execution).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.All(s.Kernel.Regions.Snapshot(), region => Assert.Equal(RegionState.Released, region.State));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void OriginalVirtualRevisionIsRequiredAcrossActualSecureBindAndRevalidation(int fault)
    {
        Scenario s = Create();
        OwnedBuffer<byte> region = s.Kernel.AllocateBuffer<byte>(s.Owner, 128).Value!;
        VirtualDomainAuthority authority = (VirtualDomainAuthority)typeof(RuntimeKernel).GetField("_virtualDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        VirtualDomainAuthority.Record local = Assert.Single(authority.ForOwner(s.Owner));
        void Change()
        {
            if (fault % 2 == 0) local.State = local.State;
            else local.AuthorityPublished = local.AuthorityPublished;
        }
        if (fault < 2)
        {
            s.Provider.DuringBind = Change;
            Assert.False(Bind(s).IsSuccess);
            Assert.Equal(1, s.Provider.BindCalls);
            Assert.Equal(1, s.Provider.CloseCalls); // Same admitted receipt, existing exact cleanup.
            s.Provider.DuringBind = null;
        }
        else
        {
            SecureExecutionBinding bound = Bind(s).Value;
            s.Provider.DuringRevalidation = Change;
            Assert.False(s.Kernel.RevalidateSecureExecution(bound).IsSuccess);
            s.Provider.DuringRevalidation = null;
            Assert.True(s.Kernel.CloseSecureExecution(bound).IsSuccess);
            Assert.Equal(1, s.Provider.BindCalls);
            Assert.Equal(1, s.Provider.CloseCalls);
        }
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
    }

    private sealed class ObservedSecureProperties(Action observe) : IReadOnlyList<SecureProperty>
    {
        public int Count { get { observe(); return 1; } }
        public SecureProperty this[int index] { get { observe(); return index == 0 ? SecureProperty.PrivateMemory : throw new IndexOutOfRangeException(); } }
        public IEnumerator<SecureProperty> GetEnumerator() { observe(); yield return SecureProperty.PrivateMemory; }
        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void SecureCompositionDoesNotEnumerateProviderMetadataUnderOwnerCommitLocks()
    {
        int observations = 0;
        Scenario s = Create(provider => provider.SecureProperties = new ObservedSecureProperties(() =>
        {
            observations++;
            provider.CheckLocks!.Invoke();
        }));
        Assert.True(observations > 0);
        int beforeBind = observations;
        KernelResult<SecureExecutionBinding> bound = Bind(s);
        Assert.True(bound.IsSuccess, bound.Message);
        Assert.Equal(beforeBind, observations);
        Assert.Equal(1, s.Provider.BindCalls);
        Assert.True(s.Kernel.CloseSecureExecution(bound.Value).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EqualTupleForeignRootCannotAdmitOrPublishSecureComposition(bool duringBind)
    {
        Scenario s = Create();
        Assert.True(s.Kernel.AllocateRegion(s.Owner, 4096).IsSuccess);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        PlatformAuthorityBridge bridge = s.Kernel.PlatformAuthority;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_domains", flags)!.GetValue(bridge)!;
        IDictionary originals = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_activeSubjects", flags)!.GetValue(bridge)!;
        object original = routes[s.Parent.BindingId]!;
        void Replace()
        {
            object foreign = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            routes[s.Parent.BindingId] = foreign;
            originals[s.Parent.Subject] = foreign;
        }
        if (duringBind) s.Provider.DuringBind = Replace;
        else Replace();
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(duringBind ? 1 : 0, s.Provider.BindCalls);
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(duringBind ? 1 : 0, s.Provider.BindCalls);
        Assert.Equal(duringBind ? 1 : 0, s.Provider.CloseCalls);
        if (duringBind)
        {
            Assert.NotNull(s.Provider.LastCloseReceipt);
            Assert.Equal(s.Provider.LastRequest, s.Provider.LastCloseRequest);
            Assert.Equal(s.Provider.LastRequest, s.Provider.LastCloseReceipt!.Value.Request);
        }
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(0, s.Provider.SecureCloses);
        Assert.Equal(0, s.Provider.ChildCloses);
    }

    private sealed class ClosureTraceSink(Func<SemanticTraceEventV1, bool> observe) : ISemanticTraceSinkV1
    {
        public bool TryRecord(SemanticTraceEventV1 traceEvent) => observe(traceEvent);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ConcurrentActualSecureGuestBindRetainsOriginalCxlSourceAndPending(bool memoryRoute, bool foreign, bool stop)
    {
        (Scenario S, CxlType3ModelProvider Model, CxlType3MemoryAuthority Memory, CheckedMemoryProvider Queries, CxlMemoryPlacementSnapshot Placement, GuestRegionMapping Mapping, SecureExecutionBinding Execution, RegionOwner Owner, CapabilityId DeviceCapability) o = BackedOverlayScenario(); Scenario s = o.S;
        CxlAuthorityBridge bridge = (CxlAuthorityBridge)typeof(CxlType3MemoryAuthority)
            .GetField("_bridge", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o.Memory)!;
        CxlMemoryBinding memory = bridge.QueryMemory(o.Placement.MemoryBindingId).Value!;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge)
            .GetField(memoryRoute ? "_trackedMemory" : "_trackedFabric", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        object key = memoryRoute ? o.Placement.MemoryBindingId : memory.FabricBinding.BindingId;
        object original = routes[key]!;
        object gate = typeof(CxlAuthorityBridge).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Provider.SecureRegionClosureFault = true;
        s.Provider.DuringSecureRegionBind = () =>
        {
            s.Provider.CheckLocks!();
            Assert.False(Monitor.IsEntered(gate));
            entered.SetResult();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(15)));
        };
        Task<KernelResult<SecureGuestRegionBinding>> binding = Task.Run(() => s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, s.Provider.SecureRegionBindCalls);
            Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, o.Mapping.Mapping).IsSuccess);
            Assert.False(s.Kernel.CloseSecureExecution(o.Execution).IsSuccess);
            Assert.Equal(0, s.Provider.CloseCalls);
            Assert.False(o.Memory.Close(o.Placement.PlacementId).IsSuccess);
            Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
            Assert.Equal(1, s.Provider.SecureRegionBindCalls);
            lock (gate)
            {
                if (foreign) routes[key] = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)!;
                else routes.Remove(key);
            }
            if (stop) Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        }
        finally { resume.Set(); }
        KernelResult<SecureGuestRegionBinding> result = await binding.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(result.IsSuccess);
        Assert.Equal(1, s.Provider.SecureRegionBindCalls);
        _ = RetainedOverlay(s);
        AssertOverlayPendingBalanced(s);
        Assert.Equal(memory, o.Model.QueryMemory(memory.BindingId).Value!);
        Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, o.Mapping.Mapping).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        AssertOverlayPendingBalanced(s);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ConcurrentActualComposedActionCannotCommitThroughLostOriginalCxlBacking(bool memoryRoute, bool foreign, bool stop)
    {
        ComposedWorkload c = CreateComposedWorkload();
        CxlMemoryPlacementSnapshot placement = c.OutputPlacement;
        CxlMemoryBinding memory = c.Bridge.QueryMemory(placement.MemoryBindingId).Value!;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge)
            .GetField(memoryRoute ? "_trackedMemory" : "_trackedFabric", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.Bridge)!;
        object key = memoryRoute ? placement.MemoryBindingId : memory.FabricBinding.BindingId;
        object original = routes[key]!;
        object gate = typeof(CxlAuthorityBridge).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.Bridge)!;
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int actions = 0;
        Task<KernelResult<ExternalOperationSnapshot>> publishing = Task.Run(() => c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () =>
        {
            c.Base.Provider.CheckLocks!();
            Assert.False(Monitor.IsEntered(gate));
            Interlocked.Increment(ref actions);
            entered.SetResult();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(15)));
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            int overlappingActions = 0;
            Assert.False(c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () => overlappingActions++).IsSuccess);
            Assert.Equal(0, overlappingActions);
            Assert.False(c.Base.Kernel.CloseGuestRegionMapping(c.Base.Owner, c.Base.Virtual.Domain,
                c.Base.Virtual.MemoryCapability, c.OutputGuest.Mapping).IsSuccess);
            Assert.False(c.Memory.Close(placement.PlacementId).IsSuccess);
            lock (gate)
            {
                if (foreign) routes[key] = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)!;
                else routes.Remove(key);
            }
            if (stop) Assert.False(c.Base.Kernel.TerminateProcess(c.Base.Owner).IsSuccess);
        }
        finally { resume.Set(); }
        KernelResult<ExternalOperationSnapshot> result = await publishing.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(result.IsSuccess);
        Assert.Equal(1, actions);
        ExternalOperationSnapshot operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.DoesNotContain(operation.Transitions, x => x.Event == "Published");
        Assert.Contains(operation.Transitions, x => x.Event == "PublicationEffectAmbiguous");
        AssertType2OwnerTrace(c, operation, published: false);
        Assert.False(c.Base.Kernel.TerminateProcess(c.Base.Owner).IsSuccess);
        Assert.False(c.Base.Kernel.QueryProcessTeardown(c.Base.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(memory, c.Model.QueryMemory(memory.BindingId).Value!);
        AssertOverlayPendingBalanced(c.Base);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 2)]
    [InlineData(false, true, 2)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 2)]
    [InlineData(false, false, 3)]
    [InlineData(false, true, 3)]
    [InlineData(true, false, 3)]
    [InlineData(true, true, 3)]
    public void ActualSecureGuestCxlBackingLossRefusesEffectOrRetainsKnownOverlay(bool memoryRoute, bool foreign, int lossStage)
    {
        Scenario s = Create();
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, s.Owner.Generation);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(process.DomainId, s.Owner);
        CapabilityDescriptorV1 cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.Device,
            "device:secure-backing-source", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        PlatformDeviceLease device = s.Kernel.BindPlatformDevice(s.Owner, s.Parent, cap.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        CxlType3ModelProvider model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("secure-backing-source"), device.Device, 4096).IsSuccess);
        CxlEndpointSnapshot endpoint = model.QueryEndpoint(new("secure-backing-source")).Value!;
        CxlAuthorityBridge bridge = new CxlAuthorityBridge(s.Kernel, model, model, model, model,
            new UnsupportedCoherentProvider(), new CxlSecurityModelProvider());
        CxlType3MemoryAuthority memory = new CxlType3MemoryAuthority(s.Kernel, bridge);
        OwnedBuffer<byte> buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        CxlMemoryPlacementSnapshot placement = memory.Place(owner, buffer.Handle, subject, device, endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryBinding binding = bridge.QueryMemory(placement.MemoryBindingId).Value!;
        CapabilityId region = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        GuestRegionMapping guest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, region,
            buffer.Handle, new(0, 16), GuestMemoryAccess.Read).Value!;
        SecureExecutionBinding execution = Bind(s).Value;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge)
            .GetField(memoryRoute ? "_trackedMemory" : "_trackedFabric", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        object key = memoryRoute ? placement.MemoryBindingId : binding.FabricBinding.BindingId;
        object original = routes[key]!;
        object gate = typeof(CxlAuthorityBridge).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        bool lost = false;
        void Lose()
        {
            s.Provider.CheckLocks!();
            Assert.False(Monitor.IsEntered(gate));
            lock (gate)
            {
                if (foreign) routes[key] = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)!;
                else routes.Remove(key);
            }
            lost = true;
        }
        if (lossStage == 0) Lose();
        if (lossStage == 1) s.Kernel.BeforeSecureGuestRegionAdmission = Lose;
        if (lossStage == 2) s.Provider.DuringSecureRegionBind = Lose;
        if (lossStage == 3) s.Kernel.BeforeSecureGuestRegionPublication = Lose;
        if (lossStage >= 2) s.Provider.SecureRegionClosureFault = true;
        KernelResult<SecureGuestRegionBinding> result = s.Kernel.BindSecureGuestRegion(s.Owner, execution, guest.Mapping, PlatformSecureRegionClass.Private);
        Assert.True(lost);
        Assert.False(result.IsSuccess);
        Assert.Equal(lossStage >= 2 ? 1 : 0, s.Provider.SecureRegionBindCalls);
        AssertOverlayPendingBalanced(s);
        SecureGuestRegionBinding? retained = null;
        if (lossStage >= 2)
        {
            retained = RetainedOverlay(s);
            Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, guest.Mapping).IsSuccess);
        }
        Assert.Equal(binding, model.QueryMemory(binding.BindingId).Value!);
        Assert.False(s.Kernel.ReleaseRegion(s.Owner, buffer).IsSuccess);
        Assert.False(s.Kernel.RevokePlatformDevice(s.Owner, device).IsSuccess);
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, execution, guest.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(lossStage >= 2 ? 1 : 0, s.Provider.SecureRegionBindCalls);
        s.Kernel.BeforeSecureGuestRegionAdmission = null;
        s.Kernel.BeforeSecureGuestRegionPublication = null;
        s.Provider.DuringSecureRegionBind = null;
        s.Provider.SecureRegionClosureFault = false;
        // Restore the exact original owner for cleanup positive; this is not durable routing.
        lock (gate) routes[key] = original;
        if (retained is { } overlay)
        {
            Assert.False(s.Kernel.CloseSecureGuestRegion(overlay).IsSuccess);
            Assert.False(s.Kernel.CloseSecureExecution(execution).IsSuccess);
            Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, guest.Mapping).IsSuccess);
            Assert.False(memory.Close(placement.PlacementId).IsSuccess);
            Assert.Equal(1, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
            Assert.Equal(RegionState.Owned, s.Kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
            AssertOverlayPendingBalanced(s);
            return;
        }
        Assert.True(s.Kernel.CloseSecureExecution(execution).IsSuccess);
        Assert.True(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, guest.Mapping).IsSuccess);
        Assert.True(memory.Close(placement.PlacementId).IsSuccess);
        AssertOverlayPendingBalanced(s);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, true, true)]
    public void ActualComposedPublicationRequiresOriginalCxlOperandBacking(bool memoryRoute, bool foreign, bool late, bool output)
    {
        ComposedWorkload c = CreateComposedWorkload();
        CxlMemoryPlacementSnapshot placement = output ? c.OutputPlacement : c.InputPlacement;
        CxlMemoryBinding memory = c.Bridge.QueryMemory(placement.MemoryBindingId).Value!;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(CxlAuthorityBridge)
            .GetField(memoryRoute ? "_trackedMemory" : "_trackedFabric", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.Bridge)!;
        object key = memoryRoute ? placement.MemoryBindingId : memory.FabricBinding.BindingId;
        object original = routes[key]!;
        object gate = typeof(CxlAuthorityBridge).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.Bridge)!;
        bool lost = false;
        void Lose()
        {
            c.Base.Provider.CheckLocks!();
            Assert.False(Monitor.IsEntered(gate));
            lock (gate)
            {
                if (foreign) routes[key] = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)!;
                else routes.Remove(key);
            }
            lost = true;
        }
        if (!late) c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = Lose;
        int actions = 0;
        KernelResult<ExternalOperationSnapshot> result;
        try
        {
            result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () =>
            {
                actions++;
                if (late) Lose();
            });
        }
        finally { c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = null; }
        Assert.True(lost);
        Assert.False(result.IsSuccess);
        Assert.Equal(late ? 1 : 0, actions);
        ExternalOperationSnapshot operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.DoesNotContain(operation.Transitions, x => x.Event == "Published");
        Assert.Equal(late, operation.Transitions.Any(x => x.Event == "PublicationEffectAmbiguous"));
        if (late) AssertType2OwnerTrace(c, operation, published: false);
        // Before action, exact provider closure may release the operation; it cannot
        // release the independent original CXL backing or guest source pins.
        Assert.False(c.Base.Kernel.TerminateProcess(c.Base.Owner).IsSuccess);
        Assert.False(c.Base.Kernel.QueryProcessTeardown(c.Base.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(memory, c.Model.QueryMemory(memory.BindingId).Value!);
        AssertOverlayPendingBalanced(c.Base);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void CompositionTraceRetainsDistinctMalformedReturnedClosureFacts(int fault)
    {
        SemanticTraceEventV1[] Observe(ulong value)
        {
            Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
            SecureExecutionClosure returned = default;
            s.Provider.ClosureTransform = closure =>
            {
                SecureExecutionReceipt receipt = closure.Receipt!.Value;
                returned = fault switch
                {
                    0 => closure with { Receipt = receipt with { Correlation = value } },
                    1 => closure with { Receipt = receipt with { Request = receipt.Request with { Context = receipt.Request.Context with { LocalPolicyGeneration = value } } } },
                    2 => closure with { Request = closure.Request with { Context = closure.Request.Context with { ProvenProperties = $"returned:{value}:雪" } } },
                    _ => closure with { Receipt = receipt with { ProtectionGeneration = value } },
                };
                return returned;
            };
            List<SemanticTraceEventV1> trace = new List<SemanticTraceEventV1>();
            Assert.False(s.Kernel.CloseSecureExecution(binding, new ClosureTraceSink(e => { trace.Add(e); return true; })).IsSuccess);
            Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
            Assert.Equal(SemanticTraceEventKindV1.Quarantined, trace[^1].Kind);
            IDictionary records = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureExecutions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
            object record = records.Values.Cast<object>().Single();
            object fact = record.GetType().GetField("LastClosureReceipt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(record)!;
            Assert.Equal(returned, (SecureExecutionClosure)fact.GetType().GetProperty("Receipt")!.GetValue(fact)!);
            Assert.Equal(0, s.Provider.SecureCloses); Assert.Equal(0, s.Provider.ChildCloses);
            s.Provider.ClosureTransform = null;
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.CloseCalls);
            return trace.ToArray();
        }
        SemanticTraceEventV1[] a = Observe(998); SemanticTraceEventV1[] b = Observe(999);
        Assert.Equal(a[0].GenerationVectorDigest, b[0].GenerationVectorDigest);
        Assert.Equal(a[0].EvidenceDigest, b[0].EvidenceDigest);
        Assert.Equal(a[1].EvidenceDigest, b[1].EvidenceDigest);
        Assert.NotEqual(a[^1].EvidenceDigest, b[^1].EvidenceDigest);
        Assert.All(a.Concat(b), e => Assert.False(e.AuthorizesEffect));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void CompositionTraceDistinguishesNestedReceiptContext(int fault)
    {
        string Observe(bool mutate)
        {
            Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
            IDictionary records = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureExecutions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
            object record = records.Values.Cast<object>().Single();
            FieldInfo field = record.GetType().GetField("Receipt", BindingFlags.Instance | BindingFlags.NonPublic)!;
            SecureExecutionReceipt receipt = (SecureExecutionReceipt)field.GetValue(record)!;
            SecureExecutionContext c = receipt.Request.Context;
            if (mutate)
            {
                c = fault switch
                {
                    0 => c with { ProvenProperties = "é:雪|\"\n" },
                    1 => c with { Parent = c.Parent with { Generation = new(999) } },
                    2 => c with { Child = c.Child with { Owner = c.Child.Owner with { Generation = 999 } } },
                    3 => c with { ChildLease = c.ChildLease with { ParentDomainLease = c.ChildLease.ParentDomainLease with { Generation = new(999) } } },
                    4 => c with { SecureLease = c.SecureLease with { ProvenProperties = new[] { (SecureProperty)999 } } },
                    _ => c with { SecureBinding = c.SecureBinding with { Parent = c.SecureBinding.Parent with { Generation = new(999) } } },
                };
                field.SetValue(record, receipt with { Request = receipt.Request with { Context = c } });
            }
            List<SemanticTraceEventV1> trace = new List<SemanticTraceEventV1>();
            Assert.True(s.Kernel.CloseSecureExecution(binding, new ClosureTraceSink(e => { trace.Add(e); return true; })).IsSuccess);
            Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            return trace[0].GenerationVectorDigest;
        }
        Assert.NotEqual(Observe(false), Observe(true));
    }

    private sealed class BrokenTraceProperties(Action queried) : IReadOnlyList<SecureProperty>
    {
        public int Count => 1;
        public SecureProperty this[int index] => throw new IOException("property enumeration lost");
        public IEnumerator<SecureProperty> GetEnumerator() { queried(); throw new IOException("property enumeration lost"); }
        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompositionTraceSerializationFailureCannotChangeCleanup(bool returnedClosure)
    {
        Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
        IDictionary records = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureExecutions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        object record = records.Values.Cast<object>().Single();
        FieldInfo field = record.GetType().GetField("Receipt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        SecureExecutionReceipt receipt = (SecureExecutionReceipt)field.GetValue(record)!;
        bool queried = false, locked = false;
        BrokenTraceProperties properties = new BrokenTraceProperties(() =>
        {
            queried = true;
            foreach (string? name in new[] { "_secureExecutionGate", "_platformMemoryUseGate" })
                locked |= Monitor.IsEntered(typeof(RuntimeKernel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!);
        });
        if (returnedClosure)
            s.Provider.ClosureTransform = closure => closure with { Request = closure.Request with { Context = closure.Request.Context with {
                SecureLease = closure.Request.Context.SecureLease with { ProvenProperties = properties } } } };
        else
            field.SetValue(record, receipt with { Request = receipt.Request with { Context = receipt.Request.Context with {
                SecureLease = receipt.Request.Context.SecureLease with { ProvenProperties = properties } } } });
        List<SemanticTraceEventV1> trace = new List<SemanticTraceEventV1>();
        Assert.Equal(!returnedClosure, s.Kernel.CloseSecureExecution(binding, new ClosureTraceSink(e => { trace.Add(e); return true; })).IsSuccess);
        Assert.True(queried); Assert.False(locked); Assert.Empty(trace);
        Assert.Equal(1, s.Provider.CloseCalls);
        s.Provider.ClosureTransform = null;
        Assert.Equal(!returnedClosure, s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void CompositionTraceDistinguishesAdmittedReceiptMetadata(int fault)
    {
        string Observe(bool mutate)
        {
            Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
            IDictionary records = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureExecutions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
            object record = records.Values.Cast<object>().Single();
            FieldInfo field = record.GetType().GetField("Receipt", BindingFlags.Instance | BindingFlags.NonPublic)!;
            SecureExecutionReceipt receipt = (SecureExecutionReceipt)field.GetValue(record)!;
            // Fault injection into the admitted cleanup input, never production admission.
            if (mutate) field.SetValue(record, fault switch
            {
                0 => (SecureExecutionReceipt?)null,
                1 => receipt with { Correlation = receipt.Correlation + 1 },
                2 => receipt with { PolicyGeneration = receipt.PolicyGeneration + 1 },
                3 => receipt with { ProtectionGeneration = receipt.ProtectionGeneration + 1 },
                4 => receipt with { ContractVersion = receipt.ContractVersion + 1 },
                5 => receipt with { ProductionSecure = false },
                6 => receipt with { Request = receipt.Request with { Binding = new(999, 1) } },
                _ => receipt with { Request = receipt.Request with { Binding = binding with { Generation = 2 } } },
            });
            List<SemanticTraceEventV1> trace = new List<SemanticTraceEventV1>();
            Assert.True(s.Kernel.CloseSecureExecution(binding, new ClosureTraceSink(e => { trace.Add(e); return true; })).IsSuccess);
            Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
            Assert.All(trace, e => Assert.False(e.AuthorizesEffect));
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            return trace[0].GenerationVectorDigest;
        }
        Assert.NotEqual(Observe(false), Observe(true));
    }

    [Theory]
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(5, false)] [InlineData(5, true)]
    [InlineData(6, false)] [InlineData(6, true)]
    public void BindCompensationTracePreservesReceiptLossAndRetry(int admissionFault, bool refuseClosure)
    {
        Scenario s = Create(); List<SemanticTraceEventV1> trace = new List<SemanticTraceEventV1>();
        s.Provider.AdmissionFault = admissionFault;
        s.Provider.ClosureFault = refuseClosure ? 3 : -1;
        bool heldLock = false;
        ClosureTraceSink sink = new ClosureTraceSink(e =>
        {
            foreach (string? field in new[] { "_secureExecutionGate", "_platformMemoryUseGate" })
                heldLock |= Monitor.IsEntered(typeof(RuntimeKernel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!);
            trace.Add(e); return true;
        });
        Assert.False(s.Kernel.BindSecureExecutionWithClosureTrace(s.Owner, s.Virtual.Domain, s.Virtual.ConfigureCapability,
            s.Secure.Domain, s.Secure.ConfigureCapability, sink).IsSuccess);
        Assert.False(heldLock);
        Assert.Equal(1, s.Provider.BindCalls);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.Equal(admissionFault == 0, s.Provider.LastCloseReceipt.HasValue);
        Assert.Equal(refuseClosure ? SemanticTraceEventKindV1.Quarantined : SemanticTraceEventKindV1.RetireOrComplete, trace[^1].Kind);
        Assert.Equal(0, s.Provider.SecureCloses);
        Assert.Equal(0, s.Provider.ChildCloses);
        if (refuseClosure)
        {
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(0, s.Provider.SecureCloses);
            Assert.Equal(0, s.Provider.ChildCloses);
            s.Provider.ClosureFault = -1;
        }
        Assert.Equal(!refuseClosure, s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.Equal(refuseClosure ? 3 : 1, trace.Select(e => e.OperationCorrelation).Distinct().Count());
        foreach (IGrouping<string, SemanticTraceEventV1> group in trace.GroupBy(e => e.OperationCorrelation))
        {
            Assert.Equal(new ulong[] { 1, 2, 3 }, group.Select(e => e.Sequence));
            Assert.True(SemanticTraceValidatorV1.Validate(group).IsValid);
        }
        Assert.Equal(refuseClosure ? SemanticTraceEventKindV1.Quarantined : SemanticTraceEventKindV1.RetireOrComplete, trace[^1].Kind);
        Assert.All(trace, e => { Assert.False(e.AuthorizesEffect); Assert.False(e.AuthorizesPublication); });
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void DomainTeardownPreservesRegisteredCompositionObserver(bool secure, bool refuse)
    {
        Scenario s = Create(); List<SemanticTraceEventV1> trace = new List<SemanticTraceEventV1>();
        bool heldLock = false;
        ClosureTraceSink sink = new ClosureTraceSink(e =>
        {
            foreach (string? field in new[] { "_secureExecutionGate", "_platformMemoryUseGate" })
                heldLock |= Monitor.IsEntered(typeof(RuntimeKernel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!);
            heldLock |= Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.PlatformAuthority)!);
            trace.Add(e); return true;
        });
        Assert.True(s.Kernel.BindSecureExecutionWithClosureTrace(s.Owner, s.Virtual.Domain, s.Virtual.ConfigureCapability,
            s.Secure.Domain, s.Secure.ConfigureCapability, sink).IsSuccess);
        KernelResult Destroy() => secure
            ? s.Kernel.DestroySecureDomain(s.Owner, s.Secure.Domain, s.Secure.ConfigureCapability)
            : s.Kernel.DestroyVirtualDomain(s.Owner, s.Virtual.Domain, s.Virtual.ConfigureCapability);
        if (refuse) s.Provider.ClosureFault = 3;
        Assert.Equal(secure && !refuse, Destroy().IsSuccess);
        Assert.False(heldLock);
        Assert.Equal(1, s.Provider.CloseCalls);
        if (refuse)
        {
            Assert.Equal(SemanticTraceEventKindV1.Quarantined, trace[^1].Kind);
            Assert.Equal(0, s.Provider.SecureCloses);
            Assert.Equal(0, s.Provider.ChildCloses);
            s.Provider.ClosureFault = -1;
            Assert.False(Destroy().IsSuccess);
            Assert.Equal(1, s.Provider.CloseCalls);
        }
        Assert.Equal(refuse ? 2 : 1, trace.Select(e => e.OperationCorrelation).Distinct().Count());
        foreach (IGrouping<string, SemanticTraceEventV1> group in trace.GroupBy(e => e.OperationCorrelation))
        {
            Assert.Equal(new ulong[] { 1, 2, 3 }, group.Select(e => e.Sequence));
            Assert.True(SemanticTraceValidatorV1.Validate(group).IsValid);
        }
        Assert.Equal(refuse ? SemanticTraceEventKindV1.Quarantined : SemanticTraceEventKindV1.RetireOrComplete, trace[^1].Kind);
        Assert.All(trace, e => { Assert.False(e.AuthorizesEffect); Assert.False(e.AuthorizesPublication); });
        if (!secure)
            Assert.Equal(!refuse, s.Kernel.DestroySecureDomain(s.Owner, s.Secure.Domain, s.Secure.ConfigureCapability).IsSuccess);
        Assert.Equal(!refuse, s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ProcessTeardownConsumesRegisteredCompositionTrace(int fault)
    {
        Scenario s = Create(); List<SemanticTraceEventV1> trace = new List<SemanticTraceEventV1>();
        int calls = 0;
        bool heldOwnerLock = false;
        ClosureTraceSink sink = new ClosureTraceSink(e =>
        {
            foreach (string? field in new[] { "_secureExecutionGate", "_platformMemoryUseGate" })
                heldOwnerLock |= Monitor.IsEntered(typeof(RuntimeKernel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!);
            calls++;
            if (fault == 1) return false;
            if (fault == 2) throw new IOException("teardown observer lost");
            trace.Add(e); return true;
        });
        SecureExecutionBinding binding = s.Kernel.BindSecureExecutionWithClosureTrace(s.Owner, s.Virtual.Domain, s.Virtual.ConfigureCapability,
            s.Secure.Domain, s.Secure.ConfigureCapability, sink).Value;
        IDictionary records = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureExecutions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        object record = records.Values.Cast<object>().Single();
        FieldInfo sinkField = record.GetType().GetField("ClosureTraceSink", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Same(sink, sinkField.GetValue(record));
        if (fault == 3) s.Provider.ClosureFault = 3;
        KernelResult terminated = s.Kernel.TerminateProcess(s.Owner);
        Assert.Equal(fault != 3, terminated.IsSuccess);
        Assert.False(heldOwnerLock);
        Assert.Equal(1, s.Provider.CloseCalls);
        if (fault == 3)
        {
            Assert.Same(sink, sinkField.GetValue(record));
            Assert.Equal(SemanticTraceEventKindV1.Quarantined, trace[^1].Kind);
            Assert.Equal(0, s.Provider.SecureCloses);
            Assert.Equal(0, s.Provider.ChildCloses);
            s.Provider.ClosureFault = -1;
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.CloseCalls);
            Assert.Equal(2, trace.Select(e => e.OperationCorrelation).Distinct().Count());
        }
        if (fault == 3) Assert.Same(sink, sinkField.GetValue(record));
        else Assert.Null(sinkField.GetValue(record));
        Assert.Equal(fault is 1 or 2 ? 1 : fault == 3 ? 6 : 3, calls);
        foreach (IGrouping<string, SemanticTraceEventV1> group in trace.GroupBy(e => e.OperationCorrelation))
            Assert.True(SemanticTraceValidatorV1.Validate(group).IsValid);
        if (trace.Count != 0) Assert.Equal(fault == 3 ? SemanticTraceEventKindV1.Quarantined : SemanticTraceEventKindV1.RetireOrComplete, trace[^1].Kind);
        Assert.All(trace, e => Assert.False(e.AuthorizesEffect));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void CompositionTraceDeliveryLossNeverChangesClosure(int fault)
    {
        Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
        List<SemanticTraceEventV1> trace = new List<SemanticTraceEventV1>();
        int calls = 0;
        if (fault == 4)
        {
            IDictionary records = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureExecutions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
            object record = records.Values.Cast<object>().Single();
            record.GetType().GetField("ClosureObservationSequence", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(record, ulong.MaxValue);
        }
        ClosureTraceSink sink = new ClosureTraceSink(e =>
        {
            s.Provider.CheckLocks?.Invoke();
            calls++;
            if (calls == (fault < 2 ? 1 : 2))
            {
                if (fault is 0 or 2) return false;
                throw new IOException("trace sink lost");
            }
            trace.Add(e); return true;
        });
        Assert.True(s.Kernel.CloseSecureExecution(binding, sink).IsSuccess);
        Assert.Equal(fault == 4 ? 0 : fault < 2 ? 1 : 2, calls);
        Assert.Equal(fault is 2 or 3 ? 1 : 0, trace.Count);
        Assert.Equal(trace.Count != 0, SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.True(s.Kernel.CloseSecureExecution(binding, sink).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompositionTraceReentrantRetryPreservesInvocationDecision(bool rejectOuter)
    {
        Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
        s.Provider.ClosureFault = 3;
        List<SemanticTraceEventV1> outer = new List<SemanticTraceEventV1>();
        List<SemanticTraceEventV1> inner = new List<SemanticTraceEventV1>();
        KernelResult retry = default;
        KernelResult result = s.Kernel.CloseSecureExecution(binding, new ClosureTraceSink(e =>
        {
            s.Provider.CheckLocks?.Invoke();
            foreach (string? field in new[] { "_secureExecutionGate", "_platformMemoryUseGate" })
                Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!));
            if (outer.Count == 0)
            {
                s.Provider.ClosureFault = -1;
                retry = s.Kernel.CloseSecureExecution(binding, new ClosureTraceSink(n => { inner.Add(n); return true; }));
            }
            outer.Add(e);
            return !rejectOuter;
        }));
        Assert.False(result.IsSuccess);
        Assert.False(retry.IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.Equal(rejectOuter ? 1 : 3, outer.Count);
        Assert.Equal(SemanticTraceEventKindV1.Quarantined, inner[^1].Kind);
        if (!rejectOuter) Assert.Equal(SemanticTraceEventKindV1.Quarantined, outer[^1].Kind);
        Assert.True(SemanticTraceValidatorV1.Validate(outer).IsValid);
        Assert.True(SemanticTraceValidatorV1.Validate(inner).IsValid);
        Assert.NotEqual(outer[0].OperationCorrelation, inner[0].OperationCorrelation);
        Assert.All(outer.Concat(inner), e => Assert.False(e.AuthorizesEffect));
        Assert.False(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CompositionTraceOverlappingFlushKeepsIndependentDecisions(bool terminateDuringFlush)
    {
        Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
        s.Provider.ClosureFault = 3;
        ConcurrentQueue<SemanticTraceEventV1> events = new global::System.Collections.Concurrent.ConcurrentQueue<SemanticTraceEventV1>();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new ManualResetEventSlim();
        string? firstCorrelation = null;
        bool released = false, heldOwnerLock = false;
        ClosureTraceSink sink = new ClosureTraceSink(e =>
        {
            foreach (string? field in new[] { "_secureExecutionGate", "_platformMemoryUseGate" })
                if (Monitor.IsEntered(typeof(RuntimeKernel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!))
                    heldOwnerLock = true;
            events.Enqueue(e);
            if (Interlocked.CompareExchange(ref firstCorrelation, e.OperationCorrelation, null) is null)
            {
                entered.TrySetResult();
                released = release.Wait(TimeSpan.FromSeconds(10));
            }
            return true;
        });
        Task<KernelResult> first = Task.Run(() => s.Kernel.CloseSecureExecution(binding, sink));
        KernelResult second = default;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            s.Provider.ClosureFault = -1;
            second = await Task.Run(() => s.Kernel.CloseSecureExecution(binding, sink)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(second.IsSuccess);
            Assert.False(first.IsCompleted);
            if (terminateDuringFlush) Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        }
        finally { release.Set(); }
        Assert.False((await first.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        Assert.True(released);
        Assert.False(heldOwnerLock);
        Assert.Equal(1, s.Provider.CloseCalls);
        SemanticTraceEventV1[] stream = events.ToArray();
        IGrouping<string, SemanticTraceEventV1>[] grouped = stream.GroupBy(e => e.OperationCorrelation).ToArray();
        Assert.Equal(2, grouped.Length);
        foreach (IGrouping<string, SemanticTraceEventV1>? group in grouped)
        {
            Assert.Equal(new ulong[] { 1, 2, 3 }, group.Select(e => e.Sequence));
            Assert.True(SemanticTraceValidatorV1.Validate(group).IsValid);
            Assert.Equal(SemanticTraceEventKindV1.Quarantined, group.Last().Kind);
        }
        Assert.Equal(SemanticTraceValidationStatusV1.MixedOperation, SemanticTraceValidatorV1.Validate(stream).Status);
        Assert.All(stream, e => { Assert.False(e.AuthorizesExecution); Assert.False(e.AuthorizesEffect); Assert.False(e.AuthorizesPublication); });
        if (!terminateDuringFlush) Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DirectBridgeResetCannotPublishCompositionClosure(bool contained)
    {
        Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
        s.Provider.Contained = contained;
        s.Provider.DuringClose = () => Assert.True(s.Kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        List<SemanticTraceEventV1> trace = new List<SemanticTraceEventV1>();
        Assert.False(s.Kernel.CloseSecureExecution(binding, new ClosureTraceSink(e => { trace.Add(e); return true; })).IsSuccess);
        Assert.Equal(new[] { SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined }, trace.Select(e => e.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.Equal(1, s.Provider.CloseCalls);
        s.Provider.DuringClose = null;
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void CompositionClosureRetainsRawFactsWithoutConflatingClosedAndContained(int fault)
    {
        Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
        IDictionary records = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureExecutions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        object record = records.Values.Cast<object>().Single();
        object? request = record.GetType().GetField("Request", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(record);
        object? admitted = record.GetType().GetField("Receipt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(record);
        PlatformBackendEpoch epoch = s.Kernel.PlatformAuthority.BackendEpoch;
        SecureExecutionRequest exactRequest = (SecureExecutionRequest)request!;
        SecureExecutionReceipt? exactReceipt = (SecureExecutionReceipt?)admitted;
        List<ProviderTraceEventV1> observations = new List<ProviderTraceEventV1>();
        string Hash(string value) => Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(
            global::System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        // Qualification correlation only, never an admission digest or authenticated binding.
        string GenerationDigest()
        {
            string requestJson = global::System.Text.Json.JsonSerializer.Serialize(exactRequest);
            string receiptJson = global::System.Text.Json.JsonSerializer.Serialize(exactReceipt);
            string ownerJson = global::System.Text.Json.JsonSerializer.Serialize(s.Owner);
            int requestBytes = global::System.Text.Encoding.UTF8.GetByteCount(requestJson);
            int receiptBytes = global::System.Text.Encoding.UTF8.GetByteCount(receiptJson);
            int ownerBytes = global::System.Text.Encoding.UTF8.GetByteCount(ownerJson);
            return Hash(FormattableString.Invariant($"composition-close/3:{requestBytes}:{requestJson}:{receiptBytes}:{receiptJson}:{ownerBytes}:{ownerJson}:{s.Kernel.PlatformAuthority.BackendEpoch.Value}"));
        }
        void Observe(SemanticTraceEventKindV1 kind)
        {
            object? known = record.GetType().GetField("LastClosureReceipt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(record);
            SecureExecutionClosure? raw = known is null ? (SecureExecutionClosure?)null : (SecureExecutionClosure)known.GetType().GetProperty("Receipt")!.GetValue(known)!;
            string? state = record.GetType().GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(record)!.ToString();
            observations.Add(new(1, "composition-close-invocation-1", (ulong)observations.Count + 1,
                ProviderTraceDispositionV1.SecurityRelevant, kind, "test.composition.close.qualifier", GenerationDigest(),
                Hash(FormattableString.Invariant($"closure-observation/1:{(byte)kind}:{s.Provider.CloseCalls}:{state}:{raw.HasValue}:{raw?.ProviderClosed == true}:{raw?.ProviderEffectContained == true}:{raw?.Request.Binding.Id ?? 0}:{raw?.Request.Binding.Generation ?? 0}"))));
        }
        s.Provider.Contained = fault is 1 or 3;
        s.Provider.ClosureFault = fault == 5 ? 0 : fault == 6 ? 4 : fault == 7 ? 3 : -1;
        s.Provider.DuringClose = () =>
        {
            object gate = typeof(RuntimeKernel).GetField("_secureExecutionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
            Assert.False(Monitor.IsEntered(gate));
            Assert.Equal(KernelError.PlatformBindingDraining, s.Kernel.CloseSecureExecution(binding).Error);
            Observe(SemanticTraceEventKindV1.Submit);
            Observe(SemanticTraceEventKindV1.EffectPossible);
            if (fault is 2 or 3 or 4)
            {
                Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
                Observe(SemanticTraceEventKindV1.GenerationChanged);
            }
            if (fault == 4) throw new IOException("closure reply lost");
        };
        List<SemanticTraceEventV1> emitted = new List<SemanticTraceEventV1>();
        KernelResult close = s.Kernel.CloseSecureExecution(binding, new ClosureTraceSink(e =>
        {
            s.Provider.CheckLocks?.Invoke();
            foreach ((Type? type, object? target, string? field) in new[] {
                (typeof(RuntimeKernel), (object)s.Kernel, "_secureExecutionGate"),
                (typeof(RuntimeKernel), (object)s.Kernel, "_platformMemoryUseGate"),
                (typeof(PlatformAuthorityBridge), (object)s.Kernel.PlatformAuthority, "_secureDomainLifecycleGate") })
                Assert.False(Monitor.IsEntered(type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!));
            emitted.Add(e); return true;
        }));
        Assert.Equal(fault is 0 or 1, close.IsSuccess);
        Assert.Equal(close.IsSuccess ? "Closed" : "Quarantined",
            record.GetType().GetField("State", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(record)!.ToString());
        // Terminal close response is completion of this cleanup call, not resource release.
        Observe(close.IsSuccess ? SemanticTraceEventKindV1.RetireOrComplete : SemanticTraceEventKindV1.Quarantined);
        IReadOnlyList<SemanticTraceEventV1> trace = SemanticTraceProjectionV1.Project(observations);
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.True(SemanticTraceValidatorV1.Validate(emitted).IsValid);
        Assert.Equal(trace.Select(e => e.Kind), emitted.Select(e => e.Kind));
        Assert.Equal(trace.Select(e => e.GenerationVectorDigest), emitted.Select(e => e.GenerationVectorDigest));
        Assert.All(emitted, e => { Assert.False(e.AuthorizesExecution); Assert.False(e.AuthorizesEffect); Assert.False(e.AuthorizesPublication); });
        Assert.All(trace, e => { Assert.False(e.AuthorizesExecution); Assert.False(e.AuthorizesEffect); Assert.False(e.AuthorizesPublication); });
        foreach (SemanticTraceEventKindV1 forged in new[] { SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Released })
            Assert.False(SemanticTraceValidatorV1.Validate(trace.Concat([trace[^1] with { Sequence = (ulong)trace.Count + 1, Kind = forged }])).IsValid);
        SemanticRefinementDecisionV1 containment = SemanticRefinementEvaluatorV1.Evaluate(
            new SemanticRequirementV1<ContainmentClassV1>(1, SemanticRequirementStrengthV1.Mandatory, ContainmentClassV1.ExactOperationProviderGenerationClosure),
            new SemanticGuaranteeV1<ContainmentClassV1>(1, SemanticGuaranteeSupportV1.Unsupported, ContainmentClassV1.None),
            SemanticPartialOrdersV1.ContainmentRefines);
        Assert.Equal(SemanticRefinementStatusV1.MandatoryUnsupported, containment.Status);
        Assert.False(containment.AuthorizesEffect);
        Assert.Equal(1, s.Provider.CloseCalls);
        object? fact = record.GetType().GetField("LastClosureReceipt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(record);
        if (fault is 4 or 5) Assert.Null(fact);
        else
        {
            Assert.NotNull(fact);
            Assert.Equal(request, fact.GetType().GetProperty("AdmittedRequest")!.GetValue(fact));
            Assert.Equal(admitted, fact.GetType().GetProperty("AdmittedReceipt")!.GetValue(fact));
            Assert.Equal(epoch, fact.GetType().GetProperty("AdmissionEpoch")!.GetValue(fact));
            SecureExecutionClosure receipt = (SecureExecutionClosure)fact.GetType().GetProperty("Receipt")!.GetValue(fact)!;
            Assert.Equal(fault is 0 or 2 or 6, receipt.ProviderClosed);
            Assert.Equal(fault is 1 or 3, receipt.ProviderEffectContained);
            if (fault == 6) Assert.NotEqual(request, receipt.Request);
        }
        Assert.Equal(0, s.Provider.SecureCloses);
        Assert.Equal(0, s.Provider.ChildCloses);
        s.Provider.DuringClose = null;
        s.Provider.ClosureFault = -1;
        Assert.Equal(fault is 0 or 1, s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.Equal(fault is 0 or 1, s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)]
    public void SecureGuestClosureSpansLocalLifetimeCompletion(int fault, bool teardown)
    {
        (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) o = OverlayScenario(); Scenario s = o.S;
        SecureGuestRegionBinding overlay = s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).Value;
        IDictionary records = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_secureGuestRegions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        object record = records.Values.Cast<object>().Single();
        PropertyInfo state = record.GetType().GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic)!;
        IDictionary domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_secureDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.PlatformAuthority)!;
        object domain = domains.Values.Cast<object>().Single();
        PlatformBackendEpoch epoch = s.Kernel.PlatformAuthority.BackendEpoch;
        object[] gates = new[] {
            typeof(RuntimeKernel).GetField("_secureGuestRegionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!,
            typeof(RuntimeKernel).GetField("_platformMemoryUseGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!,
            typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.PlatformAuthority)! };
        int calls = 0;
        s.Kernel.BeforeSecureGuestRegionClosureLocalCommit = () =>
        {
            calls++;
            foreach (object? gate in gates) Assert.False(Monitor.IsEntered(gate));
            Assert.Equal("Closing", state.GetValue(record)!.ToString());
            Assert.Single(((global::System.Collections.IEnumerable)domain.GetType().GetProperty("RegionMutationsInFlight")!.GetValue(domain)!).Cast<object>());
            Assert.Equal(KernelError.PlatformBindingDraining, s.Kernel.CloseSecureGuestRegion(overlay).Error);
            if (fault == 1) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 2) throw new IOException("local overlay closure lost");
            if (fault == 3) state.SetValue(record, Enum.Parse(state.PropertyType, "Quarantined"));
        };
        KernelResult result = teardown ? s.Kernel.TerminateProcess(s.Owner) : s.Kernel.CloseSecureGuestRegion(overlay);
        Assert.Equal(fault == 0, result.IsSuccess);
        Assert.Equal(1, calls);
        Assert.Equal(fault == 0 ? "Closed" : "Quarantined", state.GetValue(record)!.ToString());
        Assert.Empty(((global::System.Collections.IEnumerable)domain.GetType().GetProperty("RegionMutationsInFlight")!.GetValue(domain)!).Cast<object>());
        AssertOverlayPendingBalanced(s);
        if (fault != 0)
        {
            IDictionary facts = (global::System.Collections.IDictionary)domain.GetType().GetProperty("RegionClosureReceipts")!.GetValue(domain)!;
            object fact = facts.Values.Cast<object>().Single();
            Assert.Equal(epoch, fact.GetType().GetProperty("AdmissionEpoch")!.GetValue(fact));
            PlatformSecureRegionClosureReceipt receipt = (PlatformSecureRegionClosureReceipt)fact.GetType().GetProperty("Receipt")!.GetValue(fact)!;
            Assert.True(receipt.Closed);
            Assert.Equal(receipt.Binding, fact.GetType().GetProperty("AdmittedBinding")!.GetValue(fact));
            Assert.False(s.Kernel.CloseSecureGuestRegion(overlay).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        }
        Assert.Equal(1, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
    }

    private sealed class ExactReleaseProbe(ICxlType2AcceleratorProvider inner) : ICxlType2AcceleratorProvider
    {
        public List<CxlAcceleratorSubmission> SuccessfulReleases { get; } = [];
        public PlatformAuthorityResult<CxlAcceleratorSubmission> Submit(CxlAcceleratorRequest request) => inner.Submit(request);
        public PlatformAuthorityResult<CxlAcceleratorCompletion> ObserveCompletion(CxlAcceleratorSubmission submission) => inner.ObserveCompletion(submission);
        public PlatformAuthorityResult<CxlAcceleratorVisibility> AcquireVisibility(CxlAcceleratorSubmission submission, ExternalVisibilityRequirement requirement) => inner.AcquireVisibility(submission, requirement);
        public PlatformAuthorityResult Cancel(CxlAcceleratorSubmission submission) => inner.Cancel(submission);
        public PlatformAuthorityResult Release(CxlAcceleratorSubmission submission)
        {
            PlatformAuthorityResult result = inner.Release(submission);
            if (result.IsSuccess) SuccessfulReleases.Add(submission);
            return result;
        }
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    public void Type2LateDiscoveryLossPreservesPublicationPinAfterExactAcceleratorClosure(int fault, bool releaseFails)
    {
        DiscoveryQueryHook? discovery = null;
        CxlType2ModelAccelerator model = new CxlType2ModelAccelerator { ReleaseFails = releaseFails };
        ExactReleaseProbe releases = new ExactReleaseProbe(model);
        ComposedWorkload c = CreateComposedWorkload(_ => releases, discoveryFactory: provider => discovery = new(provider));
        int actions = 0;
        KernelResult<ExternalOperationSnapshot> result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () =>
        {
            actions++;
            c.Input.Span.CopyTo(c.Output.Span);
            discovery!.BeforeQuery = () => throw fault switch
            {
                0 => new TimeoutException("late discovery query timed out"),
                1 => new OperationCanceledException("late discovery query cancelled"),
                _ => new IOException("late discovery query response lost")
            };
        });
        Assert.False(result.IsSuccess);
        Assert.Equal(1, actions);
        Assert.Empty(releases.SuccessfulReleases);
        ExternalOperationSnapshot operation = c.Base.Kernel.ExternalOperations.Query(c.Execution.Operation).Value!;
        Assert.Contains(operation.Transitions, x => x.Event == "PublicationEffectAmbiguous");
        Assert.DoesNotContain(operation.Transitions, x => x.Event == "Published");
        AssertType2OwnerTrace(c, operation, published: false);
        discovery!.BeforeQuery = null;
        RegionOwner owner = new RegionOwner(c.Subject.DomainId, c.Base.Owner.Generation);
        if (releaseFails)
        {
            Assert.False(((ICxlTeardownParticipant)c.Service).CloseForProcess(c.Base.Owner, owner).IsSuccess);
            Assert.Empty(releases.SuccessfulReleases);
        }
        model.ReleaseFails = false;
        Assert.False(((ICxlTeardownParticipant)c.Service).CloseForProcess(c.Base.Owner, owner).IsSuccess);
        if (releaseFails) Assert.Empty(releases.SuccessfulReleases);
        else Assert.Equal(c.Execution.Submission, Assert.Single(releases.SuccessfulReleases));
        // Known accelerator closure does not settle the independent possible CPU publication.
        Assert.False(((ICxlTeardownParticipant)c.Service).CloseForProcess(c.Base.Owner, owner).IsSuccess);
        if (releaseFails) Assert.Empty(releases.SuccessfulReleases);
        else Assert.Single(releases.SuccessfulReleases);
        Assert.NotEqual(ExternalOperationState.Released, c.Base.Kernel.ExternalOperations.Query(c.Execution.Operation).Value!.State);
        Assert.False(c.Base.Kernel.CloseGuestRegionMapping(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.MemoryCapability, c.OutputGuest.Mapping).IsSuccess);
        Assert.False(c.Base.Kernel.TerminateProcess(c.Base.Owner).IsSuccess);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Type2InitialSourceQueryLossRetainsExactReceiptUntilRealClosureRetry(int fault)
    {
        DiscoveryQueryHook? discovery = null;
        ComposedWorkload c = CreateComposedWorkload(discoveryFactory: model => discovery = new(model));
        discovery!.BeforeQuery = () => throw fault switch
        {
            0 => new TimeoutException("initial discovery query timed out"),
            1 => new OperationCanceledException("initial discovery query cancelled"),
            _ => new IOException("initial discovery query reply lost")
        };
        int actions = 0;
        KernelResult<ExternalOperationSnapshot> result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () => actions++);
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.Equal(0, actions);
        ExternalOperationSnapshot operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.Contains(operation.Transitions, x => x.Event == "ProviderLost");
        Assert.DoesNotContain(operation.Transitions, x => x.Event is "Published" or "PublicationEffectAmbiguous");
        Assert.NotEqual(ExternalOperationState.Released, operation.State);
        Assert.False(c.Base.Kernel.CloseGuestRegionMapping(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.MemoryCapability, c.OutputGuest.Mapping).IsSuccess);
        Assert.True(c.Accelerator.ObserveCompletion(c.Execution.Submission).IsSuccess);
        discovery.BeforeQuery = null;
        RegionOwner owner = new RegionOwner(c.Subject.DomainId, c.Base.Owner.Generation);
        Assert.True(((ICxlTeardownParticipant)c.Service).CloseForProcess(c.Base.Owner, owner).IsSuccess);
        Assert.Equal(ExternalOperationState.Released, c.Base.Kernel.ExternalOperations.Query(c.Execution.Operation).Value!.State);
        CloseComposedWorkload(c, c.Fabric);
    }
    private sealed class DiscoveryQueryHook(ICxlDiscoveryProvider inner) : ICxlDiscoveryProvider
    {
        public Action? BeforeQuery;
        public PlatformAuthorityResult<CxlEndpointSnapshot> QueryEndpoint(CxlEndpointId endpoint)
        { BeforeQuery?.Invoke(); return inner.QueryEndpoint(endpoint); }
    }
    private static void AssertType2OwnerTrace(ComposedWorkload c, ExternalOperationSnapshot operation, bool published)
    {
        OperationDependencySnapshot d = c.Execution.Dependencies;
        // Evidence correlation of the exact tuple only, not a reusable admission token.
        string tuple = FormattableString.Invariant($"type2-owner-trace/1:{c.Execution.Operation.OperationId.Value}:{c.Execution.Operation.Generation.Value}:{d.PlatformGeneration}:{d.DeviceGeneration}:{d.BindingGeneration}:{d.BackingGeneration}");
        string digest = Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(tuple))).ToLowerInvariant();
        IReadOnlyList<SemanticTraceEventV1> trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(operation, digest);
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.All(trace, x => { Assert.False(x.AuthorizesExecution); Assert.Equal(digest, x.GenerationVectorDigest); });
        Assert.Equal(published, trace.Any(x => x.Kind == SemanticTraceEventKindV1.Published));
        if (!published)
        {
            Assert.Equal(operation.Transitions.Any(x => x.Event == "PublicationEffectAmbiguous"),
                trace.Any(x => x.Kind == SemanticTraceEventKindV1.Quarantined));
            Assert.DoesNotContain(trace, x => x.Kind is SemanticTraceEventKindV1.Released or SemanticTraceEventKindV1.EffectClosedWithoutPublication);
            SemanticTraceEventV1[] forgedRelease = trace.Concat([trace[^1] with { Sequence = trace[^1].Sequence + 1, Kind = SemanticTraceEventKindV1.Released }]).ToArray();
            Assert.False(SemanticTraceValidatorV1.Validate(forgedRelease).IsValid);
        }
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void Type2ActualPublicationPlacementDriftSeparatesPreActionRefusalAndLateAmbiguity(bool output, bool late)
    {
        ComposedWorkload c = CreateComposedWorkload();
        void Drift()
        {
            Assert.True(c.Model.HotRemove(c.Endpoint.EndpointId).IsSuccess);
            CxlMemoryPlacementSnapshot placement = output ? c.OutputPlacement : c.InputPlacement;
            Assert.False(c.Memory.Refresh(placement.PlacementId).IsSuccess);
            Assert.Equal(CxlMemoryPlacementState.MigrationRequired, c.Memory.Query(placement.PlacementId).Value!.State);
        }
        if (!late) c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = Drift;
        int actions = 0;
        KernelResult<ExternalOperationSnapshot> result;
        try
        {
            result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () =>
            {
                actions++;
                c.Input.Span.CopyTo(c.Output.Span);
                if (late) Drift();
            });
        }
        finally { c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = null; }
        Assert.False(result.IsSuccess);
        Assert.Equal(late ? 1 : 0, actions);
        ExternalOperationSnapshot operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.DoesNotContain(operation.Transitions, x => x.Event == "Published");
        Assert.Equal(late, operation.Transitions.Any(x => x.Event == "PublicationEffectAmbiguous"));
        AssertType2OwnerTrace(c, operation, published: false);
        // Hot remove supplies no exact provider closure; even pre-action refusal keeps the submitted effect pinned.
        Assert.NotEqual(ExternalOperationState.Released, operation.State);
        Assert.False(c.Base.Kernel.CloseGuestRegionMapping(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.MemoryCapability, c.OutputGuest.Mapping).IsSuccess);
        Assert.False(c.Base.Kernel.TerminateProcess(c.Base.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Type2PublicationPendingRejectsOverlappingDeliveryAndPinsExactGuestMapping(bool stop)
    {
        ComposedWorkload c = CreateComposedWorkload();
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        int actions = 0;
        Task<KernelResult<ExternalOperationSnapshot>> pending = Task.Run(() => c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () =>
        {
            Interlocked.Increment(ref actions);
            c.Input.Span.CopyTo(c.Output.Span);
            entered.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("publication test pause");
        }));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            KernelResult<ExternalOperationSnapshot> duplicate = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () => Interlocked.Increment(ref actions));
            Assert.False(duplicate.IsSuccess);
            Assert.Equal(KernelError.PlatformBindingDraining, duplicate.Error);
            Assert.Equal(1, Volatile.Read(ref actions));
            Assert.False(c.Base.Kernel.CloseGuestRegionMapping(c.Base.Owner, c.Base.Virtual.Domain,
                c.Base.Virtual.MemoryCapability, c.OutputGuest.Mapping).IsSuccess);
            Assert.False(c.Manager.BeginReconfiguration(c.Fabric).IsSuccess);
            if (stop) Assert.False(c.Base.Kernel.TerminateProcess(c.Base.Owner).IsSuccess);
        }
        finally { resume.Set(); }
        KernelResult<ExternalOperationSnapshot> result = await pending;
        Assert.Equal(!stop, result.IsSuccess);
        Assert.Equal(1, actions);
        ExternalOperationSnapshot operation = c.Base.Kernel.ExternalOperations.Query(c.Execution.Operation).Value!;
        Assert.Equal(stop, operation.Transitions.Any(x => x.Event == "PublicationEffectAmbiguous"));
        AssertType2OwnerTrace(c, operation, published: !stop);
        if (stop)
        {
            Assert.DoesNotContain(operation.Transitions, x => x.Event == "Published");
            Assert.NotEqual(ExternalOperationState.Released, operation.State);
            Assert.False(c.Base.Kernel.TerminateProcess(c.Base.Owner).IsSuccess);
        }
        else
        {
            Assert.Equal(ExternalOperationState.Released, operation.State);
            CloseComposedWorkload(c, c.Fabric);
        }
    }
    private sealed class BackingLockProbe(Action check) : ISecureGuestBackingAuthority
    {
        public KernelResult RevalidateForSecureGuest(RegionOwner owner, RegionHandle region) => KernelResult.Ok();
        public KernelResult<Func<Func<KernelResult>, KernelResult>> PrepareForSecureGuest(
            RegionOwner owner, RegionHandle region, out RegionBackingLeaseHandle? backing)
        {
            backing = null; // Observes locking only; contributes no backing receipt or permission.
            return KernelResult<Func<Func<KernelResult>, KernelResult>>.Ok(commit => { check(); return commit(); });
        }
    }

    [Fact]
    public void Type2MultiOperandBackingOwnersCommitBeforeAnyRegionGate()
    {
        ComposedWorkload c = CreateComposedWorkload();
        int calls = 0;
        void Check()
        {
            calls++;
            object records = typeof(RegionAuthority).GetField("_regions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.Base.Kernel.Regions)!;
            foreach (object record in ((global::System.Collections.IEnumerable)records.GetType().GetProperty("Values")!.GetValue(records)!).Cast<object>())
                Assert.False(Monitor.IsEntered(record.GetType().GetProperty("Gate")!.GetValue(record)!));
        }
        c.Base.Kernel.RegisterSecureGuestBackingAuthority(new BackingLockProbe(Check));
        c.Base.Kernel.RegisterSecureGuestBackingAuthority(new BackingLockProbe(Check));
        KernelResult<Func<Func<KernelResult>, KernelResult>> prepared = c.Base.Kernel.PrepareVirtualComputePublication(c.Base.Owner, c.Context, c.Execution.Plan);
        Assert.True(prepared.IsSuccess, prepared.Message);
        int mutations = 0;
        Assert.True(prepared.Value!(() => { mutations++; return KernelResult.Ok(); }).IsSuccess);
        Assert.Equal(4, calls);
        Assert.Equal(1, mutations);
        int actions = 0;
        Assert.True(c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () => actions++).IsSuccess);
        Assert.Equal(1, actions);
        Assert.True(calls > 4);
        CloseComposedWorkload(c, c.Fabric);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Type2PreparedPlacementDriftRefusesWithoutPartialOwnerMutation(bool output)
    {
        ComposedWorkload c = CreateComposedWorkload();
        KernelResult<Func<Func<KernelResult>, KernelResult>> prepared = c.Base.Kernel.PrepareVirtualComputePublication(c.Base.Owner, c.Context, c.Execution.Plan);
        Assert.True(prepared.IsSuccess, prepared.Message);
        Assert.True(c.Model.HotRemove(c.Endpoint.EndpointId).IsSuccess);
        CxlMemoryPlacementSnapshot placement = output ? c.OutputPlacement : c.InputPlacement;
        Assert.False(c.Memory.Refresh(placement.PlacementId).IsSuccess);
        Assert.Equal(CxlMemoryPlacementState.MigrationRequired, c.Memory.Query(placement.PlacementId).Value!.State);
        int mutations = 0;
        Assert.False(prepared.Value!(() => { mutations++; return KernelResult.Ok(); }).IsSuccess);
        Assert.Equal(0, mutations);
    }
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void Type2LateBackingConsequencePreservesPublicationAmbiguityEvenAfterProviderClosure(bool output, bool closureFails)
    {
        ComposedWorkload c = CreateComposedWorkload();
        CxlType2ModelAccelerator accelerator = Assert.IsType<CxlType2ModelAccelerator>(c.Accelerator);
        accelerator.ReleaseFails = closureFails;
        int actions = 0;
        KernelResult<ExternalOperationSnapshot> result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () =>
        {
            actions++;
            c.Input.Span.CopyTo(c.Output.Span); // Actual modeled local publication before the fault.
            ComputeRegionOperand operand = output ? c.Execution.Plan.Intent.Output : c.Execution.Plan.Intent.Input;
            Assert.True(c.Base.Kernel.Regions.QuarantineSubrange(operand.Region, c.Owner,
                new ProviderHealthEvidenceV1(1, new("late-type2-model", "bank", 1, 1), 1,
                    ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, operand.Range)).IsSuccess);
        });
        Assert.False(result.IsSuccess);
        Assert.Equal(1, actions);
        ExternalOperationSnapshot operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.Contains(operation.Transitions, x => x.Event == "PublicationEffectAmbiguous");
        Assert.DoesNotContain(operation.Transitions, x => x.Event == "Published");
        Assert.NotEqual(ExternalOperationState.Released, operation.State);
        Assert.Single(c.Base.Kernel.Regions.SnapshotDamage());
        Assert.False(c.Base.Kernel.CloseGuestRegionMapping(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.MemoryCapability, c.OutputGuest.Mapping).IsSuccess);
        accelerator.ReleaseFails = false;
        // Exact accelerator release cannot close an independently ambiguous CPU publication.
        Assert.False(c.Base.Kernel.TerminateProcess(c.Base.Owner).IsSuccess);
        Assert.NotEqual(ExternalOperationState.Released,
            c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!.State);
        Assert.Single(c.Base.Kernel.Regions.SnapshotDamage());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PreparedType2BackingCommitRefusesFreshRegionDamageWithoutOwnerMutation(bool output)
    {
        ComposedWorkload c = CreateComposedWorkload();
        KernelResult<Func<Func<KernelResult>, KernelResult>> prepared = c.Base.Kernel.PrepareVirtualComputePublication(c.Base.Owner, c.Context, c.Execution.Plan);
        Assert.True(prepared.IsSuccess, prepared.Message);
        ComputeRegionOperand operand = output ? c.Execution.Plan.Intent.Output : c.Execution.Plan.Intent.Input;
        Assert.True(c.Base.Kernel.Regions.QuarantineSubrange(operand.Region, c.Owner,
            new ProviderHealthEvidenceV1(1, new("type2-backing-model", "bank", 1, 1), 1,
                ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, operand.Range)).IsSuccess);
        int calls = 0;
        Assert.False(prepared.Value!(() => { calls++; return KernelResult.Ok(); }).IsSuccess);
        Assert.Equal(0, calls);
        Assert.Single(c.Base.Kernel.Regions.SnapshotDamage());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Type2StagedAdmissionRefusesBackingConsequenceBeforeAction(bool output)
    {
        ComposedWorkload c = CreateComposedWorkload();
        int actions = 0;
        c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = () =>
        {
            ComputeRegionOperand operand = output ? c.Execution.Plan.Intent.Output : c.Execution.Plan.Intent.Input;
            Assert.True(c.Base.Kernel.Regions.QuarantineSubrange(operand.Region, c.Owner,
                new ProviderHealthEvidenceV1(1, new("type2-backing-model", "bank", 1, 1), 1,
                    ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, operand.Range)).IsSuccess);
        };
        KernelResult<ExternalOperationSnapshot> result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () => actions++);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, actions);
        ExternalOperationSnapshot operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.DoesNotContain(operation.Transitions, x => x.Event == "PublicationEffectAmbiguous");
        Assert.Single(c.Base.Kernel.Regions.SnapshotDamage());
    }

    private static (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) OverlayScenario(bool unknownBacking = false, Action<Scenario>? beforeAllocation = null)
    {
        Scenario s = Create();
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        beforeAllocation?.Invoke(s);
        OwnedBuffer<byte> buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        if (unknownBacking) Assert.True(s.Kernel.Regions.ReserveBacking(buffer.Handle, new(process.DomainId, s.Owner.Generation)).IsSuccess);
        CapabilityId region = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        KernelResult<GuestRegionMapping> mapping = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, region,
            buffer.Handle, new(0, 64), GuestMemoryAccess.Read);
        Assert.True(mapping.IsSuccess, mapping.Message);
        return (s, mapping.Value!, region, Bind(s).Value);
    }

    private sealed class CheckedMemoryProvider(ICxlMemoryProvider inner, Action check) : ICxlMemoryProvider
    {
        public Action? BeforeQuery;
        public PlatformAuthorityResult<CxlMemoryCapacitySnapshot> QueryCapacity(CxlEndpointId id) { check(); return inner.QueryCapacity(id); }
        public PlatformAuthorityResult<CxlMemoryBinding> BindMemory(CxlFabricBinding fabric, RegionBackingLeaseDescriptor backing) { check(); return inner.BindMemory(fabric, backing); }
        public PlatformAuthorityResult<CxlMemoryBinding> QueryMemory(CxlMemoryBindingId id) { check(); BeforeQuery?.Invoke(); return inner.QueryMemory(id); }
        public PlatformAuthorityResult ReleaseMemory(CxlMemoryBinding binding) { check(); return inner.ReleaseMemory(binding); }
    }

    private static (Scenario S, CxlType3ModelProvider Model, CxlType3MemoryAuthority Memory, CheckedMemoryProvider Queries,
        CxlMemoryPlacementSnapshot Placement, GuestRegionMapping Mapping, SecureExecutionBinding Execution, RegionOwner Owner,
        CapabilityId DeviceCapability) BackedOverlayScenario()
    {
        Scenario s = Create(); SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, s.Owner.Generation);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(process.DomainId, s.Owner);
        CapabilityId cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.Device, "device:secure-virtual-type2",
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!.CapabilityId;
        PlatformDeviceLease device = s.Kernel.BindPlatformDevice(s.Owner, s.Parent, cap,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        CxlType3ModelProvider model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("secure-virtual-type2"), device.Device, 4096).IsSuccess);
        CxlEndpointSnapshot endpoint = model.QueryEndpoint(new("secure-virtual-type2")).Value!;
        CxlType3MemoryAuthority? memory = null;
        Action existingCheck = s.Provider.CheckLocks!;
        s.Provider.CheckLocks = () =>
        {
            existingCheck();
            if (memory is not null) Assert.False(Monitor.IsEntered(typeof(CxlType3MemoryAuthority).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(memory)!));
            object records = typeof(RegionAuthority).GetField("_regions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.Regions)!;
            foreach (object record in ((global::System.Collections.IEnumerable)records.GetType().GetProperty("Values")!.GetValue(records)!).Cast<object>())
                Assert.False(Monitor.IsEntered(record.GetType().GetProperty("Gate")!.GetValue(record)!));
        };
        CheckedMemoryProvider queries = new CheckedMemoryProvider(model, () => s.Provider.CheckLocks!());
        CxlAuthorityBridge bridge = new CxlAuthorityBridge(s.Kernel, model, model, model, queries, new UnsupportedCoherentProvider(), new CxlSecurityModelProvider());
        memory = new CxlType3MemoryAuthority(s.Kernel, bridge);
        OwnedBuffer<byte> buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        KernelResult<CxlMemoryPlacementSnapshot> placement = memory.Place(owner, buffer.Handle, subject, device, endpoint, CxlMemoryPersistence.Volatile);
        Assert.True(placement.IsSuccess, placement.Message);
        CapabilityId region = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        KernelResult<GuestRegionMapping> mapping = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, region,
            buffer.Handle, new(0, 64), GuestMemoryAccess.Read);
        Assert.True(mapping.IsSuccess, mapping.Message);
        SecureExecutionBinding execution = Bind(s).Value;
        s.Provider.DuringIncarnation = () => s.Provider.CheckLocks!();
        return (s, model, memory, queries, placement.Value!, mapping.Value!, execution, owner, cap);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SecureGuestType3BackingDeviceSourceRevokeRefusesAtBothBoundaries(bool late)
    {
        (Scenario S, CxlType3ModelProvider Model, CxlType3MemoryAuthority Memory, CheckedMemoryProvider Queries, CxlMemoryPlacementSnapshot Placement, GuestRegionMapping Mapping, SecureExecutionBinding Execution, RegionOwner Owner, CapabilityId DeviceCapability) o = BackedOverlayScenario(); Scenario s = o.S;
        void Revoke() { s.Provider.CheckLocks!(); Assert.True(s.Kernel.CapabilityAuthority.Revoke(o.DeviceCapability).IsSuccess); }
        if (late) s.Kernel.BeforeSecureGuestRegionPublication = Revoke;
        else s.Kernel.BeforeSecureGuestRegionAdmission = Revoke;
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
        Assert.Equal(late ? 1 : 0, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
        AssertOverlayPendingBalanced(s);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void SecureGuestFreshType3OwnerRefusesHealthOrMigrationAfterQueries(bool migration, bool late)
    {
        (Scenario S, CxlType3ModelProvider Model, CxlType3MemoryAuthority Memory, CheckedMemoryProvider Queries, CxlMemoryPlacementSnapshot Placement, GuestRegionMapping Mapping, SecureExecutionBinding Execution, RegionOwner Owner, CapabilityId DeviceCapability) o = BackedOverlayScenario(); Scenario s = o.S;
        void Fault()
        {
            s.Provider.CheckLocks!();
            if (migration)
            {
                Assert.True(o.Model.HotRemove(o.Placement.EndpointId).IsSuccess);
                Assert.False(o.Memory.Refresh(o.Placement.PlacementId).IsSuccess);
            }
            else
            {
                Assert.True(o.Model.InjectHealthFault(o.Placement.MemoryBindingId, ProviderHealthStateV1.Degraded,
                    ProviderFaultClassV1.Omission, new(0, 8)).IsSuccess);
                Assert.True(o.Memory.ObserveAndQuarantine(o.Placement.PlacementId, o.Owner, o.Model).IsSuccess);
            }
        }
        if (late) s.Kernel.BeforeSecureGuestRegionPublication = Fault;
        else s.Kernel.BeforeSecureGuestRegionAdmission = Fault;
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
        Assert.Equal(late ? 1 : 0, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
        AssertOverlayPendingBalanced(s);
        if (late) _ = RetainedOverlay(s);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SecureGuestFreshRegionConsequenceRefusesOrdinaryBackingAtBothBoundaries(bool late)
    {
        (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) o = OverlayScenario(); Scenario s = o.S;
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        void Damage()
        {
            s.Provider.CheckLocks!();
            Assert.True(s.Kernel.Regions.QuarantineSubrange(o.Mapping.Region, new(process.DomainId, s.Owner.Generation),
                new ProviderHealthEvidenceV1(1, new("overlay-model", "bank", 1, 1), 1,
                    ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(0, 8))).IsSuccess);
        }
        if (late) s.Kernel.BeforeSecureGuestRegionPublication = Damage;
        else s.Kernel.BeforeSecureGuestRegionAdmission = Damage;
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
        Assert.Single(s.Kernel.Regions.SnapshotDamage()); AssertOverlayPendingBalanced(s);
    }

    [Fact]
    public void SecureGuestUnknownBackingOwnerCannotBeSubstitutedByLeaseIdentity()
    {
        (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) o = OverlayScenario(unknownBacking: true); Scenario s = o.S;
        Assert.Equal(KernelError.PlatformDenied, s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).Error);
        Assert.Equal(0, s.Provider.SecureRegionBindCalls); AssertOverlayPendingBalanced(s);
    }

    [Fact]
    public void SecureGuestInitialBackingQueryExceptionMakesNoBindAndFreshRetryIsAllowed()
    {
        (Scenario S, CxlType3ModelProvider Model, CxlType3MemoryAuthority Memory, CheckedMemoryProvider Queries, CxlMemoryPlacementSnapshot Placement, GuestRegionMapping Mapping, SecureExecutionBinding Execution, RegionOwner Owner, CapabilityId DeviceCapability) o = BackedOverlayScenario(); Scenario s = o.S;
        o.Queries.BeforeQuery = () => throw new IOException("backing query transport loss");
        Assert.Equal(KernelError.PlatformFaulted, s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).Error);
        Assert.Equal(0, s.Provider.SecureRegionBindCalls); Assert.Empty(s.Provider.OverlayCloseOrder);
        AssertOverlayPendingBalanced(s);
        o.Queries.BeforeQuery = null;
        KernelResult<SecureGuestRegionBinding> overlay = s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private);
        Assert.True(overlay.IsSuccess, overlay.Message);
        Assert.True(s.Kernel.CloseSecureGuestRegion(overlay.Value).IsSuccess); AssertOverlayPendingBalanced(s);
    }

    [Theory]
    [InlineData(0, false, false)] [InlineData(0, true, false)]
    [InlineData(3, true, false)] [InlineData(3, true, true)]
    [InlineData(1, false, false)] [InlineData(1, true, false)]
    [InlineData(2, false, false)] [InlineData(2, true, false)]
    [InlineData(0, false, true)] [InlineData(0, true, true)]
    [InlineData(1, false, true)] [InlineData(1, true, true)]
    [InlineData(2, false, true)] [InlineData(2, true, true)]
    public void SecureGuestBindCompletesOriginalPendingOwnersAfterRouteLoss(int route, bool foreign, bool throws)
    {
        (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) o = OverlayScenario(); Scenario s = o.S;
        PlatformAuthorityBridge bridge = s.Kernel.PlatformAuthority;
        VirtualDomainAuthority domains = (VirtualDomainAuthority)typeof(RuntimeKernel).GetField("_virtualDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        (PlatformGuestMapping Guest, PlatformRegionMapping Parent) platform = domains.Resolve(s.Owner, s.Virtual.Domain).Value!.PlatformMappings[o.Mapping.Mapping.MappingId];
        IDictionary guests = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_guestBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        IDictionary children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_childBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bridge)!;
        object guest = guests[platform.Guest.MappingId]!;
        object child = children[platform.Guest.Child.BindingId]!;
        int foreignPending = route == 3 ? 1 : 7;
        int Pending(object record, string name) => (int)record.GetType().GetProperty(name)!.GetValue(record)!;
        object Clone(object record, string name)
        {
            object clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(record, null)!;
            record.GetType().GetProperty(name)!.SetValue(clone, foreignPending);
            return clone;
        }
        object? foreignGuest = null, foreignChild = null;
        s.Provider.DuringSecureRegionBind = () =>
        {
            s.Provider.CheckLocks!();
            s.Provider.SecureRegionClosureFault = true; // Original cleanup stays unproven independently of pending completion.
            Assert.Equal(1, Pending(guest, "PendingSecureRegionEffects"));
            Assert.Equal(1, Pending(child, "PendingChildEffects"));
            if (route is 0 or 2 or 3)
            {
                if (foreign) guests[platform.Guest.MappingId] = foreignGuest = Clone(guest, "PendingSecureRegionEffects");
                else guests.Remove(platform.Guest.MappingId);
            }
            if (route is 1 or 2)
            {
                if (foreign) children[platform.Guest.Child.BindingId] = foreignChild = Clone(child, "PendingChildEffects");
                else children.Remove(platform.Guest.Child.BindingId);
            }
            if (throws) throw new IOException("Original secure guest route lost after possible effect");
        };
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(1, s.Provider.SecureRegionBindCalls);
        Assert.Equal(0, Pending(guest, "PendingSecureRegionEffects"));
        Assert.Equal(0, Pending(child, "PendingChildEffects"));
        if (foreignGuest is not null) Assert.Equal(foreignPending, Pending(foreignGuest, "PendingSecureRegionEffects"));
        if (foreignChild is not null) Assert.Equal(foreignPending, Pending(foreignChild, "PendingChildEffects"));
        _ = RetainedOverlay(s);
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(1, s.Provider.SecureRegionBindCalls);
        // Test restoration establishes neither original containment nor permission.
        guests[platform.Guest.MappingId] = guest; children[platform.Guest.Child.BindingId] = child;
        AssertOverlayPendingBalanced(s);
        Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, o.Mapping.Mapping).IsSuccess);
    }

    private static void AssertOverlayPendingBalanced(Scenario s)
    {
        foreach ((string? field, string? property) in new[] { ("_guestBindings", "PendingSecureRegionEffects"), ("_childBindings", "PendingChildEffects") })
        {
            IDictionary records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.PlatformAuthority)!;
            foreach (object record in records.Values.Cast<object>())
                Assert.Equal(0, (int)record.GetType().GetProperty(property)!.GetValue(record)!);
        }
    }

    private static SecureGuestRegionBinding RetainedOverlay(Scenario s)
    {
        IDictionary records = (global::System.Collections.IDictionary)typeof(RuntimeKernel)
            .GetField("_secureGuestRegions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        object record = Assert.Single(records.Values.Cast<object>());
        Assert.NotEqual("Active", record.GetType().GetProperty("State", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(record)!.ToString());
        return (SecureGuestRegionBinding)record.GetType().GetProperty("Binding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(record)!;
    }

    [Theory]
    [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)] [InlineData(3, false)] [InlineData(3, true)]
    public void SecureGuestOverlayRequiresFreshConfigureAndMappingSources(int kind, bool late)
    {
        (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) o = OverlayScenario(); Scenario s = o.S;
        CapabilityId source = kind switch { 0 => s.Virtual.ConfigureCapability, 1 => s.Secure.ConfigureCapability, 2 => s.Virtual.MemoryCapability, _ => o.Region };
        void Revoke() { s.Provider.CheckLocks!(); Assert.True(s.Kernel.CapabilityAuthority.Revoke(source).IsSuccess); }
        if (late) s.Kernel.BeforeSecureGuestRegionPublication = Revoke;
        else s.Kernel.BeforeSecureGuestRegionAdmission = Revoke;
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
        Assert.Equal(late ? 1 : 0, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
        AssertOverlayPendingBalanced(s);
        if (late) Assert.True(s.Kernel.CloseSecureGuestRegion(RetainedOverlay(s)).IsSuccess);
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
    }

    [Fact]
    public void SecureGuestLateRefusalPinsKnownBindUntilOriginalUnknownClosureCanBeResolved()
    {
        (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) o = OverlayScenario(); Scenario s = o.S;
        s.Kernel.BeforeSecureGuestRegionPublication = () =>
        {
            Assert.True(s.Kernel.CapabilityAuthority.Revoke(o.Region).IsSuccess);
            s.Provider.SecureRegionClosureFault = true;
        };
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        SecureGuestRegionBinding retained = RetainedOverlay(s);
        AssertOverlayPendingBalanced(s);
        Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, o.Mapping.Mapping).IsSuccess);
        Assert.Equal(0, s.Provider.CloseCalls);
        s.Provider.SecureRegionClosureFault = false;
        Assert.False(s.Kernel.CloseSecureGuestRegion(retained).IsSuccess);
        Assert.False(s.Kernel.CloseSecureExecution(o.Execution).IsSuccess);
        Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, o.Mapping.Mapping).IsSuccess);
        Assert.Equal(1, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
        AssertOverlayPendingBalanced(s);
    }

    [Fact]
    public void SecureGuestCallbackPinsMappingAndCompositionAndRefusesOverlap()
    {
        (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) o = OverlayScenario(); Scenario s = o.S;
        VirtualDomainAuthority domains = (VirtualDomainAuthority)typeof(RuntimeKernel).GetField("_virtualDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        (PlatformGuestMapping Guest, PlatformRegionMapping Parent) platform = domains.Resolve(s.Owner, s.Virtual.Domain).Value!.PlatformMappings[o.Mapping.Mapping.MappingId];
        s.Provider.DuringSecureRegionBind = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.PlatformAuthority.UnmapChildGuestRegion(platform.Guest).Error);
            Assert.Equal(KernelError.PlatformBindingDraining, s.Kernel.CloseSecureExecution(o.Execution).Error);
            Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        };
        KernelResult<SecureGuestRegionBinding> overlay = s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private);
        Assert.True(overlay.IsSuccess, overlay.Message);
        Assert.Equal(1, s.Provider.SecureRegionBindCalls);
        AssertOverlayPendingBalanced(s);
        s.Provider.DuringSecureRegionBind = null;
        Assert.True(s.Kernel.CloseSecureGuestRegion(overlay.Value).IsSuccess);
        Assert.True(s.Kernel.CloseSecureExecution(o.Execution).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SecureGuestStopRefusesInitialOrLateOwnerPublication(bool late)
    {
        (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) o = OverlayScenario(); Scenario s = o.S;
        void Stop() { s.Provider.CheckLocks!(); Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess); }
        if (late) s.Kernel.BeforeSecureGuestRegionPublication = Stop;
        else s.Kernel.BeforeSecureGuestRegionAdmission = Stop;
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
        AssertOverlayPendingBalanced(s);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SecureGuestExceptionNeverPublishesOrInventsClosure(bool late)
    {
        (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) o = OverlayScenario(); Scenario s = o.S;
        if (late) s.Kernel.BeforeSecureGuestRegionPublication = () => throw new IOException("late overlay publication loss");
        else s.Provider.DuringSecureRegionBind = () => throw new IOException("bind outcome lost");
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(1, s.Provider.SecureRegionBindCalls);
        Assert.Equal(late ? 1 : 0, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
        _ = RetainedOverlay(s); AssertOverlayPendingBalanced(s);
        if (!late) Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, o.Mapping.Mapping).IsSuccess);
    }

    [Fact]
    public void SecureGuestIdentityExhaustionRefusesBeforeProviderAndBalancesAdmission()
    {
        (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) o = OverlayScenario(); Scenario s = o.S;
        typeof(RuntimeKernel).GetField("_nextSecureGuestRegionId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(s.Kernel, ulong.MaxValue);
        Assert.Equal(KernelError.CapacityExhausted, s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).Error);
        Assert.Equal(0, s.Provider.SecureRegionBindCalls); AssertOverlayPendingBalanced(s);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void SecureGuestFreshCommitRejectsMappingSourceDriftGenerationAndRevokedAba(int fault)
    {
        (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) o = OverlayScenario(); Scenario s = o.S;
        VirtualDomainAuthority domains = (VirtualDomainAuthority)typeof(RuntimeKernel).GetField("_virtualDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        VirtualDomainAuthority.Record local = domains.Resolve(s.Owner, s.Virtual.Domain).Value!;
        (CapabilityId Memory, CapabilityId Region) source = local.MappingSources[o.Mapping.Mapping.MappingId];
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        CapabilityId replacement = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(o.Mapping.Region.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        s.Kernel.BeforeSecureGuestRegionPublication = () =>
        {
            s.Provider.CheckLocks!();
            if (fault == 1) local.Mappings[o.Mapping.Mapping.MappingId] = o.Mapping with { Mapping = o.Mapping.Mapping with { Generation = new(2) } };
            else
            {
                local.MappingSources[o.Mapping.Mapping.MappingId] = (source.Memory, replacement);
                if (fault == 2)
                {
                    Assert.True(s.Kernel.CapabilityAuthority.Revoke(source.Region).IsSuccess);
                    local.MappingSources[o.Mapping.Mapping.MappingId] = source;
                }
            }
        };
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(1, s.Provider.SecureRegionBindCalls);
        Assert.Equal(1, s.Provider.OverlayCloseOrder.Count(x => x == "secure"));
        _ = RetainedOverlay(s); AssertOverlayPendingBalanced(s);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SecureGuestBackendResetNeverSuppliesClosureAndBalancesPending(bool late)
    {
        (Scenario S, GuestRegionMapping Mapping, CapabilityId Region, SecureExecutionBinding Execution) o = OverlayScenario(); Scenario s = o.S;
        void Reset() { s.Provider.CheckLocks!(); Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess); }
        if (late) s.Kernel.BeforeSecureGuestRegionPublication = Reset;
        else s.Provider.DuringSecureRegionBind = Reset;
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, o.Execution, o.Mapping.Mapping, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(1, s.Provider.SecureRegionBindCalls);
        Assert.Empty(s.Provider.OverlayCloseOrder);
        _ = RetainedOverlay(s); AssertOverlayPendingBalanced(s);
        Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, o.Mapping.Mapping).IsSuccess);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    public void SecureRegionFreshSourcesRefuseBeforeAdmissionOrLatePublication(int sourceKind, bool late)
    {
        Scenario s = Create();
        SecureExecutionBinding binding = Bind(s).Value;
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        OwnedBuffer<byte> region = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        CapabilityId regionCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        PlatformRegionMapping mapping = s.Kernel.MapPlatformOwnedRegion(s.Owner, s.Parent, regionCapability, region.Handle,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value;
        CapabilityId source = sourceKind switch { 0 => s.Secure.MemoryCapability, 1 => s.Virtual.ConfigureCapability, _ => s.Secure.ConfigureCapability };
        void Revoke() { s.Provider.CheckLocks!(); Assert.True(s.Kernel.CapabilityAuthority.Revoke(source).IsSuccess); }
        if (late) s.Kernel.BeforeSecureRegionLocalCommit = Revoke;
        else s.Kernel.BeforeSecureRegionAdmission = Revoke;
        KernelResult refused = s.Kernel.BindSecureRegion(s.Owner, s.Secure.Domain, s.Secure.MemoryCapability, mapping,
            PlatformSecureRegionClass.Private);
        Assert.False(refused.IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
        Assert.Empty(s.Provider.OverlayCloseOrder);
        IDictionary bridgeRecords = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_secureDomains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.PlatformAuthority)!;
        object bridgeRecord = Assert.Single(bridgeRecords.Values.Cast<object>());
        IDictionary receipts = (global::System.Collections.IDictionary)bridgeRecord.GetType().GetProperty("Regions")!.GetValue(bridgeRecord)!;
        Assert.Equal(late ? 1 : 0, receipts.Count);
        IDictionary localRecords = (global::System.Collections.IDictionary)typeof(RuntimeKernel)
            .GetField("_secureDomainRecords", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel)!;
        object local = Assert.Single(localRecords.Values.Cast<object>());
        Assert.Empty(((global::System.Collections.IDictionary)local.GetType().GetProperty("Regions")!.GetValue(local)!).Values.Cast<object>());
        Assert.False((bool)local.GetType().GetProperty("RegionMutationInFlight")!.GetValue(local)!);
        Assert.False(s.Kernel.BindSecureRegion(s.Owner, s.Secure.Domain, s.Secure.MemoryCapability, mapping,
            PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
        if (late) Assert.False(s.Kernel.RevokePlatformRegionMapping(s.Owner, mapping).IsSuccess);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SecureRegionProcessStopAndOverlappingBindCannotPublishOrStartAnotherEffect(bool late)
    {
        Scenario s = Create();
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        OwnedBuffer<byte> region = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        CapabilityId cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        PlatformRegionMapping mapping = s.Kernel.MapPlatformOwnedRegion(s.Owner, s.Parent, cap, region.Handle,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value;
        void Stop()
        {
            s.Provider.CheckLocks!();
            if (late)
                Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.BindSecureRegion(s.Owner, s.Secure.Domain,
                    s.Secure.MemoryCapability, mapping, PlatformSecureRegionClass.Private).Error);
            _ = s.Kernel.TerminateProcess(s.Owner);
        }
        if (late) s.Provider.DuringSecureRegionBind = Stop;
        else s.Kernel.BeforeSecureRegionAdmission = Stop;
        Assert.False(s.Kernel.BindSecureRegion(s.Owner, s.Secure.Domain, s.Secure.MemoryCapability, mapping,
            PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(late ? 1 : 0, s.Provider.SecureRegionBindCalls);
        if (late) Assert.False(s.Kernel.RevokePlatformRegionMapping(s.Owner, mapping).IsSuccess);
    }

    [Fact]
    public void SharedVirtualIoDeliveryKeepsCompositionPinnedThroughProviderCallbackAndBalancesUse()
    {
        Scenario s = Create();
        SecureExecutionBinding binding = Bind(s).Value;
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        CapabilityDescriptorV1 cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.Device,
            "device:secure-virtual-type2", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        PlatformDeviceLease device = s.Kernel.BindPlatformDevice(s.Owner, s.Parent, cap.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        int calls = 0;
        s.Provider.DuringVirtualIoBind = () =>
        {
            calls++;
            Assert.Equal(KernelError.PlatformBindingDraining, s.Kernel.CloseSecureExecution(binding).Error);
            Assert.Equal(0, s.Provider.CloseCalls);
        };
        KernelResult<VirtualIoBinding> io = s.Kernel.BindVirtualIo(s.Owner, s.Virtual.Domain, device,
            new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 4096), binding);
        Assert.True(io.IsSuccess, io.Message);
        Assert.Equal(1, calls);
        s.Provider.DuringVirtualIoBind = null;
        Assert.True(s.Kernel.CloseVirtualIo(io.Value).IsSuccess);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void ComposedEventAdmissionRequiresBothConfigureSourcesAndRetainsLateReceipt(bool virtualSource, bool during)
    {
        Scenario s = Create();
        SecureExecutionBinding binding = Bind(s).Value;
        CapabilityId source = virtualSource ? s.Virtual.ConfigureCapability : s.Secure.ConfigureCapability;
        KernelEventEndpoint endpoint = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;
        void Revoke() => Assert.True(s.Kernel.CapabilityAuthority.Revoke(source).IsSuccess);
        if (during) s.Provider.DuringEvent = Revoke;
        else Revoke();
        Assert.False(s.Kernel.InjectVirtualEvent(s.Owner, s.Virtual.Domain, s.Virtual.EventCapability, endpoint).IsSuccess);
        Assert.Equal(during ? 1 : 0, s.Provider.EventEffects);
        IDictionary children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_childBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.PlatformAuthority)!;
        object child = Assert.Single(children.Values.Cast<object>());
        Assert.Equal(during ? 1ul : 0ul, (ulong)child.GetType().GetProperty("LastEventSequence")!.GetValue(child)!);
        Assert.Equal(0, (int)child.GetType().GetProperty("PendingChildEffects")!.GetValue(child)!);
        Assert.False(s.Kernel.InjectVirtualEvent(s.Owner, s.Virtual.Domain, s.Virtual.EventCapability, endpoint).IsSuccess);
        Assert.Equal(during ? 1 : 0, s.Provider.EventEffects);
        // Disposal balances the lifetime pin; revoke does not fabricate closure.
        Assert.Equal(0, s.Provider.CloseCalls);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
    }

    [Fact]
    public void InitialSecureProviderQueryExceptionRefusesWithoutBindOrClosureFactAndCanRetry()
    {
        Scenario s = Create();
        s.Provider.DuringCapabilityQuery = () => throw new IOException("Controlled initial query loss.");
        Assert.Equal(KernelError.PlatformFaulted, Bind(s).Error);
        Assert.Equal(0, s.Provider.BindCalls);
        Assert.Equal(0, s.Provider.CloseCalls);
        s.Provider.DuringCapabilityQuery = null;
        KernelResult<SecureExecutionBinding> binding = Bind(s);
        Assert.True(binding.IsSuccess, binding.Message);
        Assert.Equal(1, s.Provider.BindCalls);
        Assert.True(s.Kernel.CloseSecureExecution(binding.Value).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void SecureBindSourceRevokeRefusesInitialOrLateOwnerAdmission(bool virtualSource, bool duringBind)
    {
        Scenario s = Create();
        CapabilityId source = virtualSource ? s.Virtual.ConfigureCapability : s.Secure.ConfigureCapability;
        void Revoke() => Assert.True(s.Kernel.CapabilityAuthority.Revoke(source).IsSuccess);
        if (duringBind) s.Provider.DuringBind = Revoke;
        else s.Provider.DuringCapabilityQuery = () =>
        {
            s.Provider.DuringCapabilityQuery = null;
            Revoke();
        };
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(duringBind ? 1 : 0, s.Provider.BindCalls);
        Assert.Equal(duringBind ? 1 : 0, s.Provider.CloseCalls);
        if (duringBind)
            Assert.Equal(s.Provider.LastRequest, s.Provider.LastCloseReceipt!.Value.Request);
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(duringBind ? 1 : 0, s.Provider.BindCalls);
    }

    [Fact]
    public void SecureBindIdentityExhaustionRefusesBeforeProviderEffect()
    {
        Scenario s = Create();
        typeof(RuntimeKernel).GetField("_nextSecureExecution", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(s.Kernel, ulong.MaxValue);
        Assert.Equal(KernelError.CapacityExhausted, Bind(s).Error);
        Assert.Equal(0, s.Provider.BindCalls);
        Assert.Equal(0, s.Provider.CloseCalls);
    }

    [Fact]
    public void KnownSecureBindReceiptSurvivesLateProviderCapabilityQueryException()
    {
        Scenario s = Create();
        s.Provider.DuringBind = () => s.Provider.DuringCapabilityQuery = () =>
        {
            s.Provider.DuringCapabilityQuery = null;
            throw new IOException("Controlled late provider contract query loss.");
        };
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(1, s.Provider.BindCalls);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.NotNull(s.Provider.LastCloseReceipt);
        Assert.Equal(s.Provider.LastRequest, s.Provider.LastCloseReceipt!.Value.Request);
        Assert.Equal(s.Provider.LastRequest, s.Provider.LastCloseRequest);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void InitialSecureProviderDriftRefusesType2ActionAndOldReceiptCannotRecover(int fault)
    {
        ComposedWorkload c = CreateComposedWorkload();
        int actionCalls = 0;
        c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = () => c.Base.Provider.RevalidationFault = fault;
        KernelResult<ExternalOperationSnapshot> result;
        try
        {
            result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () => actionCalls++);
        }
        finally { c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = null; }
        Assert.False(result.IsSuccess);
        Assert.Equal(0, actionCalls);
        Assert.Equal(0, c.Base.Provider.EventEffects);
        ExternalOperationSnapshot operation = c.Base.Kernel.ExternalOperations.Query(c.Execution.Operation).Value!;
        Assert.DoesNotContain(operation.Transitions, item => item.Event is "Published" or "PublicationEffectAmbiguous");
        c.Base.Provider.RevalidationFault = -1;
        int calls = c.Base.Provider.RevalidateCalls;
        Assert.False(c.Base.Kernel.PrepareSecurePublication(c.Base.Owner, c.Context).IsSuccess);
        Assert.Equal(calls, c.Base.Provider.RevalidateCalls);
        Assert.False(c.Base.Kernel.CloseSecureExecution(c.SecureExecution).IsSuccess);
    }

    [Fact]
    public void Type2PublishedGuestEventKeepsProviderClosurePinnedUntilLocalDelivery()
    {
        ComposedWorkload c = CreateComposedWorkload();
        KernelEventEndpoint endpoint = c.Base.Kernel.CreateKernelEventEndpoint(c.Base.Owner).Value!;
        bool checkedPending = false;
        c.Base.Provider.DuringEvent = () =>
        {
            KernelResult closure = ((ICxlTeardownParticipant)c.Service).CloseForProcess(c.Base.Owner, c.Owner);
            Assert.Equal(KernelError.PlatformBindingDraining, closure.Error);
            Assert.Equal(ExternalOperationState.Published, c.Base.Kernel.ExternalOperations.Query(c.Execution.Operation).Value!.State);
            Assert.Equal(KernelError.PlatformBindingActive, c.Bridge.ReleaseFabric(c.Fabric).Error);
            Assert.False(c.Memory.Close(c.InputPlacement.PlacementId).IsSuccess);
            Assert.False(c.Memory.Close(c.OutputPlacement.PlacementId).IsSuccess);
            checkedPending = true;
        };
        KernelResult<ExternalOperationSnapshot> completed = c.Service.CompleteVisiblePublishWithGuestEvent(c.Base.Owner, c.Execution,
            [c.Candidate], () => { }, c.VirtualIo, c.Base.Virtual.EventCapability, endpoint);
        Assert.True(checkedPending);
        Assert.True(completed.IsSuccess, completed.Message);
        Assert.Equal(ExternalOperationState.Released, completed.Value!.State);
        Assert.Equal(1, c.Base.Provider.EventEffects);
        c.Base.Provider.DuringEvent = null;
        CloseComposedWorkload(c, c.Fabric);
    }

    [Fact]
    public void ExactCompositionAndTerminalCloseAreIdempotent()
    {
        Scenario s = Create(); KernelResult<SecureExecutionBinding> binding = Bind(s);
        Assert.True(binding.IsSuccess, binding.Message);
        Assert.True(s.Kernel.RevalidateSecureExecution(binding.Value).IsSuccess);
        Assert.False(Bind(s).IsSuccess);
        Assert.True(s.Kernel.CloseSecureExecution(binding.Value).IsSuccess);
        Assert.True(s.Kernel.CloseSecureExecution(binding.Value).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.False(s.Kernel.CloseSecureExecution(binding.Value with { Generation = 2 }).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void BothExactCapabilitiesAndCurrentDomainGenerationsAreRequired(int fault)
    {
        Scenario s = Create();
        KernelResult<SecureExecutionBinding> result = s.Kernel.BindSecureExecution(s.Owner,
            fault == 2 ? s.Virtual.Domain with { Generation = new(2) } : s.Virtual.Domain,
            fault == 0 ? new(999999) : fault == 4 ? s.Secure.EvidenceCapability : s.Virtual.ConfigureCapability,
            fault == 3 ? s.Secure.Domain with { Generation = new(2) } : s.Secure.Domain,
            fault == 1 ? new(999999) : fault == 5 ? s.Secure.EvidenceCapability : s.Secure.ConfigureCapability);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, s.Provider.BindCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void MissingUnsupportedOrNonProductionCompositionContractCannotAdmit(int fault)
    {
        Scenario s = Create();
        s.Provider.SecureExecutionContractVersion = fault == 0 ? 0u : fault == 1 ? 2u : 1u;
        s.Provider.ProductionSecureExecution = fault != 2;
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(0, s.Provider.BindCalls);
    }

    [Fact]
    public void ParentMismatchCannotCompose()
    {
        Scenario s = Create();
        ProcessHandle other = TestFixtures.Create(s.Kernel, 991, 992).Handle;
        PlatformDomainBinding parent = s.Kernel.BindPlatformAuthorityDomain(other).Value;
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        CapabilityId create = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.SecureCompute,
            SecureComputeResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        // Provider bridge checks owner equality before materializing another-parent secure authority.
        Assert.False(s.Kernel.CreateSecureDomain(s.Owner, create, parent, new([SecureProperty.PrivateMemory], 4096)).IsSuccess);
        KernelResult<SecureExecutionBinding> composed = Bind(s);
        Assert.True(composed.IsSuccess);
        SecureExecutionContext context = s.Provider.LastRequest.Context;
        Assert.False(s.Kernel.PlatformAuthority.ResolveSecureExecutionContext(context.Virtual, context.Secure,
            context.Child with { ParentBinding = parent }, context.SecureBinding,
            context.LocalPolicyGeneration, context.LocalProtectionGeneration).IsSuccess);
        Assert.Equal(1, s.Provider.BindCalls);
    }

    [Fact]
    public void ProviderChildAndSecureBindingStalenessAreRejectedBeforeNewAdmission()
    {
        Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
        SecureExecutionContext c = s.Provider.LastRequest.Context;
        Assert.False(s.Kernel.PlatformAuthority.ResolveSecureExecutionContext(c.Virtual, c.Secure,
            c.Child with { Generation = new(2) }, c.SecureBinding, 1, 1).IsSuccess);
        Assert.False(s.Kernel.PlatformAuthority.ResolveSecureExecutionContext(c.Virtual, c.Secure,
            c.Child, c.SecureBinding with { Generation = new(2) }, 1, 1).IsSuccess);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.True(s.Kernel.PlatformAuthority.RevokeSecureDomain(c.SecureBinding).IsSuccess);
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(1, s.Provider.BindCalls);
    }

    [Fact]
    public void ClosedProviderChildCannotBeUsedForNewAdmission()
    {
        Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.True(s.Kernel.PlatformAuthority.TransitionChildDomain(s.Provider.LastRequest.Context.Child, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.True(s.Kernel.PlatformAuthority.CloseChildDomain(s.Provider.LastRequest.Context.Child).IsSuccess);
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(1, s.Provider.BindCalls);
    }

    [Fact]
    public void PublicationRevalidatesAfterProviderEventAndRollsBackStaging()
    {
        Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
        KernelEventEndpoint endpoint = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;
        s.Provider.DuringEvent = () => s.Provider.RevalidationFault = 0;
        Assert.False(s.Kernel.InjectVirtualEvent(s.Owner, s.Virtual.Domain, s.Virtual.EventCapability, endpoint).IsSuccess);
        Assert.Equal(1, s.Provider.EventEffects);
        Assert.False(s.Kernel.InjectVirtualEvent(s.Owner, s.Virtual.Domain, s.Virtual.EventCapability, endpoint).IsSuccess);
        Assert.Equal(1, s.Provider.EventEffects);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        KernelResult terminated = s.Kernel.TerminateProcess(s.Owner);
        Assert.True(terminated.IsSuccess, terminated.Message);
    }

    [Fact]
    public void InFlightProviderEffectPinsCompositionAgainstCloseAndTeardown()
    {
        Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
        s.Provider.DuringChildEffect = () => {
            Assert.False(s.Kernel.CloseSecureExecution(binding).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(0, s.Provider.CloseCalls);
        };
        Assert.False(s.Kernel.StartVirtualDomain(s.Owner, s.Virtual.Domain, s.Virtual.ExecuteCapability).IsSuccess);
        Assert.NotEqual(VirtualDomainState.Running, s.Kernel.QueryVirtualDomain(s.Owner, s.Virtual.Domain).Value);
        Assert.Equal(1, s.Provider.ChildEffects);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Fact]
    public void BackendResetQuarantinesCompositionWithoutNewProviderEffects()
    {
        Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
        Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
        Assert.False(s.Kernel.RevalidateSecureExecution(binding).IsSuccess);
        Assert.Equal(0, s.Provider.RevalidateCalls);
        Assert.False(s.Kernel.StartVirtualDomain(s.Owner, s.Virtual.Domain, s.Virtual.ExecuteCapability).IsSuccess);
        Assert.Equal(0, s.Provider.ChildEffects);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void MalformedOrPartialAdmissionCompensatesExactRequest(int fault)
    {
        Scenario s = Create(); s.Provider.AdmissionFault = fault;
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.Equal(s.Provider.LastRequest, s.Provider.LastCloseRequest);
        Assert.True(s.Kernel.CloseSecureExecution(s.Provider.LastRequest.Binding).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void PolicyProtectionProviderChildAndSecureLeaseChangesQuarantine(int fault)
    {
        Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
        s.Provider.RevalidationFault = fault;
        Assert.False(s.Kernel.RevalidateSecureExecution(binding).IsSuccess);
        int calls = s.Provider.RevalidateCalls;
        Assert.False(s.Kernel.RevalidateSecureExecution(binding).IsSuccess);
        Assert.Equal(calls, s.Provider.RevalidateCalls);
        Assert.False(Bind(s).IsSuccess);
        Assert.False(s.Kernel.StartVirtualDomain(s.Owner, s.Virtual.Domain, s.Virtual.ExecuteCapability).IsSuccess);
        Assert.False(s.Kernel.TransitionSecureDomain(s.Owner, s.Secure.Domain, s.Secure.ExecuteCapability,
            PlatformSecureDomainTransition.Start).IsSuccess);
        Assert.Equal(0, s.Provider.ChildEffects);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void AmbiguousClosurePinsProcessAndDomainsWithoutAnOriginalResolutionContract(int fault)
    {
        Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
        s.Provider.ClosureFault = fault;
        Assert.False(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.False(s.Kernel.DestroySecureDomain(s.Owner, s.Secure.Domain, s.Secure.ConfigureCapability).IsSuccess);
        Assert.False(s.Kernel.DestroyVirtualDomain(s.Owner, s.Virtual.Domain, s.Virtual.ConfigureCapability).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.True(s.Kernel.Processes.Resolve(s.Owner).IsSuccess);
        Assert.Equal(0, s.Provider.SecureCloses);
        Assert.Equal(0, s.Provider.ChildCloses);
        s.Provider.ClosureFault = -1;
        Assert.False(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.Empty(s.Provider.TerminalOrder);
    }

    [Fact]
    public void AmbiguousAdmissionAndLostCloseResponseKeepTheOriginalRequestPinned()
    {
        Scenario s = Create(); s.Provider.AdmissionFault = 5; s.Provider.ClosureFault = 0;
        Assert.False(Bind(s).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        s.Provider.ClosureFault = -1;
        Assert.False(s.Kernel.CloseSecureExecution(s.Provider.LastRequest.Binding).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
    }

    [Fact]
    public void ContainmentIsTerminalButUnavailableIsNot()
    {
        Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
        s.Provider.Contained = true;
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.True(s.Kernel.DestroySecureDomain(s.Owner, s.Secure.Domain, s.Secure.ConfigureCapability).IsSuccess);
    }

    [Fact]
    public void ReplayedCorrelationAndCrossBindingReceiptCannotAdmit()
    {
        Scenario s = Create(); SecureExecutionBinding first = Bind(s).Value;
        Assert.True(s.Kernel.CloseSecureExecution(first).IsSuccess);
        s.Provider.ReplayCorrelation = true;
        Assert.False(Bind(s).IsSuccess);
        s.Provider.AdmissionFault = 0;
        Assert.False(Bind(s).IsSuccess);
    }

    [Fact]
    public void TeardownDuringProviderAdmissionStopsNewAdmissionsAndCompensates()
    {
        Scenario s = Create();
        s.Provider.DuringBind = () => Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(Bind(s).IsSuccess);
        Assert.Equal(1, s.Provider.CloseCalls);
        Assert.Equal(0, s.Provider.SecureCloses);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Fact]
    public void PublicAndSipSurfacesDoNotExposeCompositionOrProviderIdentity()
    {
        Assert.False(typeof(SecureExecutionBinding).IsPublic);
        Assert.False(typeof(SecureExecutionContext).IsPublic);
        Assert.False(typeof(ISecureExecutionProvider).IsPublic);
        Assert.DoesNotContain(typeof(RuntimeKernel).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method => method.Name.Contains("SecureExecution"));
        Assert.DoesNotContain(typeof(SecureDomainHandle).Assembly.GetExportedTypes(), type => type.Name.Contains("SecureExecution"));
    }

    [Fact]
    public void LocalProtectionChangeInvalidatesComposition()
    {
        Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
        SecureExecutionContext context = s.Provider.LastRequest.Context;
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        OwnedBuffer<byte> region = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        CapabilityId cap = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        KernelResult<PlatformRegionMapping> mapped = s.Kernel.MapPlatformOwnedRegion(s.Owner, context.Parent, cap, region.Handle, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        Assert.True(mapped.IsSuccess, mapped.Message);
        Assert.True(s.Kernel.BindSecureRegion(s.Owner, s.Secure.Domain, s.Secure.MemoryCapability, mapped.Value,
            PlatformSecureRegionClass.Private).IsSuccess);
        int calls = s.Provider.RevalidateCalls;
        Assert.False(s.Kernel.RevalidateSecureExecution(binding).IsSuccess);
        Assert.Equal(calls, s.Provider.RevalidateCalls);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
        Assert.False(s.Kernel.DestroySecureDomain(s.Owner, s.Secure.Domain, s.Secure.ConfigureCapability).IsSuccess);
        Assert.False(s.Kernel.BindSecureRegion(s.Owner, s.Secure.Domain, s.Secure.MemoryCapability, mapped.Value,
            PlatformSecureRegionClass.Private).IsSuccess);
        Assert.False(s.Kernel.TransitionSecureDomain(s.Owner, s.Secure.Domain, s.Secure.ExecuteCapability,
            PlatformSecureDomainTransition.Start).IsSuccess);
        Assert.True(s.Kernel.CloseSecureRegion(s.Owner, s.Secure.Domain, s.Secure.MemoryCapability, mapped.Value).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RevokedEitherAuthorityBlocksEffectsBeforeProviderRevalidation(bool secure)
    {
        Scenario s = Create(); SecureExecutionBinding binding = Bind(s).Value;
        Assert.True(s.Kernel.RevokeCapability(secure ? s.Secure.ConfigureCapability : s.Virtual.ConfigureCapability).IsSuccess);
        Assert.False(s.Kernel.RevalidateSecureExecution(binding).IsSuccess);
        Assert.Equal(0, s.Provider.RevalidateCalls);
        Assert.False(s.Kernel.StartVirtualDomain(s.Owner, s.Virtual.Domain, s.Virtual.ExecuteCapability).IsSuccess);
        Assert.Equal(0, s.Provider.ChildEffects);
        Assert.True(s.Kernel.CloseSecureExecution(binding).IsSuccess);
    }

    [Fact]
    public void ProviderFeaturesAndDiagnosticObjectsCannotSupplyAuthorityOrBindImplicitly()
    {
        Scenario s = Create();
        Assert.Equal(PlatformFeatureAvailability.ProductionSecure,
            s.Kernel.QueryPlatformFeatures().Resolve(PlatformFeatureFamily.SecureDomains).Availability);
        Assert.Equal(0, s.Provider.BindCalls);
        MethodInfo operation = typeof(RuntimeKernel).GetMethod("BindSecureExecution", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Equal(new[] { typeof(ProcessHandle), typeof(VirtualDomainHandle), typeof(CapabilityId),
            typeof(SecureDomainHandle), typeof(CapabilityId) }, operation.GetParameters().Select(x => x.ParameterType));
        Assert.False(typeof(ISecureExecutionProvider).IsAssignableFrom(typeof(HostPlatformAuthorityProvider)));
    }

    [Fact]
    public void SecureGuestOverlayUsesExistingGuestLineageAndClosesFirst()
    {
        Scenario s = Create();
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        OwnedBuffer<byte> buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        CapabilityId region = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        KernelResult<GuestRegionMapping> guest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, region,
            buffer.Handle, new(0, 64), GuestMemoryAccess.Read);
        Assert.True(guest.IsSuccess, guest.Message);
        SecureExecutionBinding execution = Bind(s).Value;
        KernelResult<SecureGuestRegionBinding> overlay = s.Kernel.BindSecureGuestRegion(s.Owner, execution, guest.Value!.Mapping, PlatformSecureRegionClass.Private);
        Assert.True(overlay.IsSuccess, overlay.Message);
        Assert.Equal(s.Provider.LastGuestParent, s.Provider.LastSecureParent);
        Assert.True(s.Kernel.CloseSecureGuestRegion(overlay.Value).IsSuccess);
        Assert.Equal(new[] { "secure" }, s.Provider.OverlayCloseOrder);
    }

    [Fact]
    public void StaleGuestMappingMakesZeroSecureOverlayProviderCalls()
    {
        Scenario s = Create();
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        OwnedBuffer<byte> buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        CapabilityId region = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        GuestRegionMapping guest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, region,
            buffer.Handle, new(0, 64), GuestMemoryAccess.Read).Value!;
        GuestRegionMappingHandle stale = guest.Mapping with { Generation = new(2) };
        Assert.False(s.Kernel.BindSecureGuestRegion(s.Owner, Bind(s).Value, stale, PlatformSecureRegionClass.Private).IsSuccess);
        Assert.Equal(0, s.Provider.SecureRegionBindCalls);
    }

    [Fact]
    public void AmbiguousSecureOverlayCloseQuarantinesGuestMappingAndBlocksTeardown()
    {
        Scenario s = Create();
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        OwnedBuffer<byte> buffer = s.Kernel.AllocateBuffer<byte>(s.Owner, 64).Value!;
        CapabilityId region = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        GuestRegionMapping guest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, region,
            buffer.Handle, new(0, 64), GuestMemoryAccess.Read).Value!;
        SecureGuestRegionBinding overlay = s.Kernel.BindSecureGuestRegion(s.Owner, Bind(s).Value, guest.Mapping, PlatformSecureRegionClass.Private).Value;
        s.Provider.SecureRegionClosureFault = true;
        Assert.False(s.Kernel.CloseSecureGuestRegion(overlay).IsSuccess);
        Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability, guest.Mapping).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        s.Provider.SecureRegionClosureFault = false;
        Assert.False(s.Kernel.CloseSecureGuestRegion(overlay).IsSuccess);
        int terminalCloseCalls = s.Provider.OverlayCloseOrder.Count(item => item == "secure");
        Assert.False(s.Kernel.CloseSecureGuestRegion(overlay).IsSuccess);
        Assert.Equal(1, terminalCloseCalls);
        Assert.Equal(terminalCloseCalls, s.Provider.OverlayCloseOrder.Count(item => item == "secure"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecureVirtualizedType3BackedType2WorkloadPublishesEventBeforeExactClosure(bool waiterBeforePublication)
    {
        Scenario s = Create();
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        PlatformDomainIdentity subject = new PlatformDomainIdentity(process.DomainId, s.Owner);
        CapabilityDescriptorV1 deviceCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.Device,
            "device:secure-virtual-type2", CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure).Value!;
        PlatformDeviceLease device = s.Kernel.BindPlatformDevice(s.Owner, s.Parent, deviceCapability.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;

        CxlType3ModelProvider model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("secure-virtual-type2"), device.Device, 4096).IsSuccess);
        CxlEndpointSnapshot endpoint = model.QueryEndpoint(new("secure-virtual-type2")).Value!;
        CxlSecurityModelProvider security = new CxlSecurityModelProvider();
        CxlSecurityProperties securityProperties = CxlSecurityProperties.IdeEnabled | CxlSecurityProperties.DeviceAuthenticated;
        Assert.True(security.Register(endpoint.EndpointId, endpoint.DeviceGeneration, securityProperties).IsSuccess);
        CxlAuthorityBridge bridge = new CxlAuthorityBridge(s.Kernel, model, model, model, model,
            new UnsupportedCoherentProvider(), security);
        CxlType3MemoryAuthority memory = new CxlType3MemoryAuthority(s.Kernel, bridge);

        OwnedBuffer<byte> input = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        OwnedBuffer<byte> output = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        input.Span.Fill(0x5A);
        CxlMemoryPlacementSnapshot inputPlacement = memory.Place(new(process.DomainId, s.Owner.Generation), input.Handle,
            subject, device, endpoint, CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryPlacementSnapshot outputPlacement = memory.Place(new(process.DomainId, s.Owner.Generation), output.Handle,
            subject, device, endpoint, CxlMemoryPersistence.Volatile).Value!;

        CapabilityId inputCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(input.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        CapabilityId outputCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(output.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        GuestRegionMapping inputGuest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability,
            inputCapability, input.Handle, new(0, 16), GuestMemoryAccess.Read).Value!;
        GuestRegionMapping outputGuest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability,
            outputCapability, output.Handle, new(16, 16), GuestMemoryAccess.Read | GuestMemoryAccess.Write).Value!;
        SecureExecutionBinding secureExecution = Bind(s).Value;
        SecureGuestRegionBinding inputSecure = s.Kernel.BindSecureGuestRegion(s.Owner, secureExecution, inputGuest.Mapping,
            PlatformSecureRegionClass.Private).Value;
        SecureGuestRegionBinding outputSecure = s.Kernel.BindSecureGuestRegion(s.Owner, secureExecution, outputGuest.Mapping,
            PlatformSecureRegionClass.Private).Value;
        VirtualIoBinding virtualIo = s.Kernel.BindVirtualIo(s.Owner, s.Virtual.Domain, device,
            new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 4096), secureExecution).Value;

        OwnedBuffer<byte> control = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        RegionUseDescriptor controlUse = s.Kernel.AcquireRegionUse(s.Owner, control.Handle, RegionUseMode.DevicePrivate,
            new(0, 16)).Value!;
        CxlFabricBinding fabric = bridge.BindFabric(new(process.DomainId, s.Owner.Generation), controlUse.Handle,
            subject, device, new(endpoint.EndpointId, endpoint.DeviceGeneration, 16,
                CxlMemoryPersistence.Volatile, CxlMemorySharing.Exclusive)).Value!;
        ComputeProviderCandidate candidate = new ComputeProviderCandidate(new("secure-virtual-type2"), 1,
            ComputeProviderCapabilities.AcceleratorExecution | ComputeProviderCapabilities.StagedPublication |
            ComputeProviderCapabilities.SecureComputeEvidence | ComputeProviderCapabilities.VirtualizedDomain,
            4096, 1, 1, true, false);
        ComputeIntent intent = new ComputeIntent(ComputeOperationKind.Copy,
            new(input.Handle, new(0, 16)), new(output.Handle, new(0, 16)),
            ComputePublicationPreference.StagedRequired, RequiresSecureEvidence: true,
            RequiresVirtualizedDomain: true);
        KernelResult<VirtualComputeContext> contextResult = s.Kernel.CreateVirtualComputeContext(s.Owner, s.Virtual.Domain,
            inputGuest.Mapping, outputGuest.Mapping, virtualIo, secureExecution);
        Assert.True(contextResult.IsSuccess, contextResult.Message);
        VirtualComputeContext context = contextResult.Value;
        KernelResult<ComputePlan> planResult = s.Kernel.PlanVirtualizedCompute(s.Owner, intent, new(true, true, true),
            [candidate], context);
        Assert.True(planResult.IsSuccess, planResult.Message);
        ComputePlan plan = planResult.Value!;
        CxlType2ModelAccelerator accelerator = new CxlType2ModelAccelerator();
        CxlFabricManagerAuthority manager = new CxlFabricManagerAuthority(s.Kernel, model);
        Assert.True(manager.Register(fabric).IsSuccess);
        CxlType2AcceleratorService service = new CxlType2AcceleratorService(s.Kernel, bridge, accelerator, manager);
        CxlSecurityPolicy policy = new CxlSecurityPolicy(securityProperties, new(1), RequiredForOperation: true);
        CxlType2Execution execution = service.SubmitVirtualized(s.Owner, plan, [candidate], context, subject, device,
            endpoint, fabric, 1, securityAuthority: new(bridge, security), securityPolicy: policy).Value!;
        KernelEventEndpoint guestEvent = s.Kernel.CreateKernelEventEndpoint(s.Owner).Value!;
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        Task<KernelResult<KernelEvent>>? waiter = waiterBeforePublication
            ? s.Kernel.WaitForKernelEventAsync(s.Owner, guestEvent, cancellation.Token).AsTask() : null;
        if (waiter is not null) Assert.False(waiter.IsCompleted);

        Assert.Equal(KernelError.InvalidTransition, s.Kernel.PublishVirtualIoEvent(s.Owner, virtualIo,
            execution.Operation, s.Virtual.EventCapability, guestEvent).Error);
        Assert.Equal(0, s.Provider.EventEffects);
        KernelResult<ExternalOperationSnapshot> completed = service.CompleteVisiblePublishWithGuestEvent(s.Owner, execution, [candidate],
            () => input.Span.CopyTo(output.Span), virtualIo, s.Virtual.EventCapability, guestEvent);
        Assert.True(completed.IsSuccess, completed.Message);
        Assert.Equal(ExternalOperationState.Released, completed.Value!.State);
        Assert.Equal(input.Span.ToArray(), output.Span.ToArray());
        cancellation.Cancel(); // A committed original delivery belongs to its waiter even after cancellation.
        KernelResult<KernelEvent> delivered = waiter is null ? await s.Kernel.WaitForKernelEventAsync(s.Owner, guestEvent) : await waiter;
        Assert.True(delivered.IsSuccess, delivered.Message);
        Assert.False(s.Kernel.ConsumeKernelEvent(s.Owner, guestEvent).IsSuccess);
        Assert.Equal(1, s.Provider.EventEffects);
        Assert.All(s.Kernel.Regions.Snapshot().Where(r => r.Handle == input.Handle || r.Handle == output.Handle),
            r => Assert.Equal(RegionState.Owned, r.State));

        Assert.True(s.Kernel.CloseVirtualIo(virtualIo).IsSuccess);
        Assert.True(s.Kernel.CloseSecureGuestRegion(inputSecure).IsSuccess);
        Assert.True(s.Kernel.CloseSecureGuestRegion(outputSecure).IsSuccess);
        KernelResult inputGuestClose = s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain,
            s.Virtual.MemoryCapability, inputGuest.Mapping);
        Assert.True(inputGuestClose.IsSuccess, inputGuestClose.Message);
        KernelResult outputGuestClose = s.Kernel.CloseGuestRegionMapping(s.Owner, s.Virtual.Domain,
            s.Virtual.MemoryCapability, outputGuest.Mapping);
        Assert.True(outputGuestClose.IsSuccess, outputGuestClose.Message);
        Assert.True(s.Kernel.CloseSecureExecution(secureExecution).IsSuccess);
        Assert.True(memory.Close(inputPlacement.PlacementId).IsSuccess);
        Assert.True(memory.Close(outputPlacement.PlacementId).IsSuccess);
        Assert.True(bridge.ReleaseFabric(fabric).IsSuccess);
        Assert.True(s.Kernel.ReleaseRegionUse(s.Owner, controlUse.Handle).IsSuccess);
        Assert.True(s.Kernel.DestroySecureDomain(s.Owner, s.Secure.Domain,
            s.Secure.ConfigureCapability).IsSuccess);
        Assert.True(s.Kernel.RevokePlatformDevice(s.Owner, device).IsSuccess);
        KernelResult virtualClose = s.Kernel.DestroyVirtualDomain(s.Owner, s.Virtual.Domain,
            s.Virtual.ConfigureCapability);
        Assert.True(virtualClose.IsSuccess, virtualClose.Message);
        KernelResult finalTeardown = s.Kernel.TerminateProcess(s.Owner);
        Assert.True(finalTeardown.IsSuccess, finalTeardown.Message);
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(r => r.Handle == input.Handle).State);
        Assert.Equal(RegionState.Released, s.Kernel.Regions.Snapshot().Single(r => r.Handle == output.Handle).State);
    }

    [Fact]
    public void FabricReconfigurationDuringSecureVirtualGuestWorkClosesEffectBeforeReclaim()
    {
        ComposedWorkload c = CreateComposedWorkload();

        KernelResult<CxlFabricManagedBinding> draining = c.Manager.BeginReconfiguration(c.Fabric);

        Assert.True(draining.IsSuccess, draining.Message);
        Assert.Equal(ExternalOperationState.Released,
            c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!.State);
        Assert.Equal(PlatformAuthorityStatus.Stale,
            c.Accelerator.ObserveCompletion(c.Execution.Submission).Status);
        Assert.Equal(0, c.Base.Provider.EventEffects);
        Assert.True(c.Base.Kernel.Regions.Validate(c.Input.Handle, c.Owner).IsSuccess);
        KernelResult<CxlFabricManagedBinding> replacement = c.Manager.CompleteReconfiguration(c.Fabric.BindingId);
        Assert.True(replacement.IsSuccess, replacement.Message);
        CloseComposedWorkload(c, replacement.Value!.Binding);
    }

    [Fact]
    public void VirtualComputeIoRequiresExactOperandDomainWithoutQuarantiningValidBinding()
    {
        ComposedWorkload c = CreateComposedWorkload();
        VirtualDomainHandle domain = c.Base.Virtual.Domain;
        Assert.True(c.Base.Kernel.RevalidateVirtualIo(c.Base.Owner, c.VirtualIo, domain).IsSuccess);
        Assert.Equal(KernelError.WrongPlatformDomain,
            c.Base.Kernel.RevalidateVirtualIo(c.Base.Owner, c.VirtualIo, default(VirtualDomainHandle)).Error);
        Assert.True(c.Base.Kernel.RevalidateVirtualComputeContext(c.Base.Owner, c.Context, c.Execution.Plan).IsSuccess);
        Assert.True(c.Base.Kernel.RevalidateVirtualIo(c.Base.Owner, c.VirtualIo).IsSuccess);
        KernelResult<ExternalOperationSnapshot> completed = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate],
            () => c.Input.Span.CopyTo(c.Output.Span));
        Assert.True(completed.IsSuccess, completed.Message);
        CloseComposedWorkload(c, c.Fabric);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void VirtualType2FinalPublicationRequiresBothGuestMappingSources(int source)
    {
        ComposedWorkload c = CreateComposedWorkload();
        CapabilityId capability = source == 0 ? c.Base.Virtual.MemoryCapability
            : source == 1 ? c.InputCapability : c.OutputCapability;
        KernelResult<Func<Func<KernelResult>, KernelResult>> prepared = c.Base.Kernel.PrepareVirtualComputePublication(c.Base.Owner, c.Context, c.Execution.Plan);
        Assert.True(prepared.IsSuccess, prepared.Message);
        bool actionRan = false;
        KernelResult<ExternalOperationSnapshot> result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () =>
        {
            actionRan = true;
            c.Input.Span.CopyTo(c.Output.Span);
            Assert.True(c.Base.Kernel.CapabilityAuthority.Revoke(capability).IsSuccess);
            bool committed = false;
            KernelResult refusal = prepared.Value!(() => { committed = true; return KernelResult.Ok(); });
            Assert.False(refusal.IsSuccess);
            Assert.False(committed);
        });
        Assert.True(actionRan);
        Assert.False(result.IsSuccess);
        ExternalOperationSnapshot operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.NotEqual(ExternalOperationState.Published, operation.State);
        Assert.NotEqual(ExternalOperationState.Released, operation.State);
        Assert.False(c.Memory.Close(c.OutputPlacement.PlacementId).IsSuccess);
        Assert.Equal(0, c.Base.Provider.EventEffects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VirtualType2LateSubmitRegionRevokeRetainsReceiptForFabricCleanup(bool output)
    {
        int callbacks = 0;
        ComposedWorkload c = CreateComposedWorkload(afterSubmit: (kernel, inputSource, outputSource) =>
        {
            callbacks++;
            Assert.True(kernel.CapabilityAuthority.Revoke(output ? outputSource : inputSource).IsSuccess);
        });
        Assert.Equal(1, callbacks);
        Assert.False(c.SubmissionResult.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, c.SubmissionResult.Error);
        Assert.False(c.Bridge.ReleaseFabric(c.Fabric).IsSuccess);
        Assert.False(c.Memory.Close(c.OutputPlacement.PlacementId).IsSuccess);
        KernelResult<CxlFabricManagedBinding> drain = c.Manager.BeginReconfiguration(c.Fabric);
        Assert.True(drain.IsSuccess, drain.Message);
        KernelResult<CxlFabricManagedBinding> replacement = c.Manager.CompleteReconfiguration(c.Fabric.BindingId);
        Assert.True(replacement.IsSuccess, replacement.Message);
        CloseComposedWorkload(c, replacement.Value!.Binding);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VirtualType2RegionRevokeInPreSubmitProviderQueryPreventsAcceleratorEffect(bool output)
    {
        int submits = 0;
        int queries = 0;
        ComposedWorkload c = CreateComposedWorkload(afterSubmit: (_, _, _) => submits++,
            duringPreSubmitRevalidation: (kernel, inputSource, outputSource) =>
            {
                if (queries++ == 0)
                    Assert.True(kernel.CapabilityAuthority.Revoke(output ? outputSource : inputSource).IsSuccess);
            });
        Assert.True(queries > 0);
        Assert.Equal(0, submits);
        Assert.False(c.SubmissionResult.IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, c.SubmissionResult.Error);
        Assert.Equal(0, c.Base.Provider.EventEffects);
        CloseComposedWorkload(c, c.Fabric);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VirtualType2LastDeviceSourceQueryRevokePreventsSubmit(bool output)
    {
        int submits = 0;
        int faults = 0;
        ComposedWorkload c = CreateComposedWorkload(afterSubmit: (_, _, _) => submits++,
            duringIncarnation: (kernel, inputSource, outputSource) =>
            {
                string[] methods = new global::System.Diagnostics.StackTrace().GetFrames()
                    .Select(frame => frame.GetMethod()!.Name).ToArray();
                if (faults != 0 || !methods.Any(name => name.Contains("g__AdmitSubmit")) ||
                    !methods.Contains("CommitCxlDeviceEffect")) return;
                faults++;
                Assert.True(kernel.CapabilityAuthority.Revoke(output ? outputSource : inputSource).IsSuccess);
            });
        Assert.Equal(1, faults);
        Assert.Equal(0, submits);
        Assert.False(c.SubmissionResult.IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, c.SubmissionResult.Error);
        CloseComposedWorkload(c, c.Fabric);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedSecurePublicationCannotAuthorizeRevokedCompositionSource(bool secureSource)
    {
        ComposedWorkload c = CreateComposedWorkload();
        KernelResult<PreparedSecurePublication> prepared = c.Base.Kernel.PrepareSecurePublication(c.Base.Owner, c.Context);
        Assert.True(prepared.IsSuccess, prepared.Message);
        CapabilityId source = secureSource ? c.Base.Secure.ConfigureCapability : c.Base.Virtual.ConfigureCapability;
        Assert.True(c.Base.Kernel.CapabilityAuthority.Revoke(source).IsSuccess);
        bool committed = false;
        KernelResult denied = c.Base.Kernel.CommitCxlBackingPublication(c.Base.Owner, c.Owner,
            () => prepared.Value.Admission(() => { committed = true; return KernelResult.Ok(); }));
        Assert.False(denied.IsSuccess);
        Assert.False(committed);
        bool actionRan = false;
        KernelResult<ExternalOperationSnapshot> result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () => actionRan = true);
        Assert.False(result.IsSuccess);
        Assert.False(actionRan);
        Assert.NotEqual(ExternalOperationState.Published,
            c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedSecurePublicationRefusesLaterQuarantineOrProcessDrain(bool processDrain)
    {
        ComposedWorkload c = CreateComposedWorkload();
        KernelResult<PreparedSecurePublication> prepared = c.Base.Kernel.PrepareSecurePublication(c.Base.Owner, c.Context);
        Assert.True(prepared.IsSuccess, prepared.Message);
        if (processDrain)
            _ = c.Base.Kernel.TerminateProcess(c.Base.Owner);
        else
        {
            c.Base.Provider.RevalidationFault = 4;
            Assert.False(c.Base.Kernel.RevalidateSecureExecution(c.SecureExecution).IsSuccess);
        }
        bool committed = false;
        KernelResult denied = c.Base.Kernel.CommitCxlBackingPublication(c.Base.Owner, c.Owner,
            () => prepared.Value.Admission(() => { committed = true; return KernelResult.Ok(); }));
        Assert.False(denied.IsSuccess);
        Assert.False(committed);
        Assert.False(c.Base.Kernel.PrepareSecurePublication(c.Base.Owner, c.Context).IsSuccess);
    }

    [Fact]
    public void PreparedSecurePublicationCannotOverlapProviderRevalidation()
    {
        ComposedWorkload c = CreateComposedWorkload();
        KernelResult<PreparedSecurePublication> prepared = c.Base.Kernel.PrepareSecurePublication(c.Base.Owner, c.Context);
        Assert.True(prepared.IsSuccess, prepared.Message);
        int callbacks = 0;
        c.Base.Provider.DuringRevalidation = () =>
        {
            callbacks++;
            bool committed = false;
            KernelResult denied = c.Base.Kernel.CommitCxlBackingPublication(c.Base.Owner, c.Owner,
                () => prepared.Value.Admission(() => { committed = true; return KernelResult.Ok(); }));
            Assert.False(denied.IsSuccess);
            Assert.False(committed);
        };
        Assert.True(c.Base.Kernel.RevalidateSecureExecution(c.SecureExecution).IsSuccess);
        Assert.Equal(1, callbacks);
        c.Base.Provider.DuringRevalidation = null;
        KernelResult<ExternalOperationSnapshot> completed = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate],
            () => c.Input.Span.CopyTo(c.Output.Span));
        Assert.True(completed.IsSuccess, completed.Message);
        CloseComposedWorkload(c, c.Fabric);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PreparedSecurePublicationDoesNotRecoverAfterProviderGenerationABA(int drift)
    {
        ComposedWorkload c = CreateComposedWorkload();
        KernelResult<PreparedSecurePublication> prepared = c.Base.Kernel.PrepareSecurePublication(c.Base.Owner, c.Context);
        Assert.True(prepared.IsSuccess, prepared.Message);
        c.Base.Provider.RevalidationFault = drift;
        Assert.False(c.Base.Kernel.RevalidateSecureExecution(c.SecureExecution).IsSuccess);
        c.Base.Provider.RevalidationFault = -1; // Old receipt returns; quarantine still owns admission refusal.
        int queries = c.Base.Provider.RevalidateCalls;
        Assert.False(c.Base.Kernel.RevalidateSecureExecution(c.SecureExecution).IsSuccess);
        Assert.Equal(queries, c.Base.Provider.RevalidateCalls);
        bool committed = false;
        Assert.False(c.Base.Kernel.CommitCxlBackingPublication(c.Base.Owner, c.Owner,
            () => prepared.Value.Admission(() => { committed = true; return KernelResult.Ok(); })).IsSuccess);
        Assert.False(committed);
        Assert.False(c.Base.Kernel.PrepareSecurePublication(c.Base.Owner, c.Context).IsSuccess);
        Assert.False(c.Base.Kernel.CloseSecureExecution(c.SecureExecution).IsSuccess);
        Assert.False(c.Memory.Close(c.OutputPlacement.PlacementId).IsSuccess);
    }

    [Fact]
    public async Task PreparedSecurePublicationCannotOverlapRevalidationOnAnotherThread()
    {
        ComposedWorkload c = CreateComposedWorkload();
        KernelResult<PreparedSecurePublication> prepared = c.Base.Kernel.PrepareSecurePublication(c.Base.Owner, c.Context);
        Assert.True(prepared.IsSuccess, prepared.Message);
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        c.Base.Provider.DuringRevalidation = () =>
        {
            entered.TrySetResult();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
        };
        Task<KernelResult> checking = Task.Run(() => c.Base.Kernel.RevalidateSecureExecution(c.SecureExecution));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            bool committed = false;
            KernelResult denied = c.Base.Kernel.CommitCxlBackingPublication(c.Base.Owner, c.Owner,
                () => prepared.Value.Admission(() => { committed = true; return KernelResult.Ok(); }));
            Assert.False(denied.IsSuccess);
            Assert.False(committed);
            Assert.False(c.Base.Kernel.RevalidateSecureExecution(c.SecureExecution).IsSuccess);
            Assert.False(c.Base.Kernel.CloseSecureExecution(c.SecureExecution).IsSuccess);
        }
        finally
        {
            resume.Set();
            KernelResult settled = await checking.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(settled.IsSuccess, settled.Message);
            c.Base.Provider.DuringRevalidation = null;
        }
        KernelResult<ExternalOperationSnapshot> completed = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate],
            () => c.Input.Span.CopyTo(c.Output.Span));
        Assert.True(completed.IsSuccess, completed.Message);
        CloseComposedWorkload(c, c.Fabric);
    }

    [Fact]
    public void Type3HotRemoveWithLiveSecureGuestMappingsBlocksPublicationUntilExactClosure()
    {
        ComposedWorkload c = CreateComposedWorkload();
        bool published = false;

        Assert.True(c.Model.HotRemove(c.Endpoint.EndpointId).IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, c.Memory.Refresh(c.InputPlacement.PlacementId).Error);
        Assert.Equal(KernelError.PlatformUnavailable, c.Memory.Refresh(c.OutputPlacement.PlacementId).Error);
        KernelResult<ExternalOperationSnapshot> completed = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate],
            () => published = true);

        Assert.False(completed.IsSuccess);
        Assert.False(published);
        Assert.Equal(0, c.Base.Provider.EventEffects);
        Assert.Equal(CxlMemoryPlacementState.MigrationRequired,
            c.Memory.Query(c.InputPlacement.PlacementId).Value!.State);
        Assert.Equal(ExternalOperationState.Submitted,
            c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!.State);
        Assert.True(c.Base.Kernel.Regions.Validate(c.Input.Handle, c.Owner).IsSuccess);
        KernelResult<CxlFabricBinding> currentFabric = c.Bridge.QueryFabric(c.Fabric.BindingId);
        Assert.True(currentFabric.IsSuccess, currentFabric.Message);
        Assert.False(c.Memory.Close(c.InputPlacement.PlacementId).IsSuccess);
        Assert.False(c.Memory.Close(c.OutputPlacement.PlacementId).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, c.Bridge.ReleaseFabric(currentFabric.Value!).Error);
        Assert.False(c.Base.Kernel.RevokePlatformDevice(c.Base.Owner, c.Device).IsSuccess);
        Assert.False(c.Base.Kernel.DestroySecureDomain(c.Base.Owner, c.Base.Secure.Domain,
            c.Base.Secure.ConfigureCapability).IsSuccess);
        Assert.False(c.Base.Kernel.DestroyVirtualDomain(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.ConfigureCapability).IsSuccess);
        Assert.False(c.Base.Kernel.TerminateProcess(c.Base.Owner).IsSuccess);
        Assert.False(c.Base.Kernel.QueryProcessTeardown(c.Base.Owner).Value.LocalReclaimCompleted);
        Assert.Equal(KernelError.PlatformBindingActive, c.Base.Kernel.Regions.ReserveBacking(c.Input.Handle, c.Owner).Error);
        Assert.Equal(KernelError.PlatformBindingActive, c.Base.Kernel.Regions.ReserveBacking(c.Output.Handle, c.Owner).Error);
    }

    [Fact]
    public void SecurityResetAfterDeviceCompleteBlocksVisibilityPublicationAndGuestEvent()
    {
        ComposedWorkload c = CreateComposedWorkload(security => new CompletionHookAccelerator(
            new CxlType2ModelAccelerator(),
            () => Assert.True(security.ResetSecuritySession(new("secure-virtual-type2")).IsSuccess)));
        bool published = false;
        KernelEventEndpoint guestEvent = c.Base.Kernel.CreateKernelEventEndpoint(c.Base.Owner).Value!;

        KernelResult<ExternalOperationSnapshot> completed = c.Service.CompleteVisiblePublishWithGuestEvent(c.Base.Owner, c.Execution,
            [c.Candidate], () => published = true, c.VirtualIo,
            c.Base.Virtual.EventCapability, guestEvent);

        Assert.False(completed.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, completed.Error);
        Assert.False(published);
        Assert.Equal(0, c.Base.Provider.EventEffects);
        ExternalOperationSnapshot operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.Equal(ExternalOperationState.Released, operation.State);
        Assert.NotEqual(ExternalOperationDisposition.Published, operation.Disposition);
        CloseComposedWorkload(c, c.Fabric);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void VirtualDeviceSourceRevokeAtCompletionVisibilityOrPublicationRefusesPublishedClaim(int phase)
    {
        ComposedWorkload? c = null;
        void Revoke()
        {
            CapabilityId source = c!.Base.Kernel.CapabilityAuthority.InspectionSnapshot()
                .Single(x => x.Descriptor.ResourceKind == ResourceKind.Device &&
                    x.Descriptor.ResourceId == "device:secure-virtual-type2").Descriptor.CapabilityId;
            Assert.True(c.Base.Kernel.CapabilityAuthority.Revoke(source).IsSuccess);
        }
        c = CreateComposedWorkload(_ => new CompletionHookAccelerator(new CxlType2ModelAccelerator(),
            () => { if (phase == 0) Revoke(); }, () => { if (phase == 1) Revoke(); }));
        bool published = false;
        c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = () => { if (phase == 3) Revoke(); };
        KernelResult<ExternalOperationSnapshot> result;
        try
        {
            result = c.Service.CompleteVisiblePublish(c.Base.Owner, c.Execution, [c.Candidate], () =>
            {
                published = true;
                if (phase == 2) Revoke();
            });
        }
        finally { c.Base.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = null; }
        Assert.False(result.IsSuccess);
        Assert.Equal(phase == 2, published);
        Assert.Equal(0, c.Base.Provider.EventEffects);
        ExternalOperationSnapshot operation = c.Base.Kernel.QueryExternalOperation(c.Base.Owner, c.Execution.Operation).Value!;
        Assert.NotEqual(ExternalOperationDisposition.Published, operation.Disposition);
        if (phase != 2)
        {
            Assert.DoesNotContain(operation.Transitions, transition => transition.Event == "PublicationEffectAmbiguous");
            Assert.Equal(ExternalOperationState.Released, operation.State);
            CloseComposedWorkload(c, c.Fabric);
        }
        else Assert.NotEqual(ExternalOperationState.Released, operation.State);
    }

    private static ComposedWorkload CreateComposedWorkload(
        Func<CxlSecurityModelProvider, ICxlType2AcceleratorProvider>? acceleratorFactory = null,
        Action<RuntimeKernel, CapabilityId, CapabilityId>? afterSubmit = null,
        Action<RuntimeKernel, CapabilityId, CapabilityId>? duringPreSubmitRevalidation = null,
        Action<RuntimeKernel, CapabilityId, CapabilityId>? duringIncarnation = null,
        Func<CxlType3ModelProvider, ICxlDiscoveryProvider>? discoveryFactory = null)
    {
        Scenario s = Create();
        SingProcess process = s.Kernel.Processes.Resolve(s.Owner).Value!;
        RegionOwner owner = new RegionOwner(process.DomainId, s.Owner.Generation);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(process.DomainId, s.Owner);
        CapabilityDescriptorV1 deviceCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.Device,
            "device:secure-virtual-type2", CapabilityRights.Read | CapabilityRights.Write |
            CapabilityRights.Configure).Value!;
        PlatformDeviceLease device = s.Kernel.BindPlatformDevice(s.Owner, s.Parent, deviceCapability.CapabilityId,
            PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure).Value!;
        CxlType3ModelProvider model = new CxlType3ModelProvider();
        Assert.True(model.RegisterEndpoint(new("secure-virtual-type2"), device.Device, 4096).IsSuccess);
        CxlEndpointSnapshot endpoint = model.QueryEndpoint(new("secure-virtual-type2")).Value!;
        CxlSecurityModelProvider security = new CxlSecurityModelProvider();
        CxlSecurityProperties securityProperties = CxlSecurityProperties.IdeEnabled | CxlSecurityProperties.DeviceAuthenticated;
        Assert.True(security.Register(endpoint.EndpointId, endpoint.DeviceGeneration, securityProperties).IsSuccess);
        CxlAuthorityBridge bridge = new CxlAuthorityBridge(s.Kernel, discoveryFactory?.Invoke(model) ?? model, model, model, model,
            new UnsupportedCoherentProvider(), security);
        CxlType3MemoryAuthority memory = new CxlType3MemoryAuthority(s.Kernel, bridge);
        OwnedBuffer<byte> input = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        OwnedBuffer<byte> output = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        CxlMemoryPlacementSnapshot inputPlacement = memory.Place(owner, input.Handle, subject, device, endpoint,
            CxlMemoryPersistence.Volatile).Value!;
        CxlMemoryPlacementSnapshot outputPlacement = memory.Place(owner, output.Handle, subject, device, endpoint,
            CxlMemoryPersistence.Volatile).Value!;
        CapabilityId inputCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(input.Handle.RegionId), CapabilityRights.Map |
            CapabilityRights.Read).Value!.CapabilityId;
        CapabilityId outputCapability = s.Kernel.MintCapability(process.DomainId, s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(output.Handle.RegionId), CapabilityRights.Map |
            CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        GuestRegionMapping inputGuest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability,
            inputCapability, input.Handle, new(0, 16), GuestMemoryAccess.Read).Value!;
        GuestRegionMapping outputGuest = s.Kernel.MapGuestRegion(s.Owner, s.Virtual.Domain, s.Virtual.MemoryCapability,
            outputCapability, output.Handle, new(16, 16), GuestMemoryAccess.Read |
            GuestMemoryAccess.Write).Value!;
        SecureExecutionBinding secureExecution = Bind(s).Value;
        SecureGuestRegionBinding inputSecure = s.Kernel.BindSecureGuestRegion(s.Owner, secureExecution, inputGuest.Mapping,
            PlatformSecureRegionClass.Private).Value;
        SecureGuestRegionBinding outputSecure = s.Kernel.BindSecureGuestRegion(s.Owner, secureExecution, outputGuest.Mapping,
            PlatformSecureRegionClass.Private).Value;
        VirtualIoBinding virtualIo = s.Kernel.BindVirtualIo(s.Owner, s.Virtual.Domain, device,
            new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 4096), secureExecution).Value;
        OwnedBuffer<byte> control = s.Kernel.AllocateBuffer<byte>(s.Owner, 16).Value!;
        RegionUseDescriptor controlUse = s.Kernel.AcquireRegionUse(s.Owner, control.Handle, RegionUseMode.DevicePrivate,
            new(0, 16)).Value!;
        CxlFabricBinding fabric = bridge.BindFabric(owner, controlUse.Handle, subject, device,
            new(endpoint.EndpointId, endpoint.DeviceGeneration, 16, CxlMemoryPersistence.Volatile,
                CxlMemorySharing.Exclusive)).Value!;
        ComputeProviderCandidate candidate = new ComputeProviderCandidate(new("secure-virtual-type2"), 1,
            ComputeProviderCapabilities.AcceleratorExecution | ComputeProviderCapabilities.StagedPublication |
            ComputeProviderCapabilities.SecureComputeEvidence | ComputeProviderCapabilities.VirtualizedDomain,
            4096, 1, 1, true, false);
        ComputeIntent intent = new ComputeIntent(ComputeOperationKind.Copy,
            new(input.Handle, new(0, 16)), new(output.Handle, new(0, 16)),
            ComputePublicationPreference.StagedRequired, RequiresSecureEvidence: true,
            RequiresVirtualizedDomain: true);
        VirtualComputeContext context = s.Kernel.CreateVirtualComputeContext(s.Owner, s.Virtual.Domain,
            inputGuest.Mapping, outputGuest.Mapping, virtualIo, secureExecution).Value;
        ComputePlan plan = s.Kernel.PlanVirtualizedCompute(s.Owner, intent, new(true, true, true),
            [candidate], context).Value!;
        ICxlType2AcceleratorProvider accelerator = acceleratorFactory?.Invoke(security) ?? new CxlType2ModelAccelerator();
        if (afterSubmit is not null)
            accelerator = new CompletionHookAccelerator(accelerator, () => { },
                afterSubmit: () => afterSubmit(s.Kernel, inputCapability, outputCapability));
        CxlFabricManagerAuthority manager = new CxlFabricManagerAuthority(s.Kernel, model);
        Assert.True(manager.Register(fabric).IsSuccess);
        CxlType2AcceleratorService service = new CxlType2AcceleratorService(s.Kernel, bridge, accelerator, manager);
        CxlSecurityPolicy policy = new CxlSecurityPolicy(securityProperties, new(1), RequiredForOperation: true);
        if (duringPreSubmitRevalidation is not null)
            s.Provider.DuringRevalidation = () => duringPreSubmitRevalidation(s.Kernel, inputCapability, outputCapability);
        if (duringIncarnation is not null)
            s.Provider.DuringIncarnation = () => duringIncarnation(s.Kernel, inputCapability, outputCapability);
        KernelResult<CxlType2Execution> submissionResult = service.SubmitVirtualized(s.Owner, plan, [candidate], context, subject, device,
            endpoint, fabric, 1, securityAuthority: new(bridge, security), securityPolicy: policy);
        s.Provider.DuringRevalidation = null;
        s.Provider.DuringIncarnation = null;
        CxlType2Execution execution = submissionResult.Value!;
        return new(s, owner, subject, device, model, endpoint, security, bridge, memory, input, output,
            inputPlacement, outputPlacement, inputGuest, outputGuest, inputSecure, outputSecure,
            secureExecution, virtualIo, controlUse, fabric, candidate, context, accelerator, manager,
            service, execution, inputCapability, outputCapability, submissionResult);
    }

    private static void CloseComposedWorkload(ComposedWorkload c, CxlFabricBinding currentFabric)
    {
        CloseComposedGuestAuthority(c);
        Assert.True(c.Memory.Close(c.InputPlacement.PlacementId).IsSuccess);
        Assert.True(c.Memory.Close(c.OutputPlacement.PlacementId).IsSuccess);
        Assert.True(c.Bridge.ReleaseFabric(currentFabric).IsSuccess);
        Assert.True(c.Base.Kernel.ReleaseRegionUse(c.Base.Owner, c.ControlUse.Handle).IsSuccess);
        Assert.True(c.Base.Kernel.DestroySecureDomain(c.Base.Owner, c.Base.Secure.Domain,
            c.Base.Secure.ConfigureCapability).IsSuccess);
        Assert.True(c.Base.Kernel.RevokePlatformDevice(c.Base.Owner, c.Device).IsSuccess);
        Assert.True(c.Base.Kernel.DestroyVirtualDomain(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.ConfigureCapability).IsSuccess);
        KernelResult terminated = c.Base.Kernel.TerminateProcess(c.Base.Owner);
        Assert.True(terminated.IsSuccess, terminated.Message);
    }

    private static void CloseComposedGuestAuthority(ComposedWorkload c)
    {
        Assert.True(c.Base.Kernel.CloseVirtualIo(c.VirtualIo).IsSuccess);
        Assert.True(c.Base.Kernel.CloseSecureGuestRegion(c.InputSecure).IsSuccess);
        Assert.True(c.Base.Kernel.CloseSecureGuestRegion(c.OutputSecure).IsSuccess);
        Assert.True(c.Base.Kernel.CloseGuestRegionMapping(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.MemoryCapability, c.InputGuest.Mapping).IsSuccess);
        Assert.True(c.Base.Kernel.CloseGuestRegionMapping(c.Base.Owner, c.Base.Virtual.Domain,
            c.Base.Virtual.MemoryCapability, c.OutputGuest.Mapping).IsSuccess);
        Assert.True(c.Base.Kernel.CloseSecureExecution(c.SecureExecution).IsSuccess);
    }

    private sealed record ComposedWorkload(Scenario Base, RegionOwner Owner,
        PlatformDomainIdentity Subject, PlatformDeviceLease Device, CxlType3ModelProvider Model,
        CxlEndpointSnapshot Endpoint, CxlSecurityModelProvider Security, CxlAuthorityBridge Bridge,
        CxlType3MemoryAuthority Memory, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output,
        CxlMemoryPlacementSnapshot InputPlacement, CxlMemoryPlacementSnapshot OutputPlacement,
        GuestRegionMapping InputGuest, GuestRegionMapping OutputGuest,
        SecureGuestRegionBinding InputSecure, SecureGuestRegionBinding OutputSecure,
        SecureExecutionBinding SecureExecution, VirtualIoBinding VirtualIo,
        RegionUseDescriptor ControlUse, CxlFabricBinding Fabric, ComputeProviderCandidate Candidate,
        VirtualComputeContext Context, ICxlType2AcceleratorProvider Accelerator,
        CxlFabricManagerAuthority Manager, CxlType2AcceleratorService Service,
        CxlType2Execution Execution, CapabilityId InputCapability, CapabilityId OutputCapability,
        KernelResult<CxlType2Execution> SubmissionResult);

    private sealed class CompletionHookAccelerator(ICxlType2AcceleratorProvider inner,
        Action afterCompletion, Action? afterVisibility = null, Action? afterSubmit = null) : ICxlType2AcceleratorProvider
    {
        public PlatformAuthorityResult<CxlAcceleratorSubmission> Submit(CxlAcceleratorRequest request)
        {
            PlatformAuthorityResult<CxlAcceleratorSubmission> result = inner.Submit(request);
            afterSubmit?.Invoke();
            return result;
        }
        public PlatformAuthorityResult<CxlAcceleratorCompletion> ObserveCompletion(
            CxlAcceleratorSubmission submission)
        {
            PlatformAuthorityResult<CxlAcceleratorCompletion> result = inner.ObserveCompletion(submission);
            if (result.IsSuccess) afterCompletion();
            return result;
        }
        public PlatformAuthorityResult<CxlAcceleratorVisibility> AcquireVisibility(
            CxlAcceleratorSubmission submission, ExternalVisibilityRequirement requirement)
        {
            PlatformAuthorityResult<CxlAcceleratorVisibility> result = inner.AcquireVisibility(submission, requirement);
            if (result.IsSuccess) afterVisibility?.Invoke();
            return result;
        }
        public PlatformAuthorityResult Cancel(CxlAcceleratorSubmission submission) => inner.Cancel(submission);
        public PlatformAuthorityResult Release(CxlAcceleratorSubmission submission) => inner.Release(submission);
    }

    private static KernelResult<SecureExecutionBinding> Bind(Scenario s) => s.Kernel.BindSecureExecution(s.Owner,
        s.Virtual.Domain, s.Virtual.ConfigureCapability, s.Secure.Domain, s.Secure.ConfigureCapability);

    private static Scenario Create(Action<FakeProvider>? configure = null)
    {
        FakeProvider provider = new FakeProvider(); RuntimeKernel kernel = new RuntimeKernel(provider);
        provider.CheckLocks = () => {
            foreach (string? name in new[] { "_secureExecutionGate", "_platformMemoryUseGate", "_secureGuestRegionGate" })
                Assert.False(Monitor.IsEntered(typeof(RuntimeKernel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!));
            Assert.False(Monitor.IsEntered(typeof(CapabilityAuthority).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.CapabilityAuthority)!));
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!));
        };
        configure?.Invoke(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 980, 981);
        CapabilityId vc = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        KernelResult<VirtualDomainAuthoritySet> vm = kernel.CreateVirtualDomain(owner, vc, new(1, 4096));
        Assert.True(vm.IsSuccess, vm.Message);
        Assert.True(kernel.ConfigureVirtualDomain(owner, vm.Value.Domain, vm.Value.ConfigureCapability).IsSuccess);
        // Reuse the actual VM parent; do not create a parallel domain authority.
        PlatformProviderDomainLease parent = provider.Parent;
        PlatformDomainBinding localParent = new PlatformDomainBinding(new(1), new(1), parent.Subject);
        CapabilityId sc = kernel.MintCapability(process.DomainId, owner, ResourceKind.SecureCompute,
            SecureComputeResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        KernelResult<SecureDomainAuthoritySet> secure = kernel.CreateSecureDomain(owner, sc, localParent, new([SecureProperty.PrivateMemory], 4096));
        Assert.True(secure.IsSuccess, secure.Message);
        return new(kernel, provider, owner, vm.Value, secure.Value!, localParent);
    }

    private sealed record Scenario(RuntimeKernel Kernel, FakeProvider Provider, ProcessHandle Owner,
        VirtualDomainAuthoritySet Virtual, SecureDomainAuthoritySet Secure, PlatformDomainBinding Parent);

    // Deterministic test contract only. This is not a production capability claim.
    private sealed class FakeProvider : IPlatformAuthorityProvider, IPlatformFeatureProvider,
        IPlatformChildDomainProvider, IPlatformGuestMemoryProvider, IPlatformSecureComputeProvider,
        IPlatformVirtualEventProvider, IPlatformVirtualIoProvider, IPlatformDeviceLeaseProvider,
        IPlatformRegionRevocationProvider, IPlatformCompletionProvider,
        ISecureExecutionProvider, IPlatformProviderIncarnationSource
    {
        private readonly HostPlatformAuthorityProvider _host = new();
        public PlatformProviderDescriptor Descriptor => _host.Descriptor;
        public Action? DuringIncarnation;
        public PlatformProviderIncarnation CurrentIncarnation
        {
            get { DuringIncarnation?.Invoke(); return new(1); }
        }
        public PlatformProviderDomainLease Parent;
        private uint _secureExecutionContractVersion = 1;
        private bool _productionSecureExecution = true;
        public uint SecureExecutionContractVersion
        {
            get { CheckLocks?.Invoke(); DuringCapabilityQuery?.Invoke(); return _secureExecutionContractVersion; }
            set => _secureExecutionContractVersion = value;
        }
        public bool ProductionSecureExecution
        {
            get { CheckLocks?.Invoke(); DuringCapabilityQuery?.Invoke(); return _productionSecureExecution; }
            set => _productionSecureExecution = value;
        }
        public int AdmissionFault = -1, RevalidationFault = -1, ClosureFault = -1;
        public int BindCalls, CloseCalls, RevalidateCalls, SecureCloses, ChildCloses, ChildEffects, SecureRegionBindCalls;
        public bool Contained, ReplayCorrelation, SecureRegionClosureFault;
        public Action? DuringClose { get; set; }
        public Func<SecureExecutionClosure, SecureExecutionClosure>? ClosureTransform { get; set; }
        public Action? DuringBind, DuringEvent, DuringChildEffect, CheckLocks, DuringRevalidation;
        public Action? DuringCapabilityQuery;
        public Action? DuringVirtualIoBind;
        public Action? DuringSecureRegionBind;
        public SecureExecutionReceipt? LastCloseReceipt;
        public int EventEffects;
        public SecureExecutionRequest LastRequest, LastCloseRequest;
        public List<string> TerminalOrder = [];
        public List<string> OverlayCloseOrder = [];
        public PlatformProviderRegionMappingLease LastGuestParent, LastSecureParent;
        private ulong _nextGuest = 1;
        public PlatformFeatureManifest QueryFeatures() => new(_host.QueryFeatures().Features.Concat(new[] {
            new PlatformFeatureDescriptor(PlatformFeatureFamily.ChildDomainLifecycle, PlatformChildDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.ChildGuestMemory, PlatformGuestMemoryContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.ChildEventDelivery, 1, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.BoundedVirtualIo, 1, PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.SecureDomains, 1, PlatformFeatureAvailability.ProductionSecure) }).ToArray());
        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
        { PlatformAuthorityResult<PlatformProviderDomainLease> result = _host.BindDomain(subject); if (result.IsSuccess) Parent = result.Value; return result; }
        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) => _host.RevokeDomain(lease);
        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease lease, PlatformRegionIdentity region, PlatformMemoryAccess access)
        {
            PlatformAuthorityResult<PlatformProviderRegionMappingLease> result = _host.MapOwnedRegion(lease, region, access);
            if (result.IsSuccess) LastGuestParent = result.Value;
            return result;
        }
        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy) => _host.RevokeRegionMapping(mapping, policy);
        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
            PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy)
        {
            PlatformAuthorityResult revoked = _host.RevokeRegionMapping(mapping, policy);
            return revoked.IsSuccess
                ? PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(new(mapping.MappingId,
                    mapping.Generation, new(new(_nextGuest++), new(1), mapping.DomainLease)))
                : PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(revoked.Status,
                    revoked.Message ?? "mapping revocation failed");
        }
        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(PlatformOperationIdentity operation) =>
            PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(new(operation.OperationId, operation.Generation,
                operation.DomainLease, PlatformCompletionState.Closed));
        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease domain,
            PlatformDeviceIdentity device, PlatformDeviceRights rights) =>
            PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(new(new(1), new(1), domain, device, rights));
        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease) => PlatformAuthorityResult.Ok();
        public PlatformAuthorityResult<PlatformProviderChildDomainLease> CreateChildDomain(PlatformProviderDomainLease parent, PlatformChildDomainIntent intent) => PlatformAuthorityResult<PlatformProviderChildDomainLease>.Ok(new(new(1), new(1), parent, intent));
        public PlatformAuthorityResult TransitionChildDomain(PlatformProviderChildDomainLease lease, PlatformChildDomainTransition transition) { if (transition != PlatformChildDomainTransition.BeginDrain) { ChildEffects++; DuringChildEffect?.Invoke(); } return PlatformAuthorityResult.Ok(); }
        public PlatformAuthorityResult<PlatformVirtualEventReceipt> InjectVirtualEvent(PlatformVirtualEventRequest request)
        {
            EventEffects++; DuringEvent?.Invoke();
            PlatformProviderChildDomainLease child = request.ChildLease;
            return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Ok(new(child.LeaseId, child.Generation,
                child.ParentDomainLease.LeaseId, child.ParentDomainLease.Generation, (ulong)EventEffects, request.EventClass, request.SourceResourceId));
        }
        public PlatformAuthorityResult<PlatformChildDomainClosureReceipt> CloseChildDomain(PlatformProviderChildDomainLease lease)
        { ChildCloses++; TerminalOrder.Add("child"); return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation, lease.ParentDomainLease.LeaseId, lease.ParentDomainLease.Generation, PlatformChildDomainClosureDisposition.Closed)); }
        public PlatformAuthorityResult<PlatformProviderVirtualIoLease> BindVirtualIo(PlatformVirtualIoRequest request)
        {
            CheckLocks?.Invoke(); DuringVirtualIoBind?.Invoke();
            return PlatformAuthorityResult<PlatformProviderVirtualIoLease>.Ok(new(new(_nextGuest++), new(1),
                request.ChildLease, request.ParentDeviceLease, request.Profile));
        }
        public int VirtualIoCloseCalls;
        public int VirtualIoClosureFault = -1;
        public Action? DuringVirtualIoClose;
        public PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> RevokeVirtualIo(PlatformProviderVirtualIoLease lease)
        {
            CheckLocks?.Invoke(); VirtualIoCloseCalls++; DuringVirtualIoClose?.Invoke();
            if (VirtualIoClosureFault == 0) return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Fail(PlatformAuthorityStatus.Unavailable, "unknown original Close");
            if (VirtualIoClosureFault == 1) throw new OperationCanceledException("lost reply");
            return PlatformAuthorityResult<PlatformVirtualIoClosureReceipt>.Ok(new(VirtualIoClosureFault == 3 ? new(999) : lease.LeaseId, lease.Generation,
                lease.ChildLease.LeaseId, lease.ChildLease.Generation, lease.ChildLease.ParentDomainLease.LeaseId,
                lease.ChildLease.ParentDomainLease.Generation, VirtualIoClosureFault != 2));
        }
        public PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> MapGuestRegion(PlatformGuestRegionMappingRequest request) =>
            PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Ok(new(new(_nextGuest++), new(1), request.ChildLease, request.ParentMapping.Lease, request.GuestRange, request.Access));
        public PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt> UnmapGuestRegion(PlatformProviderGuestRegionMappingLease lease)
        {
            OverlayCloseOrder.Add("guest");
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Ok(new(lease.LeaseId, lease.Generation,
                lease.ChildLease.LeaseId, lease.ChildLease.Generation, lease.ChildLease.ParentDomainLease.LeaseId,
                lease.ChildLease.ParentDomainLease.Generation, true));
        }
        public IReadOnlyList<SecureProperty>? SecureProperties { get; set; }
        public PlatformAuthorityResult<PlatformProviderSecureDomainLease> CreateSecureDomain(PlatformSecureDomainRequest request) => PlatformAuthorityResult<PlatformProviderSecureDomainLease>.Ok(new(new(1), new(1), request.Parent, SecureProperties ?? request.Profile.RequiredProperties.ToArray()));
        public PlatformAuthorityResult<PlatformProviderSecureRegionBinding> BindSecureRegion(PlatformProviderSecureDomainLease domain, PlatformProviderRegionMappingLease mapping, PlatformSecureRegionClass regionClass)
        { CheckLocks?.Invoke(); SecureRegionBindCalls++; LastSecureParent = mapping; DuringSecureRegionBind?.Invoke(); return PlatformAuthorityResult<PlatformProviderSecureRegionBinding>.Ok(new(new(1), new(1), domain, mapping, regionClass)); }
        public PlatformAuthorityResult<PlatformSecureRegionClosureReceipt> UnbindSecureRegion(PlatformProviderSecureRegionBinding binding)
        {
            CheckLocks?.Invoke();
            OverlayCloseOrder.Add("secure");
            return SecureRegionClosureFault
                ? PlatformAuthorityResult<PlatformSecureRegionClosureReceipt>.Fail(PlatformAuthorityStatus.Unavailable, "secure overlay close outcome is ambiguous")
                : PlatformAuthorityResult<PlatformSecureRegionClosureReceipt>.Ok(new(binding, true));
        }
        public PlatformAuthorityResult<PlatformSecureDomainTransitionReceipt> TransitionSecureDomain(PlatformProviderSecureDomainLease domain, PlatformSecureDomainTransition transition) => PlatformAuthorityResult<PlatformSecureDomainTransitionReceipt>.Ok(new(domain, transition, true));
        public PlatformAuthorityResult<PlatformSecureDomainClosureReceipt> RevokeSecureDomain(PlatformProviderSecureDomainLease domain)
        { SecureCloses++; TerminalOrder.Add("secure"); return PlatformAuthorityResult<PlatformSecureDomainClosureReceipt>.Ok(new(domain, true)); }
        public PlatformAuthorityResult<SecureExecutionReceipt> BindSecureExecution(SecureExecutionRequest request)
        {
            CheckLocks?.Invoke(); BindCalls++; LastRequest = request; DuringBind?.Invoke();
            if (AdmissionFault == 5) return PlatformAuthorityResult<SecureExecutionReceipt>.Fail(PlatformAuthorityStatus.Unavailable, "lost response after materialization");
            if (AdmissionFault == 6) throw new OperationCanceledException("lost admission response");
            SecureExecutionReceipt receipt = new SecureExecutionReceipt(request, ReplayCorrelation ? 1 : request.Binding.Id, 1, 1, 1, 1, true);
            receipt = AdmissionFault switch {
                0 => receipt with { Request = request with { Binding = new(999, 1) } },
                1 => receipt with { PolicyGeneration = 0 }, 2 => receipt with { ProtectionGeneration = 0 },
                3 => receipt with { ProductionSecure = false }, 4 => receipt with { ContractVersion = 2 }, _ => receipt };
            return PlatformAuthorityResult<SecureExecutionReceipt>.Ok(receipt);
        }
        public PlatformAuthorityResult<SecureExecutionReceipt> RevalidateSecureExecution(SecureExecutionReceipt receipt)
        {
            CheckLocks?.Invoke(); RevalidateCalls++;
            DuringRevalidation?.Invoke();
            if (RevalidationFault == 4) return PlatformAuthorityResult<SecureExecutionReceipt>.Fail(PlatformAuthorityStatus.Revoked, "secure lease missing");
            return PlatformAuthorityResult<SecureExecutionReceipt>.Ok(RevalidationFault switch {
                0 => receipt with { PolicyGeneration = 2 }, 1 => receipt with { ProtectionGeneration = 2 },
                2 => receipt with { ProviderGeneration = 2 }, 3 => receipt with { Request = receipt.Request with {
                    Context = receipt.Request.Context with { ChildLease = receipt.Request.Context.ChildLease with { Generation = new(2) } } } }, _ => receipt });
        }
        public PlatformAuthorityResult<SecureExecutionClosure> CloseSecureExecution(SecureExecutionRequest request, SecureExecutionReceipt? receipt)
        {
            CheckLocks?.Invoke(); CloseCalls++; LastCloseRequest = request; LastCloseReceipt = receipt;
            DuringClose?.Invoke();
            if (ClosureFault == 0) return PlatformAuthorityResult<SecureExecutionClosure>.Fail(PlatformAuthorityStatus.Unavailable, "disconnect");
            if (ClosureFault == 1) return PlatformAuthorityResult<SecureExecutionClosure>.Fail(PlatformAuthorityStatus.Revoked, "not found");
            if (ClosureFault == 2) throw new OperationCanceledException();
            if (ClosureFault == 3) return PlatformAuthorityResult<SecureExecutionClosure>.Ok(new(request, receipt, false, false));
            if (ClosureFault == 4) return PlatformAuthorityResult<SecureExecutionClosure>.Ok(new(request with { Binding = new(999, 1) }, receipt, true, false));
            if (ClosureFault == 5) throw new TimeoutException();
            if (ClosureFault == 6) return PlatformAuthorityResult<SecureExecutionClosure>.Fail((PlatformAuthorityStatus)999, "unknown outcome");
            TerminalOrder.Add("execution");
            SecureExecutionClosure closure = new SecureExecutionClosure(request, receipt, !Contained, Contained);
            return PlatformAuthorityResult<SecureExecutionClosure>.Ok(ClosureTransform?.Invoke(closure) ?? closure);
        }
    }

    private sealed class UnsupportedCoherentProvider : ICxlCoherentAccessProvider
    {
        public PlatformAuthorityResult<CxlCoherentBinding> BindCoherentAccess(CxlFabricBinding binding,
            RegionUseDescriptor use) => Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult<CxlCoherentBinding> QueryCoherentAccess(CxlCoherentBindingId id) =>
            Fail<CxlCoherentBinding>();
        public PlatformAuthorityResult ReleaseCoherentAccess(CxlCoherentBinding binding) =>
            PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
        private static PlatformAuthorityResult<T> Fail<T>() =>
            PlatformAuthorityResult<T>.Fail(PlatformAuthorityStatus.Unsupported, "unsupported");
    }
}
