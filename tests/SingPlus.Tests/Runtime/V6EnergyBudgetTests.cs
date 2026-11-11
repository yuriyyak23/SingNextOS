using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class V6EnergyBudgetTests
{
    [Fact]
    public void EnergyReservationSettlementConservesAndChargesActualUsageExactlyOnce()
    {
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle account) = Create(100);
        BudgetReservationHandle lease = authority.Reserve(process, [Amount(100)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(authority.BindLease(process, lease).IsSuccess);
        Assert.True(authority.BeginConsumption(process, lease).IsSuccess);

        BudgetReservationSnapshot first = authority.SettleLease(process, lease, [Amount(60)]).Value!;
        BudgetReservationSnapshot replay = authority.SettleLease(process, lease, [Amount(90)]).Value!;

        BudgetSnapshotAssertions.Equal(first, replay);
        Assert.Equal(60UL, Used(authority.Query(account).Value!));
        Assert.Equal([Amount(60)], first.ChargedAmounts);
    }

    [Fact]
    public void EnergyPreSubmitCancellationRefundsReservationWhilePossibleEffectPinsIt()
    {
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle account) = Create(100);
        BudgetReservationHandle cancelled = authority.Reserve(process, [Amount(40)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(authority.CancelLeasePreSubmit(process, cancelled).IsSuccess);
        Assert.Equal(0UL, Used(authority.Query(account).Value!));

        BudgetReservationHandle possible = authority.Reserve(process, [Amount(40)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(authority.BindLease(process, possible).IsSuccess);
        Assert.True(authority.BeginConsumption(process, possible).IsSuccess);
        Assert.Equal(KernelError.InvalidTransition, authority.Release(process, possible).Error);
        Assert.Equal(40UL, Used(authority.Query(account).Value!));
    }

    [Fact]
    public void ManagedProviderMeasurementSettlesExactOperationThroughExistingLedger()
    {
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle account) = Create(100);
        BudgetReservationHandle lease = BoundEnergyLease(authority, process, 100);
        V6ManagedEnergyCounterProvider provider = new V6ManagedEnergyCounterProvider();
        V6EnergyOperationBinding operation = V6EnergyOperationSettlement.Begin(authority, process, lease,
            "operation:managed:1", 100, provider).Value!;

        KernelResult<V6EnergySettlementReceipt> result = V6EnergyOperationSettlement.CompleteAndSettle(authority, process, operation,
            provider, 61, ThermalStateV1.Throttled, DvfsStateV1.Dynamic);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(EnergyEvidenceClaimV1.MeasurementOnly, result.Value!.Evidence.Claim);
        Assert.Equal(EnergyEvidenceAssuranceV1.ModelOnly, result.Value.Evidence.Assurance);
        Assert.Equal(V6ManagedEnergyCounterProvider.Identity, result.Value.Evidence.ProviderIdentity);
        Assert.Equal(61UL, Used(authority.Query(account).Value!));
        Assert.Equal(BudgetReservationState.Released, result.Value.Settlement.State);
        Assert.False(result.Value.AuthorizesExecution);
    }

    [Fact]
    public void CounterOrProviderResetInvalidatesActiveOperationAndPinsReservation()
    {
        foreach (bool resetProvider in new[] { false, true })
        {
            (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle account) = Create(100);
            BudgetReservationHandle lease = BoundEnergyLease(authority, process, 80);
            V6ManagedEnergyCounterProvider provider = new V6ManagedEnergyCounterProvider();
            V6EnergyOperationBinding operation = V6EnergyOperationSettlement.Begin(authority, process, lease,
                $"operation:reset:{resetProvider}", 80, provider).Value!;
            Assert.True(resetProvider ? provider.ResetProvider().IsSuccess : provider.ResetCounter().IsSuccess);

            KernelResult<V6EnergySettlementReceipt> result = V6EnergyOperationSettlement.CompleteAndSettle(authority, process, operation,
                provider, 20, ThermalStateV1.Nominal, DvfsStateV1.Fixed);

            Assert.Equal(KernelError.StaleGeneration, result.Error);
            Assert.Equal(BudgetReservationState.Quarantined, authority.Query(lease).Value!.State);
            Assert.Equal(80UL, Used(authority.Query(account).Value!));
        }
    }

    [Fact]
    public void ProviderResetDuringCompletionCannotSettleOldEnergyEvidence()
    {
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle account) = Create(100);
        BudgetReservationHandle lease = BoundEnergyLease(authority, process, 80);
        ResetDuringCompletionProvider provider = new ResetDuringCompletionProvider();
        V6EnergyOperationBinding operation = V6EnergyOperationSettlement.Begin(authority, process, lease,
            "operation:reset-during-complete", 80, provider).Value!;

        KernelResult<V6EnergySettlementReceipt> result = V6EnergyOperationSettlement.CompleteAndSettle(authority, process,
            operation, provider, 20, ThermalStateV1.Nominal, DvfsStateV1.Fixed);

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(BudgetReservationState.Quarantined, authority.Query(lease).Value!.State);
        Assert.Equal(80UL, Used(authority.Query(account).Value!));
    }

    [Fact]
    public void ResetObservedAfterTerminalChargeCannotProduceEnergyReceipt()
    {
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle account) = Create(100);
        BudgetReservationHandle lease = BoundEnergyLease(authority, process, 80);
        ResetAtFinalGenerationReadProvider provider = new ResetAtFinalGenerationReadProvider();
        V6EnergyOperationBinding operation = V6EnergyOperationSettlement.Begin(authority, process, lease,
            "operation:reset-after-charge", 80, provider).Value!;
        provider.Arm();

        KernelResult<V6EnergySettlementReceipt> result = V6EnergyOperationSettlement.CompleteAndSettle(authority, process,
            operation, provider, 20, ThermalStateV1.Nominal, DvfsStateV1.Fixed);

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(BudgetReservationState.Released, authority.Query(lease).Value!.State);
        Assert.Equal([Amount(20)], authority.Query(lease).Value!.ChargedAmounts);
        Assert.Equal(20UL, Used(authority.Query(account).Value!));
    }

    [Fact]
    public void IndependentTerminalBudgetChargeCannotBecomeEnergySettlementReceipt()
    {
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle account) = Create(100);
        BudgetReservationHandle lease = BoundEnergyLease(authority, process, 80);
        V6ManagedEnergyCounterProvider provider = new V6ManagedEnergyCounterProvider();
        V6EnergyOperationBinding operation = V6EnergyOperationSettlement.Begin(authority, process, lease,
            "operation:terminal-charge", 80, provider).Value!;
        Assert.True(authority.SettleLease(process, lease, [Amount(10)]).IsSuccess);

        KernelResult<V6EnergySettlementReceipt> result = V6EnergyOperationSettlement.CompleteAndSettle(authority, process,
            operation, provider, 20, ThermalStateV1.Nominal, DvfsStateV1.Fixed);

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(10UL, Used(authority.Query(account).Value!));
        Assert.Equal([Amount(10)], authority.Query(lease).Value!.ChargedAmounts);
    }

    [Fact]
    public void BudgetChargeDuringCompletionCannotBeRelabeledAsProviderMeasurement()
    {
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle account) = Create(100);
        BudgetReservationHandle lease = BoundEnergyLease(authority, process, 80);
        BudgetSettlementDuringCompletionProvider provider = new BudgetSettlementDuringCompletionProvider(authority, process, lease);
        V6EnergyOperationBinding operation = V6EnergyOperationSettlement.Begin(authority, process, lease,
            "operation:concurrent-charge", 80, provider).Value!;

        KernelResult<V6EnergySettlementReceipt> result = V6EnergyOperationSettlement.CompleteAndSettle(authority, process,
            operation, provider, 20, ThermalStateV1.Nominal, DvfsStateV1.Fixed);

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(10UL, Used(authority.Query(account).Value!));
        Assert.Equal([Amount(10)], authority.Query(lease).Value!.ChargedAmounts);
    }

    [Fact]
    public void MeasurementBeyondEnvelopeQuarantinesInsteadOfUndercharging()
    {
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle account) = Create(100);
        BudgetReservationHandle lease = BoundEnergyLease(authority, process, 50);
        V6ManagedEnergyCounterProvider provider = new V6ManagedEnergyCounterProvider();
        V6EnergyOperationBinding operation = V6EnergyOperationSettlement.Begin(authority, process, lease,
            "operation:over-envelope", 50, provider).Value!;

        KernelResult<V6EnergySettlementReceipt> result = V6EnergyOperationSettlement.CompleteAndSettle(authority, process, operation,
            provider, 51, ThermalStateV1.Nominal, DvfsStateV1.Dynamic);

        Assert.Equal(KernelError.BudgetExceeded, result.Error);
        Assert.Equal(BudgetReservationState.Quarantined, authority.Query(lease).Value!.State);
        Assert.Equal(50UL, Used(authority.Query(account).Value!));
    }

    [Fact]
    public void CounterRolloverFailsWithoutWrappingOrSettling()
    {
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle account) = Create(100);
        BudgetReservationHandle lease = BoundEnergyLease(authority, process, 20);
        V6ManagedEnergyCounterProvider provider = new V6ManagedEnergyCounterProvider();
        provider.SetCounterForTest(ulong.MaxValue - 4);
        V6EnergyOperationBinding operation = V6EnergyOperationSettlement.Begin(authority, process, lease,
            "operation:rollover", 20, provider).Value!;

        KernelResult<V6EnergySettlementReceipt> result = V6EnergyOperationSettlement.CompleteAndSettle(authority, process, operation,
            provider, 5, ThermalStateV1.Nominal, DvfsStateV1.Fixed);

        Assert.Equal(KernelError.CapacityExhausted, result.Error);
        Assert.Equal(BudgetReservationState.Quarantined, authority.Query(lease).Value!.State);
        Assert.Equal(20UL, Used(authority.Query(account).Value!));
    }

    [Fact]
    public void CompletedTokenReplayCannotDoubleCharge()
    {
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle account) = Create(100);
        BudgetReservationHandle lease = BoundEnergyLease(authority, process, 70);
        V6ManagedEnergyCounterProvider provider = new V6ManagedEnergyCounterProvider();
        V6EnergyOperationBinding operation = V6EnergyOperationSettlement.Begin(authority, process, lease,
            "operation:replay", 70, provider).Value!;
        Assert.True(V6EnergyOperationSettlement.CompleteAndSettle(authority, process, operation,
            provider, 30, ThermalStateV1.Nominal, DvfsStateV1.Fixed).IsSuccess);

        KernelResult<V6EnergySettlementReceipt> replay = V6EnergyOperationSettlement.CompleteAndSettle(authority, process, operation,
            provider, 30, ThermalStateV1.Nominal, DvfsStateV1.Fixed);

        Assert.Equal(KernelError.StaleGeneration, replay.Error);
        Assert.Equal(30UL, Used(authority.Query(account).Value!));
        Assert.Equal(BudgetReservationState.Released, authority.Query(lease).Value!.State);
    }

    [Fact]
    public void MalformedProviderTokenAndEvidenceFailAtTheirExactLifecycleBoundary()
    {
        (ResourceBudgetAuthority? firstAuthority, ProcessHandle firstProcess, BudgetAccountHandle _) = Create(100);
        BudgetReservationHandle firstLease = BoundEnergyLease(firstAuthority, firstProcess, 40);
        MalformedEnergyProvider malformedToken = new MalformedEnergyProvider(true);
        Assert.Equal(KernelError.PlatformFaulted,
            V6EnergyOperationSettlement.Begin(firstAuthority, firstProcess, firstLease,
                "operation:bad-token", 40, malformedToken).Error);
        Assert.Equal(1, malformedToken.CancelCalls);
        Assert.Equal(BudgetReservationState.Bound, firstAuthority.Query(firstLease).Value!.State);

        (ResourceBudgetAuthority? secondAuthority, ProcessHandle secondProcess, BudgetAccountHandle _) = Create(100);
        BudgetReservationHandle secondLease = BoundEnergyLease(secondAuthority, secondProcess, 40);
        MalformedEnergyProvider malformedEvidence = new MalformedEnergyProvider(false);
        V6EnergyOperationBinding operation = V6EnergyOperationSettlement.Begin(secondAuthority, secondProcess, secondLease,
            "operation:bad-evidence", 40, malformedEvidence).Value!;
        Assert.Equal(KernelError.PlatformFaulted,
            V6EnergyOperationSettlement.CompleteAndSettle(secondAuthority, secondProcess, operation,
                malformedEvidence, 10, ThermalStateV1.Nominal, DvfsStateV1.Fixed).Error);
        Assert.Equal(BudgetReservationState.Quarantined, secondAuthority.Query(secondLease).Value!.State);
    }

    [Fact]
    public void ManagedCapProviderEnforcesExactAdmittedUpperBoundAndSettlesLedger()
    {
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle account) = Create(100);
        BudgetReservationHandle lease = BoundEnergyLease(authority, process, 80);
        V6ManagedEnergyCapProvider provider = new V6ManagedEnergyCapProvider();
        V6EnergyOperationBinding operation = V6EnergyOperationSettlement.BeginEnforced(authority, process, lease,
            "operation:enforced:1", 80, provider).Value!;

        KernelResult<V6EnergySettlementReceipt> result = V6EnergyOperationSettlement.CompleteAndSettleEnforced(authority, process,
            operation, provider, 63, ThermalStateV1.Throttled, DvfsStateV1.Dynamic);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(EnergyEvidenceClaimV1.EnforcedUpperBound, result.Value!.Evidence.Claim);
        Assert.Equal(EnergyEvidenceAssuranceV1.ModelOnly, result.Value.Evidence.Assurance);
        Assert.Equal(80UL, result.Value.Evidence.EnforcedUpperBoundMicrojoules);
        Assert.Equal(V6ManagedEnergyCapProvider.Mechanism, result.Value.Evidence.EnforcementMechanism);
        Assert.Equal(EnergyEvidenceMatchCodeV1.Exact,
            EnergyEvidenceMatcherV1.Match(result.Value.Binding, result.Value.Evidence));
        Assert.Equal(63UL, Used(authority.Query(account).Value!));
        Assert.False(result.Value.AuthorizesExecution);
        Assert.False(result.Value.Evidence.GrantsBudgetAuthority);
        Assert.False(result.Value.Evidence.GuaranteesDeadline);
    }

    [Fact]
    public void ManagedCapViolationFailsClosedAndPinsFullReservation()
    {
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle account) = Create(100);
        BudgetReservationHandle lease = BoundEnergyLease(authority, process, 40);
        V6ManagedEnergyCapProvider provider = new V6ManagedEnergyCapProvider();
        V6EnergyOperationBinding operation = V6EnergyOperationSettlement.BeginEnforced(authority, process, lease,
            "operation:cap-violation", 40, provider).Value!;

        KernelResult<V6EnergySettlementReceipt> result = V6EnergyOperationSettlement.CompleteAndSettleEnforced(authority, process,
            operation, provider, 41, ThermalStateV1.Emergency, DvfsStateV1.Dynamic);

        Assert.Equal(KernelError.BudgetExceeded, result.Error);
        Assert.Equal(BudgetReservationState.Quarantined, authority.Query(lease).Value!.State);
        Assert.Equal(40UL, Used(authority.Query(account).Value!));
        Assert.Equal(KernelError.StaleGeneration,
            provider.Complete(operation.CounterToken, 1,
                ThermalStateV1.Nominal, DvfsStateV1.Fixed).Error);
    }

    [Fact]
    public void ManagedCapProviderResetInvalidatesOperationBeforeSettlement()
    {
        (ResourceBudgetAuthority? authority, ProcessHandle process, BudgetAccountHandle account) = Create(100);
        BudgetReservationHandle lease = BoundEnergyLease(authority, process, 70);
        V6ManagedEnergyCapProvider provider = new V6ManagedEnergyCapProvider();
        V6EnergyOperationBinding operation = V6EnergyOperationSettlement.BeginEnforced(authority, process, lease,
            "operation:cap-reset", 70, provider).Value!;
        Assert.True(provider.ResetProvider().IsSuccess);

        KernelResult<V6EnergySettlementReceipt> result = V6EnergyOperationSettlement.CompleteAndSettleEnforced(authority, process,
            operation, provider, 10, ThermalStateV1.Nominal, DvfsStateV1.Fixed);

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(BudgetReservationState.Quarantined, authority.Query(lease).Value!.State);
        Assert.Equal(70UL, Used(authority.Query(account).Value!));
    }

    private static BudgetReservationHandle BoundEnergyLease(
        ResourceBudgetAuthority authority, ProcessHandle process, ulong amount)
    {
        BudgetReservationHandle lease = authority.Reserve(process, [Amount(amount)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(authority.BindLease(process, lease).IsSuccess);
        return lease;
    }

    private static (ResourceBudgetAuthority Authority, ProcessHandle Process, BudgetAccountHandle Account)
        Create(ulong limit)
    {
        ResourceBudgetAuthority authority = new ResourceBudgetAuthority();
        Assert.True(authority.ConfigureSystem([Amount(limit)]).IsSuccess);
        BudgetAccountHandle service = authority.CreateChild(authority.SystemBudget, BudgetAccountLevel.Service,
            "energy-service", [Amount(limit)]).Value!.Account;
        BudgetAccountHandle account = authority.CreateChild(service, BudgetAccountLevel.ProcessDomain,
            "energy-process", [Amount(limit)]).Value!.Account;
        ProcessHandle process = new ProcessHandle(new(777), 3);
        Assert.True(authority.AttachProcess(process, account).IsSuccess);
        return (authority, process, account);
    }

    private static BudgetAmount Amount(ulong amount) =>
        new(ServiceBudgetDimension.EnergyMicrojoules, amount);

    private static ulong Used(BudgetAccountSnapshot account) =>
        Assert.Single(account.Usage,
            usage => usage.Dimension == ServiceBudgetDimension.EnergyMicrojoules).Used;

    private sealed class MalformedEnergyProvider(bool malformedToken) : IV6EnergyCounterProvider
    {
        public string ProviderIdentity => "test-provider";
        public ulong ProviderGeneration => 1;
        public ulong CounterGeneration => 1;
        public int CancelCalls { get; private set; }

        public KernelResult<V6EnergyCounterToken> Begin(string operationCorrelation) =>
            KernelResult<V6EnergyCounterToken>.Ok(new(
                malformedToken ? "wrong-provider" : ProviderIdentity,
                operationCorrelation, 1, 1, 100));

        public KernelResult Cancel(V6EnergyCounterToken token)
        {
            CancelCalls++;
            return KernelResult.Ok();
        }

        public KernelResult<ProviderEnergyEvidenceV1> Complete(
            V6EnergyCounterToken token, ulong measuredMicrojoules,
            ThermalStateV1 thermalState, DvfsStateV1 dvfsState) =>
            KernelResult<ProviderEnergyEvidenceV1>.Ok(new(1, ProviderIdentity, token.OperationCorrelation,
                EnergyEvidenceClaimV1.MeasurementOnly, EnergyEvidenceAssuranceV1.ModelOnly,
                1, 1, 1, token.CounterStart, token.CounterStart + measuredMicrojoules + 1,
                measuredMicrojoules, 0, 0, 1_000_000, thermalState, dvfsState, string.Empty));
    }

    private sealed class ResetDuringCompletionProvider : IV6EnergyCounterProvider
    {
        private readonly V6ManagedEnergyCounterProvider inner = new();
        public string ProviderIdentity => inner.ProviderIdentity;
        public ulong ProviderGeneration => inner.ProviderGeneration;
        public ulong CounterGeneration => inner.CounterGeneration;
        public KernelResult<V6EnergyCounterToken> Begin(string correlation) => inner.Begin(correlation);
        public KernelResult Cancel(V6EnergyCounterToken token) => inner.Cancel(token);
        public KernelResult<ProviderEnergyEvidenceV1> Complete(V6EnergyCounterToken token,
            ulong measuredMicrojoules, ThermalStateV1 thermalState, DvfsStateV1 dvfsState)
        {
            KernelResult<ProviderEnergyEvidenceV1> evidence = inner.Complete(token, measuredMicrojoules, thermalState, dvfsState);
            Assert.True(inner.ResetProvider().IsSuccess);
            return evidence;
        }
    }

    private sealed class ResetAtFinalGenerationReadProvider : IV6EnergyCounterProvider
    {
        private readonly V6ManagedEnergyCounterProvider inner = new();
        private bool armed;
        private int generationReads;
        public string ProviderIdentity => inner.ProviderIdentity;
        public ulong ProviderGeneration
        {
            get
            {
                if (armed && ++generationReads == 3)
                    Assert.True(inner.ResetProvider().IsSuccess);
                return inner.ProviderGeneration;
            }
        }
        public ulong CounterGeneration => inner.CounterGeneration;
        public void Arm() { armed = true; generationReads = 0; }
        public KernelResult<V6EnergyCounterToken> Begin(string correlation) => inner.Begin(correlation);
        public KernelResult Cancel(V6EnergyCounterToken token) => inner.Cancel(token);
        public KernelResult<ProviderEnergyEvidenceV1> Complete(V6EnergyCounterToken token,
            ulong measuredMicrojoules, ThermalStateV1 thermalState, DvfsStateV1 dvfsState) =>
            inner.Complete(token, measuredMicrojoules, thermalState, dvfsState);
    }

    private sealed class BudgetSettlementDuringCompletionProvider(
        ResourceBudgetAuthority budgets, ProcessHandle owner, BudgetReservationHandle reservation)
        : IV6EnergyCounterProvider
    {
        private readonly V6ManagedEnergyCounterProvider inner = new();
        public string ProviderIdentity => inner.ProviderIdentity;
        public ulong ProviderGeneration => inner.ProviderGeneration;
        public ulong CounterGeneration => inner.CounterGeneration;
        public KernelResult<V6EnergyCounterToken> Begin(string correlation) => inner.Begin(correlation);
        public KernelResult Cancel(V6EnergyCounterToken token) => inner.Cancel(token);
        public KernelResult<ProviderEnergyEvidenceV1> Complete(V6EnergyCounterToken token,
            ulong measuredMicrojoules, ThermalStateV1 thermalState, DvfsStateV1 dvfsState)
        {
            KernelResult<ProviderEnergyEvidenceV1> evidence = inner.Complete(token, measuredMicrojoules, thermalState, dvfsState);
            Assert.True(budgets.SettleLease(owner, reservation, [Amount(10)]).IsSuccess);
            return evidence;
        }
    }
}
