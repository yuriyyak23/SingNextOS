using System.Reflection;
using SingPlus.Platform;
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
        AssertDenied(SipJobStageExecutionEligibilityError.UnknownVersion,
            new(2, "stage", SipJobStageExecutionClass.ManagedDefault, "None", 0));
        AssertDenied(SipJobStageExecutionEligibilityError.UnknownExecutionClass,
            new(1, "stage", (SipJobStageExecutionClass)99, "None", 0));
        AssertDenied(SipJobStageExecutionEligibilityError.FutureGatedRequiresProviderContract,
            new(1, "stage", SipJobStageExecutionClass.ManagedDefault, "hypothetical", 1));
        AssertDenied(SipJobStageExecutionEligibilityError.FutureGatedRequiresProviderContract,
            new(1, "stage", SipJobStageExecutionClass.ManagedDefault, "None", 1));
        AssertDenied(SipJobStageExecutionEligibilityError.FutureGatedRequiresProviderContract,
            new(1, "stage", SipJobStageExecutionClass.ManagedDefault, "none", 0));
    }

    [Theory]
    [InlineData(null, "None")]
    [InlineData("", "None")]
    [InlineData(" stage", "None")]
    [InlineData("stage", null)]
    [InlineData("stage", "")]
    [InlineData("stage", "None ")]
    public void MalformedIdentitiesDenyBothManagedAndProviderEligibility(string? stageId, string? contractId) =>
        AssertDenied(SipJobStageExecutionEligibilityError.Malformed,
            new(1, stageId!, SipJobStageExecutionClass.ManagedDefault, contractId!, 0));

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

    [Fact]
    public void ExistingProcessExecutionPolicyIsNotReinterpretedAsAStageContract()
    {
        var result = SipJobStageExecutionEligibilityVerifier.Verify(new(
            1,
            "stage",
            SipJobStageExecutionClass.ManagedDefault,
            nameof(PlatformExecutionPolicy),
            PlatformExecutionPolicyContract.ContractVersion));

        Assert.Equal(SipJobStageExecutionEligibilityError.FutureGatedRequiresProviderContract, result.Error);
        Assert.False(result.MayUseManagedRuntime);
        Assert.False(result.MayUseProvider);
    }

    private static void AssertDenied(
        SipJobStageExecutionEligibilityError expected,
        SipJobStageExecutionEligibilityDescriptor descriptor)
    {
        var result = SipJobStageExecutionEligibilityVerifier.Verify(descriptor);
        Assert.Equal(expected, result.Error);
        Assert.False(result.IsSuccess);
        Assert.False(result.MayUseManagedRuntime);
        Assert.False(result.MayUseProvider);
    }
}
