using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Sdk;

namespace SingPlus.Tests.SipJobs;

[SipContract, InitialState("Ready")]
public interface ISipJobRegionMoveProducer
{
    [Message(1), Transition("Ready", "Done"), ReturnsOwnership]
    ValueTask<OwnedBuffer<byte>> ProduceAsync(SipJobClosedInt request);
}

[SipContract, InitialState("Ready")]
public interface ISipJobRegionMoveConsumer
{
    [Message(1), Transition("Ready", "Done")]
    ValueTask<SipJobClosedInt> ConsumeAsync([Consumes] OwnedBuffer<byte> payload);
}

public sealed class Phase143InlineRegionMoveTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RevokedRequiredSessionCapabilityPreventsQueuedOwnershipAndPairLoan(bool pair)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle caller = TestFixtures.Create(kernel, 8111, 811_100).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 8112, 811_200).Handle;
        RequestPayloadDescriptorV1 request = pair
            ? new(new OwnershipRequestDescriptorV1("borrow", OwnershipPayloadKind.OwnedBuffer, OwnershipRequestDisposition.Borrow),
                new OwnershipRequestDescriptorV1("consume", OwnershipPayloadKind.OwnedBuffer, OwnershipRequestDisposition.Consume))
            : new(RequestPayloadKind.Ownership, "consume", typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer);
        ProtocolDefinitionV1 protocol = new ProtocolDefinitionV1("session-ownership", "1", "Ready", null,
            [new(1, "Move", consumes: ["consume"], borrows: pair ? ["borrow"] : [], requestPayload: request)], [new(1, "Ready", "Ready")]);
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "session-ownership", new(protocol.ContractName, "1", protocol.ContractDigest), protocol,
            requiredCapabilities: [new(ResourceKind.Device, "session:ownership", CapabilityRights.Read)]).Value!;
        CapabilityId capability = kernel.MintCapability(new(811_100), caller, ResourceKind.Device, "session:ownership", CapabilityRights.Read).Value!.CapabilityId;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        ChannelEndpointHandle endpoint = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        ChannelEndpoint before = kernel.Channels.GetEndpoint(endpoint).Value!;
        OwnedBuffer<byte> payload = kernel.AllocateBuffer<byte>(caller, 4).Value!;
        OwnedBuffer<byte>? borrowed = pair ? kernel.AllocateBuffer<byte>(caller, 4).Value! : null;
        RegionHandle original = payload.Handle;
        object gate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel)!;
        Task<KernelResult<ResponseEnvelope>> pending;
        lock (gate)
        {
            pending = Task.Run(async () => pair
                ? await kernel.InvokeSessionOwnershipPairAsync(caller, session, 1, borrowed!, payload)
                : await kernel.InvokeSessionAsync(caller, session, 1, payload));
            Assert.True(SpinWait.SpinUntil(() => kernel.EndpointSessions.ActivePinCount(session) == 1, TimeSpan.FromSeconds(5)));
            Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        }
        KernelResult<ResponseEnvelope> result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(KernelError.MissingCapability, result.Error);
        Assert.True(payload.IsValid);
        Assert.Equal(original, payload.Handle);
        Assert.All(kernel.Regions.Snapshot(), region => Assert.Equal(RegionState.Owned, region.State));
        Assert.Equal(before, kernel.Channels.GetEndpoint(endpoint).Value);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
        Assert.Null(kernel.SnapshotSessionInvocationConsequence(new(session, new(1), new(1))));
    }

    [Fact]
    public void ReservedOwnershipResponseCloseRefusesTransferAndConcurrentSettlement()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle caller = TestFixtures.Create(kernel, 8141, 814_100).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 8142, 814_200).Handle;
        ProtocolDefinitionV1 protocol = ISipJobRegionMoveProducerProtocol.CreateDefinition();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "reserved-response", new(protocol.ContractName, "1", protocol.ContractDigest),
            protocol, ISipJobRegionMoveProducerResponseProtocol.Definition).Value!;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor).Value;
        InlineSipInvocationLease invocation = kernel.BeginInlineSessionInvocation(caller, service, session, 1, new SipJobClosedInt(1)).Value!;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(service, 4).Value!;
        RegionHandle original = buffer.Handle;
        kernel.SessionInvocationSettlementReservedHook = () =>
        {
            Task<KernelResult> worker = Task.Run(() =>
            {
                Assert.Equal(KernelError.ResponseNotPending, kernel.TransferInlineOwnershipResponse(service, invocation, buffer).Error);
                Assert.Equal(KernelError.ResponseNotPending, kernel.SettleInlineSessionInvocation(service, invocation, false).Error);
                Assert.Equal(1, kernel.EndpointSessions.ActivePinCount(session));
                return kernel.CloseSession(caller, session);
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.SessionDraining, worker.Result.Error);
        };
        KernelResult<OwnedBuffer<byte>> response = kernel.TransferInlineOwnershipResponse(service, invocation, buffer);
        Assert.Equal(KernelError.SessionClosed, response.Error);
        Assert.True(buffer.IsValid);
        Assert.Equal(original, buffer.Handle);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        kernel.SessionInvocationSettlementReservedHook = null;
        Assert.True(kernel.SettleInlineSessionInvocation(service, invocation, false).IsSuccess);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
    }

    [Fact]
    public void InlineMoveBudgetClockObservesCommittedLeaseOutsideAdmissionGates()
    {
        ResponseClock clock = new ResponseClock();
        RuntimeKernel kernel = new RuntimeKernel(null, clock);
        byte[] image = [3, 4];
        ServiceManifestV1 manifest = new ServiceManifestV1(new("move-observe"), new("1"),
            Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant(),
            TestFixtures.Manifest(8172, 817_200),
            budgetRequests: [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096), new(ServiceBudgetDimension.TraceTelemetryBufferBytes, 8192)],
            telemetryPolicy: new(ServiceTelemetryVisibility.Self, 8192));
        ProcessHandle service = kernel.AdmitComponent(new(manifest, image)).Value!.Process;
        ProcessHandle caller = TestFixtures.Create(kernel, 8171, 817_100).Handle;
        ProtocolDefinitionV1 protocol = ISipJobRegionMoveConsumerProtocol.CreateDefinition();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "move-observe", new(protocol.ContractName, "1", protocol.ContractDigest), protocol).Value!;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor).Value;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(caller, 4).Value!;
        Assert.True(kernel.StartTraceSession(service, 8).IsSuccess);
        bool observed = false;
        clock.Next = () =>
        {
            Task<KernelResult> worker = Task.Run(() =>
            {
                Assert.True(kernel.MintCapability(new(817_200), service, ResourceKind.Device, "observation", CapabilityRights.Read).IsSuccess);
                return kernel.CloseSession(caller, session);
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.SessionDraining, worker.Result.Error);
            observed = true;
        };
        KernelResult<InlineMoveSipInvocationLease<byte>> begun = kernel.BeginInlineMoveSessionInvocation(caller, service, session, 1, buffer);
        Assert.True(observed);
        Assert.True(begun.IsSuccess);
        Assert.False(buffer.IsValid);
        Assert.True(begun.Value!.Payload.IsValid);
        Assert.Equal(new RegionOwner(new(817_200), service.Generation), Assert.Single(kernel.Regions.Snapshot()).Owner);
        Assert.True(kernel.SettleInlineMoveSessionInvocation(service, begun.Value, true).IsSuccess);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
    }

    [Fact]
    public void ResponseBudgetClockRunsAfterCommittedOwnershipAndSettlementOutsideKernelGates()
    {
        ResponseClock clock = new ResponseClock();
        RuntimeKernel kernel = new RuntimeKernel(null, clock);
        byte[] image = [1, 2];
        ServiceManifestV1 manifest = new ServiceManifestV1(new("response-pin"), new("1"),
            Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant(),
            TestFixtures.Manifest(8181, 818_100),
            budgetRequests: [new(ServiceBudgetDimension.OwnedMemoryBytes, 4096), new(ServiceBudgetDimension.TraceTelemetryBufferBytes, 8192)],
            telemetryPolicy: new(ServiceTelemetryVisibility.Self, 8192));
        ProcessHandle caller = kernel.AdmitComponent(new(manifest, image)).Value!.Process;
        ProcessHandle service = TestFixtures.Create(kernel, 8182, 818_200).Handle;
        ProtocolDefinitionV1 protocol = ISipJobRegionMoveProducerProtocol.CreateDefinition();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "response-pin", new(protocol.ContractName, "1", protocol.ContractDigest),
            protocol, ISipJobRegionMoveProducerResponseProtocol.Definition).Value!;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor).Value;
        InlineSipInvocationLease invocation = kernel.BeginInlineSessionInvocation(caller, service, session, 1, new SipJobClosedInt(1)).Value!;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(service, 4).Value!;
        RegionHandle original = buffer.Handle;
        Assert.True(kernel.StartTraceSession(caller, 8).IsSuccess);
        bool observed = false;
        clock.Next = () =>
        {
            Task<KernelResult> worker = Task.Run(() => kernel.CloseSession(caller, session));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(worker.Result.IsSuccess);
            observed = true;
        };
        KernelResult<OwnedBuffer<byte>> response = kernel.TransferInlineOwnershipResponse(service, invocation, buffer);
        Assert.True(observed);
        Assert.True(response.IsSuccess);
        Assert.False(buffer.IsValid);
        Assert.True(response.Value!.IsValid);
        Assert.NotEqual(original, response.Value.Handle);
        Assert.Equal(new RegionOwner(new(818_100), caller.Generation), Assert.Single(kernel.Regions.Snapshot()).Owner);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
    }

    private sealed class ResponseClock : TimeProvider
    {
        internal Action? Next;
        public override long GetTimestamp() { Action? callback = Next; Next = null; callback?.Invoke(); return base.GetTimestamp(); }
    }

    [Fact]
    public void RevokedSessionPermissionPreventsOwnershipMoveAndPublication()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle caller = TestFixtures.Create(kernel, 8191, 819_100).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 8192, 819_200).Handle;
        ProtocolDefinitionV1 protocol = ISipJobRegionMoveConsumerProtocol.CreateDefinition();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "move-permission",
            new(protocol.ContractName, "1", protocol.ContractDigest), protocol,
            requiredCapabilities: [new(ResourceKind.Device, "move:required", CapabilityRights.Read)]).Value!;
        CapabilityId capability = kernel.MintCapability(new(819_100), caller, ResourceKind.Device, "move:required", CapabilityRights.Read).Value!.CapabilityId;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        ChannelEndpointHandle endpoint = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        ChannelEndpoint before = kernel.Channels.GetEndpoint(endpoint).Value!;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(caller, 4).Value!;
        RegionHandle original = buffer.Handle;
        Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        KernelResult<InlineMoveSipInvocationLease<byte>> begun = kernel.BeginInlineMoveSessionInvocation(caller, service, session, 1, buffer);
        Assert.Equal(KernelError.MissingCapability, begun.Error);
        Assert.True(buffer.IsValid);
        Assert.Equal(original, buffer.Handle);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(new RegionOwner(new(819_100), caller.Generation), Assert.Single(kernel.Regions.Snapshot()).Owner);
        Assert.Equal(before, kernel.Channels.GetEndpoint(endpoint).Value);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
        Assert.Null(kernel.SnapshotSessionInvocationConsequence(new(session, new(1), new(1))));
    }

    [Fact]
    public void NonOwnershipResponseContractCannotSmuggleARegionTransfer()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle caller = TestFixtures.Create(kernel, 8151, 815_100).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 8152, 815_200).Handle;
        ProtocolDefinitionV1 protocol = ISipJobClosedValueQualificationServiceProtocol.CreateDefinition();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(
            service,
            "p14-move-shape-negative",
            new(protocol.ContractName, "1", protocol.ContractDigest),
            protocol,
            ISipJobClosedValueQualificationServiceResponseProtocol.Definition).Value!;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor).Value;
        InlineSipInvocationLease invocation = kernel.BeginInlineSessionInvocation(
            caller,
            service,
            session,
            ISipJobClosedValueQualificationServiceProtocol.Message_TransformAsync,
            new SipJobClosedInt(1)).Value!;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(service, 4).Value!;

        KernelResult<OwnedBuffer<byte>> moved = kernel.TransferInlineOwnershipResponse(service, invocation, buffer);

        Assert.Equal(KernelError.UnsupportedPayload, moved.Error);
        Assert.True(buffer.IsValid);
        SingProcess serviceProcess = kernel.Processes.Resolve(service).Value!;
        Assert.Equal(new RegionOwner(serviceProcess.DomainId, service.Generation),
            Assert.Single(kernel.Regions.Snapshot()).Owner);
        Assert.True(kernel.SettleInlineSessionInvocation(service, invocation, succeeded: false).IsSuccess);
    }

    [Fact]
    public async Task InlineMovePreservesOrdinaryResponderCallerConsumerGenerationSequence()
    {
        Scenario ordinary = Create();
        SipJobClosedInt ordinaryResult = await RunOrdinary(ordinary, faultConsumer: false);

        Scenario fused = Create();
        KernelResult<SipJobClosedInt> fusedResult = new MoveBinding(fused).Invoke(new SipJobClosedInt(11), faultConsumer: false);

        Assert.True(fusedResult.IsSuccess, fusedResult.Message);
        Assert.Equal(ordinaryResult, fusedResult.Value);
        Assert.Equal(ordinary.Events, fused.Events);
        Assert.Equal(3, fused.Events.Count);
        Assert.Equal("producer", fused.Events[0].Role);
        Assert.Equal("caller", fused.Events[1].Role);
        Assert.Equal("consumer", fused.Events[2].Role);
        Assert.Equal(fused.Events[0].Generation + 1, fused.Events[1].Generation);
        Assert.Equal(fused.Events[1].Generation + 1, fused.Events[2].Generation);
        Assert.False(fused.ProducerToken!.IsValid);
        Assert.False(fused.CallerToken!.IsValid);
        Assert.True(fused.ConsumerToken!.IsValid);
        Assert.Equal(0, fused.Kernel.Channels.InspectionSummary(fused.Caller).QueuedMessages);
    }

    [Fact]
    public async Task ConsumerFaultAfterSecondMoveHasNoImplicitInverseTransfer()
    {
        Scenario ordinary = Create();
        ResponseEnvelope ordinaryResult = await RunOrdinaryResult(ordinary, faultConsumer: true);
        Assert.Equal(ResponsePublicationStatus.Cancelled, ordinaryResult.Status);

        Scenario fused = Create();
        KernelResult<SipJobClosedInt> fusedResult = new MoveBinding(fused).Invoke(new SipJobClosedInt(13), faultConsumer: true);

        Assert.Equal(KernelError.InvalidTransition, fusedResult.Error);
        Assert.Equal(ordinary.Events, fused.Events);
        RegionDescriptor region = Assert.Single(fused.Kernel.Regions.Snapshot());
        SingProcess consumerProcess = fused.Kernel.Processes.Resolve(fused.Consumer).Value!;
        Assert.Equal(new RegionOwner(consumerProcess.DomainId, fused.Consumer.Generation), region.Owner);
        Assert.Equal(fused.Events[2].Generation, region.Handle.Generation.Value);
        Assert.False(fused.CallerToken!.IsValid);
        Assert.True(fused.ConsumerToken!.IsValid);
    }

    [Fact]
    public void StaleIntermediateTokenCannotReplaySecondMove()
    {
        Scenario scenario = Create();
        MoveBinding binding = new MoveBinding(scenario);
        KernelResult<SipJobClosedInt> result = binding.Invoke(new SipJobClosedInt(17), faultConsumer: false);
        Assert.True(result.IsSuccess, result.Message);

        KernelResult<InlineMoveSipInvocationLease<byte>> replay = scenario.Kernel.BeginInlineMoveSessionInvocation(
            scenario.Caller,
            scenario.Consumer,
            scenario.ConsumerSession,
            ISipJobRegionMoveConsumerProtocol.Message_ConsumeAsync,
            scenario.CallerToken!);

        Assert.Equal(KernelError.InvalidProtocolTransition, replay.Error);
        Assert.False(scenario.CallerToken!.IsValid);
        Assert.True(scenario.ConsumerToken!.IsValid);
    }

    [Fact]
    public void ConsumerRestartBeforeSecondMoveLeavesCommittedIntermediateCallerOwner()
    {
        Scenario scenario = Create();
        KernelResult<SipJobClosedInt> result = new MoveBinding(scenario).Invoke(
            new SipJobClosedInt(19), faultConsumer: false, terminateBeforeSecondMove: true);

        Assert.False(result.IsSuccess);
        Assert.Equal(2, scenario.Events.Count);
        Assert.True(scenario.CallerToken!.IsValid);
        SingProcess callerProcess = scenario.Kernel.Processes.Resolve(scenario.Caller).Value!;
        RegionDescriptor region = Assert.Single(scenario.Kernel.Regions.Snapshot());
        Assert.Equal(new RegionOwner(callerProcess.DomainId, scenario.Caller.Generation), region.Owner);
        Assert.Equal(scenario.Events[1].Generation, region.Handle.Generation.Value);
    }

    [Fact]
    public void ConsumerTerminationAfterSecondMoveUsesOwnerReclaimAndNeverMovesBackToCaller()
    {
        Scenario scenario = Create();
        KernelResult<SipJobClosedInt> result = new MoveBinding(scenario).Invoke(
            new SipJobClosedInt(23), faultConsumer: false, terminateAfterSecondMove: true);

        Assert.False(result.IsSuccess);
        Assert.False(scenario.CallerToken!.IsValid);
        Assert.False(scenario.ConsumerToken!.IsValid);
        Assert.False(scenario.Kernel.ObserveProcessTeardown(scenario.Consumer).Value!.IsComplete);
        RegionDescriptor region = Assert.Single(scenario.Kernel.Regions.Snapshot());
        Assert.NotEqual(RegionState.Released, region.State);
        Assert.NotEqual(
            new RegionOwner(scenario.Kernel.Processes.Resolve(scenario.Caller).Value!.DomainId, scenario.Caller.Generation),
            region.Owner);
    }

    private static async Task<SipJobClosedInt> RunOrdinary(Scenario scenario, bool faultConsumer)
    {
        ResponseEnvelope result = await RunOrdinaryResult(scenario, faultConsumer);
        Assert.Equal(ResponsePublicationStatus.Published, result.Status);
        return Assert.IsType<SipJobClosedInt>(result.Payload);
    }

    private static async Task<ResponseEnvelope> RunOrdinaryResult(Scenario scenario, bool faultConsumer)
    {
        scenario.ConsumerHost.Fault = faultConsumer;
        Task<ResponseEnvelope> producerPending = new RuntimeSipClientTransport(
            scenario.Kernel, scenario.Caller, scenario.ProducerSession).InvokeAsync(
                ISipJobRegionMoveProducerProtocol.Message_ProduceAsync,
                new SipJobClosedInt(11)).AsTask();
        KernelResult produced = scenario.ProducerHost.ProcessNext();
        Assert.True(produced.IsSuccess, produced.Message);
        ResponseEnvelope producerResponse = await producerPending.WaitAsync(TimeSpan.FromSeconds(10));
        OwnedBuffer<byte> callerToken = Assert.IsType<OwnedBuffer<byte>>(producerResponse.Payload);
        scenario.CallerToken = callerToken;
        scenario.Events.Add(new("caller", callerToken.Handle.Generation.Value));

        Task<ResponseEnvelope> consumerPending = new RuntimeSipClientTransport(
            scenario.Kernel, scenario.Caller, scenario.ConsumerSession).InvokeAsync(
                ISipJobRegionMoveConsumerProtocol.Message_ConsumeAsync,
                callerToken).AsTask();
        KernelResult consumed = scenario.ConsumerHost.ProcessNext();
        if (faultConsumer) Assert.False(consumed.IsSuccess);
        else Assert.True(consumed.IsSuccess, consumed.Message);
        return await consumerPending.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static Scenario Create()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle caller = TestFixtures.Create(kernel, 8141, 814_100).Handle;
        ProcessHandle producer = TestFixtures.Create(kernel, 8142, 814_200).Handle;
        ProcessHandle consumer = TestFixtures.Create(kernel, 8143, 814_300).Handle;
        ProtocolDefinitionV1 producerProtocol = ISipJobRegionMoveProducerProtocol.CreateDefinition();
        ServiceEndpointDescriptor producerDescriptor = kernel.RegisterService(
            producer,
            "p14-region-move-producer",
            new(producerProtocol.ContractName, "1", producerProtocol.ContractDigest),
            producerProtocol,
            ISipJobRegionMoveProducerResponseProtocol.Definition).Value!;
        ProtocolDefinitionV1 consumerProtocol = ISipJobRegionMoveConsumerProtocol.CreateDefinition();
        ServiceEndpointDescriptor consumerDescriptor = kernel.RegisterService(
            consumer,
            "p14-region-move-consumer",
            new(consumerProtocol.ContractName, "1", consumerProtocol.ContractDigest),
            consumerProtocol,
            ISipJobRegionMoveConsumerResponseProtocol.Definition).Value!;
        EndpointSessionHandle producerSession = kernel.OpenSession(caller, producerDescriptor).Value;
        EndpointSessionHandle consumerSession = kernel.OpenSession(caller, consumerDescriptor).Value;
        Scenario scenario = new Scenario(kernel, caller, producer, consumer, producerSession, consumerSession);
        scenario.ProducerHost = new ProducerHost(scenario);
        scenario.ConsumerHost = new ConsumerHost(scenario);
        return scenario;
    }

    private sealed class MoveBinding(Scenario scenario)
    {
        internal KernelResult<SipJobClosedInt> Invoke(
            SipJobClosedInt request,
            bool faultConsumer,
            bool terminateBeforeSecondMove = false,
            bool terminateAfterSecondMove = false)
        {
            scenario.ConsumerHost.Fault = faultConsumer;
            KernelResult<InlineSipInvocationLease> producerBegin = scenario.Kernel.BeginInlineSessionInvocation(
                scenario.Caller,
                scenario.Producer,
                scenario.ProducerSession,
                ISipJobRegionMoveProducerProtocol.Message_ProduceAsync,
                request);
            if (!producerBegin.IsSuccess)
                return KernelResult<SipJobClosedInt>.Fail(producerBegin.Error, producerBegin.Message!);
            InlineSipInvocationLease producerLease = producerBegin.Value!;
            TrustedSipInvocationContext producerContext = producerLease.Context;
            GeneratedSipSentryResult<OwnedBuffer<byte>> generatedProducer = ISipJobRegionMoveProducerGeneratedOperationSentries.InvokeRuntime_ProduceAsync(
                scenario.ProducerHost, in producerContext, request);
            if (!generatedProducer.IsSuccess)
            {
                _ = scenario.Kernel.SettleInlineSessionInvocation(scenario.Producer, producerLease, succeeded: false);
                return KernelResult<SipJobClosedInt>.Fail((KernelError)generatedProducer.ErrorCode, generatedProducer.Message!);
            }

            KernelResult<OwnedBuffer<byte>> callerMove = scenario.Kernel.TransferInlineOwnershipResponse(
                scenario.Producer, producerLease, generatedProducer.Value);
            if (!callerMove.IsSuccess)
                return KernelResult<SipJobClosedInt>.Fail(callerMove.Error, callerMove.Message!);
            scenario.CallerToken = callerMove.Value!;
            scenario.Events.Add(new("caller", callerMove.Value!.Handle.Generation.Value));

            if (terminateBeforeSecondMove)
                Assert.True(scenario.Kernel.TerminateProcess(scenario.Consumer).IsSuccess);

            KernelResult<InlineMoveSipInvocationLease<byte>> consumerBegin = scenario.Kernel.BeginInlineMoveSessionInvocation(
                scenario.Caller,
                scenario.Consumer,
                scenario.ConsumerSession,
                ISipJobRegionMoveConsumerProtocol.Message_ConsumeAsync,
                callerMove.Value);
            if (!consumerBegin.IsSuccess)
                return KernelResult<SipJobClosedInt>.Fail(consumerBegin.Error, consumerBegin.Message!);
            InlineMoveSipInvocationLease<byte> consumerLease = consumerBegin.Value!;
            scenario.ConsumerToken = consumerLease.Payload;
            if (terminateAfterSecondMove)
                Assert.Equal(KernelError.PlatformBindingDraining,
                    scenario.Kernel.TerminateProcess(scenario.Consumer).Error);
            KernelResult current = scenario.Kernel.RevalidateInlineMoveSessionInvocation(consumerLease);
            if (!current.IsSuccess)
            {
                _ = scenario.Kernel.SettleInlineMoveSessionInvocation(scenario.Consumer, consumerLease, succeeded: false);
                return KernelResult<SipJobClosedInt>.Fail(current.Error, current.Message!);
            }
            TrustedSipInvocationContext consumerContext = consumerLease.Context;
            GeneratedSipSentryResult<SipJobClosedInt> generatedConsumer = ISipJobRegionMoveConsumerGeneratedOperationSentries.InvokeRuntime_ConsumeAsync(
                scenario.ConsumerHost, in consumerContext, consumerLease.Payload);
            KernelResult settled = scenario.Kernel.SettleInlineMoveSessionInvocation(
                scenario.Consumer, consumerLease, generatedConsumer.IsSuccess);
            if (!settled.IsSuccess)
                return KernelResult<SipJobClosedInt>.Fail(settled.Error, settled.Message!);
            return generatedConsumer.IsSuccess
                ? KernelResult<SipJobClosedInt>.Ok(generatedConsumer.Value)
                : KernelResult<SipJobClosedInt>.Fail((KernelError)generatedConsumer.ErrorCode, generatedConsumer.Message!);
        }
    }

    private sealed class ProducerHost(Scenario scenario) :
        IISipJobRegionMoveProducerGeneratedSentryTarget_ProduceAsync
    {
        internal KernelResult ProcessNext() => NativeServiceDispatch.Dispatch(
            scenario.Kernel,
            scenario.Producer,
            scenario.ProducerSession,
            (context, envelope) =>
            {
                if (envelope.Payload is not SipJobClosedInt request)
                    return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "Producer request is invalid.");
                GeneratedSipSentryResult<OwnedBuffer<byte>> generated = ISipJobRegionMoveProducerGeneratedOperationSentries.InvokeRuntime_ProduceAsync(
                    this, in context, request);
                return generated.IsSuccess
                    ? KernelResult<object?>.Ok(generated.Value)
                    : KernelResult<object?>.Fail((KernelError)generated.ErrorCode, generated.Message!);
            });

        GeneratedSipSentryResult<OwnedBuffer<byte>>
            IISipJobRegionMoveProducerGeneratedSentryTarget_ProduceAsync.Sentry_ProduceAsync(
                in TrustedSipInvocationContext context,
                SipJobClosedInt request)
        {
            OwnedBuffer<byte> buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Producer, 4).Value!;
            buffer.Span[0] = checked((byte)request.Value);
            buffer.Span[1] = 3;
            buffer.Span[2] = 5;
            buffer.Span[3] = 7;
            scenario.ProducerToken = buffer;
            scenario.Events.Add(new("producer", buffer.Handle.Generation.Value));
            return GeneratedSipSentryResult<OwnedBuffer<byte>>.Success(buffer);
        }
    }

    private sealed class ConsumerHost(Scenario scenario) :
        IISipJobRegionMoveConsumerGeneratedSentryTarget_ConsumeAsync
    {
        internal bool Fault { get; set; }

        internal KernelResult ProcessNext() => NativeServiceDispatch.Dispatch(
            scenario.Kernel,
            scenario.Consumer,
            scenario.ConsumerSession,
            (context, envelope) =>
            {
                if (envelope.Payload is not OwnedBuffer<byte> payload)
                    return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "Consumer MOVE projection is invalid.");
                GeneratedSipSentryResult<SipJobClosedInt> generated = ISipJobRegionMoveConsumerGeneratedOperationSentries.InvokeRuntime_ConsumeAsync(
                    this, in context, payload);
                return generated.IsSuccess
                    ? KernelResult<object?>.Ok(generated.Value)
                    : KernelResult<object?>.Fail((KernelError)generated.ErrorCode, generated.Message!);
            });

        GeneratedSipSentryResult<SipJobClosedInt>
            IISipJobRegionMoveConsumerGeneratedSentryTarget_ConsumeAsync.Sentry_ConsumeAsync(
                in TrustedSipInvocationContext context,
                OwnedBuffer<byte> payload)
        {
            scenario.ConsumerToken = payload;
            scenario.Events.Add(new("consumer", payload.Handle.Generation.Value));
            if (Fault)
                return GeneratedSipSentryResult<SipJobClosedInt>.Failure(
                    (int)KernelError.InvalidTransition,
                    "Deterministic consumer fault after committed MOVE.");
            return GeneratedSipSentryResult<SipJobClosedInt>.Success(
                new SipJobClosedInt(payload.Span.ToArray().Sum(static value => value)));
        }
    }

    private sealed class Scenario(
        RuntimeKernel kernel,
        ProcessHandle caller,
        ProcessHandle producer,
        ProcessHandle consumer,
        EndpointSessionHandle producerSession,
        EndpointSessionHandle consumerSession)
    {
        internal RuntimeKernel Kernel { get; } = kernel;
        internal ProcessHandle Caller { get; } = caller;
        internal ProcessHandle Producer { get; } = producer;
        internal ProcessHandle Consumer { get; } = consumer;
        internal EndpointSessionHandle ProducerSession { get; } = producerSession;
        internal EndpointSessionHandle ConsumerSession { get; } = consumerSession;
        internal ProducerHost ProducerHost { get; set; } = null!;
        internal ConsumerHost ConsumerHost { get; set; } = null!;
        internal List<MoveEvent> Events { get; } = [];
        internal OwnedBuffer<byte>? ProducerToken { get; set; }
        internal OwnedBuffer<byte>? CallerToken { get; set; }
        internal OwnedBuffer<byte>? ConsumerToken { get; set; }
    }

    private readonly record struct MoveEvent(string Role, ulong Generation);
}
