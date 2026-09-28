using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class ServiceDiscoverySessionTests
{
    [Fact]
    public async Task DiscoveredSessionInvokesAndClosesWithoutRawChannelOrProviderHandleAtCaller()
    {
        var (kernel, caller, provider, descriptor, capability) = CreateFixture(withResponses: true);
        var session = kernel.OpenSession(caller, Assert.Single(kernel.ResolveByContract(descriptor.Contract).Value!), [capability]);
        Assert.True(session.IsSuccess, session.Message);

        var transport = new RuntimeSipClientTransport(kernel, caller, session.Value);
        var invocation = transport.InvokeAsync(1).AsTask();
        var request = kernel.ReceiveSession(provider, session.Value);
        Assert.True(request.IsSuccess, request.Message);
        var publication = kernel.PublishSessionResponse(provider, session.Value, request.Value!.Sequence, 42);
        Assert.True(publication.IsSuccess, publication.Message);
        var response = await invocation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(42, response.Payload);
        Assert.True(kernel.CloseSession(caller, session.Value).IsSuccess);
    }

    [Fact]
    public void DiscoveryIsMetadataOnlyAndSessionIsOwnerAndGenerationBound()
    {
        var (kernel, caller, provider, descriptor, capability) = CreateFixture();
        var found = kernel.ResolveByContract(descriptor.Contract);
        Assert.True(found.IsSuccess);
        Assert.Equal(descriptor, Assert.Single(found.Value!));
        Assert.Equal(descriptor, kernel.ResolveByServiceName("compute").Value);

        var opened = kernel.OpenSession(caller, descriptor, [capability]);
        Assert.True(opened.IsSuccess, opened.Message);

        var foreign = kernel.OpenSession(new ProcessHandle(new ProcessId(999), 1), descriptor, [capability]);
        Assert.False(foreign.IsSuccess);
        Assert.Equal(KernelError.ProcessNotFound, foreign.Error);

        var wrongOwner = kernel.CloseSession(provider, opened.Value);
        Assert.False(wrongOwner.IsSuccess);
        Assert.Equal(KernelError.WrongSessionOwner, wrongOwner.Error);
    }

    [Fact]
    public void MissingCapabilityAndStaleServiceGenerationFailBeforeChannelAdmission()
    {
        var (kernel, caller, _, descriptor, _) = CreateFixture();
        var denied = kernel.OpenSession(caller, descriptor);
        Assert.False(denied.IsSuccess);
        Assert.Equal(KernelError.MissingCapability, denied.Error);

        var stale = kernel.OpenSession(caller, descriptor with { Generation = new ServiceGeneration(2) });
        Assert.False(stale.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);

        var wrongContract = kernel.OpenSession(caller, descriptor with { Contract = descriptor.Contract with { Version = "2" } });
        Assert.False(wrongContract.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, wrongContract.Error);
    }

    [Fact]
    public void RevokeCloseDrainAndDoubleCloseAreFailClosed()
    {
        var (kernel, caller, _, descriptor, capability) = CreateFixture();
        var session = kernel.OpenSession(caller, descriptor, [capability]);
        Assert.True(session.IsSuccess, session.Message);

        Assert.True(kernel.SetServiceAvailability(descriptor.Service, ServiceAvailability.Draining).IsSuccess);
        var rejected = kernel.OpenSession(caller, descriptor, [capability]);
        Assert.False(rejected.IsSuccess);
        Assert.Equal(KernelError.ServiceUnavailable, rejected.Error);

        Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        var transport = new RuntimeSipClientTransport(kernel, caller, session.Value);
        var invocation = Assert.Throws<InvalidOperationException>(() => transport.Invoke(1));
        Assert.Contains(nameof(KernelError.CapabilityRevoked), invocation.Message, StringComparison.Ordinal);
        Assert.True(kernel.CloseSession(caller, session.Value).IsSuccess);
    }

    [Fact]
    public void CloseIsIdempotenceSensitiveAndStaleSessionCannotReplay()
    {
        var (kernel, caller, _, descriptor, capability) = CreateFixture();
        var session = kernel.OpenSession(caller, descriptor, [capability]);
        Assert.True(session.IsSuccess, session.Message);
        Assert.True(kernel.CloseSession(caller, session.Value).IsSuccess);

        var replay = kernel.CloseSession(caller, session.Value);
        Assert.False(replay.IsSuccess);
        Assert.Equal(KernelError.SessionClosed, replay.Error);
        var stale = kernel.CloseSession(caller, session.Value with { Generation = new EndpointSessionGeneration(2) });
        Assert.False(stale.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);
    }

    [Fact]
    public async Task ConcurrentCloseHasOneWinnerAndNeverReopensAuthority()
    {
        var (kernel, caller, _, descriptor, capability) = CreateFixture();
        var session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        var closes = await Task.WhenAll(
            Task.Run(() => kernel.CloseSession(caller, session)),
            Task.Run(() => kernel.CloseSession(caller, session)));
        Assert.Single(closes, x => x.IsSuccess);
        Assert.Single(closes, x => x.Error == KernelError.SessionClosed);
    }

    [Fact]
    public async Task ConcurrentOpenAndProviderTeardownLeaveNoActiveSessionOrOpenChannel()
    {
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var (kernel, caller, provider, descriptor, capability) = CreateFixture();
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var opening = Task.Run(async () =>
            {
                await start.Task;
                return kernel.OpenSession(caller, descriptor, [capability]);
            });
            var teardown = Task.Run(async () =>
            {
                await start.Task;
                return kernel.TerminateProcess(provider);
            });
            start.SetResult(true);
            await Task.WhenAll(opening, teardown).WaitAsync(TimeSpan.FromSeconds(5));

            var closed = await teardown;
            Assert.True(closed.IsSuccess, closed.Message);
            Assert.DoesNotContain(kernel.EndpointSessions.SnapshotForProcess(caller),
                item => item.State == EndpointSessionState.Active);
            Assert.Equal(0, kernel.Channels.InspectionSummary(caller).Channels);
            var opened = await opening;
            if (opened.IsSuccess)
            {
                var session = kernel.EndpointSessions.Resolve(opened.Value, caller);
                Assert.Equal(KernelError.SessionClosed, session.Error);
            }
        }
    }

    [Fact]
    public void ProviderDeathClosesSessionsAndRemovesServiceFromDiscovery()
    {
        var (kernel, caller, provider, descriptor, capability) = CreateFixture();
        var session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        Assert.True(kernel.TerminateProcess(provider).IsSuccess);
        Assert.Empty(kernel.ResolveByContract(descriptor.Contract).Value!);
        Assert.Equal(KernelError.SessionClosed, kernel.CloseSession(caller, session).Error);
    }

    [Fact]
    public async Task ConcurrentServiceRegistrationAndProviderTeardownLeaveNoDiscoverableDescriptor()
    {
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var kernel = new RuntimeKernel();
            var provider = TestFixtures.Create(kernel, 5, 50).Handle;
            Assert.True(kernel.AdmitProcess(provider).IsSuccess);
            var contract = new ServiceContractIdentity("RaceService", "1", "race-digest");
            var protocol = new ProtocolDefinitionV1("RaceService", "race-digest", "Idle", null,
                [new ProtocolMessageDescriptorV1(1, "Run")], [new ProtocolTransitionV1(1, "Idle", "Idle")]);
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = Task.Run(async () =>
            {
                await start.Task;
                return kernel.RegisterService(provider, "race-service", contract, protocol);
            });
            var teardown = Task.Run(async () =>
            {
                await start.Task;
                return kernel.TerminateProcess(provider);
            });
            start.SetResult(true);
            await Task.WhenAll(registration, teardown).WaitAsync(TimeSpan.FromSeconds(5));

            var closed = await teardown;
            Assert.True(closed.IsSuccess, closed.Message);
            Assert.Empty(kernel.ResolveByContract(contract).Value!);
        }
    }

    [Fact]
    public void ExpiredAndStaleSessionFailBeforeProtocolMutation()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        var (kernel, caller, provider, descriptor, capability) = CreateFixture(clock);
        var session = kernel.OpenSession(caller, descriptor, [capability], lifetime: TimeSpan.FromSeconds(1)).Value;
        clock.Advance(TimeSpan.FromSeconds(2));
        var transport = new RuntimeSipClientTransport(kernel, caller, session);
        var failure = Assert.Throws<InvalidOperationException>(() => transport.Invoke(1));
        Assert.Contains(nameof(KernelError.SessionTimedOut), failure.Message, StringComparison.Ordinal);
        Assert.Equal(KernelError.SessionClosed, kernel.ReceiveSession(provider, session).Error);
    }

    [Fact]
    public void ExpirationWithInlineEffectPinDefersChannelCloseUntilSettlement()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        var (kernel, caller, provider, descriptor, capability) = CreateFixture(clock);
        var session = kernel.OpenSession(caller, descriptor, [capability], lifetime: TimeSpan.FromSeconds(1)).Value;
        var endpoint = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        var begun = kernel.BeginInlineSessionInvocation(caller, provider, session, 1, null);
        Assert.True(begun.IsSuccess, begun.Message);

        clock.Advance(TimeSpan.FromSeconds(2));
        var expired = kernel.ResolveSession(caller, session);
        Assert.Equal(KernelError.SessionTimedOut, expired.Error);
        Assert.Equal(EndpointSessionState.Draining, kernel.EndpointSessions.SnapshotForProcess(caller).Single().State);
        Assert.True(kernel.Channels.GetEndpoint(endpoint).IsSuccess);
        Assert.False(kernel.RevalidateInlineSessionInvocation(begun.Value!).IsSuccess);

        Assert.True(kernel.SettleInlineSessionInvocation(provider, begun.Value!, succeeded: false).IsSuccess);
        Assert.Equal(EndpointSessionState.Closed, kernel.EndpointSessions.SnapshotForProcess(caller).Single().State);
        Assert.Equal(KernelError.StaleGeneration, kernel.Channels.GetEndpoint(endpoint).Error);
    }

    [Fact]
    public void ExplicitCloseAndExpirationHaveTheSameObservedOwnerStateProjection()
    {
        var (referenceKernel, referenceCaller, referenceService, referenceDescriptor, referenceCapability) = CreateFixture();
        var referenceSession = referenceKernel.OpenSession(referenceCaller, referenceDescriptor,
            [referenceCapability]).Value;
        var referenceChannel = referenceKernel.EndpointSessions.Resolve(
            referenceSession, referenceCaller).Value!.Channel;
        var referencePin = referenceKernel.BeginInlineSessionInvocation(
            referenceCaller, referenceService, referenceSession, 1, null).Value!;
        Assert.Equal(KernelError.SessionDraining,
            referenceKernel.CloseSession(referenceCaller, referenceSession).Error);
        Assert.False(referenceKernel.Channels.SnapshotClosure(referenceChannel)!.Value.Closed);
        Assert.True(referenceKernel.SettleInlineSessionInvocation(
            referenceService, referencePin, succeeded: true).IsSuccess);
        var referenceClosure = referenceKernel.Channels.SnapshotClosure(referenceChannel)!.Value;
        Assert.True(referenceClosure.Closed);
        Assert.Equal(referenceChannel.Generation + 1, referenceClosure.CurrentGeneration);
        Assert.False(referenceClosure.AuthorizesReclaim);
        var reference = referenceKernel.EndpointSessions.SnapshotLifecycleTrace(referenceSession);

        var clock = new TestTimeProvider(new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        var (candidateKernel, candidateCaller, candidateService, candidateDescriptor, candidateCapability) =
            CreateFixture(clock);
        var candidateSession = candidateKernel.OpenSession(candidateCaller, candidateDescriptor,
            [candidateCapability], lifetime: TimeSpan.FromSeconds(1)).Value;
        var candidateChannel = candidateKernel.EndpointSessions.Resolve(
            candidateSession, candidateCaller).Value!.Channel;
        var candidatePin = candidateKernel.BeginInlineSessionInvocation(
            candidateCaller, candidateService, candidateSession, 1, null).Value!;
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(KernelError.SessionTimedOut,
            candidateKernel.ResolveSession(candidateCaller, candidateSession).Error);
        Assert.False(candidateKernel.Channels.SnapshotClosure(candidateChannel)!.Value.Closed);
        Assert.True(candidateKernel.SettleInlineSessionInvocation(
            candidateService, candidatePin, succeeded: true).IsSuccess);
        Assert.True(candidateKernel.Channels.SnapshotClosure(candidateChannel)!.Value.Closed);
        Assert.Null(candidateKernel.Channels.SnapshotClosure(candidateChannel with
        { Generation = candidateChannel.Generation + 1 }));
        Assert.Null(candidateKernel.Channels.SnapshotClosure(candidateChannel with
        { EndpointId = new EndpointId(3) }));
        var candidate = candidateKernel.EndpointSessions.SnapshotLifecycleTrace(candidateSession);

        Assert.Equal([EndpointSessionState.Active, EndpointSessionState.Draining,
            EndpointSessionState.Closed], reference.Select(item => item.State));
        Assert.Equal([SessionLifecycleTraceCauseV1.Opened, SessionLifecycleTraceCauseV1.Expired,
            SessionLifecycleTraceCauseV1.PinReleased], candidate.Select(item => item.Cause));
        Assert.True(SessionLifecycleTraceValidatorV1.Validate(reference).IsValid);
        Assert.True(SessionLifecycleTraceValidatorV1.Validate(candidate).IsValid);
        Assert.True(SessionLifecycleTraceDifferentialV1.CompareAllowedStates(reference, candidate,
            new string('a', 64), new string('b', 64)).IsEquivalent);
        Assert.All(reference.Concat(candidate), item => Assert.False(item.AuthorizesClosure));
    }

    [Fact]
    public async Task ConcurrentCloseAndProcessTeardownRecordOneDrainingTransition()
    {
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var (kernel, caller, service, descriptor, capability) = CreateFixture();
            var session = kernel.OpenSession(caller, descriptor, [capability]).Value;
            var channel = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
            var pin = kernel.BeginInlineSessionInvocation(caller, service, session, 1, null).Value!;
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closing = Task.Run(async () =>
            {
                await start.Task;
                return kernel.CloseSession(caller, session);
            });
            var teardown = Task.Run(async () =>
            {
                await start.Task;
                return kernel.TerminateProcess(service);
            });
            start.SetResult(true);
            await Task.WhenAll(closing, teardown).WaitAsync(TimeSpan.FromSeconds(10));
            var closeResult = await closing;
            var teardownResult = await teardown;
            Assert.Equal(KernelError.PlatformBindingDraining, teardownResult.Error);
            Assert.True(closeResult.IsSuccess || closeResult.Error is KernelError.SessionDraining or
                KernelError.SessionClosed);
            var prefix = kernel.EndpointSessions.SnapshotLifecycleTrace(session);
            Assert.Equal([EndpointSessionState.Active, EndpointSessionState.Draining],
                prefix.Select(item => item.State));
            Assert.True(kernel.Channels.SnapshotClosure(channel)!.Value.Closed);
            Assert.True(SessionLifecycleTraceValidatorV1.Validate(prefix).IsValid);

            _ = kernel.SettleInlineSessionInvocation(service, pin, succeeded: false);
            var final = kernel.EndpointSessions.SnapshotLifecycleTrace(session);
            Assert.Equal([EndpointSessionState.Active, EndpointSessionState.Draining,
                EndpointSessionState.Closed], final.Select(item => item.State));
            Assert.True(SessionLifecycleTraceValidatorV1.Validate(final).IsValid);
        }
    }

    [Fact]
    public void DiscoveryDescriptorContainsNoInvocationAuthorityTypes()
    {
        var propertyTypes = typeof(ServiceEndpointDescriptor).GetProperties().Select(x => x.PropertyType).ToArray();
        Assert.DoesNotContain(typeof(ProcessHandle), propertyTypes);
        Assert.DoesNotContain(typeof(ChannelEndpointHandle), propertyTypes);
        Assert.DoesNotContain(typeof(CapabilityId), propertyTypes);
        Assert.NotEqual(typeof(ServiceId), typeof(EndpointSessionId));
    }

    private static (RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Provider, ServiceEndpointDescriptor Descriptor, CapabilityId Capability) CreateFixture(TimeProvider? timeProvider = null, bool withResponses = false)
    {
        var kernel = new RuntimeKernel(null, timeProvider);
        var caller = TestFixtures.Create(kernel, 1, 10).Handle;
        var provider = TestFixtures.Create(kernel, 2, 20).Handle;
        Assert.True(kernel.AdmitProcess(caller).IsSuccess);
        Assert.True(kernel.AdmitProcess(provider).IsSuccess);
        var capability = kernel.MintCapability(new DomainId(10), caller, ResourceKind.KernelService, "compute", CapabilityRights.Read).Value!.CapabilityId;
        var protocol = new ProtocolDefinitionV1("Compute", "digest", "Idle", ["Done"], [new ProtocolMessageDescriptorV1(1, "Run")], [new ProtocolTransitionV1(1, "Idle", "Done")]);
        var contract = new ServiceContractIdentity("Compute", "1", "digest");
        var responses = withResponses
            ? new ResponseProtocolDefinitionV1("Compute", "response-digest", [new ResponseMessageDescriptorV1(1, "Run", new ResponsePayloadDescriptorV1(ResponsePayloadKind.Primitive, typeof(int).FullName))])
            : null;
        var descriptor = kernel.RegisterService(provider, "compute", contract, protocol, responses, [new CapabilityRequirementV1(ResourceKind.KernelService, "compute", CapabilityRights.Read)]).Value!;
        return (kernel, caller, provider, descriptor, capability);
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
