using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class V6DataMotionTests
{
    [Fact]
    public void HostMoveCopiesThenReleasesSourceAndConservesOwnedMemoryCharge()
    {
        var context = Create();
        context.Input.Span.Fill(0x5A);
        var before = Used(context.Kernel, context.Source) + Used(context.Kernel, context.Destination);

        var moved = context.Kernel.ExecuteV6HostDataMotion(context.Source.Process, context.Destination.Process,
            context.Input, context.Plan, () => 3, () => 5);

        Assert.True(moved.IsSuccess, moved.Message);
        Assert.False(context.Input.IsValid);
        Assert.All(moved.Value!.Moved.Span.ToArray(), value => Assert.Equal((byte)0x5A, value));
        Assert.Equal(before, Used(context.Kernel, context.Source) + Used(context.Kernel, context.Destination));
        Assert.Equal([DataMotionLifecycleStateV1.Planned, DataMotionLifecycleStateV1.SourcePinned,
            DataMotionLifecycleStateV1.Copied, DataMotionLifecycleStateV1.SourceReleased,
            DataMotionLifecycleStateV1.Completed], moved.Value.Receipt.Lifecycle);
        Assert.False(moved.Value.Receipt.AuthorizesMovement);
    }

    [Fact]
    public void StaleEstimateFailsBeforeAllocatingDestination()
    {
        var context = Create();
        var targetBefore = Used(context.Kernel, context.Destination);

        var result = context.Kernel.ExecuteV6HostDataMotion(context.Source.Process, context.Destination.Process,
            context.Input, context.Plan, () => 4, () => 5);

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.True(context.Input.IsValid);
        Assert.Equal(targetBefore, Used(context.Kernel, context.Destination));
    }

    [Fact]
    public void MutationAfterPlanningFailsWithoutLeakingTemporaryCharge()
    {
        var context = Create();
        var write = context.Kernel.AcquireRegionUse(context.Source.Process, context.Input.Handle,
            RegionUseMode.ExclusiveWrite, new(0, 1)).Value!;
        Assert.True(context.Kernel.ReleaseRegionUse(context.Source.Process, write.Handle).IsSuccess);
        var targetBefore = Used(context.Kernel, context.Destination);

        var result = context.Kernel.ExecuteV6HostDataMotion(context.Source.Process, context.Destination.Process,
            context.Input, context.Plan, () => 3, () => 5);

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(targetBefore, Used(context.Kernel, context.Destination));
    }

    [Fact]
    public async Task ConcurrentMovementHasOneWinnerAndNoBudgetLeak()
    {
        var context = Create();
        var before = Used(context.Kernel, context.Source) + Used(context.Kernel, context.Destination);
        using var start = new ManualResetEventSlim(false);
        var calls = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return context.Kernel.ExecuteV6HostDataMotion(context.Source.Process, context.Destination.Process,
                context.Input, context.Plan, () => 3, () => 5);
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(calls);

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(results, result => !result.IsSuccess);
        Assert.Equal(before, Used(context.Kernel, context.Source) + Used(context.Kernel, context.Destination));
    }

    [Fact]
    public void DmaModeRemainsUnsupportedAndLocalityGateRemainsOff()
    {
        var context = Create();
        var result = context.Kernel.ExecuteV6HostDataMotion(context.Source.Process, context.Destination.Process,
            context.Input, context.Plan with { Mode = DataMotionModeV1.ProviderDmaThenReleaseSource }, () => 3, () => 5);
        Assert.Equal(KernelError.PlatformUnsupported, result.Error);
        Assert.False(V6FeatureGates.IsEnabled("V6-LOCALITY-PLANNING"));
    }

    [Fact]
    public void ManagedProviderSidebandSelectsFreshDestinationAndRevalidatesAtMove()
    {
        var context = Create();
        var provider = ProviderWith(Estimate("source", 3),
            Estimate("slower", 4) with { EstimatedLatencyNanoseconds = 900 },
            Estimate("destination", 5));
        context.Input.Span.Fill(0x2A);

        var result = context.Kernel.ExecuteV6ProviderObservedHostDataMotion(
            context.Source.Process, context.Destination.Process, context.Input,
            "source", ["slower", "destination"], 1500, 64, provider);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("destination", result.Value!.Receipt.Plan.DestinationProviderClass);
        Assert.All(result.Value.Moved.Span.ToArray(), value => Assert.Equal((byte)0x2A, value));
        Assert.False(result.Value.Receipt.AuthorizesMovement);
    }

    [Fact]
    public void ManagedProviderRejectsReplayAndRestartInvalidatesEvidence()
    {
        var provider = ProviderWith(Estimate("source", 3));

        Assert.Equal(KernelError.StaleGeneration, provider.Publish(Estimate("source", 3)).Error);
        Assert.True(provider.QueryFresh("source", 1500).IsSuccess);
        provider.Restart();
        Assert.Equal(KernelError.PlatformUnavailable, provider.QueryFresh("source", 1500).Error);
        Assert.Equal(KernelError.StaleGeneration, provider.CurrentGeneration("source").Error);
    }

    [Fact]
    public void ExpiredOrMissingProviderSidebandFailsBeforeDestinationAllocation()
    {
        var context = Create();
        var before = Used(context.Kernel, context.Destination);
        var provider = ProviderWith(Estimate("source", 3), Estimate("destination", 5));

        var expired = context.Kernel.ExecuteV6ProviderObservedHostDataMotion(
            context.Source.Process, context.Destination.Process, context.Input,
            "source", ["destination"], 2000, 64, provider);

        Assert.Equal(KernelError.StaleGeneration, expired.Error);
        Assert.True(context.Input.IsValid);
        Assert.Equal(before, Used(context.Kernel, context.Destination));
    }

    [Fact]
    public void ProviderGenerationDriftAtFinalSentryCompensatesTemporaryAllocation()
    {
        var context = Create();
        var before = Used(context.Kernel, context.Destination);
        var stable = ProviderWith(Estimate("source", 3), Estimate("destination", 5));
        var drifting = new DriftingCostProvider(stable);

        var result = context.Kernel.ExecuteV6ProviderObservedHostDataMotion(
            context.Source.Process, context.Destination.Process, context.Input,
            "source", ["destination"], 1500, 64, drifting);

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.True(context.Input.IsValid);
        Assert.Equal(before, Used(context.Kernel, context.Destination));
    }

    private static Context Create()
    {
        var kernel = new RuntimeKernel();
        var source = Component(kernel, 8800, 8810, "motion-source");
        var destination = Component(kernel, 8820, 8830, "motion-destination");
        var input = kernel.AllocateBuffer<byte>(source.Process, 64).Value!;
        var descriptor = kernel.Regions.Validate(input.Handle,
            new(new(8810), source.Process.Generation)).Value!;
        var plan = LocalityPlanningPolicyV1.PlanHostMove(
            Estimate("source", 3), [Estimate("destination", 5)], 1500,
            input.Handle.Generation.Value, descriptor.MutationEpoch.Value, 64);
        return new(kernel, source, destination, input, plan);
    }

    private static ComponentLifecycleSnapshot Component(RuntimeKernel kernel, ulong process, ulong domain, string identity)
    {
        byte[] image = [1, 2, 3, checked((byte)(process % 255))];
        var manifest = new ServiceManifestV1(new(identity), new("1"),
            Convert.ToHexStringLower(SHA256.HashData(image)), TestFixtures.Manifest(process, domain, identity: identity),
            budgetRequests: [new(ServiceBudgetDimension.OwnedMemoryBytes, 1024)]);
        return kernel.AdmitComponent(new(manifest, image)).Value!;
    }

    private static ulong Used(RuntimeKernel kernel, ComponentLifecycleSnapshot component) =>
        component.ProcessBudget is { } budget
            ? kernel.QueryBudget(budget).Value!.Usage
                .Single(item => item.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used
            : 0;

    private static LocalityCostEstimateV1 Estimate(string provider, ulong generation) =>
        new(1, provider, generation, 1000, 2000, 64, 100, 0, 900);

    private static V6ManagedLocalityCostProvider ProviderWith(
        params LocalityCostEstimateV1[] estimates)
    {
        var provider = new V6ManagedLocalityCostProvider();
        foreach (var estimate in estimates) Assert.True(provider.Publish(estimate).IsSuccess);
        return provider;
    }

    private sealed class DriftingCostProvider(IV6LocalityCostEvidenceProvider inner) :
        IV6LocalityCostEvidenceProvider
    {
        private int generationReads;
        public KernelResult<LocalityCostEstimateV1> QueryFresh(string providerClass, long nowUnixMilliseconds) =>
            inner.QueryFresh(providerClass, nowUnixMilliseconds);
        public KernelResult<ulong> CurrentGeneration(string providerClass)
        {
            var current = inner.CurrentGeneration(providerClass);
            if (!current.IsSuccess) return current;
            return Interlocked.Increment(ref generationReads) >= 3
                ? KernelResult<ulong>.Ok(current.Value + 1)
                : current;
        }
    }

    private sealed record Context(RuntimeKernel Kernel, ComponentLifecycleSnapshot Source,
        ComponentLifecycleSnapshot Destination, OwnedBuffer<byte> Input, DataMotionPlanV1 Plan);
}
