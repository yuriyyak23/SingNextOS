using System.Reflection;
using SingPlus.Runtime;

namespace SingPlus.Tests.SipJobs;

public sealed class Phase147ProviderNeutralSchedulingTests
{
    [Fact]
    public void ManagedDefaultIsLocalEligibilityOnlyAndBothProviderGatesRemainOff()
    {
        var result = SipJobStageExecutionEligibilityVerifier.Verify(new(
            1, "stage-1", SipJobStageExecutionClass.ManagedDefault, "None", 0));
        Assert.True(result.IsSuccess);
        Assert.True(result.MayUseManagedRuntime);
        Assert.False(result.MayUseProvider);
        Assert.False(SipJobFeatureGates.IsEnabled("FG-HYBRIDCPU-HINTS"));
        Assert.False(SipJobFeatureGates.IsEnabled("FG-HYBRIDCPU-ACCEL"));
    }

    [Fact]
    public void UnknownHintVersionAndUnmappedProviderContractFailClosed()
    {
        Assert.Equal(SipJobStageExecutionEligibilityError.UnknownVersion,
            SipJobStageExecutionEligibilityVerifier.Verify(new(2, "stage", SipJobStageExecutionClass.ManagedDefault, "None", 0)).Error);
        Assert.Equal(SipJobStageExecutionEligibilityError.UnknownExecutionClass,
            SipJobStageExecutionEligibilityVerifier.Verify(new(1, "stage", (SipJobStageExecutionClass)99, "None", 0)).Error);
        Assert.Equal(SipJobStageExecutionEligibilityError.FutureGatedRequiresProviderContract,
            SipJobStageExecutionEligibilityVerifier.Verify(new(1, "stage", SipJobStageExecutionClass.ManagedDefault, "hypothetical", 1)).Error);
    }

    [Fact]
    public void EligibilitySurfaceExposesNoPhysicalOrProviderPrivateSelector()
    {
        var forbidden = new[] { "Lane", "Opcode", "Slot", "Dsc", "Vmcs", "Iommu", "Dma", "Cxl", "Token", "Queue", "Topology", "Handle" };
        var properties = typeof(SipJobStageExecutionEligibilityDescriptor)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(static property => property.Name != "EqualityContract")
            .ToArray();
        Assert.DoesNotContain(properties, property => forbidden.Any(value => property.Name.Contains(value, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(properties, property => property.PropertyType == typeof(object) || property.PropertyType == typeof(Delegate));
    }
}
