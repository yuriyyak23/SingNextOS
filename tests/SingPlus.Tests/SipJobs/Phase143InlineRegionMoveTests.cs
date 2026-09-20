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
    [Fact]
    public void NonOwnershipResponseContractCannotSmuggleARegionTransfer()
    {
        var kernel = new RuntimeKernel();
        var caller = TestFixtures.Create(kernel, 8151, 815_100).Handle;
        var service = TestFixtures.Create(kernel, 8152, 815_200).Handle;
        var protocol = ISipJobClosedValueQualificationServiceProtocol.CreateDefinition();
        var descriptor = kernel.RegisterService(
            service,
            "p14-move-shape-negative",
            new(protocol.ContractName, "1", protocol.ContractDigest),
            protocol,
            ISipJobClosedValueQualificationServiceResponseProtocol.Definition).Value!;
        var session = kernel.OpenSession(caller, descriptor).Value;
        var invocation = kernel.BeginInlineSessionInvocation(
            caller,
            service,
            session,
            ISipJobClosedValueQualificationServiceProtocol.Message_TransformAsync,
            new SipJobClosedInt(1)).Value!;
        var buffer = kernel.AllocateBuffer<byte>(service, 4).Value!;

        var moved = kernel.TransferInlineOwnershipResponse(service, invocation, buffer);

        Assert.Equal(KernelError.UnsupportedPayload, moved.Error);
        Assert.True(buffer.IsValid);
        var serviceProcess = kernel.Processes.Resolve(service).Value!;
        Assert.Equal(new RegionOwner(serviceProcess.DomainId, service.Generation),
            Assert.Single(kernel.Regions.Snapshot()).Owner);
        Assert.True(kernel.SettleInlineSessionInvocation(service, invocation, succeeded: false).IsSuccess);
    }

    [Fact]
    public async Task InlineMovePreservesOrdinaryResponderCallerConsumerGenerationSequence()
    {
        var ordinary = Create();
        var ordinaryResult = await RunOrdinary(ordinary, faultConsumer: false);

        var fused = Create();
        var fusedResult = new MoveBinding(fused).Invoke(new SipJobClosedInt(11), faultConsumer: false);

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
        var ordinary = Create();
        var ordinaryResult = await RunOrdinaryResult(ordinary, faultConsumer: true);
        Assert.Equal(ResponsePublicationStatus.Cancelled, ordinaryResult.Status);

        var fused = Create();
        var fusedResult = new MoveBinding(fused).Invoke(new SipJobClosedInt(13), faultConsumer: true);

        Assert.Equal(KernelError.InvalidTransition, fusedResult.Error);
        Assert.Equal(ordinary.Events, fused.Events);
        var region = Assert.Single(fused.Kernel.Regions.Snapshot());
        var consumerProcess = fused.Kernel.Processes.Resolve(fused.Consumer).Value!;
        Assert.Equal(new RegionOwner(consumerProcess.DomainId, fused.Consumer.Generation), region.Owner);
        Assert.Equal(fused.Events[2].Generation, region.Handle.Generation.Value);
        Assert.False(fused.CallerToken!.IsValid);
        Assert.True(fused.ConsumerToken!.IsValid);
    }

    [Fact]
    public void StaleIntermediateTokenCannotReplaySecondMove()
    {
        var scenario = Create();
        var binding = new MoveBinding(scenario);
        var result = binding.Invoke(new SipJobClosedInt(17), faultConsumer: false);
        Assert.True(result.IsSuccess, result.Message);

        var replay = scenario.Kernel.BeginInlineMoveSessionInvocation(
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
        var scenario = Create();
        var result = new MoveBinding(scenario).Invoke(
            new SipJobClosedInt(19), faultConsumer: false, terminateBeforeSecondMove: true);

        Assert.False(result.IsSuccess);
        Assert.Equal(2, scenario.Events.Count);
        Assert.True(scenario.CallerToken!.IsValid);
        var callerProcess = scenario.Kernel.Processes.Resolve(scenario.Caller).Value!;
        var region = Assert.Single(scenario.Kernel.Regions.Snapshot());
        Assert.Equal(new RegionOwner(callerProcess.DomainId, scenario.Caller.Generation), region.Owner);
        Assert.Equal(scenario.Events[1].Generation, region.Handle.Generation.Value);
    }

    [Fact]
    public void ConsumerTerminationAfterSecondMoveUsesOwnerReclaimAndNeverMovesBackToCaller()
    {
        var scenario = Create();
        var result = new MoveBinding(scenario).Invoke(
            new SipJobClosedInt(23), faultConsumer: false, terminateAfterSecondMove: true);

        Assert.False(result.IsSuccess);
        Assert.False(scenario.CallerToken!.IsValid);
        Assert.False(scenario.ConsumerToken!.IsValid);
        var region = Assert.Single(scenario.Kernel.Regions.Snapshot());
        Assert.Equal(RegionState.Released, region.State);
        Assert.NotEqual(
            new RegionOwner(scenario.Kernel.Processes.Resolve(scenario.Caller).Value!.DomainId, scenario.Caller.Generation),
            region.Owner);
    }

    private static async Task<SipJobClosedInt> RunOrdinary(Scenario scenario, bool faultConsumer)
    {
        var result = await RunOrdinaryResult(scenario, faultConsumer);
        Assert.Equal(ResponsePublicationStatus.Published, result.Status);
        return Assert.IsType<SipJobClosedInt>(result.Payload);
    }

    private static async Task<ResponseEnvelope> RunOrdinaryResult(Scenario scenario, bool faultConsumer)
    {
        scenario.ConsumerHost.Fault = faultConsumer;
        var producerPending = new RuntimeSipClientTransport(
            scenario.Kernel, scenario.Caller, scenario.ProducerSession).InvokeAsync(
                ISipJobRegionMoveProducerProtocol.Message_ProduceAsync,
                new SipJobClosedInt(11)).AsTask();
        var produced = scenario.ProducerHost.ProcessNext();
        Assert.True(produced.IsSuccess, produced.Message);
        var producerResponse = await producerPending.WaitAsync(TimeSpan.FromSeconds(10));
        var callerToken = Assert.IsType<OwnedBuffer<byte>>(producerResponse.Payload);
        scenario.CallerToken = callerToken;
        scenario.Events.Add(new("caller", callerToken.Handle.Generation.Value));

        var consumerPending = new RuntimeSipClientTransport(
            scenario.Kernel, scenario.Caller, scenario.ConsumerSession).InvokeAsync(
                ISipJobRegionMoveConsumerProtocol.Message_ConsumeAsync,
                callerToken).AsTask();
        var consumed = scenario.ConsumerHost.ProcessNext();
        if (faultConsumer) Assert.False(consumed.IsSuccess);
        else Assert.True(consumed.IsSuccess, consumed.Message);
        return await consumerPending.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static Scenario Create()
    {
        var kernel = new RuntimeKernel();
        var caller = TestFixtures.Create(kernel, 8141, 814_100).Handle;
        var producer = TestFixtures.Create(kernel, 8142, 814_200).Handle;
        var consumer = TestFixtures.Create(kernel, 8143, 814_300).Handle;
        var producerProtocol = ISipJobRegionMoveProducerProtocol.CreateDefinition();
        var producerDescriptor = kernel.RegisterService(
            producer,
            "p14-region-move-producer",
            new(producerProtocol.ContractName, "1", producerProtocol.ContractDigest),
            producerProtocol,
            ISipJobRegionMoveProducerResponseProtocol.Definition).Value!;
        var consumerProtocol = ISipJobRegionMoveConsumerProtocol.CreateDefinition();
        var consumerDescriptor = kernel.RegisterService(
            consumer,
            "p14-region-move-consumer",
            new(consumerProtocol.ContractName, "1", consumerProtocol.ContractDigest),
            consumerProtocol,
            ISipJobRegionMoveConsumerResponseProtocol.Definition).Value!;
        var producerSession = kernel.OpenSession(caller, producerDescriptor).Value;
        var consumerSession = kernel.OpenSession(caller, consumerDescriptor).Value;
        var scenario = new Scenario(kernel, caller, producer, consumer, producerSession, consumerSession);
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
            var producerBegin = scenario.Kernel.BeginInlineSessionInvocation(
                scenario.Caller,
                scenario.Producer,
                scenario.ProducerSession,
                ISipJobRegionMoveProducerProtocol.Message_ProduceAsync,
                request);
            if (!producerBegin.IsSuccess)
                return KernelResult<SipJobClosedInt>.Fail(producerBegin.Error, producerBegin.Message!);
            var producerLease = producerBegin.Value!;
            var producerContext = producerLease.Context;
            var generatedProducer = ISipJobRegionMoveProducerGeneratedOperationSentries.InvokeRuntime_ProduceAsync(
                scenario.ProducerHost, in producerContext, request);
            if (!generatedProducer.IsSuccess)
            {
                _ = scenario.Kernel.SettleInlineSessionInvocation(scenario.Producer, producerLease, succeeded: false);
                return KernelResult<SipJobClosedInt>.Fail((KernelError)generatedProducer.ErrorCode, generatedProducer.Message!);
            }

            var callerMove = scenario.Kernel.TransferInlineOwnershipResponse(
                scenario.Producer, producerLease, generatedProducer.Value);
            if (!callerMove.IsSuccess)
                return KernelResult<SipJobClosedInt>.Fail(callerMove.Error, callerMove.Message!);
            scenario.CallerToken = callerMove.Value!;
            scenario.Events.Add(new("caller", callerMove.Value!.Handle.Generation.Value));

            if (terminateBeforeSecondMove)
                Assert.True(scenario.Kernel.TerminateProcess(scenario.Consumer).IsSuccess);

            var consumerBegin = scenario.Kernel.BeginInlineMoveSessionInvocation(
                scenario.Caller,
                scenario.Consumer,
                scenario.ConsumerSession,
                ISipJobRegionMoveConsumerProtocol.Message_ConsumeAsync,
                callerMove.Value);
            if (!consumerBegin.IsSuccess)
                return KernelResult<SipJobClosedInt>.Fail(consumerBegin.Error, consumerBegin.Message!);
            var consumerLease = consumerBegin.Value!;
            scenario.ConsumerToken = consumerLease.Payload;
            if (terminateAfterSecondMove)
                Assert.True(scenario.Kernel.TerminateProcess(scenario.Consumer).IsSuccess);
            var current = scenario.Kernel.RevalidateInlineMoveSessionInvocation(consumerLease);
            if (!current.IsSuccess)
            {
                _ = scenario.Kernel.SettleInlineMoveSessionInvocation(scenario.Consumer, consumerLease, succeeded: false);
                return KernelResult<SipJobClosedInt>.Fail(current.Error, current.Message!);
            }
            var consumerContext = consumerLease.Context;
            var generatedConsumer = ISipJobRegionMoveConsumerGeneratedOperationSentries.InvokeRuntime_ConsumeAsync(
                scenario.ConsumerHost, in consumerContext, consumerLease.Payload);
            var settled = scenario.Kernel.SettleInlineMoveSessionInvocation(
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
                var generated = ISipJobRegionMoveProducerGeneratedOperationSentries.InvokeRuntime_ProduceAsync(
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
            var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Producer, 4).Value!;
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
                var generated = ISipJobRegionMoveConsumerGeneratedOperationSentries.InvokeRuntime_ConsumeAsync(
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
