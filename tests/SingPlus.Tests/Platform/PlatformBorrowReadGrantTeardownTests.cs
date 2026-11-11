using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformBorrowReadGrantTeardownTests
{
    [Fact]
    public void BorrowerExitCannotCloseLocalUsePastUnknownExternalGrant()
    {
        Scenario s = CreateScenario();
        RegionUseDescriptor use = s.Kernel.AcquireBorrowRegionUse(s.Owner, s.Borrower, s.Lease.Handle,
            RegionUseMode.ReadOnly, new(0, 64)).Value!;
        _ = CreateGrant(s);
        s.Provider.BeforeBegin = () => throw new IOException("Original grant closure lost response");
        Assert.False(s.Kernel.TerminateProcess(s.Borrower).IsSuccess);
        s.Provider.BeforeBegin = null;
        Assert.False(s.Kernel.TerminateProcess(s.Borrower).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Borrower).Value!.LocalReclaimCompleted);
        Assert.NotEqual(RegionUseState.Released, s.Kernel.Regions.SnapshotUses().Single(u => u.Handle == use.Handle).State);
        Assert.Equal(RegionState.Loaned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.False(s.Kernel.ReturnBorrow(s.Borrower, s.Lease.Handle).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task ProcessBorrowProgressionAllowsConcurrentOwnerQueriesAndOtherExit(bool observe, bool ownerExits)
    {
        Scenario s = CreateScenario();
        PlatformBorrowReadGrant grant = CreateGrant(s);
        if (observe)
        {
            s.Provider.CompletionState = PlatformCompletionState.Draining;
            Assert.False(s.Kernel.TerminateProcess(ownerExits ? s.Owner : s.Borrower).IsSuccess);
            s.Provider.CompletionState = PlatformCompletionState.Closed;
        }
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        object kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(s.Kernel)!;
        object bridgeGate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        Action callback = () =>
        {
            Assert.False(Monitor.IsEntered(kernelGate));
            Assert.False(Monitor.IsEntered(bridgeGate));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        if (observe) s.Provider.BeforeObserve = callback; else s.Provider.BeforeBegin = callback;
        ProcessHandle exiting = ownerExits ? s.Owner : s.Borrower;
        ProcessHandle other = ownerExits ? s.Borrower : s.Owner;
        Task<KernelResult> progression = Task.Run(() => s.Kernel.TerminateProcess(exiting));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Task queries = Task.Run(() =>
            {
                Assert.True(s.Kernel.QueryProcessTeardown(exiting).IsSuccess);
                Assert.True(s.Kernel.ObserveProcessTeardown(exiting).IsSuccess);
                Assert.False(s.Kernel.TerminateProcess(other).IsSuccess);
                Assert.False(s.Kernel.ReturnBorrow(s.Borrower, s.Lease.Handle).IsSuccess);
                Assert.Equal(RegionState.Loaned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
            });
            await queries.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { release.Set(); }
        Assert.True((await progression).IsSuccess);
        s.Provider.BeforeBegin = null;
        s.Provider.BeforeObserve = null;
        Assert.True(s.Kernel.TerminateProcess(other).IsSuccess);
        Assert.Equal(0, s.Provider.ActiveMappingCount);
        Assert.Equal(1, s.Provider.BeginCalls);
        Assert.Equal(observe ? 2 : 1, s.Provider.ObserveCalls);
        Assert.False(s.Lease.IsValid);
        Assert.Equal(RegionState.Released, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void ProcessBorrowCallbackFaultOrResetKeepsActualReclaimPinned(bool observe, bool reset)
    {
        Scenario s = CreateScenario();
        _ = CreateGrant(s);
        if (observe)
        {
            s.Provider.CompletionState = PlatformCompletionState.Draining;
            Assert.False(s.Kernel.TerminateProcess(s.Borrower).IsSuccess);
            s.Provider.CompletionState = PlatformCompletionState.Closed;
        }
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        object gate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(s.Kernel)!;
        Action callback = () =>
        {
            Assert.False(Monitor.IsEntered(gate));
            Assert.True(s.Kernel.QueryProcessTeardown(s.Borrower).IsSuccess);
            if (reset) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            else throw new IOException("Provider progression lost response");
        };
        if (observe) s.Provider.BeforeObserve = callback; else s.Provider.BeforeBegin = callback;
        Assert.False(s.Kernel.TerminateProcess(s.Borrower).IsSuccess);
        s.Provider.BeforeBegin = null;
        s.Provider.BeforeObserve = null;
        Assert.False(s.Kernel.TerminateProcess(s.Borrower).IsSuccess);
        Assert.False(s.Kernel.ReturnBorrow(s.Borrower, s.Lease.Handle).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Borrower).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Loaned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(observe && reset ? 0 : 1, s.Provider.ActiveMappingCount);
    }

    [Fact]
    public void BorrowClosureCannotReclaimProcessPastChangedCxlParticipantInventory()
    {
        Scenario s = CreateScenario();
        _ = CreateGrant(s);
        InventoryOnlyCxlParticipant participant = new InventoryOnlyCxlParticipant();
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        s.Provider.AfterObserve = () => typeof(RuntimeKernel).GetMethod("RegisterCxlTeardownParticipant", flags)!
            .Invoke(s.Kernel, new object[] { participant });
        Assert.False(s.Kernel.TerminateProcess(s.Borrower).IsSuccess);
        Assert.True(s.Kernel.Processes.Resolve(s.Borrower).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Borrower).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(0, participant.Calls);
        s.Provider.AfterObserve = null;
        Assert.True(s.Kernel.TerminateProcess(s.Borrower).IsSuccess);
        Assert.Equal(1, participant.Calls);
        Assert.Equal(1, s.Provider.BeginCalls);
        Assert.Equal(1, s.Provider.ObserveCalls);
    }

    private sealed class InventoryOnlyCxlParticipant : ICxlTeardownParticipant
    {
        public int Calls { get; private set; }
        public KernelResult CloseForProcess(ProcessHandle process, RegionOwner owner)
        {
            Calls++;
            return KernelResult.Ok();
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void BorrowCreateRefusesLatePublicationAndRetainsActualCleanupConsumer(int fault)
    {
        Scenario s = CreateScenario();
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        object kernelGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(s.Kernel)!;
        object bridgeGate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        bool outside = false;
        bool generationOutside = false;
        s.Provider.AfterMap = () =>
        {
            outside = !Monitor.IsEntered(kernelGate) && !Monitor.IsEntered(bridgeGate);
            s.Provider.BeforeGeneration = () =>
            {
                generationOutside = !Monitor.IsEntered(kernelGate) && !Monitor.IsEntered(bridgeGate);
                s.Provider.BeforeGeneration = null;
            };
            if (fault == 0 || fault == 1) Assert.False(s.Kernel.TerminateProcess(fault == 0 ? s.Owner : s.Borrower).IsSuccess);
            else if (fault == 2) s.Provider.ThrowGeneration = true;
            else if (fault == 3) s.Provider.Incarnation = new PlatformProviderIncarnation(2);
            else if (fault == 4) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            else if (fault == 5) throw new IOException("Mapping reply lost after effect");
        };
        s.Provider.MalformedMap = fault == 6;
        KernelResult<PlatformBorrowReadGrant> result = s.Kernel.CreatePlatformBorrowReadGrant(s.Owner, s.Borrower, s.Binding, s.Lease.Handle, 0, 64);
        Assert.True(outside);
        if (fault != 5) Assert.True(generationOutside);
        Assert.False(result.IsSuccess);
        Assert.Equal(1, s.Provider.MapCalls);
        Assert.Equal(1, s.Provider.ActiveMappingCount);
        Assert.Equal(RegionState.Loaned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        s.Provider.AfterMap = null;
        s.Provider.ThrowGeneration = false;
        if (fault <= 2)
        {
            Assert.True(s.Kernel.TerminateProcess(fault == 0 ? s.Owner : s.Borrower).IsSuccess);
            Assert.Equal(0, s.Provider.ActiveMappingCount);
            Assert.Equal(1, s.Provider.BeginCalls);
            Assert.Equal(1, s.Provider.ObserveCalls);
            Assert.False(s.Lease.IsValid);
            RegionDescriptor remaining = Assert.Single(s.Kernel.Regions.Snapshot());
            Assert.Equal(fault == 0 ? RegionState.Released : RegionState.Owned, remaining.State);
        }
        else
        {
            Assert.False(s.Kernel.ReturnBorrow(s.Borrower, s.Lease.Handle).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Borrower).IsSuccess);
            Assert.Equal(RegionState.Loaned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
            Assert.Equal(0, s.Provider.BeginCalls);
            Assert.Equal(0, s.Provider.ObserveCalls);
        }
    }

    [Fact]
    public async Task PausedBorrowMappingCannotPublishAfterConcurrentBorrowerExitAndCanDrainLater()
    {
        Scenario s = CreateScenario();
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        s.Provider.AfterMap = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        Task<KernelResult<PlatformBorrowReadGrant>> create = Task.Run(() => s.Kernel.CreatePlatformBorrowReadGrant(s.Owner, s.Borrower, s.Binding, s.Lease.Handle, 0, 64));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.False(s.Kernel.TerminateProcess(s.Borrower).IsSuccess);
            Assert.Equal(RegionState.Loaned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
            Assert.Equal(0, s.Provider.BeginCalls);
        }
        finally { release.Set(); }
        Assert.False((await create).IsSuccess);
        s.Provider.AfterMap = null;
        KernelResult<ProcessTeardownSnapshot> drained = s.Kernel.ObserveProcessTeardown(s.Borrower);
        Assert.True(drained.IsSuccess);
        Assert.True(drained.Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.Equal(1, s.Provider.MapCalls);
        Assert.Equal(1, s.Provider.BeginCalls);
        Assert.Equal(1, s.Provider.ObserveCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ExhaustedLocalBorrowMutationRetainsClosedGrantReservationAndTracking(int consumer)
    {
        Scenario s = CreateScenario();
        PlatformBorrowReadGrant grant = CreateGrant(s);
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        IDictionary records = (global::System.Collections.IDictionary)typeof(RegionAuthority).GetField("_regions", flags)!.GetValue(s.Kernel.Regions)!;
        object record = records.Values.Cast<object>().Single();
        record.GetType().GetProperty("MutationEpoch")!.SetValue(record, new MutationEpoch(ulong.MaxValue));
        KernelResult result = consumer == 0
            ? s.Kernel.RequestPlatformBorrowCompletion(s.Owner, s.Borrower, s.Lease.Handle, grant)
            : s.Kernel.TerminateProcess(consumer == 1 ? s.Owner : s.Borrower);
        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.CapacityExhausted, result.Error);
        KernelResult<PlatformBorrowReadGrantLifecycle> lifecycle = s.Kernel.QueryPlatformBorrowReadGrantLifecycle(s.Owner, grant);
        Assert.True(lifecycle.IsSuccess);
        Assert.Equal(PlatformExternalClosureState.Closed, lifecycle.Value.PlatformClosure);
        Assert.False(lifecycle.Value.LocalReservationReleased);
        Assert.Equal(RegionState.Loaned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        SingProcess borrower = s.Kernel.Processes.Resolve(s.Borrower).Value!;
        SingProcess owner = s.Kernel.Processes.Resolve(s.Owner).Value!;
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.ReturnLoan(s.Lease.Handle,
            new RegionOwner(borrower.DomainId, s.Borrower.Generation)).Error);
        Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.Regions.RevokeLoan(s.Lease.Handle,
            new RegionOwner(owner.DomainId, s.Owner.Generation)).Error);
        IDictionary tracking = (global::System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_platformBorrowReadGrants", flags)!.GetValue(s.Kernel)!;
        Assert.Equal(1, tracking.Count);
        Assert.Equal(1, s.Provider.BeginCalls);
        Assert.Equal(1, s.Provider.ObserveCalls);
        Assert.Equal(0, s.Provider.ActiveMappingCount);
    }

    [Fact]
    public async Task ClosedBorrowCommitKeepsReservationUntilOwnerCompositionAndOrdinaryReturnSettlesOnce()
    {
        Scenario s = CreateScenario();
        PlatformBorrowReadGrant grant = CreateGrant(s);
        Assert.True(Progress(s, grant, true).IsSuccess);
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        object gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(s.Kernel.PlatformAuthority)!;
        using ManualResetEventSlim held = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        Task holder = Task.Factory.StartNew(() =>
        {
            lock (gate) { held.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
        Task<KernelResult> completion;
        Task<KernelResult> ordinary;
        try
        {
            completion = Task.Run(() => s.Kernel.RequestPlatformBorrowCompletion(s.Owner, s.Borrower, s.Lease.Handle, grant));
            ordinary = Task.Run(() => s.Kernel.ReturnBorrow(s.Borrower, s.Lease.Handle));
            await Task.Delay(200);
            Assert.False(completion.IsCompleted);
        }
        finally { release.Set(); }
        await holder;
        Assert.True((await completion).IsSuccess);
        Assert.False((await ordinary).IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.False(s.Lease.IsValid);
        Assert.Equal(1, s.Provider.BeginCalls);
        Assert.Equal(1, s.Provider.ObserveCalls);
        BorrowLease<byte> nextLease = CreateCpuBorrow(s.Kernel, s.Owner, s.Borrower, s.Buffer);
        Assert.NotEqual(s.Lease.Handle.Generation, nextLease.Handle.Generation);
        KernelResult<PlatformBorrowReadGrant> nextGrant = s.Kernel.CreatePlatformBorrowReadGrant(s.Owner, s.Borrower, s.Binding, nextLease.Handle, 0, 64);
        Assert.True(nextGrant.IsSuccess);
        Assert.False(s.Kernel.RequestPlatformBorrowCompletion(s.Owner, s.Borrower, s.Lease.Handle, grant).IsSuccess);
        Assert.True(nextLease.IsValid);
        Assert.True(s.Kernel.RequestPlatformBorrowCompletion(s.Owner, s.Borrower, nextLease.Handle, nextGrant.Value).IsSuccess);
        Assert.Equal(2, s.Provider.BeginCalls);
        Assert.Equal(2, s.Provider.ObserveCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task ResetHoldingMappingCommitGatePreventsOldAdmissionOrClosedPublication(int stage)
    {
        Scenario s = CreateScenario();
        PlatformBorrowReadGrant grant = CreateGrant(s);
        bool retained = stage == 2;
        if (retained)
        {
            s.Provider.AfterObserve = () => s.Provider.ThrowGeneration = true;
            Assert.Equal(KernelError.PlatformFaulted, Progress(s, grant, true).Error);
            s.Provider.AfterObserve = null;
            s.Provider.ThrowGeneration = false;
        }
        PlatformAuthorityBridge bridge = s.Kernel.PlatformAuthority;
        BindingFlags flags = global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic;
        object gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(bridge)!;
        IDictionary mappings = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_mappings", flags)!.GetValue(bridge)!;
        object record = mappings.Values.Cast<object>().Single();
        PropertyInfo closureProperty = record.GetType().GetProperty("ClosureState")!;
        using ManualResetEventSlim sourceRead = new ManualResetEventSlim();
        using ManualResetEventSlim gateHeld = new ManualResetEventSlim();
        bool outside = false;
        bool prematureClosed = false;
        KernelResult<PlatformBackendResetSnapshot> reset = default;
        Task resetWriter = Task.Factory.StartNew(() =>
        {
            Assert.True(sourceRead.Wait(TimeSpan.FromSeconds(5)));
            lock (gate)
            {
                gateHeld.Set();
                // An older implementation publishes Closed while this owner gate
                // is held and then blocks only in the transition's finally block.
                prematureClosed = SpinWait.SpinUntil(() =>
                    (PlatformExternalClosureState)closureProperty.GetValue(record)! == PlatformExternalClosureState.Closed,
                    TimeSpan.FromMilliseconds(500));
                reset = bridge.ObserveBackendReset();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Action pauseSource = () =>
        {
            s.Provider.BeforeGeneration = null;
            outside = !Monitor.IsEntered(gate);
            sourceRead.Set();
            Assert.True(gateHeld.Wait(TimeSpan.FromSeconds(5)));
        };
        if (stage != 1) s.Provider.BeforeGeneration = pauseSource;
        else s.Provider.AfterObserve = () => s.Provider.BeforeGeneration = pauseSource;
        KernelResult<PlatformBorrowReadGrantLifecycle> progressed = await Task.Run(() => Progress(s, grant, !retained));
        await resetWriter;
        Assert.True(outside);
        Assert.False(prematureClosed);
        Assert.True(reset.IsSuccess);
        Assert.Equal(1, reset.Value.FaultPinnedMappings);
        Assert.Equal(KernelError.PlatformFaulted, progressed.Error);
        Assert.Equal(stage == 0 ? 0 : 1, s.Provider.BeginCalls);
        Assert.Equal(stage == 0 ? 0 : 1, s.Provider.ObserveCalls);
        Assert.Equal(stage == 0 ? 1 : 0, s.Provider.ActiveMappingCount);
        Assert.False(s.Kernel.RequestPlatformBorrowCompletion(s.Owner, s.Borrower, s.Lease.Handle, grant).IsSuccess);
        Assert.True(s.Lease.IsValid);
        Assert.False(s.Kernel.TerminateProcess(s.Borrower).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Borrower).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Loaned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Fact]
    public void FaultedMappingReceiptRemainsPinnedWhenLateGenerationQueryThrows()
    {
        Scenario s = CreateScenario();
        PlatformBorrowReadGrant grant = CreateGrant(s);
        s.Provider.CompletionState = PlatformCompletionState.Faulted;
        s.Provider.AfterObserve = () => s.Provider.ThrowGeneration = true;
        Assert.Equal(KernelError.PlatformFaulted, Progress(s, grant, true).Error);
        s.Provider.AfterObserve = null;
        s.Provider.ThrowGeneration = false;
        s.Provider.CompletionState = PlatformCompletionState.Closed;
        Assert.False(s.Kernel.RequestPlatformBorrowCompletion(s.Owner, s.Borrower, s.Lease.Handle, grant).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Borrower).IsSuccess);
        Assert.Equal(1, s.Provider.BeginCalls);
        Assert.Equal(1, s.Provider.ObserveCalls);
        Assert.Equal(RegionState.Loaned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    [InlineData(false, 3)] [InlineData(true, 3)]
    public void ExactMappingCleanupFactsSurviveLateQueryButUnknownOrChangedSourcePins(bool observe, int fault)
    {
        Scenario s = CreateScenario();
        PlatformBorrowReadGrant grant = CreateGrant(s);
        if (observe)
        {
            s.Provider.CompletionState = PlatformCompletionState.Draining;
            Assert.True(Progress(s, grant, true).IsSuccess);
            s.Provider.CompletionState = PlatformCompletionState.Closed;
        }
        Action late = () =>
        {
            if (fault == 0) s.Provider.ThrowGeneration = true;
            else if (fault == 1) s.Provider.Incarnation = new PlatformProviderIncarnation(2);
            else if (fault == 3) Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            else throw new IOException("Exact provider reply lost after effect");
        };
        if (observe) s.Provider.AfterObserve = late;
        else s.Provider.AfterBegin = late;
        Assert.Equal(KernelError.PlatformFaulted, Progress(s, grant, !observe).Error);
        Assert.Equal(RegionState.Loaned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        Assert.True(s.Lease.IsValid);
        int begins = s.Provider.BeginCalls;
        int observations = s.Provider.ObserveCalls;
        s.Provider.AfterBegin = null;
        s.Provider.AfterObserve = null;
        s.Provider.ThrowGeneration = false;
        if (fault == 0)
        {
            Assert.True(s.Kernel.RequestPlatformBorrowCompletion(s.Owner, s.Borrower, s.Lease.Handle, grant).IsSuccess);
            Assert.Equal(begins, s.Provider.BeginCalls);
            Assert.Equal(observations + (observe ? 0 : 1), s.Provider.ObserveCalls);
            Assert.False(s.Lease.IsValid);
            Assert.Equal(RegionState.Owned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
            Assert.Equal(0, s.Provider.ActiveMappingCount);
            Assert.True(s.Kernel.TerminateProcess(s.Borrower).IsSuccess);
        }
        else
        {
            Assert.False(s.Kernel.RequestPlatformBorrowCompletion(s.Owner, s.Borrower, s.Lease.Handle, grant).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Borrower).IsSuccess);
            Assert.Equal(RegionState.Loaned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
            Assert.Equal(begins, s.Provider.BeginCalls);
            Assert.Equal(observations, s.Provider.ObserveCalls);
        }
    }

    private static KernelResult<PlatformBorrowReadGrantLifecycle> Progress(Scenario s, PlatformBorrowReadGrant grant, bool begin)
    {
        MethodInfo method = typeof(PlatformAuthorityBridge).GetMethod(begin ? "BeginBorrowReadGrantRevocation" : "ObserveBorrowReadGrantRevocation",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!;
        return (KernelResult<PlatformBorrowReadGrantLifecycle>)method.Invoke(s.Kernel.PlatformAuthority,
            new object[] { grant, grant.DomainBinding.Subject })!;
    }
    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)]
    public void MappingTransitionRefusesNestedBeginAndObserveWithoutDuplicateEffect(bool observe, int fault)
    {
        Scenario s = CreateScenario();
        PlatformBorrowReadGrant grant = CreateGrant(s);
        if (observe)
        {
            s.Provider.CompletionState = PlatformCompletionState.Draining;
            Assert.True(Progress(s, grant, true).IsSuccess);
            s.Provider.CompletionState = PlatformCompletionState.Closed;
        }
        object gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(s.Kernel.PlatformAuthority)!;
        bool outside = false;
        KernelResult<PlatformBorrowReadGrantLifecycle> nestedBegin = default;
        KernelResult<PlatformBorrowReadGrantLifecycle> nestedObserve = default;
        Action callback = () =>
        {
            outside = !Monitor.IsEntered(gate);
            nestedBegin = Progress(s, grant, true);
            nestedObserve = Progress(s, grant, false);
            if (fault == 1) throw new IOException("Provider transition reply lost");
        };
        if (observe) s.Provider.BeforeObserve = callback;
        else s.Provider.BeforeBegin = callback;
        s.Provider.MalformedReceipt = fault == 2;
        KernelResult<PlatformBorrowReadGrantLifecycle> result = Progress(s, grant, !observe);
        Assert.True(outside);
        Assert.Equal(KernelError.PlatformBindingDraining, nestedBegin.Error);
        Assert.Equal(KernelError.PlatformBindingDraining, nestedObserve.Error);
        Assert.Equal(1, s.Provider.BeginCalls);
        Assert.Equal(observe ? 2 : fault == 1 ? 0 : 1, s.Provider.ObserveCalls);
        s.Provider.BeforeBegin = null;
        s.Provider.BeforeObserve = null;
        if (fault == 0)
        {
            Assert.True(result.IsSuccess);
            Assert.True(s.Kernel.RequestPlatformBorrowCompletion(s.Owner, s.Borrower, s.Lease.Handle, grant).IsSuccess);
            Assert.Equal(0, s.Provider.ActiveMappingCount);
            Assert.True(s.Kernel.TerminateProcess(s.Borrower).IsSuccess);
        }
        else
        {
            Assert.False(result.IsSuccess);
            if (fault == 1)
            {
                Assert.Equal(KernelError.PlatformFaulted, Progress(s, grant, true).Error);
                Assert.False(s.Kernel.TerminateProcess(s.Borrower).IsSuccess);
                Assert.Equal(RegionState.Loaned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
            }
            else
            {
                Assert.Equal(KernelError.StaleGeneration, result.Error);
                Assert.Equal(RegionState.Loaned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
                s.Provider.MalformedReceipt = false;
                Assert.True(Progress(s, grant, false).IsSuccess);
                Assert.True(s.Kernel.RequestPlatformBorrowCompletion(s.Owner, s.Borrower, s.Lease.Handle, grant).IsSuccess);
                Assert.Equal(1, s.Provider.BeginCalls);
            }
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PausedMappingTransitionAllowsCompetingCallsWithoutDuplicateProviderInvocation(bool observe)
    {
        Scenario s = CreateScenario();
        PlatformBorrowReadGrant grant = CreateGrant(s);
        if (observe)
        {
            s.Provider.CompletionState = PlatformCompletionState.Draining;
            Assert.True(Progress(s, grant, true).IsSuccess);
            s.Provider.CompletionState = PlatformCompletionState.Closed;
        }
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        bool released = false;
        Action callback = () => { entered.Set(); released = release.Wait(TimeSpan.FromSeconds(5)); };
        if (observe) s.Provider.BeforeObserve = callback; else s.Provider.BeforeBegin = callback;
        Task<KernelResult<PlatformBorrowReadGrantLifecycle>> task = Task.Run(() => Progress(s, grant, !observe));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.PlatformBindingDraining, Progress(s, grant, true).Error);
            Assert.Equal(KernelError.PlatformBindingDraining, Progress(s, grant, false).Error);
            Assert.Equal(1, s.Provider.BeginCalls);
            Assert.Equal(observe ? 2 : 0, s.Provider.ObserveCalls);
            Assert.Equal(RegionState.Loaned, Assert.Single(s.Kernel.Regions.Snapshot()).State);
        }
        finally { release.Set(); }
        Assert.True((await task).IsSuccess);
        Assert.True(released);
        Assert.True(s.Kernel.RequestPlatformBorrowCompletion(s.Owner, s.Borrower, s.Lease.Handle, grant).IsSuccess);
        Assert.Equal(1, s.Provider.BeginCalls);
        Assert.Equal(observe ? 2 : 1, s.Provider.ObserveCalls);
    }
    [Fact]
    public void BorrowerTeardownClosesGrantBeforeReturningCpuBorrow()
    {
        Scenario scenario = CreateScenario();
        PlatformBorrowReadGrant grant = CreateGrant(scenario);

        KernelResult terminate = scenario.Kernel.TerminateProcess(scenario.Borrower);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.False(scenario.Lease.IsValid);
        Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        scenario.Buffer.Span[0] = 7;
        Assert.Equal((byte)7, scenario.Buffer.Span[0]);
        Assert.Equal(0, scenario.Provider.ActiveMappingCount);
        Assert.Equal(
            KernelError.PlatformBindingNotFound,
            scenario.Kernel.PreparePlatformBorrowReadGrantForExternalReader(
                scenario.Owner,
                grant).Error);
    }

    [Fact]
    public void OwnerTeardownClosesGrantBeforeRevokingBorrowAndReclaimingRegion()
    {
        Scenario scenario = CreateScenario();
        _ = CreateGrant(scenario);

        KernelResult terminate = scenario.Kernel.TerminateProcess(scenario.Owner);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.False(scenario.Lease.IsValid);
        Assert.False(scenario.Buffer.IsValid);
        Assert.Equal(RegionState.Released, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        Assert.Equal(0, scenario.Provider.ActiveMappingCount);
        Assert.Equal(KernelError.StaleHandle, scenario.Kernel.Processes.Resolve(scenario.Owner).Error);
    }

    [Fact]
    public void BorrowerTeardownRemainsDrainingUntilGrantClosureIsObserved()
    {
        Scenario scenario = CreateScenario();
        _ = CreateGrant(scenario);
        scenario.Provider.CompletionState = PlatformCompletionState.Draining;
        ReadOnlySpan<byte> previouslyIssuedView = scenario.Lease.Span;

        KernelResult terminate = scenario.Kernel.TerminateProcess(scenario.Borrower);

        Assert.Equal(KernelError.PlatformBindingDraining, terminate.Error);
        Assert.False(scenario.Lease.IsValid);
        Assert.Throws<InvalidOperationException>(() => _ = scenario.Lease.Length);
        // Managed spans already handed out cannot be revoked by this token transition.
        Assert.Equal((byte)0, previouslyIssuedView[0]);
        Assert.Equal(RegionState.Loaned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        KernelResult<SingProcess> process = scenario.Kernel.Processes.Resolve(scenario.Borrower);
        Assert.True(process.IsSuccess, process.Message);
        Assert.Equal(ProcessState.Exiting, process.Value!.State);
        KernelResult<ProcessTeardownSnapshot> pending = scenario.Kernel.QueryProcessTeardown(scenario.Borrower);
        Assert.True(pending.IsSuccess, pending.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformDraining, pending.Value!.Phase);
        Assert.Equal(1, pending.Value.PendingPlatformMappings);
        Assert.False(pending.Value.LocalReclaimCompleted);

        scenario.Provider.CompletionState = PlatformCompletionState.Closed;
        KernelResult<ProcessTeardownSnapshot> completed = scenario.Kernel.ObserveProcessTeardown(scenario.Borrower);

        Assert.True(completed.IsSuccess, completed.Message);
        Assert.True(completed.Value!.LocalReclaimCompleted);
        Assert.False(scenario.Lease.IsValid);
        Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        Assert.Equal(0, scenario.Provider.ActiveMappingCount);
    }

    private static PlatformBorrowReadGrant CreateGrant(Scenario scenario)
    {
        KernelResult<PlatformBorrowReadGrant> grant = scenario.Kernel.CreatePlatformBorrowReadGrant(
            scenario.Owner,
            scenario.Borrower,
            scenario.Binding,
            scenario.Lease.Handle,
            0,
            64);
        Assert.True(grant.IsSuccess, grant.Message);
        return grant.Value!;
    }

    private static Scenario CreateScenario()
    {
        TeardownProvider provider = new TeardownProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 821, 921);
        (SingProcess _, ProcessHandle borrower) = TestFixtures.Create(kernel, 822, 922);
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(owner, 256).Value!;
        BorrowLease<byte> lease = CreateCpuBorrow(kernel, owner, borrower, buffer);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        return new Scenario(kernel, provider, owner, borrower, buffer, lease, binding);
    }

    private static BorrowLease<byte> CreateCpuBorrow(
        RuntimeKernel kernel,
        ProcessHandle owner,
        ProcessHandle borrower,
        OwnedBuffer<byte> buffer)
    {
        KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> channel = kernel.CreateChannel(owner, borrower, BorrowProtocol(), 4);
        Assert.True(channel.IsSuccess, channel.Message);
        Assert.True(kernel.Send(owner, borrower, channel.Value.Left, 1).IsSuccess);
        Assert.True(kernel.Receive(borrower, channel.Value.Right).IsSuccess);
        Assert.True(kernel.Send(owner, borrower, channel.Value.Left, 2, buffer).IsSuccess);
        KernelResult<ChannelEnvelope> receive = kernel.Receive(borrower, channel.Value.Right);
        Assert.True(receive.IsSuccess, receive.Message);
        return Assert.IsType<BorrowLease<byte>>(receive.Value!.Payload);
    }

    private static RequestPayloadDescriptorV1 OwnershipRequest(string parameterName) =>
        new(
            RequestPayloadKind.Ownership,
            parameterName,
            "SingPlus.Sip.OwnedBuffer",
            ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer);

    private static ProtocolDefinitionV1 BorrowProtocol() => new(
        "SingPlus.Tests.Platform.IBorrowGrantTeardownProtocol",
        "borrow-grant-teardown-contract-digest",
        "Idle",
        new[] { "Done" },
        new[]
        {
            new ProtocolMessageDescriptorV1(1, "Start"),
            new ProtocolMessageDescriptorV1(
                2,
                "Peek",
                borrows: new[] { "data" },
                requestPayload: OwnershipRequest("data")),
        },
        new[]
        {
            new ProtocolTransitionV1(1, "Idle", "Active"),
            new ProtocolTransitionV1(2, "Active", "Active"),
        });

    private sealed record Scenario(
        RuntimeKernel Kernel,
        TeardownProvider Provider,
        ProcessHandle Owner,
        ProcessHandle Borrower,
        OwnedBuffer<byte> Buffer,
        BorrowLease<byte> Lease,
        PlatformDomainBinding Binding);

    private sealed class TeardownProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformOwnedRegionMappingProvider,
        IPlatformRegionVisibilityProvider,
        IPlatformRegionRevocationProvider,
        IPlatformProviderIncarnationSource
    {
        private sealed class MappingRecord(
            PlatformProviderOwnedRegionMapping mapping)
        {
            public PlatformProviderOwnedRegionMapping Mapping { get; } = mapping;
            public bool Revoked { get; set; }
        }

        private readonly Dictionary<PlatformProviderDomainLeaseId, PlatformProviderDomainLease>
            _domains = [];
        private readonly Dictionary<PlatformProviderRegionMappingId, MappingRecord>
            _mappings = [];
        private PlatformOperationIdentity? _operation;
        private PlatformProviderRegionMappingId? _revokingMapping;
        private ulong _nextDomain = 1;
        private ulong _nextMapping = 1;
        private ulong _nextOperation = 1;

        public PlatformCompletionState CompletionState { get; set; } =
            PlatformCompletionState.Closed;
        public bool ThrowGeneration { get; set; }
        public Action? AfterMap { get; set; }
        public bool MalformedMap { get; set; }
        public int MapCalls { get; private set; }
        public Action? BeforeGeneration { get; set; }
        public PlatformProviderIncarnation Incarnation { get; set; } = new(1);
        public PlatformProviderIncarnation CurrentIncarnation
        {
            get
            {
                BeforeGeneration?.Invoke();
                return ThrowGeneration ? throw new IOException("Generation source unavailable") : Incarnation;
            }
        }
        public int ActiveMappingCount => _mappings.Values.Count(static record => !record.Revoked);

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("borrow-read-grant-teardown-test"),
            4,
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
                PlatformRegionVisibilityContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
        });

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject)
        {
            PlatformProviderDomainLease lease = new PlatformProviderDomainLease(
                new PlatformProviderDomainLeaseId(_nextDomain++),
                new PlatformProviderLeaseGeneration(1),
                subject);
            _domains.Add(lease.LeaseId, lease);
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            if (_mappings.Values.Any(record =>
                    !record.Revoked && record.Mapping.Lease.DomainLease == lease))
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Domain still has an active mapping.");
            }

            _domains.Remove(lease.LeaseId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access)
        {
            PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> exact = MapOwnedRegionSlice(
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
            MapCalls++;
            if (!_domains.TryGetValue(domainLease.LeaseId, out PlatformProviderDomainLease active) || active != domainLease)
            {
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Wrong domain lease.");
            }

            RegionOwner expectedOwner = new RegionOwner(
                domainLease.Subject.DomainId,
                domainLease.Subject.ProcessGeneration);
            if (slice.Region.Owner != expectedOwner || slice.Access != PlatformMemoryAccess.Read)
            {
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Teardown test accepts only owner-bound read mappings.");
            }

            PlatformProviderRegionMappingLease lease = new PlatformProviderRegionMappingLease(
                new PlatformProviderRegionMappingId(_nextMapping++),
                new PlatformProviderLeaseGeneration(1),
                domainLease,
                slice.Region,
                slice.Access);
            PlatformProviderOwnedRegionMapping mapping = new PlatformProviderOwnedRegionMapping(lease, slice);
            _mappings.Add(lease.MappingId, new MappingRecord(mapping));
            AfterMap?.Invoke();
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Ok(MalformedMap
                ? mapping with { Lease = lease with { Access = PlatformMemoryAccess.Read | PlatformMemoryAccess.Write } }
                : mapping);
        }

        public PlatformAuthorityResult<PlatformRegionVisibilityResult>
            PrepareRegionMappingForConsumer(PlatformRegionVisibilityRequest request) =>
            PlatformAuthorityResult<PlatformRegionVisibilityResult>.Ok(
                new PlatformRegionVisibilityResult(
                    request.Mapping.MappingId,
                    request.Mapping.Generation,
                    request.Slice,
                    request.Consumer,
                    request.Requirement,
                    PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied));

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy)
        {
            if (_mappings.TryGetValue(mapping.MappingId, out MappingRecord? record))
                record.Revoked = true;
            return PlatformAuthorityResult.Ok();
        }

        public Action? BeforeBegin { get; set; }
        public Action? BeforeObserve { get; set; }
        public Action? AfterBegin { get; set; }
        public Action? AfterObserve { get; set; }
        public int BeginCalls { get; private set; }
        public int ObserveCalls { get; private set; }
        public bool MalformedReceipt { get; set; }
        public PlatformAuthorityResult<PlatformRegionRevocationTicket>
            BeginRegionMappingRevocation(
                PlatformProviderRegionMappingLease mapping,
                PlatformRegionRevocationPolicy policy)
        {
            BeginCalls++;
            BeforeBegin?.Invoke();
            if (!_mappings.TryGetValue(mapping.MappingId, out MappingRecord? record) ||
                record.Mapping.Lease != mapping)
            {
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Wrong mapping.");
            }

            _revokingMapping = mapping.MappingId;
            _operation = new PlatformOperationIdentity(
                new PlatformOperationId(_nextOperation++),
                new PlatformOperationGeneration(1),
                mapping.DomainLease);
            AfterBegin?.Invoke();
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(
                new PlatformRegionRevocationTicket(
                    mapping.MappingId,
                    mapping.Generation,
                    _operation.Value));
        }

        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
            PlatformOperationIdentity operation)
        {
            ObserveCalls++;
            BeforeObserve?.Invoke();
            if (_operation != operation || _revokingMapping is null)
            {
                return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Wrong operation.");
            }

            if (CompletionState == PlatformCompletionState.Closed &&
                _mappings.TryGetValue(_revokingMapping.Value, out MappingRecord? record))
            {
                record.Revoked = true;
            }

            AfterObserve?.Invoke();
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(
                new PlatformCompletionReceipt(
                    operation.OperationId,
                    MalformedReceipt ? new PlatformOperationGeneration(operation.Generation.Value + 1) : operation.Generation,
                    operation.DomainLease,
                    CompletionState));
        }
    }
}
