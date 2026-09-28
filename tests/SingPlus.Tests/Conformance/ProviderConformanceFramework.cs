using Xunit;

namespace SingPlus.Tests.Conformance;

public enum ProviderFaultMode
{
    FailBeforeEffect = 0,
    FailAfterAcceptance,
    ThrowAfterAcceptance,
    ReturnMalformedReceipt,
    ChangeGeneration,
    DelayClosure,
    FailClosure,
    ResetDuringSubmitted,
    ResetDuringVisible,
    ReturnStaleClosureReceipt,
}

public sealed record ProviderFaultPlan(ProviderFaultMode Mode, ulong DeterministicSeed = 0x51A6UL);

public sealed record ProviderScenario(string Name, ProviderFaultPlan Fault);

public sealed record ProviderEffectOracle(
    bool RejectedBeforeEffect,
    bool EffectMayExist,
    bool PublicationSuppressed,
    bool LocalPinsRetained,
    bool BudgetChargeRetained);

public sealed record ProviderClosureOracle(
    bool TeardownOrReplacementBlocked,
    bool QuarantineObserved,
    bool CheckpointBlocked,
    bool EventualPinsReleased,
    bool EventualBudgetReleased,
    bool EventualReclaim);

public sealed record ProviderGenerationOracle(bool StaleRejected, bool FreshScenarioIsolated);

public sealed record ProviderConformanceObservation(
    string Family,
    ProviderFaultMode Fault,
    ProviderEffectOracle Effect,
    ProviderClosureOracle Closure,
    ProviderGenerationOracle Generation);

public interface IProviderConformanceDriver
{
    string Family { get; }
    ProviderConformanceObservation Execute(ProviderScenario scenario);
}

public sealed class ProviderConformanceSuite(Func<IProviderConformanceDriver> driverFactory)
{
    public IReadOnlyList<ProviderConformanceObservation> RunAll()
    {
        var observations = new List<ProviderConformanceObservation>();
        foreach (var mode in Enum.GetValues<ProviderFaultMode>())
        {
            var scenario = new ProviderScenario($"{driverFactory().Family}:{mode}", new(mode));
            var first = driverFactory().Execute(scenario);
            var repeated = driverFactory().Execute(scenario);
            Assert.Equal(first, repeated);
            Validate(first);
            observations.Add(first);
        }
        return observations;
    }

    private static void Validate(ProviderConformanceObservation observation)
    {
        Assert.True(observation.Generation.FreshScenarioIsolated);
        if (observation.Fault == ProviderFaultMode.FailBeforeEffect)
        {
            Assert.True(observation.Effect.RejectedBeforeEffect);
            Assert.False(observation.Effect.EffectMayExist);
            Assert.False(observation.Effect.LocalPinsRetained);
            Assert.False(observation.Effect.BudgetChargeRetained);
            Assert.True(observation.Closure.EventualReclaim);
            return;
        }

        Assert.False(observation.Effect.RejectedBeforeEffect);
        Assert.True(observation.Effect.EffectMayExist);
        Assert.True(observation.Effect.PublicationSuppressed);
        var context = $"{observation.Family}/{observation.Fault}";
        Assert.True(observation.Effect.LocalPinsRetained, context);
        Assert.True(observation.Effect.BudgetChargeRetained, context);
        Assert.True(observation.Closure.TeardownOrReplacementBlocked, context);
        Assert.True(observation.Closure.QuarantineObserved, context);
        Assert.True(observation.Closure.CheckpointBlocked, context);
        Assert.True(observation.Closure.EventualPinsReleased, context);
        Assert.True(observation.Closure.EventualBudgetReleased, context);
        Assert.True(observation.Closure.EventualReclaim, context);
        if (observation.Fault is ProviderFaultMode.ChangeGeneration or
            ProviderFaultMode.ReturnMalformedReceipt or ProviderFaultMode.ReturnStaleClosureReceipt)
            Assert.True(observation.Generation.StaleRejected);
    }
}
