using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase11TemporalClaimBoundaryTests
{
    [Fact]
    public void TemporalAndGuaranteeGatesRemainDefaultOff()
    {
        Assert.False(VNextFeatureGates.IsEnabled("FG-VNX-TEMPORAL-UPPER-BOUND"));
        Assert.False(VNextFeatureGates.IsEnabled("FG-VNX-GUARANTEED-RESERVATION"));
    }

    [Fact]
    public void DeadlineObservationNeverAuthorizesEffectReclaimOrCapacity()
    {
        var observation = new CancellationObservation(
            new(new(1), new(1)), new(new(1), 1), null,
            new(long.MaxValue), false, TimeoutDisposition.NotExpired,
            CancellationDisposition.Active, 1);

        Assert.False(observation.AuthorizesEffect);
        Assert.False(observation.AuthorizesReclaim);
        Assert.DoesNotContain(observation.GetType().GetProperties(), property =>
            property.Name.Contains("Guarantee", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Capacity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExistingRuntimeHasNoTemporalReplenishmentOrPreemptionAuthority()
    {
        string[] forbidden = ["Replenish", "Throttle", "Preempt", "GuaranteeCapacity"];
        var methods = typeof(ResourceBudgetAuthority).GetMethods(
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.Public |
            global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(methods, method => forbidden.Any(fragment =>
            method.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }
}
