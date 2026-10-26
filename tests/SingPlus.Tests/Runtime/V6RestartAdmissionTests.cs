using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class V6RestartAdmissionTests
{
    private static readonly OperationDependencySnapshot Dependencies = new(7, 11, 13, 17);
    private static readonly PreemptionGuaranteeV1 RestartOnly = new(1,
        PreemptionClassV1.RestartOnly, PreemptionEffectSemanticsV1.RequestOnly, 0, false);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ZeroGenerationCopiedBindingCannotSubmit(bool zeroProvider)
    {
        Scenario scenario = CreateScenario();
        OperationPreparation predecessor = CreateClosedPredecessor(scenario);
        OperationPreparation replacement = PrepareAndAdmit(scenario);
        V6RestartBindingV1 valid = scenario.Kernel.CreateV6RestartBinding(scenario.Owner, predecessor.Operation,
            replacement.Operation, Dependencies, RestartOnly, 19, 23).Value!;
        V6RestartBindingV1 binding = zeroProvider
            ? valid with { ProviderGeneration = 0 }
            : valid with { RuntimeGeneration = 0 };
        int callbacks = 0;
        KernelResult<RestartAdmissionReceiptV1> submitted = scenario.Kernel.SubmitV6Restart(scenario.Owner, binding,
            () => { callbacks++; return KernelResult.Ok(); }, KernelResult.Ok, KernelResult.Ok,
            () => binding.ProviderGeneration, () => binding.RuntimeGeneration,
            () => { callbacks++; return KernelResult.Ok(); });
        Assert.Equal(KernelError.InvalidMessage, submitted.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(ExternalOperationState.Admitted,
            scenario.Kernel.QueryExternalOperation(scenario.Owner, replacement.Operation).Value!.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TerminalDependencyGenerationCannotCreateOrSubmitRestart(bool providerTerminal)
    {
        Scenario scenario = CreateScenario();
        OperationPreparation predecessor = CreateClosedPredecessor(scenario);
        OperationPreparation replacement = PrepareAndAdmit(scenario);
        ulong providerGeneration = providerTerminal ? ulong.MaxValue : 19UL;
        ulong runtimeGeneration = providerTerminal ? 23UL : ulong.MaxValue;
        KernelResult<V6RestartBindingV1> rejected = scenario.Kernel.CreateV6RestartBinding(scenario.Owner, predecessor.Operation,
            replacement.Operation, Dependencies, RestartOnly, providerGeneration, runtimeGeneration);
        Assert.Equal(KernelError.CapacityExhausted, rejected.Error);

        V6RestartBindingV1 valid = scenario.Kernel.CreateV6RestartBinding(scenario.Owner, predecessor.Operation,
            replacement.Operation, Dependencies, RestartOnly, 19, 23).Value!;
        V6RestartBindingV1 binding = providerTerminal
            ? valid with { ProviderGeneration = ulong.MaxValue }
            : valid with { RuntimeGeneration = ulong.MaxValue };
        int callbacks = 0;
        KernelResult<RestartAdmissionReceiptV1> submitted = scenario.Kernel.SubmitV6Restart(scenario.Owner, binding,
            () => { callbacks++; return KernelResult.Ok(); }, KernelResult.Ok, KernelResult.Ok,
            () => providerGeneration, () => runtimeGeneration,
            () => { callbacks++; return KernelResult.Ok(); });
        Assert.Equal(KernelError.CapacityExhausted, submitted.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(ExternalOperationState.Admitted,
            scenario.Kernel.QueryExternalOperation(scenario.Owner, replacement.Operation).Value!.State);
    }

    [Fact]
    public void ClosedProviderLostPredecessorCanRestartThroughAllFreshAdmissionSentries()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation predecessor = CreateClosedPredecessor(scenario);
        OperationPreparation replacement = PrepareAndAdmit(scenario);
        KernelResult<V6RestartBindingV1> binding = scenario.Kernel.CreateV6RestartBinding(scenario.Owner, predecessor.Operation,
            replacement.Operation, Dependencies, RestartOnly, 19, 23);
        Assert.True(binding.IsSuccess, binding.Message);
        List<string> sentries = new List<string>();
        int providerSubmits = 0;

        KernelResult<RestartAdmissionReceiptV1> result = scenario.Kernel.SubmitV6Restart(scenario.Owner, binding.Value!,
            () => { sentries.Add("SingNext"); return KernelResult.Ok(); },
            () => { sentries.Add("Provider"); return KernelResult.Ok(); },
            () => { sentries.Add("Runtime"); return KernelResult.Ok(); },
            () => 19, () => 23,
            () => { providerSubmits++; return KernelResult.Ok(); });

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(["SingNext", "Provider", "Runtime", "SingNext", "Provider"], sentries);
        Assert.Equal(1, providerSubmits);
        Assert.False(result.Value!.AuthorizesExecution);
        Assert.False(result.Value.ProvesPredecessorClosure);
        Assert.True(PreemptionLifecycleValidatorV1.IsValidRestartOnly(result.Value.Lifecycle));
        Assert.Equal(ExternalOperationState.Submitted,
            scenario.Kernel.QueryExternalOperation(scenario.Owner, replacement.Operation).Value!.State);
    }

    [Fact]
    public void FailedFreshAdmissionLeavesReplacementUnsubmitted()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation predecessor = CreateClosedPredecessor(scenario);
        OperationPreparation replacement = PrepareAndAdmit(scenario);
        V6RestartBindingV1 binding = scenario.Kernel.CreateV6RestartBinding(scenario.Owner, predecessor.Operation,
            replacement.Operation, Dependencies, RestartOnly, 19, 23).Value!;
        int providerSubmits = 0;

        KernelResult<RestartAdmissionReceiptV1> result = scenario.Kernel.SubmitV6Restart(scenario.Owner, binding,
            KernelResult.Ok,
            () => KernelResult.Fail(KernelError.PlatformDenied, "provider policy changed"),
            KernelResult.Ok, () => 19, () => 23,
            () => { providerSubmits++; return KernelResult.Ok(); });

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, providerSubmits);
        Assert.Equal(ExternalOperationState.Admitted,
            scenario.Kernel.QueryExternalOperation(scenario.Owner, replacement.Operation).Value!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegalityRevokesEarlierAdmissionBeforeReplacementSubmit(bool providerRevoked)
    {
        Scenario scenario = CreateScenario();
        OperationPreparation predecessor = CreateClosedPredecessor(scenario);
        OperationPreparation replacement = PrepareAndAdmit(scenario);
        V6RestartBindingV1 binding = scenario.Kernel.CreateV6RestartBinding(scenario.Owner, predecessor.Operation,
            replacement.Operation, Dependencies, RestartOnly, 19, 23).Value!;
        bool singNextAllowed = true;
        bool providerAllowed = true;
        int callbacks = 0;

        KernelResult<RestartAdmissionReceiptV1> result = scenario.Kernel.SubmitV6Restart(scenario.Owner, binding,
            () => singNextAllowed ? KernelResult.Ok() : KernelResult.Fail(KernelError.CapabilityRevoked, "revoked"),
            () => providerAllowed ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformDenied, "revoked"),
            () =>
            {
                if (providerRevoked) providerAllowed = false;
                else singNextAllowed = false;
                return KernelResult.Ok();
            },
            () => 19, () => 23,
            () => { callbacks++; return KernelResult.Ok(); });

        Assert.Equal(providerRevoked ? KernelError.PlatformDenied : KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(ExternalOperationState.Admitted,
            scenario.Kernel.QueryExternalOperation(scenario.Owner, replacement.Operation).Value!.State);
    }

    [Fact]
    public void GenerationDriftAtFinalSentryFailsClosedBeforeReplacementSubmit()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation predecessor = CreateClosedPredecessor(scenario);
        OperationPreparation replacement = PrepareAndAdmit(scenario);
        V6RestartBindingV1 binding = scenario.Kernel.CreateV6RestartBinding(scenario.Owner, predecessor.Operation,
            replacement.Operation, Dependencies, RestartOnly, 19, 23).Value!;
        ulong providerGeneration = 19;

        KernelResult<RestartAdmissionReceiptV1> result = scenario.Kernel.SubmitV6Restart(scenario.Owner, binding,
            KernelResult.Ok, KernelResult.Ok,
            () => { providerGeneration++; return KernelResult.Ok(); },
            () => providerGeneration, () => 23, KernelResult.Ok);

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(ExternalOperationState.Admitted,
            scenario.Kernel.QueryExternalOperation(scenario.Owner, replacement.Operation).Value!.State);
    }

    [Fact]
    public void CancellationRequestIsNotContainmentAndCannotAuthorizeRestart()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation predecessor = PrepareAndAdmit(scenario);
        _ = scenario.Kernel.RecordExternalOperationSubmission(
            scenario.Owner, predecessor.Operation, Dependencies).Value!;
        Assert.True(scenario.Kernel.CancelExternalOperation(
            scenario.Owner, predecessor.Operation, providerCancellationSupported: true).IsSuccess);
        OperationPreparation replacement = PrepareAndAdmitWithFreshBuffers(scenario);

        KernelResult<V6RestartBindingV1> binding = scenario.Kernel.CreateV6RestartBinding(scenario.Owner, predecessor.Operation,
            replacement.Operation, Dependencies, RestartOnly, 19, 23);

        Assert.False(binding.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, binding.Error);
    }

    [Fact]
    public void ProviderSubmitFailureTransitionsReplacementToProviderLostWithoutSuccessReceipt()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation predecessor = CreateClosedPredecessor(scenario);
        OperationPreparation replacement = PrepareAndAdmit(scenario);
        V6RestartBindingV1 binding = scenario.Kernel.CreateV6RestartBinding(scenario.Owner, predecessor.Operation,
            replacement.Operation, Dependencies, RestartOnly, 19, 23).Value!;

        KernelResult<RestartAdmissionReceiptV1> result = scenario.Kernel.SubmitV6Restart(scenario.Owner, binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, () => 19, () => 23,
            () => KernelResult.Fail(KernelError.PlatformUnavailable, "provider reset"));

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformUnavailable, result.Error);
        ExternalOperationSnapshot snapshot = scenario.Kernel.QueryExternalOperation(scenario.Owner, replacement.Operation).Value!;
        Assert.Equal(ExternalOperationDisposition.ProviderLost, snapshot.Disposition);
        Assert.DoesNotContain(snapshot.Transitions, transition => transition.Event == "Published");
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("runtime")]
    [InlineData("observation-throws")]
    public void PostSubmitGenerationDriftOrObservationFailureCannotIssueRestartReceipt(string fault)
    {
        Scenario scenario = CreateScenario();
        OperationPreparation predecessor = CreateClosedPredecessor(scenario);
        OperationPreparation replacement = PrepareAndAdmit(scenario);
        V6RestartBindingV1 binding = scenario.Kernel.CreateV6RestartBinding(scenario.Owner, predecessor.Operation,
            replacement.Operation, Dependencies, RestartOnly, 19, 23, scenario.RestartBudget).Value!;
        ulong providerGeneration = 19;
        ulong runtimeGeneration = 23;
        bool callbackEntered = false;

        KernelResult<RestartAdmissionReceiptV1> result = scenario.Kernel.SubmitV6Restart(scenario.Owner, binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => callbackEntered && fault == "observation-throws"
                ? throw new InvalidOperationException("generation source unavailable")
                : providerGeneration,
            () => runtimeGeneration,
            () =>
            {
                callbackEntered = true;
                if (fault == "provider") providerGeneration++;
                if (fault == "runtime") runtimeGeneration++;
                return KernelResult.Ok();
            });

        Assert.True(callbackEntered);
        Assert.Equal(fault == "observation-throws" ? KernelError.PlatformUnavailable : KernelError.StaleGeneration,
            result.Error);
        Assert.Equal(ExternalOperationDisposition.ProviderLost,
            scenario.Kernel.QueryExternalOperation(scenario.Owner, replacement.Operation).Value!.Disposition);
        Assert.Equal(BudgetReservationState.Quarantined,
            scenario.Kernel.Budgets.Query(scenario.RestartBudget).Value!.State);
    }

    [Fact]
    public void RestartBindingCannotSubmitTheReplacementOrProviderEffectTwice()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation predecessor = CreateClosedPredecessor(scenario);
        OperationPreparation replacement = PrepareAndAdmit(scenario);
        V6RestartBindingV1 binding = scenario.Kernel.CreateV6RestartBinding(scenario.Owner, predecessor.Operation,
            replacement.Operation, Dependencies, RestartOnly, 19, 23).Value!;
        int providerSubmits = 0;
        KernelResult ProviderSubmit() { providerSubmits++; return KernelResult.Ok(); }

        KernelResult<RestartAdmissionReceiptV1> first = scenario.Kernel.SubmitV6Restart(scenario.Owner, binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, () => 19, () => 23, ProviderSubmit);
        KernelResult<RestartAdmissionReceiptV1> second = scenario.Kernel.SubmitV6Restart(scenario.Owner, binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, () => 19, () => 23, ProviderSubmit);

        Assert.True(first.IsSuccess, first.Message);
        Assert.False(second.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, second.Error);
        Assert.Equal(1, providerSubmits);
    }

    [Fact]
    public void RestartConsumesFreshReplacementBudgetAndSettlesThroughExistingAuthority()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation predecessor = CreateClosedPredecessor(scenario);
        OperationPreparation replacement = PrepareAndAdmit(scenario);
        V6RestartBindingV1 binding = scenario.Kernel.CreateV6RestartBinding(scenario.Owner, predecessor.Operation,
            replacement.Operation, Dependencies, RestartOnly, 19, 23, scenario.RestartBudget).Value!;

        KernelResult<RestartAdmissionReceiptV1> result = scenario.Kernel.SubmitV6Restart(scenario.Owner, binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, () => 19, () => 23, KernelResult.Ok);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(scenario.RestartBudget, result.Value!.ReplacementBudget);
        Assert.Equal(BudgetReservationState.Consuming, result.Value.ReplacementBudgetState);
        Assert.False(result.Value.AuthorizesBudgetConsumption);
        Assert.Equal(BudgetReservationState.Consuming,
            scenario.Kernel.Budgets.Query(scenario.RestartBudget).Value!.State);
        KernelResult<BudgetReservationSnapshot> settled = scenario.Kernel.Budgets.SettleLease(scenario.Owner, scenario.RestartBudget,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 7)]);
        Assert.True(settled.IsSuccess, settled.Message);
        Assert.Equal(BudgetReservationState.Released, settled.Value!.State);
        Assert.Equal(7UL, Assert.Single(settled.Value.ChargedAmounts!).Amount);
    }

    [Fact]
    public void FailedFreshAdmissionLeavesReplacementBudgetBoundAndReusable()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation predecessor = CreateClosedPredecessor(scenario);
        OperationPreparation replacement = PrepareAndAdmit(scenario);
        V6RestartBindingV1 binding = scenario.Kernel.CreateV6RestartBinding(scenario.Owner, predecessor.Operation,
            replacement.Operation, Dependencies, RestartOnly, 19, 23, scenario.RestartBudget).Value!;

        KernelResult<RestartAdmissionReceiptV1> result = scenario.Kernel.SubmitV6Restart(scenario.Owner, binding,
            KernelResult.Ok, () => KernelResult.Fail(KernelError.PlatformDenied, "denied"),
            KernelResult.Ok, () => 19, () => 23, KernelResult.Ok);

        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(BudgetReservationState.Bound,
            scenario.Kernel.Budgets.Query(scenario.RestartBudget).Value!.State);
    }

    [Fact]
    public void ProviderLossQuarantinesConsumingRestartBudget()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation predecessor = CreateClosedPredecessor(scenario);
        OperationPreparation replacement = PrepareAndAdmit(scenario);
        V6RestartBindingV1 binding = scenario.Kernel.CreateV6RestartBinding(scenario.Owner, predecessor.Operation,
            replacement.Operation, Dependencies, RestartOnly, 19, 23, scenario.RestartBudget).Value!;

        KernelResult<RestartAdmissionReceiptV1> result = scenario.Kernel.SubmitV6Restart(scenario.Owner, binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, () => 19, () => 23,
            () => KernelResult.Fail(KernelError.PlatformUnavailable, "lost"));

        Assert.Equal(KernelError.PlatformUnavailable, result.Error);
        Assert.Equal(BudgetReservationState.Quarantined,
            scenario.Kernel.Budgets.Query(scenario.RestartBudget).Value!.State);
    }

    [Fact]
    public void BudgetStateDriftAtFinalSentryFailsBeforeReplacementSubmit()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation predecessor = CreateClosedPredecessor(scenario);
        OperationPreparation replacement = PrepareAndAdmit(scenario);
        V6RestartBindingV1 binding = scenario.Kernel.CreateV6RestartBinding(scenario.Owner, predecessor.Operation,
            replacement.Operation, Dependencies, RestartOnly, 19, 23, scenario.RestartBudget).Value!;
        Assert.True(scenario.Kernel.Budgets.CancelLeasePreSubmit(
            scenario.Owner, scenario.RestartBudget).IsSuccess);

        KernelResult<RestartAdmissionReceiptV1> result = scenario.Kernel.SubmitV6Restart(scenario.Owner, binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, () => 19, () => 23, KernelResult.Ok);

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(ExternalOperationState.Admitted,
            scenario.Kernel.QueryExternalOperation(scenario.Owner, replacement.Operation).Value!.State);
    }

    private static OperationPreparation CreateClosedPredecessor(Scenario scenario)
    {
        OperationPreparation predecessor = PrepareAndAdmit(scenario);
        _ = scenario.Kernel.RecordExternalOperationSubmission(
            scenario.Owner, predecessor.Operation, Dependencies).Value!;
        Assert.True(scenario.Kernel.RecordExternalOperationProviderLoss(
            scenario.Owner, predecessor.Operation).IsSuccess);
        Assert.True(scenario.Kernel.ReleaseExternalOperation(
            scenario.Owner, predecessor.Operation, new(true, true)).IsSuccess);
        return predecessor;
    }

    private static OperationPreparation PrepareAndAdmit(Scenario scenario) =>
        PrepareAndAdmit(scenario, scenario.Input.Handle, scenario.Output.Handle);

    private static OperationPreparation PrepareAndAdmitWithFreshBuffers(Scenario scenario)
    {
        OwnedBuffer<byte> input = scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 8).Value!;
        OwnedBuffer<byte> output = scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 8).Value!;
        return PrepareAndAdmit(scenario, input.Handle, output.Handle);
    }

    private static OperationPreparation PrepareAndAdmit(
        Scenario scenario, RegionHandle input, RegionHandle output)
    {
        KernelResult<OperationPreparation> prepared = scenario.Kernel.PrepareExternalOperation(
            scenario.Owner,
            [new(input, RegionUseMode.ReadOnly, new(0, 8)),
             new(output, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence,
            ExternalPublicationPolicy.Staged);
        Assert.True(prepared.IsSuccess, prepared.Message);
        KernelResult<OperationAdmissionSnapshot> admitted = scenario.Kernel.AdmitExternalOperation(
            scenario.Owner, prepared.Value!.Operation, Dependencies);
        Assert.True(admitted.IsSuccess, admitted.Message);
        return prepared.Value;
    }

    private static Scenario CreateScenario()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        OwnedBuffer<byte> input = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        OwnedBuffer<byte> output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        ProcessHandle admin = TestFixtures.Create(kernel, 2, 20).Handle;
        CapabilityId capability = kernel.MintCapability(new(20), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, capability, owner, "v6-restart",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        BudgetReservationHandle budget = kernel.ReserveBudget(owner,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 10)],
            BudgetReservationLifetime.ExternalEffect).Value!.Reservation;
        Assert.True(kernel.Budgets.BindLease(owner, budget).IsSuccess);
        return new(kernel, owner, input, output, budget);
    }

    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Owner,
        OwnedBuffer<byte> Input, OwnedBuffer<byte> Output, BudgetReservationHandle RestartBudget);
}
