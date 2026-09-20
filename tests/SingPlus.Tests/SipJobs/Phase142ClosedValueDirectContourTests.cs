using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip.Sdk;
using System.Collections.Immutable;
using System.Reflection;

namespace SingPlus.Tests.SipJobs;

[BoundedPayload(32)]
public readonly record struct SipJobClosedInt(int Value) : IBoundedPayload
{
    public int PayloadSize => sizeof(int);
    public int MaxPayloadSize => 32;
}

[SipContract, InitialState("Ready")]
public interface ISipJobClosedValueQualificationService
{
    [Message(1), Transition("Ready", "Ready")]
    ValueTask<SipJobClosedInt> TransformAsync(SipJobClosedInt request);
}

public sealed class Phase142ClosedValueDirectContourTests
{
    [Fact]
    public async Task TwoStageDirectContourMatchesOrdinarySemanticTraceAndKeepsFinalPublicationMaterialized()
    {
        var ordinary = CreateScenario();
        var ordinaryResult = await RunOrdinary(ordinary, new SipJobClosedInt(5));

        var fused = CreateScenario();
        var gates = TestGateSet.Enable("FG-JOB-LINEAR", "FG-DIRECT-SENTRY");
        var executor = new TestTwoStageExecutor(gates);
        var fusedResult = await executor.Run(
            fused.Plan, fused.Catalog, fused.Stage1Binding, fused.FinalRoute, new SipJobClosedInt(5));

        Assert.Equal(ordinaryResult, fusedResult);
        Assert.Equal(7, fusedResult.Value);
        Assert.Equal(ordinary.SemanticTrace, fused.SemanticTrace);
        Assert.Equal(2, ordinary.TransportRequests);
        Assert.Equal(1, fused.TransportRequests);
        Assert.Equal(1, ordinary.FinalPublications);
        Assert.Equal(1, fused.FinalPublications);
        Assert.Equal(0, fused.Kernel.Channels.InspectionSummary(fused.Caller).QueuedMessages);
    }

    [Fact]
    public async Task DirectContourIsDefaultOffAndRejectsMalformedOrStaleFirstStageBeforeImplementation()
    {
        var disabled = CreateScenario();
        var executor = new TestTwoStageExecutor(TestGateSet.Default);
        var denied = await executor.RunResult(
            disabled.Plan, disabled.Catalog, disabled.Stage1Binding, disabled.FinalRoute, new SipJobClosedInt(1));
        Assert.Equal(KernelError.PlatformUnsupported, denied.Error);
        Assert.Empty(disabled.SemanticTrace);

        var malformed = CreateScenario();
        var malformedBegin = malformed.Kernel.BeginInlineSessionInvocation(
            malformed.Caller,
            malformed.Stage1Service,
            malformed.Stage1Session,
            ISipJobClosedValueQualificationServiceProtocol.Message_TransformAsync,
            "wrong-shape");
        Assert.Equal(KernelError.UnsupportedPayload, malformedBegin.Error);
        Assert.Empty(malformed.SemanticTrace);

        var stale = CreateScenario();
        Assert.True(stale.Kernel.FaultProcess(stale.Stage1Service).IsSuccess);
        var staleResult = await new TestTwoStageExecutor(
            TestGateSet.Enable("FG-JOB-LINEAR", "FG-DIRECT-SENTRY"))
            .RunResult(stale.Plan, stale.Catalog, stale.Stage1Binding, stale.FinalRoute, new SipJobClosedInt(1));
        Assert.False(staleResult.IsSuccess);
        Assert.Empty(stale.SemanticTrace);

        var tampered = CreateScenario();
        var wrongDigest = new SipJobPlanDescriptor(
            tampered.Plan.FormatVersion,
            tampered.Plan.Stages,
            tampered.Plan.Edges,
            tampered.Plan.DeclaredGateSet,
            "00" + tampered.Plan.PlanDigest[2..]);
        var rejectedPlan = await new TestTwoStageExecutor(
            TestGateSet.Enable("FG-JOB-LINEAR", "FG-DIRECT-SENTRY"))
            .RunResult(wrongDigest, tampered.Catalog, tampered.Stage1Binding, tampered.FinalRoute, new SipJobClosedInt(1));
        Assert.Equal(KernelError.InvalidMessage, rejectedPlan.Error);
        Assert.Empty(tampered.SemanticTrace);
    }

    [Fact]
    public async Task FirstStageFaultSettlesWithExistingInvocationOwnerAndNeverRunsSecondStage()
    {
        var scenario = CreateScenario(stage1Fault: true);
        var result = await new TestTwoStageExecutor(
            TestGateSet.Enable("FG-JOB-LINEAR", "FG-DIRECT-SENTRY"))
            .RunResult(scenario.Plan, scenario.Catalog, scenario.Stage1Binding, scenario.FinalRoute, new SipJobClosedInt(3));

        Assert.Equal(KernelError.InvalidTransition, result.Error);
        Assert.Equal(
            [SemanticEvent.Stage1SessionValidated, SemanticEvent.Stage1ImplementationEntered],
            scenario.SemanticTrace);
        Assert.Equal(0, scenario.TransportRequests);
        Assert.Equal(0, scenario.FinalPublications);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Stage1Session));
    }

    [Fact]
    public async Task CloseAfterInlineBeginWinsBeforeSentryAndDeferredOwnerCleanupClosesChannel()
    {
        var scenario = CreateScenario(closeStage1AfterBegin: true);
        var result = await new TestTwoStageExecutor(
            TestGateSet.Enable("FG-JOB-LINEAR", "FG-DIRECT-SENTRY"))
            .RunResult(scenario.Plan, scenario.Catalog, scenario.Stage1Binding, scenario.FinalRoute, new SipJobClosedInt(3));

        Assert.Equal(KernelError.SessionClosed, result.Error);
        Assert.Equal(KernelError.SessionDraining, scenario.CloseRaceResult.Error);
        Assert.Empty(scenario.SemanticTrace);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Stage1Session));
        Assert.Equal(KernelError.StaleGeneration, scenario.Kernel.Channels.GetEndpoint(scenario.Stage1CallerEndpoint).Error);
    }

    [Fact]
    public async Task CancellationBeforeAdmissionStopsAllStagesAndLateCancellationLosesAfterOrdinaryAcceptancePoint()
    {
        var before = CreateScenario();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var beforeResult = await new TestTwoStageExecutor(
            TestGateSet.Enable("FG-JOB-LINEAR", "FG-DIRECT-SENTRY"))
            .RunResult(
                before.Plan,
                before.Catalog,
                before.Stage1Binding,
                before.FinalRoute,
                new SipJobClosedInt(3),
                cancelled.Token);
        Assert.Equal(KernelError.CancellationPending, beforeResult.Error);
        Assert.Empty(before.SemanticTrace);

        var late = CreateScenario(requestCancellationAfterBegin: true);
        var lateResult = await new TestTwoStageExecutor(
            TestGateSet.Enable("FG-JOB-LINEAR", "FG-DIRECT-SENTRY"))
            .RunResult(late.Plan, late.Catalog, late.Stage1Binding, late.FinalRoute, new SipJobClosedInt(3));
        Assert.True(lateResult.IsSuccess, lateResult.Message);
        Assert.Equal(KernelError.InvalidTransition, late.CancellationRaceResult.Error);
        Assert.Contains(SemanticEvent.Stage1ImplementationEntered, late.SemanticTrace);
        Assert.Equal(1, late.FinalPublications);
    }

    [Fact]
    public async Task ServiceFaultAfterInlineBeginPreventsSentryEntryAndCannotReplayBinding()
    {
        var scenario = CreateScenario(faultStage1AfterBegin: true);
        var executor = new TestTwoStageExecutor(
            TestGateSet.Enable("FG-JOB-LINEAR", "FG-DIRECT-SENTRY"));
        var first = await executor.RunResult(
            scenario.Plan, scenario.Catalog, scenario.Stage1Binding, scenario.FinalRoute, new SipJobClosedInt(3));
        var replay = await executor.RunResult(
            scenario.Plan, scenario.Catalog, scenario.Stage1Binding, scenario.FinalRoute, new SipJobClosedInt(3));

        Assert.False(first.IsSuccess);
        Assert.False(replay.IsSuccess);
        Assert.True(scenario.FaultRaceResult.IsSuccess, scenario.FaultRaceResult.Message);
        Assert.Empty(scenario.SemanticTrace);
        Assert.Equal(0, scenario.FinalPublications);
    }

    [Fact]
    public void ExecutorCannotHoldImplementationTargetDelegateOrObjectContainer()
    {
        var forbidden = new[]
        {
            typeof(QualificationHost),
            typeof(IISipJobClosedValueQualificationServiceGeneratedSentryTarget_TransformAsync),
            typeof(object),
        };
        var fields = typeof(TestTwoStageExecutor).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.DoesNotContain(fields, field =>
            forbidden.Contains(field.FieldType) || typeof(Delegate).IsAssignableFrom(field.FieldType));
        Assert.All(
            typeof(QualificationBinding).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => Assert.False(field.IsPublic));
    }

    private static async Task<SipJobClosedInt> RunOrdinary(Scenario scenario, SipJobClosedInt input)
    {
        var first = await scenario.InvokeStage1Ordinary(input);
        return await scenario.InvokeFinalOrdinary(first);
    }

    private static async Task<SipJobClosedInt> InvokeOrdinary(
        Scenario scenario,
        QualificationHost host,
        EndpointSessionHandle session,
        SipJobClosedInt input,
        bool final)
    {
        var transport = new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, session);
        var pending = transport.InvokeAsync(
            ISipJobClosedValueQualificationServiceProtocol.Message_TransformAsync,
            input).AsTask();
        scenario.TransportRequests++;
        var processed = host.ProcessNext();
        Assert.True(processed.IsSuccess, processed.Message);
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ResponsePublicationStatus.Published, response.Status);
        if (final) scenario.FinalPublications++;
        return Assert.IsType<SipJobClosedInt>(response.Payload);
    }

    private static Scenario CreateScenario(
        bool stage1Fault = false,
        bool closeStage1AfterBegin = false,
        bool requestCancellationAfterBegin = false,
        bool faultStage1AfterBegin = false)
    {
        var kernel = new RuntimeKernel();
        var caller = TestFixtures.Create(kernel, 63_001, 630_010).Handle;
        var stage1Service = TestFixtures.Create(kernel, 63_002, 630_020).Handle;
        var stage2Service = TestFixtures.Create(kernel, 63_003, 630_030).Handle;
        var protocol = ISipJobClosedValueQualificationServiceProtocol.CreateDefinition();
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var descriptor1 = kernel.RegisterService(
            stage1Service, "p14-closed-stage-1", contract, protocol,
            ISipJobClosedValueQualificationServiceResponseProtocol.Definition).Value!;
        var descriptor2 = kernel.RegisterService(
            stage2Service, "p14-closed-stage-2", contract, protocol,
            ISipJobClosedValueQualificationServiceResponseProtocol.Definition).Value!;
        var session1 = kernel.OpenSession(caller, descriptor1).Value;
        var session2 = kernel.OpenSession(caller, descriptor2).Value;
        var stage1CallerEndpoint = kernel.EndpointSessions.Resolve(session1, caller).Value!.Channel;
        var (plan, catalog) = CreatePlan(protocol);
        var scenario = new Scenario(kernel, caller, stage1Service, stage2Service, session1, session2, stage1CallerEndpoint, plan, catalog);
        scenario.Initialize(
            stage1Fault,
            closeStage1AfterBegin,
            requestCancellationAfterBegin,
            faultStage1AfterBegin);
        return scenario;
    }

    private static (SipJobPlanDescriptor Plan, SipJobClosedCatalog Catalog) CreatePlan(ProtocolDefinitionV1 protocol)
    {
        const string schema = "SingPlus.Tests.SipJobs.SipJobClosedInt";
        const string schemaDigest = "p14-closed-int-v1";
        var catalogEntry = new SipJobCatalogEntry(
            protocol.ContractName,
            protocol.ContractDigest,
            "TransformAsync",
            schema,
            schemaDigest,
            schema,
            schemaDigest,
            ISipJobClosedValueQualificationServiceGeneratedOperationSentries.Thunk_TransformAsync,
            ISipJobClosedValueQualificationServiceGeneratedOperationSentries.Thunk_TransformAsync_Digest,
            ImmutableHashSet<SipJobAuthoritySourceClass>.Empty);
        SipJobStageDescriptor Stage(string id, SipJobInvocationObservability observability) => new(
            SipJobPlanVerifier.DescriptorVersion,
            id,
            protocol.ContractName,
            protocol.ContractDigest,
            "TransformAsync",
            schema,
            schemaDigest,
            schema,
            schemaDigest,
            catalogEntry.GeneratedThunkId,
            catalogEntry.ThunkDigest,
            authorityRequirements: [],
            ownershipUseRequirements: [],
            protocolTransitionId: "Ready.TransformAsync.Ready",
            invocationObservability: observability,
            cancellationPolicyId: "JobRun.NoIndependentCancellation",
            effectClass: SipJobEffectClass.None,
            executionClass: SipJobExecutionClass.ManagedDefault,
            mode: SipJobStageMode.Synchronous);
        var stages = new[]
        {
            Stage("stage-1", SipJobInvocationObservability.HiddenIntermediate),
            Stage("stage-2", SipJobInvocationObservability.FinalPublication),
        };
        var edge = new SipJobEdgeDescriptor(
            SipJobPlanVerifier.DescriptorVersion,
            "edge-1-2",
            "stage-1",
            "stage-2",
            SipJobEdgeKind.ClosedCopiedValue,
            schema,
            schemaDigest,
            "None",
            "ClosedCopy",
            "InternalOnly",
            SipJobBarrierClass.None,
            "JobRun");
        var plan = new SipJobPlanDescriptor(
            SipJobPlanVerifier.FormatVersion,
            stages,
            [edge],
            [SipJobPlanVerifier.LinearGate],
            planDigest: string.Empty).WithDigest();
        return (plan, new SipJobClosedCatalog([catalogEntry]));
    }

    private enum SemanticEvent
    {
        Stage1SessionValidated,
        Stage1ImplementationEntered,
        Stage1ImplementationExited,
        Stage1InvocationSettled,
        Stage2SessionValidated,
        Stage2ImplementationEntered,
        Stage2ImplementationExited,
        Stage2InvocationSettled,
    }

    private sealed class Scenario(
        RuntimeKernel kernel,
        ProcessHandle caller,
        ProcessHandle stage1Service,
        ProcessHandle stage2Service,
        EndpointSessionHandle stage1Session,
        EndpointSessionHandle stage2Session,
        ChannelEndpointHandle stage1CallerEndpoint,
        SipJobPlanDescriptor plan,
        SipJobClosedCatalog catalog)
    {
        internal RuntimeKernel Kernel { get; } = kernel;
        internal ProcessHandle Caller { get; } = caller;
        internal ProcessHandle Stage1Service { get; } = stage1Service;
        internal ProcessHandle Stage2Service { get; } = stage2Service;
        internal EndpointSessionHandle Stage1Session { get; } = stage1Session;
        internal EndpointSessionHandle Stage2Session { get; } = stage2Session;
        internal ChannelEndpointHandle Stage1CallerEndpoint { get; } = stage1CallerEndpoint;
        internal SipJobPlanDescriptor Plan { get; } = plan;
        internal SipJobClosedCatalog Catalog { get; } = catalog;
        private QualificationHost _stage1Host = null!;
        private QualificationHost _stage2Host = null!;
        internal QualificationBinding Stage1Binding { get; private set; } = null!;
        internal QualificationFinalRoute FinalRoute { get; private set; } = null!;
        internal List<SemanticEvent> SemanticTrace { get; } = [];
        internal int TransportRequests { get; set; }
        internal int FinalPublications { get; set; }
        internal KernelResult CloseRaceResult { get; set; }
        internal KernelResult CancellationRaceResult { get; set; }
        internal KernelResult FaultRaceResult { get; set; }

        internal void Initialize(
            bool stage1Fault,
            bool closeStage1AfterBegin,
            bool requestCancellationAfterBegin,
            bool faultStage1AfterBegin)
        {
            _stage1Host = new QualificationHost(this, stage: 1, delta: 1, fault: stage1Fault);
            _stage2Host = new QualificationHost(this, stage: 2, delta: 1, fault: false);
            Stage1Binding = new QualificationBinding(
                Kernel,
                Caller,
                Stage1Service,
                Stage1Session,
                _stage1Host,
                SemanticTrace,
                closeStage1AfterBegin
                    ? new CloseAfterBeginHook(this)
                    : requestCancellationAfterBegin
                        ? new CancellationAfterBeginHook(this)
                        : faultStage1AfterBegin
                            ? new FaultAfterBeginHook(this)
                            : null);
            FinalRoute = new QualificationFinalRoute(this, _stage2Host);
        }

        internal Task<SipJobClosedInt> InvokeStage1Ordinary(SipJobClosedInt input) =>
            InvokeOrdinary(this, _stage1Host, Stage1Session, input, final: false);

        internal Task<SipJobClosedInt> InvokeFinalOrdinary(SipJobClosedInt input) =>
            InvokeOrdinary(this, _stage2Host, Stage2Session, input, final: true);

        internal KernelResult<SipJobClosedInt> InvokeStage1Inline(SipJobClosedInt input) =>
            Stage1Binding.Invoke(input);
    }

    private interface IQualificationInlineHook
    {
        void AfterBegin(InlineSipInvocationLease lease);
    }

    private sealed class CloseAfterBeginHook(Scenario scenario) : IQualificationInlineHook
    {
        public void AfterBegin(InlineSipInvocationLease lease) =>
            scenario.CloseRaceResult = scenario.Kernel.CloseSession(scenario.Caller, scenario.Stage1Session);
    }

    private sealed class CancellationAfterBeginHook(Scenario scenario) : IQualificationInlineHook
    {
        public void AfterBegin(InlineSipInvocationLease lease) =>
            scenario.CancellationRaceResult = scenario.Kernel.RequestSessionCancellation(
                scenario.Caller,
                lease.Context.Invocation);
    }

    private sealed class FaultAfterBeginHook(Scenario scenario) : IQualificationInlineHook
    {
        public void AfterBegin(InlineSipInvocationLease lease) =>
            scenario.FaultRaceResult = scenario.Kernel.FaultProcess(scenario.Stage1Service);
    }

    private sealed class QualificationFinalRoute(Scenario scenario, QualificationHost target)
    {
        internal Task<SipJobClosedInt> Invoke(SipJobClosedInt input) =>
            InvokeOrdinary(scenario, target, scenario.Stage2Session, input, final: true);
    }

    // Operation-specific TCB binding: the typed generated target never leaves this
    // object and the executor cannot obtain an implementation reference.
    private sealed class QualificationBinding(
        RuntimeKernel kernel,
        ProcessHandle caller,
        ProcessHandle service,
        EndpointSessionHandle session,
        IISipJobClosedValueQualificationServiceGeneratedSentryTarget_TransformAsync target,
        List<SemanticEvent> trace,
        IQualificationInlineHook? hook)
    {
        internal KernelResult<SipJobClosedInt> Invoke(SipJobClosedInt input)
        {
            var begun = kernel.BeginInlineSessionInvocation(
                caller,
                service,
                session,
                ISipJobClosedValueQualificationServiceProtocol.Message_TransformAsync,
                input);
            if (!begun.IsSuccess)
                return KernelResult<SipJobClosedInt>.Fail(begun.Error, begun.Message!);
            var lease = begun.Value!;
            try
            {
                hook?.AfterBegin(lease);
                var current = kernel.RevalidateInlineSessionInvocation(lease);
                if (!current.IsSuccess)
                {
                    _ = kernel.SettleInlineSessionInvocation(service, lease, succeeded: false);
                    return KernelResult<SipJobClosedInt>.Fail(current.Error, current.Message!);
                }
                trace.Add(SemanticEvent.Stage1SessionValidated);
                var context = lease.Context;
                var generated = ISipJobClosedValueQualificationServiceGeneratedOperationSentries.InvokeRuntime_TransformAsync(
                    target, in context, input);
                var settled = kernel.SettleInlineSessionInvocation(service, lease, generated.IsSuccess);
                if (!settled.IsSuccess)
                    return KernelResult<SipJobClosedInt>.Fail(settled.Error, settled.Message!);
                if (!generated.IsSuccess)
                    return KernelResult<SipJobClosedInt>.Fail((KernelError)generated.ErrorCode, generated.Message!);
                trace.Add(SemanticEvent.Stage1InvocationSettled);
                return KernelResult<SipJobClosedInt>.Ok(generated.Value);
            }
            finally
            {
                lease.Dispose();
            }
        }
    }

    private sealed class QualificationHost : IISipJobClosedValueQualificationServiceGeneratedSentryTarget_TransformAsync
    {
        private readonly Scenario _scenario;
        private readonly int _stage;
        private readonly int _delta;
        private readonly bool _fault;

        internal QualificationHost(Scenario scenario, int stage, int delta, bool fault) =>
            (_scenario, _stage, _delta, _fault) = (scenario, stage, delta, fault);

        internal KernelResult ProcessNext()
        {
            var result = NativeServiceDispatch.Dispatch(
                _scenario.Kernel,
                Service,
                Session,
                (context, envelope) =>
                {
                    RecordSession();
                    if (envelope.MessageId != ISipJobClosedValueQualificationServiceProtocol.Message_TransformAsync ||
                        envelope.Payload is not SipJobClosedInt request)
                        return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "Qualification request shape is invalid.");
                    var generated = ISipJobClosedValueQualificationServiceGeneratedOperationSentries.InvokeRuntime_TransformAsync(
                        this, in context, request);
                    return generated.IsSuccess
                        ? KernelResult<object?>.Ok(generated.Value)
                        : KernelResult<object?>.Fail((KernelError)generated.ErrorCode, generated.Message!);
                });
            if (result.IsSuccess) RecordSettled();
            return result;
        }

        GeneratedSipSentryResult<SipJobClosedInt> IISipJobClosedValueQualificationServiceGeneratedSentryTarget_TransformAsync.Sentry_TransformAsync(
            in TrustedSipInvocationContext context,
            SipJobClosedInt request)
        {
            RecordEntered();
            if (_fault)
                return GeneratedSipSentryResult<SipJobClosedInt>.Failure(
                    (int)KernelError.InvalidTransition,
                    "Deterministic qualification fault.");
            var response = new SipJobClosedInt(checked(request.Value + _delta));
            RecordExited();
            return GeneratedSipSentryResult<SipJobClosedInt>.Success(response);
        }

        internal void RecordSession() => _scenario.SemanticTrace.Add(
            _stage == 1 ? SemanticEvent.Stage1SessionValidated : SemanticEvent.Stage2SessionValidated);
        private void RecordEntered() => _scenario.SemanticTrace.Add(
            _stage == 1 ? SemanticEvent.Stage1ImplementationEntered : SemanticEvent.Stage2ImplementationEntered);
        private void RecordExited() => _scenario.SemanticTrace.Add(
            _stage == 1 ? SemanticEvent.Stage1ImplementationExited : SemanticEvent.Stage2ImplementationExited);
        internal void RecordSettled() => _scenario.SemanticTrace.Add(
            _stage == 1 ? SemanticEvent.Stage1InvocationSettled : SemanticEvent.Stage2InvocationSettled);
        private ProcessHandle Service => _stage == 1 ? _scenario.Stage1Service : _scenario.Stage2Service;
        private EndpointSessionHandle Session => _stage == 1 ? _scenario.Stage1Session : _scenario.Stage2Session;
    }

    private sealed class TestTwoStageExecutor(TestGateSet gates)
    {
        internal async Task<SipJobClosedInt> Run(
            SipJobPlanDescriptor plan,
            SipJobClosedCatalog catalog,
            QualificationBinding firstRoute,
            QualificationFinalRoute finalRoute,
            SipJobClosedInt input,
            CancellationToken cancellationToken = default)
        {
            var result = await RunResult(plan, catalog, firstRoute, finalRoute, input, cancellationToken);
            Assert.True(result.IsSuccess, result.Message);
            return result.Value;
        }

        internal async Task<KernelResult<SipJobClosedInt>> RunResult(
            SipJobPlanDescriptor plan,
            SipJobClosedCatalog catalog,
            QualificationBinding firstRoute,
            QualificationFinalRoute finalRoute,
            SipJobClosedInt input,
            CancellationToken cancellationToken = default)
        {
            if (!gates.IsExact("FG-JOB-LINEAR", "FG-DIRECT-SENTRY"))
                return KernelResult<SipJobClosedInt>.Fail(
                    KernelError.PlatformUnsupported,
                    "The exact P14-2 qualification gate set is not enabled.");
            var verified = SipJobPlanVerifier.Verify(plan, catalog);
            if (!verified.IsSuccess)
                return KernelResult<SipJobClosedInt>.Fail(
                    KernelError.InvalidMessage,
                    verified.Failure!.Value.Detail);
            if (cancellationToken.IsCancellationRequested)
                return KernelResult<SipJobClosedInt>.Fail(
                    KernelError.CancellationPending,
                    "Job-run cancellation was requested before first-stage admission.");

            var first = firstRoute.Invoke(input);
            if (!first.IsSuccess)
                return KernelResult<SipJobClosedInt>.Fail(first.Error, first.Message!);
            return KernelResult<SipJobClosedInt>.Ok(
                await finalRoute.Invoke(first.Value));
        }
    }

    private sealed class TestGateSet
    {
        private readonly HashSet<string> _enabled;
        private TestGateSet(IEnumerable<string> enabled) => _enabled = enabled.ToHashSet(StringComparer.Ordinal);
        internal static TestGateSet Default { get; } = new([]);
        internal static TestGateSet Enable(params string[] enabled) => new(enabled);
        internal bool IsExact(params string[] required) =>
            _enabled.SetEquals(required) && required.All(static name => SipJobFeatureGates.TryResolve(name, out _));
    }
}
