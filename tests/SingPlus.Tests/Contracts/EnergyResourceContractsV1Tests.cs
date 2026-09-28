using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class EnergyResourceContractsV1Tests
{
    private static readonly EnergyExecutionBindingV1 Binding = new(1,
        new(new(7), new(3)), "provider:model", "operation:42", 100, 11, 13);
    private static readonly ProviderEnergyEvidenceV1 Enforced = new(1,
        "provider:model", "operation:42", EnergyEvidenceClaimV1.EnforcedUpperBound,
        EnergyEvidenceAssuranceV1.ModelOnly, 11, 13, 17, 1_000, 1_060, 60, 100,
        10_000, 990_000, ThermalStateV1.Throttled, DvfsStateV1.Dynamic,
        "deterministic-model-cap");

    [Fact]
    public void ExactEnforcedEvidenceMatchesReservedEnvelopeWithoutGrantingAuthority()
    {
        Assert.Equal(EnergyEvidenceMatchCodeV1.Exact,
            EnergyEvidenceMatcherV1.Match(Binding, Enforced));
        Assert.False(Enforced.GrantsBudgetAuthority);
        Assert.False(Enforced.AuthorizesExecution);
        Assert.False(Binding.AuthorizesExecution);
        Assert.False(Binding.AuthorizesBudgetSettlement);
    }

    [Fact]
    public void MeasurementCannotBeUpgradedToEnforcedUpperBound()
    {
        var measured = Enforced with
        {
            Claim = EnergyEvidenceClaimV1.MeasurementOnly,
            EnforcedUpperBoundMicrojoules = 0,
            EnforcementMechanism = "",
        };

        Assert.Equal(EnergyEvidenceMatchCodeV1.MeasurementNotEnforcement,
            EnergyEvidenceMatcherV1.Match(Binding, measured));
    }

    [Fact]
    public void CounterResetProviderResetAndReplayAgainstAnotherOperationFailExactMatch()
    {
        Assert.Equal(EnergyEvidenceMatchCodeV1.StaleCounterGeneration,
            EnergyEvidenceMatcherV1.Match(Binding, Enforced with { CounterGeneration = 14 }));
        Assert.Equal(EnergyEvidenceMatchCodeV1.StaleProviderGeneration,
            EnergyEvidenceMatcherV1.Match(Binding, Enforced with { ProviderGeneration = 12 }));
        Assert.Equal(EnergyEvidenceMatchCodeV1.WrongProvider,
            EnergyEvidenceMatcherV1.Match(Binding, Enforced with { ProviderIdentity = "provider:other" }));
        Assert.Equal(EnergyEvidenceMatchCodeV1.WrongOperation,
            EnergyEvidenceMatcherV1.Match(Binding, Enforced with { OperationCorrelation = "operation:other" }));
    }

    [Fact]
    public void CounterRolloverAndFalseEnforcementClaimsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => (Enforced with
            { CounterStart = ulong.MaxValue, CounterEnd = 1 }).Validate());
        Assert.Throws<ArgumentException>(() => (Enforced with
            { MeasuredMicrojoules = 101 }).Validate());
        Assert.Throws<ArgumentException>(() => (Enforced with
            { EnforcementMechanism = "" }).Validate());
    }

    [Fact]
    public void ThermalThrottleNeverImpliesDeadlineOrPerformanceGuarantee()
    {
        Assert.Equal(ThermalStateV1.Throttled, Enforced.ThermalState);
        Assert.False(Enforced.GuaranteesDeadline);
        Assert.False(Enforced.GuaranteesPerformance);
    }
}
