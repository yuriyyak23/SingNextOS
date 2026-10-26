using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class Phase02ServiceSupervisorTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NativeCallerTeardownRetriesDrainAfterTransportRecordClosed(bool throws)
    {
        (SupervisorScenario? s, ManagedServiceDefinition _, ManagedServiceDefinition? dependent, ServiceInstanceHandle provider, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        EndpointSessionHandle session = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        bool refuse = true;
        int calls = 0;
        s.Kernel.RegisterNativeServiceDrain(provider.Process, session, () =>
        {
            calls++;
            if (!refuse) return KernelResult.Ok();
            if (throws) throw new InvalidOperationException("Native caller closure unknown.");
            return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Native caller drain refused.");
        });
        if (throws) Assert.Throws<InvalidOperationException>(() => s.Kernel.CloseSessionsForProcess(peer.Process));
        else Assert.Equal(KernelError.ExternalEffectUncontained, s.Kernel.CloseSessionsForProcess(peer.Process).Error);
        Assert.Equal(EndpointSessionState.Closed, Assert.Single(s.Kernel.EndpointSessions.SnapshotForProcess(peer.Process)).State);
        Assert.Equal(throws ? KernelError.PlatformFaulted : KernelError.ExternalEffectUncontained,
            s.Kernel.TerminateProcess(peer.Process).Error);
        KernelResult<ProcessTeardownSnapshot> pending = s.Kernel.QueryProcessTeardown(peer.Process);
        Assert.True(pending.IsSuccess);
        Assert.True(pending.Value.LocalAuthorizationRevoked);
        Assert.False(pending.Value.LocalReclaimCompleted);
        Assert.Equal(2, calls);
        refuse = false;
        KernelResult retried = s.Kernel.TerminateProcess(peer.Process);
        Assert.True(retried.IsSuccess, retried.Message);
        Assert.Equal(3, calls);
        // The real successful teardown result proves managed completion;
        // retirement removes its record, and absence is not closure evidence.
        Assert.False(s.Kernel.Processes.Resolve(peer.Process).IsSuccess);
    }

    [Fact]
    public async Task NativeProcessDrainStopsAdmissionBeforeCallbackAndLeavesLifecycleGateFree()
    {
        (SupervisorScenario? s, ManagedServiceDefinition _, ManagedServiceDefinition? dependent, ServiceInstanceHandle provider, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        EndpointSessionHandle session = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        ServiceEndpointDescriptor descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        KernelResult<EndpointSessionHandle> extra = s.Kernel.OpenSession(peer.Process, descriptor);
        Assert.True(extra.IsSuccess);
        KernelResult<CapabilityDescriptorV1> permission = s.Kernel.MintCapability(s.IssuerDomain, provider.Process, ResourceKind.File, "native-drain-source", CapabilityRights.Read);
        Assert.True(permission.IsSuccess);
        using ManualResetEventSlim release = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Kernel.RegisterNativeServiceDrain(provider.Process, session, () =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Closure unknown.");
        });
        Task<KernelResult> teardown = Task.Run(() => s.Kernel.TerminateProcess(provider.Process));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            KernelResult<ProcessTeardownSnapshot> pending = await Task.Run(() => s.Kernel.QueryProcessTeardown(provider.Process)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(pending.IsSuccess);
            Assert.True(pending.Value.LocalAuthorizationRevoked);
            Assert.False(pending.Value.LocalReclaimCompleted);
            Assert.Equal(ProcessState.Exiting, s.Kernel.Processes.Resolve(provider.Process).Value!.State);
            Assert.False(s.Kernel.CapabilityAuthority.Validate(permission.Value!.CapabilityId, provider.Domain,
                provider.Process.Generation, CapabilityRights.Read).IsSuccess);
            Assert.False(s.Kernel.MintCapability(s.IssuerDomain, provider.Process, ResourceKind.File, "late", CapabilityRights.Read).IsSuccess);
            Assert.False(s.Kernel.OpenSession(peer.Process, descriptor).IsSuccess);
            Assert.Throws<InvalidOperationException>(() => s.Kernel.RegisterNativeServiceDrain(provider.Process, extra.Value, () => KernelResult.Ok()));
            Assert.Equal(KernelError.PlatformBindingDraining, s.Kernel.TerminateProcess(provider.Process).Error);
            Assert.False(teardown.IsCompleted);
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.ExternalEffectUncontained, (await teardown.WaitAsync(TimeSpan.FromSeconds(10))).Error);
        Assert.False(s.Kernel.ObserveProcessTeardown(provider.Process).Value.LocalReclaimCompleted);
    }

    [Fact]
    public void NativeDrainReentryRefusesWithoutReplayingCallback()
    {
        (SupervisorScenario? s, ManagedServiceDefinition _, ManagedServiceDefinition? dependent, ServiceInstanceHandle provider, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        EndpointSessionHandle session = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        int calls = 0;
        s.Kernel.RegisterNativeServiceDrain(provider.Process, session, () =>
        {
            calls++;
            Assert.Equal(KernelError.SessionDraining, s.Kernel.CloseSession(peer.Process, session).Error);
            return KernelResult.Ok();
        });
        Assert.True(s.Kernel.CloseSession(peer.Process, session).IsSuccess);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NativeProcessDrainRetryDoesNotReplayPreviouslyCompletedRegistration(bool throws)
    {
        (SupervisorScenario? s, ManagedServiceDefinition _, ManagedServiceDefinition? dependent, ServiceInstanceHandle provider, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        EndpointSessionHandle first = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        ServiceEndpointDescriptor descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        KernelResult<EndpointSessionHandle> opened = s.Kernel.OpenSession(peer.Process, descriptor);
        Assert.True(opened.IsSuccess, opened.Message);
        bool refuse = true;
        int refusedCalls = 0;
        int completedCalls = 0;
        s.Kernel.RegisterNativeServiceDrain(provider.Process, first, () =>
        {
            refusedCalls++;
            if (!refuse) return KernelResult.Ok();
            if (throws) throw new InvalidOperationException("Native process closure unknown.");
            return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Native closure refused.");
        });
        // Process drain visits the later registration first.
        s.Kernel.RegisterNativeServiceDrain(provider.Process, opened.Value, () =>
        {
            completedCalls++;
            return KernelResult.Ok();
        });
        Assert.Equal(throws ? KernelError.PlatformFaulted : KernelError.ExternalEffectUncontained,
            s.Kernel.TerminateProcess(provider.Process).Error);
        Assert.Equal(1, completedCalls);
        Assert.Equal(1, refusedCalls);
        KernelResult<ProcessTeardownSnapshot> pending = s.Kernel.QueryProcessTeardown(provider.Process);
        Assert.True(pending.IsSuccess);
        Assert.True(pending.Value.LocalAuthorizationRevoked);
        Assert.False(pending.Value.LocalReclaimCompleted);
        Assert.True(s.Kernel.EndpointSessions.Resolve(first, peer.Process).IsSuccess);
        refuse = false;
        Assert.True(s.Kernel.TerminateProcess(provider.Process).IsSuccess);
        Assert.Equal(1, completedCalls);
        Assert.Equal(2, refusedCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NativeSessionDrainRetainsRegistrationAfterRefusalOrException(bool throws)
    {
        (SupervisorScenario? s, ManagedServiceDefinition _, ManagedServiceDefinition? dependent, ServiceInstanceHandle provider, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        EndpointSessionHandle session = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        bool refuse = true;
        int calls = 0;
        s.Kernel.RegisterNativeServiceDrain(provider.Process, session, () =>
        {
            calls++;
            if (!refuse) return KernelResult.Ok();
            if (throws) throw new InvalidOperationException("Native drain outcome unknown.");
            return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Native closure refused.");
        });
        if (throws) Assert.Throws<InvalidOperationException>(() => s.Kernel.CloseSession(peer.Process, session));
        else Assert.Equal(KernelError.ExternalEffectUncontained, s.Kernel.CloseSession(peer.Process, session).Error);
        Assert.True(s.Kernel.EndpointSessions.Resolve(session, peer.Process).IsSuccess);
        refuse = false;
        Assert.True(s.Kernel.CloseSession(peer.Process, session).IsSuccess);
        Assert.Equal(2, calls);
        Assert.Equal(KernelError.SessionClosed, s.Kernel.CloseSession(peer.Process, session).Error);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NativeSessionAndProcessDrainShareExactRegistrationInterlock(bool processFirst)
    {
        (SupervisorScenario? s, ManagedServiceDefinition _, ManagedServiceDefinition? dependent, ServiceInstanceHandle provider, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        EndpointSessionHandle session = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        using ManualResetEventSlim release = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        s.Kernel.RegisterNativeServiceDrain(provider.Process, session, () =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return KernelResult.Ok();
        });
        Task<KernelResult> first = Task.Run(() => processFirst
            ? s.Kernel.TerminateProcess(provider.Process)
            : s.Kernel.CloseSession(peer.Process, session));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            KernelResult refused = await Task.Run(() => processFirst
                ? s.Kernel.CloseSession(peer.Process, session)
                : s.Kernel.TerminateProcess(provider.Process)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(KernelError.SessionDraining, refused.Error);
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.False(first.IsCompleted);
        }
        finally { release.Set(); }
        KernelResult settled = await first.WaitAsync(TimeSpan.FromSeconds(10));
        if (processFirst) Assert.True(settled.IsSuccess);
        else Assert.Equal(KernelError.StaleGeneration, settled.Error);
        if (!processFirst) Assert.True(s.Kernel.TerminateProcess(provider.Process).IsSuccess);
        Assert.Equal(1, calls);
    }

    private static (SupervisorScenario Scenario, ManagedServiceDefinition Dependent, ServiceInstanceHandle Peer,
        ServiceEndpointDescriptor Descriptor) CreateUnboundRebindScenario()
    {
        (SupervisorScenario? s, ManagedServiceDefinition _, ManagedServiceDefinition? dependent, ServiceInstanceHandle provider, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        ServiceEndpointDescriptor descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        EndpointSessionHandle old = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        s.Kernel.RegisterNativeServiceDrain(provider.Process, old,
            () => s.Kernel.SetServiceAvailability(descriptor.Service, ServiceAvailability.Draining));
        Assert.Equal(KernelError.ServiceUnavailable,
            s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor, s.Principal, s.ControlCapability).Error);
        Assert.Empty(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        Assert.True(s.Kernel.SetServiceAvailability(descriptor.Service, ServiceAvailability.Accepting).IsSuccess);
        return (s, dependent, peer, descriptor);
    }

    [Fact]
    public async Task RebindAdmissionAccountsSessionBeforeConcurrentComponentTeardown()
    {
        (SupervisorScenario? s, ManagedServiceDefinition? dependent, ServiceInstanceHandle peer, ServiceEndpointDescriptor descriptor) = CreateUnboundRebindScenario();
        object sessionGate = s.Kernel.EndpointSessions.GetType().GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(s.Kernel.EndpointSessions)!;
        FieldInfo allocator = s.Kernel.Channels.GetType().GetField("_nextChannelId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ulong beforeAllocation = (ulong)allocator.GetValue(s.Kernel.Channels)!;
        int pastPreflight = 0;
        s.Kernel.BeforeComponentDependencySessionQualificationHook = () => Volatile.Write(ref pastPreflight, 1);
        object lifecycleGate = s.Kernel.GetType().GetField("_platformMemoryUseGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(s.Kernel)!;
        using ManualResetEventSlim release = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task locker = Task.Run(() =>
        {
            lock (sessionGate) { entered.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
        });
        Task<KernelResult<EndpointSessionHandle>>? admission = null;
        Task<KernelResult<ComponentLifecycleSnapshot>>? teardown = null;
        ComponentLifecycleSnapshot? drainingSnapshot = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            admission = Task.Run(() => s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process,
                descriptor, s.Principal, s.ControlCapability));
            // Observation coordinates the test only; it grants no permission or closure.
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(() =>
            {
                if (Volatile.Read(ref pastPreflight) == 0) return false;
                if (!Monitor.TryEnter(lifecycleGate)) return true;
                Monitor.Exit(lifecycleGate);
                return false;
            }, TimeSpan.FromSeconds(10))));
            Assert.Equal(beforeAllocation, (ulong)allocator.GetValue(s.Kernel.Channels)!);
            teardown = Task.Run(() => s.Kernel.DrainComponent(dependent.Identity, peer.Process));
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(
                () => s.Kernel.QueryComponent(dependent.Identity).Value!.State == ComponentLifecycleState.Draining,
                TimeSpan.FromSeconds(10))));
            Assert.False(teardown.IsCompleted);
            drainingSnapshot = (await Task.Run(() => s.Kernel.QueryComponent(dependent.Identity))
                .WaitAsync(TimeSpan.FromSeconds(5))).Value!;
            Assert.Equal(ComponentLifecycleState.Draining, drainingSnapshot.State);
            Assert.Empty(drainingSnapshot.Sessions);
        }
        finally { release.Set(); }
        await locker.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(KernelError.InvalidTransition, (await admission!.WaitAsync(TimeSpan.FromSeconds(10))).Error);
        KernelResult<ComponentLifecycleSnapshot> closed = await teardown!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(closed.IsSuccess, closed.Message);
        Assert.True(closed.Value!.Reclaimable);
        Assert.Empty(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        Assert.Empty(drainingSnapshot!.Sessions);
        Assert.Equal(0, s.Kernel.Channels.InspectionSummary(peer.Process).Channels);
        EndpointSessionDiagnosticSnapshot[] ownerRecords = s.Kernel.EndpointSessions.SnapshotForProcess(peer.Process);
        Assert.Equal(2, ownerRecords.Length);
        Assert.All(ownerRecords, record => Assert.Equal(EndpointSessionState.Closed, record.State));
    }

    [Fact]
    public void ComponentSnapshotKeepsCapturedSessionAfterRebindAndActualReclaim()
    {
        (SupervisorScenario? s, ManagedServiceDefinition? dependent, ServiceInstanceHandle peer, ServiceEndpointDescriptor descriptor) = CreateUnboundRebindScenario();
        KernelResult<EndpointSessionHandle> first = s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor,
            s.Principal, s.ControlCapability);
        Assert.True(first.IsSuccess, first.Message);
        ComponentLifecycleSnapshot captured = s.Kernel.QueryComponent(dependent.Identity).Value!;
        Assert.Equal(first.Value, Assert.Single(captured.Sessions));
        KernelResult<EndpointSessionHandle> second = s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor,
            s.Principal, s.ControlCapability);
        Assert.True(second.IsSuccess, second.Message);
        Assert.NotEqual(first.Value, second.Value);
        Assert.Equal(second.Value, Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions));
        Assert.True(s.Kernel.DrainComponent(dependent.Identity, peer.Process).Value!.Reclaimable);
        ComponentLifecycleSnapshot closed = s.Kernel.QueryComponent(dependent.Identity).Value!;
        Assert.Empty(closed.Sessions);
        Assert.Empty(closed.Capabilities);
        Assert.Empty(closed.ServiceInstances);
        Assert.Equal(first.Value, Assert.Single(captured.Sessions));
        Assert.Equal(ComponentLifecycleState.Running, captured.State);
        Assert.All(s.Kernel.EndpointSessions.SnapshotForProcess(peer.Process), record => Assert.Equal(EndpointSessionState.Closed, record.State));
    }

    [Fact]
    public async Task OverlappingLocalRebindsKeepOneTrackedActiveDependency()
    {
        (SupervisorScenario? s, ManagedServiceDefinition? dependent, ServiceInstanceHandle peer, ServiceEndpointDescriptor descriptor) = CreateUnboundRebindScenario();
        for (int iteration = 0; iteration < 4; iteration++)
        {
            TaskCompletionSource start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<KernelResult<EndpointSessionHandle>>[] attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor, s.Principal, s.ControlCapability);
            })).ToArray();
            start.SetResult();
            KernelResult<EndpointSessionHandle>[] results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains(results, result => result.IsSuccess);
            Assert.All(results, result => Assert.True(result.IsSuccess || result.Error is KernelError.StaleGeneration or KernelError.InvalidTransition,
                result.Message));
            EndpointSessionHandle tracked = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
            Assert.True(s.Kernel.EndpointSessions.Resolve(tracked, peer.Process).IsSuccess);
            Assert.Equal(1, s.Kernel.Channels.InspectionSummary(peer.Process).Channels);
            Assert.Single(s.Kernel.EndpointSessions.SnapshotForProcess(peer.Process), record => record.State == EndpointSessionState.Active);
        }
        Assert.True(s.Kernel.DrainComponent(dependent.Identity, peer.Process).Value!.Reclaimable);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void RebindProviderPreflightRefusesBeforeNativeClosureOrRoutingMutation(int refusal)
    {
        (SupervisorScenario? s, ManagedServiceDefinition _, ManagedServiceDefinition? dependent, ServiceInstanceHandle exactProvider, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        ServiceEndpointDescriptor descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        ComponentLifecycleSnapshot before = s.Kernel.QueryComponent(dependent.Identity).Value!;
        EndpointSessionHandle old = Assert.Single(before.Sessions);
        int callbacks = 0;
        s.Kernel.RegisterNativeServiceDrain(exactProvider.Process, old, () => { callbacks++; return KernelResult.Ok(); });
        if (refusal == 0) Assert.True(s.Kernel.SetServiceAvailability(descriptor.Service, ServiceAvailability.Draining).IsSuccess);
        if (refusal == 1) descriptor = descriptor with { Generation = new(descriptor.Generation.Value + 1) };
        if (refusal == 2)
        {
            (SingProcess _, ProcessHandle alternative) = TestFixtures.Create(s.Kernel, 256, 2056);
            Assert.True(s.Kernel.AdmitProcess(alternative).IsSuccess);
            descriptor = s.Kernel.RegisterService(alternative, "required-provider", Contract("FailureProvider"), Protocol(Contract("FailureProvider")),
                requiredCapabilities: [new(ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure)]).Value!;
        }
        if (refusal == 3) descriptor = descriptor with { Service = descriptor.Service with { Id = new(descriptor.Service.Id.Value + 100) } };
        KernelResult<EndpointSessionHandle> refused = s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor, s.Principal, s.ControlCapability);
        Assert.Equal(refusal switch { 0 => KernelError.ServiceUnavailable, 1 => KernelError.StaleGeneration,
            2 => KernelError.MissingCapability, _ => KernelError.ServiceNotFound }, refused.Error);
        Assert.Equal(0, callbacks);
        ComponentLifecycleSnapshot after = s.Kernel.QueryComponent(dependent.Identity).Value!;
        Assert.Equal(before.Sessions.ToArray(), after.Sessions.ToArray());
        Assert.Equal(before.ProcessBudget, after.ProcessBudget);
        Assert.Equal(before.ServiceBudget, after.ServiceBudget);
        Assert.True(s.Kernel.EndpointSessions.Resolve(old, peer.Process).IsSuccess);
        Assert.Equal(1, s.Kernel.Channels.InspectionSummary(peer.Process).Channels);
    }

    [Fact]
    public void SourcedRebindRejectsRevokedCapabilityAncestorAfterNativeCallback()
    {
        (SupervisorScenario? s, ManagedServiceDefinition _, ManagedServiceDefinition? dependent, ServiceInstanceHandle exactProvider, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        ServiceEndpointDescriptor descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        CapabilityId root = s.Kernel.MintCapability(s.IssuerDomain, s.Principal, ResourceKind.KernelService,
            CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Configure | CapabilityRights.Execute | CapabilityRights.Delegate).Value!.CapabilityId;
        CapabilityId child = s.Kernel.DelegateCapability(s.Principal, s.Principal, root,
            CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;
        EndpointSessionHandle old = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        s.Kernel.RegisterNativeServiceDrain(exactProvider.Process, old, () => s.Kernel.RevokeCapability(root));
        Assert.Equal(KernelError.SupervisorDenied,
            s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor, s.Principal, child).Error);
        Assert.Empty(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        Assert.Equal(0, s.Kernel.Channels.InspectionSummary(peer.Process).Channels);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void SourcedRebindRefusesWrongAuthorityBeforeOldSessionClosure(int refusal)
    {
        (SupervisorScenario? s, ManagedServiceDefinition _, ManagedServiceDefinition? dependent, ServiceInstanceHandle _, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        ServiceEndpointDescriptor descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        EndpointSessionHandle[] before = s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions.ToArray();
        CapabilityId source = s.ControlCapability;
        ProcessHandle principal = s.Principal;
        if (refusal == 0)
            source = s.Kernel.MintCapability(s.IssuerDomain, principal, ResourceKind.KernelService,
                CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;
        if (refusal == 1) principal = principal with { Generation = principal.Generation + 1 };
        if (refusal == 2) Assert.True(s.Kernel.RevokeCapability(source).IsSuccess);
        Assert.Equal(KernelError.SupervisorDenied,
            s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor, principal, source).Error);
        Assert.Equal(before, s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions.ToArray());
        Assert.True(s.Kernel.EndpointSessions.Resolve(Assert.Single(before), peer.Process).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SourcedRebindRevalidatesExactServiceAfterNativeCallback(bool retireGeneration)
    {
        (SupervisorScenario? s, ManagedServiceDefinition? provider, ManagedServiceDefinition? dependent, ServiceInstanceHandle exactProvider, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        ServiceEndpointDescriptor descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        EndpointSessionHandle old = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        BudgetAccountHandle budget = s.Kernel.QueryComponent(dependent.Identity).Value!.ProcessBudget;
        s.Kernel.RegisterNativeServiceDrain(exactProvider.Process, old, () =>
        {
            if (retireGeneration) s.Kernel.Services.RetireForProvider(exactProvider.Process);
            else Assert.True(s.Kernel.SetServiceAvailability(descriptor.Service, ServiceAvailability.Draining).IsSuccess);
            return KernelResult.Ok();
        });
        KernelResult<EndpointSessionHandle> refused = s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor, s.Principal, s.ControlCapability);
        Assert.Equal(retireGeneration ? KernelError.StaleGeneration : KernelError.ServiceUnavailable, refused.Error);
        Assert.Empty(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        Assert.Equal(0, s.Kernel.Channels.InspectionSummary(peer.Process).Channels);
        Assert.Equal(budget, s.Kernel.QueryComponent(dependent.Identity).Value!.ProcessBudget);
        if (!retireGeneration)
        {
            Assert.True(s.Kernel.SetServiceAvailability(descriptor.Service, ServiceAvailability.Accepting).IsSuccess);
            KernelResult<EndpointSessionHandle> retried = s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor, s.Principal, s.ControlCapability);
            Assert.True(retried.IsSuccess, retried.Message);
            Assert.NotEqual(old, retried.Value);
            Assert.Equal(retried.Value, Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions));
            Assert.Equal(budget, s.Kernel.QueryComponent(dependent.Identity).Value!.ProcessBudget);
        }
    }

    [Fact]
    public async Task BlockedNativeRebindAllowsSourceRevokeAndCannotCreateLateSession()
    {
        (SupervisorScenario? s, ManagedServiceDefinition? provider, ManagedServiceDefinition? dependent, ServiceInstanceHandle exactProvider, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        EndpointSessionHandle old = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Kernel.RegisterNativeServiceDrain(exactProvider.Process, old, () =>
        {
            entered.SetResult();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
            return KernelResult.Ok();
        });
        Task<KernelResult> pending = Task.Run(() => Rebind(s.Supervisor, provider.Identity, exactProvider));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(s.Supervisor.Query(dependent.Identity).IsSuccess);
            Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
        }
        finally { resume.Set(); }
        Assert.Equal(KernelError.SupervisorDenied, (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Error);
        Assert.Empty(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        Assert.Equal(0, s.Kernel.Channels.InspectionSummary(peer.Process).Channels);
    }

    [Fact]
    public void RebindNativeCallbackLifecycleChangeCannotBeOverwrittenByPublication()
    {
        (SupervisorScenario? s, ManagedServiceDefinition? provider, ManagedServiceDefinition? dependent, ServiceInstanceHandle exactProvider, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        EndpointSessionHandle old = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        s.Kernel.RegisterNativeServiceDrain(exactProvider.Process, old, () =>
        {
            Assert.True(s.Supervisor.ReportHealth(peer, ServiceHealthState.Degraded, "Independent health transition").IsSuccess);
            return KernelResult.Ok();
        });
        Assert.Equal(KernelError.InvalidTransition, Rebind(s.Supervisor, provider.Identity, exactProvider).Error);
        ServiceSupervisorSnapshot after = s.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Degraded, after.State);
        Assert.Equal("Independent health transition", after.Health!.Reason);
    }

    [Fact]
    public void RebindMultipleContractsAdvancesOnlyItsOwnCommittedSnapshots()
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock, new SupervisorTimeProvider());
        ServiceContractIdentity first = Contract("MultiFirst");
        ServiceContractIdentity second = Contract("MultiSecond");
        byte[] image = new byte[] { 254, 1 };
        ProvidedServiceManifestV1[] services = new[] { new ProvidedServiceManifestV1("multi-provider", first), new ProvidedServiceManifestV1("multi-second", second) };
        ServiceManifestV1 manifest = new ServiceManifestV1(new("multi-provider"), new("1.1"), Digest(image),
            TestFixtures.Manifest(254, 2054, 1, "multi-entry"), services);
        ManagedServiceDefinition provider = new ManagedServiceDefinition(new ComponentAdmissionPlan(manifest, image,
            providedServices: services.Select(item => new ComponentProvidedServiceRegistration(item, Protocol(item.Contract)))), "multi-provider");
        ManagedServiceDefinition dependent = Definition("multi-peer", 255, 2055, Contract("MultiPeer"),
            dependencies: [new(first, ServiceDependencyKind.Hard), new(second, ServiceDependencyKind.Hard)]);
        Assert.True(s.Supervisor.Register(provider).IsSuccess);
        Assert.True(s.Supervisor.Register(dependent).IsSuccess);
        Assert.True(s.Supervisor.StartAll().IsSuccess);
        ServiceInstanceHandle exact = s.Supervisor.Query(provider.Identity).Value!.Instance!.Value;
        ServiceInstanceHandle oldPeer = s.Supervisor.Query(dependent.Identity).Value!.Instance!.Value;
        Assert.True(s.Supervisor.ReportHealth(oldPeer, ServiceHealthState.Degraded, "Waiting for fresh contracts").IsSuccess);
        byte[] freshImage = new byte[] { 254, 2 };
        ServiceManifestV1 freshManifest = new ServiceManifestV1(new("multi-provider"), new("1.2"), Digest(freshImage),
            TestFixtures.Manifest(254, 2054, 2, "multi-entry"), services);
        ManagedServiceDefinition freshDefinition = new ManagedServiceDefinition(new ComponentAdmissionPlan(freshManifest, freshImage,
            providedServices: services.Select(item => new ComponentProvidedServiceRegistration(item, Protocol(item.Contract)))), "multi-provider");
        int rebindClocks = 0;
        Action inspect = null!;
        inspect = () =>
        {
            if (s.Supervisor.Query(provider.Identity).Value!.Instance?.Process.Generation == 2)
            {
                rebindClocks++;
                Assert.Equal(ServiceLifecycleState.Degraded, s.Supervisor.Query(dependent.Identity).Value!.State);
            }
            clock.OnRead = inspect;
        };
        clock.OnRead = inspect;
        Assert.True(s.Supervisor.Replace(exact, freshDefinition).IsSuccess);
        clock.OnRead = null;
        Assert.Equal(2, rebindClocks);
        ServiceSupervisorSnapshot peer = s.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(2, peer.Dependencies.Count);
        Assert.Equal(2, s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions.Count);
        Assert.Equal(ServiceLifecycleState.Ready, peer.State);
        Assert.All(peer.Dependencies, binding => Assert.Equal(2ul, binding.Provider.Process.Generation));
    }

    private static KernelResult Rebind(CapabilityAwareServiceSupervisor supervisor, ComponentIdentity provider,
        ServiceInstanceHandle instance) => (KernelResult)typeof(CapabilityAwareServiceSupervisor)
        .GetMethod("RebindDependents", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(supervisor, [provider, instance])!;

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void RebindClockSourceLossOrFaultRefusesBeforeDispatchAndPublication(int fault)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        (SupervisorScenario? s, ManagedServiceDefinition? provider, ManagedServiceDefinition? dependent, ServiceInstanceHandle exactProvider, ServiceInstanceHandle _) = CreateFailureDependencyScenario(clock);
        ServiceSupervisorSnapshot before = s.Supervisor.Query(dependent.Identity).Value!;
        EndpointSessionHandle[] sessions = s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions.ToArray();
        clock.OnRead = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            if (fault == 3) throw new InvalidOperationException("Rebind clock fault.");
        };
        Assert.Equal(fault == 3 ? KernelError.PlatformFaulted : KernelError.SupervisorDenied,
            Rebind(s.Supervisor, provider.Identity, exactProvider).Error);
        ServiceSupervisorSnapshot after = InspectSupervisorRecord(s.Supervisor, dependent.Identity);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Health, after.Health);
        Assert.Equal(before.Dependencies.ToArray(), after.Dependencies.ToArray());
        Assert.Equal(sessions, s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions.ToArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RebindClockReplacementRefusesCapturedProviderOrDependent(bool replaceProvider)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        (SupervisorScenario? s, ManagedServiceDefinition? provider, ManagedServiceDefinition? dependent, ServiceInstanceHandle exactProvider, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(clock);
        ManagedServiceDefinition target = replaceProvider ? provider : dependent;
        clock.OnRead = () => Assert.True(s.Supervisor.Replace(replaceProvider ? exactProvider : peer,
            Definition(target.Identity.Name, replaceProvider ? 251ul : 252ul, replaceProvider ? 2051ul : 2052ul,
                Contract(replaceProvider ? "FailureProvider" : "FailurePeer"), generation: 2)).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, Rebind(s.Supervisor, provider.Identity, exactProvider).Error);
        ServiceSupervisorSnapshot fresh = s.Supervisor.Query(target.Identity).Value!;
        Assert.Equal(2ul, fresh.Instance!.Value.Process.Generation);
        Assert.Equal(ServiceLifecycleState.Ready, fresh.State);
        Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(target.Identity).Value!.State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void RebindNativeCallbackSourceLossRefusesNewSessionAndPublication(int loss)
    {
        (SupervisorScenario? s, ManagedServiceDefinition? provider, ManagedServiceDefinition? dependent, ServiceInstanceHandle exactProvider, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        ServiceSupervisorSnapshot before = s.Supervisor.Query(dependent.Identity).Value!;
        EndpointSessionHandle old = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        s.Kernel.RegisterNativeServiceDrain(exactProvider.Process, old, () =>
        {
            if (loss == 0) Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
            if (loss == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (loss is 2 or 3) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            if (loss == 3)
            {
                (SingProcess _, ProcessHandle fresh) = TestFixtures.Create(s.Kernel, 900, 9000, generation: 2, identity: "supervisor-control");
                Assert.True(s.Kernel.AdmitProcess(fresh).IsSuccess);
                Assert.True(s.Kernel.MintCapability(s.IssuerDomain, fresh, ResourceKind.KernelService,
                    CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Configure | CapabilityRights.Execute).IsSuccess);
            }
            return KernelResult.Ok();
        });
        Assert.Equal(KernelError.SupervisorDenied, Rebind(s.Supervisor, provider.Identity, exactProvider).Error);
        ServiceSupervisorSnapshot after = InspectSupervisorRecord(s.Supervisor, dependent.Identity);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Health, after.Health);
        Assert.Equal(before.Dependencies.ToArray(), after.Dependencies.ToArray());
        Assert.Empty(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        Assert.Equal(0, s.Kernel.Channels.InspectionSummary(peer.Process).Channels);
        Assert.True(s.Kernel.DrainComponent(dependent.Identity, peer.Process).Value!.Reclaimable);
    }

    [Fact]
    public async Task BlockedRebindClockAllowsQueryAndRevoke()
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        (SupervisorScenario? s, ManagedServiceDefinition? provider, ManagedServiceDefinition? dependent, ServiceInstanceHandle exactProvider, ServiceInstanceHandle _) = CreateFailureDependencyScenario(clock);
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnRead = () => { entered.SetResult(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10))); };
        Task<KernelResult> rebinding = Task.Run(() => Rebind(s.Supervisor, provider.Identity, exactProvider));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ServiceLifecycleState.Ready, s.Supervisor.Query(dependent.Identity).Value!.State);
            Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
        }
        finally { resume.Set(); }
        Assert.Equal(KernelError.SupervisorDenied, (await rebinding.WaitAsync(TimeSpan.FromSeconds(10))).Error);
    }

    [Fact]
    public void DependencyRebindCannotDiscardClosedSessionWithAcceptedInvocationEffect()
    {
        (SupervisorScenario? s, ManagedServiceDefinition _, ManagedServiceDefinition? dependent, ServiceInstanceHandle failed, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        ServiceEndpointDescriptor descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        EndpointSessionHandle session = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        ChannelEndpointHandle channel = s.Kernel.EndpointSessions.Resolve(session, peer.Process).Value!.Channel;
        Assert.True(s.Kernel.Send(peer.Process, failed.Process, channel, 1).IsSuccess);
        KernelResult<EndpointSessionRequestEnvelope> request = s.Kernel.ReceiveSessionRequest(failed.Process, session);
        Assert.True(request.IsSuccess);
        Assert.True(s.Kernel.AcceptSessionInvocation(failed.Process, request.Value!.Invocation, false).IsSuccess);
        Assert.True(s.Kernel.CloseSession(peer.Process, session).IsSuccess);
        Assert.Equal(KernelError.SessionDraining,
            s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor).Error);
        Assert.Equal(session, Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions));
        Assert.False(s.Kernel.DrainComponent(dependent.Identity, peer.Process).Value!.Reclaimable);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DependencyRebindRetainsSessionAccountingOnNativeClosureRefusal(bool throws)
    {
        (SupervisorScenario? s, ManagedServiceDefinition _, ManagedServiceDefinition? dependent, ServiceInstanceHandle failed, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        ServiceEndpointDescriptor descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        ComponentLifecycleSnapshot before = s.Kernel.QueryComponent(dependent.Identity).Value!;
        EndpointSessionHandle session = Assert.Single(before.Sessions);
        bool refuse = true;
        int calls = 0;
        s.Kernel.RegisterNativeServiceDrain(failed.Process, session, () =>
        {
            calls++;
            if (!refuse) return KernelResult.Ok();
            if (throws) throw new InvalidOperationException("Unknown native closure.");
            return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Native closure refused.");
        });
        KernelResult<EndpointSessionHandle> refused = s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor);
        Assert.Equal(throws ? KernelError.PlatformFaulted : KernelError.ExternalEffectUncontained, refused.Error);
        ComponentLifecycleSnapshot after = s.Kernel.QueryComponent(dependent.Identity).Value!;
        Assert.Equal(before.Sessions.ToArray(), after.Sessions.ToArray());
        Assert.Equal(before.ProcessBudget, after.ProcessBudget);
        Assert.True(s.Kernel.EndpointSessions.Resolve(session, peer.Process).IsSuccess);
        refuse = false;
        KernelResult<EndpointSessionHandle> retried = s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor);
        Assert.True(retried.IsSuccess, retried.Message);
        Assert.Equal(2, calls);
        Assert.NotEqual(session, retried.Value);
        Assert.Equal(retried.Value, Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions));
    }

    [Fact]
    public void DependencyRebindRetainsPinnedSessionUntilExistingOwnerCleanup()
    {
        (SupervisorScenario? s, ManagedServiceDefinition _, ManagedServiceDefinition? dependent, ServiceInstanceHandle failed, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        ServiceEndpointDescriptor descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        EndpointSessionHandle session = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        KernelResult<EndpointSessionPin> pin = s.Kernel.EndpointSessions.AcquirePin(session, peer.Process, failed.Process);
        Assert.True(pin.IsSuccess);
        try
        {
            Assert.Equal(KernelError.SessionDraining,
                s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor).Error);
            Assert.Equal(1, s.Kernel.EndpointSessions.ActivePinCount(session));
            Assert.Equal(session, Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions));
        }
        finally { pin.Value!.Dispose(); }
        KernelResult<EndpointSessionHandle> reconciled = s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor);
        Assert.True(reconciled.IsSuccess, reconciled.Message);
        Assert.NotEqual(session, reconciled.Value);
        Assert.Equal(reconciled.Value, Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions));
        Assert.True(s.Kernel.DrainComponent(dependent.Identity, peer.Process).Value!.Reclaimable);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExactDependencyRebindRefusesStaleTargetBeforeSessionMutation(bool replace)
    {
        (SupervisorScenario? s, ManagedServiceDefinition _, ManagedServiceDefinition? dependent, ServiceInstanceHandle _, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        ServiceEndpointDescriptor descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        if (replace)
            Assert.True(s.Supervisor.Replace(peer, Definition(dependent.Identity.Name, 252, 2052,
                Contract("FailurePeer"), generation: 2,
                dependencies: [new(Contract("FailureProvider"), ServiceDependencyKind.Hard)])).IsSuccess);
        ComponentLifecycleSnapshot before = s.Kernel.QueryComponent(dependent.Identity).Value!;
        ProcessHandle stale = replace ? peer.Process : peer.Process with { Generation = peer.Process.Generation + 1 };
        KernelResult<EndpointSessionHandle> refused = s.Kernel.RebindComponentDependency(dependent.Identity, stale, descriptor);
        Assert.Equal(KernelError.StaleGeneration, refused.Error);
        ComponentLifecycleSnapshot after = s.Kernel.QueryComponent(dependent.Identity).Value!;
        Assert.Equal(before.Process, after.Process);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Sessions.ToArray(), after.Sessions.ToArray());
        Assert.Equal(before.Capabilities.ToArray(), after.Capabilities.ToArray());
        Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(after.Process).Value!.State);
    }

    [Fact]
    public void ExactDependencyRebindAcceptsCurrentTargetThroughExistingConsumer()
    {
        (SupervisorScenario? s, ManagedServiceDefinition _, ManagedServiceDefinition? dependent, ServiceInstanceHandle _, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        ServiceEndpointDescriptor descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        EndpointSessionHandle before = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        KernelResult<EndpointSessionHandle> rebound = s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor);
        Assert.True(rebound.IsSuccess, rebound.Message);
        Assert.NotEqual(before, rebound.Value);
        Assert.Equal(rebound.Value, Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void DependencyFailureClockRefusesSourceLossOrFaultWithoutPartialPeerMutation(int fault)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        (SupervisorScenario? s, ManagedServiceDefinition? provider, ManagedServiceDefinition? dependent, ServiceInstanceHandle failed, ServiceInstanceHandle _) = CreateFailureDependencyScenario(clock);
        ServiceSupervisorSnapshot before = s.Supervisor.Query(dependent.Identity).Value!;
        clock.OnRead = () => clock.OnRead = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            if (fault == 3) throw new InvalidOperationException("Propagation clock fault.");
        };
        KernelResult<ServiceHealthSnapshot> refused = s.Supervisor.ReportHealth(failed, ServiceHealthState.Failed, "Failure propagation");
        Assert.Equal(fault == 3 ? KernelError.PlatformFaulted : KernelError.SupervisorDenied, refused.Error);
        ServiceSupervisorSnapshot after = InspectSupervisorRecord(s.Supervisor, dependent.Identity);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Health, after.Health);
        Assert.True(s.Kernel.QueryComponent(provider.Identity).Value!.Reclaimable);
        if (fault == 3)
        {
            Assert.True(s.Supervisor.ReportHealth(failed, ServiceHealthState.Failed, "Retry existing failure").IsSuccess);
            Assert.Equal(ServiceLifecycleState.Degraded, s.Supervisor.Query(dependent.Identity).Value!.State);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DependencyFailureClockReplacementCannotPublishOrFaultFreshGeneration(bool replaceProvider)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        (SupervisorScenario? s, ManagedServiceDefinition? provider, ManagedServiceDefinition? dependent, ServiceInstanceHandle failed, ServiceInstanceHandle peer) = CreateFailureDependencyScenario(clock);
        ManagedServiceDefinition definition = replaceProvider ? provider : dependent;
        ServiceInstanceHandle previous = replaceProvider ? failed : peer;
        ServiceContractIdentity contract = replaceProvider ? Contract("FailureProvider") : Contract("FailurePeer");
        clock.OnRead = () => clock.OnRead = () => Assert.True(s.Supervisor.Replace(previous,
            Definition(definition.Identity.Name, previous.Process.ProcessId.Value,
                replaceProvider ? 2051ul : 2052ul, contract, generation: 2)).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration,
            s.Supervisor.ReportHealth(failed, ServiceHealthState.Failed, "Old provider failure").Error);
        ServiceSupervisorSnapshot fresh = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, fresh.State);
        Assert.Equal(2ul, fresh.Instance!.Value.Process.Generation);
        Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(fresh.Instance.Value.Process).Value!.State);
        Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(definition.Identity).Value!.State);
    }

    [Fact]
    public void DependencyFailureBatchValidatesEveryPeerBeforeFirstMutation()
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        (SupervisorScenario? s, ManagedServiceDefinition _, ManagedServiceDefinition? dependent, ServiceInstanceHandle failed, ServiceInstanceHandle _) = CreateFailureDependencyScenario(clock);
        ManagedServiceDefinition second = Definition("failure-peer-second", 253, 2053, Contract("FailurePeerSecond"),
            dependencies: [new(Contract("FailureProvider"), ServiceDependencyKind.Hard)]);
        Assert.True(s.Supervisor.Register(second).IsSuccess);
        ServiceInstanceHandle secondInstance = s.Supervisor.Start(second.Identity).Value!.Snapshot.Instance!.Value;
        ServiceSupervisorSnapshot before = s.Supervisor.Query(dependent.Identity).Value!;
        clock.OnRead = () => clock.OnRead = () => Assert.True(s.Supervisor.Drain(secondInstance).Value!.ClosureProven);
        Assert.Equal(KernelError.InvalidTransition,
            s.Supervisor.ReportHealth(failed, ServiceHealthState.Failed, "Captured peer changed").Error);
        ServiceSupervisorSnapshot first = s.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(before.State, first.State);
        Assert.Equal(before.Health, first.Health);
        Assert.Equal(ServiceLifecycleState.Stopped, s.Supervisor.Query(second.Identity).Value!.State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailureBatchSameStateHealthChangeRefusesBeforeAnyPeerMutation(bool degraded)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        (SupervisorScenario? s, ManagedServiceDefinition? provider, ManagedServiceDefinition? dependent, ServiceInstanceHandle failed, ServiceInstanceHandle _) = CreateFailureDependencyScenario(clock);
        ManagedServiceDefinition second = Definition("same-state-peer", 1219, 1220, Contract("SameStatePeer"),
            dependencies: [new(Contract("FailureProvider"), ServiceDependencyKind.Hard)]);
        Assert.True(s.Supervisor.Register(second).IsSuccess);
        ServiceInstanceHandle peer = s.Supervisor.Start(second.Identity).Value!.Snapshot.Instance!.Value;
        if (degraded) Assert.True(s.Supervisor.ReportHealth(peer, ServiceHealthState.Degraded, "Initial observation").IsSuccess);
        ServiceSupervisorSnapshot firstBefore = s.Supervisor.Query(dependent.Identity).Value!;
        ServiceSupervisorSnapshot secondBefore = s.Supervisor.Query(second.Identity).Value!;
        clock.OnRead = () => clock.OnRead = () =>
            Assert.True(s.Supervisor.ReportHealth(peer,
                degraded ? ServiceHealthState.Degraded : ServiceHealthState.Ready, "Fresh independent observation").IsSuccess);

        Assert.Equal(KernelError.StaleGeneration,
            s.Supervisor.ReportHealth(failed, ServiceHealthState.Failed, "Captured failure batch").Error);

        ServiceSupervisorSnapshot firstAfter = s.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(firstBefore.State, firstAfter.State);
        Assert.Equal(firstBefore.Health, firstAfter.Health);
        Assert.Equal(firstBefore.Dependencies.ToArray(), firstAfter.Dependencies.ToArray());
        ServiceSupervisorSnapshot secondAfter = s.Supervisor.Query(second.Identity).Value!;
        Assert.Equal(secondBefore.State, secondAfter.State);
        Assert.Equal("Fresh independent observation", secondAfter.Health!.Reason);
        Assert.Equal(secondBefore.Dependencies.ToArray(), secondAfter.Dependencies.ToArray());
        Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(peer.Process).Value!.State);
        Assert.True(s.Kernel.QueryComponent(provider.Identity).Value!.Reclaimable);
    }

    [Theory]
    [InlineData(ServiceFailurePropagationPolicy.DrainDependent)]
    [InlineData(ServiceFailurePropagationPolicy.StopDependent)]
    public void SequentialFailurePolicyChecksLaterPeerAfterEarlierDrainCallback(ServiceFailurePropagationPolicy policy)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("SequentialPolicyProvider");
        ManagedServiceDefinition provider = Definition("sequential-provider", 1221, 1222, contract);
        ManagedServiceDefinition firstPlan = Definition("sequential-first", 1223, 1224, Contract("SequentialFirst"),
            dependencies: [new(contract, ServiceDependencyKind.Hard)]);
        ManagedServiceDefinition secondPlan = Definition("sequential-second", 1225, 1226, Contract("SequentialSecond"),
            dependencies: [new(contract, ServiceDependencyKind.Hard)]);
        ManagedServiceDefinition first = new ManagedServiceDefinition(firstPlan.Admission, firstPlan.PrimaryServiceName, dependencyFailurePolicy: policy);
        ManagedServiceDefinition second = new ManagedServiceDefinition(secondPlan.Admission, secondPlan.PrimaryServiceName, dependencyFailurePolicy: policy);
        Assert.True(s.Supervisor.Register(provider).IsSuccess);
        Assert.True(s.Supervisor.Register(first).IsSuccess);
        Assert.True(s.Supervisor.Register(second).IsSuccess);
        Assert.True(s.Supervisor.StartAll().IsSuccess);
        ServiceInstanceHandle failed = s.Supervisor.Query(provider.Identity).Value!.Instance!.Value;
        ServiceInstanceHandle later = s.Supervisor.Query(second.Identity).Value!.Instance!.Value;
        ServiceDependencyBinding[] bindings = s.Supervisor.Query(second.Identity).Value!.Dependencies.ToArray();
        // Failure health, first drain intent, then first drain completion clock.
        // This batch contains no Degrade policy and needs no observation clock.
        clock.OnRead = () => clock.OnRead = () => clock.OnRead = () =>
            Assert.True(s.Supervisor.ReportHealth(later, ServiceHealthState.Ready, "New peer source between effects").IsSuccess);

        Assert.Equal(KernelError.StaleGeneration,
            s.Supervisor.ReportHealth(failed, ServiceHealthState.Failed, "Sequential failure").Error);

        Assert.Equal(ServiceLifecycleState.Stopped, s.Supervisor.Query(first.Identity).Value!.State);
        Assert.True(s.Kernel.QueryComponent(first.Identity).Value!.Reclaimable);
        ServiceSupervisorSnapshot after = s.Supervisor.Query(second.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, after.State);
        Assert.Equal("New peer source between effects", after.Health!.Reason);
        Assert.Equal(bindings, after.Dependencies.ToArray());
        Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(later.Process).Value!.State);
        Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(second.Identity).Value!.State);
    }

    [Fact]
    public async Task BlockedDependencyFailureClockAllowsQueryAndSourceRevoke()
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        (SupervisorScenario? s, ManagedServiceDefinition _, ManagedServiceDefinition? dependent, ServiceInstanceHandle failed, ServiceInstanceHandle _) = CreateFailureDependencyScenario(clock);
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnRead = () => clock.OnRead = () => { entered.SetResult(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10))); };
        Task<KernelResult<ServiceHealthSnapshot>> propagating = Task.Run(() => s.Supervisor.ReportHealth(failed, ServiceHealthState.Failed, "Failure"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ServiceLifecycleState.Ready, s.Supervisor.Query(dependent.Identity).Value!.State);
            Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
        }
        finally { resume.Set(); }
        Assert.Equal(KernelError.SupervisorDenied, (await propagating.WaitAsync(TimeSpan.FromSeconds(10))).Error);
        Assert.Equal(ServiceLifecycleState.Ready, InspectSupervisorRecord(s.Supervisor, dependent.Identity).State);
    }

    private static (SupervisorScenario Scenario, ManagedServiceDefinition Provider, ManagedServiceDefinition Dependent,
        ServiceInstanceHandle Failed, ServiceInstanceHandle Peer) CreateFailureDependencyScenario(CallbackHealthClock clock)
    {
        SupervisorScenario s = CreateSupervisor(clock, new SupervisorTimeProvider());
        ManagedServiceDefinition provider = Definition("failure-provider", 251, 2051, Contract("FailureProvider"));
        ManagedServiceDefinition dependent = Definition("failure-peer", 252, 2052, Contract("FailurePeer"),
            dependencies: [new(Contract("FailureProvider"), ServiceDependencyKind.Hard)]);
        Assert.True(s.Supervisor.Register(provider).IsSuccess);
        Assert.True(s.Supervisor.Register(dependent).IsSuccess);
        Assert.True(s.Supervisor.StartAll().IsSuccess);
        return (s, provider, dependent, s.Supervisor.Query(provider.Identity).Value!.Instance!.Value,
            s.Supervisor.Query(dependent.Identity).Value!.Instance!.Value);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ReadinessClockSourceLossOrFaultCannotPublishPartialReady(int fault)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ManagedServiceDefinition definition = Definition("readiness-commit", 246, 2046, Contract("ReadinessCommit"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        clock.OnRead = () =>
        {
            Assert.Equal(ServiceLifecycleState.Admitting, s.Supervisor.Query(definition.Identity).Value!.State);
            if (fault == 0) Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            if (fault == 3) throw new InvalidOperationException("Readiness clock fault.");
        };
        KernelResult<ServiceStartReceipt> refused = s.Supervisor.Start(definition.Identity);
        Assert.Equal(fault == 3 ? KernelError.PlatformFaulted : KernelError.SupervisorDenied, refused.Error);
        ServiceSupervisorSnapshot after = InspectSupervisorRecord(s.Supervisor, definition.Identity);
        Assert.Null(after.Instance);
        Assert.Null(after.Health);
        Assert.Empty(after.Dependencies);
        Assert.Equal(fault == 3 ? ServiceLifecycleState.Stopped : ServiceLifecycleState.Admitting, after.State);
        Assert.True(s.Kernel.QueryComponent(definition.Identity).Value!.Reclaimable);
        Assert.Equal(KernelError.ServiceNotFound, s.Kernel.ResolveByServiceName(definition.PrimaryServiceName).Error);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ReadinessClockRequiresActualRunningProcessAndAcceptingService(int fault)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ManagedServiceDefinition definition = Definition("readiness-owner", 251, 2051, Contract("ReadinessOwner"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ProcessHandle process = new ProcessHandle(definition.Admission.Manifest.Process.ProcessId,
            definition.Admission.Manifest.Process.Generation);
        clock.OnRead = () =>
        {
            Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(definition.Identity).Value!.State);
            if (fault == 0) Assert.True(s.Kernel.TerminateProcess(process).IsSuccess);
            if (fault == 1) Assert.True(s.Kernel.ParkProcess(process).IsSuccess);
            if (fault == 2)
            {
                ServiceEndpointDescriptor service = s.Kernel.ResolveByServiceName(definition.PrimaryServiceName).Value!;
                Assert.True(s.Kernel.SetServiceAvailability(service.Service, ServiceAvailability.Unavailable).IsSuccess);
            }
        };
        KernelResult<ServiceStartReceipt> result = s.Supervisor.Start(definition.Identity);
        Assert.Equal(fault == 0 ? KernelError.StaleHandle : fault == 1 ? KernelError.InvalidTransition : KernelError.ServiceUnavailable, result.Error);
        ServiceSupervisorSnapshot current = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Null(current.Instance);
        Assert.NotEqual(ServiceLifecycleState.Ready, current.State);
        Assert.True(s.Kernel.QueryComponent(definition.Identity).Value!.Reclaimable);
        Assert.Equal(ProcessState.Exited, s.Kernel.Processes.ResolveTerminalState(process).Value);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReadinessRefusesComponentIntentBeforeProcessOwnerTransition(bool dependency)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("IntentProvider");
        ManagedServiceDefinition provider = Definition("intent-provider", 259, 2059, contract);
        ManagedServiceDefinition peer = Definition("intent-peer", 260, 2060, Contract("IntentPeer"),
            dependencies: dependency ? [new(contract, ServiceDependencyKind.Hard)] : []);
        Assert.True(s.Supervisor.Register(peer).IsSuccess);
        if (dependency)
        {
            Assert.True(s.Supervisor.Register(provider).IsSuccess);
            Assert.True(s.Supervisor.Start(provider.Identity).IsSuccess);
        }
        using ManualResetEventSlim intent = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        ComponentIdentity target = dependency ? provider.Identity : peer.Identity;
        ProcessHandle targetProcess = new ProcessHandle((dependency ? provider : peer).Admission.Manifest.Process.ProcessId, 1);
        Task<KernelResult<ComponentLifecycleSnapshot>>? drain = null;
        int intentCalls = 0;
        s.Kernel.AfterComponentTeardownIntentQualificationHook = () =>
        {
            if (Interlocked.Increment(ref intentCalls) != 1) return;
            intent.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        clock.OnRead = () =>
        {
            drain = Task.Run(() => s.Kernel.DrainComponent(target));
            Assert.True(intent.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(ComponentLifecycleState.Draining, s.Kernel.QueryComponent(target).Value!.State);
            Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(targetProcess).Value!.State);
        };
        try
        {
            Assert.Equal(KernelError.InvalidTransition, s.Supervisor.Start(peer.Identity).Error);
            ServiceSupervisorSnapshot after = s.Supervisor.Query(peer.Identity).Value!;
            Assert.Null(after.Instance);
            Assert.NotEqual(ServiceLifecycleState.Ready, after.State);
        }
        finally
        {
            s.Kernel.AfterComponentTeardownIntentQualificationHook = null;
            release.Set();
            if (drain is not null) Assert.True((await drain).IsSuccess);
        }
        Assert.True(s.Kernel.ObserveComponentTeardown(target).Value!.Reclaimable);
        Assert.True(s.Kernel.ObserveComponentTeardown(peer.Identity).Value!.Reclaimable);
    }

    [Fact]
    public void ReadinessClockDependencyLossIsRevalidatedBeforePublication()
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity providerContract = Contract("ReadinessProvider");
        ManagedServiceDefinition provider = Definition("readiness-provider", 247, 2047, providerContract);
        ManagedServiceDefinition dependent = Definition("readiness-dependent", 248, 2048, Contract("ReadinessDependent"),
            dependencies: [new(providerContract, ServiceDependencyKind.Hard)]);
        Assert.True(s.Supervisor.Register(provider).IsSuccess);
        Assert.True(s.Supervisor.Register(dependent).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(provider.Identity).Value!.Snapshot.Instance!.Value;
        clock.OnRead = () => Assert.True(s.Supervisor.Drain(instance).Value!.ClosureProven);
        Assert.Equal(KernelError.DependencyUnavailable, s.Supervisor.Start(dependent.Identity).Error);
        ServiceSupervisorSnapshot after = s.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Stopped, after.State);
        Assert.Null(after.Instance);
        Assert.Null(after.Health);
        Assert.Empty(after.Dependencies);
        Assert.True(s.Kernel.QueryComponent(dependent.Identity).Value!.Reclaimable);
    }

    [Fact]
    public void LateAdmissionCompensationCannotDrainOrOverwriteReplacement()
    {
        SupervisorScenario s = CreateSupervisor();
        ServiceContractIdentity contract = Contract("CompensationAba");
        ManagedServiceDefinition definition = Definition("compensation-aba", 249, 2049, contract);
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        ManagedServiceDefinition next = Definition("compensation-aba", 249, 2049, contract, generation: 2);
        Assert.True(s.Supervisor.Replace(instance, next).IsSuccess);
        MethodInfo compensate = typeof(CapabilityAwareServiceSupervisor).GetMethod("CompensateIncompleteAdmission", BindingFlags.Instance | BindingFlags.NonPublic)!;
        KernelResult<ServiceStartReceipt> refused = (KernelResult<ServiceStartReceipt>)compensate.Invoke(s.Supervisor,
            [definition.Identity, definition, instance.Process, KernelError.PlatformFaulted, "Late old completion"] )!;
        Assert.Equal(KernelError.PlatformFaulted, refused.Error);
        ServiceSupervisorSnapshot fresh = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, fresh.State);
        Assert.Equal(2ul, fresh.Instance!.Value.Process.Generation);
        Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(definition.Identity).Value!.State);
        Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(fresh.Instance.Value.Process).Value!.State);
    }

    [Fact]
    public async Task BlockedReadinessClockAllowsQueryAndSourceRevocation()
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ManagedServiceDefinition definition = Definition("readiness-blocked", 250, 2050, Contract("ReadinessBlocked"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnRead = () => { entered.SetResult(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10))); };
        Task<KernelResult<ServiceStartReceipt>> admitting = Task.Run(() => s.Supervisor.Start(definition.Identity));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ServiceLifecycleState.Admitting, s.Supervisor.Query(definition.Identity).Value!.State);
            Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
        }
        finally { resume.Set(); }
        Assert.Equal(KernelError.SupervisorDenied, (await admitting.WaitAsync(TimeSpan.FromSeconds(10))).Error);
        Assert.Null(InspectSupervisorRecord(s.Supervisor, definition.Identity).Instance);
        Assert.True(s.Kernel.QueryComponent(definition.Identity).Value!.Reclaimable);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(false, 3)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)] [InlineData(true, 3)]
    public void DrainDeadlinePublicationRefusesClockFaultOrSourceLoss(bool quarantine, int fault)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorTimeProvider kernelClock = new SupervisorTimeProvider();
        SupervisorScenario s = CreateSupervisor(clock, kernelClock);
        ManagedServiceDefinition definition = Definition("deadline-commit", 243, 2043, Contract("DeadlineCommit"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceSupervisorSnapshot before = s.Supervisor.Start(definition.Identity).Value!.Snapshot;
        ServiceInstanceHandle instance = before.Instance!.Value;
        SubmittedOperation operation = SubmitOperation(s.Kernel, instance.Process);
        CancellationScopeHandle scope = s.Kernel.CreateCancellationScope(instance.Process, new(1)).Value!.Scope;
        kernelClock.AdvanceMilliseconds(1);
        clock.OnRead = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            if (fault == 3) throw new InvalidOperationException("Timeout publication clock fault.");
        };
        ServiceDrainTimeoutPolicy policy = quarantine ? ServiceDrainTimeoutPolicy.Quarantine : ServiceDrainTimeoutPolicy.FailReplacement;
        KernelResult<ServiceDrainReceipt> refused = s.Supervisor.Drain(instance, scope, policy);
        Assert.Equal(fault == 3 ? KernelError.PlatformFaulted : KernelError.SupervisorDenied, refused.Error);
        ServiceSupervisorSnapshot after = InspectSupervisorRecord(s.Supervisor, definition.Identity);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Health, after.Health);
        Assert.Equal(before.BlockingReason, after.BlockingReason);
        Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(definition.Identity).Value!.State);
        Assert.Equal(ExternalOperationState.Submitted, s.Kernel.QueryExternalOperation(instance.Process, operation.Operation).Value!.State);
        Assert.False(s.Kernel.ReleaseRegion(instance.Process, operation.Output).IsSuccess);
        if (fault == 3)
        {
            KernelResult<ServiceDrainReceipt> recovered = s.Supervisor.Drain(instance, scope, policy);
            Assert.Equal(quarantine, recovered.IsSuccess);
            if (quarantine) Assert.False(recovered.Value!.ClosureProven);
            else Assert.Equal(KernelError.ReplacementBlocked, recovered.Error);
            Assert.Equal(quarantine ? ServiceLifecycleState.Quarantined : ServiceLifecycleState.Draining,
                s.Supervisor.Query(definition.Identity).Value!.State);
            Assert.False(s.Kernel.QueryComponent(definition.Identity).Value!.Reclaimable);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DrainDeadlineClockCannotPublishOntoReplacement(bool quarantine)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorTimeProvider kernelClock = new SupervisorTimeProvider();
        SupervisorScenario s = CreateSupervisor(clock, kernelClock);
        ServiceContractIdentity contract = Contract("DeadlineAba");
        ManagedServiceDefinition definition = Definition("deadline-aba", 244, 2044, contract);
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        CancellationScopeHandle scope = s.Kernel.CreateCancellationScope(instance.Process, new(1)).Value!.Scope;
        kernelClock.AdvanceMilliseconds(1);
        clock.OnRead = () => Assert.True(s.Supervisor.Replace(instance,
            Definition("deadline-aba", 244, 2044, contract, generation: 2)).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, s.Supervisor.Drain(instance, scope,
            quarantine ? ServiceDrainTimeoutPolicy.Quarantine : ServiceDrainTimeoutPolicy.FailReplacement).Error);
        ServiceSupervisorSnapshot fresh = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, fresh.State);
        Assert.Equal(2ul, fresh.Instance!.Value.Process.Generation);
        Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(fresh.Instance.Value.Process).Value!.State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DeadlinePublicationCannotRegressActualStoppedSameInstance(bool quarantine)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorTimeProvider kernelClock = new SupervisorTimeProvider();
        SupervisorScenario s = CreateSupervisor(clock, kernelClock);
        ManagedServiceDefinition definition = Definition("deadline-same-instance", 246, 2046, Contract("DeadlineSameInstance"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        CancellationScopeHandle scope = s.Kernel.CreateCancellationScope(instance.Process, new(1)).Value!.Scope;
        kernelClock.AdvanceMilliseconds(1);
        clock.OnRead = () => Assert.True(s.Supervisor.Drain(instance).Value!.ClosureProven);
        KernelResult<ServiceDrainReceipt> result = s.Supervisor.Drain(instance, scope,
            quarantine ? ServiceDrainTimeoutPolicy.Quarantine : ServiceDrainTimeoutPolicy.FailReplacement);
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        ServiceSupervisorSnapshot current = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(instance, current.Instance);
        Assert.Equal(ServiceLifecycleState.Stopped, current.State);
        Assert.Equal(ServiceHealthState.Stopped, current.Health!.State);
        Assert.Null(current.BlockingReason);
        Assert.True(s.Kernel.QueryComponent(definition.Identity).Value!.Reclaimable);
        Assert.Equal(ProcessState.Exited, s.Kernel.Processes.ResolveTerminalState(instance.Process).Value);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BlockedDeadlinePublicationClockAllowsQueryAndRevoke(bool quarantine)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorTimeProvider kernelClock = new SupervisorTimeProvider();
        SupervisorScenario s = CreateSupervisor(clock, kernelClock);
        ManagedServiceDefinition definition = Definition("deadline-blocked", 245, 2045, Contract("DeadlineBlocked"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        CancellationScopeHandle scope = s.Kernel.CreateCancellationScope(instance.Process, new(1)).Value!.Scope;
        kernelClock.AdvanceMilliseconds(1);
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnRead = () => { entered.SetResult(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10))); };
        Task<KernelResult<ServiceDrainReceipt>> publishing = Task.Run(() => s.Supervisor.Drain(instance, scope,
            quarantine ? ServiceDrainTimeoutPolicy.Quarantine : ServiceDrainTimeoutPolicy.FailReplacement));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ServiceLifecycleState.Ready, s.Supervisor.Query(definition.Identity).Value!.State);
            Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
        }
        finally { resume.Set(); }
        Assert.Equal(KernelError.SupervisorDenied, (await publishing.WaitAsync(TimeSpan.FromSeconds(10))).Error);
        Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(definition.Identity).Value!.State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void DrainRequestClockCannotStartTeardownAfterSourceLossOrFault(int fault)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ManagedServiceDefinition definition = Definition("drain-request", 240, 2040, Contract("DrainRequest"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceSupervisorSnapshot before = s.Supervisor.Start(definition.Identity).Value!.Snapshot;
        ServiceInstanceHandle instance = before.Instance!.Value;
        clock.OnRead = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            if (fault == 3) throw new InvalidOperationException("Drain request clock fault.");
        };
        Assert.Equal(fault == 3 ? KernelError.PlatformFaulted : KernelError.SupervisorDenied,
            s.Supervisor.Drain(instance).Error);
        ServiceSupervisorSnapshot after = InspectSupervisorRecord(s.Supervisor, definition.Identity);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Health, after.Health);
        Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(definition.Identity).Value!.State);
        Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(instance.Process).Value!.State);
        Assert.Equal(KernelError.InvalidTransition, s.Kernel.QueryProcessTeardown(instance.Process).Error);
        if (fault == 3) Assert.True(s.Supervisor.Drain(instance).Value!.ClosureProven);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DrainRequestClockCannotRestartIntentAfterSameInstanceActualClosure(bool planned)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ManagedServiceDefinition definition = Definition("drain-intent-source", 247, 2047, Contract("DrainIntentSource"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        int effects = 0;
        s.Kernel.RegisterComposedWorkTeardownParticipant(new SupervisorClosureCallback(() =>
        {
            effects++;
            return KernelResult.Ok();
        }, instance.Process));
        clock.OnRead = () => Assert.True(s.Supervisor.Drain(instance).Value!.ClosureProven);
        Assert.Equal(KernelError.StaleGeneration, s.Supervisor.Drain(instance, planned).Error);
        ServiceSupervisorSnapshot current = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(instance, current.Instance);
        Assert.Equal(ServiceLifecycleState.Stopped, current.State);
        Assert.Equal(ServiceHealthState.Stopped, current.Health!.State);
        Assert.Null(current.BlockingReason);
        Assert.Equal(1, effects);
        Assert.True(s.Kernel.QueryComponent(definition.Identity).Value!.Reclaimable);
        Assert.Equal(ProcessState.Exited, s.Kernel.Processes.ResolveTerminalState(instance.Process).Value);
    }

    [Fact]
    public void DrainRequestClockReplacementCannotDrainFreshProcess()
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("RequestAba");
        ManagedServiceDefinition definition = Definition("request-aba", 241, 2041, contract);
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        clock.OnRead = () => Assert.True(s.Supervisor.Replace(instance,
            Definition("request-aba", 241, 2041, contract, generation: 2)).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, s.Supervisor.Drain(instance).Error);
        ServiceSupervisorSnapshot fresh = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, fresh.State);
        Assert.Equal(2ul, fresh.Instance!.Value.Process.Generation);
        Assert.Equal(KernelError.StaleGeneration, s.Kernel.DrainComponent(definition.Identity, instance.Process).Error);
        Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(fresh.Instance.Value.Process).Value!.State);
        Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(definition.Identity).Value!.State);
    }

    [Fact]
    public async Task BlockedDrainRequestClockAllowsQueryAndSourceRevoke()
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ManagedServiceDefinition definition = Definition("request-blocked", 242, 2042, Contract("RequestBlocked"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnRead = () => { entered.SetResult(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10))); };
        Task<KernelResult<ServiceDrainReceipt>> requesting = Task.Run(() => s.Supervisor.Drain(instance));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ServiceLifecycleState.Ready, s.Supervisor.Query(definition.Identity).Value!.State);
            Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
        }
        finally { resume.Set(); }
        Assert.Equal(KernelError.SupervisorDenied, (await requesting.WaitAsync(TimeSpan.FromSeconds(10))).Error);
        Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(definition.Identity).Value!.State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DrainCompletionClockFaultPreservesPendingOrUnknownEffect(bool providerLost)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ManagedServiceDefinition definition = Definition("drain-effect", 239, 2039, Contract("DrainEffect"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        SubmittedOperation operation = SubmitOperation(s.Kernel, instance.Process);
        if (providerLost)
            Assert.True(s.Kernel.RecordExternalOperationProviderLoss(instance.Process, operation.Operation).IsSuccess);
        Assert.True(s.Supervisor.Drain(instance).IsSuccess);
        ServiceSupervisorSnapshot before = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(providerLost ? ServiceLifecycleState.Quarantined : ServiceLifecycleState.Draining, before.State);
        clock.OnRead = () => throw new InvalidOperationException("Completion observation fault.");
        Assert.Equal(KernelError.PlatformFaulted, s.Supervisor.Observe(definition.Identity).Error);
        ServiceSupervisorSnapshot after = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Health, after.Health);
        Assert.False(s.Kernel.QueryComponent(definition.Identity).Value!.Reclaimable);
        Assert.True(s.Kernel.Processes.Resolve(instance.Process).IsSuccess);
        Assert.False(s.Kernel.ReleaseRegion(instance.Process, operation.Output).IsSuccess);
        Assert.Equal(before.State, s.Supervisor.Observe(definition.Identity).Value!.State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void DrainCompletionClockCannotPublishAfterSourceLossOrFault(int fault)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ManagedServiceDefinition definition = Definition("drain-commit", 236, 2036, Contract("DrainCommit"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        int calls = 0;
        s.Kernel.RegisterComposedWorkTeardownParticipant(new SupervisorClosureCallback(() =>
            ++calls == 1 ? KernelResult.Fail(KernelError.PlatformFaulted, "Initial refusal.") : KernelResult.Ok(), instance.Process));
        Assert.True(s.Supervisor.Drain(instance).IsSuccess);
        ServiceSupervisorSnapshot before = s.Supervisor.Query(definition.Identity).Value!;
        clock.OnRead = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            if (fault == 3) throw new InvalidOperationException("Completion clock fault.");
        };
        KernelResult<ServiceSupervisorSnapshot> refused = s.Supervisor.Observe(definition.Identity);
        Assert.Equal(fault == 3 ? KernelError.PlatformFaulted : KernelError.SupervisorDenied, refused.Error);
        ServiceSupervisorSnapshot after = InspectSupervisorRecord(s.Supervisor, definition.Identity);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Health, after.Health);
        Assert.Equal(before.BlockingReason, after.BlockingReason);
        Assert.True(s.Kernel.QueryComponent(definition.Identity).Value!.Reclaimable);
        if (fault == 3)
            Assert.Equal(ServiceLifecycleState.Stopped, s.Supervisor.Observe(definition.Identity).Value!.State);
    }

    [Fact]
    public void LateDrainCompletionCannotRegressStoppedSameInstanceAfterClockReentry()
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ManagedServiceDefinition definition = Definition("drain-same-instance", 239, 2039, Contract("DrainSameInstance"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        int calls = 0;
        s.Kernel.RegisterComposedWorkTeardownParticipant(new SupervisorClosureCallback(() =>
        {
            if (++calls != 1) return KernelResult.Ok();
            clock.OnRead = () => Assert.Equal(ServiceLifecycleState.Stopped,
                s.Supervisor.Observe(definition.Identity).Value!.State);
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original cleanup pending.");
        }, instance.Process));
        Assert.Equal(KernelError.StaleGeneration, s.Supervisor.Drain(instance).Error);
        ServiceSupervisorSnapshot current = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(instance, current.Instance);
        Assert.Equal(ServiceLifecycleState.Stopped, current.State);
        Assert.Equal(ServiceHealthState.Stopped, current.Health!.State);
        Assert.Null(current.BlockingReason);
        Assert.True(s.Kernel.QueryComponent(definition.Identity).Value!.Reclaimable);
        Assert.Equal(ProcessState.Exited, s.Kernel.Processes.ResolveTerminalState(instance.Process).Value);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void DrainCompletionClockReplacementCannotOverwriteFreshInstance()
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("DrainAba");
        ManagedServiceDefinition definition = Definition("drain-aba", 237, 2037, contract);
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        int calls = 0;
        s.Kernel.RegisterComposedWorkTeardownParticipant(new SupervisorClosureCallback(() =>
            ++calls == 1 ? KernelResult.Fail(KernelError.PlatformFaulted, "Initial refusal.") : KernelResult.Ok(), instance.Process));
        Assert.True(s.Supervisor.Drain(instance).IsSuccess);
        clock.OnRead = () => Assert.True(s.Supervisor.Replace(instance,
            Definition("drain-aba", 237, 2037, contract, generation: 2)).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, s.Supervisor.Observe(definition.Identity).Error);
        ServiceSupervisorSnapshot fresh = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, fresh.State);
        Assert.Equal(2ul, fresh.Instance!.Value.Process.Generation);
        Assert.Equal(ServiceHealthState.Ready, fresh.Health!.State);
    }

    [Fact]
    public async Task BlockedDrainCompletionClockHoldsNoSupervisorOrCapabilityGate()
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ManagedServiceDefinition definition = Definition("drain-blocked", 238, 2038, Contract("DrainBlocked"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        int calls = 0;
        s.Kernel.RegisterComposedWorkTeardownParticipant(new SupervisorClosureCallback(() =>
            ++calls == 1 ? KernelResult.Fail(KernelError.PlatformFaulted, "Initial refusal.") : KernelResult.Ok(), instance.Process));
        Assert.True(s.Supervisor.Drain(instance).IsSuccess);
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnRead = () => { entered.SetResult(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10))); };
        Task<KernelResult<ServiceSupervisorSnapshot>> completing = Task.Run(() => s.Supervisor.Observe(definition.Identity));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ServiceLifecycleState.Quarantined, s.Supervisor.Query(definition.Identity).Value!.State);
            Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
        }
        finally { resume.Set(); }
        Assert.Equal(KernelError.SupervisorDenied, (await completing.WaitAsync(TimeSpan.FromSeconds(10))).Error);
    }

    private static ServiceSupervisorSnapshot InspectSupervisorRecord(CapabilityAwareServiceSupervisor supervisor, ComponentIdentity identity)
    {
        IDictionary records = (IDictionary)typeof(CapabilityAwareServiceSupervisor).GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(supervisor)!;
        return (ServiceSupervisorSnapshot)typeof(CapabilityAwareServiceSupervisor).GetMethod("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(supervisor, [records[identity]!])!;
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void QuarantinedObserveUsesRealClosureBeforeStopOrRestart(bool planned, bool throws)
    {
        SupervisorTimeProvider clock = new SupervisorTimeProvider();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("QuarantineRecovery");
        ServiceRestartPolicyV1 policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 10, 100, 1000);
        ManagedServiceDefinition definition = Definition("quarantine-recovery", 234, 2034, contract, restart: policy,
            replacementFactory: generation => Plan("quarantine-recovery", 234, 2034, contract, generation, restart: policy));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        BudgetAccountHandle budget = s.Kernel.QueryComponent(definition.Identity).Value!.ProcessBudget;
        int calls = 0;
        s.Kernel.RegisterComposedWorkTeardownParticipant(new SupervisorClosureCallback(() =>
        {
            Assert.Equal(planned ? ProcessState.Exited : ProcessState.Faulted,
                s.Kernel.QueryProcessTeardown(instance.Process).Value!.TargetTerminalState);
            if (++calls != 1) return KernelResult.Ok();
            if (throws) throw new InvalidOperationException("Closure reply lost.");
            return KernelResult.Fail(KernelError.PlatformFaulted, "Existing participant refuses closure.");
        }));
        if (planned) Assert.True(s.Supervisor.Drain(instance).IsSuccess);
        else Assert.True(s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "failure").IsSuccess);
        Assert.Equal(ServiceLifecycleState.Quarantined, s.Supervisor.Query(definition.Identity).Value!.State);
        Assert.True(s.Kernel.Processes.Resolve(instance.Process).IsSuccess);
        Assert.True(s.Kernel.QueryBudget(budget).IsSuccess);
        Assert.Equal(1, calls);

        KernelResult<ServiceSupervisorSnapshot> observed = s.Supervisor.Observe(definition.Identity);
        Assert.True(observed.IsSuccess);
        Assert.Equal(planned ? ServiceLifecycleState.Stopped : ServiceLifecycleState.RestartBackoff, observed.Value!.State);
        Assert.Equal(instance, observed.Value.Instance);
        Assert.Equal(planned ? 0u : 1u, observed.Value.RestartAttemptsInWindow);
        Assert.Equal(2, calls);
        Assert.True(s.Kernel.QueryComponent(definition.Identity).Value!.Reclaimable);
        Assert.Equal(KernelError.StaleHandle, s.Kernel.Processes.Resolve(instance.Process).Error);
        if (!planned)
        {
            clock.AdvanceMilliseconds(10);
            Assert.Equal(2ul, s.Supervisor.Observe(definition.Identity).Value!.Instance!.Value.Process.Generation);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void QuarantinedObserveWithoutControlCannotRetryClosure(int loss)
    {
        SupervisorScenario s = CreateSupervisor();
        ManagedServiceDefinition definition = Definition("quarantine-control", 235, 2035, Contract("QuarantineControl"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        int calls = 0;
        s.Kernel.RegisterComposedWorkTeardownParticipant(new SupervisorClosureCallback(() =>
        {
            calls++;
            return KernelResult.Fail(KernelError.PlatformFaulted, "No exact closure.");
        }, instance.Process));
        Assert.True(s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "failure").IsSuccess);
        Assert.Equal(ServiceLifecycleState.Quarantined, s.Supervisor.Query(definition.Identity).Value!.State);
        if (loss == 0) Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
        if (loss == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
        if (loss == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
        int before = calls;
        Assert.Equal(KernelError.SupervisorDenied, s.Supervisor.Observe(definition.Identity).Error);
        Assert.Equal(before, calls);
        Assert.False(s.Kernel.QueryComponent(definition.Identity).Value!.Reclaimable);
        Assert.True(s.Kernel.Processes.Resolve(instance.Process).IsSuccess);
    }

    private sealed class SupervisorClosureCallback(Func<KernelResult> close, ProcessHandle? subject = null) : IComposedWorkTeardownParticipant
    {
        public KernelResult CloseComposedWorkForProcess(ProcessHandle process, RegionOwner owner) =>
            subject is { } expected && expected != process ? KernelResult.Ok() : close();
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ReplacementSourcePrerequisitesRefuseBeforeOldHierarchyRetirement(int fault)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("SourcePreflight");
        CapabilityRequirementV1 requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "source-preflight", CapabilityRights.Read);
        ServiceRestartPolicyV1 policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        ManagedServiceDefinition definition = Definition("source-preflight", 231, 2031, contract, capability: requirement, issuer: s.IssuerDomain,
            restart: policy, replacementFactory: generation => Plan("source-preflight", 231, 2031, contract, generation,
                restart: policy, capability: requirement, issuer: fault == 0 ? new DomainId(99999) : s.IssuerDomain));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        Assert.True(s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "failure").IsSuccess);
        ComponentLifecycleSnapshot before = s.Kernel.QueryComponent(definition.Identity).Value!;
        if (fault == 1) TestFixtures.Create(s.Kernel, 231, 2031, 2, "other-live-process");
        if (fault == 2) TestFixtures.Create(s.Kernel, 232, 2032, identity: "source-preflight-entry");
        if (fault == 3)
        {
            (SingProcess _, ProcessHandle newer) = TestFixtures.Create(s.Kernel, 231, 2031, 3, "newer-retired-process");
            Assert.True(s.Kernel.TerminateProcess(newer).IsSuccess);
        }
        clock.Timestamp = 100;
        KernelResult<ServiceSupervisorSnapshot> refused = s.Supervisor.Observe(definition.Identity);
        Assert.Equal(fault switch { 0 => KernelError.DomainNotFound, 1 => KernelError.DuplicateProcessId,
            2 => KernelError.DuplicateIdentity, _ => KernelError.StaleGeneration }, refused.Error);
        ComponentLifecycleSnapshot after = s.Kernel.QueryComponent(definition.Identity).Value!;
        Assert.Equal(before.Process, after.Process);
        Assert.Equal(ComponentLifecycleState.Reclaimable, after.State);
        Assert.Equal(before.ProcessBudget, after.ProcessBudget);
        Assert.Equal(before.ServiceBudget, after.ServiceBudget);
        Assert.Equal(ServiceLifecycleState.RestartBackoff, s.Supervisor.Query(definition.Identity).Value!.State);
    }

    [Fact]
    public void ReplacementSelfDomainGrantRemainsRequestUntilRealMintConsumer()
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("SelfDomainReplacement");
        CapabilityRequirementV1 requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "self-domain", CapabilityRights.Read);
        ServiceRestartPolicyV1 policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        ManagedServiceDefinition definition = Definition("self-domain-replacement", 233, 2033, contract, capability: requirement, issuer: s.IssuerDomain,
            restart: policy, replacementFactory: generation => Plan("self-domain-replacement", 233, 2033, contract, generation,
                restart: policy, capability: requirement, issuer: new DomainId(2033)));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        Assert.True(s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "failure").IsSuccess);
        Assert.False(s.Kernel.Domains.Contains(new(2033)));
        ComponentAdmissionPlan next = Plan("self-domain-replacement", 233, 2033, contract, 2, restart: policy,
            capability: requirement, issuer: new DomainId(2033));
        Assert.True(s.Kernel.ValidateReplacementAdmission(next, instance.Process).IsSuccess);
        Assert.False(s.Kernel.Domains.Contains(new(2033)));
        clock.Timestamp = 100;
        KernelResult<ServiceSupervisorSnapshot> replaced = s.Supervisor.Observe(definition.Identity);
        Assert.True(replaced.IsSuccess, replaced.Message);
        ComponentLifecycleSnapshot component = s.Kernel.QueryComponent(definition.Identity).Value!;
        CapabilityId capability = Assert.Single(component.Capabilities);
        Assert.True(s.Kernel.ValidateCapability(component.Process, capability, CapabilityRights.Read).IsSuccess);
        Assert.Equal(2ul, component.Process.Generation);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ReplacementAdmissionPreflightRefusalPreservesOldReclaimableComponent(int fault)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("PreflightReplacement");
        ServiceRestartPolicyV1 policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        bool inject = true;
        ManagedServiceDefinition definition = Definition("preflight-replacement", 229, 2029, contract, restart: policy,
            replacementFactory: generation =>
            {
                ComponentAdmissionPlan plan = Plan("preflight-replacement", 229, 2029, contract, generation, restart: policy,
                    compatibility: inject && fault == 2 ? new ServiceCompatibilityConstraintsV1(2, 2) : null,
                    dependencies: inject && fault == 3 ? [new(Contract("MissingReplacementDependency"), ServiceDependencyKind.Hard)] : null);
                return inject && fault is 0 or 1 ? new ComponentAdmissionPlan(plan.Manifest,
                    fault == 0 ? new byte[] { 0xff } : plan.Image, plan.Grants,
                    fault == 1 ? [] : plan.ProvidedServices) : plan;
            });
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        Assert.True(s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "failure").IsSuccess);
        ComponentLifecycleSnapshot before = s.Kernel.QueryComponent(definition.Identity).Value!;
        clock.Timestamp = 100;
        KernelResult<ServiceSupervisorSnapshot> refused = s.Supervisor.Observe(definition.Identity);
        Assert.Equal(fault switch { 0 => KernelError.ComponentDigestMismatch, 1 => KernelError.InvalidManifest,
            2 => KernelError.PlatformUnsupported, _ => KernelError.ServiceNotFound }, refused.Error);
        ComponentLifecycleSnapshot after = s.Kernel.QueryComponent(definition.Identity).Value!;
        Assert.Equal(before.Process, after.Process);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.ManifestDigest, after.ManifestDigest);
        Assert.Equal(before.ProcessBudget, after.ProcessBudget);
        Assert.Equal(before.ServiceBudget, after.ServiceBudget);
        ServiceSupervisorSnapshot preserved = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.RestartBackoff, preserved.State);
        Assert.Equal(instance, preserved.Instance);
        inject = false;
        Assert.True(s.Supervisor.Observe(definition.Identity).IsSuccess);
        Assert.Equal(2ul, s.Supervisor.Query(definition.Identity).Value!.Instance!.Value.Process.Generation);
    }

    [Fact]
    public void ReplacementEvaluatorBypassRequiresExactClosedOldGeneration()
    {
        SupervisorScenario s = CreateSupervisor();
        ServiceContractIdentity contract = Contract("ExactReplacement");
        ManagedServiceDefinition definition = Definition("exact-replacement", 230, 2030, contract);
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        ComponentAdmissionPlan next = Plan("exact-replacement", 230, 2030, contract, 2);
        Assert.Equal(KernelError.ReplacementBlocked, s.Kernel.ValidateReplacementAdmission(next, instance.Process).Error);
        Assert.Equal(KernelError.StaleGeneration, s.Kernel.ValidateReplacementAdmission(next, s.Principal).Error);
        Assert.True(s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "failure").IsSuccess);
        Assert.Equal(KernelError.DuplicateIdentity, s.Kernel.AdmitComponent(next).Error);
        Assert.True(s.Kernel.ValidateReplacementAdmission(next, instance.Process).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, s.Kernel.ValidateReplacementAdmission(
            Plan("exact-replacement", 230, 2030, contract, 3), instance.Process).Error);
        Assert.Equal(ComponentLifecycleState.Reclaimable, s.Kernel.QueryComponent(definition.Identity).Value!.State);
    }

    [Fact]
    public async Task BlockedRestartFactoryAllowsQueryAndRevokeBeforeRetirement()
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("BlockedFactory");
        ServiceRestartPolicyV1 policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        ManagedServiceDefinition definition = Definition("blocked-factory", 228, 2028, contract, restart: policy,
            replacementFactory: generation =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                return Plan("blocked-factory", 228, 2028, contract, generation, restart: policy);
            });
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        Assert.True(s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "failure").IsSuccess);
        clock.Timestamp = 100;
        Task<KernelResult<ServiceSupervisorSnapshot>> pending = Task.Run(() => s.Supervisor.Observe(definition.Identity));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            KernelResult<ServiceSupervisorSnapshot> query = await Task.Run(() => s.Supervisor.Query(definition.Identity)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(query.IsSuccess);
            Assert.Equal(ServiceLifecycleState.RestartBackoff, query.Value!.State);
            Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.SupervisorDenied, (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Error);
        Assert.Equal(ComponentLifecycleState.Reclaimable, s.Kernel.QueryComponent(definition.Identity).Value!.State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void RestartEligibilityAndFactoryRefusalCannotRetireOldComponent(int fault)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("RestartDispatch");
        ServiceRestartPolicyV1 policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        int calls = 0;
        bool inject = true;
        ManagedServiceDefinition definition = Definition("restart-dispatch", 227, 2027, contract, restart: policy,
            replacementFactory: generation =>
            {
                calls++;
                if (inject && fault == 2) Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
                if (inject && fault == 3) throw new InvalidOperationException("factory fault");
                return Plan(inject && fault == 5 ? "wrong-dispatch" : "restart-dispatch", 227, 2027, contract,
                    inject && fault == 4 ? generation + 1 : generation, restart: policy,
                    componentIdentity: inject && fault == 6 ? new ComponentIdentity("wrong-component") : null);
            });
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        Assert.True(s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "failure").IsSuccess);
        clock.Timestamp = 100;
        if (fault == 0) clock.OnRead = () => throw new InvalidOperationException("eligibility clock fault");
        if (fault == 1) clock.OnRead = () => Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
        KernelResult<ServiceSupervisorSnapshot> refused = s.Supervisor.Observe(definition.Identity);
        Assert.Equal(fault switch { 0 or 3 or 5 => KernelError.PlatformFaulted, 1 or 2 => KernelError.SupervisorDenied,
            4 => KernelError.StaleGeneration, _ => KernelError.InvalidManifest }, refused.Error);
        Assert.Equal(fault < 2 ? 0 : 1, calls);
        KernelResult<ComponentLifecycleSnapshot> component = s.Kernel.QueryComponent(definition.Identity);
        Assert.True(component.IsSuccess);
        Assert.Equal(ComponentLifecycleState.Reclaimable, component.Value!.State);
        if (fault is not 1 and not 2)
        {
            ServiceSupervisorSnapshot snapshot = s.Supervisor.Query(definition.Identity).Value!;
            Assert.Equal(ServiceLifecycleState.RestartBackoff, snapshot.State);
            Assert.Equal(instance, snapshot.Instance);
            Assert.Equal(1u, snapshot.RestartAttemptsInWindow);
            inject = false;
            KernelResult<ServiceSupervisorSnapshot> recovered = s.Supervisor.Observe(definition.Identity);
            Assert.True(recovered.IsSuccess, recovered.Message);
            Assert.Equal(2ul, recovered.Value!.Instance!.Value.Process.Generation);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void RestartFrequencyCallbackRevalidatesSourceAndExactOldInstance(int fault)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("RestartSource");
        ServiceRestartPolicyV1 policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        ManagedServiceDefinition definition = Definition("restart-source", 226, 2026, contract, restart: policy,
            replacementFactory: generation => Plan("restart-source", 226, 2026, contract, generation, restart: policy));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        clock.OnFrequency = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
            else if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            else if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            else Assert.True(s.Supervisor.Replace(instance,
                new ManagedServiceDefinition(Plan("restart-source", 226, 2026, contract, 2, restart: policy), "restart-source")).IsSuccess);
        };
        KernelResult<ServiceHealthSnapshot> refused = s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "failure");
        Assert.Equal(fault == 3 ? KernelError.StaleGeneration : KernelError.SupervisorDenied, refused.Error);
        IDictionary records = (IDictionary)typeof(CapabilityAwareServiceSupervisor).GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Supervisor)!;
        ServiceSupervisorSnapshot snapshot = (ServiceSupervisorSnapshot)typeof(CapabilityAwareServiceSupervisor).GetMethod("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(s.Supervisor, [records[definition.Identity]!])!;
        Assert.Equal(0u, snapshot.RestartAttemptsInWindow);
        Assert.Null(snapshot.RestartEligibleTimestamp);
        Assert.Equal(fault == 3 ? ServiceLifecycleState.Ready : ServiceLifecycleState.Stopped, snapshot.State);
        Assert.Equal(fault == 3 ? 2ul : 1ul, snapshot.Instance!.Value.Process.Generation);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void RestartObservationFaultPreservesStoppedStateAndRetriesThroughObserve(int fault)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("RestartClock");
        ServiceRestartPolicyV1 policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        ManagedServiceDefinition definition = Definition("restart-clock", 225, 2025, contract, restart: policy,
            replacementFactory: generation => Plan("restart-clock", 225, 2025, contract, generation, restart: policy));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        clock.Timestamp = fault == 1 ? long.MaxValue : 10;
        clock.FaultFrequency = fault == 0;
        clock.Frequency = fault == 2 ? 0 : fault == 3 ? -1 : 1000;
        KernelResult<ServiceHealthSnapshot> refused = s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "failure");
        Assert.Equal(KernelError.PlatformFaulted, refused.Error);
        ServiceSupervisorSnapshot stopped = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Stopped, stopped.State);
        Assert.Equal(0u, stopped.RestartAttemptsInWindow);
        Assert.Null(stopped.RestartEligibleTimestamp);
        Assert.Equal(ComponentLifecycleState.Reclaimable, s.Kernel.QueryComponent(definition.Identity).Value!.State);
        clock.Timestamp = 10;
        clock.FaultFrequency = false;
        clock.Frequency = 1000;
        KernelResult<ServiceSupervisorSnapshot> recovered = s.Supervisor.Observe(definition.Identity);
        Assert.True(recovered.IsSuccess, recovered.Message);
        Assert.Equal(ServiceLifecycleState.RestartBackoff, recovered.Value!.State);
        Assert.Equal(1u, recovered.Value.RestartAttemptsInWindow);
        Assert.Equal(instance, recovered.Value.Instance);
    }

    [Fact]
    public async Task HealthClockObservationDoesNotHoldSupervisorOrCapabilityGate()
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ManagedServiceDefinition definition = Definition("health-unlocked", 224, 2024, Contract("HealthUnlocked"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        Assert.True(s.Supervisor.Start(definition.Identity).IsSuccess);
        ServiceSupervisorSnapshot before = s.Supervisor.Query(definition.Identity).Value!;
        using ManualResetEventSlim observed = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        clock.OnRead = () =>
        {
            observed.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        Task<KernelResult<ServiceHealthSnapshot>> pending = Task.Run(() => s.Supervisor.ReportHealth(before.Instance!.Value, ServiceHealthState.Degraded, "late"));
        try
        {
            Assert.True(observed.Wait(TimeSpan.FromSeconds(10)));
            KernelResult<ServiceSupervisorSnapshot> query = await Task.Run(() => s.Supervisor.Query(definition.Identity)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(query.IsSuccess);
            Assert.Equal(before.Health, query.Value!.Health);
            Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.SupervisorDenied, (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Error);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void HealthClockCallbackCannotPublishAfterSourceLossOrClockFault(int fault)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ManagedServiceDefinition definition = Definition("health-clock", 223, 2023, Contract("HealthClock"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        KernelResult<ServiceStartReceipt> started = s.Supervisor.Start(definition.Identity);
        Assert.True(started.IsSuccess, started.Message);
        ServiceSupervisorSnapshot before = s.Supervisor.Query(definition.Identity).Value!;
        clock.OnRead = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
            else if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            else if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            else throw new InvalidOperationException("injected health clock failure");
        };
        KernelResult<ServiceHealthSnapshot> refused = s.Supervisor.ReportHealth(before.Instance!.Value, ServiceHealthState.Degraded, "refused");
        Assert.Equal(fault == 3 ? KernelError.PlatformFaulted : KernelError.SupervisorDenied, refused.Error);
        Assert.Null(refused.Value);
        IDictionary records = (IDictionary)typeof(CapabilityAwareServiceSupervisor).GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Supervisor)!;
        object record = records[definition.Identity]!;
        ServiceSupervisorSnapshot snapshot = (ServiceSupervisorSnapshot)typeof(CapabilityAwareServiceSupervisor).GetMethod("Snapshot", BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(s.Supervisor, [record])!;
        Assert.Equal(before.Health, snapshot.Health);
        Assert.Equal(before.State, snapshot.State);
        if (fault == 3)
            Assert.True(s.Supervisor.ReportHealth(before.Instance.Value, ServiceHealthState.Degraded, "recovered").IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HealthClockCannotOverwriteNewerSameInstanceFact(bool closure)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ManagedServiceDefinition definition = Definition("health-source", 264, 2064, Contract("HealthSource"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        ServiceSupervisorSnapshot? newer = null;
        clock.OnRead = () =>
        {
            if (closure) Assert.True(s.Supervisor.Drain(instance).Value!.ClosureProven);
            else Assert.True(s.Supervisor.ReportHealth(instance, ServiceHealthState.Degraded, "newer health").IsSuccess);
            newer = s.Supervisor.Query(definition.Identity).Value!;
        };
        KernelResult<ServiceHealthSnapshot> refused = s.Supervisor.ReportHealth(instance, ServiceHealthState.Ready, "late health");
        Assert.Equal(KernelError.StaleGeneration, refused.Error);
        ServiceSupervisorSnapshot after = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(newer!.State, after.State);
        Assert.Equal(newer.Health, after.Health);
        Assert.Equal(newer.BlockingReason, after.BlockingReason);
        if (closure)
        {
            Assert.Equal(ServiceLifecycleState.Stopped, after.State);
            Assert.True(s.Kernel.QueryComponent(definition.Identity).Value!.Reclaimable);
        }
        else Assert.True(s.Supervisor.ReportHealth(instance, ServiceHealthState.Degraded, "fresh followup").IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void FailureIntentRequiresFreshAuthorityAndOriginalHealthSource(int fault)
    {
        SupervisorScenario s = CreateSupervisor();
        ManagedServiceDefinition definition = Definition("failure-intent", 265, 2065, Contract("FailureIntent"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        ServiceSupervisorSnapshot? newer = null;
        s.Supervisor.BeforeFailureIntentQualificationHook = () =>
        {
            s.Supervisor.BeforeFailureIntentQualificationHook = null;
            ServiceSupervisorSnapshot published = s.Supervisor.Query(definition.Identity).Value!;
            Assert.Equal(ServiceLifecycleState.Ready, published.State);
            Assert.Equal(fault == 4 ? ServiceHealthState.Unhealthy : ServiceHealthState.Failed, published.Health!.State);
            if (fault == 0) Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            if (fault == 3) Assert.True(s.Supervisor.Drain(instance).Value!.ClosureProven);
            if (fault == 4) Assert.True(s.Supervisor.ReportHealth(instance, ServiceHealthState.Degraded, "newer").IsSuccess);
            if (fault >= 3) newer = s.Supervisor.Query(definition.Identity).Value!;
        };
        KernelResult<ServiceHealthSnapshot> refused = s.Supervisor.ReportHealth(instance,
            fault == 4 ? ServiceHealthState.Unhealthy : ServiceHealthState.Failed, "failure observation");
        Assert.Equal(fault < 3 ? KernelError.SupervisorDenied : KernelError.StaleGeneration, refused.Error);
        ServiceSupervisorSnapshot after = InspectSupervisorRecord(s.Supervisor, definition.Identity);
        if (fault < 3)
        {
            Assert.Equal(ServiceLifecycleState.Ready, after.State);
            Assert.Equal(ServiceHealthState.Failed, after.Health!.State);
            Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(instance.Process).Value!.State);
            Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(definition.Identity).Value!.State);
        }
        else
        {
            Assert.Equal(newer!.State, after.State);
            Assert.Equal(newer.Health, after.Health);
            Assert.Equal(newer.BlockingReason, after.BlockingReason);
            if (fault == 3) Assert.True(s.Kernel.QueryComponent(definition.Identity).Value!.Reclaimable);
        }
        Assert.Equal(0U, after.RestartAttemptsInWindow);
    }

    [Fact]
    public void RestartClockPreservesNewerStoppedHealthAndRetriesThroughObserve()
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("RestartHealthSource");
        ServiceRestartPolicyV1 policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        ManagedServiceDefinition definition = Definition("restart-health-source", 266, 2066, contract, restart: policy,
            replacementFactory: generation => Plan("restart-health-source", 266, 2066, contract, generation, restart: policy));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        ServiceSupervisorSnapshot? newer = null;
        clock.OnFrequency = () =>
        {
            Assert.Equal(ServiceLifecycleState.Stopped, s.Supervisor.Query(definition.Identity).Value!.State);
            Assert.True(s.Supervisor.ReportHealth(instance, ServiceHealthState.Degraded, "newer stopped observation").IsSuccess);
            newer = s.Supervisor.Query(definition.Identity).Value!;
        };
        Assert.Equal(KernelError.StaleGeneration, s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "failure").Error);
        ServiceSupervisorSnapshot after = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Stopped, after.State);
        Assert.Equal(newer!.Health, after.Health);
        Assert.Equal(newer.BlockingReason, after.BlockingReason);
        Assert.Equal(0U, after.RestartAttemptsInWindow);
        Assert.Null(after.RestartEligibleTimestamp);
        Assert.True(s.Kernel.QueryComponent(definition.Identity).Value!.Reclaimable);
        KernelResult<ServiceSupervisorSnapshot> retried = s.Supervisor.Observe(definition.Identity);
        Assert.True(retried.IsSuccess, retried.Message);
        Assert.Equal(ServiceLifecycleState.RestartBackoff, retried.Value!.State);
        Assert.Equal(1U, retried.Value.RestartAttemptsInWindow);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailureHealthReceiptCannotBeRedirectedToReplacement(bool beforeSnapshot)
    {
        SupervisorScenario s = CreateSupervisor();
        ServiceContractIdentity contract = Contract("FailureReceipt");
        ManagedServiceDefinition definition = Definition("failure-receipt", 267, 2067, contract);
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        ServiceHealthSnapshot? original = null;
        Action replace = () =>
        {
            original = s.Supervisor.Query(definition.Identity).Value!.Health;
            Assert.True(s.Kernel.QueryComponent(definition.Identity).Value!.Reclaimable);
            ManagedServiceDefinition next = Definition("failure-receipt", 267, 2067, contract, generation: 2);
            Assert.True(s.Supervisor.Replace(instance, next).IsSuccess);
        };
        if (beforeSnapshot) s.Supervisor.BeforeFailureSnapshotQualificationHook = replace;
        else s.Supervisor.AfterFailureCompletionQualificationHook = replace;
        KernelResult<ServiceHealthSnapshot> result = s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "original failure");
        if (beforeSnapshot) Assert.Equal(KernelError.StaleGeneration, result.Error);
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(original, result.Value);
            Assert.Equal(instance, result.Value!.Instance);
        }
        ServiceSupervisorSnapshot fresh = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(2UL, fresh.Instance!.Value.Process.Generation);
        Assert.Equal(ServiceLifecycleState.Ready, fresh.State);
        Assert.Equal(ServiceHealthState.Ready, fresh.Health!.State);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    public void StartControllerLossRefusesBeforeComponentAllocation(bool allocation, int fault)
    {
        SupervisorScenario s = CreateSupervisor();
        ManagedServiceDefinition definition = Definition("controller-allocation", 268, 2068, Contract("ControllerAllocation"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        Action lose = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
        };
        if (allocation) s.Kernel.BeforeComponentAdmissionCommitQualificationHook = lose;
        else s.Supervisor.BeforeStartIntentQualificationHook = lose;
        Assert.Equal(KernelError.SupervisorDenied, s.Supervisor.Start(definition.Identity).Error);
        ServiceSupervisorSnapshot after = InspectSupervisorRecord(s.Supervisor, definition.Identity);
        Assert.Equal(allocation ? ServiceLifecycleState.Failed : ServiceLifecycleState.Declared, after.State);
        Assert.Null(after.Instance);
        Assert.Null(after.Health);
        Assert.Equal(KernelError.ComponentNotFound, s.Kernel.QueryComponent(definition.Identity).Error);
        Assert.False(s.Kernel.Processes.Resolve(new ProcessHandle(definition.Admission.Manifest.Process.ProcessId, 1)).IsSuccess);
        Assert.False(s.Kernel.Budgets.HasProcessAccount(new ProcessHandle(definition.Admission.Manifest.Process.ProcessId, 1)));
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    public void ControllerCreationRefusalRetiresOnlyExactUncreatedHierarchy(bool foreign, int fault)
    {
        SupervisorScenario s = CreateSupervisor();
        ManagedServiceDefinition definition = Definition("controller-creation", 269, 2069, Contract("ControllerCreation"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ProcessHandle process = new ProcessHandle(definition.Admission.Manifest.Process.ProcessId, 1);
        s.Kernel.BeforeComponentProcessCreationQualificationHook = () =>
        {
            Assert.True(s.Kernel.Budgets.HasProcessAccount(process));
            if (foreign) Assert.True(s.Kernel.CreateProcess(definition.Admission.Manifest.Process).IsSuccess);
            if (fault == 0) Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
        };
        Assert.Equal(KernelError.SupervisorDenied, s.Supervisor.Start(definition.Identity).Error);
        Assert.False(s.Kernel.Processes.ResolveTerminalState(process).IsSuccess);
        Assert.Equal(foreign, s.Kernel.Budgets.HasProcessAccount(process));
        if (foreign)
        {
            Assert.Equal(ProcessState.Created, s.Kernel.Processes.Resolve(process).Value!.State);
            ComponentLifecycleSnapshot retained = s.Kernel.QueryComponent(definition.Identity).Value!;
            Assert.Equal(ComponentLifecycleState.Faulted, retained.State);
            Assert.False(retained.Reclaimable);
            Assert.True(s.Kernel.QueryBudget(retained.ProcessBudget).IsSuccess);
        }
        else
        {
            Assert.False(s.Kernel.Processes.Resolve(process).IsSuccess);
            Assert.Equal(KernelError.ComponentNotFound, s.Kernel.QueryComponent(definition.Identity).Error);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ControllerLossBeforeGrantMintRefusesAndReclaimsCreatedProcess(int fault)
    {
        SupervisorScenario s = CreateSupervisor();
        CapabilityRequirementV1 requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "controller-mint", CapabilityRights.Read);
        ManagedServiceDefinition definition = Definition("controller-mint", 270, 2070, Contract("ControllerMint"),
            capability: requirement, issuer: new DomainId(2070));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        s.Kernel.BeforeComponentGrantMintQualificationHook = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
        };
        Assert.Equal(KernelError.SupervisorDenied, s.Supervisor.Start(definition.Identity).Error);
        ComponentLifecycleSnapshot component = s.Kernel.QueryComponent(definition.Identity).Value!;
        Assert.Empty(component.Capabilities);
        Assert.Equal(ComponentLifecycleState.Reclaimable, component.State);
        Assert.True(s.Kernel.Processes.ResolveTerminalState(component.Process).IsSuccess);
        Assert.True(s.Kernel.Budgets.HasProcessAccount(component.Process));
        Assert.DoesNotContain(s.Kernel.CapabilityAuthority.InspectionSnapshot(),
            record => record.Descriptor.ResourceId == "controller-mint");
        Assert.True(s.Kernel.RetireReclaimableComponent(definition.Identity, component.Process).IsSuccess);
        Assert.False(s.Kernel.Budgets.HasProcessAccount(component.Process));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ControllerLossBeforeProcessAdmissionPreservesMintFactAndUsesExactCleanup(int fault)
    {
        SupervisorScenario s = CreateSupervisor();
        CapabilityRequirementV1 requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "controller-admit", CapabilityRights.Read);
        ManagedServiceDefinition definition = Definition("controller-admit", 271, 2071, Contract("ControllerAdmit"),
            capability: requirement, issuer: new DomainId(2071));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        CapabilityId minted = default;
        s.Kernel.BeforeComponentProcessAdmissionQualificationHook = () =>
        {
            ComponentLifecycleSnapshot before = s.Kernel.QueryComponent(definition.Identity).Value!;
            minted = Assert.Single(before.Capabilities);
            Assert.Equal(ProcessState.Created, s.Kernel.Processes.Resolve(before.Process).Value!.State);
            if (fault == 0) Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
        };
        Assert.Equal(KernelError.SupervisorDenied, s.Supervisor.Start(definition.Identity).Error);
        ComponentLifecycleSnapshot component = s.Kernel.QueryComponent(definition.Identity).Value!;
        Assert.Empty(component.Capabilities);
        Assert.Equal(ComponentLifecycleState.Reclaimable, component.State);
        Assert.True(s.Kernel.Processes.ResolveTerminalState(component.Process).IsSuccess);
        Assert.False(s.Kernel.CapabilityAuthority.Validate(minted, new DomainId(2071), 1, CapabilityRights.Read).IsSuccess);
        Assert.Single(s.Kernel.CapabilityAuthority.InspectionSnapshot(), record => record.Descriptor.CapabilityId == minted);
        Assert.True(s.Kernel.Budgets.HasProcessAccount(component.Process));
        Assert.True(s.Kernel.RetireReclaimableComponent(definition.Identity, component.Process).IsSuccess);
        Assert.False(s.Kernel.Budgets.HasProcessAccount(component.Process));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ControllerLossBeforeRegistrationCreatesNoServiceAndUsesActualCleanup(int fault)
    {
        SupervisorScenario s = CreateSupervisor();
        ServiceContractIdentity contract = Contract("ControllerRegister");
        ManagedServiceDefinition definition = Definition("controller-register", 272, 2072, contract);
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        s.Kernel.BeforeComponentServiceRegistrationQualificationHook = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
        };
        Assert.Equal(KernelError.SupervisorDenied, s.Supervisor.Start(definition.Identity).Error);
        Assert.Empty(s.Kernel.ResolveByContract(contract).Value!);
        ComponentLifecycleSnapshot component = s.Kernel.QueryComponent(definition.Identity).Value!;
        Assert.Equal(ComponentLifecycleState.Reclaimable, component.State);
        Assert.True(s.Kernel.Processes.ResolveTerminalState(component.Process).IsSuccess);
        Assert.True(s.Kernel.RetireReclaimableComponent(definition.Identity, component.Process).IsSuccess);
    }

    [Fact]
    public void RegistrationTraceClockAllowsIndependentLifecycleCommit()
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        Assert.True(s.Kernel.StartTraceSession(s.Principal, 16).IsSuccess);
        bool completed = false;
        clock.OnRead = () =>
        {
            Task<KernelResult<SingProcess>> task = Task.Run(() => s.Kernel.CreateProcess(TestFixtures.Manifest(273, 2073)));
            completed = task.Wait(TimeSpan.FromSeconds(3));
            if (completed) Assert.True(task.Result.IsSuccess);
        };
        ServiceContractIdentity contract = Contract("TraceRegister");
        KernelResult<ServiceEndpointDescriptor> result = s.Kernel.RegisterService(s.Principal, "trace-register", contract, Protocol(contract));
        Assert.True(result.IsSuccess, result.Message);
        Assert.True(completed, "Trace clock ran while lifecycle scope blocked independent creation.");
        Assert.Single(s.Kernel.ResolveByContract(contract).Value!);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    public void ControllerDependencySessionRefusalDoesNotAllocateOrDowngrade(bool optional, int fault)
    {
        SupervisorScenario s = CreateSupervisor();
        ServiceContractIdentity contract = Contract("SessionSourceProvider");
        ManagedServiceDefinition provider = Definition("session-source-provider", 274, 2074, contract);
        Assert.True(s.Supervisor.Register(provider).IsSuccess);
        ServiceInstanceHandle liveProvider = s.Supervisor.Start(provider.Identity).Value!.Snapshot.Instance!.Value;
        ManagedServiceDefinition dependent = Definition("session-source-dependent", 275, 2075, Contract("SessionSourceDependent"),
            dependencies: [new(contract, optional ? ServiceDependencyKind.Optional : ServiceDependencyKind.Hard)]);
        Assert.True(s.Supervisor.Register(dependent).IsSuccess);
        s.Kernel.BeforeComponentDependencySessionQualificationHook = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
        };
        Assert.Equal(KernelError.SupervisorDenied, s.Supervisor.Start(dependent.Identity).Error);
        ComponentLifecycleSnapshot component = s.Kernel.QueryComponent(dependent.Identity).Value!;
        Assert.Empty(s.Kernel.EndpointSessions.SnapshotForProcess(component.Process));
        Assert.Empty(s.Kernel.EndpointSessions.SnapshotForProcess(liveProvider.Process));
        Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(liveProvider.Process).Value!.State);
        Assert.Single(s.Kernel.ResolveByContract(contract).Value!);
        Assert.Equal(ComponentLifecycleState.Reclaimable, component.State);
        Assert.True(s.Kernel.Processes.ResolveTerminalState(component.Process).IsSuccess);
        Assert.True(s.Kernel.RetireReclaimableComponent(dependent.Identity, component.Process).IsSuccess);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    public void ControllerExecutionRefusalUsesActualCleanupBeforeAndAfterRunning(int stage, int fault)
    {
        SupervisorScenario s = CreateSupervisor();
        ServiceContractIdentity contract = Contract("ControllerStart");
        ManagedServiceDefinition definition = Definition("controller-start", 276, 2076, contract);
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        Action lose = () =>
        {
            ProcessHandle process = s.Kernel.QueryComponent(definition.Identity).Value!.Process;
            Assert.Equal(stage == 2 ? ProcessState.Running : ProcessState.Admitted,
                s.Kernel.Processes.Resolve(process).Value!.State);
            if (fault == 0) Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
        };
        if (stage == 0) s.Kernel.BeforeComponentStartingPublicationQualificationHook = lose;
        if (stage == 1) s.Kernel.BeforeComponentExecutionStartQualificationHook = lose;
        if (stage == 2) s.Kernel.BeforeComponentRunningPublicationQualificationHook = lose;
        Assert.Equal(KernelError.SupervisorDenied, s.Supervisor.Start(definition.Identity).Error);
        ComponentLifecycleSnapshot component = s.Kernel.QueryComponent(definition.Identity).Value!;
        Assert.Equal(ComponentLifecycleState.Reclaimable, component.State);
        Assert.True(s.Kernel.Processes.ResolveTerminalState(component.Process).IsSuccess);
        Assert.Empty(s.Kernel.ResolveByContract(contract).Value!);
        Assert.True(s.Kernel.RetireReclaimableComponent(definition.Identity, component.Process).IsSuccess);
    }

    private sealed class CallbackHealthClock : TimeProvider
    {
        public Action? OnRead { get; set; }
        public long Timestamp { get; set; } = 10;
        public bool FaultFrequency { get; set; }
        public long Frequency { get; set; } = 1000;
        public Action? OnFrequency { get; set; }
        public override long TimestampFrequency
        {
            get
            {
                Action? action = OnFrequency;
                OnFrequency = null;
                action?.Invoke();
                return FaultFrequency ? throw new InvalidOperationException("injected frequency fault") : Frequency;
            }
        }
        public override long GetTimestamp()
        {
            Action? action = OnRead;
            OnRead = null;
            action?.Invoke();
            return Timestamp;
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task QueryWaitingOnSupervisorGateCannotPublishAfterSourceRevocation(int fault)
    {
        SupervisorScenario s = CreateSupervisor();
        ManagedServiceDefinition definition = Definition("revoked-query", 222, 2022, Contract("RevokedQuery"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        Assert.Equal(ServiceLifecycleState.Declared, s.Supervisor.Query(definition.Identity).Value!.State);
        object gate = typeof(CapabilityAwareServiceSupervisor).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Supervisor)!;
        TaskCompletionSource<KernelResult<ServiceSupervisorSnapshot>> completion = new TaskCompletionSource<KernelResult<ServiceSupervisorSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread worker = new Thread(() =>
        {
            try { completion.TrySetResult(s.Supervisor.Query(definition.Identity)); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        lock (gate)
        {
            worker.Start();
            Assert.True(SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(10)));
            Assert.False(completion.Task.IsCompleted);
            if (fault == 0) Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
            else if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            else Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
        }
        KernelResult<ServiceSupervisorSnapshot> refused = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(KernelError.SupervisorDenied, refused.Error);
        Assert.Null(refused.Value);
        IDictionary records = (IDictionary)typeof(CapabilityAwareServiceSupervisor).GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Supervisor)!;
        Assert.Single(records);
        Assert.Equal(KernelError.ProcessNotFound, s.Kernel.Processes.Resolve(new(new(222), 1)).Error);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task RegistrationWaitingOnSupervisorGateCannotPublishAfterSourceRevocation(int fault)
    {
        SupervisorScenario s = CreateSupervisor();
        ManagedServiceDefinition definition = Definition("revoked-registration", 220, 2020, Contract("RevokedRegistration"));
        object gate = typeof(CapabilityAwareServiceSupervisor).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Supervisor)!;
        TaskCompletionSource<KernelResult> completion = new TaskCompletionSource<KernelResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread worker = new Thread(() =>
        {
            try { completion.TrySetResult(s.Supervisor.Register(definition)); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }) { IsBackground = true };
        lock (gate)
        {
            worker.Start();
            // All prerequisite owner gates are free. The worker waits on this held supervisor gate
            // after initial control validation, before local registry publication.
            Assert.True(SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0,
                TimeSpan.FromSeconds(10)));
            Assert.False(completion.Task.IsCompleted);
            if (fault == 0) Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
            else if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            else Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
        }
        KernelResult refused = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(refused.IsSuccess);
        Assert.Equal(KernelError.SupervisorDenied, refused.Error);
        IDictionary records = (IDictionary)typeof(CapabilityAwareServiceSupervisor).GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Supervisor)!;
        Assert.Empty(records);
        Assert.Equal(KernelError.ProcessNotFound, s.Kernel.Processes.Resolve(new(new(220), 1)).Error);
    }

    [Fact]
    public void RegistrationOwnerCommitRejectsStaleAndWrongSourceWithoutCallback()
    {
        SupervisorScenario s = CreateSupervisor();
        bool called = false;
        KernelResult Publish() { called = true; return KernelResult.Ok(); }
        Assert.False(s.Kernel.CapabilityAuthority.CommitSupervisorRecordPublication(s.ControlCapability,
            s.IssuerDomain, s.Principal.Generation + 1, Publish).IsSuccess);
        Assert.False(s.Kernel.CapabilityAuthority.CommitSupervisorRecordPublication(s.ControlCapability,
            new(9999), s.Principal.Generation, Publish).IsSuccess);
        foreach (CapabilityId wrong in new[]
        {
            s.Kernel.MintCapability(s.IssuerDomain, s.Principal, ResourceKind.KernelService,
                CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Read).Value!.CapabilityId,
            s.Kernel.MintCapability(s.IssuerDomain, s.Principal, ResourceKind.KernelService,
                "other-supervisor", CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId,
        })
            Assert.False(s.Kernel.CapabilityAuthority.CommitSupervisorRecordPublication(wrong,
                s.IssuerDomain, s.Principal.Generation, Publish).IsSuccess);
        Assert.False(called);
        ManagedServiceDefinition definition = Definition("valid-registration", 221, 2021, Contract("ValidRegistration"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        Assert.Equal(KernelError.DuplicateIdentity, s.Supervisor.Register(definition).Error);
        Assert.Equal(ServiceLifecycleState.Declared, s.Supervisor.Query(definition.Identity).Value!.State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void DependencyReadinessClockRequiresActualProviderOwners(int fault)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("ActualDependencyProvider");
        ManagedServiceDefinition provider = Definition("actual-provider", 252, 2052, contract);
        ManagedServiceDefinition dependent = Definition("actual-dependent", 253, 2053, Contract("ActualDependent"),
            dependencies: [new(contract, ServiceDependencyKind.Hard)]);
        Assert.True(s.Supervisor.Register(provider).IsSuccess);
        Assert.True(s.Supervisor.Register(dependent).IsSuccess);
        ServiceInstanceHandle instance = s.Supervisor.Start(provider.Identity).Value!.Snapshot.Instance!.Value;
        clock.OnRead = () =>
        {
            Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(dependent.Identity).Value!.State);
            if (fault == 0) Assert.True(s.Kernel.TerminateProcess(instance.Process).IsSuccess);
            if (fault == 1) Assert.True(s.Kernel.ParkProcess(instance.Process).IsSuccess);
            if (fault == 2)
                Assert.True(s.Kernel.SetServiceAvailability(
                    s.Kernel.ResolveByServiceName(provider.PrimaryServiceName).Value!.Service,
                    ServiceAvailability.Unavailable).IsSuccess);
        };
        KernelResult<ServiceStartReceipt> refused = s.Supervisor.Start(dependent.Identity);
        Assert.Equal(fault == 0 ? KernelError.StaleHandle : fault == 1 ? KernelError.InvalidTransition : KernelError.ServiceUnavailable,
            refused.Error);
        ServiceSupervisorSnapshot after = s.Supervisor.Query(dependent.Identity).Value!;
        Assert.Null(after.Instance);
        Assert.Empty(after.Dependencies);
        Assert.Equal(ServiceLifecycleState.Stopped, after.State);
        Assert.True(s.Kernel.QueryComponent(dependent.Identity).Value!.Reclaimable);
        // Supervisor metadata alone still describes the provider as Ready.
        Assert.Equal(ServiceLifecycleState.Ready, s.Supervisor.Query(provider.Identity).Value!.State);
    }

    [Fact]
    public void HardDependenciesStartFirstAndBindExactGeneration()
    {
        SupervisorScenario scenario = CreateSupervisor();
        ManagedServiceDefinition provider = Definition("provider", 201, 2001, Contract("Provider"));
        ManagedServiceDefinition dependent = Definition("dependent", 202, 2002, Contract("Dependent"),
            dependencies: [new(provider.Admission.Manifest.ProvidedContracts[0].Contract, ServiceDependencyKind.Hard)]);
        Assert.True(scenario.Supervisor.Register(dependent).IsSuccess);
        Assert.True(scenario.Supervisor.Register(provider).IsSuccess);

        KernelResult<ServiceStartReceipt[]> started = scenario.Supervisor.StartAll();

        Assert.True(started.IsSuccess, started.Message);
        Assert.Equal(new[] { provider.Identity, dependent.Identity }, started.Value!.Select(receipt => receipt.Request.Identity));
        ServiceSupervisorSnapshot dependentSnapshot = scenario.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, dependentSnapshot.State);
        ServiceDependencyBinding binding = Assert.Single(dependentSnapshot.Dependencies);
        Assert.Equal(scenario.Supervisor.Query(provider.Identity).Value!.Instance, binding.Provider);
        Assert.Equal(dependentSnapshot.Instance, binding.Consumer);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void LateRebindCannotPublishReadyAfterActualHardProviderLoss(int fault)
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity hardContract = Contract("ActualHardProvider");
        ServiceContractIdentity lateContract = Contract("ActualLateProvider");
        ManagedServiceDefinition hard = Definition("actual-hard", 256, 2056, hardContract);
        ManagedServiceDefinition late = Definition("actual-late", 257, 2057, lateContract);
        ManagedServiceDefinition peer = Definition("actual-peer", 258, 2058, Contract("ActualPeer"),
            dependencies: [new(hardContract, ServiceDependencyKind.Hard), new(lateContract, ServiceDependencyKind.Optional)]);
        Assert.True(s.Supervisor.Register(hard).IsSuccess);
        Assert.True(s.Supervisor.Register(peer).IsSuccess);
        ServiceInstanceHandle hardInstance = s.Supervisor.Start(hard.Identity).Value!.Snapshot.Instance!.Value;
        ServiceInstanceHandle peerInstance = s.Supervisor.Start(peer.Identity).Value!.Snapshot.Instance!.Value;
        Assert.Equal(ServiceLifecycleState.Degraded, s.Supervisor.Query(peer.Identity).Value!.State);
        Assert.True(s.Supervisor.Register(late).IsSuccess);
        clock.OnRead = () => clock.OnRead = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.TerminateProcess(hardInstance.Process).IsSuccess);
            if (fault == 1) Assert.True(s.Kernel.ParkProcess(hardInstance.Process).IsSuccess);
            if (fault == 2)
                Assert.True(s.Kernel.SetServiceAvailability(s.Kernel.ResolveByServiceName(hard.PrimaryServiceName).Value!.Service,
                    ServiceAvailability.Unavailable).IsSuccess);
        };
        KernelResult<ServiceStartReceipt> refused = s.Supervisor.Start(late.Identity);
        Assert.Equal(fault == 0 ? KernelError.StaleHandle : fault == 1 ? KernelError.InvalidTransition : KernelError.ServiceUnavailable,
            refused.Error);
        ServiceSupervisorSnapshot after = s.Supervisor.Query(peer.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Degraded, after.State);
        Assert.Equal(hardContract, Assert.Single(after.Dependencies).Contract);
        // Successful local session admission remains an exact cleanup fact despite publication refusal.
        IReadOnlyList<EndpointSessionHandle> tracked = s.Kernel.QueryComponent(peer.Identity).Value!.Sessions;
        ServiceSupervisorSnapshot lateSnapshot = s.Supervisor.Query(late.Identity).Value!;
        Assert.Contains(tracked, session =>
        {
            KernelResult<EndpointSessionRegistry.Record> actual = s.Kernel.EndpointSessions.Resolve(session, peerInstance.Process);
            return actual.IsSuccess && actual.Value!.Service == lateSnapshot.Instance!.Value.Process;
        });
        Assert.Equal(ServiceLifecycleState.Ready, lateSnapshot.State);
        Assert.True(s.Supervisor.Drain(peerInstance).Value!.ClosureProven);
        Assert.True(s.Kernel.QueryComponent(peer.Identity).Value!.Reclaimable);
    }

    [Fact]
    public async Task LateRebindRetainsSessionButRefusesReadyAfterHardComponentIntent()
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity hardContract = Contract("IntentHard");
        ServiceContractIdentity lateContract = Contract("IntentLate");
        ManagedServiceDefinition hard = Definition("intent-hard", 261, 2061, hardContract);
        ManagedServiceDefinition late = Definition("intent-late", 262, 2062, lateContract);
        ManagedServiceDefinition peer = Definition("intent-rebind-peer", 263, 2063, Contract("IntentRebindPeer"),
            dependencies: [new(hardContract, ServiceDependencyKind.Hard), new(lateContract, ServiceDependencyKind.Optional)]);
        Assert.True(s.Supervisor.Register(hard).IsSuccess);
        Assert.True(s.Supervisor.Register(peer).IsSuccess);
        ServiceInstanceHandle hardInstance = s.Supervisor.Start(hard.Identity).Value!.Snapshot.Instance!.Value;
        ServiceInstanceHandle peerInstance = s.Supervisor.Start(peer.Identity).Value!.Snapshot.Instance!.Value;
        Assert.True(s.Supervisor.Register(late).IsSuccess);
        using ManualResetEventSlim intent = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        Task<KernelResult<ComponentLifecycleSnapshot>>? drain = null;
        s.Kernel.AfterComponentTeardownIntentQualificationHook = () =>
        {
            intent.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        clock.OnRead = () => clock.OnRead = () =>
        {
            drain = Task.Run(() => s.Kernel.DrainComponent(hard.Identity));
            Assert.True(intent.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(hardInstance.Process).Value!.State);
        };
        try
        {
            Assert.Equal(KernelError.InvalidTransition, s.Supervisor.Start(late.Identity).Error);
            ServiceSupervisorSnapshot after = s.Supervisor.Query(peer.Identity).Value!;
            Assert.Equal(ServiceLifecycleState.Degraded, after.State);
            Assert.Equal(hardContract, Assert.Single(after.Dependencies).Contract);
            ServiceInstanceHandle lateInstance = s.Supervisor.Query(late.Identity).Value!.Instance!.Value;
            Assert.Contains(s.Kernel.QueryComponent(peer.Identity).Value!.Sessions, session =>
            {
                KernelResult<EndpointSessionRegistry.Record> actual = s.Kernel.EndpointSessions.Resolve(session, peerInstance.Process);
                return actual.IsSuccess && actual.Value!.Service == lateInstance.Process;
            });
        }
        finally
        {
            s.Kernel.AfterComponentTeardownIntentQualificationHook = null;
            release.Set();
            if (drain is not null) Assert.True((await drain).IsSuccess);
        }
        Assert.True(s.Supervisor.Drain(peerInstance).Value!.ClosureProven);
        Assert.True(s.Kernel.QueryComponent(peer.Identity).Value!.Reclaimable);
    }

    [Fact]
    public void OptionalDependencyAbsenceIsExplicitDegradation()
    {
        SupervisorScenario scenario = CreateSupervisor();
        ManagedServiceDefinition service = Definition("optional", 203, 2003, Contract("OptionalConsumer"),
            dependencies: [new(Contract("Absent"), ServiceDependencyKind.Optional)]);
        Assert.True(scenario.Supervisor.Register(service).IsSuccess);

        KernelResult<ServiceStartReceipt> started = scenario.Supervisor.Start(service.Identity);

        Assert.True(started.IsSuccess, started.Message);
        Assert.Equal(ServiceLifecycleState.Degraded, started.Value!.Snapshot.State);
        Assert.Equal(ServiceHealthState.Degraded, started.Value.Snapshot.Health!.State);
        Assert.Empty(started.Value.Snapshot.Dependencies);
    }

    [Fact]
    public void OptionalDependencyLateArrivalUsesExplicitSessionRebind()
    {
        SupervisorScenario scenario = CreateSupervisor();
        ServiceContractIdentity providerContract = Contract("LateProvider");
        ManagedServiceDefinition dependent = Definition("late-dependent", 213, 2013, Contract("LateDependent"),
            dependencies: [new(providerContract, ServiceDependencyKind.Optional)]);
        Assert.True(scenario.Supervisor.Register(dependent).IsSuccess);
        Assert.True(scenario.Supervisor.Start(dependent.Identity).IsSuccess);
        Assert.Equal(ServiceLifecycleState.Degraded, scenario.Supervisor.Query(dependent.Identity).Value!.State);
        ManagedServiceDefinition provider = Definition("late-provider", 214, 2014, providerContract);

        Assert.True(scenario.Supervisor.Register(provider).IsSuccess);
        Assert.True(scenario.Supervisor.Start(provider.Identity).IsSuccess);

        ServiceSupervisorSnapshot rebound = scenario.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, rebound.State);
        Assert.Single(rebound.Dependencies);
        Assert.Single(scenario.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
    }

    [Fact]
    public void DependencyCycleIsRejectedBeforeProcessEffects()
    {
        SupervisorScenario scenario = CreateSupervisor();
        ServiceContractIdentity contractA = Contract("CycleA");
        ServiceContractIdentity contractB = Contract("CycleB");
        ManagedServiceDefinition a = Definition("cycle-a", 204, 2004, contractA, dependencies: [new(contractB, ServiceDependencyKind.Hard)]);
        ManagedServiceDefinition b = Definition("cycle-b", 205, 2005, contractB, dependencies: [new(contractA, ServiceDependencyKind.Hard)]);
        Assert.True(scenario.Supervisor.Register(a).IsSuccess);

        KernelResult rejected = scenario.Supervisor.Register(b);

        Assert.Equal(KernelError.InvalidManifest, rejected.Error);
        Assert.Equal(KernelError.ProcessNotFound, scenario.Kernel.Processes.Resolve(new(new(205), 1)).Error);
        Assert.Equal(KernelError.ComponentNotFound, scenario.Supervisor.Query(b.Identity).Error);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ReplacementProspectiveGraphRefusesBeforeOriginalDrain(bool duplicate)
    {
        SupervisorScenario s = CreateSupervisor();
        ServiceContractIdentity a = Contract("ReplacementGraphA");
        ServiceContractIdentity b = Contract("ReplacementGraphB");
        ManagedServiceDefinition original = Definition("replacement-graph-a", 1201, 1202, a);
        Assert.True(s.Supervisor.Register(original).IsSuccess);
        ServiceInstanceHandle old = s.Supervisor.Start(original.Identity).Value!.Snapshot.Instance!.Value;
        Assert.True(s.Supervisor.Register(Definition("replacement-graph-b", 1203, 1204, b,
            dependencies: [new(a, ServiceDependencyKind.Hard)])).IsSuccess);
        ServiceSupervisorSnapshot before = s.Supervisor.Query(original.Identity).Value!;
        ComponentLifecycleSnapshot component = s.Kernel.QueryComponent(original.Identity).Value!;
        ManagedServiceDefinition next = Definition("replacement-graph-a", 1201, 1202, duplicate ? b : a, generation: 2,
            dependencies: duplicate ? [] : [new(b, ServiceDependencyKind.Hard)]);
        Assert.Equal(KernelError.InvalidManifest, s.Supervisor.Replace(old, next).Error);
        ServiceSupervisorSnapshot after = s.Supervisor.Query(original.Identity).Value!;
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Health, after.Health);
        Assert.Equal(old, after.Instance);
        Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(original.Identity).Value!.State);
        Assert.True(s.Kernel.QueryBudget(component.ProcessBudget).IsSuccess);
        Assert.True(s.Kernel.Processes.Resolve(old.Process).IsSuccess);
    }

    [Fact]
    public void ReplacementFinalGraphDriftPreservesOldReclaimableAccounts()
    {
        SupervisorScenario s = CreateSupervisor();
        ServiceContractIdentity a = Contract("FinalGraphA");
        ServiceContractIdentity b = Contract("FinalGraphB");
        ManagedServiceDefinition original = Definition("final-graph-a", 1205, 1206, a);
        Assert.True(s.Supervisor.Register(original).IsSuccess);
        ServiceInstanceHandle old = s.Supervisor.Start(original.Identity).Value!.Snapshot.Instance!.Value;
        ComponentLifecycleSnapshot component = s.Kernel.QueryComponent(original.Identity).Value!;
        s.Supervisor.BeforeReplacementRetirementQualificationHook = () =>
            Assert.True(s.Supervisor.Register(Definition("final-graph-b", 1207, 1208, b)).IsSuccess);
        KernelResult<ServiceReplacementReceipt> refused = s.Supervisor.Replace(old,
            Definition("final-graph-a", 1205, 1206, b, generation: 2));
        Assert.Equal(KernelError.InvalidManifest, refused.Error);
        ComponentLifecycleSnapshot retained = s.Kernel.QueryComponent(original.Identity).Value!;
        Assert.Equal(old.Process, retained.Process);
        Assert.Equal(ComponentLifecycleState.Reclaimable, retained.State);
        Assert.True(s.Kernel.QueryBudget(component.ProcessBudget).IsSuccess);
        Assert.True(s.Kernel.Budgets.HasProcessAccount(old.Process));
        Assert.False(s.Kernel.Processes.Resolve(new(old.Process.ProcessId, 2)).IsSuccess);
        Assert.Equal(old, s.Supervisor.Query(original.Identity).Value!.Instance);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void ReplacementRemovedContractKeepsActualDependentDegradedWithoutStaleBinding(bool optional, bool secondary)
    {
        SupervisorScenario s = CreateSupervisor();
        ServiceContractIdentity oldContract = Contract("RemovedDependency");
        ServiceContractIdentity freshContract = Contract("FreshUnrelated");
        ManagedServiceDefinition provider = Definition("removed-provider", 1211, 1212, oldContract);
        if (secondary)
        {
            byte[] image = new byte[] { 187, 1 };
            ProvidedServiceManifestV1[] services = new[] { new ProvidedServiceManifestV1("removed-provider", freshContract),
                new ProvidedServiceManifestV1("removed-secondary", oldContract) };
            ServiceManifestV1 manifest = new ServiceManifestV1(new("removed-provider"), new("1.1"), Digest(image),
                TestFixtures.Manifest(1211, 1212, 1, "removed-entry"), services);
            provider = new(new ComponentAdmissionPlan(manifest, image,
                providedServices: services.Select(item => new ComponentProvidedServiceRegistration(item, Protocol(item.Contract)))),
                "removed-provider");
        }
        ManagedServiceDefinition dependent = Definition("removed-dependent", 1213, 1214, Contract("RemovedConsumer"),
            dependencies: [new(oldContract, optional ? ServiceDependencyKind.Optional : ServiceDependencyKind.Hard)]);
        Assert.True(s.Supervisor.Register(provider).IsSuccess);
        Assert.True(s.Supervisor.Register(dependent).IsSuccess);
        ServiceInstanceHandle old = s.Supervisor.Start(provider.Identity).Value!.Snapshot.Instance!.Value;
        ServiceInstanceHandle consumer = s.Supervisor.Start(dependent.Identity).Value!.Snapshot.Instance!.Value;
        Assert.Single(s.Supervisor.Query(dependent.Identity).Value!.Dependencies);
        EndpointSessionHandle oldSession = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);

        KernelResult<ServiceReplacementReceipt> replacement = s.Supervisor.Replace(old,
            Definition("removed-provider", 1211, 1212, freshContract, generation: 2));

        Assert.True(replacement.IsSuccess, replacement.Message);
        ServiceSupervisorSnapshot after = s.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(consumer, after.Instance);
        Assert.Equal(ServiceLifecycleState.Degraded, after.State);
        Assert.Equal(ServiceHealthState.Degraded, after.Health!.State);
        Assert.Empty(after.Dependencies);
        Assert.Empty(s.Kernel.ResolveByContract(oldContract).Value!);
        Assert.Equal(KernelError.SessionClosed, s.Kernel.CloseSession(consumer.Process, oldSession).Error);
        Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(dependent.Identity).Value!.State);
    }

    [Theory]
    [InlineData(ServiceFailurePropagationPolicy.DegradeDependent)]
    [InlineData(ServiceFailurePropagationPolicy.DrainDependent)]
    [InlineData(ServiceFailurePropagationPolicy.StopDependent)]
    public void SecondaryContractFailureUsesActualDependentPolicy(ServiceFailurePropagationPolicy policy)
    {
        SupervisorScenario s = CreateSupervisor();
        ServiceContractIdentity primary = Contract("SecondaryFailurePrimary");
        ServiceContractIdentity secondary = Contract("SecondaryFailureSecondary");
        byte[] image = new byte[] { 191, 1 };
        ProvidedServiceManifestV1[] services = new[] { new ProvidedServiceManifestV1("secondary-failure-provider", primary),
            new ProvidedServiceManifestV1("secondary-failure-service", secondary) };
        ServiceManifestV1 manifest = new ServiceManifestV1(new("secondary-failure-provider"), new("1.1"), Digest(image),
            TestFixtures.Manifest(1215, 1216, 1, "secondary-failure-entry"), services);
        ManagedServiceDefinition provider = new ManagedServiceDefinition(new ComponentAdmissionPlan(manifest, image,
            providedServices: services.Select(item => new ComponentProvidedServiceRegistration(item, Protocol(item.Contract)))),
            "secondary-failure-provider");
        ManagedServiceDefinition baseDependent = Definition("secondary-failure-peer", 1217, 1218, Contract("SecondaryFailureConsumer"),
            dependencies: [new(secondary, ServiceDependencyKind.Hard)]);
        ManagedServiceDefinition dependent = new ManagedServiceDefinition(baseDependent.Admission, baseDependent.PrimaryServiceName,
            dependencyFailurePolicy: policy);
        Assert.True(s.Supervisor.Register(provider).IsSuccess);
        Assert.True(s.Supervisor.Register(dependent).IsSuccess);
        Assert.True(s.Supervisor.StartAll().IsSuccess);
        ServiceInstanceHandle failed = s.Supervisor.Query(provider.Identity).Value!.Instance!.Value;
        ServiceInstanceHandle peer = s.Supervisor.Query(dependent.Identity).Value!.Instance!.Value;
        ServiceDependencyBinding binding = Assert.Single(s.Supervisor.Query(dependent.Identity).Value!.Dependencies);
        Assert.NotEqual(failed.ServiceId, binding.Provider.ServiceId);
        Assert.Equal(failed.Process, binding.Provider.Process);

        Assert.True(s.Supervisor.ReportHealth(failed, ServiceHealthState.Failed, "Secondary provider failed").IsSuccess);

        ServiceSupervisorSnapshot after = s.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(peer, after.Instance);
        if (policy == ServiceFailurePropagationPolicy.DegradeDependent)
        {
            Assert.Equal(ServiceLifecycleState.Degraded, after.State);
            Assert.Equal(ServiceHealthState.Degraded, after.Health!.State);
            Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(peer.Process).Value!.State);
        }
        else
        {
            Assert.Equal(ServiceLifecycleState.Stopped, after.State);
            Assert.Equal(ComponentLifecycleState.Reclaimable, s.Kernel.QueryComponent(dependent.Identity).Value!.State);
            Assert.True(s.Kernel.Processes.ResolveTerminalState(peer.Process).IsSuccess);
        }
    }

    [Fact]
    public void ReplacementDrainClockCannotDispatchCompetingReplacement()
    {
        CallbackHealthClock clock = new CallbackHealthClock();
        SupervisorScenario s = CreateSupervisor(clock);
        ManagedServiceDefinition definition = Definition("replacement-interlock", 1101, 1102, Contract("ReplacementInterlock"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle old = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        ManagedServiceDefinition next = Definition("replacement-interlock", 1101, 1102, Contract("ReplacementInterlock"), generation: 2);
        int calls = 0;
        clock.OnRead = () =>
        {
            calls++;
            Assert.Equal(KernelError.ReplacementBlocked, s.Supervisor.Replace(old, next).Error);
            Assert.Equal(old, s.Supervisor.Query(definition.Identity).Value!.Instance);
        };
        KernelResult<ServiceReplacementReceipt> replaced = s.Supervisor.Replace(old, next);
        Assert.True(replaced.IsSuccess, replaced.Message);
        Assert.Equal(1, calls);
        Assert.Equal(2UL, replaced.Value!.Snapshot.Instance!.Value.Process.Generation);
        Assert.True(s.Supervisor.Replace(replaced.Value.Snapshot.Instance.Value,
            Definition("replacement-interlock", 1101, 1102, Contract("ReplacementInterlock"), generation: 3)).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RestartFactoryReentryCannotDuplicateDispatchAndThrowAllowsRetry(bool throws)
    {
        SupervisorTimeProvider clock = new SupervisorTimeProvider();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("DispatchInterlock");
        ServiceRestartPolicyV1 policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        int calls = 0;
        bool refuse = throws;
        ManagedServiceDefinition definition = Definition("dispatch-interlock", 1099, 1100, contract, restart: policy,
            replacementFactory: generation =>
            {
                calls++;
                Assert.Equal(KernelError.ReplacementBlocked, s.Supervisor.Observe(new("dispatch-interlock")).Error);
                Assert.Equal(ServiceLifecycleState.RestartBackoff, s.Supervisor.Query(new("dispatch-interlock")).Value!.State);
                ServiceInstanceHandle captured = s.Supervisor.Query(new("dispatch-interlock")).Value!.Instance!.Value;
                ComponentLifecycleSnapshot component = s.Kernel.QueryComponent(new("dispatch-interlock")).Value!;
                Assert.Equal(KernelError.ReplacementBlocked, s.Supervisor.Replace(captured,
                    Definition("dispatch-interlock", 1099, 1100, contract, generation, restart: policy)).Error);
                Assert.Equal(captured, s.Supervisor.Query(new("dispatch-interlock")).Value!.Instance);
                Assert.Equal(component.ProcessBudget, s.Kernel.QueryComponent(new("dispatch-interlock")).Value!.ProcessBudget);
                Assert.True(s.Kernel.QueryBudget(component.ProcessBudget).IsSuccess);
                if (refuse) throw new InvalidOperationException("factory failure");
                return Plan("dispatch-interlock", 1099, 1100, contract, generation, restart: policy);
            });
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle old = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        Assert.True(s.Supervisor.ReportHealth(old, ServiceHealthState.Failed, "restart").IsSuccess);
        clock.AdvanceMilliseconds(1);
        KernelResult<ServiceSupervisorSnapshot> result = s.Supervisor.Observe(definition.Identity);
        if (throws)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            Assert.Equal(old, s.Supervisor.Query(definition.Identity).Value!.Instance);
            refuse = false;
            result = s.Supervisor.Observe(definition.Identity);
        }
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(ServiceLifecycleState.Ready, result.Value!.State);
        Assert.Equal(2UL, result.Value.Instance!.Value.Process.Generation);
        Assert.Equal(throws ? 2 : 1, calls);
        Assert.True(s.Supervisor.Observe(definition.Identity).IsSuccess);
        Assert.Equal(throws ? 2 : 1, calls);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void RestartGenerationCapacityRefusesBeforeFactoryOrUsesLastGeneration(bool exhausted)
    {
        SupervisorTimeProvider clock = new SupervisorTimeProvider();
        SupervisorScenario s = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("LastRestart");
        ServiceRestartPolicyV1 policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        int calls = 0;
        ManagedServiceDefinition definition = Definition("last-restart", 1097, 1098, contract,
            generation: exhausted ? ulong.MaxValue : ulong.MaxValue - 1, restart: policy,
            replacementFactory: generation =>
            {
                calls++;
                Assert.Equal(ulong.MaxValue, generation);
                return Plan("last-restart", 1097, 1098, contract, generation, restart: policy);
            });
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle old = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        Assert.True(s.Supervisor.ReportHealth(old, ServiceHealthState.Failed, "restart").IsSuccess);
        ServiceSupervisorSnapshot before = s.Supervisor.Query(definition.Identity).Value!;
        ComponentLifecycleSnapshot oldComponent = s.Kernel.QueryComponent(definition.Identity).Value!;
        clock.AdvanceMilliseconds(1);
        KernelResult<ServiceSupervisorSnapshot> observed = s.Supervisor.Observe(definition.Identity);
        if (exhausted)
        {
            Assert.Equal(KernelError.StaleGeneration, observed.Error);
            Assert.Equal(KernelError.StaleGeneration, s.Supervisor.Observe(definition.Identity).Error);
            Assert.Equal(0, calls);
            ServiceSupervisorSnapshot after = s.Supervisor.Query(definition.Identity).Value!;
            Assert.Equal(before.Instance, after.Instance);
            Assert.Equal(before.State, after.State);
            Assert.Equal(before.RestartAttemptsInWindow, after.RestartAttemptsInWindow);
            Assert.Equal(before.RestartEligibleTimestamp, after.RestartEligibleTimestamp);
            Assert.Equal(ComponentLifecycleState.Reclaimable, s.Kernel.QueryComponent(definition.Identity).Value!.State);
            Assert.True(s.Kernel.QueryBudget(oldComponent.ProcessBudget).IsSuccess);
        }
        else
        {
            Assert.True(observed.IsSuccess, observed.Message);
            Assert.Equal(ServiceLifecycleState.Ready, observed.Value!.State);
            Assert.Equal(ulong.MaxValue, observed.Value.Instance!.Value.Process.Generation);
            Assert.True(s.Supervisor.Observe(definition.Identity).IsSuccess);
            Assert.Equal(1, calls);
            Assert.Equal(KernelError.StaleGeneration, s.Kernel.QueryBudget(oldComponent.ProcessBudget).Error);
        }
    }

    [Fact]
    public void ReplacementGenerationCapacityRefusesBeforeDrainOrRetirement()
    {
        SupervisorScenario s = CreateSupervisor();
        ManagedServiceDefinition current = Definition("last-generation", 1095, 1096, Contract("LastGeneration"), generation: ulong.MaxValue);
        Assert.True(s.Supervisor.Register(current).IsSuccess);
        ServiceSupervisorSnapshot before = s.Supervisor.Start(current.Identity).Value!.Snapshot;
        ComponentLifecycleSnapshot component = s.Kernel.QueryComponent(current.Identity).Value!;
        BudgetAccountSnapshot budget = s.Kernel.QueryBudget(component.ProcessBudget).Value!;
        ManagedServiceDefinition next = Definition("last-generation", 1095, 1096, Contract("LastGeneration"), generation: 1);

        Assert.Equal(KernelError.StaleGeneration, s.Supervisor.Replace(before.Instance!.Value, next).Error);
        ServiceSupervisorSnapshot after = s.Supervisor.Query(current.Identity).Value!;
        Assert.Equal(before.Instance, after.Instance);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Health, after.Health);
        ComponentLifecycleSnapshot afterComponent = s.Kernel.QueryComponent(current.Identity).Value!;
        Assert.Equal(component.Process, afterComponent.Process);
        Assert.Equal(ComponentLifecycleState.Running, afterComponent.State);
        Assert.Equal(component.ProcessBudget, afterComponent.ProcessBudget);
        Assert.Equal(budget.Usage.ToArray(), s.Kernel.QueryBudget(component.ProcessBudget).Value!.Usage.ToArray());
        Assert.True(s.Kernel.Budgets.HasProcessAccount(component.Process));
    }

    [Theory]
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    public void ReplacementPropagatesControllerToActualAllocationCreationAndStart(int stage, bool terminate)
    {
        SupervisorScenario s = CreateSupervisor();
        ServiceContractIdentity contract = Contract("ReplacementController");
        ManagedServiceDefinition current = Definition("replacement-controller", 1195, 1196, contract);
        Assert.True(s.Supervisor.Register(current).IsSuccess);
        ServiceInstanceHandle old = s.Supervisor.Start(current.Identity).Value!.Snapshot.Instance!.Value;
        ComponentLifecycleSnapshot oldComponent = s.Kernel.QueryComponent(current.Identity).Value!;
        int runningPublications = 0;
        s.Kernel.BeforeComponentRunningPublicationQualificationHook = () => runningPublications++;
        Action lose = () =>
        {
            Assert.False(s.Kernel.Budgets.HasProcessAccount(old.Process));
            if (terminate) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            else Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
        };
        if (stage == 0) s.Kernel.BeforeComponentAdmissionCommitQualificationHook = lose;
        if (stage == 1) s.Kernel.BeforeComponentProcessCreationQualificationHook = lose;
        if (stage == 2) s.Kernel.BeforeComponentExecutionStartQualificationHook = lose;
        ManagedServiceDefinition next = Definition("replacement-controller", 1195, 1196, contract, generation: 2);

        Assert.Equal(KernelError.SupervisorDenied, s.Supervisor.Replace(old, next).Error);

        ProcessHandle fresh = new ProcessHandle(old.Process.ProcessId, 2);
        Assert.False(s.Kernel.Processes.Resolve(fresh).IsSuccess);
        Assert.Equal(0, runningPublications);
        Assert.Empty(s.Kernel.ResolveByContract(contract).Value!);
        Assert.Equal(KernelError.StaleGeneration, s.Kernel.QueryBudget(oldComponent.ProcessBudget).Error);
        if (stage < 2)
        {
            Assert.False(s.Kernel.Processes.ResolveTerminalState(fresh).IsSuccess);
            Assert.Equal(KernelError.ComponentNotFound, s.Kernel.QueryComponent(current.Identity).Error);
            Assert.False(s.Kernel.Budgets.HasProcessAccount(fresh));
        }
        else
        {
            ComponentLifecycleSnapshot retained = s.Kernel.QueryComponent(current.Identity).Value!;
            Assert.Equal(fresh, retained.Process);
            Assert.Equal(ComponentLifecycleState.Reclaimable, retained.State);
            Assert.True(s.Kernel.Processes.ResolveTerminalState(fresh).IsSuccess);
            Assert.True(s.Kernel.RetireReclaimableComponent(current.Identity, fresh).IsSuccess);
            Assert.False(s.Kernel.Budgets.HasProcessAccount(fresh));
        }
    }

    [Fact]
    public void ReplacementAdmissionFailureCannotOverwriteReentrantDrainFact()
    {
        SupervisorScenario s = CreateSupervisor();
        ServiceContractIdentity contract = Contract("FailureDrainSource");
        ManagedServiceDefinition current = Definition("failure-drain-source", 1197, 1198, contract);
        Assert.True(s.Supervisor.Register(current).IsSuccess);
        ServiceInstanceHandle old = s.Supervisor.Start(current.Identity).Value!.Snapshot.Instance!.Value;
        ServiceSupervisorSnapshot? drained = null;
        s.Kernel.BeforeComponentAdmissionCommitQualificationHook = () =>
        {
            // The old component has retired. This actual drain records its
            // newer state even though its exact component is no longer present.
            Assert.False(s.Supervisor.Drain(old).IsSuccess);
            drained = s.Supervisor.Query(current.Identity).Value!;
            Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
        };
        KernelResult<ServiceReplacementReceipt> refused = s.Supervisor.Replace(old,
            Definition("failure-drain-source", 1197, 1198, contract, generation: 2));
        Assert.Equal(KernelError.SupervisorDenied, refused.Error);
        Assert.NotNull(drained);
        ServiceSupervisorSnapshot after = InspectSupervisorRecord(s.Supervisor, current.Identity);
        Assert.Equal(drained!.State, after.State);
        Assert.Equal(drained.Health, after.Health);
        Assert.Equal(drained.BlockingReason, after.BlockingReason);
        Assert.Equal(KernelError.ComponentNotFound, s.Kernel.QueryComponent(current.Identity).Error);
        Assert.False(s.Kernel.Processes.Resolve(new(old.Process.ProcessId, 2)).IsSuccess);
    }

    [Fact]
    public void ReplacementControlRevokeBeforeRetirementPreservesExactOldAccounting()
    {
        SupervisorScenario s = CreateSupervisor();
        ManagedServiceDefinition current = Definition("retirement-control", 1193, 1194, Contract("RetirementControl"));
        Assert.True(s.Supervisor.Register(current).IsSuccess);
        ServiceInstanceHandle old = s.Supervisor.Start(current.Identity).Value!.Snapshot.Instance!.Value;
        ComponentLifecycleSnapshot component = s.Kernel.QueryComponent(current.Identity).Value!;
        int calls = 0;
        s.Supervisor.BeforeReplacementRetirementQualificationHook = () =>
        {
            calls++;
            Task<KernelResult> revoke = Task.Run(() => s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability));
            Assert.True(revoke.Wait(TimeSpan.FromSeconds(5)), "Retirement hook must run outside capability locks.");
            Assert.True(revoke.Result.IsSuccess);
        };
        ManagedServiceDefinition next = Definition("retirement-control", 1193, 1194, Contract("RetirementControl"), generation: 2);

        KernelResult<ServiceReplacementReceipt> refused = s.Supervisor.Replace(old, next);

        Assert.Equal(KernelError.SupervisorDenied, refused.Error);
        Assert.Equal(1, calls);
        ComponentLifecycleSnapshot retained = s.Kernel.QueryComponent(current.Identity).Value!;
        Assert.Equal(old.Process, retained.Process);
        Assert.Equal(ComponentLifecycleState.Reclaimable, retained.State);
        Assert.Equal(component.ProcessBudget, retained.ProcessBudget);
        Assert.True(s.Kernel.Budgets.HasProcessAccount(old.Process));
        Assert.True(s.Kernel.QueryBudget(component.ProcessBudget).IsSuccess);
        Assert.True(s.Kernel.QueryBudget(component.ServiceBudget).IsSuccess);
        Assert.Equal(KernelError.ProcessNotFound, s.Kernel.Processes.Resolve(new(old.Process.ProcessId, 2)).Error);
    }

    [Theory]
    [InlineData(0UL)] [InlineData(10UL)]
    public void ActualReplacementPreservesOldTerminalChargeAndSeparatesFreshAccounting(ulong charge)
    {
        SupervisorScenario s = CreateSupervisor();
        ManagedServiceDefinition current = Definition("charged-replacement", 1093, 1094, Contract("ChargedReplacement"));
        Assert.True(s.Supervisor.Register(current).IsSuccess);
        ServiceInstanceHandle old = s.Supervisor.Start(current.Identity).Value!.Snapshot.Instance!.Value;
        ComponentLifecycleSnapshot oldComponent = s.Kernel.QueryComponent(current.Identity).Value!;
        BudgetReservationHandle lease = s.Kernel.Budgets.Reserve(old.Process,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 40)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(s.Kernel.Budgets.BindLease(old.Process, lease).IsSuccess);
        Assert.True(s.Kernel.Budgets.BeginConsumption(old.Process, lease).IsSuccess);
        BudgetReservationSnapshot terminal = s.Kernel.Budgets.SettleLease(old.Process, lease,
            charge == 0 ? [] : [new(ServiceBudgetDimension.ComputeTimeNanoseconds, charge)]).Value!;
        ManagedServiceDefinition next = Definition("charged-replacement", 1093, 1094, Contract("ChargedReplacement"), generation: 2);

        KernelResult<ServiceReplacementReceipt> replaced = s.Supervisor.Replace(old, next);

        Assert.True(replaced.IsSuccess, replaced.Message);
        Assert.Equal(ServiceLifecycleState.Ready, replaced.Value!.Snapshot.State);
        ComponentLifecycleSnapshot fresh = s.Kernel.QueryComponent(current.Identity).Value!;
        Assert.Equal(2UL, fresh.Process.Generation);
        Assert.False(s.Kernel.Budgets.HasProcessAccount(old.Process));
        Assert.Equal(KernelError.StaleGeneration, s.Kernel.QueryBudget(oldComponent.ProcessBudget).Error);
        Assert.Equal(KernelError.StaleGeneration, s.Kernel.QueryBudget(oldComponent.ServiceBudget).Error);
        Assert.NotEqual(oldComponent.ProcessBudget, fresh.ProcessBudget);
        BudgetSnapshotAssertions.Equal(terminal, s.Kernel.Budgets.Query(lease).Value!);
        Assert.Equal(charge, Assert.Single(s.Kernel.QueryBudget(s.Kernel.Budgets.SystemBudget).Value!.Usage,
            item => item.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used);
        Assert.Equal(0UL, Assert.Single(s.Kernel.QueryBudget(fresh.ProcessBudget).Value!.Usage,
            item => item.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used);
        Assert.Equal(KernelError.StaleGeneration, s.Supervisor.Replace(old, next).Error);
        Assert.Equal(fresh.Process, s.Kernel.QueryComponent(current.Identity).Value!.Process);
        BudgetSnapshotAssertions.Equal(terminal, s.Kernel.Budgets.Query(lease).Value!);
    }

    [Fact]
    public void PlannedReplacementUsesFreshProcessCapabilityAndStableServiceLineage()
    {
        SupervisorScenario scenario = CreateSupervisor();
        CapabilityRequirementV1 capability = new CapabilityRequirementV1(ResourceKind.KernelService, "managed-data", CapabilityRights.Read);
        ManagedServiceDefinition current = Definition("planned", 206, 2006, Contract("Planned"), capability: capability, issuer: scenario.IssuerDomain);
        Assert.True(scenario.Supervisor.Register(current).IsSuccess);
        ServiceSupervisorSnapshot old = scenario.Supervisor.Start(current.Identity).Value!.Snapshot;
        ComponentLifecycleSnapshot oldComponent = scenario.Kernel.QueryComponent(current.Identity).Value!;
        CapabilityId oldCapability = Assert.Single(oldComponent.Capabilities);
        ManagedServiceDefinition next = Definition("planned", 206, 2006, Contract("Planned"), generation: 2, capability: capability, issuer: scenario.IssuerDomain);

        KernelResult<ServiceReplacementReceipt> replaced = scenario.Supervisor.Replace(old.Instance!.Value, next);

        Assert.True(replaced.IsSuccess, replaced.Message);
        Assert.True(replaced.Value!.FreshAdmission);
        ServiceInstanceHandle fresh = replaced.Value.Snapshot.Instance!.Value;
        Assert.Equal(old.Instance.Value.ServiceId, fresh.ServiceId);
        Assert.Equal(old.Instance.Value.Generation.Value + 1, fresh.Generation.Value);
        Assert.Equal(old.Instance.Value.Process.Generation + 1, fresh.Process.Generation);
        Assert.NotEqual(oldComponent.ManifestDigest, replaced.Value.Snapshot.ManifestDigest);
        Assert.False(scenario.Kernel.ValidateCapability(fresh.Process, oldCapability, CapabilityRights.Read).IsSuccess);
        ComponentLifecycleSnapshot freshComponent = scenario.Kernel.QueryComponent(current.Identity).Value!;
        Assert.NotEqual(oldCapability, Assert.Single(freshComponent.Capabilities));
        Assert.NotEqual(oldComponent.ProcessBudget, freshComponent.ProcessBudget);
        Assert.NotEqual(oldComponent.ServiceBudget, freshComponent.ServiceBudget);
    }

    [Fact]
    public void DependencyReplacementRebindsDependentToFreshExactGeneration()
    {
        SupervisorTimeProvider clock = new SupervisorTimeProvider();
        SupervisorScenario scenario = CreateSupervisor(clock);
        ServiceContractIdentity providerContract = Contract("ReplaceableProvider");
        ManagedServiceDefinition provider = Definition("replaceable-provider", 207, 2007, providerContract,
            restart: new(ServiceRestartMode.OnFailure, 3, 10, 100, 1000),
            replacementFactory: generation => Plan("replaceable-provider", 207, 2007, providerContract, generation,
                restart: new(ServiceRestartMode.OnFailure, 3, 10, 100, 1000)));
        ManagedServiceDefinition dependent = Definition("rebound-dependent", 208, 2008, Contract("ReboundDependent"),
            dependencies: [new(providerContract, ServiceDependencyKind.Hard)]);
        Assert.True(scenario.Supervisor.Register(dependent).IsSuccess);
        Assert.True(scenario.Supervisor.Register(provider).IsSuccess);
        Assert.True(scenario.Supervisor.StartAll().IsSuccess);
        ServiceDependencyBinding oldBinding = Assert.Single(scenario.Supervisor.Query(dependent.Identity).Value!.Dependencies);

        Assert.True(scenario.Supervisor.ReportHealth(oldBinding.Provider, ServiceHealthState.Failed, "crash").IsSuccess);
        Assert.Equal(ServiceLifecycleState.Degraded, scenario.Supervisor.Query(dependent.Identity).Value!.State);
        clock.AdvanceMilliseconds(10);
        Assert.True(scenario.Supervisor.Observe(provider.Identity).IsSuccess);

        ServiceSupervisorSnapshot rebound = scenario.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, rebound.State);
        ServiceDependencyBinding newBinding = Assert.Single(rebound.Dependencies);
        Assert.NotEqual(oldBinding.Provider, newBinding.Provider);
        Assert.Equal(oldBinding.Provider.ServiceId, newBinding.Provider.ServiceId);
        Assert.Equal(oldBinding.Provider.Generation.Value + 1, newBinding.Provider.Generation.Value);
        Assert.Equal(oldBinding.Provider.Process.Generation + 1, newBinding.Provider.Process.Generation);
    }

    [Fact]
    public void BoundedRestartPolicyEntersCrashLoop()
    {
        SupervisorTimeProvider clock = new SupervisorTimeProvider();
        SupervisorScenario scenario = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("CrashLoop");
        ServiceRestartPolicyV1 restart = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 1, 1, 1, 10_000);
        ManagedServiceDefinition definition = Definition("crash-loop", 209, 2009, contract, restart: restart,
            replacementFactory: generation => Plan("crash-loop", 209, 2009, contract, generation, restart: restart));
        Assert.True(scenario.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle first = scenario.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        Assert.True(scenario.Supervisor.ReportHealth(first, ServiceHealthState.Failed, "first").IsSuccess);
        clock.AdvanceMilliseconds(1);
        ServiceInstanceHandle replacement = scenario.Supervisor.Observe(definition.Identity).Value!.Instance!.Value;

        Assert.True(scenario.Supervisor.ReportHealth(replacement, ServiceHealthState.Failed, "second").IsSuccess);

        ServiceSupervisorSnapshot terminal = scenario.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.CrashLoop, terminal.State);
        Assert.Contains("Crash-loop", terminal.BlockingReason, StringComparison.Ordinal);
    }

    [Fact]
    public void SubmittedClosableOperationDrainsBeforeReplacement()
    {
        SupervisorTimeProvider clock = new SupervisorTimeProvider();
        SupervisorScenario scenario = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("Closable");
        ServiceRestartPolicyV1 restart = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        ManagedServiceDefinition definition = Definition("closable", 210, 2010, contract, restart: restart,
            replacementFactory: generation => Plan("closable", 210, 2010, contract, generation, restart: restart));
        Assert.True(scenario.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = scenario.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        SubmittedOperation operation = SubmitOperation(scenario.Kernel, instance.Process);

        Assert.True(scenario.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "crash with submitted work").IsSuccess);
        Assert.Equal(ServiceLifecycleState.Draining, scenario.Supervisor.Query(definition.Identity).Value!.State);
        Assert.True(scenario.Kernel.RecordExternalOperationCompletion(instance.Process, new(operation.Binding, ExternalOperationCompletionDisposition.Cancelled)).IsSuccess);
        KernelResult<ServiceSupervisorSnapshot> closurePending = scenario.Supervisor.Observe(definition.Identity);
        Assert.True(closurePending.IsSuccess, closurePending.Message);
        Assert.Equal(ServiceLifecycleState.Draining, closurePending.Value!.State);
        Assert.True(scenario.Kernel.ReleaseExternalOperation(instance.Process, operation.Operation, new(true, false)).IsSuccess);
        KernelResult<ServiceSupervisorSnapshot> observed = scenario.Supervisor.Observe(definition.Identity);
        Assert.True(observed.IsSuccess, observed.Message);
        Assert.Equal(ServiceLifecycleState.RestartBackoff, observed.Value!.State);
        clock.AdvanceMilliseconds(1);
        KernelResult<ServiceSupervisorSnapshot> restarted = scenario.Supervisor.Observe(definition.Identity);
        Assert.True(restarted.IsSuccess, restarted.Message);
        Assert.Equal(ServiceLifecycleState.Ready, restarted.Value!.State);
        Assert.Equal(instance.Process.Generation + 1, restarted.Value.Instance!.Value.Process.Generation);
    }

    [Fact]
    public void AmbiguousExternalEffectQuarantinesAndBlocksReplacement()
    {
        SupervisorScenario scenario = CreateSupervisor();
        ServiceContractIdentity contract = Contract("Ambiguous");
        ManagedServiceDefinition definition = Definition("ambiguous", 211, 2011, contract,
            restart: new(ServiceRestartMode.OnFailure, 2, 1, 10, 1000),
            replacementFactory: generation => Plan("ambiguous", 211, 2011, contract, generation,
                restart: new(ServiceRestartMode.OnFailure, 2, 1, 10, 1000)));
        Assert.True(scenario.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = scenario.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        SubmittedOperation operation = SubmitOperation(scenario.Kernel, instance.Process);
        Assert.True(scenario.Kernel.RecordExternalOperationProviderLoss(instance.Process, operation.Operation).IsSuccess);

        KernelResult<ServiceHealthSnapshot> failure = scenario.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "provider response lost");

        Assert.True(failure.IsSuccess, failure.Message);
        ServiceSupervisorSnapshot quarantined = scenario.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Quarantined, quarantined.State);
        Assert.Equal(ServiceHealthState.Quarantined, quarantined.Health!.State);
        ManagedServiceDefinition next = new ManagedServiceDefinition(Plan("ambiguous", 211, 2011, contract, 2), "ambiguous");
        KernelResult<ServiceReplacementReceipt> replacement = scenario.Supervisor.Replace(instance, next);
        Assert.False(replacement.IsSuccess);
        Assert.Equal(KernelError.ReplacementBlocked, replacement.Error);
        Assert.Equal(instance, scenario.Supervisor.Query(definition.Identity).Value!.Instance);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            KernelResult<ServiceSupervisorSnapshot> observed = scenario.Supervisor.Observe(definition.Identity);
            Assert.True(observed.IsSuccess);
            Assert.Equal(ServiceLifecycleState.Quarantined, observed.Value!.State);
            Assert.Equal(instance, observed.Value.Instance);
            Assert.Equal(0u, observed.Value.RestartAttemptsInWindow);
            Assert.False(scenario.Kernel.QueryComponent(definition.Identity).Value!.Reclaimable);
            Assert.True(scenario.Kernel.Processes.Resolve(instance.Process).IsSuccess);
            Assert.Equal(ExternalOperationDisposition.ProviderLost,
                scenario.Kernel.QueryExternalOperation(instance.Process, operation.Operation).Value!.Disposition);
            Assert.False(scenario.Kernel.ReleaseRegion(instance.Process, operation.Output).IsSuccess);
        }
    }

    [Fact]
    public void DrainDeadlineWithAmbiguousExternalEffectQuarantinesWithoutReclaim()
    {
        SupervisorTimeProvider clock = new SupervisorTimeProvider();
        SupervisorScenario scenario = CreateSupervisor(clock);
        ServiceContractIdentity contract = Contract("DeadlineAmbiguous");
        ManagedServiceDefinition definition = Definition("deadline-ambiguous", 215, 2015, contract);
        Assert.True(scenario.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = scenario.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        SubmittedOperation operation = SubmitOperation(scenario.Kernel, instance.Process);
        CancellationObservation scope = scenario.Kernel.CreateCancellationScope(instance.Process, new(1)).Value!;
        clock.AdvanceMilliseconds(1);

        KernelResult<ServiceDrainReceipt> drained = scenario.Supervisor.Drain(
            instance, scope.Scope, ServiceDrainTimeoutPolicy.Quarantine);

        Assert.True(drained.IsSuccess, drained.Message);
        Assert.False(drained.Value!.ClosureProven);
        Assert.Equal(ServiceLifecycleState.Quarantined, drained.Value.Snapshot.State);
        Assert.Equal(ExternalOperationState.Submitted,
            scenario.Kernel.QueryExternalOperation(instance.Process, operation.Operation).Value!.State);
        Assert.Contains(scenario.Kernel.Regions.SnapshotUses(), use => use.State == RegionUseState.Active);
    }

    [Fact]
    public void StaleHandleAndRevokedSupervisorCapabilityFailClosed()
    {
        SupervisorScenario scenario = CreateSupervisor();
        ManagedServiceDefinition definition = Definition("stale", 212, 2012, Contract("Stale"));
        Assert.True(scenario.Supervisor.Register(definition).IsSuccess);
        ServiceInstanceHandle instance = scenario.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        ServiceInstanceHandle stale = instance with { Generation = new(instance.Generation.Value + 1) };

        Assert.Equal(KernelError.StaleGeneration, scenario.Supervisor.ReportHealth(stale, ServiceHealthState.Ready, "forged").Error);
        Assert.True(scenario.Kernel.RevokeCapability(scenario.ControlCapability).IsSuccess);
        Assert.Equal(KernelError.SupervisorDenied, scenario.Supervisor.Drain(instance).Error);
        Assert.Equal(ComponentLifecycleState.Running, scenario.Kernel.QueryComponent(definition.Identity).Value!.State);
    }

    [Fact]
    public void OrdinaryKernelServiceCapabilityCannotCreateSupervisor()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle principal) = TestFixtures.Create(kernel, 901, 9001);
        Assert.True(kernel.AdmitProcess(principal).IsSuccess);
        CapabilityId ordinary = kernel.MintCapability(new DomainId(9001), principal, ResourceKind.KernelService,
            "ordinary-service", CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;

        KernelResult<CapabilityAwareServiceSupervisor> denied = kernel.CreateServiceSupervisor(principal, ordinary);

        Assert.Equal(KernelError.SupervisorDenied, denied.Error);
    }

    [Fact]
    public void HealthAndSupervisorDtosContainNoRootOrProviderAuthority()
    {
        Type[] propertyTypes = typeof(ServiceHealthSnapshot).GetProperties().Select(property => property.PropertyType)
            .Concat(typeof(ServiceSupervisorSnapshot).GetProperties().Select(property => property.PropertyType))
            .ToArray();
        Assert.DoesNotContain(typeof(CapabilityId), propertyTypes);
        string[] forbiddenVocabulary =
        [
            "ProviderLease",
            "PlatformDomainBinding",
            "Cxl",
            "Bdf",
            "Hdm",
            "Dpa",
            "FabricManager",
            "HybridCpuLane",
            "Opcode",
            "ReplayCertificate"
        ];
        Assert.DoesNotContain(propertyTypes, type => forbiddenVocabulary.Any(forbidden =>
            (type.FullName ?? type.Name).Contains(forbidden, StringComparison.OrdinalIgnoreCase)));
        Assert.False(new ServiceHealthSnapshot(default, ServiceHealthState.Ready, 1, "observation").AuthorizesMutation);
    }

    private static SupervisorScenario CreateSupervisor(TimeProvider? clock = null, TimeProvider? kernelClock = null)
    {
        RuntimeKernel kernel = new RuntimeKernel(null, kernelClock ?? clock);
        (SingProcess _, ProcessHandle principal) = TestFixtures.Create(kernel, 900, 9000, identity: "supervisor-control");
        Assert.True(kernel.AdmitProcess(principal).IsSuccess);
        CapabilityId capability = kernel.MintCapability(new DomainId(9000), principal, ResourceKind.KernelService,
            CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;
        CapabilityAwareServiceSupervisor supervisor = kernel.CreateServiceSupervisor(principal, capability, clock).Value!;
        return new(kernel, supervisor, principal, new DomainId(9000), capability);
    }

    private static ManagedServiceDefinition Definition(
        string name,
        ulong processId,
        ulong domainId,
        ServiceContractIdentity contract,
        ulong generation = 1,
        IEnumerable<ServiceDependencyRequirementV1>? dependencies = null,
        ServiceRestartPolicyV1? restart = null,
        Func<ulong, ComponentAdmissionPlan>? replacementFactory = null,
        CapabilityRequirementV1? capability = null,
        DomainId? issuer = null) =>
        new(Plan(name, processId, domainId, contract, generation, dependencies, restart, capability, issuer), name,
            ServiceReadinessPolicy.HardDependenciesBound, ServiceFailurePropagationPolicy.DegradeDependent, replacementFactory);

    private static ComponentAdmissionPlan Plan(
        string name,
        ulong processId,
        ulong domainId,
        ServiceContractIdentity contract,
        ulong generation = 1,
        IEnumerable<ServiceDependencyRequirementV1>? dependencies = null,
        ServiceRestartPolicyV1? restart = null,
        CapabilityRequirementV1? capability = null,
        DomainId? issuer = null,
        ComponentIdentity? componentIdentity = null,
        ServiceCompatibilityConstraintsV1? compatibility = null)
    {
        byte[] image = new byte[] { (byte)(processId & 0xff), (byte)generation };
        ProvidedServiceManifestV1 provided = new ProvidedServiceManifestV1(name, contract);
        CapabilityRequirementV1[] capabilities = capability is { } requirement ? new[] { requirement } : [];
        SingProcessManifestV1 process = TestFixtures.Manifest(processId, domainId, generation, $"{name}-entry", capabilities);
        ServiceManifestV1 manifest = new ServiceManifestV1(componentIdentity ?? new(name), new($"1.{generation}"), Digest(image), process, [provided],
            dependencies: dependencies,
            compatibility: compatibility,
            restartPolicy: restart ?? ServiceRestartPolicyV1.Never);
        ComponentCapabilityGrant[] grants = capability is { } grant
            ? new[] { new ComponentCapabilityGrant(issuer ?? throw new ArgumentNullException(nameof(issuer)), grant) }
            : [];
        return new(manifest, image, grants, [new(provided, Protocol(contract))]);
    }

    private static SubmittedOperation SubmitOperation(RuntimeKernel kernel, ProcessHandle process)
    {
        OwnedBuffer<byte> input = kernel.AllocateBuffer<byte>(process, 8).Value!;
        OwnedBuffer<byte> output = kernel.AllocateBuffer<byte>(process, 8).Value!;
        OperationPreparation prepared = kernel.PrepareExternalOperation(process,
        [
            new(input.Handle, RegionUseMode.ReadOnly, new(0, 8)),
            new(output.Handle, RegionUseMode.StagedOutput, new(0, 8)),
        ], ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
        OperationDependencySnapshot dependencies = new OperationDependencySnapshot(7, 11);
        Assert.True(kernel.AdmitExternalOperation(process, prepared.Operation, dependencies).IsSuccess);
        OperationBinding binding = kernel.RecordExternalOperationSubmission(process, prepared.Operation, dependencies).Value!;
        return new(prepared.Operation, binding, input, output);
    }

    private static ServiceContractIdentity Contract(string name) => new(name, "1", $"{name.ToLowerInvariant()}-digest");
    private static ProtocolDefinitionV1 Protocol(ServiceContractIdentity contract) =>
        new(contract.Name, contract.Digest, "Idle", ["Done"], [new(1, "Invoke")], [new(1, "Idle", "Done")]);
    private static string Digest(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed record SupervisorScenario(RuntimeKernel Kernel, CapabilityAwareServiceSupervisor Supervisor, ProcessHandle Principal, DomainId IssuerDomain, CapabilityId ControlCapability);
    private sealed record SubmittedOperation(ExternalOperationHandle Operation, OperationBinding Binding, OwnedBuffer<byte> Input, OwnedBuffer<byte> Output);

    private sealed class SupervisorTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void AdvanceMilliseconds(long milliseconds) => _timestamp += milliseconds;
    }
}
