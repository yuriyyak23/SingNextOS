using System.Text;
using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Tests.Contracts;

namespace SingPlus.Tests.Runtime;

public sealed class V6TemporalRuntimeEnforcementTests
{
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

    private static KernelResult<OperationBinding> Submit(Context context, Func<ulong> generation,
        IRuntimeLegalityService runtime, Func<KernelResult> callback) =>
        context.Kernel.SubmitV6TemporalSemanticResourceExternalAdmission(context.Commit,
            context.Dependencies, context.Binding, context.TemporalBinding, context.Obligations,
            context.Guarantees, context.Refinement, ProviderIdentity, generation,
            context.ProviderGenerations, new Provider(context.Binding), runtime, callback);

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
