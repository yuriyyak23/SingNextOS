using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Ownership;

public sealed class RegionUseTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DomainLoanReturnCapacityRefusesWholeBatchAndPreservesClosureForRetry(bool exhaustFirst)
    {
        var kernel = new RuntimeKernel(new SingPlus.Platform.Host.HostPlatformAuthorityProvider());
        var owner = TestFixtures.Create(kernel, 3168, 3268).Handle;
        var borrower = TestFixtures.Create(kernel, 3169, 3269).Handle;
        Assert.True(kernel.BindPlatformDomain(borrower).IsSuccess);
        var first = kernel.AllocateBuffer<byte>(owner, 4).Value!;
        var second = kernel.AllocateBuffer<byte>(owner, 4).Value!;
        var leftLease = kernel.Regions.Loan(first.Handle, new(new(3268), 1), new(new(3269), 1)).Value;
        var rightLease = kernel.Regions.Loan(second.Handle, new(new(3268), 1), new(new(3269), 1)).Value;
        var leftUse = kernel.AcquireBorrowRegionUse(owner, borrower, leftLease, RegionUseMode.ReadOnly, new(0, 4)).Value!;
        var rightUse = kernel.AcquireBorrowRegionUse(owner, borrower, rightLease, RegionUseMode.ReadOnly, new(0, 4)).Value!;
        var regions = typeof(RegionAuthority).GetField("_regions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.Regions)!;
        var records = (global::System.Collections.IEnumerable)regions.GetType().GetProperty("Values")!.GetValue(regions)!;
        var exhausted = exhaustFirst ? first.Handle : second.Handle;
        var record = records.Cast<object>().Single(r => (RegionId)r.GetType().GetProperty("Id")!.GetValue(r)! == exhausted.RegionId);
        record.GetType().GetProperty("MutationEpoch")!.SetValue(record, new MutationEpoch(ulong.MaxValue));
        var before = kernel.Regions.Snapshot().ToDictionary(r => r.Handle);
        Assert.Equal(KernelError.CapacityExhausted, kernel.TerminateProcess(borrower).Error);
        var teardown = kernel.QueryProcessTeardown(borrower).Value!;
        Assert.True(teardown.PlatformDomainClosed);
        Assert.False(teardown.LocalReclaimCompleted);
        Assert.Equal(KernelError.CapacityExhausted, teardown.BlockingError);
        Assert.Equal(RegionUseState.Active, kernel.Regions.SnapshotUses().Single(u => u.Handle == leftUse.Handle).State);
        Assert.Equal(RegionUseState.Active, kernel.Regions.SnapshotUses().Single(u => u.Handle == rightUse.Handle).State);
        foreach (var region in kernel.Regions.Snapshot())
        {
            Assert.Equal(RegionState.Loaned, region.State);
            Assert.Equal(before[region.Handle].MutationEpoch, region.MutationEpoch);
        }
        var retry = kernel.ObserveProcessTeardown(borrower);
        Assert.True(retry.IsSuccess, retry.Message);
        Assert.True(retry.Value!.PlatformDomainClosed);
        Assert.Equal(KernelError.CapacityExhausted, retry.Value.BlockingError);
        Assert.All(kernel.Regions.Snapshot(), r => Assert.Equal(RegionState.Loaned, r.State));
        var healthyLease = exhaustFirst ? rightLease : leftLease;
        var healthyUse = exhaustFirst ? rightUse : leftUse;
        var healthyBuffer = exhaustFirst ? second : first;
        Assert.True(kernel.RevokeBorrow(owner, healthyLease).IsSuccess);
        Assert.True(kernel.ReleaseRegionUse(borrower, healthyUse.Handle).IsSuccess);
        Assert.True(kernel.ReleaseRegion(owner, healthyBuffer).IsSuccess);
        Assert.Equal(KernelError.CapacityExhausted, kernel.ObserveProcessTeardown(borrower).Value!.BlockingError);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == healthyBuffer.Handle).State);
        Assert.Equal(RegionState.Loaned, kernel.Regions.Snapshot().Single(r => r.Handle == exhausted).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void FinalBorrowerDomainExitClosesOnlyOriginalLocalBorrowUses(int priorClosure)
    {
        var kernel = new RuntimeKernel();
        var owner = TestFixtures.Create(kernel, 3178, 3278).Handle;
        var borrower = TestFixtures.Create(kernel, 3179, 3279).Handle;
        var cohort = TestFixtures.Create(kernel, 3180, 3279).Handle;
        var other = TestFixtures.Create(kernel, 3181, 3280).Handle;
        var buffer = kernel.AllocateBuffer<byte>(owner, 4).Value!;
        var outside = kernel.AllocateBuffer<byte>(other, 4).Value!;
        var unrelated = kernel.AcquireRegionUse(other, outside.Handle, RegionUseMode.ReadOnly, new(0, 4)).Value!;
        var lease = kernel.Regions.Loan(buffer.Handle, new(new(3278), 1), new(new(3279), 1)).Value;
        var use = kernel.AcquireBorrowRegionUse(owner, borrower, lease, RegionUseMode.ReadOnly, new(0, 4)).Value!;
        if (priorClosure == 1) Assert.True(kernel.ReturnBorrow(borrower, lease).IsSuccess);
        if (priorClosure == 2) Assert.True(kernel.RevokeBorrow(owner, lease).IsSuccess);
        Assert.True(kernel.TerminateProcess(borrower).IsSuccess);
        Assert.True(kernel.Domains.Contains(new(3279)));
        Assert.NotEqual(RegionUseState.Released, kernel.Regions.SnapshotUses().Single(u => u.Handle == use.Handle).State);
        Assert.True(kernel.TerminateProcess(cohort).IsSuccess);
        Assert.False(kernel.Domains.Contains(new(3279)));
        Assert.Equal(RegionUseState.Released, kernel.Regions.SnapshotUses().Single(u => u.Handle == use.Handle).State);
        Assert.Equal(RegionUseState.Active, kernel.Regions.SnapshotUses().Single(u => u.Handle == unrelated.Handle).State);
        Assert.True(kernel.ReleaseRegion(owner, buffer).IsSuccess);
        Assert.False(kernel.ReleaseRegion(other, outside).IsSuccess);
        Assert.True(kernel.ReleaseRegionUse(other, unrelated.Handle).IsSuccess);
        Assert.True(kernel.ReleaseRegion(other, outside).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void BorrowUseAdmissionRefusesOwnerOrBorrowerExit(bool ownerExit)
    {
        var kernel = new RuntimeKernel();
        var owner = TestFixtures.Create(kernel, 3198, 3298).Handle;
        var borrower = TestFixtures.Create(kernel, 3199, 3299).Handle;
        var buffer = kernel.AllocateBuffer<byte>(owner, 4).Value!;
        var lease = kernel.Regions.Loan(buffer.Handle, new(new(3298), 1), new(new(3299), 1)).Value;
        kernel.BeforeBorrowRegionUseAdmissionQualificationHook = () =>
        {
            kernel.BeforeBorrowRegionUseAdmissionQualificationHook = null;
            var worker = Task.Run(() => kernel.TerminateProcess(ownerExit ? owner : borrower));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(worker.Result.IsSuccess, worker.Result.Message);
        };
        var acquired = kernel.AcquireBorrowRegionUse(owner, borrower, lease, RegionUseMode.ReadOnly, new(0, 4));
        Assert.Equal(KernelError.StaleHandle, acquired.Error);
        Assert.Empty(kernel.Regions.SnapshotUses());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void BorrowUseTraceRefusesChangedLeaseOrProcessAndRetainsOriginalUse(int fault)
    {
        var clock = new ReleaseClock();
        var kernel = new RuntimeKernel(null, clock);
        var owner = TestFixtures.Create(kernel, 3188, 3288).Handle;
        var borrower = TestFixtures.Create(kernel, 3189, 3289).Handle;
        var buffer = kernel.AllocateBuffer<byte>(owner, 4).Value!;
        var lease = kernel.Regions.Loan(buffer.Handle, new(new(3288), 1), new(new(3289), 1)).Value;
        Assert.True(kernel.StartTraceSession(borrower, 8).IsSuccess);
        RegionUseHandle exact = default;
        var observed = false;
        clock.Next = () =>
        {
            exact = Assert.Single(kernel.Regions.SnapshotUses()).Handle;
            var worker = Task.Run(() => fault switch
            {
                0 => kernel.ReturnBorrow(borrower, lease),
                1 => kernel.RevokeBorrow(owner, lease),
                2 => kernel.TerminateProcess(borrower),
                _ => kernel.TerminateProcess(owner)
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(worker.Result.IsSuccess, worker.Result.Message);
            observed = true;
        };
        var acquired = kernel.AcquireBorrowRegionUse(owner, borrower, lease, RegionUseMode.ReadOnly, new(0, 4));
        Assert.True(observed);
        Assert.False(acquired.IsSuccess);
        var retained = Assert.Single(kernel.Regions.SnapshotUses());
        Assert.Equal(exact, retained.Handle);
        Assert.Equal(fault == 2 ? RegionUseState.Released : RegionUseState.Invalidated, retained.State);
        if (fault < 2)
        {
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == buffer.Handle).State);
            Assert.True(kernel.ReleaseRegionUse(borrower, exact).IsSuccess);
            Assert.True(kernel.ReleaseRegion(owner, buffer).IsSuccess);
        }
        else if (fault == 2)
        {
            Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(borrower).Error);
            Assert.False(kernel.ReleaseRegionUse(borrower, exact).IsSuccess);
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == buffer.Handle).State);
            Assert.Equal(exact, Assert.Single(kernel.Regions.SnapshotUses()).Handle);
            return;
        }
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == buffer.Handle).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OwnedUseAdmissionCannotFollowCohortActorExit(bool typed)
    {
        var kernel = new RuntimeKernel();
        var payer = TestFixtures.Create(kernel, 3195, 3295).Handle;
        var actor = TestFixtures.Create(kernel, 3196, 3295).Handle;
        var buffer = kernel.AllocateBuffer<byte>(payer, 4).Value!;
        kernel.BeforeOwnedRegionUseAdmissionQualificationHook = () =>
        {
            kernel.BeforeOwnedRegionUseAdmissionQualificationHook = null;
            var worker = Task.Run(() => kernel.TerminateProcess(actor));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(worker.Result.IsSuccess, worker.Result.Message);
        };
        var acquired = typed ? kernel.AcquireRegionUse<byte>(actor, buffer.Handle, RegionUseMode.ReadOnly, 0, 4)
            : kernel.AcquireRegionUse(actor, buffer.Handle, RegionUseMode.ReadOnly, new(0, 4));
        Assert.Equal(KernelError.StaleHandle, acquired.Error);
        Assert.Empty(kernel.Regions.SnapshotUses());
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == buffer.Handle).State);
        Assert.True(kernel.TerminateProcess(payer).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OwnedUseTraceRefusesStalePublicationWithoutErasingOriginalUse(bool exit)
    {
        var clock = new ReleaseClock();
        var kernel = new RuntimeKernel(null, clock);
        var owner = TestFixtures.Create(kernel, 3197, 3297).Handle;
        var buffer = kernel.AllocateBuffer<byte>(owner, 4).Value!;
        Assert.True(kernel.StartTraceSession(owner, 8).IsSuccess);
        RegionUseHandle exact = default;
        var observed = false;
        clock.Next = () =>
        {
            exact = Assert.Single(kernel.Regions.SnapshotUses()).Handle;
            var worker = Task.Run(() => exit ? kernel.TerminateProcess(owner) : kernel.InvalidateRegionUse(owner, exact));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            if (!exit) Assert.True(worker.Result.IsSuccess, worker.Result.Message);
            observed = true;
        };
        var acquired = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ReadOnly, new(0, 4));
        Assert.True(observed);
        Assert.False(acquired.IsSuccess);
        var retained = Assert.Single(kernel.Regions.SnapshotUses());
        Assert.Equal(exact, retained.Handle);
        Assert.Equal(RegionUseState.Invalidated, retained.State);
        if (exit)
        {
            // Ordinary local uses have no external release owner. Authoritative
            // process reclaim can end backing while retaining the use history.
            Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(owner).Error);
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == buffer.Handle).State);
            return;
        }
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == buffer.Handle).State);
        Assert.True(kernel.ReleaseRegionUse(owner, exact).IsSuccess);
        Assert.True(kernel.ReleaseRegion(owner, buffer).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void RegionReleaseObservationAllowsExitAndPreservesCommittedFact(bool scalar, bool throwClock)
    {
        var clock = new ReleaseClock();
        var kernel = new RuntimeKernel(null, clock);
        var owner = TestFixtures.Create(kernel, 3193, 3293).Handle;
        var admin = TestFixtures.Create(kernel, 3194, 3294).Handle;
        var cap = kernel.MintCapability(new(3294), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, cap, owner, "release-observation",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4), new(ServiceBudgetDimension.TraceTelemetryBufferBytes, 1024)]).Value!;
        Assert.True(kernel.StartTraceSession(owner, 4).IsSuccess);
        var buffer = scalar ? null : kernel.AllocateBuffer<byte>(owner, 4).Value!;
        var region = scalar ? kernel.AllocateRegion(owner, 7).Value! : null;
        var handle = scalar ? region!.Handle : buffer!.Handle;
        var observed = false;
        clock.Next = () =>
        {
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == handle).State);
            Assert.Equal(0UL, kernel.QueryBudget(budget.ProcessBudget).Value!.Usage.Single(u => u.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            var worker = Task.Run(() => kernel.TerminateProcess(owner));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(worker.Result.IsSuccess, worker.Result.Message);
            observed = true;
            if (throwClock) throw new InvalidOperationException("post-release clock fault");
        };
        var released = scalar ? kernel.ReleaseRegion(owner, region!) : kernel.ReleaseRegion(owner, buffer!);
        Assert.True(observed);
        Assert.True(released.IsSuccess, released.Message);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(owner).Error);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == handle).State);
    }

    private sealed class ReleaseClock : TimeProvider
    {
        public Action? Next { get; set; }
        public override long GetTimestamp() { var callback = Next; Next = null; callback?.Invoke(); return base.GetTimestamp(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RegionReleaseRechecksExitedCohortActorBeforeBackingCommit(bool scalar)
    {
        var kernel = new RuntimeKernel();
        var payer = TestFixtures.Create(kernel, 3191, 3291).Handle;
        var actor = TestFixtures.Create(kernel, 3192, 3291).Handle;
        var buffer = scalar ? null : kernel.AllocateBuffer<byte>(payer, 4).Value!;
        var region = scalar ? kernel.AllocateRegion(payer, 7).Value! : null;
        var handle = scalar ? region!.Handle : buffer!.Handle;
        var observed = false;
        kernel.BeforeRegionReleaseQualificationHook = () =>
        {
            kernel.BeforeRegionReleaseQualificationHook = null;
            var worker = Task.Run(() => kernel.TerminateProcess(actor));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(worker.Result.IsSuccess, worker.Result.Message);
            Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(actor).Error);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == handle).State);
            observed = true;
        };
        var released = scalar ? kernel.ReleaseRegion(actor, region!) : kernel.ReleaseRegion(actor, buffer!);
        Assert.True(observed);
        Assert.Equal(KernelError.StaleHandle, released.Error);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == handle).State);
        Assert.True(kernel.TerminateProcess(payer).IsSuccess);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == handle).State);
    }

    [Fact]
    public async Task ConcurrentAllocationAndTeardownLeaveNoOwnedRegion()
    {
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var kernel = new RuntimeKernel();
            var (_, process) = TestFixtures.Create(kernel, (ulong)(3100 + iteration),
                (ulong)(3200 + iteration));
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var allocation = Task.Run(async () =>
            {
                await start.Task;
                return kernel.AllocateBuffer<byte>(process, 8);
            });
            var teardown = Task.Run(async () =>
            {
                await start.Task;
                return kernel.TerminateProcess(process);
            });
            start.SetResult(true);
            await Task.WhenAll(allocation, teardown).WaitAsync(TimeSpan.FromSeconds(5));
            var closed = await teardown;
            Assert.True(closed.IsSuccess, closed.Message);
            Assert.All(kernel.Regions.Snapshot(), region =>
                Assert.Equal(RegionState.Released, region.State));
        }
    }

    [Fact]
    public async Task ConcurrentTransferAndTargetTeardownLeaveNoRegionOwnedByExitedTarget()
    {
        for (var iteration = 0; iteration < 16; iteration++)
        {
            var kernel = new RuntimeKernel();
            var (_, source) = TestFixtures.Create(kernel, (ulong)(3300 + iteration),
                (ulong)(3400 + iteration));
            var (_, target) = TestFixtures.Create(kernel, (ulong)(3500 + iteration),
                (ulong)(3600 + iteration));
            var buffer = kernel.AllocateBuffer<byte>(source, 8).Value!;
            var region = kernel.AllocateRegion(source, 42).Value!;
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transfer = Task.Run(async () =>
            {
                await start.Task;
                _ = kernel.TransferRegion(source, target, buffer);
                _ = kernel.TransferRegion(source, target, region);
            });
            var teardown = Task.Run(async () =>
            {
                await start.Task;
                return kernel.TerminateProcess(target);
            });
            start.SetResult(true);
            await Task.WhenAll(transfer, teardown).WaitAsync(TimeSpan.FromSeconds(5));
            var closed = await teardown;
            Assert.True(closed.IsSuccess, closed.Message);
            Assert.DoesNotContain(kernel.Regions.Snapshot(), item =>
                item.Owner.DomainId == new DomainId((ulong)(3600 + iteration)) &&
                item.State != RegionState.Released);
        }
    }

    [Fact]
    public void DomainReclaimPreflightsLaterInvalidatedUseBeforeReleasingEarlierRegion()
    {
        var kernel = new RuntimeKernel();
        var (_, process) = TestFixtures.Create(kernel, 3010, 3020);
        var owner = new RegionOwner(new DomainId(3020), process.Generation);
        var first = kernel.AllocateBuffer<byte>(process, 8).Value!;
        var blocked = kernel.AllocateBuffer<byte>(process, 8).Value!;
        var use = kernel.AcquireRegionUse(process, blocked.Handle,
            RegionUseMode.ReadOnly, new(0, 8)).Value!;
        Assert.True(kernel.InvalidateRegionUse(process, use.Handle).IsSuccess);

        Assert.Throws<InvalidOperationException>(() =>
            kernel.Regions.ReclaimAllForDomain(owner.DomainId));
        Assert.True(kernel.Regions.Validate(first.Handle, owner).IsSuccess);
        Assert.True(kernel.Regions.Validate(blocked.Handle, owner).IsSuccess);
    }

    [Fact]
    public void ManagedSpanWritesDoNotClaimAnAuthorityObservedMutation()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var buffer = kernel.AllocateBuffer<int>(owner, 2).Value!;
        var before = Assert.Single(kernel.Regions.Snapshot()).MutationEpoch;

        buffer.Span[0] = 42;

        Assert.Equal(before, Assert.Single(kernel.Regions.Snapshot()).MutationEpoch);
    }

    [Fact]
    public void SubrangeConflictMatrixAllowsDisjointUsesAndRejectsOverlap()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var buffer = kernel.AllocateBuffer<byte>(owner, 16).Value!;

        var firstRead = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ReadOnly, new(0, 4));
        var secondRead = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ReadOnly, new(4, 4));
        var writer = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ExclusiveWrite, new(8, 4));

        Assert.True(firstRead.IsSuccess, firstRead.Message);
        Assert.True(secondRead.IsSuccess, secondRead.Message);
        Assert.True(writer.IsSuccess, writer.Message);
        var overlappingWriter = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ExclusiveWrite, new(6, 4));
        Assert.False(overlappingWriter.IsSuccess);
        Assert.Equal(KernelError.RegionUseConflict, overlappingWriter.Error);

        Assert.True(kernel.ReleaseRegionUse(owner, firstRead.Value!.Handle).IsSuccess);
        Assert.True(kernel.ReleaseRegionUse(owner, secondRead.Value!.Handle).IsSuccess);
        Assert.True(kernel.ReleaseRegionUse(owner, writer.Value!.Handle).IsSuccess);

        var firstWriter = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ExclusiveWrite, new(0, 4));
        var disjointWriter = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.StagedOutput, new(8, 4));
        Assert.True(firstWriter.IsSuccess, firstWriter.Message);
        Assert.True(disjointWriter.IsSuccess, disjointWriter.Message);
        var overlappingRead = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ReadOnly, new(2, 4));
        Assert.False(overlappingRead.IsSuccess);
        Assert.Equal(KernelError.RegionUseConflict, overlappingRead.Error);
    }

    [Fact]
    public void WritableUseActivationAndReleaseAdvanceEpochExactlyOnce()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;

        var acquired = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ExclusiveWrite, new(0, 8));
        Assert.True(acquired.IsSuccess, acquired.Message);
        Assert.Equal<ulong>(2, acquired.Value!.MutationEpoch.Value);

        Assert.True(kernel.ReleaseRegionUse(owner, acquired.Value.Handle).IsSuccess);
        Assert.Equal<ulong>(3, Assert.Single(kernel.Regions.Snapshot()).MutationEpoch.Value);
        Assert.True(kernel.ReleaseRegionUse(owner, acquired.Value.Handle).IsSuccess);
        Assert.Equal<ulong>(3, Assert.Single(kernel.Regions.Snapshot()).MutationEpoch.Value);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Fact]
    public void ExhaustedMutationEpochCannotReleaseOrInvalidateWritableUse()
    {
        var kernel = new RuntimeKernel();
        var (_, principal) = TestFixtures.Create(kernel, 110, 111);
        var owner = new RegionOwner(kernel.Processes.Resolve(principal).Value!.DomainId,
            principal.Generation);
        var buffer = kernel.AllocateBuffer<byte>(principal, 8).Value!;
        var use = kernel.AcquireRegionUse(principal, buffer.Handle,
            RegionUseMode.ExclusiveWrite, new(0, 8)).Value!;
        var regions = typeof(RegionAuthority).GetField("_regions",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.Regions)!;
        var records = (global::System.Collections.IEnumerable)regions.GetType().GetProperty("Values")!
            .GetValue(regions)!;
        var record = Assert.Single(records.Cast<object>());
        record.GetType().GetProperty("MutationEpoch")!.SetValue(record,
            new MutationEpoch(ulong.MaxValue));

        Assert.Equal(KernelError.CapacityExhausted,
            kernel.Regions.ReleaseUse(use.Handle, owner).Error);
        Assert.Equal(KernelError.CapacityExhausted,
            kernel.Regions.InvalidateUse(use.Handle, owner).Error);
        Assert.Equal(RegionUseState.Active,
            Assert.Single(kernel.Regions.SnapshotUses()).State);
    }

    [Fact]
    public void StaleUseFailsBeforeSubmitAndBetweenSubmitAndPublish()
    {
        var kernel = new RuntimeKernel();
        var (_, source) = TestFixtures.Create(kernel, 1, 10);
        var (_, target) = TestFixtures.Create(kernel, 2, 20);
        var provider = new FakeExternalProvider(kernel);

        var beforeSubmitBuffer = kernel.AllocateBuffer<byte>(source, 8).Value!;
        var beforeSubmitUse = kernel.AcquireRegionUse(source, beforeSubmitBuffer.Handle, RegionUseMode.ReadOnly, new(0, 8)).Value!;
        Assert.True(kernel.TransferRegion(source, target, beforeSubmitBuffer).IsSuccess);

        var rejectedSubmit = provider.Submit(source, beforeSubmitUse.Handle);
        Assert.False(rejectedSubmit.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, rejectedSubmit.Error);
        Assert.Equal(0, provider.AcceptedSubmissions);

        var beforePublishBuffer = kernel.AllocateBuffer<byte>(source, 8).Value!;
        var beforePublishUse = kernel.AcquireRegionUse(source, beforePublishBuffer.Handle, RegionUseMode.ReadOnly, new(0, 8)).Value!;
        Assert.True(provider.Submit(source, beforePublishUse.Handle).IsSuccess);
        Assert.True(kernel.TransferRegion(source, target, beforePublishBuffer).IsSuccess);

        var rejectedPublish = provider.Publish(source, beforePublishUse.Handle);
        Assert.False(rejectedPublish.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, rejectedPublish.Error);
        Assert.Equal(0, provider.Publications);
    }

    [Fact]
    public void ReadOnlyBorrowCannotAuthorizeWriterAndReturnInvalidatesReadUse()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, borrower) = TestFixtures.Create(kernel, 2, 20);
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var ownerIdentity = new RegionOwner(new DomainId(10), owner.Generation);
        var borrowerIdentity = new RegionOwner(new DomainId(20), borrower.Generation);
        var loan = kernel.Regions.Loan(buffer.Handle, ownerIdentity, borrowerIdentity).Value;

        var writer = kernel.AcquireBorrowRegionUse(owner, borrower, loan, RegionUseMode.ExclusiveWrite, new(0, 8));
        Assert.False(writer.IsSuccess);
        Assert.Equal(KernelError.InsufficientRights, writer.Error);

        var reader = kernel.AcquireBorrowRegionUse(owner, borrower, loan, RegionUseMode.ReadOnly, new(0, 8));
        Assert.True(reader.IsSuccess, reader.Message);
        Assert.True(kernel.ReturnBorrow(borrower, loan).IsSuccess);
        var stale = kernel.ValidateRegionUse(borrower, reader.Value!.Handle);
        Assert.False(stale.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);
    }

    [Fact]
    public void WritableUseExcludesBorrowAndSeparatePlatformMappingAdmission()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, borrower) = TestFixtures.Create(kernel, 2, 20);
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var ownerIdentity = new RegionOwner(new DomainId(10), owner.Generation);
        var borrowerIdentity = new RegionOwner(new DomainId(20), borrower.Generation);
        var writer = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ExclusiveWrite, new(0, 8));
        Assert.True(writer.IsSuccess, writer.Message);

        var borrow = kernel.Regions.Loan(buffer.Handle, ownerIdentity, borrowerIdentity);
        var mapping = kernel.Regions.ReservePlatformMapping(buffer.Handle, ownerIdentity);

        Assert.False(borrow.IsSuccess);
        Assert.Equal(KernelError.RegionUseConflict, borrow.Error);
        Assert.False(mapping.IsSuccess);
        Assert.Equal(KernelError.RegionUseConflict, mapping.Error);
    }

    [Fact]
    public void BorrowGrantAdvancesEpochAndInvalidatesPriorOwnerReadUse()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, borrower) = TestFixtures.Create(kernel, 2, 20);
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var ownerIdentity = new RegionOwner(new DomainId(10), owner.Generation);
        var borrowerIdentity = new RegionOwner(new DomainId(20), borrower.Generation);
        var use = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ReadOnly, new(0, 8)).Value!;

        var loan = kernel.Regions.Loan(buffer.Handle, ownerIdentity, borrowerIdentity);

        Assert.True(loan.IsSuccess, loan.Message);
        Assert.Equal<ulong>(use.MutationEpoch.Value + 1, Assert.Single(kernel.Regions.Snapshot()).MutationEpoch.Value);
        Assert.Equal(KernelError.StaleGeneration, kernel.Regions.ValidateUse(use.Handle, ownerIdentity).Error);
    }

    [Fact]
    public void MoveBorrowReclaimAndBackendResetCannotResurrectAUse()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var (_, target) = TestFixtures.Create(kernel, 2, 20);
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var principal = new RegionOwner(new DomainId(10), owner.Generation);
        var use = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ReadOnly, new(0, 8)).Value!;

        Assert.True(kernel.TransferRegion(owner, target, buffer).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, kernel.Regions.ValidateUse(use.Handle, principal).Error);
        _ = kernel.ObservePlatformBackendReset();
        Assert.Equal(KernelError.StaleGeneration, kernel.Regions.ValidateUse(use.Handle, principal).Error);
        Assert.True(kernel.Regions.ReleaseUse(use.Handle, principal).IsSuccess);
        Assert.True(kernel.Regions.ReleaseUse(use.Handle, principal).IsSuccess);

        var reclaimed = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var reclaimedUse = kernel.AcquireRegionUse(owner, reclaimed.Handle, RegionUseMode.ReadOnly, new(0, 8)).Value!;
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, kernel.Regions.ValidateUse(reclaimedUse.Handle, principal).Error);
        Assert.True(kernel.Regions.ReleaseUse(reclaimedUse.Handle, principal).IsSuccess);
        Assert.True(kernel.Regions.ReleaseUse(reclaimedUse.Handle, principal).IsSuccess);
    }

    [Fact]
    public void InvalidationIsIdempotentAndDoesNotReturnOwnership()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var use = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.DevicePrivate, new(0, 8)).Value!;

        Assert.True(kernel.InvalidateRegionUse(owner, use.Handle).IsSuccess);
        Assert.True(kernel.InvalidateRegionUse(owner, use.Handle).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, kernel.ValidateRegionUse(owner, use.Handle).Error);
        Assert.True(kernel.ReleaseRegionUse(owner, use.Handle).IsSuccess);
        Assert.True(kernel.ReleaseRegionUse(owner, use.Handle).IsSuccess);
        Assert.True(kernel.Regions.Validate(buffer.Handle, new(new DomainId(10), owner.Generation)).IsSuccess);
    }

    [Fact]
    public void InvalidRangesAndFutureGatedSharingFailClosed()
    {
        var kernel = new RuntimeKernel();
        var (_, owner) = TestFixtures.Create(kernel, 1, 10);
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;

        Assert.Equal(KernelError.InvalidRegionState, kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ReadOnly, new(-1, 1)).Error);
        Assert.Equal(KernelError.InvalidRegionState, kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ReadOnly, new(0, 0)).Error);
        Assert.Equal(KernelError.InvalidRegionState, kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ReadOnly, new(7, 2)).Error);
        Assert.Equal(KernelError.PlatformUnsupported, kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.SharedReadMostly, new(0, 8)).Error);
    }

    [Fact]
    public void RegionUsePublicSurfaceContainsOnlyLogicalProviderNeutralIdentity()
    {
        var forbidden = new[] { "Cxl", "Hdm", "Dpa", "Hpa", "Pasid", "Requester", "PhysicalAddress", "Route", "Port", "ProviderToken" };
        var surface = typeof(RegionUseDescriptor).Assembly.GetExportedTypes()
            .Where(type => type.Name.Contains("RegionUse", StringComparison.Ordinal) || type == typeof(MutationEpoch))
            .SelectMany(type => new[] { type.Name }
                .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(property => property.Name))
                .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(field => field.Name)))
            .ToArray();

        Assert.NotEmpty(surface);
        Assert.DoesNotContain(surface, name => forbidden.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class FakeExternalProvider(RuntimeKernel kernel)
    {
        public int AcceptedSubmissions { get; private set; }
        public int Publications { get; private set; }

        public KernelResult Submit(ProcessHandle principal, RegionUseHandle use)
        {
            var validation = kernel.ValidateRegionUse(principal, use);
            if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
            AcceptedSubmissions++;
            return KernelResult.Ok();
        }

        public KernelResult Publish(ProcessHandle principal, RegionUseHandle use)
        {
            var validation = kernel.ValidateRegionUse(principal, use);
            if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
            Publications++;
            return KernelResult.Ok();
        }
    }
}
