using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class V6DataMotionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusedTemporaryReleaseRetainsExactChargeUntilUseReleaseAndActualTeardown(bool callbackThrows)
    {
        var context = Create();
        RegionUseDescriptor? held = null;
        var reads = 0;
        var moved = context.Kernel.ExecuteV6HostDataMotion(context.Source.Process, context.Destination.Process,
            context.Input, context.Plan, () =>
            {
                if (++reads != 2) return 3UL;
                var temporary = Assert.Single(context.Kernel.Regions.Snapshot(), r =>
                    r.Owner.DomainId == context.Destination.Domain && r.State == RegionState.Owned);
                held = context.Kernel.AcquireRegionUse(context.Destination.Process, temporary.Handle,
                    RegionUseMode.ExclusiveRead, new(0, 64)).Value!;
                return callbackThrows ? throw new InvalidOperationException("cost fault") : 4UL;
            }, () => 5UL);

        Assert.Equal(callbackThrows ? KernelError.PlatformUnavailable : KernelError.StaleGeneration, moved.Error);
        Assert.Contains("cleanup refused (RegionUseConflict)", moved.Message);
        Assert.Contains(held!.Region.ToString(), moved.Message);
        Assert.True(context.Input.IsValid);
        Assert.Equal(64UL, Used(context.Kernel, context.Source));
        Assert.Equal(64UL, Used(context.Kernel, context.Destination));
        Assert.True(context.Kernel.Regions.Validate(held.Region,
            new(context.Destination.Domain, context.Destination.Process.Generation)).IsSuccess);
        Assert.True(context.Kernel.InvalidateRegionUse(context.Destination.Process, held.Handle).IsSuccess);
        Assert.False(context.Kernel.TerminateProcess(context.Destination.Process).IsSuccess);
        Assert.Equal(64UL, Used(context.Kernel, context.Destination));
        Assert.True(context.Kernel.ReleaseRegionUse(context.Destination.Process, held.Handle).IsSuccess);
        Assert.True(context.Kernel.TerminateProcess(context.Destination.Process).IsSuccess);
        Assert.Equal(0UL, Used(context.Kernel, context.Destination));
        Assert.Equal(RegionState.Released, Assert.Single(context.Kernel.Regions.Snapshot(),
            r => r.Handle == held.Region).State);
        Assert.Equal(64UL, Used(context.Kernel, context.Source));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ProcessExitAtCostCallbackOrSourceReleaseRefusesUsablePublication(bool sourceExit, bool afterCopy)
    {
        var context = Create();
        context.Input.Span.Fill(0x53);
        var reads = 0;
        var exited = sourceExit ? context.Source : context.Destination;
        void Exit()
        {
            context.Kernel.BeforeRegionReleaseQualificationHook = null;
            Assert.True(context.Kernel.TerminateProcess(exited.Process).IsSuccess);
        }
        if (afterCopy) context.Kernel.BeforeRegionReleaseQualificationHook = Exit;
        var moved = context.Kernel.ExecuteV6HostDataMotion(context.Source.Process, context.Destination.Process,
            context.Input, context.Plan, () =>
            {
                if (++reads == 2 && !afterCopy) Exit();
                return 3UL;
            }, () => 5UL);

        Assert.Equal(KernelError.StaleHandle, moved.Error);
        Assert.Equal(!sourceExit && !afterCopy, context.Input.IsValid);
        if (context.Input.IsValid)
            Assert.All(context.Input.Span.ToArray(), value => Assert.Equal((byte)0x53, value));
        Assert.Equal(!sourceExit && !afterCopy ? 64UL : 0UL, Used(context.Kernel, context.Source));
        Assert.Equal(0UL, Used(context.Kernel, context.Destination));
        Assert.All(context.Kernel.Regions.SnapshotUses(), use => Assert.Equal(RegionUseState.Released, use.State));
        Assert.DoesNotContain(context.Kernel.Regions.Snapshot(), r =>
            r.Owner.DomainId == context.Destination.Domain && r.State != RegionState.Released);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalCostCallbackSourceMutationRefusesCopyAndRefundsDestination(bool destinationCallback)
    {
        var context = Create();
        var before = Used(context.Kernel, context.Source) + Used(context.Kernel, context.Destination);
        var observations = 0;
        ulong Observe()
        {
            if (++observations == 2)
            {
                var write = context.Kernel.AcquireRegionUse(context.Source.Process, context.Input.Handle,
                    RegionUseMode.ExclusiveWrite, new(0, 64));
                Assert.True(write.IsSuccess, write.Message);
                context.Input.Span.Fill(0x7B);
                Assert.True(context.Kernel.ReleaseRegionUse(context.Source.Process, write.Value!.Handle).IsSuccess);
            }
            return destinationCallback ? 5UL : 3UL;
        }
        var moved = context.Kernel.ExecuteV6HostDataMotion(context.Source.Process, context.Destination.Process,
            context.Input, context.Plan, destinationCallback ? () => 3UL : Observe,
            destinationCallback ? Observe : () => 5UL);

        Assert.Equal(KernelError.StaleGeneration, moved.Error);
        Assert.True(context.Input.IsValid);
        Assert.All(context.Input.Span.ToArray(), value => Assert.Equal((byte)0x7B, value));
        Assert.Equal(before, Used(context.Kernel, context.Source) + Used(context.Kernel, context.Destination));
        Assert.All(context.Kernel.Regions.SnapshotUses(), use => Assert.Equal(RegionUseState.Released, use.State));
        Assert.DoesNotContain(context.Kernel.Regions.Snapshot(), r =>
            r.Owner.DomainId == context.Destination.Domain && r.State != RegionState.Released);
    }

    [Theory]
    [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 1)] [InlineData(true, 2)]
    public void CostGenerationCallbackFaultBeforeCopyPreservesSourceAndRefundsTemporaryMemory(bool destinationFault, int attempt)
    {
        var context = Create();
        context.Input.Span.Fill(0x6C);
        var before = Used(context.Kernel, context.Source) + Used(context.Kernel, context.Destination);
        var sourceReads = 0;
        var destinationReads = 0;
        var moved = context.Kernel.ExecuteV6HostDataMotion(context.Source.Process, context.Destination.Process,
            context.Input, context.Plan,
            () => !destinationFault && ++sourceReads == attempt ? throw new InvalidOperationException("source cost") : 3UL,
            () => destinationFault && ++destinationReads == attempt ? throw new InvalidOperationException("destination cost") : 5UL);

        Assert.Equal(KernelError.PlatformUnavailable, moved.Error);
        Assert.True(context.Input.IsValid);
        Assert.All(context.Input.Span.ToArray(), value => Assert.Equal((byte)0x6C, value));
        Assert.Equal(before, Used(context.Kernel, context.Source) + Used(context.Kernel, context.Destination));
        Assert.DoesNotContain(context.Kernel.Regions.Snapshot(), r =>
            r.Owner.DomainId == context.Destination.Domain && r.State != RegionState.Released);
        Assert.Empty(context.Kernel.Regions.SnapshotUses());
    }

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
