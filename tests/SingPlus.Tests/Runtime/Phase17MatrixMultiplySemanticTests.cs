using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class Phase17MatrixMultiplySemanticTests
{
    private static readonly ComputeSelectionPolicy Policy = new(true, true, true);

    [Fact]
    public void ExactMatrixIntentPlansAAndBReadOnlyAndCStaged()
    {
        var scenario = CreateScenario();
        var intent = Intent(scenario);

        var plan = scenario.Kernel.PlanCompute(scenario.Caller, intent, Policy, [Provider()]);

        Assert.True(plan.IsSuccess, plan.Message);
        Assert.Equal(ComputePublicationPath.Staged, plan.Value!.PublicationPath);
        Assert.Equal(3, plan.Value.RequiredRegionUses.Count);
        Assert.Equal(new OperationRegionUseRequest(scenario.A.Handle, RegionUseMode.ReadOnly, new(0, 24)), plan.Value.RequiredRegionUses[0]);
        Assert.Equal(new OperationRegionUseRequest(scenario.B.Handle, RegionUseMode.ReadOnly, new(0, 48)), plan.Value.RequiredRegionUses[1]);
        Assert.Equal(new OperationRegionUseRequest(scenario.C.Handle, RegionUseMode.StagedOutput, new(0, 32)), plan.Value.RequiredRegionUses[2]);
        Assert.Contains(plan.Value.Dependencies.Nodes, node => node.Kind == ComputeDependencyKind.StagedOutputReady);
    }

    [Theory]
    [InlineData(0, 3, 4, 2)]
    [InlineData(2, 0, 4, 2)]
    [InlineData(2, 3, 0, 2)]
    [InlineData(2, 3, 4, 0)]
    public void ZeroShapeDimensionsFailClosed(ulong rows, ulong inner, ulong columns, uint elementBytes)
    {
        var scenario = CreateScenario();
        var malformed = Intent(scenario) with
        {
            MatrixMultiplyShape = new MatrixMultiplyShapeV1(1, rows, inner, columns, elementBytes)
        };

        Assert.Equal(KernelError.InvalidMessage,
            scenario.Kernel.PlanCompute(scenario.Caller, malformed, Policy, [Provider()]).Error);
    }

    [Fact]
    public void UnknownShapeVersionOverflowAndRangeMismatchFailClosed()
    {
        var scenario = CreateScenario();
        var intent = Intent(scenario);
        Assert.Equal(KernelError.InvalidMessage, scenario.Kernel.PlanCompute(scenario.Caller,
            intent with { MatrixMultiplyShape = intent.MatrixMultiplyShape!.Value with { Version = 2 } },
            Policy, [Provider()]).Error);
        Assert.Equal(KernelError.InvalidMessage, scenario.Kernel.PlanCompute(scenario.Caller,
            intent with { MatrixMultiplyShape = new(1, ulong.MaxValue, 2, 2, 8) },
            Policy, [Provider()]).Error);
        Assert.Equal(KernelError.InvalidMessage, scenario.Kernel.PlanCompute(scenario.Caller,
            intent with { RightInput = new ComputeRegionOperand(scenario.B.Handle, new(0, 47)) },
            Policy, [Provider()]).Error);
    }

    [Fact]
    public void AliasAndMissingMatrixFieldsFailClosed()
    {
        var scenario = CreateScenario();
        var intent = Intent(scenario);
        Assert.Equal(KernelError.InvalidMessage, scenario.Kernel.PlanCompute(scenario.Caller,
            intent with { RightInput = intent.Input }, Policy, [Provider()]).Error);
        Assert.Equal(KernelError.InvalidMessage, scenario.Kernel.PlanCompute(scenario.Caller,
            intent with { RightInput = null }, Policy, [Provider()]).Error);
        Assert.Equal(KernelError.InvalidMessage, scenario.Kernel.PlanCompute(scenario.Caller,
            intent with { MatrixMultiplyShape = null }, Policy, [Provider()]).Error);
    }

    [Fact]
    public void NonMatrixOperationsRejectMatrixOnlyFields()
    {
        var scenario = CreateScenario();
        var disguised = Intent(scenario) with { Operation = ComputeOperationKind.Copy };

        Assert.Equal(KernelError.InvalidMessage,
            scenario.Kernel.PlanCompute(scenario.Caller, disguised, Policy, [Provider()]).Error);
    }

    [Fact]
    public void ProviderCapacityCoversAllThreeOperandsAndGenerationDriftStillFails()
    {
        var scenario = CreateScenario();
        var intent = Intent(scenario);
        Assert.Equal(KernelError.PlatformUnavailable, scenario.Kernel.PlanCompute(
            scenario.Caller, intent, Policy, [Provider() with { MaximumOperationBytes = 103 }]).Error);

        var provider = Provider();
        var plan = scenario.Kernel.PlanCompute(scenario.Caller, intent, Policy, [provider]).Value!;
        Assert.Equal(KernelError.StaleGeneration, scenario.Kernel.ValidateComputePlanBeforeSubmit(
            scenario.Caller, plan, [provider with { Generation = 8 }]).Error);
    }

    private static ComputeIntent Intent(Scenario value) => new(
        ComputeOperationKind.MatrixMultiply,
        new(value.A.Handle, new(0, 24)),
        new(value.C.Handle, new(0, 32)),
        ComputePublicationPreference.StagedRequired,
        RequiresSecureEvidence: false,
        RequiresVirtualizedDomain: false,
        RightInput: new ComputeRegionOperand(value.B.Handle, new(0, 48)),
        MatrixMultiplyShape: new MatrixMultiplyShapeV1(1, 2, 3, 4, 4));

    private static ComputeProviderCandidate Provider() => new(
        new("matrix-contract-only"), 7,
        ComputeProviderCapabilities.AcceleratorExecution | ComputeProviderCapabilities.StagedPublication,
        104, 1, 1, Available: true, Faulted: false);

    private static Scenario CreateScenario()
    {
        var kernel = new RuntimeKernel();
        var (_, caller) = TestFixtures.Create(kernel, 17, 170);
        return new(kernel, caller,
            kernel.AllocateBuffer<byte>(caller, 24).Value!,
            kernel.AllocateBuffer<byte>(caller, 48).Value!,
            kernel.AllocateBuffer<byte>(caller, 32).Value!);
    }

    private sealed record Scenario(
        RuntimeKernel Kernel,
        ProcessHandle Caller,
        OwnedBuffer<byte> A,
        OwnedBuffer<byte> B,
        OwnedBuffer<byte> C);
}
