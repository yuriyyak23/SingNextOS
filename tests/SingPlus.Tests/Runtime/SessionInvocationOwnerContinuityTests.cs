using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class SessionInvocationOwnerContinuityTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalServiceNameCannotBeReplacedByAbsentOrForeignRouting(bool foreignRoute)
    {
        var original = Create(deliver: false);
        var owner = Assert.Single(original.Kernel.Services.Snapshot());
        var other = original.Kernel.RegisterService(original.Service, "other-original-name",
            owner.Descriptor.Contract, owner.Protocol, owner.ResponseProtocol).Value!;
        Assert.True(original.Kernel.TerminateProcess(original.Service).IsSuccess);
        var names = (IDictionary)typeof(ServiceRegistry).GetField("_names", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(original.Kernel.Services)!;
        var name = owner.Descriptor.Service.Name;
        var descriptor = owner.Descriptor;
        var otherOwner = original.Kernel.Services.Snapshot().Single(record => record.Descriptor.Service.Id == other.Service.Id);
        var otherDescriptor = otherOwner.Descriptor;
        if (foreignRoute) names[name] = other.Service.Id;
        else names.Remove(name);
        var count = original.Kernel.Services.Snapshot().Length;
        var result = original.Kernel.RegisterService(original.Caller, name, descriptor.Contract, owner.Protocol, owner.ResponseProtocol);
        Assert.False(result.IsSuccess);
        Assert.Equal(count, original.Kernel.Services.Snapshot().Length);
        Assert.Equal(descriptor, owner.Descriptor);
        Assert.Equal(otherDescriptor, otherOwner.Descriptor);
        Assert.Empty(original.Kernel.Services.ReplacementLineageSnapshot());
        names[name] = descriptor.Service.Id;
        var replacement = original.Kernel.RegisterService(original.Caller, name, descriptor.Contract, owner.Protocol, owner.ResponseProtocol);
        Assert.True(replacement.IsSuccess);
        Assert.Equal(descriptor.Service, replacement.Value!.Service);
        Assert.Equal(original.Caller, owner.Provider);
        Assert.Single(original.Kernel.Services.ReplacementLineageSnapshot());
        Assert.Equal(otherDescriptor, otherOwner.Descriptor);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void OriginalServiceRouteControlsSessionPublicationAndProviderRetirement(int fault)
    {
        var original = Create(deliver: false);
        var foreign = Create(deliver: false);
        IDictionary ServiceRoutes(RuntimeKernel kernel) => (IDictionary)typeof(ServiceRegistry)
            .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.Services)!;
        var routes = ServiceRoutes(original.Kernel);
        var key = Assert.Single(routes.Keys.Cast<object>());
        var record = (ServiceRegistry.Record)routes[key]!;
        var foreignRecord = (ServiceRegistry.Record)ServiceRoutes(foreign.Kernel)[key]!;
        var descriptor = record.Descriptor;
        var before = Sessions(original.Kernel).SnapshotForProcess(original.Caller).Length;
        if (fault == 0) routes.Remove(key);
        else routes[key] = fault == 1 ? foreignRecord
            : record.GetType().GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(record, null);
        Assert.False(original.Kernel.OpenSession(original.Caller, descriptor).IsSuccess);
        Assert.Equal(before, Sessions(original.Kernel).SnapshotForProcess(original.Caller).Length);
        Assert.True(original.Kernel.TerminateProcess(original.Service).IsSuccess);
        Assert.Equal(ServiceAvailability.Unavailable, record.Descriptor.Availability);
        Assert.Equal(ServiceAvailability.Accepting, foreignRecord.Descriptor.Availability);
        Assert.NotEqual(descriptor.Generation, record.Descriptor.Generation);
    }

    [Fact]
    public void SessionPinCapacityRefusesWithoutMutationAndDisposedPinCannotBorrowAnotherLifetime()
    {
        var original = Create();
        var sessions = Sessions(original.Kernel);
        var record = (EndpointSessionRegistry.Record)SessionRoutes(original.Kernel)[original.Session.SessionId]!;
        var first = sessions.AcquirePin(original.Session, original.Caller, original.Service).Value!;
        using var second = sessions.AcquirePin(original.Session, original.Caller, original.Service).Value!;
        first.Dispose();
        Assert.False(sessions.RevalidatePin(first).IsSuccess);
        Assert.True(sessions.RevalidatePin(second).IsSuccess);
        Assert.Equal(1, record.ActivePins);
        record.ActivePins = int.MaxValue;
        Assert.Equal(KernelError.CapacityExhausted,
            sessions.AcquirePin(original.Session, original.Caller, original.Service).Error);
        Assert.Equal(int.MaxValue, record.ActivePins);
        record.ActivePins = 1;
    }

    [Fact]
    public void ForeignClosedSessionCannotAuthorizeOriginalClosedShortcut()
    {
        var original = Create();
        var foreign = Create();
        Assert.True(foreign.Kernel.CloseSession(foreign.Caller, foreign.Session).IsSuccess);
        var originalRecord = (EndpointSessionRegistry.Record)SessionRoutes(original.Kernel)[original.Session.SessionId]!;
        var foreignRecord = (EndpointSessionRegistry.Record)SessionRoutes(foreign.Kernel)[foreign.Session.SessionId]!;
        SessionRoutes(original.Kernel)[original.Session.SessionId] = foreignRecord;
        Assert.False(Sessions(original.Kernel).Close(original.Session, original.Caller, allowAlreadyClosed: true).IsSuccess);
        Assert.Equal(EndpointSessionState.Active, originalRecord.State);
        Assert.Equal(EndpointSessionState.Closed, foreignRecord.State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void OriginalSessionRouteLossCannotEraseAcceptedEffectBeforeProcessReclaim(int fault)
    {
        var original = Create();
        var foreign = Create();
        var routes = SessionRoutes(original.Kernel);
        var record = (EndpointSessionRegistry.Record)routes[original.Session.SessionId]!;
        var foreignRecord = (EndpointSessionRegistry.Record)SessionRoutes(foreign.Kernel)[foreign.Session.SessionId]!;
        LoseSessionRoute(routes, original.Session.SessionId, record, foreignRecord, fault);
        var result = original.Kernel.TerminateProcess(original.Service);
        Assert.False(result.IsSuccess);
        Assert.Equal(EndpointSessionState.Closed, record.State);
        Assert.Equal(EndpointSessionState.Active, foreignRecord.State);
        Assert.Equal(1, Registry(original.Kernel).UnclosedPossibleEffectCount(original.Session));
        // Teardown may invalidate local use; backing release still requires exact closure.
        Assert.NotEqual(RegionState.Released, Assert.Single(original.Kernel.Regions.Snapshot()).State);
        Assert.True(foreign.Payload.IsValid);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void OriginalSessionPinConsumptionSurvivesRouteLossWithoutConsumingForeignPin(int fault)
    {
        var original = Create();
        var foreign = Create();
        var sessions = Sessions(original.Kernel);
        var routes = SessionRoutes(original.Kernel);
        var record = (EndpointSessionRegistry.Record)routes[original.Session.SessionId]!;
        var foreignRecord = (EndpointSessionRegistry.Record)SessionRoutes(foreign.Kernel)[foreign.Session.SessionId]!;
        var pin = sessions.AcquirePin(original.Session, original.Caller, original.Service).Value!;
        using var foreignPin = Sessions(foreign.Kernel).AcquirePin(foreign.Session, foreign.Caller, foreign.Service).Value!;
        Assert.Equal(KernelError.SessionDraining, sessions.Close(original.Session, original.Caller).Error);
        LoseSessionRoute(routes, original.Session.SessionId, record, foreignRecord, fault);
        pin.Dispose();
        pin.Dispose();
        Assert.Equal(0, record.ActivePins);
        Assert.Equal(EndpointSessionState.Closed, record.State);
        Assert.Equal(1, foreignRecord.ActivePins);
        Assert.Same(record, sessions.ClaimClosedCleanup(original.Session));
        Assert.Null(sessions.ClaimClosedCleanup(original.Session));
        Assert.False(foreignRecord.CloseCleanupClaimed);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ForeignOrMissingSessionRouteCannotAuthorizeOriginalEffectPin(int fault)
    {
        var original = Create();
        var foreign = Create();
        var sessions = Sessions(original.Kernel);
        var routes = SessionRoutes(original.Kernel);
        var record = (EndpointSessionRegistry.Record)routes[original.Session.SessionId]!;
        var foreignRecord = (EndpointSessionRegistry.Record)SessionRoutes(foreign.Kernel)[foreign.Session.SessionId]!;
        using var pin = sessions.AcquirePin(original.Session, original.Caller, original.Service).Value!;
        using var foreignPin = Sessions(foreign.Kernel).AcquirePin(foreign.Session, foreign.Caller, foreign.Service).Value!;
        LoseSessionRoute(routes, original.Session.SessionId, record, foreignRecord, fault);
        Assert.False(sessions.Resolve(original.Session, original.Caller).IsSuccess);
        Assert.False(sessions.ResolveForService(original.Session, original.Service).IsSuccess);
        Assert.False(sessions.AcquirePin(original.Session, original.Caller, original.Service).IsSuccess);
        Assert.False(sessions.RevalidatePin(pin).IsSuccess);
        Assert.Equal(1, record.ActivePins);
        Assert.Equal(1, foreignRecord.ActivePins);
        Assert.Equal(1, sessions.ActivePinCount(original.Session));
    }

    private static EndpointSessionRegistry Sessions(RuntimeKernel kernel) =>
        (EndpointSessionRegistry)typeof(RuntimeKernel).GetProperty("EndpointSessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
    private static IDictionary SessionRoutes(RuntimeKernel kernel) =>
        (IDictionary)typeof(EndpointSessionRegistry).GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Sessions(kernel))!;
    private static void LoseSessionRoute(IDictionary routes, EndpointSessionId key,
        EndpointSessionRegistry.Record original, EndpointSessionRegistry.Record foreign, int fault)
    {
        if (fault == 0) routes.Remove(key);
        else routes[key] = fault == 1 ? foreign
            : original.GetType().GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task OriginalInvocationDeliveryRefusesBeforeQueueAndChargeConsumption(int fault)
    {
        var original = Create(deliver: false, budgetCaller: true);
        var foreign = Create();
        Assert.True(foreign.Kernel.PublishSessionResponse(foreign.Service, foreign.Invocation, foreign.Payload).IsSuccess);
        Assert.True((await foreign.Pending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        ulong MessagesUsed() => Assert.Single(original.Kernel.QueryBudget(original.Budget!.Value).Value!.Usage,
            usage => usage.Dimension == ServiceBudgetDimension.IpcMessages).Used;
        var registry = Registry(original.Kernel);
        var routes = Routes(registry);
        var key = Assert.Single(routes.Keys.Cast<object>());
        var record = routes[key]!;
        var sessions = (EndpointSessionRegistry)typeof(RuntimeKernel).GetProperty("EndpointSessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(original.Kernel)!;
        var endpoint = sessions.ResolveForService(original.Session, original.Service).Value!.ServiceEndpoint;
        var channelRoutes = (IDictionary)typeof(ChannelRegistry).GetField("_channels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(original.Kernel.Channels)!;
        var channel = channelRoutes[endpoint.ChannelId]!;
        var queue = (Queue<ChannelEnvelope>)channel.GetType().GetProperty("Queue")!.GetValue(channel)!;
        var envelope = Assert.Single(queue);
        Assert.Equal(1UL, MessagesUsed());
        if (fault == 0) routes.Remove(key);
        else routes[key] = fault == 1 ? Routes(Registry(foreign.Kernel))[key]
            : record.GetType().GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(record, null);
        Assert.False(original.Kernel.ReceiveSessionRequest(original.Service, original.Session).IsSuccess);
        Assert.Same(envelope, Assert.Single(queue));
        Assert.Equal(1UL, MessagesUsed());
        Assert.False(original.Pending.IsCompleted);
        Assert.False((bool)record.GetType().GetProperty("Delivered")!.GetValue(record)!);
        routes[key] = record;
        var delivered = original.Kernel.ReceiveSessionRequest(original.Service, original.Session);
        Assert.True(delivered.IsSuccess);
        Assert.Equal(original.Invocation, delivered.Value!.Invocation);
        Assert.Empty(queue);
        Assert.Equal(0UL, MessagesUsed());
        Assert.True(original.Kernel.PublishSessionResponse(original.Service, original.Invocation, original.Payload).IsSuccess);
        Assert.True((await original.Pending.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
    }

    [Fact]
    public async Task ExactCompletedSettlementSurvivesRouteLossWithoutRepinningOriginalLifetime()
    {
        var original = Create();
        var registry = Registry(original.Kernel);
        var routes = Routes(registry);
        var key = Assert.Single(routes.Keys.Cast<object>());
        var record = routes[key]!;
        var sessions = (EndpointSessionRegistry)typeof(RuntimeKernel).GetProperty("EndpointSessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(original.Kernel)!;
        var endpoint = sessions.ResolveForService(original.Session, original.Service).Value!.ServiceEndpoint;
        var result = registry.Publish(original.Invocation, original.Service, () =>
        {
            var published = original.Kernel.PublishResponse(original.Service, endpoint, original.Invocation.InvocationId.Value, original.Payload);
            routes.Clear();
            return published;
        });
        Assert.True(result.IsSuccess);
        Assert.Same(result.Value, record.GetType().GetProperty("SettlementOutcome")!.GetValue(record));
        Assert.Null(record.GetType().GetProperty("SettlementInProgress")!.GetValue(record));
        Assert.Equal(0, registry.PendingCount(original.Session));
        Assert.Equal(0, registry.UnclosedPossibleEffectCount(original.Session));
        var delivered = await original.Pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(delivered.IsSuccess);
        Assert.True(Assert.IsType<OwnedBuffer<byte>>(delivered.Value!.Payload).IsValid);
        routes[key] = record;
        Assert.False(original.Kernel.PublishSessionResponse(original.Service, original.Invocation, null).IsSuccess);
        registry.CloseSession(original.Session);
        Assert.Equal(0, registry.UnclosedPossibleEffectCount(original.Session));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void MalformedSettlementOutcomeRetainsOriginalFactAndCannotAuthorizeRepeat(int malformed)
    {
        var registry = new EndpointSessionInvocationRegistry(new CancellationScopeAuthority(TimeProvider.System));
        var caller = new ProcessHandle(new ProcessId(810), 1);
        var service = new ProcessHandle(new ProcessId(811), 1);
        var session = new EndpointSessionHandle(new EndpointSessionId(12), new EndpointSessionGeneration(1));
        var invocation = registry.Register(session, caller, service, 1, 1);
        Assert.True(registry.MarkDelivered(session, service, 1).IsSuccess);
        var record = Routes(registry)[Assert.Single(Routes(registry).Keys.Cast<object>())]!;
        ResponseEnvelope? outcome = malformed == 3 ? null : new(malformed == 0 ? 2UL : 1UL,
            malformed == 1 ? 2U : 1U, malformed == 2 ? ResponsePublicationStatus.Cancelled : ResponsePublicationStatus.Published, null);
        var calls = 0;
        KernelResult<ResponseEnvelope> Action()
        {
            calls++;
            return KernelResult<ResponseEnvelope>.Ok(outcome!);
        }
        Assert.False(registry.Publish(invocation, service, Action).IsSuccess);
        Assert.Same(outcome, record.GetType().GetProperty("SettlementOutcome")!.GetValue(record));
        Assert.Null(record.GetType().GetProperty("TerminalStatus")!.GetValue(record));
        Assert.Null(record.GetType().GetProperty("SettlementInProgress")!.GetValue(record));
        Assert.False(registry.Publish(invocation, service, Action).IsSuccess);
        Assert.False(registry.Cancel(invocation, service, Action).IsSuccess);
        Assert.Equal(1, calls);
        Routes(registry).Clear();
        registry.CloseSession(session);
        Assert.Equal(1, registry.PendingCount(session));
        Assert.Equal(1, registry.UnclosedPossibleEffectCount(session));
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    public async Task OriginalSessionInvocationRouteControlsOwnershipPublicationAndPins(int fault, bool afterReservation)
    {
        var original = Create();
        var foreign = Create();
        Assert.True(foreign.Kernel.PublishSessionResponse(foreign.Service, foreign.Invocation, foreign.Payload).IsSuccess);
        var foreignBuffer = Assert.IsType<OwnedBuffer<byte>>((await foreign.Pending.WaitAsync(TimeSpan.FromSeconds(5))).Value!.Payload);
        var registry = Registry(original.Kernel);
        var routes = Routes(registry);
        var key = Assert.Single(routes.Keys.Cast<object>());
        var record = routes[key]!;
        void LoseRoute()
        {
            if (fault == 0) routes.Remove(key);
            else routes[key] = fault == 1 ? Routes(Registry(foreign.Kernel))[key]
                : record.GetType().GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(record, null);
        }
        if (afterReservation) original.Kernel.SessionInvocationSettlementReservedHook = LoseRoute;
        else LoseRoute();
        Assert.False(original.Kernel.PublishSessionResponse(original.Service, original.Invocation, original.Payload).IsSuccess);
        Assert.True(original.Payload.IsValid);
        Assert.True(original.Kernel.Regions.Validate(original.Payload.Handle, new RegionOwner(new DomainId(931_100), original.Service.Generation)).IsSuccess);
        Assert.False(original.Pending.IsCompleted);
        Assert.Equal(1, registry.PendingCount(original.Session));
        Assert.Equal(1, registry.UnclosedPossibleEffectCount(original.Session));
        Assert.Null(record.GetType().GetProperty("SettlementInProgress")!.GetValue(record));
        Assert.True(foreignBuffer.IsValid);
        routes[key] = record;
        original.Kernel.SessionInvocationSettlementReservedHook = null;
        Assert.True(original.Kernel.PublishSessionResponse(original.Service, original.Invocation, original.Payload).IsSuccess);
        var completed = await original.Pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(completed.IsSuccess);
        Assert.True(Assert.IsType<OwnedBuffer<byte>>(completed.Value!.Payload).IsValid);
        Assert.Equal(0, registry.PendingCount(original.Session));
        Assert.False(original.Kernel.PublishSessionResponse(original.Service, original.Invocation, null).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AmbiguousSettlementKeepsOriginalPinAndDoesNotRepeatAction(bool throws)
    {
        var registry = new EndpointSessionInvocationRegistry(new CancellationScopeAuthority(TimeProvider.System));
        var caller = new ProcessHandle(new ProcessId(810), 1);
        var service = new ProcessHandle(new ProcessId(811), 1);
        var session = new EndpointSessionHandle(new EndpointSessionId(12), new EndpointSessionGeneration(1));
        var invocation = registry.Register(session, caller, service, 1, 1);
        Assert.True(registry.MarkDelivered(session, service, 1).IsSuccess);
        var effects = 0;
        KernelResult<ResponseEnvelope> Action()
        {
            effects++;
            if (throws) throw new InvalidOperationException("unknown original effect");
            return KernelResult<ResponseEnvelope>.Fail(KernelError.PlatformFaulted, "unknown original effect");
        }
        if (throws) Assert.Throws<InvalidOperationException>(() => registry.Publish(invocation, service, Action));
        else Assert.False(registry.Publish(invocation, service, Action).IsSuccess);
        Assert.False(registry.Publish(invocation, service, Action).IsSuccess);
        Assert.Equal(1, effects);
        Assert.Equal(1, registry.UnclosedPossibleEffectCount(session));
        var routes = Routes(registry);
        routes.Clear();
        Assert.Equal(1, registry.PendingCount(session));
        registry.CloseSession(session);
        Assert.Equal(1, registry.UnclosedPossibleEffectCount(session));
    }

    private static EndpointSessionInvocationRegistry Registry(RuntimeKernel kernel) =>
        (EndpointSessionInvocationRegistry)typeof(RuntimeKernel).GetProperty("SessionInvocations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
    private static IDictionary Routes(EndpointSessionInvocationRegistry registry) =>
        (IDictionary)typeof(EndpointSessionInvocationRegistry).GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;

    private static (RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service, EndpointSessionHandle Session,
        EndpointSessionInvocationHandle Invocation, Task<KernelResult<ResponseEnvelope>> Pending, OwnedBuffer<byte> Payload,
        BudgetAccountHandle? Budget) Create(bool deliver = true, bool budgetCaller = false)
    {
        var kernel = new RuntimeKernel();
        ProcessHandle caller;
        BudgetAccountHandle? budget = null;
        if (budgetCaller)
        {
            byte[] image = [1, 2];
            var manifest = new ServiceManifestV1(new("session-delivery-budget"), new("1"),
                Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant(),
                TestFixtures.Manifest(9310, 931_000), budgetRequests:
                [new(ServiceBudgetDimension.IpcMessages, 2), new(ServiceBudgetDimension.IpcBytes, 64),
                 new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]);
            var admitted = kernel.AdmitComponent(new(manifest, image));
            Assert.True(admitted.IsSuccess, admitted.Message);
            caller = admitted.Value!.Process;
            budget = admitted.Value.ProcessBudget;
        }
        else caller = TestFixtures.Create(kernel, 9310, 931_000).Handle;
        var service = TestFixtures.Create(kernel, 9311, 931_100).Handle;
        var protocol = new ProtocolDefinitionV1("OriginalSessionOwner", "original-session-digest", "Ready", null,
            [new(1, "Return", returnsOwnership: true, returnOwnershipPayloadKind: OwnershipPayloadKind.OwnedBuffer)],
            [new(1, "Ready", "Ready")]);
        var responses = new ResponseProtocolDefinitionV1(protocol.ContractName, protocol.ContractDigest,
            [new(1, "Return", new(ResponsePayloadKind.Ownership, typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer))]);
        var descriptor = kernel.RegisterService(service, "original-session-owner", new(protocol.ContractName, "1", protocol.ContractDigest), protocol, responses).Value!;
        var session = kernel.OpenSession(caller, descriptor, capacity: 2).Value;
        var pending = kernel.InvokeSessionAsync(caller, session, 1, payload: null).AsTask();
        var invocation = new EndpointSessionInvocationHandle(session, new(1), new(1));
        if (deliver)
        {
            var received = kernel.ReceiveSessionRequest(service, session).Value!;
            invocation = received.Invocation;
            Assert.True(kernel.AcceptSessionInvocation(service, invocation, allowInFlightCancellation: false).IsSuccess);
        }
        return (kernel, caller, service, session, invocation, pending, kernel.AllocateBuffer<byte>(service, 4).Value!, budget);
    }
}
