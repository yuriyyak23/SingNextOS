using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip.Drivers;

namespace SingPlus.Tests.Drivers;

public sealed class DriverComponentVerticalSliceTests
{
    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    [InlineData(false, 3)] [InlineData(true, 3)]
    public void ControllerChildLateRefusalKeepsOriginalCleanupAndPinsGenericFailure(bool irq, int fault)
    {
        var status = fault == 1 ? PlatformAuthorityStatus.Denied : PlatformAuthorityStatus.NotAccepted;
        var provider = new DriverProvider
        {
            MmioRevokeStatus = irq ? null : status,
            IrqRevokeStatus = irq ? status : null
        };
        var kernel = new RuntimeKernel(provider);
        provider.BeforeChildRevoke = () =>
        {
            if (fault == 2) throw new InvalidOperationException("Missing exact cleanup reply.");
            if (fault == 3) kernel.PlatformAuthority.ObserveBackendReset();
        };
        var (_, principal) = TestFixtures.Create(kernel, 1928, 19280);
        Assert.True(kernel.AdmitProcess(principal).IsSuccess);
        var control = kernel.MintCapability(new DomainId(19280), principal, ResourceKind.KernelService,
            CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;
        var setup = DriverSetup.Create(1929, 19290);
        Action lose = () => Assert.True(kernel.CapabilityAuthority.Revoke(control).IsSuccess);
        if (irq) provider.IrqBindCallback = lose;
        else provider.MmioBindCallback = lose;
        Assert.False(kernel.AdmitComponent(setup.Plan(new DomainId(19290)), principal, control).IsSuccess);
        var pinned = kernel.QueryComponent(setup.Identity).Value!;
        Assert.False(pinned.Reclaimable);
        Assert.False(kernel.Processes.ResolveTerminalState(pinned.Process).IsSuccess);
        Assert.True(kernel.Budgets.HasProcessAccount(pinned.Process));
        Assert.False(kernel.RetireReclaimableComponent(setup.Identity, pinned.Process).IsSuccess);
        provider.MmioRevokeStatus = null;
        provider.IrqRevokeStatus = null;
        provider.BeforeChildRevoke = null;
        var (_, unrelated) = TestFixtures.Create(kernel, 1930, 19300);
        Assert.True(kernel.TerminateProcess(unrelated).IsSuccess);
        Assert.DoesNotContain(irq ? "revoke-irq" : "revoke-mmio", provider.Log);
        Assert.False(kernel.Processes.ResolveTerminalState(pinned.Process).IsSuccess);
        Assert.True(kernel.DrainComponent(setup.Identity).IsSuccess);
        var closed = false;
        Assert.Equal(closed, kernel.QueryComponent(setup.Identity).Value!.Reclaimable);
        Assert.Equal(closed, kernel.Processes.ResolveTerminalState(pinned.Process).IsSuccess);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.Equal(1, provider.MmioBindCalls);
        Assert.Equal(irq ? 1 : 0, provider.IrqBindCalls);
        Assert.Equal(closed ? 1 : 0, provider.Log.Count(value => value == (irq ? "revoke-irq" : "revoke-mmio")));
        Assert.Equal(closed ? 1 : 0, provider.Log.Count(value => value == "revoke-device"));
        Assert.Equal(closed ? 1 : 0, provider.Log.Count(value => value == "revoke-domain"));
        Assert.Equal(closed, kernel.RetireReclaimableComponent(setup.Identity, pinned.Process).IsSuccess);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    public void ControllerIrqLossStopsAllocationOrCleansExactEndpointAndBinding(int stage, int fault)
    {
        var provider = new DriverProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, principal) = TestFixtures.Create(kernel, 1926, 19260);
        Assert.True(kernel.AdmitProcess(principal).IsSuccess);
        var control = kernel.MintCapability(new DomainId(19260), principal, ResourceKind.KernelService,
            CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;
        var setup = DriverSetup.Create(1927, 19270);
        var created = false;
        Action lose = () =>
        {
            if (fault == 0) Assert.True(kernel.CapabilityAuthority.Revoke(control).IsSuccess);
            if (fault == 1) kernel.CapabilityAuthority.RevokeAllForDomain(new DomainId(19260));
            if (fault == 2) Assert.True(kernel.TerminateProcess(principal).IsSuccess);
        };
        kernel.BeforeComponentDriverIrqBindingQualificationHook = endpoint =>
        {
            created = true;
            if (stage == 1) lose();
        };
        if (stage == 0) kernel.BeforeComponentDriverIrqEndpointQualificationHook = lose;
        if (stage == 2) provider.IrqBindCallback = lose;
        Assert.Equal(KernelError.SupervisorDenied, kernel.AdmitComponent(setup.Plan(new DomainId(19270)), principal, control).Error);
        Assert.Equal(stage != 0, created);
        Assert.Equal(stage == 2 ? 1 : 0, provider.IrqBindCalls);
        Assert.Equal(stage == 2 ? 1 : 0, provider.Log.Count(value => value == "revoke-irq"));
        Assert.Equal(1, provider.Log.Count(value => value == "revoke-mmio"));
        Assert.Equal(1, provider.Log.Count(value => value == "revoke-device"));
        var component = kernel.QueryComponent(setup.Identity).Value!;
        Assert.Equal(ComponentLifecycleState.Reclaimable, component.State);
        Assert.True(kernel.Processes.ResolveTerminalState(component.Process).IsSuccess);
        Assert.True(kernel.RetireReclaimableComponent(setup.Identity, component.Process).IsSuccess);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    public void ControllerMmioLossStopsFurtherEffectsAndCleansExactLease(bool late, int fault)
    {
        var provider = new DriverProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, principal) = TestFixtures.Create(kernel, 1924, 19240);
        Assert.True(kernel.AdmitProcess(principal).IsSuccess);
        var control = kernel.MintCapability(new DomainId(19240), principal, ResourceKind.KernelService,
            CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;
        var setup = DriverSetup.Create(1925, 19250);
        Action lose = () =>
        {
            if (fault == 0) Assert.True(kernel.CapabilityAuthority.Revoke(control).IsSuccess);
            if (fault == 1) kernel.CapabilityAuthority.RevokeAllForDomain(new DomainId(19240));
            if (fault == 2) Assert.True(kernel.TerminateProcess(principal).IsSuccess);
        };
        if (late) provider.MmioBindCallback = lose;
        else kernel.BeforeComponentDriverMmioQualificationHook = lose;
        Assert.Equal(KernelError.SupervisorDenied, kernel.AdmitComponent(setup.Plan(new DomainId(19250)), principal, control).Error);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.Equal(late ? 1 : 0, provider.MmioBindCalls);
        Assert.Equal(late ? 1 : 0, provider.Log.Count(value => value == "revoke-mmio"));
        Assert.Equal(1, provider.Log.Count(value => value == "revoke-device"));
        Assert.Equal(0, provider.IrqBindCalls);
        var component = kernel.QueryComponent(setup.Identity).Value!;
        Assert.Equal(ComponentLifecycleState.Reclaimable, component.State);
        Assert.True(kernel.Processes.ResolveTerminalState(component.Process).IsSuccess);
        Assert.True(kernel.RetireReclaimableComponent(setup.Identity, component.Process).IsSuccess);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    public void ControllerDeviceLossRefusesEarlyOrCleansExactReturnedLease(bool late, int fault)
    {
        var provider = new DriverProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, principal) = TestFixtures.Create(kernel, 1920, 19200);
        Assert.True(kernel.AdmitProcess(principal).IsSuccess);
        var control = kernel.MintCapability(new DomainId(19200), principal, ResourceKind.KernelService,
            CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;
        var setup = DriverSetup.Create(1921, 19210);
        Action lose = () =>
        {
            if (fault == 0) Assert.True(kernel.CapabilityAuthority.Revoke(control).IsSuccess);
            if (fault == 1) kernel.CapabilityAuthority.RevokeAllForDomain(new DomainId(19200));
            if (fault == 2) Assert.True(kernel.TerminateProcess(principal).IsSuccess);
        };
        if (late) provider.DeviceBindCallback = lose;
        else kernel.BeforeComponentDriverDeviceQualificationHook = lose;
        Assert.Equal(KernelError.SupervisorDenied, kernel.AdmitComponent(setup.Plan(new DomainId(19210)), principal, control).Error);
        Assert.Equal(late ? 1 : 0, provider.DeviceBindCalls);
        Assert.Equal(late ? 1 : 0, provider.Log.Count(value => value == "revoke-device"));
        Assert.Equal(0, provider.MmioBindCalls);
        Assert.Equal(0, provider.IrqBindCalls);
        var component = kernel.QueryComponent(setup.Identity).Value!;
        Assert.Equal(ComponentLifecycleState.Reclaimable, component.State);
        Assert.True(kernel.Processes.ResolveTerminalState(component.Process).IsSuccess);
        Assert.True(kernel.RetireReclaimableComponent(setup.Identity, component.Process).IsSuccess);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.NotAccepted)]
    [InlineData(PlatformAuthorityStatus.Denied)]
    public void ControllerDeviceLateRefusalPinsGenericFailedCleanupWithoutRebind(PlatformAuthorityStatus status)
    {
        var provider = new DriverProvider { DeviceRevokeStatus = status };
        var kernel = new RuntimeKernel(provider);
        var (_, principal) = TestFixtures.Create(kernel, 1922, 19220);
        Assert.True(kernel.AdmitProcess(principal).IsSuccess);
        var control = kernel.MintCapability(new DomainId(19220), principal, ResourceKind.KernelService,
            CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;
        var setup = DriverSetup.Create(1923, 19230);
        provider.DeviceBindCallback = () => Assert.True(kernel.CapabilityAuthority.Revoke(control).IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, kernel.AdmitComponent(setup.Plan(new DomainId(19230)), principal, control).Error);
        var pinned = kernel.QueryComponent(setup.Identity).Value!;
        Assert.False(pinned.Reclaimable);
        Assert.True(kernel.Budgets.HasProcessAccount(pinned.Process));
        Assert.False(kernel.Processes.ResolveTerminalState(pinned.Process).IsSuccess);
        Assert.False(kernel.RetireReclaimableComponent(setup.Identity, pinned.Process).IsSuccess);
        provider.DeviceRevokeStatus = null;
        Assert.True(kernel.DrainComponent(setup.Identity).IsSuccess);
        Assert.Equal(1, provider.DeviceBindCalls);
        var closed = false;
        Assert.Equal(closed, kernel.QueryComponent(setup.Identity).Value!.Reclaimable);
        Assert.Equal(closed, kernel.Processes.ResolveTerminalState(pinned.Process).IsSuccess);
        Assert.True(kernel.Budgets.HasProcessAccount(pinned.Process));
        Assert.Equal(closed ? 1 : 0, provider.Log.Count(value => value == "revoke-device"));
        Assert.Equal(closed ? 1 : 0, provider.Log.Count(value => value == "revoke-domain"));
        Assert.Equal(closed, kernel.RetireReclaimableComponent(setup.Identity, pinned.Process).IsSuccess);
    }

    [Fact]
    public void ObserveUsesExactTerminalReceiptWhenActualCleanupRemovesTransientRecordDuringLookup()
    {
        var provider = new DriverProvider { ThrowSourceAfterDeviceRevoke = true };
        var kernel = new RuntimeKernel(provider);
        _ = TestFixtures.Create(kernel, 1910, 19100);
        var setup = DriverSetup.Create(1911, 19110);
        var admitted = kernel.AdmitComponent(setup.Plan(new DomainId(19100))).Value!;
        var first = kernel.DrainComponent(setup.Identity);
        Assert.False(first.IsSuccess);
        Assert.False(kernel.QueryComponent(setup.Identity).Value!.Reclaimable);
        Assert.True(kernel.QueryProcessTeardown(admitted.Process).IsSuccess);
        kernel.BeforeComponentTeardownRecordQueryQualificationHook = () =>
        {
            kernel.BeforeComponentTeardownRecordQueryQualificationHook = null;
            provider.ThrowSourceAfterDeviceRevoke = false;
            provider.ThrowSourceObservation = false;
            Assert.True(kernel.TerminateProcess(admitted.Process).IsSuccess);
            Assert.False(kernel.QueryProcessTeardown(admitted.Process).IsSuccess);
        };
        var observed = kernel.ObserveComponentTeardown(setup.Identity);
        Assert.True(observed.IsSuccess, observed.Message);
        Assert.True(observed.Value!.Reclaimable);
        Assert.Null(observed.Value.DeviceResources);
        Assert.False(observed.Value.PlatformAuthorityLive);
        Assert.Equal(ProcessState.Exited, kernel.Processes.ResolveTerminalState(admitted.Process).Value);
        Assert.True(kernel.RetireReclaimableComponent(setup.Identity, admitted.Process).IsSuccess);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.Equal(1, provider.MmioBindCalls);
        Assert.Equal(1, provider.IrqBindCalls);
        Assert.Single(provider.Log, item => item == "revoke-device");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LateDmaTrackingCannotPublishAfterActualDrainAndPreservesAmbiguousOriginal(bool ambiguous)
    {
        var provider = new DriverProvider { DmaRevokeStatus = ambiguous ? PlatformAuthorityStatus.Faulted : null };
        var kernel = new RuntimeKernel(provider);
        _ = TestFixtures.Create(kernel, 1900, 19000);
        var setup = DriverSetup.Create(1901, 19010);
        var component = kernel.AdmitComponent(setup.Plan(new DomainId(19000))).Value!;
        var buffer = kernel.AllocateBuffer<byte>(component.Process, 256).Value!;
        var memory = kernel.MintCapability(component.Domain, component.Process, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = kernel.MapComponentDriverOwnedRegion(setup.Identity, memory, buffer.Handle, 0, 256,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        kernel.BeforeComponentDmaTrackingQualificationHook = () =>
        {
            kernel.BeforeComponentDmaTrackingQualificationHook = null;
            var drained = kernel.DrainComponent(setup.Identity);
            Assert.Equal(!ambiguous, drained.Value?.Reclaimable == true);
        };
        var result = kernel.BindComponentDriverDma(setup.Identity, mapping, 0, 128, PlatformDmaDirection.DeviceReadsMemory);
        Assert.Equal(ambiguous ? KernelError.InvalidTransition : KernelError.StaleHandle, result.Error);
        Assert.Equal(1, provider.DmaBindCalls);
        var snapshot = kernel.QueryComponent(setup.Identity).Value!;
        Assert.Equal(!ambiguous, snapshot.Reclaimable);
        if (ambiguous)
        {
            Assert.Equal(0, kernel.QueryComponentDeviceResources(setup.Identity).Value!.ActiveDmaGrantCount);
            Assert.True(kernel.QueryBudget(snapshot.ProcessBudget).IsSuccess);
            Assert.False(kernel.RetireReclaimableComponent(setup.Identity, component.Process).IsSuccess);
            Assert.DoesNotContain("revoke-domain", provider.Log);
        }
        else
        {
            Assert.Equal(KernelError.ComponentNotFound, kernel.QueryComponentDeviceResources(setup.Identity).Error);
            Assert.True(kernel.RetireReclaimableComponent(setup.Identity, component.Process).IsSuccess);
        }
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    public void LateDriverReceiptTrackingUsesOriginalOwnerCleanupAndPreservesAmbiguousPins(int stage, bool ambiguous)
    {
        var provider = new DriverProvider { DeviceRevokeStatus = ambiguous ? PlatformAuthorityStatus.Faulted : null };
        var kernel = new RuntimeKernel(provider);
        _ = TestFixtures.Create(kernel, 1890, 18900);
        var setup = DriverSetup.Create(1891, 18910);
        var process = new ProcessHandle(setup.Process.ProcessId, setup.Process.Generation);
        var visited = 0;
        kernel.BeforeComponentDriverTrackingQualificationHook = () =>
        {
            if (visited++ != stage) return;
            kernel.BeforeComponentDriverTrackingQualificationHook = null;
            var terminated = kernel.TerminateProcess(process);
            Assert.Equal(!ambiguous, terminated.IsSuccess);
        };
        var result = kernel.AdmitComponent(setup.Plan(new DomainId(18900)));
        Assert.Equal(ambiguous ? KernelError.PlatformFaulted : KernelError.StaleHandle, result.Error);
        Assert.Equal(stage + 1, visited);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.Equal(stage >= 1 ? 1 : 0, provider.MmioBindCalls);
        Assert.Equal(stage >= 2 ? 1 : 0, provider.IrqBindCalls);
        var component = kernel.QueryComponent(setup.Identity).Value!;
        Assert.Equal(!ambiguous, component.Reclaimable);
        if (ambiguous)
        {
            Assert.Equal(ProcessState.Exiting, kernel.Processes.Resolve(process).Value!.State);
            Assert.True(kernel.QueryProcessTeardown(process).Value!.LocalAuthorizationRevoked);
            Assert.True(kernel.QueryBudget(component.ProcessBudget).IsSuccess);
            Assert.DoesNotContain("revoke-domain", provider.Log);
            Assert.False(kernel.RetireReclaimableComponent(setup.Identity, process).IsSuccess);
        }
        else
        {
            Assert.Null(component.DeviceResources);
            Assert.False(component.PlatformAuthorityLive);
            Assert.Equal(ProcessState.Exited, kernel.Processes.ResolveTerminalState(process).Value);
            Assert.True(kernel.RetireReclaimableComponent(setup.Identity, process).IsSuccess);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AdmissionRollbackRevokesLocalSourcesBeforeProviderCleanup(int closureMode)
    {
        var provider = new DriverProvider
        {
            DeviceRevokeStatus = closureMode == 1 ? PlatformAuthorityStatus.Faulted :
                closureMode == 2 ? PlatformAuthorityStatus.NotAccepted : null,
            ThrowDeviceRevoke = closureMode == 3,
        };
        var kernel = new RuntimeKernel(provider);
        _ = TestFixtures.Create(kernel, 1870, 18700);
        var setup = DriverSetup.Create(1880, 18800);
        var process = new ProcessHandle(setup.Process.ProcessId, setup.Process.Generation);
        var observed = 0;
        provider.BeforeDeviceRevoke = () =>
        {
            var live = kernel.Processes.Resolve(process).Value!;
            Assert.Equal(ProcessState.Exiting, live.State);
            Assert.Empty(live.Capabilities.Items);
            var capabilities = kernel.CapabilityAuthority.InspectionSnapshot()
                .Where(item => item.Descriptor.SubjectDomainId == setup.Process.DomainId &&
                    item.Descriptor.Generation == process.Generation)
                .Select(item => item.Descriptor.CapabilityId).ToArray();
            Assert.Equal(setup.Requirements.Length, capabilities.Length);
            foreach (var capability in capabilities)
                Assert.Equal(KernelError.CapabilityRevoked,
                    kernel.ValidateCapability(process, capability, CapabilityRights.Read).Error);
            observed++;
        };

        var result = kernel.AdmitComponent(setup.Plan(new DomainId(18700), missingDependency: true));

        Assert.Equal(1, observed);
        Assert.Equal(closureMode == 0 ? KernelError.ServiceNotFound : KernelError.PlatformFaulted, result.Error);
        var snapshot = kernel.QueryComponent(setup.Identity).Value!;
        Assert.Equal(closureMode == 0 ? ComponentLifecycleState.Reclaimable : ComponentLifecycleState.Faulted, snapshot.State);
        if (closureMode != 0)
        {
            Assert.False(snapshot.Reclaimable);
            Assert.True(snapshot.PlatformAuthorityLive);
            Assert.NotNull(snapshot.DeviceResources);
            Assert.True(kernel.Processes.Resolve(process).IsSuccess);
            Assert.DoesNotContain("revoke-domain", provider.Log);
            Assert.True(kernel.QueryBudget(snapshot.ProcessBudget).IsSuccess);
            Assert.True(kernel.QueryProcessTeardown(process).Value!.LocalAuthorizationRevoked);
            provider.DeviceRevokeStatus = null;
            provider.ThrowDeviceRevoke = false;
            var observedTeardown = kernel.ObserveComponentTeardown(setup.Identity);
            Assert.True(observedTeardown.IsSuccess);
            Assert.False(observedTeardown.Value!.Reclaimable);
            Assert.Equal(1, observed);
            Assert.Equal(ComponentLifecycleState.Faulted, observedTeardown.Value.State);
            Assert.True(kernel.QueryBudget(snapshot.ProcessBudget).IsSuccess);
            Assert.True(kernel.Processes.Resolve(process).IsSuccess);
            Assert.DoesNotContain("revoke-domain", provider.Log);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task AdmittedDriverOwnsBoundedResourcesAndServesInterruptWithoutDelegatingAuthority(int publicationFault)
    {
        var provider = new DriverProvider();
        var kernel = new RuntimeKernel(provider);
        _ = TestFixtures.Create(kernel, 1800, 18000);
        var setup = DriverSetup.Create(1810, 18100);
        var admitted = kernel.AdmitComponent(setup.Plan(new DomainId(18000)));

        Assert.True(admitted.IsSuccess, admitted.Message);
        Assert.Equal(ComponentLifecycleState.Running, admitted.Value!.State);
        var resources = Assert.IsType<DeviceResourceSetSnapshot>(admitted.Value.DeviceResources);
        Assert.Equal(setup.DeviceId, resources.DeviceResourceId);
        Assert.Equal(PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure, resources.DeviceRights);
        Assert.Single(resources.Mmio);
        Assert.Single(resources.Irqs);
        Assert.NotNull(resources.DmaPolicy);
        Assert.Equal(128, resources.DmaPolicy!.MaximumTransferBytes);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.Equal(1, provider.MmioBindCalls);
        Assert.Equal(1, provider.IrqBindCalls);

        var caller = TestFixtures.Create(kernel, 1820, 18200).Handle;
        var service = Assert.Single(kernel.ResolveByContract(setup.Contract).Value!);
        var session = kernel.OpenSession(caller, service).Value;
        var host = RuntimeDriverInterruptServiceHost.CreateForComponent(kernel, setup.Identity, session, setup.IrqResourceId).Value!;
        var client = IDriverInterruptServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
        provider.QueueInterrupt();

        var poll = client.PollAsync().AsTask();
        if(publicationFault!=0)
        {
            kernel.SessionInvocationSettlementReservedHook=()=>
            {
                Assert.False(host.ProcessNext().IsSuccess); // Overlap cannot Poll again.
                if(publicationFault==2)
                {
                    var invocation=(EndpointSessionInvocationHandle)typeof(RuntimeDriverInterruptServiceHost).GetField("_pending",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.GetValue(host)!;
                    var payload=typeof(RuntimeDriverInterruptServiceHost).GetField("_pendingResponse",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.GetValue(host)!;
                    var endpoint=kernel.EndpointSessions.ResolveForService(session,admitted.Value.Process).Value!.ServiceEndpoint;
                    Assert.True(kernel.PublishResponse(admitted.Value.Process,endpoint,invocation.InvocationId.Value,payload).IsSuccess);
                }
                throw new IOException("Response action not started after committed IRQ delivery");
            };
            Assert.Throws<IOException>(()=>host.ProcessNext());
            kernel.SessionInvocationSettlementReservedHook=null;
        }
        Assert.True(host.ProcessNext().IsSuccess);
        var delivery = await poll.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(host.ReconcileLastPublication().IsSuccess);
        var publication=(EndpointSessionInvocationRegistry.OriginalPublication)typeof(RuntimeDriverInterruptServiceHost)
            .GetField("_publication",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.GetValue(host)!;
        var copied=(EndpointSessionInvocationRegistry.OriginalPublication)typeof(object).GetMethod("MemberwiseClone",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.Invoke(publication,null)!;
        Assert.False(kernel.ReadOriginalSessionPublication(copied).IsSuccess);
        Assert.False(new RuntimeKernel().ReadOriginalSessionPublication(publication).IsSuccess);
        Assert.True(kernel.CloseSession(caller,session).IsSuccess);
        Assert.True(host.ReconcileLastPublication().IsSuccess); // Original published fact, not current routing/permission.

        Assert.Equal(DriverInterruptPollStatus.Delivered, delivery.Status);
        Assert.Equal(KernelEventClass.ExternalSignal, delivery.Event.EventClass);
        Assert.Equal("rx0", delivery.Event.SourceResourceId);
        Assert.DoesNotContain(
            kernel.CapabilityAuthority.SnapshotForDomain(new DomainId(18200)),
            capability => capability.ResourceKind is ResourceKind.Device or ResourceKind.MmioRegion or ResourceKind.Irq or ResourceKind.Dma);

        var buffer = kernel.AllocateBuffer<byte>(admitted.Value.Process, 256).Value!;
        var memory = kernel.MintCapability(
            admitted.Value.Domain,
            admitted.Value.Process,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var mapping = kernel.MapComponentDriverOwnedRegion(
            setup.Identity,
            memory,
            buffer.Handle,
            0,
            256,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var dma = kernel.BindComponentDriverDma(
            setup.Identity,
            mapping,
            0,
            128,
            PlatformDmaDirection.DeviceReadsMemory);
        Assert.True(dma.IsSuccess, dma.Message);
        Assert.Equal(1, provider.DmaBindCalls);
        Assert.Equal(1, kernel.QueryComponentDeviceResources(setup.Identity).Value!.ActiveDmaGrantCount);

        var oversized = kernel.BindComponentDriverDma(
            setup.Identity,
            mapping,
            0,
            129,
            PlatformDmaDirection.DeviceReadsMemory);
        Assert.Equal(KernelError.PlatformDenied, oversized.Error);
        var wrongDirection = kernel.BindComponentDriverDma(
            setup.Identity,
            mapping,
            0,
            64,
            PlatformDmaDirection.DeviceWritesMemory);
        Assert.Equal(KernelError.PlatformDenied, wrongDirection.Error);
        Assert.Equal(1, provider.DmaBindCalls);

        var crashed = kernel.FaultComponent(setup.Identity);
        Assert.True(crashed.IsSuccess, crashed.Message);
        Assert.Equal(ComponentLifecycleState.Reclaimable, crashed.Value!.State);
        Assert.Null(crashed.Value.DeviceResources);
        Assert.False(crashed.Value.PlatformAuthorityLive);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(admitted.Value.Process).Error);
        AssertOrdered(provider.Log, "revoke-dma", "revoke-irq", "revoke-mmio", "revoke-device", "revoke-mapping", "revoke-domain");
    }

    [Fact]
    public void DriverHardwareCapabilityOutsideResourceSetFailsBeforeProcessOrProviderAuthorityCreation()
    {
        var provider = new DriverProvider();
        var kernel = new RuntimeKernel(provider);
        _ = TestFixtures.Create(kernel, 1830, 18300);
        var setup = DriverSetup.Create(1840, 18400, includeAmbientDevice: true);

        var result = kernel.AdmitComponent(setup.Plan(new DomainId(18300)));

        Assert.Equal(KernelError.InvalidManifest, result.Error);
        Assert.Equal(0, provider.DeviceBindCalls);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new ProcessHandle(new ProcessId(1840), 1)).Error);
    }

    [Fact]
    public void CrashWithAmbiguousDmaRevokePinsDriverResourcesAndForbidsReclaim()
    {
        var provider = new DriverProvider { DmaRevokeStatus = PlatformAuthorityStatus.Faulted };
        var kernel = new RuntimeKernel(provider);
        _ = TestFixtures.Create(kernel, 1850, 18500);
        var setup = DriverSetup.Create(1860, 18600);
        var admitted = kernel.AdmitComponent(setup.Plan(new DomainId(18500))).Value!;
        var buffer = kernel.AllocateBuffer<byte>(admitted.Process, 256).Value!;
        var memory = kernel.MintCapability(
            admitted.Domain,
            admitted.Process,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapping = kernel.MapComponentDriverOwnedRegion(
            setup.Identity,
            memory,
            buffer.Handle,
            0,
            256,
            PlatformMemoryAccess.Read).Value!;
        Assert.True(kernel.BindComponentDriverDma(
            setup.Identity,
            mapping,
            0,
            64,
            PlatformDmaDirection.DeviceReadsMemory).IsSuccess);

        var fault = kernel.FaultComponent(setup.Identity);
        var snapshot = kernel.QueryComponent(setup.Identity).Value!;

        Assert.Equal(KernelError.PlatformFaulted, fault.Error);
        Assert.Equal(ComponentLifecycleState.Faulted, snapshot.State);
        Assert.False(snapshot.Reclaimable);
        Assert.True(snapshot.PlatformAuthorityLive);
        Assert.Equal(1, snapshot.DeviceResources!.ActiveDmaGrantCount);
        Assert.True(kernel.Processes.Resolve(admitted.Process).IsSuccess);
        Assert.False(kernel.ReleaseRegion(admitted.Process, buffer).IsSuccess);
        Assert.DoesNotContain("revoke-device", provider.Log);
        Assert.DoesNotContain("revoke-mapping", provider.Log);
        Assert.DoesNotContain("revoke-domain", provider.Log);
    }

    [Fact]
    public void PublicDriverSessionContractCannotExposePlatformOrProviderAuthority()
    {
        var publicTypes = typeof(IDriverInterruptService).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == "SingPlus.Sip.Drivers")
            .SelectMany(type => type.GetProperties().Select(property => property.PropertyType)
                .Concat(type.GetMethods().Select(method => method.ReturnType)))
            .ToArray();
        Assert.DoesNotContain(publicTypes, type => type.Assembly.GetName().Name?.StartsWith("SingPlus.Platform", StringComparison.Ordinal) == true);
        foreach (var forbidden in new[] { "ProviderLease", "PhysicalAddress", "Iommu", "Vmcs", "Lane", "Opcode", "DeviceResourceSet", "PlatformDeviceLease", "PlatformDmaGrant" })
            Assert.DoesNotContain(typeof(IDriverInterruptService).Assembly.GetExportedTypes().Where(type => type.Namespace == "SingPlus.Sip.Drivers"), type => type.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(1, 0)] [InlineData(2, 0)]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(2, 1)]
    [InlineData(0, 2)] [InlineData(1, 2)] [InlineData(2, 2)]
    public void RestoreDeviceChecksExactSourceBeforeEffectAndAfterProviderReceipt(int boundary, int fault)
    {
        var provider = new DriverProvider();
        var kernel = new RuntimeKernel(provider);
        var admin = TestFixtures.Create(kernel, 19601, 19601).Handle;
        var cleanup = TestFixtures.Create(kernel, 19602, 19602).Handle;
        var permission = kernel.MintCapability(new(19601), admin, ResourceKind.KernelService,
            CapabilityResourceIds.CheckpointAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var cleanupPermission = kernel.MintCapability(new(19602), cleanup, ResourceKind.KernelService,
            CapabilityResourceIds.CheckpointAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        byte[] bytes = [1, 2];
        var identity = new ComponentIdentity("restore-device-source");
        var originalManifest = new ServiceManifestV1(identity, new("1"), Digest(bytes),
            TestFixtures.Manifest(19600, 19600, 1, "restore-device-source"),
            budgetRequests: [new(ServiceBudgetDimension.CheckpointStorageBytes, 4096)],
            checkpointPolicy: new(ServiceCheckpointMode.PlannedReplacementOnly));
        var original = kernel.AdmitComponent(new(originalManifest, bytes)).Value!;
        var image = kernel.CreateOrdinaryCheckpoint(admin, permission, identity, new byte[8]).Value!;
        Assert.True(kernel.DrainComponent(identity).Value!.Reclaimable);
        const string device = "device/mock-net0";
        var requirement = new CapabilityRequirementV1(ResourceKind.Device, device, CapabilityRights.Read);
        var process = new SingProcessManifestV1(new(19600), new(19600), 2, "restore-device-source",
            ExecutionRole.Driver, MemoryProfile.SipRegion, [requirement]);
        var manifest = new ServiceManifestV1(identity, new("1"), Digest(bytes), process,
            requiresPlatformDomain: true, platformRequirements:
            [new(ComponentPlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, ComponentPlatformFeatureAvailability.RuntimeAdmission),
             new(ComponentPlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion, ComponentPlatformFeatureAvailability.RuntimeAdmission)]);
        var plan = new ComponentAdmissionPlan(manifest, bytes, [new(new(19600), requirement)],
            driverResources: new(device, PlatformDeviceRights.Read, [], []));
        Action interfere = () =>
        {
            if (fault == 0) Assert.True(kernel.RevokeCapability(permission).IsSuccess);
            else if (fault == 1) Assert.True(kernel.TerminateProcess(admin).IsSuccess);
            else Assert.True(kernel.DeleteOrdinaryCheckpoint(cleanup, cleanupPermission, image.Handle).IsSuccess);
        };
        if (boundary == 1) provider.DeviceBindCallback = interfere;
        else if (boundary == 2) kernel.BeforeComponentDriverTrackingQualificationHook = interfere;
        else kernel.BeforeComponentDriverDeviceQualificationHook = interfere;
        var refused = kernel.RestoreOrdinaryCheckpoint(admin, permission, image, plan);
        Assert.False(refused.IsSuccess);
        Assert.Equal(boundary == 0 ? 0 : 1, provider.DeviceBindCalls);
        Assert.Equal(boundary == 0 ? 0 : 1, provider.Log.Count(item => item == "revoke-device"));
        Assert.Equal(0, provider.MmioBindCalls);
        Assert.Equal(0, provider.IrqBindCalls);
        var failed = kernel.QueryComponent(identity).Value!;
        Assert.True(failed.Reclaimable);
        Assert.True(kernel.Processes.ResolveTerminalState(failed.Process).IsSuccess);
        Assert.True(kernel.RetireReclaimableComponent(identity, failed.Process).IsSuccess);
        Assert.Equal(boundary == 0 ? 0 : 1, provider.DeviceBindCalls);
        Assert.Equal(boundary == 0 ? 0 : 1, provider.Log.Count(item => item == "revoke-device"));
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(1, 0)] [InlineData(2, 0)]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(2, 1)]
    [InlineData(0, 2)] [InlineData(1, 2)] [InlineData(2, 2)]
    public void RestoreMmioSourceRefusalClosesExactReceiptAndRetainsParentCleanup(int boundary, int fault)
    {
        var provider = new DriverProvider();
        var kernel = new RuntimeKernel(provider);
        var admin = TestFixtures.Create(kernel, 19701, 19701).Handle;
        var cleanup = TestFixtures.Create(kernel, 19702, 19702).Handle;
        var permission = kernel.MintCapability(new(19701), admin, ResourceKind.KernelService,
            CapabilityResourceIds.CheckpointAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var cleanupPermission = kernel.MintCapability(new(19702), cleanup, ResourceKind.KernelService,
            CapabilityResourceIds.CheckpointAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        byte[] bytes = [1, 2];
        var identity = new ComponentIdentity("restore-mmio-source");
        var originalManifest = new ServiceManifestV1(identity, new("1"), Digest(bytes),
            TestFixtures.Manifest(19700, 19700, 1, "restore-mmio-source"),
            budgetRequests: [new(ServiceBudgetDimension.CheckpointStorageBytes, 4096)],
            checkpointPolicy: new(ServiceCheckpointMode.PlannedReplacementOnly));
        var original = kernel.AdmitComponent(new(originalManifest, bytes)).Value!;
        var image = kernel.CreateOrdinaryCheckpoint(admin, permission, identity, new byte[8]).Value!;
        Assert.True(kernel.DrainComponent(identity).Value!.Reclaimable);
        const string device = "device/mock-net0";
        var requirement = new CapabilityRequirementV1(ResourceKind.Device, device, CapabilityRights.Read | CapabilityRights.Configure);
        var mmioId = CapabilityResourceIds.MmioRegion(device, "bar0", 4096);
        var mmioRequirement = new CapabilityRequirementV1(ResourceKind.MmioRegion, mmioId, CapabilityRights.Map | CapabilityRights.Read);
        var process = new SingProcessManifestV1(new(19700), new(19700), 2, "restore-mmio-source",
            ExecutionRole.Driver, MemoryProfile.SipRegion, [requirement, mmioRequirement]);
        var manifest = new ServiceManifestV1(identity, new("1"), Digest(bytes), process,
            requiresPlatformDomain: true, platformRequirements:
            [new(ComponentPlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, ComponentPlatformFeatureAvailability.RuntimeAdmission),
             new(ComponentPlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion, ComponentPlatformFeatureAvailability.RuntimeAdmission),
             new(ComponentPlatformFeatureFamily.MmioMapping, PlatformMmioLeaseContract.ContractVersion, ComponentPlatformFeatureAvailability.RuntimeAdmission)]);
        var plan = new ComponentAdmissionPlan(manifest, bytes, [new(new(19700), requirement), new(new(19700), mmioRequirement)],
            driverResources: new(device, PlatformDeviceRights.Read | PlatformDeviceRights.Configure, [new(mmioId, 0, 256, PlatformMmioAccess.Read)], []));
        Action interfere = () =>
        {
            if (fault == 0) Assert.True(kernel.RevokeCapability(permission).IsSuccess);
            else if (fault == 1) Assert.True(kernel.TerminateProcess(admin).IsSuccess);
            else Assert.True(kernel.DeleteOrdinaryCheckpoint(cleanup, cleanupPermission, image.Handle).IsSuccess);
        };
        if (boundary == 1) provider.MmioBindCallback = interfere;
        else if (boundary == 2) kernel.BeforeComponentDriverTrackingQualificationHook = () => { if (provider.MmioBindCalls == 1) interfere(); };
        else kernel.BeforeComponentDriverMmioQualificationHook = interfere;
        var refused = kernel.RestoreOrdinaryCheckpoint(admin, permission, image, plan);
        Assert.False(refused.IsSuccess);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.Equal(1, provider.Log.Count(item => item == "revoke-device"));
        Assert.Equal(boundary == 0 ? 0 : 1, provider.Log.Count(item => item == "revoke-mmio"));
        Assert.Equal(boundary == 0 ? 0 : 1, provider.MmioBindCalls);
        Assert.Equal(0, provider.IrqBindCalls);
        var failed = kernel.QueryComponent(identity).Value!;
        Assert.True(failed.Reclaimable);
        Assert.True(kernel.Processes.ResolveTerminalState(failed.Process).IsSuccess);
        Assert.True(kernel.RetireReclaimableComponent(identity, failed.Process).IsSuccess);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.Equal(1, provider.Log.Count(item => item == "revoke-device"));
        Assert.Equal(boundary == 0 ? 0 : 1, provider.Log.Count(item => item == "revoke-mmio"));
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(1, 0)] [InlineData(2, 0)]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(2, 1)]
    [InlineData(0, 2)] [InlineData(1, 2)] [InlineData(2, 2)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 2)]
    public void RestoreIrqSourceRefusalClosesExactRouteAndParentWithoutRepeat(int boundary, int fault)
    {
        var provider = new DriverProvider();
        var kernel = new RuntimeKernel(provider);
        var admin = TestFixtures.Create(kernel, 19801, 19801).Handle;
        var cleanup = TestFixtures.Create(kernel, 19802, 19802).Handle;
        var permission = kernel.MintCapability(new(19801), admin, ResourceKind.KernelService,
            CapabilityResourceIds.CheckpointAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var cleanupPermission = kernel.MintCapability(new(19802), cleanup, ResourceKind.KernelService,
            CapabilityResourceIds.CheckpointAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        byte[] bytes = [1, 2];
        var identity = new ComponentIdentity("restore-irq-source");
        var originalManifest = new ServiceManifestV1(identity, new("1"), Digest(bytes),
            TestFixtures.Manifest(19800, 19800, 1, "restore-irq-source"),
            budgetRequests: [new(ServiceBudgetDimension.CheckpointStorageBytes, 4096)],
            checkpointPolicy: new(ServiceCheckpointMode.PlannedReplacementOnly));
        var original = kernel.AdmitComponent(new(originalManifest, bytes)).Value!;
        var image = kernel.CreateOrdinaryCheckpoint(admin, permission, identity, new byte[8]).Value!;
        Assert.True(kernel.DrainComponent(identity).Value!.Reclaimable);
        const string device = "device/mock-net0";
        var requirement = new CapabilityRequirementV1(ResourceKind.Device, device, CapabilityRights.Read | CapabilityRights.Configure);
        var irqId = CapabilityResourceIds.Irq(device, "rx0", IrqTriggerMode.Edge);
        var irqRequirement = new CapabilityRequirementV1(ResourceKind.Irq, irqId, CapabilityRights.Signal);
        var process = new SingProcessManifestV1(new(19800), new(19800), 2, "restore-irq-source",
            ExecutionRole.Driver, MemoryProfile.SipRegion, [requirement, irqRequirement]);
        var manifest = new ServiceManifestV1(identity, new("1"), Digest(bytes), process,
            requiresPlatformDomain: true, platformRequirements:
            [new(ComponentPlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, ComponentPlatformFeatureAvailability.RuntimeAdmission),
             new(ComponentPlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion, ComponentPlatformFeatureAvailability.RuntimeAdmission),
             new(ComponentPlatformFeatureFamily.IrqBinding, PlatformIrqBindingContract.ContractVersion, ComponentPlatformFeatureAvailability.RuntimeAdmission)]);
        var plan = new ComponentAdmissionPlan(manifest, bytes, [new(new(19800), requirement), new(new(19800), irqRequirement)],
            driverResources: new(device, PlatformDeviceRights.Read | PlatformDeviceRights.Configure, [], [new(irqId)]));
        Action interfere = () =>
        {
            if (fault == 0) Assert.True(kernel.RevokeCapability(permission).IsSuccess);
            else if (fault == 1) Assert.True(kernel.TerminateProcess(admin).IsSuccess);
            else Assert.True(kernel.DeleteOrdinaryCheckpoint(cleanup, cleanupPermission, image.Handle).IsSuccess);
        };
        if (boundary == 1) provider.IrqBindCallback = interfere;
        else if (boundary == 2) kernel.BeforeComponentDriverTrackingQualificationHook = () => { if (provider.IrqBindCalls == 1) interfere(); };
        else if (boundary == 3) kernel.BeforeComponentDriverIrqEndpointQualificationHook = interfere;
        else kernel.BeforeComponentDriverIrqBindingQualificationHook = _ => interfere();
        var refused = kernel.RestoreOrdinaryCheckpoint(admin, permission, image, plan);
        Assert.False(refused.IsSuccess);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.Equal(1, provider.Log.Count(item => item == "revoke-device"));
        Assert.Equal(boundary is 0 or 3 ? 0 : 1, provider.Log.Count(item => item == "revoke-irq"));
        Assert.Equal(boundary is 0 or 3 ? 0 : 1, provider.IrqBindCalls);
        Assert.Equal(0, provider.MmioBindCalls);
        var failed = kernel.QueryComponent(identity).Value!;
        Assert.True(failed.Reclaimable);
        Assert.True(kernel.Processes.ResolveTerminalState(failed.Process).IsSuccess);
        Assert.True(kernel.RetireReclaimableComponent(identity, failed.Process).IsSuccess);
        Assert.Equal(1, provider.DeviceBindCalls);
        Assert.Equal(1, provider.Log.Count(item => item == "revoke-device"));
        Assert.Equal(boundary is 0 or 3 ? 0 : 1, provider.Log.Count(item => item == "revoke-irq"));
    }

    private static void AssertOrdered(IReadOnlyList<string> log, params string[] expected)
    {
        var position = -1;
        foreach (var item in expected)
        {
            var next = -1;
            for (var index = position + 1; index < log.Count; index++)
            {
                if (string.Equals(log[index], item, StringComparison.Ordinal))
                {
                    next = index;
                    break;
                }
            }
            Assert.True(next > position, $"Expected '{item}' after position {position}. Log: {string.Join(", ", log)}");
            position = next;
        }
    }

    private sealed record DriverSetup(
        ComponentIdentity Identity,
        string DeviceId,
        string MmioResourceId,
        string IrqResourceId,
        ServiceContractIdentity Contract,
        SingProcessManifestV1 Process,
        byte[] Image,
        CapabilityRequirementV1[] Requirements,
        ComponentDriverResourcePlan Resources)
    {
        public static DriverSetup Create(ulong processId, ulong domainId, bool includeAmbientDevice = false)
        {
            const string device = "device/mock-net0";
            var mmio = CapabilityResourceIds.MmioRegion(device, "bar0", 4096);
            var irq = CapabilityResourceIds.Irq(device, "rx0", IrqTriggerMode.Edge);
            var requirements = new List<CapabilityRequirementV1>
            {
                new(ResourceKind.Device, device, CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure),
                new(ResourceKind.MmioRegion, mmio, CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write),
                new(ResourceKind.Irq, irq, CapabilityRights.Signal),
            };
            if (includeAmbientDevice)
                requirements.Add(new CapabilityRequirementV1(ResourceKind.Device, "device/ambient", CapabilityRights.Read));

            var process = new SingProcessManifestV1(
                new ProcessId(processId),
                new DomainId(domainId),
                1,
                $"driver-entry-{processId}",
                ExecutionRole.Driver,
                MemoryProfile.SipRegion,
                requirements);
            var protocol = IDriverInterruptServiceProtocol.CreateDefinition();
            var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
            var resources = new ComponentDriverResourcePlan(
                device,
                PlatformDeviceRights.Read | PlatformDeviceRights.Write | PlatformDeviceRights.Configure,
                [new DriverMmioResourcePlan(mmio, 256, 512, PlatformMmioAccess.Read | PlatformMmioAccess.Write)],
                [new DriverIrqResourcePlan(irq)],
                new DriverDmaPolicy(128, [PlatformDmaDirection.DeviceReadsMemory]));
            return new DriverSetup(
                new ComponentIdentity($"driver-{processId}"),
                device,
                mmio,
                irq,
                contract,
                process,
                [0x44, 0x52, 0x56, (byte)(processId & 0xff)],
                requirements.ToArray(),
                resources);
        }

        public ComponentAdmissionPlan Plan(DomainId issuer, bool missingDependency = false)
        {
            var provided = new ProvidedServiceManifestV1("driver-interrupt", Contract);
            var manifest = new ServiceManifestV1(
                Identity,
                new ComponentVersion("1"),
                Digest(Image),
                Process,
                [provided],
                missingDependency ? [new ServiceContractIdentity("missing-driver-dependency", "1", "missing-digest")] : [],
                requiresPlatformDomain: true,
                platformRequirements:
                [
                    new PlatformRequirementV1(ComponentPlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, ComponentPlatformFeatureAvailability.RuntimeAdmission),
                    new PlatformRequirementV1(ComponentPlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion, ComponentPlatformFeatureAvailability.RuntimeAdmission),
                    new PlatformRequirementV1(ComponentPlatformFeatureFamily.MmioMapping, PlatformMmioLeaseContract.ContractVersion, ComponentPlatformFeatureAvailability.RuntimeAdmission),
                    new PlatformRequirementV1(ComponentPlatformFeatureFamily.IrqBinding, PlatformIrqBindingContract.ContractVersion, ComponentPlatformFeatureAvailability.RuntimeAdmission),
                    new PlatformRequirementV1(ComponentPlatformFeatureFamily.DmaMapping, PlatformDmaGrantContract.ContractVersion, ComponentPlatformFeatureAvailability.RuntimeAdmission),
                ]);
            return new ComponentAdmissionPlan(
                manifest,
                Image,
                Requirements.Select(requirement => new ComponentCapabilityGrant(issuer, requirement)),
                [new ComponentProvidedServiceRegistration(provided, IDriverInterruptServiceProtocol.CreateDefinition(), IDriverInterruptServiceResponseProtocol.Definition)],
                Resources);
        }
    }

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class DriverProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformDeviceLeaseProvider,
        IPlatformMmioLeaseProvider,
        IPlatformIrqBindingProvider,
        IPlatformOwnedRegionMappingProvider,
        IPlatformRegionRevocationProvider,
        IPlatformDmaGrantProvider,
        IPlatformProviderIncarnationSource
    {
        private readonly Dictionary<PlatformProviderDeviceLeaseId, PlatformProviderDeviceLease> _devices = [];
        private readonly Dictionary<PlatformProviderMmioLeaseId, PlatformProviderMmioLease> _mmio = [];
        private readonly Dictionary<PlatformProviderIrqBindingId, PlatformProviderIrqBinding> _irqs = [];
        private readonly HashSet<PlatformProviderIrqBindingId> _pendingIrqs = [];
        private readonly Dictionary<PlatformProviderRegionMappingId, PlatformProviderOwnedRegionMapping> _mappings = [];
        private readonly Dictionary<PlatformProviderDmaGrantId, PlatformProviderDmaGrant> _grants = [];
        private readonly Dictionary<PlatformOperationId, PlatformCompletionReceipt> _operations = [];
        private PlatformProviderDomainLease? _domain;
        private ulong _nextDevice = 1;
        private ulong _nextMmio = 1;
        private ulong _nextIrq = 1;
        private ulong _nextMapping = 1;
        private ulong _nextGrant = 1;
        private ulong _nextOperation = 1;
        private ulong _nextIrqSequence = 1;

        public Action? DeviceBindCallback { get; set; }
        public bool ThrowSourceAfterDeviceRevoke { get; set; }
        public bool ThrowSourceObservation { get; set; }
        public PlatformProviderIncarnation CurrentIncarnation => ThrowSourceObservation
            ? throw new IOException("Temporary source observation failure after exact device close") : new(1);
        public Action? MmioBindCallback { get; set; }
        public Action? IrqBindCallback { get; set; }
        public int DeviceBindCalls { get; private set; }
        public int MmioBindCalls { get; private set; }
        public int IrqBindCalls { get; private set; }
        public int DmaBindCalls { get; private set; }
        public PlatformAuthorityStatus? DmaRevokeStatus { get; set; }
        public PlatformAuthorityStatus? DeviceRevokeStatus { get; set; }
        public PlatformAuthorityStatus? MmioRevokeStatus { get; set; }
        public PlatformAuthorityStatus? IrqRevokeStatus { get; set; }
        public Action? BeforeChildRevoke { get; set; }
        public Action? BeforeDeviceRevoke { get; set; }
        public bool ThrowDeviceRevoke { get; set; }
        public List<string> Log { get; } = [];

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("driver-vertical-test"),
            1,
            PlatformAuthorityFeatures.NeutralDomainBinding | PlatformAuthorityFeatures.DirectOwnedRegionMapping);

        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(PlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.OwnedRegionMapping, PlatformOwnedRegionMappingContract.ContractVersion, PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.IoDomainBinding, PlatformDeviceLeaseContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.MmioMapping, PlatformMmioLeaseContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.IrqBinding, PlatformIrqBindingContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(PlatformFeatureFamily.DmaMapping, PlatformDmaGrantContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
        });

        public void QueueInterrupt()
        {
            foreach (var id in _irqs.Keys) _pendingIrqs.Add(id);
        }

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
        {
            var lease = new PlatformProviderDomainLease(new PlatformProviderDomainLeaseId(1), new PlatformProviderLeaseGeneration(1), subject);
            _domain = lease;
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            if (_devices.Count != 0 || _mappings.Count != 0)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Child driver resources remain live.");
            Log.Add("revoke-domain");
            _domain = null;
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(PlatformProviderDomainLease domainLease, PlatformDeviceIdentity device, PlatformDeviceRights rights)
        {
            DeviceBindCalls++;
            if (_domain != domainLease)
                return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(PlatformAuthorityStatus.WrongDomain, "Wrong domain.");
            var lease = new PlatformProviderDeviceLease(new PlatformProviderDeviceLeaseId(_nextDevice++), new PlatformProviderLeaseGeneration(1), domainLease, device, rights);
            _devices.Add(lease.LeaseId, lease);
            DeviceBindCallback?.Invoke();
            return PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease)
        {
            BeforeDeviceRevoke?.Invoke();
            if (ThrowDeviceRevoke) throw new InvalidOperationException("Device closure reply lost.");
            if (DeviceRevokeStatus is { } failure)
                return PlatformAuthorityResult.Fail(failure, "Device closure is ambiguous.");
            if (_mmio.Values.Any(item => item.DeviceLease == lease) || _irqs.Values.Any(item => item.DeviceLease == lease) || _grants.Values.Any(item => item.DeviceLease == lease))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Child device resources remain live.");
            Log.Add("revoke-device");
            _devices.Remove(lease.LeaseId);
            if (ThrowSourceAfterDeviceRevoke) ThrowSourceObservation = true;
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderMmioLease> MapMmio(PlatformProviderDeviceLease deviceLease, PlatformMmioRegionIdentity region, PlatformMmioRange range, PlatformMmioAccess access)
        {
            MmioBindCalls++;
            var validation = PlatformMmioLeaseContract.ValidateRequest(region, range, access);
            if (!validation.IsSuccess) return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(validation.Status, validation.Message!);
            if (!_devices.ContainsKey(deviceLease.LeaseId)) return PlatformAuthorityResult<PlatformProviderMmioLease>.Fail(PlatformAuthorityStatus.Revoked, "Device is not live.");
            var lease = new PlatformProviderMmioLease(new PlatformProviderMmioLeaseId(_nextMmio++), new PlatformProviderLeaseGeneration(1), deviceLease, region, range, access);
            _mmio.Add(lease.LeaseId, lease);
            MmioBindCallback?.Invoke();
            return PlatformAuthorityResult<PlatformProviderMmioLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeMmio(PlatformProviderMmioLease lease)
        {
            BeforeChildRevoke?.Invoke();
            if (MmioRevokeStatus is { } status)
                return PlatformAuthorityResult.Fail(status, "Injected MMIO closure refusal.");
            Log.Add("revoke-mmio");
            _mmio.Remove(lease.LeaseId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderIrqBinding> BindInterrupt(PlatformProviderDeviceLease deviceLease, PlatformInterruptSourceIdentity source)
        {
            IrqBindCalls++;
            if (!_devices.ContainsKey(deviceLease.LeaseId)) return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(PlatformAuthorityStatus.Revoked, "Device is not live.");
            var binding = new PlatformProviderIrqBinding(new PlatformProviderIrqBindingId(_nextIrq++), new PlatformProviderLeaseGeneration(1), deviceLease, source);
            _irqs.Add(binding.BindingId, binding);
            IrqBindCallback?.Invoke();
            return PlatformAuthorityResult<PlatformProviderIrqBinding>.Ok(binding);
        }

        public PlatformAuthorityResult<PlatformInterruptDeliveryObservation> PollInterrupt(PlatformProviderIrqBinding binding)
        {
            if (!_irqs.ContainsKey(binding.BindingId)) return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(PlatformAuthorityStatus.Revoked, "IRQ route is not live.");
            if (!_pendingIrqs.Contains(binding.BindingId)) return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Ok(new PlatformInterruptDeliveryObservation(binding, false, default));
            return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Ok(new PlatformInterruptDeliveryObservation(binding, true, new PlatformProviderInterruptDeliverySequence(_nextIrqSequence++)));
        }

        public PlatformAuthorityResult CompleteInterruptDelivery(PlatformProviderIrqBinding binding, PlatformProviderInterruptDeliverySequence sequence)
        {
            if (sequence.Value == 0 || !_pendingIrqs.Remove(binding.BindingId)) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "IRQ delivery is stale.");
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult RevokeInterrupt(PlatformProviderIrqBinding binding)
        {
            BeforeChildRevoke?.Invoke();
            if (IrqRevokeStatus is { } status)
                return PlatformAuthorityResult.Fail(status, "Injected IRQ closure refusal.");
            Log.Add("revoke-irq");
            _pendingIrqs.Remove(binding.BindingId);
            _irqs.Remove(binding.BindingId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(PlatformProviderDomainLease domainLease, PlatformRegionIdentity region, PlatformMemoryAccess access)
        {
            var mapped = MapOwnedRegionSlice(domainLease, new PlatformRegionSlice(region, 0, region.ByteLength, access));
            return mapped.IsSuccess
                ? PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(mapped.Value!.Lease)
                : PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(mapped.Status, mapped.Message!);
        }

        public PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSlice(PlatformProviderDomainLease domainLease, PlatformRegionSlice slice)
        {
            if (_domain != domainLease) return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(PlatformAuthorityStatus.WrongDomain, "Wrong domain.");
            var validation = PlatformOwnedRegionMappingContract.ValidateSlice(slice);
            if (!validation.IsSuccess) return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(validation.Status, validation.Message!);
            var lease = new PlatformProviderRegionMappingLease(new PlatformProviderRegionMappingId(_nextMapping++), new PlatformProviderLeaseGeneration(1), domainLease, slice.Region, slice.Access);
            var mapping = new PlatformProviderOwnedRegionMapping(lease, slice);
            _mappings.Add(lease.MappingId, mapping);
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Ok(mapping);
        }

        public PlatformAuthorityResult<PlatformProviderDmaGrant> BindDmaGrant(PlatformDmaGrantRequest request)
        {
            DmaBindCalls++;
            var validation = PlatformDmaGrantContract.ValidateRequest(request);
            if (!validation.IsSuccess) return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(validation.Status, validation.Message!);
            if (!_devices.ContainsKey(request.DeviceLease.LeaseId) || !_mappings.TryGetValue(request.MappingLease.MappingId, out var mapping) || mapping.Slice != request.MappingSlice)
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(PlatformAuthorityStatus.Denied, "Exact device or mapping is not live.");
            var grant = new PlatformProviderDmaGrant(new PlatformProviderDmaGrantId(_nextGrant++), new PlatformProviderLeaseGeneration(1), request.DeviceLease, request.MappingLease, request.Range, request.Direction);
            _grants.Add(grant.GrantId, grant);
            return PlatformAuthorityResult<PlatformProviderDmaGrant>.Ok(grant);
        }

        public PlatformAuthorityResult RevokeDmaGrant(PlatformProviderDmaGrant grant)
        {
            if (DmaRevokeStatus is { } status) return PlatformAuthorityResult.Fail(status, "Injected ambiguous DMA revoke.");
            Log.Add("revoke-dma");
            _grants.Remove(grant.GrantId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy)
        {
            if (_grants.Values.Any(grant => grant.MappingLease == mapping)) return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "DMA grant remains live.");
            Log.Add("revoke-mapping");
            _mappings.Remove(mapping.MappingId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy)
        {
            if (_grants.Values.Any(grant => grant.MappingLease == mapping)) return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(PlatformAuthorityStatus.Denied, "DMA grant remains live.");
            Log.Add("revoke-mapping");
            _mappings.Remove(mapping.MappingId);
            var operation = new PlatformOperationIdentity(new PlatformOperationId(_nextOperation++), new PlatformOperationGeneration(1), mapping.DomainLease);
            var receipt = new PlatformCompletionReceipt(operation.OperationId, operation.Generation, operation.DomainLease, PlatformCompletionState.Closed);
            _operations.Add(operation.OperationId, receipt);
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(new PlatformRegionRevocationTicket(mapping.MappingId, mapping.Generation, operation));
        }

        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(PlatformOperationIdentity operation) =>
            _operations.TryGetValue(operation.OperationId, out var receipt)
                ? PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(receipt)
                : PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(PlatformAuthorityStatus.Denied, "Unknown operation.");
    }
}
