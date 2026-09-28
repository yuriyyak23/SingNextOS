using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class V6ProtectedAcceleratorLabelFlowTests
{
    [Fact]
    public void InputIntermediateAndOutputRemainBoundToOneOperationAndLabel()
    {
        var result = V6ProtectedAcceleratorLabelFlow.Verify(
        [
            Value(ProtectedAcceleratorValueKindV1.Input, 1, "input", 4),
            Value(ProtectedAcceleratorValueKindV1.Intermediate, 2, "scratch", 5),
            Value(ProtectedAcceleratorValueKindV1.Output, 3, "output", 5),
        ]);

        Assert.True(result.IsSuccess, result.Detail);
        Assert.All(result.Sideband, value =>
        {
            Assert.False(value.AuthorizesAccess);
            Assert.False(value.AuthorizesExecution);
        });
    }

    [Fact]
    public void OperationGenerationAndSequenceSubstitutionFailClosed()
    {
        Assert.Equal(ProtectedAcceleratorLabelError.OperationMismatch,
            V6ProtectedAcceleratorLabelFlow.Verify(
            [
                Value(ProtectedAcceleratorValueKindV1.Input, 1, "input"),
                Value(ProtectedAcceleratorValueKindV1.Output, 2, "output") with { ProviderGeneration = 10 },
            ]).Error);
        Assert.Equal(ProtectedAcceleratorLabelError.SequenceMismatch,
            V6ProtectedAcceleratorLabelFlow.Verify(
            [
                Value(ProtectedAcceleratorValueKindV1.Input, 1, "input"),
                Value(ProtectedAcceleratorValueKindV1.Output, 3, "output"),
            ]).Error);
    }

    [Fact]
    public void IntermediateOrOutputLabelLaunderingFailsClosed()
    {
        var changed = Value(ProtectedAcceleratorValueKindV1.Output, 2, "output") with
        {
            Label = new(1, ConfidentialityClassV1.Public, IntegrityClassV1.Trusted)
        };
        Assert.Equal(ProtectedAcceleratorLabelError.LabelLaundering,
            V6ProtectedAcceleratorLabelFlow.Verify(
                [Value(ProtectedAcceleratorValueKindV1.Input, 1, "input"), changed]).Error);
        Assert.Equal(ProtectedAcceleratorLabelError.LabelLaundering,
            V6ProtectedAcceleratorLabelFlow.Verify(
            [
                Value(ProtectedAcceleratorValueKindV1.Input, 1, "input", 4),
                Value(ProtectedAcceleratorValueKindV1.Output, 2, "output", 3),
            ]).Error);
        Assert.Equal(ProtectedAcceleratorLabelError.LabelLaundering,
            V6ProtectedAcceleratorLabelFlow.Verify(
            [
                Value(ProtectedAcceleratorValueKindV1.Input, 1, "input", 4),
                Value(ProtectedAcceleratorValueKindV1.Intermediate, 2, "scratch", 6),
                Value(ProtectedAcceleratorValueKindV1.Output, 3, "output", 5),
            ]).Error);
    }

    private static ProtectedAcceleratorLabelSidebandV1 Value(
        ProtectedAcceleratorValueKindV1 kind, uint sequence, string valueId, ulong labelGeneration = 4) =>
        new(1, "operation", valueId, kind, sequence,
            new(1, ConfidentialityClassV1.Protected, IntegrityClassV1.Validated),
            labelGeneration, 9, 11);
}
