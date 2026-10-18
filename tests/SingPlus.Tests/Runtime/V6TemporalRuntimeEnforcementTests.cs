using System.Text;
using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Tests.Contracts;

namespace SingPlus.Tests.Runtime;

public sealed class V6TemporalRuntimeEnforcementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedPreSubmitTemporalTraceRetainsCapturedSourceAndActualLocalRelease(bool throws)
    {
        var c = Create();
        using var commit = c.Commit;
        var events = new List<SemanticTraceEventV1>();
        var calls = 0;
        ulong generation = 1;
        var runtime = new Runtime(c.Binding, () =>
        {
            if (throws) throw new IOException("runtime lost before submit");
            generation = 2;
        });
        Assert.False(Submit(c, () => generation, runtime, () => { calls++; return KernelResult.Ok(); },
            new Observer(events.Add)).IsSuccess);
        Assert.Equal(0, calls);
        Assert.Equal(SemanticTraceEventKindV1.CancelledBeforeSubmit, Assert.Single(events).Kind);
        Assert.Equal(c.TemporalBinding.ExtensionBinding.Digest.Value, events[0].GenerationVectorDigest);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit, c.Kernel.QueryBudget(c.Lease).Value!.State);
        Assert.True(c.Kernel.ReleaseExternalOperation(c.Principal, c.Operation, new(false, false)).IsSuccess);
        Assert.Equal([SemanticTraceEventKindV1.CancelledBeforeSubmit,
            SemanticTraceEventKindV1.LocalAuthorityReleasedBeforeSubmit], events.Select(item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(events).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TemporalGenerationCallbackCannotAuthorizeChangedDispatchOwners(int fault)
    {
        var c = Create();
        using var commit = c.Commit;
        var armed = false;
        c.Kernel.ResourceAdmissionQualificationHook = new CallbackHook(() => armed = true);
        ulong Generation()
        {
            if (armed)
            {
                armed = false;
                var changed = fault switch
                {
                    0 => c.Kernel.ExternalOperations.RecordProviderLoss(c.Operation).IsSuccess,
                    1 => c.Kernel.Budgets.QuarantineLease(c.Commit.BudgetOwner, c.Lease).IsSuccess,
                    _ => c.Kernel.ExternalOperations.MarkResourceQuarantined(c.Operation).IsSuccess,
                };
                Assert.True(changed);
            }
            return 1;
        }
        var callbacks = 0;
        var result = Submit(c, Generation, new Runtime(c.Binding),
            () => { callbacks++; return KernelResult.Ok(); });
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.QueryBudget(c.Lease).Value!.State);
        Assert.Equal(ExternalOperationState.Submitted, c.Kernel.ExternalOperations.Query(c.Operation).Value!.State);
    }

    [Fact]
    public void NullTemporalCallbackCannotCommitOwnerOrEmitTrace()
    {
        var c = Create();
        using var commit = c.Commit;
        var observations = 0;
        Assert.Throws<ArgumentNullException>(() => Submit(c, () => 1, new Runtime(c.Binding), null!,
            new Observer(_ => observations++)));
        Assert.Equal(0, observations);
        Assert.Equal(ExternalOperationState.Admitted,
            c.Kernel.QueryExternalOperation(c.Principal, c.Operation).Value!.State);
        Assert.Equal(BudgetReservationState.Bound, c.Kernel.QueryBudget(c.Lease).Value!.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TemporalTraceObserverCannotAuthorizeLateCallbackOrVetoSubmit(int action)
    {
        var c = Create();
        using var commit = c.Commit;
        var callbacks = 0;
        var observations = 0;
        ulong providerGeneration = 1;
        ExternalOperationState? observedState = null;
        bool? lossSucceeded = null;
        bool? duplicateSucceeded = null;
        if (action == 3)
            c.Kernel.ResourceAdmissionQualificationHook = new CallbackHook(() =>
                lossSucceeded = c.Kernel.RecordExternalOperationProviderLoss(c.Principal, c.Operation).IsSuccess);
        var sink = new Observer(item =>
        {
            if (Interlocked.Increment(ref observations) != 1) return;
            observedState = c.Kernel.QueryExternalOperation(c.Principal, c.Operation).Value!.State;
            if (action == 0) throw new InvalidOperationException("observation lost");
            if (action == 1)
                lossSucceeded = c.Kernel.RecordExternalOperationProviderLoss(c.Principal, c.Operation).IsSuccess;
            if (action == 2)
                duplicateSucceeded = Submit(c, () => 1, new Runtime(c.Binding), KernelResult.Ok).IsSuccess;
            if (action == 4) providerGeneration++;
        });
        var submitted = Submit(c, () => providerGeneration, new Runtime(c.Binding),
            () => { callbacks++; return KernelResult.Ok(); }, sink);
        Assert.True(observations > 0);
        Assert.Equal(ExternalOperationState.Submitted, observedState);
        if (action is 1 or 3) Assert.True(lossSucceeded);
        if (action == 2) Assert.False(duplicateSucceeded);
        Assert.Equal(action is 1 or 3 or 4 ? 0 : 1, callbacks);
        Assert.Equal(action is not (1 or 3 or 4), submitted.IsSuccess);
        Assert.Equal(action is 1 or 3 or 4 ? BudgetReservationState.Quarantined : BudgetReservationState.Consuming,
            c.Kernel.QueryBudget(c.Lease).Value!.State);
    }

    private sealed class CallbackHook(Action action) : IResourceAdmissionQualificationHook
    {
        public void At(ResourceAdmissionQualificationPoint point)
        {
            if (point == ResourceAdmissionQualificationPoint.BeforeProviderCallback) action();
        }
    }

    private sealed class Observer(Action<SemanticTraceEventV1> observe) : ISemanticTraceSinkV1
    {
        public bool TryRecord(SemanticTraceEventV1 item) { observe(item); return true; }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingOptionalCompanionRequiresRebuiltExactTemporalSidecar(bool guarantee)
    {
        var context = Create();
        using var commit = context.Commit;
        var original = context.TemporalBinding;
        var optional = SemanticExtensionClauseV1.Create(new("zzz.optional-companion"), "companion/1", 1,
            SemanticExtensionRequirement.Optional, [1]);
        var requirements = OperationSemanticExtensionsV1.Create(guarantee ? original.Requirements.Clauses :
            original.Requirements.Clauses.Append(optional));
        var guarantees = ExecutionGuaranteeExtensionsV1.Create(guarantee ? original.Guarantees.Clauses.Append(optional) :
            original.Guarantees.Clauses);
        var extended = context.Kernel.CreateV6TemporalSemanticBinding(context.Binding, context.Obligations,
            context.Guarantees, requirements, guarantees, 1);
        Assert.True(extended.IsSuccess, extended.Message);
        var changed = extended.Value! with { Requirements = original.Requirements, Guarantees = original.Guarantees };
        Assert.Equal(KernelError.StaleGeneration, context.Kernel.RevalidateV6TemporalSemanticBinding(changed,
            commit, context.Binding, context.Obligations, context.Guarantees, 1).Error);
        var fresh = context.Kernel.CreateV6TemporalSemanticBinding(context.Binding, context.Obligations,
            context.Guarantees, original.Requirements, original.Guarantees, 1);
        Assert.True(fresh.IsSuccess, fresh.Message);
        Assert.NotEqual(extended.Value.ExtensionBinding.Digest, fresh.Value!.ExtensionBinding.Digest);
        Assert.True(context.Kernel.RevalidateV6TemporalSemanticBinding(fresh.Value,
            commit, context.Binding, context.Obligations, context.Guarantees, 1).IsSuccess);
        Assert.False(commit.SubmitStarted);
        Assert.Equal(BudgetReservationState.Bound, context.Kernel.QueryBudget(commit.Lease).Value!.State);
    }

    [Fact]
    public void MandatoryTemporalCannotDowngradeToOptionalEvenWithFreshBinding()
    {
        var context = Create();
        using var commit = context.Commit;
        var original = context.TemporalBinding;
        var optional = OperationSemanticExtensionsV1.Create([
            original.RequiredTemporal.ToClause(SemanticExtensionRequirement.Optional)]);
        Assert.Equal(KernelError.PlatformDenied, context.Kernel.CreateV6TemporalSemanticBinding(context.Binding,
            context.Obligations, context.Guarantees, optional, original.Guarantees, 1).Error);
        Assert.Equal(KernelError.PlatformDenied, context.Kernel.RevalidateV6TemporalSemanticBinding(
            original with { Requirements = optional }, commit, context.Binding, context.Obligations,
            context.Guarantees, 1).Error);
        Assert.False(commit.SubmitStarted);
        Assert.Equal(BudgetReservationState.Bound, context.Kernel.QueryBudget(commit.Lease).Value!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlteredBaseDigestSidecarCannotPassFreshTemporalBinding(bool guarantee)
    {
        var context = Create();
        using var commit = context.Commit;
        var sidecar = context.TemporalBinding;
        var original = sidecar.ExtensionBinding;
        var forged = SemanticBindingExtensionSetV1.Create(
            guarantee ? original.ObligationsV1Digest : Change(original.ObligationsV1Digest),
            guarantee ? Change(original.GuaranteesV1Digest) : original.GuaranteesV1Digest,
            sidecar.Requirements, sidecar.Guarantees, original.Generations);
        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.RevalidateV6TemporalSemanticBinding(sidecar with { ExtensionBinding = forged },
                commit, context.Binding, context.Obligations, context.Guarantees, 1).Error);
        Assert.False(commit.SubmitStarted);
        Assert.Equal(BudgetReservationState.Bound, context.Kernel.QueryBudget(commit.Lease).Value!.State);
        static string Change(string digest) => (digest[0] == '0' ? "1" : "0") + digest[1..];
    }

    [Theory]
    [InlineData("future.temporal/1", 1)]
    [InlineData("singnext.temporal-semantics/1", 2)]
    public void MatchingUnknownTemporalSchemasCannotBorrowV1PayloadPermission(string schema, int version)
    {
        var context = Create(createTemporalBinding: false);
        using var commit = context.Commit;
        var clause = SemanticExtensionClauseV1.Create(TemporalSemanticsV1.ExtensionClassId,
            schema, (ushort)version, SemanticExtensionRequirement.Mandatory, Temporal(10).SerializeCanonical());
        var result = context.Kernel.CreateV6TemporalSemanticBinding(context.Binding, context.Obligations,
            context.Guarantees, OperationSemanticExtensionsV1.Create([clause]),
            ExecutionGuaranteeExtensionsV1.Create([clause]), 1);
        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(BudgetReservationState.Bound, context.Kernel.Budgets.Query(context.Lease).Value!.State);
    }
    [Fact]
    public void ExactUpperBoundRevalidatesAtFinalSentryAndSubmitsOnce()
    {
        var context = Create();
        using var commit = context.Commit;
        var callbacks = 0;

        var result = Submit(context, () => 1, new Runtime(context.Binding), () =>
        {
            callbacks++;
            Assert.Equal(BudgetReservationState.Consuming,
                context.Kernel.Budgets.Query(context.Lease).Value!.State);
            return KernelResult.Ok();
        });

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, callbacks);
        Assert.False(context.TemporalBinding.AuthorizesExecution);
        Assert.False(context.TemporalBinding.ReservesCapacity);
        Assert.False(context.TemporalBinding.GuaranteesDeadline);
    }

    [Fact]
    public void ProviderGenerationDriftDuringRuntimeLegalityFailsBeforeEffect()
    {
        var context = Create();
        using var commit = context.Commit;
        ulong generation = 1;
        var callbacks = 0;

        var result = Submit(context, () => generation,
            new Runtime(context.Binding, () => generation = 2), () =>
            {
                callbacks++;
                return KernelResult.Ok();
            });

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(context.Lease).Value!.State);
        Assert.NotEqual(ExternalOperationState.Submitted,
            context.Kernel.ExternalOperations.Query(context.Operation).Value!.State);
    }

    [Fact]
    public void WeakerRuntimeAccountingCannotSatisfyUpperBoundRequirement()
    {
        var context = Create(createTemporalBinding: false);
        using var commit = context.Commit;
        var weaker = Temporal(10) with { Assurance = ResourceAssuranceV1.RuntimeEnforced };

        var result = context.Kernel.CreateV6TemporalSemanticBinding(context.Binding, context.Obligations,
            context.Guarantees, Requirements(Temporal(10)), Guarantees(weaker), 1);

        Assert.Equal(KernelError.PlatformDenied, result.Error);
    }

    [Fact]
    public void CapacityReservationAndGuaranteedDeadlineRemainOutsideFirstContour()
    {
        var context = Create(createTemporalBinding: false);
        using var commit = context.Commit;
        var deadline = new TemporalSemanticsV1(1, Envelope(10),
            ResourceAssuranceV1.GuaranteedReservation,
            TemporalDeadlineSemanticsV1.GuaranteedCompletion,
            DeadlineClockClass.MonotonicRuntime, 1000).Validate();

        var result = context.Kernel.CreateV6TemporalSemanticBinding(context.Binding, context.Obligations,
            context.Guarantees, Requirements(deadline), Guarantees(deadline), 1);

        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.False(V6FeatureGates.IsEnabled("V6-GUARANTEED-DEADLINE"));
    }

    [Fact]
    public void UnknownMandatoryTemporalCompanionClauseFailsClosed()
    {
        var context = Create(createTemporalBinding: false);
        using var commit = context.Commit;
        var unknown = SemanticExtensionClauseV1.Create(new("zzz.temporal-unknown"), "unknown/1", 1,
            SemanticExtensionRequirement.Mandatory, Encoding.UTF8.GetBytes("required"));
        var requirements = OperationSemanticExtensionsV1.Create([
            Temporal(10).ToClause(SemanticExtensionRequirement.Mandatory), unknown
        ]);

        var result = context.Kernel.CreateV6TemporalSemanticBinding(context.Binding, context.Obligations,
            context.Guarantees, requirements, Guarantees(Temporal(10)), 1);

        Assert.Equal(KernelError.PlatformDenied, result.Error);
    }

    [Fact]
    public void UnknownMandatoryProviderCompanionCannotHideBehindTemporalGuarantee()
    {
        var context = Create(createTemporalBinding: false);
        using var commit = context.Commit;
        var unknown = SemanticExtensionClauseV1.Create(new("zzz.provider-unknown"), "unknown/1", 1,
            SemanticExtensionRequirement.Mandatory, []);
        var offered = ExecutionGuaranteeExtensionsV1.Create([.. Guarantees(Temporal(10)).Clauses, unknown]);
        var result = context.Kernel.CreateV6TemporalSemanticBinding(context.Binding, context.Obligations,
            context.Guarantees, OperationSemanticExtensionsV1.Create([
                Temporal(10).ToClause(SemanticExtensionRequirement.Mandatory)]), offered, 1);
        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.False(commit.SubmitStarted);
        Assert.Equal(BudgetReservationState.Bound, context.Kernel.QueryBudget(commit.Lease).Value!.State);
    }

    private static KernelResult<OperationBinding> Submit(Context context, Func<ulong> generation,
        IRuntimeLegalityService runtime, Func<KernelResult> callback, ISemanticTraceSinkV1? sink = null) =>
        context.Kernel.SubmitV6TemporalSemanticResourceExternalAdmission(context.Commit,
            context.Dependencies, context.Binding, context.TemporalBinding, context.Obligations,
            context.Guarantees, context.Refinement, ProviderIdentity, generation,
            context.ProviderGenerations, new Provider(context.Binding), runtime, callback, sink);

    private static Context Create(bool createTemporalBinding = true)
    {
        var time = new TestTimeProvider(new(2026, 9, 24, 15, 0, 0, TimeSpan.Zero));
        var kernel = new RuntimeKernel(null, time);
        var admin = TestFixtures.Create(kernel, 5200, 6200).Handle;
        var principal = TestFixtures.Create(kernel, 5201, 6201).Handle;
        var administration = kernel.MintCapability(new(6200), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, principal, "v6-temporal",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        var effect = kernel.MintCapability(new(6201), principal, ResourceKind.Compute,
            "compute:v6-temporal", CapabilityRights.Execute, resourceGeneration: 1).Value!.CapabilityId;
        var grant = kernel.CapabilityAuthority.Mint(new(6201), new(6201), ResourceKind.Compute,
            "resource-use:v6-temporal", CapabilityRights.Delegate, principal.Generation, 1,
            range: null, session: null, quota: 100, delegationDepth: 2,
            resourceUse: new(1, Envelope(100), 0, long.MaxValue,
                ResourceAssuranceV1.EnforcedUpperBound, 2)).Value!.CapabilityId;
        var lease = kernel.Budgets.Reserve(principal,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 10)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        var region = kernel.AllocateBuffer<byte>(principal, 16).Value!.Handle;
        var operation = kernel.PrepareExternalOperation(principal,
            [new(region, RegionUseMode.ReadOnly, new(0, 16))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        var dependencies = new OperationDependencySnapshot(1, 1, 1, 1);
        var commit = kernel.PrepareResourceExternalAdmission(principal, effect, ResourceKind.Compute,
            "compute:v6-temporal", 1, grant, 1, Envelope(10), operation, dependencies,
            existingLease: lease, providerIdentity: ProviderIdentity, providerGeneration: 1).Value!;
        var now = time.GetUtcNow();
        var obligations = kernel.ConstructOperationObligationsV1(principal, operation, [Envelope(10)],
            RefinableRequirements(), new(now.AddMinutes(-1).UtcTicks, now.AddHours(1).UtcTicks)).Value!;
        var guarantees = UpperBoundGuarantees();
        var providerGenerations = new Hc.ExternalGenerationSet(Hc.ExternalOperationContract.Version,
            [Guid.Parse("34dbc39e-84b3-4bb8-9c58-466d05e92b60")]);
        var binding = kernel.CreateSemanticExecutionBindingV1(obligations, guarantees, principal, lease,
            Envelope(10), ProviderIdentity, ExecutionGuaranteesV1Tests.Request().Correlation,
            providerGenerations).Value!;
        var refinement = SemanticExecutionRefinementV1.Evaluate(binding, obligations, guarantees).Value!;
        var temporalBinding = createTemporalBinding
            ? kernel.CreateV6TemporalSemanticBinding(binding, obligations, guarantees,
                Requirements(Temporal(10)), Guarantees(Temporal(10)), 1).Value!
            : null!;
        return new(kernel, principal, operation, lease, obligations, guarantees, binding,
            temporalBinding, refinement, providerGenerations, dependencies, commit);
    }

    private static ExecutionGuaranteesV1 UpperBoundGuarantees()
    {
        var mapped = HybridCpu114SemanticCompatibility.Map(ExecutionGuaranteesV1Tests.Request()).Value!;
        return (mapped with
        {
            Digest = default,
            ResourceEnforcement = new(1, SemanticGuaranteeSupportV1.Supported,
                ResourceAssuranceV1.EnforcedUpperBound, SemanticClaimLevelV1.EnforcedUpperBound,
                GuaranteeEvidenceSourceV1.RuntimeAdmissionReceipt),
        }).Canonicalize();
    }

    private static TemporalSemanticsV1 Temporal(ulong amount) => new TemporalSemanticsV1(1,
        Envelope(amount), ResourceAssuranceV1.EnforcedUpperBound,
        TemporalDeadlineSemanticsV1.None, DeadlineClockClass.MonotonicRuntime, 0).Validate();
    private static ResourceEnvelopeV1 Envelope(ulong amount) =>
        OperationObligationsV1Tests.Envelope(amount);
    private static OperationSemanticExtensionsV1 Requirements(TemporalSemanticsV1 value) =>
        OperationSemanticExtensionsV1.Create([value.ToClause(SemanticExtensionRequirement.Mandatory)]);
    private static ExecutionGuaranteeExtensionsV1 Guarantees(TemporalSemanticsV1 value) =>
        ExecutionGuaranteeExtensionsV1.Create([value.ToClause(SemanticExtensionRequirement.Mandatory)]);

    private static OperationSemanticRequirementsV1 RefinableRequirements() => new(
        Advisory(IsolationClassV1.DomainSeparated),
        Required(PublicationEnforcementClassV1.StagedWithholdingUntilDecision),
        Advisory(ReplayClassV1.None), Advisory(DeterminismClassV1.StableOrdering),
        Required(CancellationClassV1.ExactAcknowledgement), Advisory(ContainmentClassV1.None),
        Advisory(LocalityClassV1.Any), Required(ResourceAssuranceV1.EnforcedUpperBound));
    private static SemanticRequirementV1<T> Required<T>(T value) where T : struct, Enum =>
        new(1, SemanticRequirementStrengthV1.Mandatory, value);
    private static SemanticRequirementV1<T> Advisory<T>(T value) where T : struct, Enum =>
        new(1, SemanticRequirementStrengthV1.Advisory, value);

    private sealed class Provider(SemanticExecutionBindingV1 binding) : ISemanticProviderAdmissionService
    {
        public KernelResult<ProviderAdmissionDecisionV1> Revalidate(SemanticExecutionBindingV1 _) =>
            KernelResult<ProviderAdmissionDecisionV1>.Ok(new(1, binding.Digest, binding.ProviderIdentity,
                binding.ProviderGenerationDigest, binding.ProviderRequestCorrelation,
                SemanticGateDecisionStatusV1.Allowed, "v6-temporal-test-provider"));
    }

    private sealed class Runtime(SemanticExecutionBindingV1 binding, Action? beforeDecision = null)
        : IRuntimeLegalityService
    {
        public KernelResult<RuntimeLegalityDecisionV1> Evaluate(SemanticExecutionBindingV1 _)
        {
            beforeDecision?.Invoke();
            return KernelResult<RuntimeLegalityDecisionV1>.Ok(new(1, binding.Digest,
                "v6-temporal-test-runtime", 1, SemanticGateDecisionStatusV1.Allowed,
                "v6-temporal-runtime-evidence"));
        }
    }

    private sealed record Context(RuntimeKernel Kernel, ProcessHandle Principal,
        ExternalOperationHandle Operation, BudgetReservationHandle Lease,
        OperationObligationsV1 Obligations, ExecutionGuaranteesV1 Guarantees,
        SemanticExecutionBindingV1 Binding, V6TemporalSemanticBindingV1 TemporalBinding,
        SemanticRefinementProofV1 Refinement, Hc.ExternalGenerationSet ProviderGenerations,
        OperationDependencySnapshot Dependencies, ResourceAdmissionCommit Commit);

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private const string ProviderIdentity = "hybridcpu:external-runtime";
}
