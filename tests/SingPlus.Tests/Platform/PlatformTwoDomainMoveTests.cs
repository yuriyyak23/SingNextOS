using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformTwoDomainMoveTests
{
    [Fact]
    public void RematerializationInventoryWaitsForOriginalOwnerTransaction()
    {
        var provider = new MoveProvider();
        var (kernel, source, target, buffer, mapping) = CreateMappedSource(provider, 919, 9190, 920, 9200,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        var bridge = kernel.PlatformAuthority;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var gate = bridge.GetType().GetField("_secureDomainLifecycleGate", flags)!.GetValue(bridge)!;
        using var entered = new ManualResetEventSlim();
        Task<bool> reader;
        bool completedInsideTransaction;
        lock (gate)
        {
            reader = Task.Factory.StartNew(() =>
            {
                entered.Set();
                return bridge.HasActiveMapping(buffer.Handle);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            completedInsideTransaction = reader.Wait(TimeSpan.FromMilliseconds(250));
        }
        Assert.True(reader.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(completedInsideTransaction);
        Assert.True(reader.Result);
        Assert.True(kernel.RevokePlatformRegionMapping(source, mapping).IsSuccess);
        Assert.False(bridge.HasActiveMapping(buffer.Handle));
        Assert.True(buffer.IsValid);
    }

    [Theory]
    [InlineData("mapping")][InlineData("slice")]
    public void AcquireCaptureWaitsForOwnerGateAndSourceLossPreventsMove(string lostFact)
    {
        var provider = new MoveProvider();
        var (kernel, source, target, buffer, mapping) = CreateMappedSource(provider, 909, 9090, 910, 9100,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        var bridge = kernel.PlatformAuthority;
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var gate = bridge.GetType().GetField("_secureDomainLifecycleGate", flags)!.GetValue(bridge)!;
        var facts = (global::System.Collections.IDictionary)bridge.GetType()
            .GetField(lostFact == "mapping" ? "_mappings" : "_exactMappingSlices", flags)!.GetValue(bridge)!;
        KernelResult<PlatformRegionAcquireEvidence> result = default;
        Exception? failure = null;
        using var entered = new ManualResetEventSlim();
        var contender = new Thread(() =>
        {
            entered.Set();
            try { result = bridge.AcquireClosedRegionMappingFromConsumer(mapping, mapping.Mapping.DomainBinding.Subject,
                PlatformMemoryConsumerClass.ExternalExecutionDomain, PlatformMemoryAcquireRequirement.AcquisitionFence); }
            catch (Exception exception) { failure = exception; }
        });
        lock (gate)
        {
            contender.Start();
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(SpinWait.SpinUntil(() => (contender.ThreadState & ThreadState.WaitSleepJoin) != 0 || !contender.IsAlive,
                TimeSpan.FromSeconds(5)));
            Assert.True(contender.IsAlive);
            facts.Remove(mapping.Mapping.MappingId);
        }
        Assert.True(contender.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingNotFound, result.Error);
        Assert.Equal(0, provider.AcquireCalls);
        Assert.False(kernel.MovePlatformOwnedBuffer(source, target, buffer, mapping).IsSuccess);
        Assert.True(buffer.IsValid);
        Assert.Equal(new DomainId(9090), kernel.Regions.Snapshot().Single(r => r.Handle == buffer.Handle).Owner.DomainId);
        Assert.DoesNotContain("publish:9100", provider.MoveLog);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void ExactAcquireRetainedAfterSourceLossContinuesMoveWithoutRepeat(bool sourceThrow)
    {
        var provider = new MoveProvider();
        var (kernel, source, target, buffer, mapping) = CreateMappedSource(provider, 907, 9070, 908, 9080,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var slices = (global::System.Collections.IDictionary)kernel.PlatformAuthority.GetType().GetField("_exactMappingSlices", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = slices[mapping.Mapping.MappingId]!;
        provider.AcquireCallback = () => { if (sourceThrow) provider.ThrowSource = true; else slices.Remove(mapping.Mapping.MappingId); };
        var failed = kernel.MovePlatformOwnedBuffer(source, target, buffer, mapping);
        Assert.False(failed.IsSuccess);
        Assert.True(buffer.IsValid);
        slices[mapping.Mapping.MappingId] = original;
        provider.ThrowSource = false;
        provider.AcquireCallback = null;
        var retried = kernel.MovePlatformOwnedBuffer(source, target, buffer, mapping);
        Assert.True(retried.IsSuccess, retried.Message);
        Assert.False(buffer.IsValid);
        Assert.True(retried.Value!.Buffer.IsValid);
        Assert.Equal(1, provider.AcquireCalls);
        Assert.Equal(1, provider.BeginRevokeCalls);
    }
    [Fact]
    public void AcquireThrowBalancesPendingWithoutTransferringSource()
    {
        var provider = new MoveProvider();
        var (kernel, source, target, buffer, mapping) = CreateMappedSource(provider, 905, 9050, 906, 9060,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)kernel.PlatformAuthority.GetType().GetField("_mappings", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = records[mapping.Mapping.MappingId]!;
        provider.AcquireCallback = () => throw new IOException("Acquire response lost.");
        Assert.Throws<IOException>(() => kernel.MovePlatformOwnedBuffer(source, target, buffer, mapping));
        Assert.False((bool)original.GetType().GetProperty("AcquireInFlight")!.GetValue(original)!);
        Assert.True(buffer.IsValid);
        Assert.Equal(1, provider.AcquireCalls);
        Assert.DoesNotContain("publish:9060", provider.MoveLog);
        provider.AcquireCallback = null;
        Assert.False(kernel.MovePlatformOwnedBuffer(source, target, buffer, mapping).IsSuccess);
        Assert.Equal(1, provider.AcquireCalls);
    }
    [Fact]
    public void AcquireReentryCannotReleaseReservationOrRepeatProviderAndMoveCompletes()
    {
        var provider = new MoveProvider();
        var (kernel, source, target, buffer, mapping) = CreateMappedSource(provider, 903, 9030, 904, 9040,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        var identity = mapping.Mapping.DomainBinding.Subject;
        provider.AcquireCallback = () =>
        {
            var refused = Task.Run(() => kernel.PlatformAuthority.CommitRegionReservationRelease(mapping.Mapping, identity, kernel.Regions))
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Assert.False(refused.IsSuccess);
            var repeated = kernel.PlatformAuthority.AcquireClosedRegionMappingFromConsumer(mapping, identity,
                PlatformMemoryConsumerClass.ExternalExecutionDomain, PlatformMemoryAcquireRequirement.AcquisitionFence);
            Assert.False(repeated.IsSuccess);
            Assert.Equal(1, provider.AcquireCalls);
            Assert.True(buffer.IsValid);
        };
        var moved = kernel.MovePlatformOwnedBuffer(source, target, buffer, mapping);
        Assert.True(moved.IsSuccess, moved.Message);
        Assert.False(buffer.IsValid);
        Assert.True(moved.Value!.Buffer.IsValid);
        Assert.Equal(1, provider.AcquireCalls);
    }
    [Theory]
    [InlineData("mapping")][InlineData("reservation")][InlineData("slice")]
    public void AcquireCallbackSourceLossCannotTransferOrPublishTarget(string outcome)
    {
        var provider = new MoveProvider();
        var (kernel, source, target, buffer, mapping) = CreateMappedSource(provider, 901, 9010, 902, 9020,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        var flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        var bridge = kernel.PlatformAuthority;
        var records = (global::System.Collections.IDictionary)bridge.GetType().GetField("_mappings", flags)!.GetValue(bridge)!;
        var original = records[mapping.Mapping.MappingId]!;
        provider.AcquireCallback = () =>
        {
            if (outcome == "mapping") records.Remove(mapping.Mapping.MappingId);
            if (outcome == "reservation") original.GetType().GetProperty("LocalReservationReleased")!.SetValue(original, true);
            if (outcome == "slice")
                ((global::System.Collections.IDictionary)bridge.GetType().GetField("_exactMappingSlices", flags)!.GetValue(bridge)!).Remove(mapping.Mapping.MappingId);
        };
        var moved = kernel.MovePlatformOwnedBuffer(source, target, buffer, mapping);
        Assert.False(moved.IsSuccess);
        Assert.True(buffer.IsValid);
        Assert.Equal(new DomainId(9010), kernel.Regions.Snapshot().Single(r => r.Handle == buffer.Handle).Owner.DomainId);
        Assert.Equal(1, provider.AcquireCalls);
        Assert.DoesNotContain("publish:9020", provider.MoveLog);
        Assert.Single(provider.MoveLog, entry => entry == "publish:9010");
        Assert.NotNull(original.GetType().GetProperty("AcquireObservation")!.GetValue(original));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TargetPublicationFailureCannotHidePendingMappingOrReturnExitingOwner(bool exitTarget)
    {
        var provider = new MoveProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, source) = TestFixtures.Create(kernel, 896, 8960);
        var (_, target) = TestFixtures.Create(kernel, 897, 8970);
        var (_, admin) = TestFixtures.Create(kernel, 898, 8980);
        var budgetCapability = kernel.MintCapability(new(8980), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var targetBudget = kernel.AdmitProcessBudget(admin, budgetCapability, target, "failed-move-target",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value;
        var buffer = kernel.AllocateBuffer<byte>(source, 512).Value!;
        var sb = kernel.BindPlatformDomain(source).Value!;
        var tb = kernel.BindPlatformDomain(target).Value!;
        var sc = MintRegionCapability(kernel, source, buffer.Handle, CapabilityRights.Map | CapabilityRights.Read);
        var tc = MintRegionCapability(kernel, target, buffer.Handle, CapabilityRights.Map | CapabilityRights.Read);
        var mapping = kernel.MapPlatformOwnedRegionSlice(source, sb, sc, buffer.Handle, 0, 512, PlatformMemoryAccess.Read).Value!;
        var publications = 0;
        provider.PublicationCallback = () =>
        {
            if (++publications != 2) return;
            provider.PublicationSupported = false;
            if (exitTarget) _ = kernel.TerminateProcess(target);
            else provider.CompletionState = PlatformCompletionState.Draining;
        };
        var moved = kernel.MovePlatformOwnedBuffer(source, target, buffer, mapping,
            new(tb, tc, PlatformMemoryAccess.Read));
        Assert.False(moved.IsSuccess);
        Assert.False(buffer.IsValid); // Transfer already committed; no rollback claim.
        if (!exitTarget)
        {
            Assert.Contains("cleanup refused", moved.Message);
            var region = Assert.Single(kernel.Regions.Snapshot(), r => r.Owner.DomainId == new DomainId(8970));
            Assert.Equal(RegionState.Owned, region.State);
            Assert.True(kernel.PlatformAuthority.HasActiveMapping(region.Handle));
            Assert.False(kernel.TerminateProcess(target).IsSuccess);
        }
        else Assert.False(kernel.Processes.Resolve(target).IsSuccess &&
            kernel.Processes.Resolve(target).Value!.State == ProcessState.Running);
        Assert.Equal(2, publications);
        Assert.Equal(exitTarget ? 0UL : 512UL, kernel.QueryBudget(targetBudget.ProcessBudget).Value!.Usage
            .Single(item => item.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        var begins = provider.BeginRevokeCalls;
        provider.PublicationCallback = null;
        provider.CompletionState = PlatformCompletionState.Closed;
        if (!exitTarget) Assert.True(kernel.TerminateProcess(target).IsSuccess);
        Assert.Equal(begins, provider.BeginRevokeCalls);
        Assert.Equal(2, publications);
        Assert.Equal(0, provider.AcquireCalls);
        Assert.Equal(0UL, kernel.QueryBudget(targetBudget.ProcessBudget).Value!.Usage
            .Single(item => item.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot(),
            r => r.Owner.DomainId == new DomainId(8970)).State);
        Assert.Equal(ProcessState.Exited, kernel.Processes.ResolveTerminalState(target).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransferBudgetRefusalAfterClosureRetainsHandoffAndRetriesWithoutProviderEffects(bool writable)
    {
        var provider = new MoveProvider();
        var (kernel, source, target, buffer, mapping) = CreateMappedSource(provider,
            893, 8930, 894, 8940, writable ? PlatformMemoryAccess.Read | PlatformMemoryAccess.Write : PlatformMemoryAccess.Read);
        var (_, admin) = TestFixtures.Create(kernel, 895, 8950);
        var cap = kernel.MintCapability(new(8950), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, cap, target, "move-target",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value;
        var held = kernel.ReserveBudget(target, [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)],
            BudgetReservationLifetime.LocalResource).Value!;
        var refused = kernel.MovePlatformOwnedBuffer(source, target, buffer, mapping);
        Assert.Equal(KernelError.BudgetExceeded, refused.Error);
        Assert.True(buffer.IsValid);
        var lifecycle = kernel.QueryPlatformRegionMappingLifecycle(source, mapping.Mapping).Value;
        Assert.Equal(PlatformExternalClosureState.Closed, lifecycle.PlatformClosure);
        Assert.True(lifecycle.LocalReservationReleased);
        Assert.True(kernel.ReleaseBudget(target, held.Reservation).IsSuccess);
        var retry = kernel.MovePlatformOwnedBuffer(source, target, buffer, mapping);
        Assert.True(retry.IsSuccess, retry.Message);
        Assert.False(buffer.IsValid); Assert.True(retry.Value!.Buffer.IsValid);
        Assert.Equal(new RegionGeneration(2), retry.Value.Buffer.Handle.Generation);
        Assert.Equal(1, provider.BeginRevokeCalls); Assert.Equal(1, provider.ObserveCalls);
        Assert.Equal(writable ? 1 : 0, provider.AcquireCalls);
        Assert.Single(provider.MoveLog, entry => entry.StartsWith("publish:", StringComparison.Ordinal));
        Assert.Equal(512UL, kernel.QueryBudget(budget.ProcessBudget).Value!.Usage
            .Single(item => item.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Fact]
    public void PublicationCallbackReentryRefusesBeforeEffectsAndOriginalMoveStillTransfers()
    {
        var provider = new MoveProvider();
        var (kernel, source, target, buffer, mapping) = CreateMappedSource(
            provider, 891, 8910, 892, 8920, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        KernelResult<PlatformOwnedBufferMoveResult<byte>>? nested = null;
        provider.PublicationCallback = () =>
        {
            provider.PublicationCallback = null;
            nested = Task.Run(() => kernel.MovePlatformOwnedBuffer(source, target, buffer, mapping))
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        };
        var moved = kernel.MovePlatformOwnedBuffer(source, target, buffer, mapping);
        Assert.Equal(KernelError.PlatformBindingDraining, nested!.Value.Error);
        Assert.True(moved.IsSuccess, moved.Message);
        Assert.False(buffer.IsValid); Assert.True(moved.Value!.Buffer.IsValid);
        Assert.Equal(1, provider.BeginRevokeCalls); Assert.Equal(1, provider.ObserveCalls);
        Assert.Equal(1, provider.AcquireCalls);
        Assert.Single(provider.MoveLog, entry => entry.StartsWith("publish:", StringComparison.Ordinal));
    }

    [Fact]
    public void WritableSourceClosesAcquiresTransfersAndPublishesTargetMapping()
    {
        var provider = new MoveProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, source) = TestFixtures.Create(kernel, 801, 810);
        var (_, target) = TestFixtures.Create(kernel, 802, 820);
        var buffer = kernel.AllocateBuffer<byte>(source, 1024).Value!;
        var sourceBinding = kernel.BindPlatformDomain(source).Value!;
        var targetBinding = kernel.BindPlatformDomain(target).Value!;
        var sourceCapability = MintRegionCapability(
            kernel, source, buffer.Handle,
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write);
        var targetCapability = MintRegionCapability(
            kernel, target, buffer.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var sourceMapping = kernel.MapPlatformOwnedRegionSlice(
            source,
            sourceBinding,
            sourceCapability,
            buffer.Handle,
            64,
            512,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;

        var moved = kernel.MovePlatformOwnedBuffer(
            source,
            target,
            buffer,
            sourceMapping,
            new PlatformMoveTargetMappingRequest(
                targetBinding,
                targetCapability,
                PlatformMemoryAccess.Read));

        Assert.True(moved.IsSuccess, moved.Message);
        Assert.False(buffer.IsValid);
        Assert.True(moved.Value!.Buffer.IsValid);
        Assert.Equal(new RegionGeneration(2), moved.Value.Buffer.Handle.Generation);
        Assert.Equal(
            PlatformMoveTargetExposureState.ExactMappedAndPublished,
            moved.Value.TargetExposure);
        Assert.NotNull(moved.Value.TargetMapping);
        Assert.NotNull(moved.Value.TargetPublication);
        Assert.True(moved.Value.TargetPublication!.Value.IsSatisfied);
        Assert.Equal(1, provider.AcquireCalls);
        Assert.Equal(
            new[]
            {
                "publish:810",
                "begin-revoke:810",
                "observe:Closed",
                "acquire:810",
                "map:820",
                "publish:820",
            },
            provider.MoveLog.Skip(1).ToArray());

        var targetOwner = new RegionOwner(new DomainId(820), target.Generation);
        Assert.True(kernel.Regions.Validate(moved.Value.Buffer.Handle, targetOwner).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration,
            kernel.Regions.Validate(sourceMapping.Region, new RegionOwner(new DomainId(810), source.Generation)).Error);
    }

    [Fact]
    public void DrainingSourceKeepsOwnerAndReservationPinnedUntilRetryReachesClosed()
    {
        var provider = new MoveProvider { CompletionState = PlatformCompletionState.Draining };
        var (kernel, source, target, buffer, sourceMapping) = CreateMappedSource(
            provider, 803, 830, 804, 840, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);

        var first = kernel.MovePlatformOwnedBuffer(
            source,
            target,
            buffer,
            sourceMapping);

        Assert.False(first.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, first.Error);
        Assert.True(buffer.IsValid);
        Assert.Equal(new RegionGeneration(1), buffer.Handle.Generation);
        Assert.Equal(KernelError.PlatformBindingActive,
            kernel.TransferRegion(source, target, buffer).Error);
        Assert.Equal(KernelError.PlatformBindingDraining,
            kernel.PreparePlatformRegionMappingForConsumer(
                source,
                sourceMapping,
                PlatformMemoryConsumerClass.ExternalExecutionDomain,
                PlatformMemoryVisibilityRequirement.PublicationFence).Error);

        provider.CompletionState = PlatformCompletionState.Closed;
        var retry = kernel.MovePlatformOwnedBuffer(
            source,
            target,
            buffer,
            sourceMapping);

        Assert.True(retry.IsSuccess, retry.Message);
        Assert.Equal(new RegionGeneration(2), retry.Value!.Buffer.Handle.Generation);
        Assert.Equal(1, provider.AcquireCalls);
        Assert.Equal(2, provider.ObserveCalls);
    }

    [Fact]
    public void StaleAcquireEvidenceCannotReleaseReservationOrMoveOwnership()
    {
        var provider = new MoveProvider { ReturnStaleAcquireGeneration = true };
        var (kernel, source, target, buffer, sourceMapping) = CreateMappedSource(
            provider, 805, 850, 806, 860, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);

        var move = kernel.MovePlatformOwnedBuffer(source, target, buffer, sourceMapping);

        Assert.False(move.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, move.Error);
        Assert.True(buffer.IsValid);
        Assert.Equal(new RegionGeneration(1), buffer.Handle.Generation);
        Assert.Equal(KernelError.PlatformBindingActive,
            kernel.TransferRegion(source, target, buffer).Error);

        provider.ReturnStaleAcquireGeneration = false;
        var retry = kernel.MovePlatformOwnedBuffer(source, target, buffer, sourceMapping);
        Assert.False(retry.IsSuccess);
        Assert.True(buffer.IsValid);
        Assert.Equal(1, provider.AcquireCalls);
        Assert.Equal(KernelError.PlatformBindingActive, kernel.TransferRegion(source, target, buffer).Error);
    }

    [Fact]
    public void WritableMoveFailsClosedWhenProviderCannotAcquire()
    {
        var provider = new MoveProvider { AcquireSupported = false };
        var (kernel, source, target, buffer, sourceMapping) = CreateMappedSource(
            provider, 807, 870, 808, 880, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);

        var move = kernel.MovePlatformOwnedBuffer(source, target, buffer, sourceMapping);

        Assert.False(move.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, move.Error);
        Assert.True(buffer.IsValid);
        Assert.Equal(KernelError.PlatformBindingActive,
            kernel.TransferRegion(source, target, buffer).Error);
    }

    [Fact]
    public void ReadOnlyExternalMappingDoesNotRequireAcquireBeforeMove()
    {
        var provider = new MoveProvider { AcquireSupported = false };
        var (kernel, source, target, buffer, sourceMapping) = CreateMappedSource(
            provider, 809, 890, 810, 900, PlatformMemoryAccess.Read);

        var move = kernel.MovePlatformOwnedBuffer(source, target, buffer, sourceMapping);

        Assert.True(move.IsSuccess, move.Message);
        Assert.Equal(0, provider.AcquireCalls);
        Assert.Equal(new RegionGeneration(2), move.Value!.Buffer.Handle.Generation);
    }

    [Fact]
    public void SourcePublicationFailureOccursBeforeRevocationAndOwnershipMutation()
    {
        var provider = new MoveProvider { PublicationSupported = false };
        var (kernel, source, target, buffer, sourceMapping) = CreateMappedSource(
            provider, 811, 910, 812, 920, PlatformMemoryAccess.Read);

        var move = kernel.MovePlatformOwnedBuffer(source, target, buffer, sourceMapping);

        Assert.False(move.IsSuccess);
        Assert.Equal(KernelError.PlatformUnsupported, move.Error);
        Assert.Equal(0, provider.BeginRevokeCalls);
        Assert.True(buffer.IsValid);
        Assert.Equal(new RegionGeneration(1), buffer.Handle.Generation);
        Assert.Equal(KernelError.PlatformBindingActive,
            kernel.TransferRegion(source, target, buffer).Error);
    }

    [Fact]
    public void TargetCapabilityIsRejectedBeforeSourceDrainStarts()
    {
        var provider = new MoveProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, source) = TestFixtures.Create(kernel, 813, 930);
        var (_, target) = TestFixtures.Create(kernel, 814, 940);
        var buffer = kernel.AllocateBuffer<byte>(source, 256).Value!;
        var sourceBinding = kernel.BindPlatformDomain(source).Value!;
        var targetBinding = kernel.BindPlatformDomain(target).Value!;
        var sourceCapability = MintRegionCapability(
            kernel, source, buffer.Handle, CapabilityRights.Map | CapabilityRights.Read);
        var badTargetCapability = MintRegionCapability(
            kernel, target, buffer.Handle, CapabilityRights.Read);
        var mapping = kernel.MapPlatformOwnedRegionSlice(
            source, sourceBinding, sourceCapability, buffer.Handle,
            0, 128, PlatformMemoryAccess.Read).Value!;

        var move = kernel.MovePlatformOwnedBuffer(
            source,
            target,
            buffer,
            mapping,
            new PlatformMoveTargetMappingRequest(
                targetBinding,
                badTargetCapability,
                PlatformMemoryAccess.Read));

        Assert.False(move.IsSuccess);
        Assert.Equal(KernelError.InsufficientRights, move.Error);
        Assert.Equal(0, provider.BeginRevokeCalls);
        Assert.True(buffer.IsValid);
        Assert.Equal(KernelError.PlatformBindingActive,
            kernel.TransferRegion(source, target, buffer).Error);
    }

    [Fact]
    public void UnsupportedTargetMappingLeavesCompletedMoveAsLocalOwnershipFallback()
    {
        var provider = new MoveProvider { RejectTargetDomain = new DomainId(960) };
        var kernel = new RuntimeKernel(provider);
        var (_, source) = TestFixtures.Create(kernel, 815, 950);
        var (_, target) = TestFixtures.Create(kernel, 816, 960);
        var buffer = kernel.AllocateBuffer<byte>(source, 512).Value!;
        var sourceBinding = kernel.BindPlatformDomain(source).Value!;
        var targetBinding = kernel.BindPlatformDomain(target).Value!;
        var sourceCapability = MintRegionCapability(
            kernel, source, buffer.Handle,
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write);
        var targetCapability = MintRegionCapability(
            kernel, target, buffer.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        var sourceMapping = kernel.MapPlatformOwnedRegionSlice(
            source, sourceBinding, sourceCapability, buffer.Handle,
            32, 128, PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;

        var moved = kernel.MovePlatformOwnedBuffer(
            source,
            target,
            buffer,
            sourceMapping,
            new PlatformMoveTargetMappingRequest(
                targetBinding,
                targetCapability,
                PlatformMemoryAccess.Read));

        Assert.True(moved.IsSuccess, moved.Message);
        Assert.Equal(
            PlatformMoveTargetExposureState.LocalOwnershipFallback,
            moved.Value!.TargetExposure);
        Assert.Null(moved.Value.TargetMapping);
        Assert.Equal(KernelError.PlatformUnsupported, moved.Value.TargetExposureError);
        Assert.Equal(new RegionGeneration(2), moved.Value.Buffer.Handle.Generation);
        Assert.False(buffer.IsValid);
        Assert.True(moved.Value.Buffer.IsValid);
        Assert.Equal(1, provider.AcquireCalls);
    }

    [Fact]
    public void AcquireContractUsesDedicatedEvidenceTypesAndNoProviderAuthorityLeaksIntoMoveResult()
    {
        Assert.NotEqual(typeof(PlatformMemoryAcquireRequirement), typeof(PlatformMemoryVisibilityRequirement));
        Assert.NotEqual(typeof(PlatformMemoryAcquireOutcome), typeof(PlatformMemoryVisibilityOutcome));

        var forbidden = new[]
        {
            typeof(PlatformProviderDomainLease),
            typeof(PlatformProviderRegionMappingLease),
            typeof(PlatformOperationIdentity),
        };
        var moveResultProperties = typeof(PlatformOwnedBufferMoveResult<byte>)
            .GetProperties()
            .Select(static property => property.PropertyType)
            .ToArray();
        foreach (var type in forbidden)
            Assert.DoesNotContain(type, moveResultProperties);
    }

    private static (RuntimeKernel Kernel, ProcessHandle Source, ProcessHandle Target,
        OwnedBuffer<byte> Buffer, PlatformOwnedRegionSliceMapping Mapping) CreateMappedSource(
        MoveProvider provider,
        ulong sourceProcess,
        ulong sourceDomain,
        ulong targetProcess,
        ulong targetDomain,
        PlatformMemoryAccess access)
    {
        var kernel = new RuntimeKernel(provider);
        var (_, source) = TestFixtures.Create(kernel, sourceProcess, sourceDomain);
        var (_, target) = TestFixtures.Create(kernel, targetProcess, targetDomain);
        var buffer = kernel.AllocateBuffer<byte>(source, 512).Value!;
        var sourceBinding = kernel.BindPlatformDomain(source).Value!;
        _ = kernel.BindPlatformDomain(target).Value!;
        var rights = CapabilityRights.Map;
        if ((access & PlatformMemoryAccess.Read) != 0) rights |= CapabilityRights.Read;
        if ((access & PlatformMemoryAccess.Write) != 0) rights |= CapabilityRights.Write;
        var sourceCapability = MintRegionCapability(kernel, source, buffer.Handle, rights);
        var mapping = kernel.MapPlatformOwnedRegionSlice(
            source,
            sourceBinding,
            sourceCapability,
            buffer.Handle,
            16,
            128,
            access).Value!;
        return (kernel, source, target, buffer, mapping);
    }

    private static CapabilityId MintRegionCapability(
        RuntimeKernel kernel,
        ProcessHandle subject,
        RegionHandle region,
        CapabilityRights rights)
    {
        var process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        var capability = kernel.MintCapability(
            process.Value!.DomainId,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.RegionId),
            rights);
        Assert.True(capability.IsSuccess, capability.Message);
        return capability.Value!.CapabilityId;
    }

    private sealed class MoveProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformOwnedRegionMappingProvider,
        IPlatformRegionVisibilityProvider,
        IPlatformRegionRevocationProvider,
        IPlatformRegionAcquireProvider,
        IPlatformProviderIncarnationSource
    {
        private sealed class MappingRecord(
            PlatformProviderOwnedRegionMapping mapping)
        {
            public PlatformProviderOwnedRegionMapping Mapping { get; } = mapping;
            public bool RevocationStarted { get; set; }
            public bool Revoked { get; set; }
        }

        private readonly Dictionary<PlatformProviderDomainLeaseId, PlatformProviderDomainLease> _domains = [];
        private readonly Dictionary<PlatformProviderRegionMappingId, MappingRecord> _mappings = [];
        private readonly Dictionary<PlatformOperationId, (PlatformOperationIdentity Identity, PlatformProviderRegionMappingId MappingId)> _operations = [];
        private ulong _nextDomain = 1;
        private ulong _nextMapping = 1;
        private ulong _nextOperation = 1;

        public PlatformCompletionState CompletionState { get; set; } = PlatformCompletionState.Closed;
        public bool ReturnStaleAcquireGeneration { get; set; }
        public bool AcquireSupported { get; set; } = true;
        public bool ThrowSource { get; set; }
        public PlatformProviderIncarnation CurrentIncarnation => ThrowSource ? throw new IOException("Source unavailable.") : new(1);
        public bool PublicationSupported { get; set; } = true;
        public DomainId? RejectTargetDomain { get; set; }
        public int BeginRevokeCalls { get; private set; }
        public int ObserveCalls { get; private set; }
        public int AcquireCalls { get; private set; }
        public Action? PublicationCallback { get; set; }
        public Action? AcquireCallback { get; set; }
        public List<string> MoveLog { get; } = [];

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("move-test"),
            5,
            PlatformAuthorityFeatures.NeutralDomainBinding |
            PlatformAuthorityFeatures.DirectOwnedRegionMapping);

        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.NeutralDomains,
                PlatformDomainContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.OwnedRegionMapping,
                PlatformOwnedRegionMappingContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.ExplicitMemoryVisibility,
                PlatformRegionAcquireContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
        });

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject)
        {
            var lease = new PlatformProviderDomainLease(
                new PlatformProviderDomainLeaseId(_nextDomain++),
                new PlatformProviderLeaseGeneration(1),
                subject);
            _domains.Add(lease.LeaseId, lease);
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease) =>
            PlatformAuthorityResult.Ok();

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access)
        {
            var exact = MapOwnedRegionSlice(
                domainLease,
                new PlatformRegionSlice(region, 0, region.ByteLength, access));
            return exact.IsSuccess
                ? PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(exact.Value!.Lease)
                : PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                    exact.Status,
                    exact.Message!);
        }

        public PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSlice(
            PlatformProviderDomainLease domainLease,
            PlatformRegionSlice slice)
        {
            if (!_domains.TryGetValue(domainLease.LeaseId, out var live) || live != domainLease)
            {
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Unknown domain lease.");
            }

            if (RejectTargetDomain is { } rejected && domainLease.Subject.DomainId == rejected)
            {
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    PlatformAuthorityStatus.Unsupported,
                    "Target exact mapping is intentionally unavailable.");
            }

            var lease = new PlatformProviderRegionMappingLease(
                new PlatformProviderRegionMappingId(_nextMapping++),
                new PlatformProviderLeaseGeneration(1),
                domainLease,
                slice.Region,
                slice.Access);
            var mapped = new PlatformProviderOwnedRegionMapping(lease, slice);
            _mappings.Add(lease.MappingId, new MappingRecord(mapped));
            MoveLog.Add($"map:{domainLease.Subject.DomainId.Value}");
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Ok(mapped);
        }

        public PlatformAuthorityResult<PlatformRegionVisibilityResult> PrepareRegionMappingForConsumer(
            PlatformRegionVisibilityRequest request)
        {
            if (!_mappings.TryGetValue(request.Mapping.MappingId, out var record) ||
                record.Mapping.Lease != request.Mapping ||
                record.Mapping.Slice != request.Slice ||
                record.RevocationStarted)
            {
                return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Fail(
                    PlatformAuthorityStatus.Revoked,
                    "Mapping is not live for publication.");
            }

            MoveLog.Add($"publish:{request.Mapping.DomainLease.Subject.DomainId.Value}");
            PublicationCallback?.Invoke();
            var outcome = PublicationSupported &&
                          request.Consumer == PlatformMemoryConsumerClass.ExternalExecutionDomain &&
                          request.Requirement == PlatformMemoryVisibilityRequirement.PublicationFence
                ? PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied
                : PlatformMemoryVisibilityOutcome.Unsupported;
            return PlatformAuthorityResult<PlatformRegionVisibilityResult>.Ok(
                new PlatformRegionVisibilityResult(
                    request.Mapping.MappingId,
                    request.Mapping.Generation,
                    request.Slice,
                    request.Consumer,
                    request.Requirement,
                    outcome));
        }

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy)
        {
            if (_mappings.TryGetValue(mapping.MappingId, out var record))
                record.Revoked = true;
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy)
        {
            BeginRevokeCalls++;
            if (!_mappings.TryGetValue(mapping.MappingId, out var record) ||
                record.Mapping.Lease != mapping || record.Revoked)
            {
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Unknown mapping.");
            }

            record.RevocationStarted = true;
            MoveLog.Add($"begin-revoke:{mapping.DomainLease.Subject.DomainId.Value}");
            var operation = new PlatformOperationIdentity(
                new PlatformOperationId(_nextOperation++),
                new PlatformOperationGeneration(1),
                mapping.DomainLease);
            _operations.Add(operation.OperationId, (operation, mapping.MappingId));
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(
                new PlatformRegionRevocationTicket(
                    mapping.MappingId,
                    mapping.Generation,
                    operation));
        }

        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
            PlatformOperationIdentity operation)
        {
            ObserveCalls++;
            if (!_operations.TryGetValue(operation.OperationId, out var entry) ||
                entry.Identity != operation)
            {
                return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Unknown operation.");
            }

            if (CompletionState == PlatformCompletionState.Closed)
                _mappings[entry.MappingId].Revoked = true;
            MoveLog.Add($"observe:{CompletionState}");
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(
                new PlatformCompletionReceipt(
                    operation.OperationId,
                    operation.Generation,
                    operation.DomainLease,
                    CompletionState));
        }

        public PlatformAuthorityResult<PlatformRegionAcquireResult> AcquireRegionMappingFromConsumer(
            PlatformRegionAcquireRequest request)
        {
            AcquireCalls++;
            AcquireCallback?.Invoke();
            if (!_mappings.TryGetValue(request.Mapping.MappingId, out var record) ||
                record.Mapping.Lease != request.Mapping ||
                record.Mapping.Slice != request.Slice ||
                !record.Revoked)
            {
                return PlatformAuthorityResult<PlatformRegionAcquireResult>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Mapping has not reached exact closure.");
            }

            MoveLog.Add($"acquire:{request.Mapping.DomainLease.Subject.DomainId.Value}");
            var generation = ReturnStaleAcquireGeneration
                ? new PlatformProviderLeaseGeneration(request.Mapping.Generation.Value + 1)
                : request.Mapping.Generation;
            var outcome = AcquireSupported &&
                          request.Producer == PlatformMemoryConsumerClass.ExternalExecutionDomain &&
                          request.Requirement == PlatformMemoryAcquireRequirement.AcquisitionFence
                ? PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied
                : PlatformMemoryAcquireOutcome.Unsupported;
            return PlatformAuthorityResult<PlatformRegionAcquireResult>.Ok(
                new PlatformRegionAcquireResult(
                    request.Mapping.MappingId,
                    generation,
                    request.Slice,
                    request.Producer,
                    request.Requirement,
                    outcome));
        }
    }
}
