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
        var (s, _, dependent, provider, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var session = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        var refuse = true;
        var calls = 0;
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
        var pending = s.Kernel.QueryProcessTeardown(peer.Process);
        Assert.True(pending.IsSuccess);
        Assert.True(pending.Value.LocalAuthorizationRevoked);
        Assert.False(pending.Value.LocalReclaimCompleted);
        Assert.Equal(2, calls);
        refuse = false;
        var retried = s.Kernel.TerminateProcess(peer.Process);
        Assert.True(retried.IsSuccess, retried.Message);
        Assert.Equal(3, calls);
        // The real successful teardown result proves managed completion;
        // retirement removes its record, and absence is not closure evidence.
        Assert.False(s.Kernel.Processes.Resolve(peer.Process).IsSuccess);
    }

    [Fact]
    public async Task NativeProcessDrainStopsAdmissionBeforeCallbackAndLeavesLifecycleGateFree()
    {
        var (s, _, dependent, provider, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var session = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        var descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        var extra = s.Kernel.OpenSession(peer.Process, descriptor);
        Assert.True(extra.IsSuccess);
        var permission = s.Kernel.MintCapability(s.IssuerDomain, provider.Process, ResourceKind.File, "native-drain-source", CapabilityRights.Read);
        Assert.True(permission.IsSuccess);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Kernel.RegisterNativeServiceDrain(provider.Process, session, () =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Closure unknown.");
        });
        var teardown = Task.Run(() => s.Kernel.TerminateProcess(provider.Process));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var pending = await Task.Run(() => s.Kernel.QueryProcessTeardown(provider.Process)).WaitAsync(TimeSpan.FromSeconds(10));
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
        var (s, _, dependent, provider, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var session = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        var calls = 0;
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
        var (s, _, dependent, provider, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var first = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        var descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        var opened = s.Kernel.OpenSession(peer.Process, descriptor);
        Assert.True(opened.IsSuccess, opened.Message);
        var refuse = true;
        var refusedCalls = 0;
        var completedCalls = 0;
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
        var pending = s.Kernel.QueryProcessTeardown(provider.Process);
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
        var (s, _, dependent, provider, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var session = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        var refuse = true;
        var calls = 0;
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
        var (s, _, dependent, provider, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var session = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        s.Kernel.RegisterNativeServiceDrain(provider.Process, session, () =>
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return KernelResult.Ok();
        });
        var first = Task.Run(() => processFirst
            ? s.Kernel.TerminateProcess(provider.Process)
            : s.Kernel.CloseSession(peer.Process, session));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var refused = await Task.Run(() => processFirst
                ? s.Kernel.CloseSession(peer.Process, session)
                : s.Kernel.TerminateProcess(provider.Process)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(KernelError.SessionDraining, refused.Error);
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.False(first.IsCompleted);
        }
        finally { release.Set(); }
        var settled = await first.WaitAsync(TimeSpan.FromSeconds(10));
        if (processFirst) Assert.True(settled.IsSuccess);
        else Assert.Equal(KernelError.StaleGeneration, settled.Error);
        if (!processFirst) Assert.True(s.Kernel.TerminateProcess(provider.Process).IsSuccess);
        Assert.Equal(1, calls);
    }

    private static (SupervisorScenario Scenario, ManagedServiceDefinition Dependent, ServiceInstanceHandle Peer,
        ServiceEndpointDescriptor Descriptor) CreateUnboundRebindScenario()
    {
        var (s, _, dependent, provider, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        var old = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
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
        var (s, dependent, peer, descriptor) = CreateUnboundRebindScenario();
        var sessionGate = s.Kernel.EndpointSessions.GetType().GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(s.Kernel.EndpointSessions)!;
        var allocator = s.Kernel.Channels.GetType().GetField("_nextChannelId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var beforeAllocation = (ulong)allocator.GetValue(s.Kernel.Channels)!;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var locker = Task.Run(() =>
        {
            lock (sessionGate) { entered.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); }
        });
        Task<KernelResult<EndpointSessionHandle>>? admission = null;
        Task<KernelResult<ComponentLifecycleSnapshot>>? teardown = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            admission = Task.Run(() => s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process,
                descriptor, s.Principal, s.ControlCapability));
            // Observation coordinates the test only; it grants no permission or closure.
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(
                () => (ulong)allocator.GetValue(s.Kernel.Channels)! > beforeAllocation, TimeSpan.FromSeconds(10))));
            teardown = Task.Run(() => s.Kernel.DrainComponent(dependent.Identity, peer.Process));
            Assert.True(await Task.Run(() => SpinWait.SpinUntil(
                () => s.Kernel.QueryComponent(dependent.Identity).Value!.State == ComponentLifecycleState.Draining,
                TimeSpan.FromSeconds(10))));
            Assert.False(teardown.IsCompleted);
        }
        finally { release.Set(); }
        await locker.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(KernelError.InvalidTransition, (await admission!.WaitAsync(TimeSpan.FromSeconds(10))).Error);
        var closed = await teardown!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(closed.IsSuccess, closed.Message);
        Assert.True(closed.Value!.Reclaimable);
        Assert.Empty(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        Assert.Equal(0, s.Kernel.Channels.InspectionSummary(peer.Process).Channels);
        var ownerRecords = s.Kernel.EndpointSessions.SnapshotForProcess(peer.Process);
        Assert.Equal(2, ownerRecords.Length);
        Assert.All(ownerRecords, record => Assert.Equal(EndpointSessionState.Closed, record.State));
    }

    [Fact]
    public async Task OverlappingLocalRebindsKeepOneTrackedActiveDependency()
    {
        var (s, dependent, peer, descriptor) = CreateUnboundRebindScenario();
        for (var iteration = 0; iteration < 4; iteration++)
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                return s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor, s.Principal, s.ControlCapability);
            })).ToArray();
            start.SetResult();
            var results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains(results, result => result.IsSuccess);
            Assert.All(results, result => Assert.True(result.IsSuccess || result.Error is KernelError.StaleGeneration or KernelError.InvalidTransition,
                result.Message));
            var tracked = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
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
        var (s, _, dependent, exactProvider, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        var before = s.Kernel.QueryComponent(dependent.Identity).Value!;
        var old = Assert.Single(before.Sessions);
        var callbacks = 0;
        s.Kernel.RegisterNativeServiceDrain(exactProvider.Process, old, () => { callbacks++; return KernelResult.Ok(); });
        if (refusal == 0) Assert.True(s.Kernel.SetServiceAvailability(descriptor.Service, ServiceAvailability.Draining).IsSuccess);
        if (refusal == 1) descriptor = descriptor with { Generation = new(descriptor.Generation.Value + 1) };
        if (refusal == 2)
        {
            var (_, alternative) = TestFixtures.Create(s.Kernel, 256, 2056);
            Assert.True(s.Kernel.AdmitProcess(alternative).IsSuccess);
            descriptor = s.Kernel.RegisterService(alternative, "required-provider", Contract("FailureProvider"), Protocol(Contract("FailureProvider")),
                requiredCapabilities: [new(ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure)]).Value!;
        }
        if (refusal == 3) descriptor = descriptor with { Service = descriptor.Service with { Id = new(descriptor.Service.Id.Value + 100) } };
        var refused = s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor, s.Principal, s.ControlCapability);
        Assert.Equal(refusal switch { 0 => KernelError.ServiceUnavailable, 1 => KernelError.StaleGeneration,
            2 => KernelError.MissingCapability, _ => KernelError.ServiceNotFound }, refused.Error);
        Assert.Equal(0, callbacks);
        var after = s.Kernel.QueryComponent(dependent.Identity).Value!;
        Assert.Equal(before.Sessions.ToArray(), after.Sessions.ToArray());
        Assert.Equal(before.ProcessBudget, after.ProcessBudget);
        Assert.Equal(before.ServiceBudget, after.ServiceBudget);
        Assert.True(s.Kernel.EndpointSessions.Resolve(old, peer.Process).IsSuccess);
        Assert.Equal(1, s.Kernel.Channels.InspectionSummary(peer.Process).Channels);
    }

    [Fact]
    public void SourcedRebindRejectsRevokedCapabilityAncestorAfterNativeCallback()
    {
        var (s, _, dependent, exactProvider, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        var root = s.Kernel.MintCapability(s.IssuerDomain, s.Principal, ResourceKind.KernelService,
            CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Configure | CapabilityRights.Execute | CapabilityRights.Delegate).Value!.CapabilityId;
        var child = s.Kernel.DelegateCapability(s.Principal, s.Principal, root,
            CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;
        var old = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
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
        var (s, _, dependent, _, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        var before = s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions.ToArray();
        var source = s.ControlCapability;
        var principal = s.Principal;
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
        var (s, provider, dependent, exactProvider, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        var old = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        var budget = s.Kernel.QueryComponent(dependent.Identity).Value!.ProcessBudget;
        s.Kernel.RegisterNativeServiceDrain(exactProvider.Process, old, () =>
        {
            if (retireGeneration) s.Kernel.Services.RetireForProvider(exactProvider.Process);
            else Assert.True(s.Kernel.SetServiceAvailability(descriptor.Service, ServiceAvailability.Draining).IsSuccess);
            return KernelResult.Ok();
        });
        var refused = s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor, s.Principal, s.ControlCapability);
        Assert.Equal(retireGeneration ? KernelError.StaleGeneration : KernelError.ServiceUnavailable, refused.Error);
        Assert.Empty(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        Assert.Equal(0, s.Kernel.Channels.InspectionSummary(peer.Process).Channels);
        Assert.Equal(budget, s.Kernel.QueryComponent(dependent.Identity).Value!.ProcessBudget);
        if (!retireGeneration)
        {
            Assert.True(s.Kernel.SetServiceAvailability(descriptor.Service, ServiceAvailability.Accepting).IsSuccess);
            var retried = s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor, s.Principal, s.ControlCapability);
            Assert.True(retried.IsSuccess, retried.Message);
            Assert.NotEqual(old, retried.Value);
            Assert.Equal(retried.Value, Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions));
            Assert.Equal(budget, s.Kernel.QueryComponent(dependent.Identity).Value!.ProcessBudget);
        }
    }

    [Fact]
    public async Task BlockedNativeRebindAllowsSourceRevokeAndCannotCreateLateSession()
    {
        var (s, provider, dependent, exactProvider, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var old = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Kernel.RegisterNativeServiceDrain(exactProvider.Process, old, () =>
        {
            entered.SetResult();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
            return KernelResult.Ok();
        });
        var pending = Task.Run(() => Rebind(s.Supervisor, provider.Identity, exactProvider));
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
        var (s, provider, dependent, exactProvider, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var old = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        s.Kernel.RegisterNativeServiceDrain(exactProvider.Process, old, () =>
        {
            Assert.True(s.Supervisor.ReportHealth(peer, ServiceHealthState.Degraded, "Independent health transition").IsSuccess);
            return KernelResult.Ok();
        });
        Assert.Equal(KernelError.InvalidTransition, Rebind(s.Supervisor, provider.Identity, exactProvider).Error);
        var after = s.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Degraded, after.State);
        Assert.Equal("Independent health transition", after.Health!.Reason);
    }

    [Fact]
    public void RebindMultipleContractsAdvancesOnlyItsOwnCommittedSnapshots()
    {
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock, new SupervisorTimeProvider());
        var first = Contract("MultiFirst");
        var second = Contract("MultiSecond");
        var image = new byte[] { 254, 1 };
        var services = new[] { new ProvidedServiceManifestV1("multi-provider", first), new ProvidedServiceManifestV1("multi-second", second) };
        var manifest = new ServiceManifestV1(new("multi-provider"), new("1.1"), Digest(image),
            TestFixtures.Manifest(254, 2054, 1, "multi-entry"), services);
        var provider = new ManagedServiceDefinition(new ComponentAdmissionPlan(manifest, image,
            providedServices: services.Select(item => new ComponentProvidedServiceRegistration(item, Protocol(item.Contract)))), "multi-provider");
        var dependent = Definition("multi-peer", 255, 2055, Contract("MultiPeer"),
            dependencies: [new(first, ServiceDependencyKind.Hard), new(second, ServiceDependencyKind.Hard)]);
        Assert.True(s.Supervisor.Register(provider).IsSuccess);
        Assert.True(s.Supervisor.Register(dependent).IsSuccess);
        Assert.True(s.Supervisor.StartAll().IsSuccess);
        var exact = s.Supervisor.Query(provider.Identity).Value!.Instance!.Value;
        var oldPeer = s.Supervisor.Query(dependent.Identity).Value!.Instance!.Value;
        Assert.True(s.Supervisor.ReportHealth(oldPeer, ServiceHealthState.Degraded, "Waiting for fresh contracts").IsSuccess);
        var freshImage = new byte[] { 254, 2 };
        var freshManifest = new ServiceManifestV1(new("multi-provider"), new("1.2"), Digest(freshImage),
            TestFixtures.Manifest(254, 2054, 2, "multi-entry"), services);
        var freshDefinition = new ManagedServiceDefinition(new ComponentAdmissionPlan(freshManifest, freshImage,
            providedServices: services.Select(item => new ComponentProvidedServiceRegistration(item, Protocol(item.Contract)))), "multi-provider");
        var rebindClocks = 0;
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
        var peer = s.Supervisor.Query(dependent.Identity).Value!;
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
        var clock = new CallbackHealthClock();
        var (s, provider, dependent, exactProvider, _) = CreateFailureDependencyScenario(clock);
        var before = s.Supervisor.Query(dependent.Identity).Value!;
        var sessions = s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions.ToArray();
        clock.OnRead = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            if (fault == 3) throw new InvalidOperationException("Rebind clock fault.");
        };
        Assert.Equal(fault == 3 ? KernelError.PlatformFaulted : KernelError.SupervisorDenied,
            Rebind(s.Supervisor, provider.Identity, exactProvider).Error);
        var after = InspectSupervisorRecord(s.Supervisor, dependent.Identity);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Health, after.Health);
        Assert.Equal(before.Dependencies.ToArray(), after.Dependencies.ToArray());
        Assert.Equal(sessions, s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions.ToArray());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RebindClockReplacementRefusesCapturedProviderOrDependent(bool replaceProvider)
    {
        var clock = new CallbackHealthClock();
        var (s, provider, dependent, exactProvider, peer) = CreateFailureDependencyScenario(clock);
        var target = replaceProvider ? provider : dependent;
        clock.OnRead = () => Assert.True(s.Supervisor.Replace(replaceProvider ? exactProvider : peer,
            Definition(target.Identity.Name, replaceProvider ? 251ul : 252ul, replaceProvider ? 2051ul : 2052ul,
                Contract(replaceProvider ? "FailureProvider" : "FailurePeer"), generation: 2)).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, Rebind(s.Supervisor, provider.Identity, exactProvider).Error);
        var fresh = s.Supervisor.Query(target.Identity).Value!;
        Assert.Equal(2ul, fresh.Instance!.Value.Process.Generation);
        Assert.Equal(ServiceLifecycleState.Ready, fresh.State);
        Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(target.Identity).Value!.State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void RebindNativeCallbackSourceLossRefusesNewSessionAndPublication(int loss)
    {
        var (s, provider, dependent, exactProvider, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var before = s.Supervisor.Query(dependent.Identity).Value!;
        var old = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        s.Kernel.RegisterNativeServiceDrain(exactProvider.Process, old, () =>
        {
            if (loss == 0) Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
            if (loss == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (loss is 2 or 3) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            if (loss == 3)
            {
                var (_, fresh) = TestFixtures.Create(s.Kernel, 900, 9000, generation: 2, identity: "supervisor-control");
                Assert.True(s.Kernel.AdmitProcess(fresh).IsSuccess);
                Assert.True(s.Kernel.MintCapability(s.IssuerDomain, fresh, ResourceKind.KernelService,
                    CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Configure | CapabilityRights.Execute).IsSuccess);
            }
            return KernelResult.Ok();
        });
        Assert.Equal(KernelError.SupervisorDenied, Rebind(s.Supervisor, provider.Identity, exactProvider).Error);
        var after = InspectSupervisorRecord(s.Supervisor, dependent.Identity);
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
        var clock = new CallbackHealthClock();
        var (s, provider, dependent, exactProvider, _) = CreateFailureDependencyScenario(clock);
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnRead = () => { entered.SetResult(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10))); };
        var rebinding = Task.Run(() => Rebind(s.Supervisor, provider.Identity, exactProvider));
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
        var (s, _, dependent, failed, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        var session = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        var channel = s.Kernel.EndpointSessions.Resolve(session, peer.Process).Value!.Channel;
        Assert.True(s.Kernel.Send(peer.Process, failed.Process, channel, 1).IsSuccess);
        var request = s.Kernel.ReceiveSessionRequest(failed.Process, session);
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
        var (s, _, dependent, failed, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        var before = s.Kernel.QueryComponent(dependent.Identity).Value!;
        var session = Assert.Single(before.Sessions);
        var refuse = true;
        var calls = 0;
        s.Kernel.RegisterNativeServiceDrain(failed.Process, session, () =>
        {
            calls++;
            if (!refuse) return KernelResult.Ok();
            if (throws) throw new InvalidOperationException("Unknown native closure.");
            return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Native closure refused.");
        });
        var refused = s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor);
        Assert.Equal(throws ? KernelError.PlatformFaulted : KernelError.ExternalEffectUncontained, refused.Error);
        var after = s.Kernel.QueryComponent(dependent.Identity).Value!;
        Assert.Equal(before.Sessions.ToArray(), after.Sessions.ToArray());
        Assert.Equal(before.ProcessBudget, after.ProcessBudget);
        Assert.True(s.Kernel.EndpointSessions.Resolve(session, peer.Process).IsSuccess);
        refuse = false;
        var retried = s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor);
        Assert.True(retried.IsSuccess, retried.Message);
        Assert.Equal(2, calls);
        Assert.NotEqual(session, retried.Value);
        Assert.Equal(retried.Value, Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions));
    }

    [Fact]
    public void DependencyRebindRetainsPinnedSessionUntilExistingOwnerCleanup()
    {
        var (s, _, dependent, failed, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        var session = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        var pin = s.Kernel.EndpointSessions.AcquirePin(session, peer.Process, failed.Process);
        Assert.True(pin.IsSuccess);
        try
        {
            Assert.Equal(KernelError.SessionDraining,
                s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor).Error);
            Assert.Equal(1, s.Kernel.EndpointSessions.ActivePinCount(session));
            Assert.Equal(session, Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions));
        }
        finally { pin.Value!.Dispose(); }
        var reconciled = s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor);
        Assert.True(reconciled.IsSuccess, reconciled.Message);
        Assert.NotEqual(session, reconciled.Value);
        Assert.Equal(reconciled.Value, Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions));
        Assert.True(s.Kernel.DrainComponent(dependent.Identity, peer.Process).Value!.Reclaimable);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExactDependencyRebindRefusesStaleTargetBeforeSessionMutation(bool replace)
    {
        var (s, _, dependent, _, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        if (replace)
            Assert.True(s.Supervisor.Replace(peer, Definition(dependent.Identity.Name, 252, 2052,
                Contract("FailurePeer"), generation: 2,
                dependencies: [new(Contract("FailureProvider"), ServiceDependencyKind.Hard)])).IsSuccess);
        var before = s.Kernel.QueryComponent(dependent.Identity).Value!;
        var stale = replace ? peer.Process : peer.Process with { Generation = peer.Process.Generation + 1 };
        var refused = s.Kernel.RebindComponentDependency(dependent.Identity, stale, descriptor);
        Assert.Equal(KernelError.StaleGeneration, refused.Error);
        var after = s.Kernel.QueryComponent(dependent.Identity).Value!;
        Assert.Equal(before.Process, after.Process);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Sessions.ToArray(), after.Sessions.ToArray());
        Assert.Equal(before.Capabilities.ToArray(), after.Capabilities.ToArray());
        Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(after.Process).Value!.State);
    }

    [Fact]
    public void ExactDependencyRebindAcceptsCurrentTargetThroughExistingConsumer()
    {
        var (s, _, dependent, _, peer) = CreateFailureDependencyScenario(new CallbackHealthClock());
        var descriptor = Assert.Single(s.Kernel.ResolveByContract(Contract("FailureProvider")).Value!);
        var before = Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
        var rebound = s.Kernel.RebindComponentDependency(dependent.Identity, peer.Process, descriptor);
        Assert.True(rebound.IsSuccess, rebound.Message);
        Assert.NotEqual(before, rebound.Value);
        Assert.Equal(rebound.Value, Assert.Single(s.Kernel.QueryComponent(dependent.Identity).Value!.Sessions));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void DependencyFailureClockRefusesSourceLossOrFaultWithoutPartialPeerMutation(int fault)
    {
        var clock = new CallbackHealthClock();
        var (s, provider, dependent, failed, _) = CreateFailureDependencyScenario(clock);
        var before = s.Supervisor.Query(dependent.Identity).Value!;
        clock.OnRead = () => clock.OnRead = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            if (fault == 3) throw new InvalidOperationException("Propagation clock fault.");
        };
        var refused = s.Supervisor.ReportHealth(failed, ServiceHealthState.Failed, "Failure propagation");
        Assert.Equal(fault == 3 ? KernelError.PlatformFaulted : KernelError.SupervisorDenied, refused.Error);
        var after = InspectSupervisorRecord(s.Supervisor, dependent.Identity);
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
        var clock = new CallbackHealthClock();
        var (s, provider, dependent, failed, peer) = CreateFailureDependencyScenario(clock);
        var definition = replaceProvider ? provider : dependent;
        var previous = replaceProvider ? failed : peer;
        var contract = replaceProvider ? Contract("FailureProvider") : Contract("FailurePeer");
        clock.OnRead = () => clock.OnRead = () => Assert.True(s.Supervisor.Replace(previous,
            Definition(definition.Identity.Name, previous.Process.ProcessId.Value,
                replaceProvider ? 2051ul : 2052ul, contract, generation: 2)).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration,
            s.Supervisor.ReportHealth(failed, ServiceHealthState.Failed, "Old provider failure").Error);
        var fresh = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, fresh.State);
        Assert.Equal(2ul, fresh.Instance!.Value.Process.Generation);
        Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(fresh.Instance.Value.Process).Value!.State);
        Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(definition.Identity).Value!.State);
    }

    [Fact]
    public void DependencyFailureBatchValidatesEveryPeerBeforeFirstMutation()
    {
        var clock = new CallbackHealthClock();
        var (s, _, dependent, failed, _) = CreateFailureDependencyScenario(clock);
        var second = Definition("failure-peer-second", 253, 2053, Contract("FailurePeerSecond"),
            dependencies: [new(Contract("FailureProvider"), ServiceDependencyKind.Hard)]);
        Assert.True(s.Supervisor.Register(second).IsSuccess);
        var secondInstance = s.Supervisor.Start(second.Identity).Value!.Snapshot.Instance!.Value;
        var before = s.Supervisor.Query(dependent.Identity).Value!;
        clock.OnRead = () => clock.OnRead = () => Assert.True(s.Supervisor.Drain(secondInstance).Value!.ClosureProven);
        Assert.Equal(KernelError.InvalidTransition,
            s.Supervisor.ReportHealth(failed, ServiceHealthState.Failed, "Captured peer changed").Error);
        var first = s.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(before.State, first.State);
        Assert.Equal(before.Health, first.Health);
        Assert.Equal(ServiceLifecycleState.Stopped, s.Supervisor.Query(second.Identity).Value!.State);
    }

    [Fact]
    public async Task BlockedDependencyFailureClockAllowsQueryAndSourceRevoke()
    {
        var clock = new CallbackHealthClock();
        var (s, _, dependent, failed, _) = CreateFailureDependencyScenario(clock);
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnRead = () => clock.OnRead = () => { entered.SetResult(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10))); };
        var propagating = Task.Run(() => s.Supervisor.ReportHealth(failed, ServiceHealthState.Failed, "Failure"));
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
        var s = CreateSupervisor(clock, new SupervisorTimeProvider());
        var provider = Definition("failure-provider", 251, 2051, Contract("FailureProvider"));
        var dependent = Definition("failure-peer", 252, 2052, Contract("FailurePeer"),
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
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var definition = Definition("readiness-commit", 246, 2046, Contract("ReadinessCommit"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        clock.OnRead = () =>
        {
            Assert.Equal(ServiceLifecycleState.Admitting, s.Supervisor.Query(definition.Identity).Value!.State);
            if (fault == 0) Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            if (fault == 3) throw new InvalidOperationException("Readiness clock fault.");
        };
        var refused = s.Supervisor.Start(definition.Identity);
        Assert.Equal(fault == 3 ? KernelError.PlatformFaulted : KernelError.SupervisorDenied, refused.Error);
        var after = InspectSupervisorRecord(s.Supervisor, definition.Identity);
        Assert.Null(after.Instance);
        Assert.Null(after.Health);
        Assert.Empty(after.Dependencies);
        Assert.Equal(fault == 3 ? ServiceLifecycleState.Stopped : ServiceLifecycleState.Admitting, after.State);
        Assert.True(s.Kernel.QueryComponent(definition.Identity).Value!.Reclaimable);
        Assert.Equal(KernelError.ServiceNotFound, s.Kernel.ResolveByServiceName(definition.PrimaryServiceName).Error);
    }

    [Fact]
    public void ReadinessClockDependencyLossIsRevalidatedBeforePublication()
    {
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var providerContract = Contract("ReadinessProvider");
        var provider = Definition("readiness-provider", 247, 2047, providerContract);
        var dependent = Definition("readiness-dependent", 248, 2048, Contract("ReadinessDependent"),
            dependencies: [new(providerContract, ServiceDependencyKind.Hard)]);
        Assert.True(s.Supervisor.Register(provider).IsSuccess);
        Assert.True(s.Supervisor.Register(dependent).IsSuccess);
        var instance = s.Supervisor.Start(provider.Identity).Value!.Snapshot.Instance!.Value;
        clock.OnRead = () => Assert.True(s.Supervisor.Drain(instance).Value!.ClosureProven);
        Assert.Equal(KernelError.DependencyUnavailable, s.Supervisor.Start(dependent.Identity).Error);
        var after = s.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Stopped, after.State);
        Assert.Null(after.Instance);
        Assert.Null(after.Health);
        Assert.Empty(after.Dependencies);
        Assert.True(s.Kernel.QueryComponent(dependent.Identity).Value!.Reclaimable);
    }

    [Fact]
    public void LateAdmissionCompensationCannotDrainOrOverwriteReplacement()
    {
        var s = CreateSupervisor();
        var contract = Contract("CompensationAba");
        var definition = Definition("compensation-aba", 249, 2049, contract);
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var next = Definition("compensation-aba", 249, 2049, contract, generation: 2);
        Assert.True(s.Supervisor.Replace(instance, next).IsSuccess);
        var compensate = typeof(CapabilityAwareServiceSupervisor).GetMethod("CompensateIncompleteAdmission", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var refused = (KernelResult<ServiceStartReceipt>)compensate.Invoke(s.Supervisor,
            [definition.Identity, definition, instance.Process, KernelError.PlatformFaulted, "Late old completion"] )!;
        Assert.Equal(KernelError.PlatformFaulted, refused.Error);
        var fresh = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, fresh.State);
        Assert.Equal(2ul, fresh.Instance!.Value.Process.Generation);
        Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(definition.Identity).Value!.State);
        Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(fresh.Instance.Value.Process).Value!.State);
    }

    [Fact]
    public async Task BlockedReadinessClockAllowsQueryAndSourceRevocation()
    {
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var definition = Definition("readiness-blocked", 250, 2050, Contract("ReadinessBlocked"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnRead = () => { entered.SetResult(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10))); };
        var admitting = Task.Run(() => s.Supervisor.Start(definition.Identity));
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
        var clock = new CallbackHealthClock();
        var kernelClock = new SupervisorTimeProvider();
        var s = CreateSupervisor(clock, kernelClock);
        var definition = Definition("deadline-commit", 243, 2043, Contract("DeadlineCommit"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var before = s.Supervisor.Start(definition.Identity).Value!.Snapshot;
        var instance = before.Instance!.Value;
        var operation = SubmitOperation(s.Kernel, instance.Process);
        var scope = s.Kernel.CreateCancellationScope(instance.Process, new(1)).Value!.Scope;
        kernelClock.AdvanceMilliseconds(1);
        clock.OnRead = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            if (fault == 3) throw new InvalidOperationException("Timeout publication clock fault.");
        };
        var policy = quarantine ? ServiceDrainTimeoutPolicy.Quarantine : ServiceDrainTimeoutPolicy.FailReplacement;
        var refused = s.Supervisor.Drain(instance, scope, policy);
        Assert.Equal(fault == 3 ? KernelError.PlatformFaulted : KernelError.SupervisorDenied, refused.Error);
        var after = InspectSupervisorRecord(s.Supervisor, definition.Identity);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Health, after.Health);
        Assert.Equal(before.BlockingReason, after.BlockingReason);
        Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(definition.Identity).Value!.State);
        Assert.Equal(ExternalOperationState.Submitted, s.Kernel.QueryExternalOperation(instance.Process, operation.Operation).Value!.State);
        Assert.False(s.Kernel.ReleaseRegion(instance.Process, operation.Output).IsSuccess);
        if (fault == 3)
        {
            var recovered = s.Supervisor.Drain(instance, scope, policy);
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
        var clock = new CallbackHealthClock();
        var kernelClock = new SupervisorTimeProvider();
        var s = CreateSupervisor(clock, kernelClock);
        var contract = Contract("DeadlineAba");
        var definition = Definition("deadline-aba", 244, 2044, contract);
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var scope = s.Kernel.CreateCancellationScope(instance.Process, new(1)).Value!.Scope;
        kernelClock.AdvanceMilliseconds(1);
        clock.OnRead = () => Assert.True(s.Supervisor.Replace(instance,
            Definition("deadline-aba", 244, 2044, contract, generation: 2)).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, s.Supervisor.Drain(instance, scope,
            quarantine ? ServiceDrainTimeoutPolicy.Quarantine : ServiceDrainTimeoutPolicy.FailReplacement).Error);
        var fresh = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, fresh.State);
        Assert.Equal(2ul, fresh.Instance!.Value.Process.Generation);
        Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(fresh.Instance.Value.Process).Value!.State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BlockedDeadlinePublicationClockAllowsQueryAndRevoke(bool quarantine)
    {
        var clock = new CallbackHealthClock();
        var kernelClock = new SupervisorTimeProvider();
        var s = CreateSupervisor(clock, kernelClock);
        var definition = Definition("deadline-blocked", 245, 2045, Contract("DeadlineBlocked"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var scope = s.Kernel.CreateCancellationScope(instance.Process, new(1)).Value!.Scope;
        kernelClock.AdvanceMilliseconds(1);
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnRead = () => { entered.SetResult(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10))); };
        var publishing = Task.Run(() => s.Supervisor.Drain(instance, scope,
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
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var definition = Definition("drain-request", 240, 2040, Contract("DrainRequest"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var before = s.Supervisor.Start(definition.Identity).Value!.Snapshot;
        var instance = before.Instance!.Value;
        clock.OnRead = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            if (fault == 3) throw new InvalidOperationException("Drain request clock fault.");
        };
        Assert.Equal(fault == 3 ? KernelError.PlatformFaulted : KernelError.SupervisorDenied,
            s.Supervisor.Drain(instance).Error);
        var after = InspectSupervisorRecord(s.Supervisor, definition.Identity);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Health, after.Health);
        Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(definition.Identity).Value!.State);
        Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(instance.Process).Value!.State);
        Assert.Equal(KernelError.InvalidTransition, s.Kernel.QueryProcessTeardown(instance.Process).Error);
        if (fault == 3) Assert.True(s.Supervisor.Drain(instance).Value!.ClosureProven);
    }

    [Fact]
    public void DrainRequestClockReplacementCannotDrainFreshProcess()
    {
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var contract = Contract("RequestAba");
        var definition = Definition("request-aba", 241, 2041, contract);
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        clock.OnRead = () => Assert.True(s.Supervisor.Replace(instance,
            Definition("request-aba", 241, 2041, contract, generation: 2)).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, s.Supervisor.Drain(instance).Error);
        var fresh = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, fresh.State);
        Assert.Equal(2ul, fresh.Instance!.Value.Process.Generation);
        Assert.Equal(KernelError.StaleGeneration, s.Kernel.DrainComponent(definition.Identity, instance.Process).Error);
        Assert.Equal(ProcessState.Running, s.Kernel.Processes.Resolve(fresh.Instance.Value.Process).Value!.State);
        Assert.Equal(ComponentLifecycleState.Running, s.Kernel.QueryComponent(definition.Identity).Value!.State);
    }

    [Fact]
    public async Task BlockedDrainRequestClockAllowsQueryAndSourceRevoke()
    {
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var definition = Definition("request-blocked", 242, 2042, Contract("RequestBlocked"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnRead = () => { entered.SetResult(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10))); };
        var requesting = Task.Run(() => s.Supervisor.Drain(instance));
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
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var definition = Definition("drain-effect", 239, 2039, Contract("DrainEffect"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var operation = SubmitOperation(s.Kernel, instance.Process);
        if (providerLost)
            Assert.True(s.Kernel.RecordExternalOperationProviderLoss(instance.Process, operation.Operation).IsSuccess);
        Assert.True(s.Supervisor.Drain(instance).IsSuccess);
        var before = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(providerLost ? ServiceLifecycleState.Quarantined : ServiceLifecycleState.Draining, before.State);
        clock.OnRead = () => throw new InvalidOperationException("Completion observation fault.");
        Assert.Equal(KernelError.PlatformFaulted, s.Supervisor.Observe(definition.Identity).Error);
        var after = s.Supervisor.Query(definition.Identity).Value!;
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
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var definition = Definition("drain-commit", 236, 2036, Contract("DrainCommit"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var calls = 0;
        s.Kernel.RegisterComposedWorkTeardownParticipant(new SupervisorClosureCallback(() =>
            ++calls == 1 ? KernelResult.Fail(KernelError.PlatformFaulted, "Initial refusal.") : KernelResult.Ok(), instance.Process));
        Assert.True(s.Supervisor.Drain(instance).IsSuccess);
        var before = s.Supervisor.Query(definition.Identity).Value!;
        clock.OnRead = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.RevokeCapability(s.ControlCapability).IsSuccess);
            if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            if (fault == 3) throw new InvalidOperationException("Completion clock fault.");
        };
        var refused = s.Supervisor.Observe(definition.Identity);
        Assert.Equal(fault == 3 ? KernelError.PlatformFaulted : KernelError.SupervisorDenied, refused.Error);
        var after = InspectSupervisorRecord(s.Supervisor, definition.Identity);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.Health, after.Health);
        Assert.Equal(before.BlockingReason, after.BlockingReason);
        Assert.True(s.Kernel.QueryComponent(definition.Identity).Value!.Reclaimable);
        if (fault == 3)
            Assert.Equal(ServiceLifecycleState.Stopped, s.Supervisor.Observe(definition.Identity).Value!.State);
    }

    [Fact]
    public void DrainCompletionClockReplacementCannotOverwriteFreshInstance()
    {
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var contract = Contract("DrainAba");
        var definition = Definition("drain-aba", 237, 2037, contract);
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var calls = 0;
        s.Kernel.RegisterComposedWorkTeardownParticipant(new SupervisorClosureCallback(() =>
            ++calls == 1 ? KernelResult.Fail(KernelError.PlatformFaulted, "Initial refusal.") : KernelResult.Ok(), instance.Process));
        Assert.True(s.Supervisor.Drain(instance).IsSuccess);
        clock.OnRead = () => Assert.True(s.Supervisor.Replace(instance,
            Definition("drain-aba", 237, 2037, contract, generation: 2)).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, s.Supervisor.Observe(definition.Identity).Error);
        var fresh = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, fresh.State);
        Assert.Equal(2ul, fresh.Instance!.Value.Process.Generation);
        Assert.Equal(ServiceHealthState.Ready, fresh.Health!.State);
    }

    [Fact]
    public async Task BlockedDrainCompletionClockHoldsNoSupervisorOrCapabilityGate()
    {
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var definition = Definition("drain-blocked", 238, 2038, Contract("DrainBlocked"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var calls = 0;
        s.Kernel.RegisterComposedWorkTeardownParticipant(new SupervisorClosureCallback(() =>
            ++calls == 1 ? KernelResult.Fail(KernelError.PlatformFaulted, "Initial refusal.") : KernelResult.Ok(), instance.Process));
        Assert.True(s.Supervisor.Drain(instance).IsSuccess);
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        clock.OnRead = () => { entered.SetResult(); Assert.True(resume.Wait(TimeSpan.FromSeconds(10))); };
        var completing = Task.Run(() => s.Supervisor.Observe(definition.Identity));
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
        var records = (IDictionary)typeof(CapabilityAwareServiceSupervisor).GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(supervisor)!;
        return (ServiceSupervisorSnapshot)typeof(CapabilityAwareServiceSupervisor).GetMethod("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(supervisor, [records[identity]!])!;
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void QuarantinedObserveUsesRealClosureBeforeStopOrRestart(bool planned, bool throws)
    {
        var clock = new SupervisorTimeProvider();
        var s = CreateSupervisor(clock);
        var contract = Contract("QuarantineRecovery");
        var policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 10, 100, 1000);
        var definition = Definition("quarantine-recovery", 234, 2034, contract, restart: policy,
            replacementFactory: generation => Plan("quarantine-recovery", 234, 2034, contract, generation, restart: policy));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var budget = s.Kernel.QueryComponent(definition.Identity).Value!.ProcessBudget;
        var calls = 0;
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

        var observed = s.Supervisor.Observe(definition.Identity);
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
        var s = CreateSupervisor();
        var definition = Definition("quarantine-control", 235, 2035, Contract("QuarantineControl"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var calls = 0;
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
        var before = calls;
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
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var contract = Contract("SourcePreflight");
        var requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "source-preflight", CapabilityRights.Read);
        var policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        var definition = Definition("source-preflight", 231, 2031, contract, capability: requirement, issuer: s.IssuerDomain,
            restart: policy, replacementFactory: generation => Plan("source-preflight", 231, 2031, contract, generation,
                restart: policy, capability: requirement, issuer: fault == 0 ? new DomainId(99999) : s.IssuerDomain));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        Assert.True(s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "failure").IsSuccess);
        var before = s.Kernel.QueryComponent(definition.Identity).Value!;
        if (fault == 1) TestFixtures.Create(s.Kernel, 231, 2031, 2, "other-live-process");
        if (fault == 2) TestFixtures.Create(s.Kernel, 232, 2032, identity: "source-preflight-entry");
        if (fault == 3)
        {
            var (_, newer) = TestFixtures.Create(s.Kernel, 231, 2031, 3, "newer-retired-process");
            Assert.True(s.Kernel.TerminateProcess(newer).IsSuccess);
        }
        clock.Timestamp = 100;
        var refused = s.Supervisor.Observe(definition.Identity);
        Assert.Equal(fault switch { 0 => KernelError.DomainNotFound, 1 => KernelError.DuplicateProcessId,
            2 => KernelError.DuplicateIdentity, _ => KernelError.StaleGeneration }, refused.Error);
        var after = s.Kernel.QueryComponent(definition.Identity).Value!;
        Assert.Equal(before.Process, after.Process);
        Assert.Equal(ComponentLifecycleState.Reclaimable, after.State);
        Assert.Equal(before.ProcessBudget, after.ProcessBudget);
        Assert.Equal(before.ServiceBudget, after.ServiceBudget);
        Assert.Equal(ServiceLifecycleState.RestartBackoff, s.Supervisor.Query(definition.Identity).Value!.State);
    }

    [Fact]
    public void ReplacementSelfDomainGrantRemainsRequestUntilRealMintConsumer()
    {
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var contract = Contract("SelfDomainReplacement");
        var requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "self-domain", CapabilityRights.Read);
        var policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        var definition = Definition("self-domain-replacement", 233, 2033, contract, capability: requirement, issuer: s.IssuerDomain,
            restart: policy, replacementFactory: generation => Plan("self-domain-replacement", 233, 2033, contract, generation,
                restart: policy, capability: requirement, issuer: new DomainId(2033)));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        Assert.True(s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "failure").IsSuccess);
        Assert.False(s.Kernel.Domains.Contains(new(2033)));
        var next = Plan("self-domain-replacement", 233, 2033, contract, 2, restart: policy,
            capability: requirement, issuer: new DomainId(2033));
        Assert.True(s.Kernel.ValidateReplacementAdmission(next, instance.Process).IsSuccess);
        Assert.False(s.Kernel.Domains.Contains(new(2033)));
        clock.Timestamp = 100;
        var replaced = s.Supervisor.Observe(definition.Identity);
        Assert.True(replaced.IsSuccess, replaced.Message);
        var component = s.Kernel.QueryComponent(definition.Identity).Value!;
        var capability = Assert.Single(component.Capabilities);
        Assert.True(s.Kernel.ValidateCapability(component.Process, capability, CapabilityRights.Read).IsSuccess);
        Assert.Equal(2ul, component.Process.Generation);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ReplacementAdmissionPreflightRefusalPreservesOldReclaimableComponent(int fault)
    {
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var contract = Contract("PreflightReplacement");
        var policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        var inject = true;
        var definition = Definition("preflight-replacement", 229, 2029, contract, restart: policy,
            replacementFactory: generation =>
            {
                var plan = Plan("preflight-replacement", 229, 2029, contract, generation, restart: policy,
                    compatibility: inject && fault == 2 ? new ServiceCompatibilityConstraintsV1(2, 2) : null,
                    dependencies: inject && fault == 3 ? [new(Contract("MissingReplacementDependency"), ServiceDependencyKind.Hard)] : null);
                return inject && fault is 0 or 1 ? new ComponentAdmissionPlan(plan.Manifest,
                    fault == 0 ? new byte[] { 0xff } : plan.Image, plan.Grants,
                    fault == 1 ? [] : plan.ProvidedServices) : plan;
            });
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        Assert.True(s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "failure").IsSuccess);
        var before = s.Kernel.QueryComponent(definition.Identity).Value!;
        clock.Timestamp = 100;
        var refused = s.Supervisor.Observe(definition.Identity);
        Assert.Equal(fault switch { 0 => KernelError.ComponentDigestMismatch, 1 => KernelError.InvalidManifest,
            2 => KernelError.PlatformUnsupported, _ => KernelError.ServiceNotFound }, refused.Error);
        var after = s.Kernel.QueryComponent(definition.Identity).Value!;
        Assert.Equal(before.Process, after.Process);
        Assert.Equal(before.State, after.State);
        Assert.Equal(before.ManifestDigest, after.ManifestDigest);
        Assert.Equal(before.ProcessBudget, after.ProcessBudget);
        Assert.Equal(before.ServiceBudget, after.ServiceBudget);
        var preserved = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.RestartBackoff, preserved.State);
        Assert.Equal(instance, preserved.Instance);
        inject = false;
        Assert.True(s.Supervisor.Observe(definition.Identity).IsSuccess);
        Assert.Equal(2ul, s.Supervisor.Query(definition.Identity).Value!.Instance!.Value.Process.Generation);
    }

    [Fact]
    public void ReplacementEvaluatorBypassRequiresExactClosedOldGeneration()
    {
        var s = CreateSupervisor();
        var contract = Contract("ExactReplacement");
        var definition = Definition("exact-replacement", 230, 2030, contract);
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var next = Plan("exact-replacement", 230, 2030, contract, 2);
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
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var contract = Contract("BlockedFactory");
        var policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var definition = Definition("blocked-factory", 228, 2028, contract, restart: policy,
            replacementFactory: generation =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                return Plan("blocked-factory", 228, 2028, contract, generation, restart: policy);
            });
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        Assert.True(s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "failure").IsSuccess);
        clock.Timestamp = 100;
        var pending = Task.Run(() => s.Supervisor.Observe(definition.Identity));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var query = await Task.Run(() => s.Supervisor.Query(definition.Identity)).WaitAsync(TimeSpan.FromSeconds(5));
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
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var contract = Contract("RestartDispatch");
        var policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        var calls = 0;
        var inject = true;
        var definition = Definition("restart-dispatch", 227, 2027, contract, restart: policy,
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
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        Assert.True(s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "failure").IsSuccess);
        clock.Timestamp = 100;
        if (fault == 0) clock.OnRead = () => throw new InvalidOperationException("eligibility clock fault");
        if (fault == 1) clock.OnRead = () => Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
        var refused = s.Supervisor.Observe(definition.Identity);
        Assert.Equal(fault switch { 0 or 3 or 5 => KernelError.PlatformFaulted, 1 or 2 => KernelError.SupervisorDenied,
            4 => KernelError.StaleGeneration, _ => KernelError.InvalidManifest }, refused.Error);
        Assert.Equal(fault < 2 ? 0 : 1, calls);
        var component = s.Kernel.QueryComponent(definition.Identity);
        Assert.True(component.IsSuccess);
        Assert.Equal(ComponentLifecycleState.Reclaimable, component.Value!.State);
        if (fault is not 1 and not 2)
        {
            var snapshot = s.Supervisor.Query(definition.Identity).Value!;
            Assert.Equal(ServiceLifecycleState.RestartBackoff, snapshot.State);
            Assert.Equal(instance, snapshot.Instance);
            Assert.Equal(1u, snapshot.RestartAttemptsInWindow);
            inject = false;
            var recovered = s.Supervisor.Observe(definition.Identity);
            Assert.True(recovered.IsSuccess, recovered.Message);
            Assert.Equal(2ul, recovered.Value!.Instance!.Value.Process.Generation);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void RestartFrequencyCallbackRevalidatesSourceAndExactOldInstance(int fault)
    {
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var contract = Contract("RestartSource");
        var policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        var definition = Definition("restart-source", 226, 2026, contract, restart: policy,
            replacementFactory: generation => Plan("restart-source", 226, 2026, contract, generation, restart: policy));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        clock.OnFrequency = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
            else if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            else if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            else Assert.True(s.Supervisor.Replace(instance,
                new ManagedServiceDefinition(Plan("restart-source", 226, 2026, contract, 2, restart: policy), "restart-source")).IsSuccess);
        };
        var refused = s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "failure");
        Assert.Equal(fault == 3 ? KernelError.StaleGeneration : KernelError.SupervisorDenied, refused.Error);
        var records = (IDictionary)typeof(CapabilityAwareServiceSupervisor).GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Supervisor)!;
        var snapshot = (ServiceSupervisorSnapshot)typeof(CapabilityAwareServiceSupervisor).GetMethod("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(s.Supervisor, [records[definition.Identity]!])!;
        Assert.Equal(0u, snapshot.RestartAttemptsInWindow);
        Assert.Null(snapshot.RestartEligibleTimestamp);
        Assert.Equal(fault == 3 ? ServiceLifecycleState.Ready : ServiceLifecycleState.Stopped, snapshot.State);
        Assert.Equal(fault == 3 ? 2ul : 1ul, snapshot.Instance!.Value.Process.Generation);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void RestartObservationFaultPreservesStoppedStateAndRetriesThroughObserve(int fault)
    {
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var contract = Contract("RestartClock");
        var policy = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        var definition = Definition("restart-clock", 225, 2025, contract, restart: policy,
            replacementFactory: generation => Plan("restart-clock", 225, 2025, contract, generation, restart: policy));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var instance = s.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        clock.Timestamp = fault == 1 ? long.MaxValue : 10;
        clock.FaultFrequency = fault == 0;
        clock.Frequency = fault == 2 ? 0 : fault == 3 ? -1 : 1000;
        var refused = s.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "failure");
        Assert.Equal(KernelError.PlatformFaulted, refused.Error);
        var stopped = s.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Stopped, stopped.State);
        Assert.Equal(0u, stopped.RestartAttemptsInWindow);
        Assert.Null(stopped.RestartEligibleTimestamp);
        Assert.Equal(ComponentLifecycleState.Reclaimable, s.Kernel.QueryComponent(definition.Identity).Value!.State);
        clock.Timestamp = 10;
        clock.FaultFrequency = false;
        clock.Frequency = 1000;
        var recovered = s.Supervisor.Observe(definition.Identity);
        Assert.True(recovered.IsSuccess, recovered.Message);
        Assert.Equal(ServiceLifecycleState.RestartBackoff, recovered.Value!.State);
        Assert.Equal(1u, recovered.Value.RestartAttemptsInWindow);
        Assert.Equal(instance, recovered.Value.Instance);
    }

    [Fact]
    public async Task HealthClockObservationDoesNotHoldSupervisorOrCapabilityGate()
    {
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var definition = Definition("health-unlocked", 224, 2024, Contract("HealthUnlocked"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        Assert.True(s.Supervisor.Start(definition.Identity).IsSuccess);
        var before = s.Supervisor.Query(definition.Identity).Value!;
        using var observed = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        clock.OnRead = () =>
        {
            observed.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        var pending = Task.Run(() => s.Supervisor.ReportHealth(before.Instance!.Value, ServiceHealthState.Degraded, "late"));
        try
        {
            Assert.True(observed.Wait(TimeSpan.FromSeconds(10)));
            var query = await Task.Run(() => s.Supervisor.Query(definition.Identity)).WaitAsync(TimeSpan.FromSeconds(5));
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
        var clock = new CallbackHealthClock();
        var s = CreateSupervisor(clock);
        var definition = Definition("health-clock", 223, 2023, Contract("HealthClock"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        var started = s.Supervisor.Start(definition.Identity);
        Assert.True(started.IsSuccess, started.Message);
        var before = s.Supervisor.Query(definition.Identity).Value!;
        clock.OnRead = () =>
        {
            if (fault == 0) Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.ControlCapability).IsSuccess);
            else if (fault == 1) s.Kernel.CapabilityAuthority.RevokeAllForDomain(s.IssuerDomain);
            else if (fault == 2) Assert.True(s.Kernel.TerminateProcess(s.Principal).IsSuccess);
            else throw new InvalidOperationException("injected health clock failure");
        };
        var refused = s.Supervisor.ReportHealth(before.Instance!.Value, ServiceHealthState.Degraded, "refused");
        Assert.Equal(fault == 3 ? KernelError.PlatformFaulted : KernelError.SupervisorDenied, refused.Error);
        Assert.Null(refused.Value);
        var records = (IDictionary)typeof(CapabilityAwareServiceSupervisor).GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Supervisor)!;
        var record = records[definition.Identity]!;
        var snapshot = (ServiceSupervisorSnapshot)typeof(CapabilityAwareServiceSupervisor).GetMethod("Snapshot", BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(s.Supervisor, [record])!;
        Assert.Equal(before.Health, snapshot.Health);
        Assert.Equal(before.State, snapshot.State);
        if (fault == 3)
            Assert.True(s.Supervisor.ReportHealth(before.Instance.Value, ServiceHealthState.Degraded, "recovered").IsSuccess);
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
                var action = OnFrequency;
                OnFrequency = null;
                action?.Invoke();
                return FaultFrequency ? throw new InvalidOperationException("injected frequency fault") : Frequency;
            }
        }
        public override long GetTimestamp()
        {
            var action = OnRead;
            OnRead = null;
            action?.Invoke();
            return Timestamp;
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task QueryWaitingOnSupervisorGateCannotPublishAfterSourceRevocation(int fault)
    {
        var s = CreateSupervisor();
        var definition = Definition("revoked-query", 222, 2022, Contract("RevokedQuery"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        Assert.Equal(ServiceLifecycleState.Declared, s.Supervisor.Query(definition.Identity).Value!.State);
        var gate = typeof(CapabilityAwareServiceSupervisor).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Supervisor)!;
        var completion = new TaskCompletionSource<KernelResult<ServiceSupervisorSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
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
        var refused = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(KernelError.SupervisorDenied, refused.Error);
        Assert.Null(refused.Value);
        var records = (IDictionary)typeof(CapabilityAwareServiceSupervisor).GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Supervisor)!;
        Assert.Single(records);
        Assert.Equal(KernelError.ProcessNotFound, s.Kernel.Processes.Resolve(new(new(222), 1)).Error);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task RegistrationWaitingOnSupervisorGateCannotPublishAfterSourceRevocation(int fault)
    {
        var s = CreateSupervisor();
        var definition = Definition("revoked-registration", 220, 2020, Contract("RevokedRegistration"));
        var gate = typeof(CapabilityAwareServiceSupervisor).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Supervisor)!;
        var completion = new TaskCompletionSource<KernelResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
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
        var refused = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(refused.IsSuccess);
        Assert.Equal(KernelError.SupervisorDenied, refused.Error);
        var records = (IDictionary)typeof(CapabilityAwareServiceSupervisor).GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Supervisor)!;
        Assert.Empty(records);
        Assert.Equal(KernelError.ProcessNotFound, s.Kernel.Processes.Resolve(new(new(220), 1)).Error);
    }

    [Fact]
    public void RegistrationOwnerCommitRejectsStaleAndWrongSourceWithoutCallback()
    {
        var s = CreateSupervisor();
        var called = false;
        KernelResult Publish() { called = true; return KernelResult.Ok(); }
        Assert.False(s.Kernel.CapabilityAuthority.CommitSupervisorRecordPublication(s.ControlCapability,
            s.IssuerDomain, s.Principal.Generation + 1, Publish).IsSuccess);
        Assert.False(s.Kernel.CapabilityAuthority.CommitSupervisorRecordPublication(s.ControlCapability,
            new(9999), s.Principal.Generation, Publish).IsSuccess);
        foreach (var wrong in new[]
        {
            s.Kernel.MintCapability(s.IssuerDomain, s.Principal, ResourceKind.KernelService,
                CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Read).Value!.CapabilityId,
            s.Kernel.MintCapability(s.IssuerDomain, s.Principal, ResourceKind.KernelService,
                "other-supervisor", CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId,
        })
            Assert.False(s.Kernel.CapabilityAuthority.CommitSupervisorRecordPublication(wrong,
                s.IssuerDomain, s.Principal.Generation, Publish).IsSuccess);
        Assert.False(called);
        var definition = Definition("valid-registration", 221, 2021, Contract("ValidRegistration"));
        Assert.True(s.Supervisor.Register(definition).IsSuccess);
        Assert.Equal(KernelError.DuplicateIdentity, s.Supervisor.Register(definition).Error);
        Assert.Equal(ServiceLifecycleState.Declared, s.Supervisor.Query(definition.Identity).Value!.State);
    }

    [Fact]
    public void HardDependenciesStartFirstAndBindExactGeneration()
    {
        var scenario = CreateSupervisor();
        var provider = Definition("provider", 201, 2001, Contract("Provider"));
        var dependent = Definition("dependent", 202, 2002, Contract("Dependent"),
            dependencies: [new(provider.Admission.Manifest.ProvidedContracts[0].Contract, ServiceDependencyKind.Hard)]);
        Assert.True(scenario.Supervisor.Register(dependent).IsSuccess);
        Assert.True(scenario.Supervisor.Register(provider).IsSuccess);

        var started = scenario.Supervisor.StartAll();

        Assert.True(started.IsSuccess, started.Message);
        Assert.Equal(new[] { provider.Identity, dependent.Identity }, started.Value!.Select(receipt => receipt.Request.Identity));
        var dependentSnapshot = scenario.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, dependentSnapshot.State);
        var binding = Assert.Single(dependentSnapshot.Dependencies);
        Assert.Equal(scenario.Supervisor.Query(provider.Identity).Value!.Instance, binding.Provider);
        Assert.Equal(dependentSnapshot.Instance, binding.Consumer);
    }

    [Fact]
    public void OptionalDependencyAbsenceIsExplicitDegradation()
    {
        var scenario = CreateSupervisor();
        var service = Definition("optional", 203, 2003, Contract("OptionalConsumer"),
            dependencies: [new(Contract("Absent"), ServiceDependencyKind.Optional)]);
        Assert.True(scenario.Supervisor.Register(service).IsSuccess);

        var started = scenario.Supervisor.Start(service.Identity);

        Assert.True(started.IsSuccess, started.Message);
        Assert.Equal(ServiceLifecycleState.Degraded, started.Value!.Snapshot.State);
        Assert.Equal(ServiceHealthState.Degraded, started.Value.Snapshot.Health!.State);
        Assert.Empty(started.Value.Snapshot.Dependencies);
    }

    [Fact]
    public void OptionalDependencyLateArrivalUsesExplicitSessionRebind()
    {
        var scenario = CreateSupervisor();
        var providerContract = Contract("LateProvider");
        var dependent = Definition("late-dependent", 213, 2013, Contract("LateDependent"),
            dependencies: [new(providerContract, ServiceDependencyKind.Optional)]);
        Assert.True(scenario.Supervisor.Register(dependent).IsSuccess);
        Assert.True(scenario.Supervisor.Start(dependent.Identity).IsSuccess);
        Assert.Equal(ServiceLifecycleState.Degraded, scenario.Supervisor.Query(dependent.Identity).Value!.State);
        var provider = Definition("late-provider", 214, 2014, providerContract);

        Assert.True(scenario.Supervisor.Register(provider).IsSuccess);
        Assert.True(scenario.Supervisor.Start(provider.Identity).IsSuccess);

        var rebound = scenario.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, rebound.State);
        Assert.Single(rebound.Dependencies);
        Assert.Single(scenario.Kernel.QueryComponent(dependent.Identity).Value!.Sessions);
    }

    [Fact]
    public void DependencyCycleIsRejectedBeforeProcessEffects()
    {
        var scenario = CreateSupervisor();
        var contractA = Contract("CycleA");
        var contractB = Contract("CycleB");
        var a = Definition("cycle-a", 204, 2004, contractA, dependencies: [new(contractB, ServiceDependencyKind.Hard)]);
        var b = Definition("cycle-b", 205, 2005, contractB, dependencies: [new(contractA, ServiceDependencyKind.Hard)]);
        Assert.True(scenario.Supervisor.Register(a).IsSuccess);

        var rejected = scenario.Supervisor.Register(b);

        Assert.Equal(KernelError.InvalidManifest, rejected.Error);
        Assert.Equal(KernelError.ProcessNotFound, scenario.Kernel.Processes.Resolve(new(new(205), 1)).Error);
        Assert.Equal(KernelError.ComponentNotFound, scenario.Supervisor.Query(b.Identity).Error);
    }

    [Fact]
    public void PlannedReplacementUsesFreshProcessCapabilityAndStableServiceLineage()
    {
        var scenario = CreateSupervisor();
        var capability = new CapabilityRequirementV1(ResourceKind.KernelService, "managed-data", CapabilityRights.Read);
        var current = Definition("planned", 206, 2006, Contract("Planned"), capability: capability, issuer: scenario.IssuerDomain);
        Assert.True(scenario.Supervisor.Register(current).IsSuccess);
        var old = scenario.Supervisor.Start(current.Identity).Value!.Snapshot;
        var oldComponent = scenario.Kernel.QueryComponent(current.Identity).Value!;
        var oldCapability = Assert.Single(oldComponent.Capabilities);
        var next = Definition("planned", 206, 2006, Contract("Planned"), generation: 2, capability: capability, issuer: scenario.IssuerDomain);

        var replaced = scenario.Supervisor.Replace(old.Instance!.Value, next);

        Assert.True(replaced.IsSuccess, replaced.Message);
        Assert.True(replaced.Value!.FreshAdmission);
        var fresh = replaced.Value.Snapshot.Instance!.Value;
        Assert.Equal(old.Instance.Value.ServiceId, fresh.ServiceId);
        Assert.Equal(old.Instance.Value.Generation.Value + 1, fresh.Generation.Value);
        Assert.Equal(old.Instance.Value.Process.Generation + 1, fresh.Process.Generation);
        Assert.NotEqual(oldComponent.ManifestDigest, replaced.Value.Snapshot.ManifestDigest);
        Assert.False(scenario.Kernel.ValidateCapability(fresh.Process, oldCapability, CapabilityRights.Read).IsSuccess);
        var freshComponent = scenario.Kernel.QueryComponent(current.Identity).Value!;
        Assert.NotEqual(oldCapability, Assert.Single(freshComponent.Capabilities));
        Assert.NotEqual(oldComponent.ProcessBudget, freshComponent.ProcessBudget);
        Assert.NotEqual(oldComponent.ServiceBudget, freshComponent.ServiceBudget);
    }

    [Fact]
    public void DependencyReplacementRebindsDependentToFreshExactGeneration()
    {
        var clock = new SupervisorTimeProvider();
        var scenario = CreateSupervisor(clock);
        var providerContract = Contract("ReplaceableProvider");
        var provider = Definition("replaceable-provider", 207, 2007, providerContract,
            restart: new(ServiceRestartMode.OnFailure, 3, 10, 100, 1000),
            replacementFactory: generation => Plan("replaceable-provider", 207, 2007, providerContract, generation,
                restart: new(ServiceRestartMode.OnFailure, 3, 10, 100, 1000)));
        var dependent = Definition("rebound-dependent", 208, 2008, Contract("ReboundDependent"),
            dependencies: [new(providerContract, ServiceDependencyKind.Hard)]);
        Assert.True(scenario.Supervisor.Register(dependent).IsSuccess);
        Assert.True(scenario.Supervisor.Register(provider).IsSuccess);
        Assert.True(scenario.Supervisor.StartAll().IsSuccess);
        var oldBinding = Assert.Single(scenario.Supervisor.Query(dependent.Identity).Value!.Dependencies);

        Assert.True(scenario.Supervisor.ReportHealth(oldBinding.Provider, ServiceHealthState.Failed, "crash").IsSuccess);
        Assert.Equal(ServiceLifecycleState.Degraded, scenario.Supervisor.Query(dependent.Identity).Value!.State);
        clock.AdvanceMilliseconds(10);
        Assert.True(scenario.Supervisor.Observe(provider.Identity).IsSuccess);

        var rebound = scenario.Supervisor.Query(dependent.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, rebound.State);
        var newBinding = Assert.Single(rebound.Dependencies);
        Assert.NotEqual(oldBinding.Provider, newBinding.Provider);
        Assert.Equal(oldBinding.Provider.ServiceId, newBinding.Provider.ServiceId);
        Assert.Equal(oldBinding.Provider.Generation.Value + 1, newBinding.Provider.Generation.Value);
        Assert.Equal(oldBinding.Provider.Process.Generation + 1, newBinding.Provider.Process.Generation);
    }

    [Fact]
    public void BoundedRestartPolicyEntersCrashLoop()
    {
        var clock = new SupervisorTimeProvider();
        var scenario = CreateSupervisor(clock);
        var contract = Contract("CrashLoop");
        var restart = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 1, 1, 1, 10_000);
        var definition = Definition("crash-loop", 209, 2009, contract, restart: restart,
            replacementFactory: generation => Plan("crash-loop", 209, 2009, contract, generation, restart: restart));
        Assert.True(scenario.Supervisor.Register(definition).IsSuccess);
        var first = scenario.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        Assert.True(scenario.Supervisor.ReportHealth(first, ServiceHealthState.Failed, "first").IsSuccess);
        clock.AdvanceMilliseconds(1);
        var replacement = scenario.Supervisor.Observe(definition.Identity).Value!.Instance!.Value;

        Assert.True(scenario.Supervisor.ReportHealth(replacement, ServiceHealthState.Failed, "second").IsSuccess);

        var terminal = scenario.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.CrashLoop, terminal.State);
        Assert.Contains("Crash-loop", terminal.BlockingReason, StringComparison.Ordinal);
    }

    [Fact]
    public void SubmittedClosableOperationDrainsBeforeReplacement()
    {
        var clock = new SupervisorTimeProvider();
        var scenario = CreateSupervisor(clock);
        var contract = Contract("Closable");
        var restart = new ServiceRestartPolicyV1(ServiceRestartMode.OnFailure, 2, 1, 10, 1000);
        var definition = Definition("closable", 210, 2010, contract, restart: restart,
            replacementFactory: generation => Plan("closable", 210, 2010, contract, generation, restart: restart));
        Assert.True(scenario.Supervisor.Register(definition).IsSuccess);
        var instance = scenario.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var operation = SubmitOperation(scenario.Kernel, instance.Process);

        Assert.True(scenario.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "crash with submitted work").IsSuccess);
        Assert.Equal(ServiceLifecycleState.Draining, scenario.Supervisor.Query(definition.Identity).Value!.State);
        Assert.True(scenario.Kernel.RecordExternalOperationCompletion(instance.Process, new(operation.Binding, ExternalOperationCompletionDisposition.Cancelled)).IsSuccess);
        var closurePending = scenario.Supervisor.Observe(definition.Identity);
        Assert.True(closurePending.IsSuccess, closurePending.Message);
        Assert.Equal(ServiceLifecycleState.Draining, closurePending.Value!.State);
        Assert.True(scenario.Kernel.ReleaseExternalOperation(instance.Process, operation.Operation, new(true, false)).IsSuccess);
        var observed = scenario.Supervisor.Observe(definition.Identity);
        Assert.True(observed.IsSuccess, observed.Message);
        Assert.Equal(ServiceLifecycleState.RestartBackoff, observed.Value!.State);
        clock.AdvanceMilliseconds(1);
        var restarted = scenario.Supervisor.Observe(definition.Identity);
        Assert.True(restarted.IsSuccess, restarted.Message);
        Assert.Equal(ServiceLifecycleState.Ready, restarted.Value!.State);
        Assert.Equal(instance.Process.Generation + 1, restarted.Value.Instance!.Value.Process.Generation);
    }

    [Fact]
    public void AmbiguousExternalEffectQuarantinesAndBlocksReplacement()
    {
        var scenario = CreateSupervisor();
        var contract = Contract("Ambiguous");
        var definition = Definition("ambiguous", 211, 2011, contract,
            restart: new(ServiceRestartMode.OnFailure, 2, 1, 10, 1000),
            replacementFactory: generation => Plan("ambiguous", 211, 2011, contract, generation,
                restart: new(ServiceRestartMode.OnFailure, 2, 1, 10, 1000)));
        Assert.True(scenario.Supervisor.Register(definition).IsSuccess);
        var instance = scenario.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var operation = SubmitOperation(scenario.Kernel, instance.Process);
        Assert.True(scenario.Kernel.RecordExternalOperationProviderLoss(instance.Process, operation.Operation).IsSuccess);

        var failure = scenario.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "provider response lost");

        Assert.True(failure.IsSuccess, failure.Message);
        var quarantined = scenario.Supervisor.Query(definition.Identity).Value!;
        Assert.Equal(ServiceLifecycleState.Quarantined, quarantined.State);
        Assert.Equal(ServiceHealthState.Quarantined, quarantined.Health!.State);
        var next = new ManagedServiceDefinition(Plan("ambiguous", 211, 2011, contract, 2), "ambiguous");
        var replacement = scenario.Supervisor.Replace(instance, next);
        Assert.False(replacement.IsSuccess);
        Assert.Equal(KernelError.ReplacementBlocked, replacement.Error);
        Assert.Equal(instance, scenario.Supervisor.Query(definition.Identity).Value!.Instance);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var observed = scenario.Supervisor.Observe(definition.Identity);
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
        var clock = new SupervisorTimeProvider();
        var scenario = CreateSupervisor(clock);
        var contract = Contract("DeadlineAmbiguous");
        var definition = Definition("deadline-ambiguous", 215, 2015, contract);
        Assert.True(scenario.Supervisor.Register(definition).IsSuccess);
        var instance = scenario.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var operation = SubmitOperation(scenario.Kernel, instance.Process);
        var scope = scenario.Kernel.CreateCancellationScope(instance.Process, new(1)).Value!;
        clock.AdvanceMilliseconds(1);

        var drained = scenario.Supervisor.Drain(
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
        var scenario = CreateSupervisor();
        var definition = Definition("stale", 212, 2012, Contract("Stale"));
        Assert.True(scenario.Supervisor.Register(definition).IsSuccess);
        var instance = scenario.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var stale = instance with { Generation = new(instance.Generation.Value + 1) };

        Assert.Equal(KernelError.StaleGeneration, scenario.Supervisor.ReportHealth(stale, ServiceHealthState.Ready, "forged").Error);
        Assert.True(scenario.Kernel.RevokeCapability(scenario.ControlCapability).IsSuccess);
        Assert.Equal(KernelError.SupervisorDenied, scenario.Supervisor.Drain(instance).Error);
        Assert.Equal(ComponentLifecycleState.Running, scenario.Kernel.QueryComponent(definition.Identity).Value!.State);
    }

    [Fact]
    public void OrdinaryKernelServiceCapabilityCannotCreateSupervisor()
    {
        var kernel = new RuntimeKernel();
        var (_, principal) = TestFixtures.Create(kernel, 901, 9001);
        Assert.True(kernel.AdmitProcess(principal).IsSuccess);
        var ordinary = kernel.MintCapability(new DomainId(9001), principal, ResourceKind.KernelService,
            "ordinary-service", CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;

        var denied = kernel.CreateServiceSupervisor(principal, ordinary);

        Assert.Equal(KernelError.SupervisorDenied, denied.Error);
    }

    [Fact]
    public void HealthAndSupervisorDtosContainNoRootOrProviderAuthority()
    {
        var propertyTypes = typeof(ServiceHealthSnapshot).GetProperties().Select(property => property.PropertyType)
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
        var kernel = new RuntimeKernel(null, kernelClock ?? clock);
        var (_, principal) = TestFixtures.Create(kernel, 900, 9000, identity: "supervisor-control");
        Assert.True(kernel.AdmitProcess(principal).IsSuccess);
        var capability = kernel.MintCapability(new DomainId(9000), principal, ResourceKind.KernelService,
            CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;
        var supervisor = kernel.CreateServiceSupervisor(principal, capability, clock).Value!;
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
        var image = new byte[] { (byte)(processId & 0xff), (byte)generation };
        var provided = new ProvidedServiceManifestV1(name, contract);
        var capabilities = capability is { } requirement ? new[] { requirement } : [];
        var process = TestFixtures.Manifest(processId, domainId, generation, $"{name}-entry", capabilities);
        var manifest = new ServiceManifestV1(componentIdentity ?? new(name), new($"1.{generation}"), Digest(image), process, [provided],
            dependencies: dependencies,
            compatibility: compatibility,
            restartPolicy: restart ?? ServiceRestartPolicyV1.Never);
        var grants = capability is { } grant
            ? new[] { new ComponentCapabilityGrant(issuer ?? throw new ArgumentNullException(nameof(issuer)), grant) }
            : [];
        return new(manifest, image, grants, [new(provided, Protocol(contract))]);
    }

    private static SubmittedOperation SubmitOperation(RuntimeKernel kernel, ProcessHandle process)
    {
        var input = kernel.AllocateBuffer<byte>(process, 8).Value!;
        var output = kernel.AllocateBuffer<byte>(process, 8).Value!;
        var prepared = kernel.PrepareExternalOperation(process,
        [
            new(input.Handle, RegionUseMode.ReadOnly, new(0, 8)),
            new(output.Handle, RegionUseMode.StagedOutput, new(0, 8)),
        ], ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
        var dependencies = new OperationDependencySnapshot(7, 11);
        Assert.True(kernel.AdmitExternalOperation(process, prepared.Operation, dependencies).IsSuccess);
        var binding = kernel.RecordExternalOperationSubmission(process, prepared.Operation, dependencies).Value!;
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
