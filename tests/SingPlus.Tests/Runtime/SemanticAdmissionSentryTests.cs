using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Tests.Contracts;

namespace SingPlus.Tests.Runtime;

public sealed class SemanticAdmissionSentryTests
{
    [Fact]
    public void ExactFourGateConjunctionSubmitsOnceAndSequentialReplayCannotCompensate()
    {
        var context = Create();
        using var commit = context.Commit;
        var callbacks = 0;

        var first = Submit(context, new Provider(context.Binding), new Runtime(context.Binding),
            () => { callbacks++; return KernelResult.Ok(); });
        var duplicate = Submit(context, new Provider(context.Binding), new Runtime(context.Binding),
            () => { callbacks++; return KernelResult.Ok(); });

        Assert.True(first.IsSuccess, first.Message);
        Assert.Equal(KernelError.InvalidTransition, duplicate.Error);
        Assert.Equal(1, callbacks);
        Assert.Equal(BudgetReservationState.Consuming,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ProviderRefinementOrRuntimeGateFailureCompensatesBeforeSubmit(int failingGate)
    {
        var context = Create();
        using var commit = context.Commit;
        var callbacks = 0;
        var provider = new Provider(context.Binding, failingGate == 0);
        var runtime = new Runtime(context.Binding, failingGate == 2);
        var proof = failingGate == 1
            ? context.Refinement with { Digest = new("00") }
            : context.Refinement;

        var result = context.Kernel.SubmitSemanticResourceExternalAdmission(commit, context.Dependencies,
            context.Binding, context.Obligations, context.Guarantees, proof,
            ProviderIdentity, context.ProviderGenerations, provider, runtime,
            () => { callbacks++; return KernelResult.Ok(); });

        Assert.False(result.IsSuccess);
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
        Assert.NotEqual(ExternalOperationState.Submitted,
            context.Kernel.ExternalOperations.Query(context.Operation).Value!.State);
    }

    [Fact]
    public void RevocationDuringIndependentRuntimeLegalityIsCaughtByFinalOwnerRead()
    {
        var context = Create();
        using var commit = context.Commit;
        var runtime = new Runtime(context.Binding, beforeDecision: () =>
            Assert.True(context.Kernel.CapabilityAuthority.Revoke(context.EffectCapability).IsSuccess));

        var result = Submit(context, new Provider(context.Binding), runtime, KernelResult.Ok);

        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void CachedRefinementAndGateEvidenceCannotSubmitAfterRevocation()
    {
        var context = Create();
        using var commit = context.Commit;
        Assert.True(context.Kernel.CapabilityAuthority.Revoke(context.EffectCapability).IsSuccess);
        var callbacks = 0;

        var result = Submit(context, new Provider(context.Binding), new Runtime(context.Binding),
            () => { callbacks++; return KernelResult.Ok(); });

        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void SessionCloseDuringRuntimeLegalityIsCaughtBeforeSubmit()
    {
        var context = Create(withSession: true);
        using var commit = context.Commit;
        var runtime = new Runtime(context.Binding, beforeDecision: () =>
            Assert.True(context.Kernel.CloseSession(context.Principal, context.Session!.Value).IsSuccess));

        var result = Submit(context, new Provider(context.Binding), runtime, KernelResult.Ok);

        Assert.Equal(KernelError.SessionClosed, result.Error);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void ProviderGenerationDriftFailsBeforeRuntimeLegalityAndSubmit()
    {
        var context = Create();
        using var commit = context.Commit;
        var stale = new Hc.ExternalGenerationSet(Hc.ExternalOperationContract.Version,
            [Guid.Parse("70db585c-12e8-4c7b-a8dc-876f69c899c4")]);
        var runtimeCalls = 0;

        var result = context.Kernel.SubmitSemanticResourceExternalAdmission(commit, context.Dependencies,
            context.Binding, context.Obligations, context.Guarantees, context.Refinement,
            ProviderIdentity, stale, new Provider(context.Binding),
            new Runtime(context.Binding, beforeDecision: () => runtimeCalls++), KernelResult.Ok);

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(0, runtimeCalls);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public async Task ConcurrentCallersHaveOneIrreversibleSubmitWinnerAndNoRefund()
    {
        var context = Create();
        using var commit = context.Commit;
        using var barrier = new Barrier(2);
        var callbacks = 0;
        KernelResult<OperationBinding> Invoke() => Submit(context,
            new Provider(context.Binding),
            new Runtime(context.Binding, beforeDecision: () => barrier.SignalAndWait(TimeSpan.FromSeconds(5))),
            () => { Interlocked.Increment(ref callbacks); return KernelResult.Ok(); });

        var results = await Task.WhenAll(Task.Run(Invoke), Task.Run(Invoke)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(results, result => result.Error == KernelError.InvalidTransition);
        Assert.Equal(1, callbacks);
        Assert.Equal(BudgetReservationState.Consuming,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    [Fact]
    public void FinalSentryExceptionFailsClosedBeforeSubmit()
    {
        var context = Create();
        using var commit = context.Commit;

        var result = Submit(context, new Provider(context.Binding),
            new Runtime(context.Binding, beforeDecision: () => throw new InvalidOperationException("fault")),
            KernelResult.Ok);

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }

    private static KernelResult<OperationBinding> Submit(Context context,
        ISemanticProviderAdmissionService provider, IRuntimeLegalityService runtime,
        Func<KernelResult> callback) =>
        context.Kernel.SubmitSemanticResourceExternalAdmission(context.Commit, context.Dependencies,
            context.Binding, context.Obligations, context.Guarantees, context.Refinement,
            ProviderIdentity, context.ProviderGenerations, provider, runtime, callback);

    private static Context Create(bool withSession = false)
    {
        var time = new TestTimeProvider(new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        var kernel = new RuntimeKernel(null, time);
        var admin = TestFixtures.Create(kernel, 1200, 2300).Handle;
        var principal = TestFixtures.Create(kernel, 1201, 2301).Handle;
        var administration = kernel.MintCapability(new(2300), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, principal, "semantic-sentry",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        var effect = kernel.MintCapability(new(2301), principal, ResourceKind.Compute,
            "compute:semantic-sentry", CapabilityRights.Execute, resourceGeneration: 1).Value!.CapabilityId;
        var grant = kernel.CapabilityAuthority.Mint(new(2301), new(2301), ResourceKind.Compute,
            "resource-use:semantic-sentry", CapabilityRights.Delegate, principal.Generation, 1,
            range: null, session: null, quota: 100, delegationDepth: 2,
            resourceUse: new(1, OperationObligationsV1Tests.Envelope(100), 0, long.MaxValue,
                ResourceAssuranceV1.RuntimeEnforced, 2)).Value!.CapabilityId;
        var lease = kernel.Budgets.Reserve(principal,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 10)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        var region = kernel.AllocateBuffer<byte>(principal, 16).Value!.Handle;
        var operation = kernel.PrepareExternalOperation(principal,
            [new(region, RegionUseMode.ReadOnly, new(0, 16))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        EndpointSessionHandle? session = null;
        if (withSession)
        {
            var provider = TestFixtures.Create(kernel, 1202, 2302).Handle;
            var protocol = new ProtocolDefinitionV1("SentrySession", "semantic-sentry-v1", "Ready", ["Done"],
                [new ProtocolMessageDescriptorV1(1, "Run")],
                [new ProtocolTransitionV1(1, "Ready", "Done")]);
            var service = kernel.RegisterService(provider, "semantic-sentry",
                new("SentrySession", "1", "semantic-sentry-v1"), protocol).Value!;
            session = kernel.OpenSession(principal, service).Value;
        }
        var now = time.GetUtcNow();
        var obligations = kernel.ConstructOperationObligationsV1(principal, operation,
            [OperationObligationsV1Tests.Envelope(10)], RefinableRequirements(),
            new(now.AddMinutes(-1).UtcTicks, now.AddHours(1).UtcTicks), session).Value!;
        var guarantees = HybridCpu114SemanticCompatibility.Map(ExecutionGuaranteesV1Tests.Request()).Value!;
        var generations = new Hc.ExternalGenerationSet(Hc.ExternalOperationContract.Version,
            [Guid.Parse("7ba47ce0-cb38-4a60-b542-2013e4576af0")]);
        var binding = kernel.CreateSemanticExecutionBindingV1(obligations, guarantees, principal, lease,
            OperationObligationsV1Tests.Envelope(10), ProviderIdentity,
            ExecutionGuaranteesV1Tests.Request().Correlation, generations).Value!;
        var refinement = SemanticExecutionRefinementV1.Evaluate(binding, obligations, guarantees).Value!;
        var dependencies = new OperationDependencySnapshot(1, 1);
        var commit = kernel.PrepareResourceExternalAdmission(principal, effect, ResourceKind.Compute,
            "compute:semantic-sentry", 1, grant, 1, OperationObligationsV1Tests.Envelope(10),
            operation, dependencies, existingLease: lease, providerIdentity: ProviderIdentity,
            providerGeneration: 1).Value!;
        return new(kernel, principal, effect, operation, lease, obligations, guarantees, binding,
            refinement, generations, dependencies, commit, session);
    }

    private static OperationSemanticRequirementsV1 RefinableRequirements() => new(
        Advisory(IsolationClassV1.DomainSeparated),
        Required(PublicationEnforcementClassV1.StagedWithholdingUntilDecision),
        Advisory(ReplayClassV1.None),
        Advisory(DeterminismClassV1.StableOrdering),
        Required(CancellationClassV1.ExactAcknowledgement),
        Advisory(ContainmentClassV1.None),
        Advisory(LocalityClassV1.Any),
        Advisory(ResourceAssuranceV1.RuntimeEnforced));

    private static SemanticRequirementV1<T> Required<T>(T value) where T : struct, Enum =>
        new(1, SemanticRequirementStrengthV1.Mandatory, value);
    private static SemanticRequirementV1<T> Advisory<T>(T value) where T : struct, Enum =>
        new(1, SemanticRequirementStrengthV1.Advisory, value);

    private const string ProviderIdentity = "hybridcpu:external-runtime";

    private sealed class Provider(SemanticExecutionBindingV1 binding, bool deny = false)
        : ISemanticProviderAdmissionService
    {
        public KernelResult<ProviderAdmissionDecisionV1> Revalidate(SemanticExecutionBindingV1 _) =>
            KernelResult<ProviderAdmissionDecisionV1>.Ok(new(1, binding.Digest, binding.ProviderIdentity,
                binding.ProviderGenerationDigest, binding.ProviderRequestCorrelation,
                deny ? SemanticGateDecisionStatusV1.Denied : SemanticGateDecisionStatusV1.Allowed, "test-provider"));
    }

    private sealed class Runtime(SemanticExecutionBindingV1 binding, bool deny = false,
        Action? beforeDecision = null) : IRuntimeLegalityService
    {
        public KernelResult<RuntimeLegalityDecisionV1> Evaluate(SemanticExecutionBindingV1 _)
        {
            beforeDecision?.Invoke();
            return KernelResult<RuntimeLegalityDecisionV1>.Ok(new(1, binding.Digest, "test-runtime", 1,
                deny ? SemanticGateDecisionStatusV1.Denied : SemanticGateDecisionStatusV1.Allowed,
                "test-runtime-evidence"));
        }
    }

    private sealed record Context(RuntimeKernel Kernel, ProcessHandle Principal, CapabilityId EffectCapability,
        ExternalOperationHandle Operation, BudgetReservationHandle Lease, OperationObligationsV1 Obligations,
        ExecutionGuaranteesV1 Guarantees, SemanticExecutionBindingV1 Binding,
        SemanticRefinementProofV1 Refinement, Hc.ExternalGenerationSet ProviderGenerations,
        OperationDependencySnapshot Dependencies, ResourceAdmissionCommit Commit, EndpointSessionHandle? Session);

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
