using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed partial class PlatformDsc1ComputeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualTupleForeignDsc1PayloadCannotConsumeAnotherKernelsViews(bool discard)
    {
        (Scenario Scenario, BudgetAccountHandle Budget, Dsc1NativeSpy Spy) Build()
        {
            var proxy = DispatchProxy.Create<IOriginalDsc1Runtime, Dsc1NativeSpy>();
            var spy = (Dsc1NativeSpy)(object)proxy;
            spy.Native = new();
            BudgetAccountHandle budget = default;
            var scenario = CreateScenario(new HybridCpuPlatformAuthorityProvider(proxy), 2597, 25970, 32,
                (kernel, subject) =>
                {
                    var admin = TestFixtures.Create(kernel, 2598, 25980).Handle;
                    var cap = kernel.MintCapability(new(25980), admin, ResourceKind.KernelService,
                        CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
                    budget = kernel.AdmitProcessBudget(admin, cap, subject, "dsc1-payload-custody",
                        [new(ServiceBudgetDimension.OwnedMemoryBytes, 128)]).Value!.ProcessBudget;
                }, originalSlices: true);
            return (scenario, budget, spy);
        }

        var x = Build();
        var foreign = Build();
        x.Scenario.Source.Span.Fill(0x6B);
        foreign.Scenario.Source.Span.Fill(0x7D);
        var submitted = SubmitWhole(x.Scenario);
        var otherSubmitted = SubmitWhole(foreign.Scenario);
        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.True(otherSubmitted.IsSuccess, otherSubmitted.Message);
        Assert.Equal(submitted.Value, otherSubmitted.Value);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary Inventory(RuntimeKernel kernel) =>
            (IDictionary)kernel.GetType().GetField("_dsc1PayloadOperations", flags)!.GetValue(kernel)!;
        ulong Used((Scenario Scenario, BudgetAccountHandle Budget, Dsc1NativeSpy Spy) item) =>
            item.Scenario.Kernel.QueryBudget(item.Budget).Value!.Usage
                .Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
        var routes = Inventory(x.Scenario.Kernel);
        var original = routes[submitted.Value.SubmissionId]!;
        var other = Inventory(foreign.Scenario.Kernel)[otherSubmitted.Value.SubmissionId]!;
        routes[submitted.Value.SubmissionId] = other;

        if (discard) Assert.False(x.Scenario.Kernel.TerminateProcess(x.Scenario.Subject).IsSuccess);
        else Assert.False(x.Scenario.Kernel.ObservePlatformDsc1Copy(x.Scenario.Subject, submitted.Value).IsSuccess);
        Assert.Equal(128UL, Used(x));
        Assert.Equal(128UL, Used(foreign));
        Assert.Equal(1, x.Spy.Copies);
        Assert.Equal(1, foreign.Spy.Copies);
        Assert.Throws<InvalidOperationException>(() => foreign.Scenario.Destination.Span.ToArray());
        Assert.Throws<InvalidOperationException>(() => foreign.Scenario.Source.Span.ToArray());
        Assert.False((bool)other.GetType().GetProperty("StagedOutputChargeConsumed")!.GetValue(other)!);
        Assert.False((bool)other.GetType().GetProperty("OutputCopyCommitted")!.GetValue(other)!);

        routes[submitted.Value.SubmissionId] = original;
        if (!discard)
        {
            Assert.True(x.Scenario.Kernel.ObservePlatformDsc1Copy(x.Scenario.Subject, submitted.Value).IsSuccess);
            Assert.All(x.Scenario.Destination.Span.ToArray(), b => Assert.Equal((byte)0x6B, b));
            Assert.Equal(64UL, Used(x));
        }
        Assert.True(x.Scenario.Kernel.TerminateProcess(x.Scenario.Subject).IsSuccess);
        Assert.Equal(0UL, Used(x));
        Assert.Empty(routes);
        Assert.Equal(128UL, Used(foreign));
        var foreignResult = foreign.Scenario.Kernel.ObservePlatformDsc1Copy(foreign.Scenario.Subject, otherSubmitted.Value);
        Assert.True(foreignResult.IsSuccess, foreignResult.Message);
        Assert.All(foreign.Scenario.Destination.Span.ToArray(), b => Assert.Equal((byte)0x7D, b));
        Assert.True(foreign.Scenario.Kernel.TerminateProcess(foreign.Scenario.Subject).IsSuccess);
        Assert.Equal(0UL, Used(foreign));
        Assert.Equal(1, x.Spy.Copies);
        Assert.Equal(1, foreign.Spy.Copies);
    }
}
