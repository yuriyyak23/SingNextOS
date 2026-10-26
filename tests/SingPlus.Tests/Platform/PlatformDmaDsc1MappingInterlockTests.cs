using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformDmaDsc1MappingInterlockTests
{
    private const long DmaOffset = 0;
    private const long OperationLength = 16;
    private const long DisjointDscOffset = 64;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapabilityRevokeDsc1CallbackAllowsWorkerAdmissionAndPreservesClosure(bool throwAfterAdmission)
    {
        Scenario scenario = CreateScenario(2221, 2410);
        KernelResult<PlatformDsc1CopySubmission> submitted = SubmitDsc1(scenario, scenario.First, scenario.Second, DisjointDscOffset);
        Assert.True(submitted.IsSuccess);
        bool workerAdmitted = false;
        scenario.Provider.BeforeDsc1Cancel = () =>
        {
            Assert.Equal(KernelError.PlatformBindingRevoked,
                scenario.Kernel.PlatformAuthority.ValidateMapping(
                    scenario.First.Mapping.Mapping, scenario.Binding.Subject).Error);
            Task<KernelResult<CapabilityDescriptorV1>> worker = Task.Run(() => scenario.Kernel.MintCapability(
                scenario.Binding.Subject.DomainId, scenario.Subject,
                ResourceKind.Compute, "compute/revoke-callback", CapabilityRights.Execute));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(worker.Result.IsSuccess);
            workerAdmitted = true;
            if (throwAfterAdmission)
                throw new InvalidOperationException("Injected cancellation callback failure.");
        };

        KernelResult revoked = scenario.Kernel.RevokeCapability(scenario.First.Capability);

        Assert.True(workerAdmitted);
        Assert.Equal(1, scenario.Provider.Dsc1CancelCalls);
        Assert.False(scenario.Kernel.CapabilityAuthority.Validate(scenario.First.Capability,
            scenario.Binding.Subject.DomainId, scenario.Subject.Generation, CapabilityRights.Map).IsSuccess);
        Assert.Equal(!throwAfterAdmission, revoked.IsSuccess);
        if (throwAfterAdmission)
        {
            Assert.Equal(KernelError.PlatformFaulted, revoked.Error);
            Assert.Equal(KernelError.PlatformFaulted,
                scenario.Kernel.CancelPlatformDsc1Copy(scenario.Subject, submitted.Value!).Error);
            Assert.Equal(1, scenario.Provider.Dsc1CancelCalls);
            Assert.Throws<InvalidOperationException>(() => { _ = scenario.First.Buffer.Span.Length; });
            Assert.Throws<InvalidOperationException>(() => { _ = scenario.Second.Buffer.Span.Length; });
        }
        else
        {
            Assert.True(scenario.First.Buffer.IsValid);
            Assert.True(scenario.Second.Buffer.IsValid);
            Assert.Equal(128, scenario.First.Buffer.Span.Length);
            Assert.Equal(128, scenario.Second.Buffer.Span.Length);
        }
        Assert.True(Task.Run(() => scenario.Kernel.MintCapability(
            scenario.Binding.Subject.DomainId, scenario.Subject,
            ResourceKind.Compute, "compute/after-revoke", CapabilityRights.Execute))
            .Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void CapabilityRevokeDeniesMappingBeforeDsc1CancellationCallback()
    {
        Scenario scenario = CreateScenario(2220, 2400);
        Assert.True(SubmitDsc1(scenario, scenario.First, scenario.Second, DisjointDscOffset).IsSuccess);
        scenario.Provider.BeforeDsc1Cancel = () =>
        {
            Assert.False(scenario.Kernel.CapabilityAuthority.Validate(scenario.First.Capability,
                scenario.Binding.Subject.DomainId, scenario.Subject.Generation, CapabilityRights.Map).IsSuccess);
            Assert.Equal(KernelError.PlatformBindingRevoked,
                scenario.Kernel.PlatformAuthority.ValidateMapping(
                    scenario.First.Mapping.Mapping, scenario.Binding.Subject).Error);
        };
        Assert.True(scenario.Kernel.RevokeCapability(scenario.First.Capability).IsSuccess);
        Assert.Equal(1, scenario.Provider.Dsc1CancelCalls);
    }
    [Fact]
    public void ActiveDmaRejectsEitherDsc1RoleBeforeProviderAndPreservesIdentityErrors()
    {
        Scenario scenario = CreateScenario(2201, 2210);
        PlatformDmaGrant dmaGrant = CreateDmaGrant(
            scenario,
            scenario.First,
            PlatformDmaDirection.DeviceReadsMemory);
        PlatformDmaSubmission dma = PrepareAndSubmitDma(scenario, dmaGrant);

        PlatformRegionMapping staleMapping = scenario.First.Mapping.Mapping with
        {
            Generation = new PlatformRegionMappingGeneration(
                scenario.First.Mapping.Mapping.Generation.Value + 1),
        };
        KernelResult<PlatformDsc1CopySubmission> stale = SubmitDsc1(
            scenario,
            scenario.First.Buffer,
            staleMapping,
            scenario.Second,
            DisjointDscOffset);
        Assert.False(stale.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);

        PlatformRegionMapping forgedMapping = scenario.First.Mapping.Mapping with
        {
            Access = PlatformMemoryAccess.Read,
        };
        KernelResult<PlatformDsc1CopySubmission> forged = SubmitDsc1(
            scenario,
            scenario.First.Buffer,
            forgedMapping,
            scenario.Second,
            DisjointDscOffset);
        Assert.False(forged.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, forged.Error);

        KernelResult<PlatformDsc1CopySubmission> sameAsSource = SubmitDsc1(
            scenario,
            scenario.First,
            scenario.Second,
            DisjointDscOffset);
        Assert.False(sameAsSource.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, sameAsSource.Error);

        KernelResult<PlatformDsc1CopySubmission> sameAsDestination = SubmitDsc1(
            scenario,
            scenario.Second,
            scenario.First,
            DisjointDscOffset);
        Assert.False(sameAsDestination.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, sameAsDestination.Error);

        Assert.Equal(0, scenario.Provider.Dsc1SubmitCalls);
        scenario.First.Buffer.Span[0] = 0x31;
        scenario.Second.Buffer.Span[0] = 0x42;

        CompleteAndFinalizeDma(scenario, dma);
    }

    [Fact]
    public void PreparedDmaDoesNotPinButDsc1CancellationDrainRetainsThenReleasesPin()
    {
        Scenario scenario = CreateScenario(2202, 2220);
        PlatformDmaGrant dmaGrant = CreateDmaGrant(
            scenario,
            scenario.First,
            PlatformDmaDirection.DeviceWritesMemory);
        KernelResult<PlatformDmaPrepareEvidence> prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            dmaGrant);
        Assert.True(prepared.IsSuccess, prepared.Message);

        scenario.Provider.Dsc1CancelPendingOnce = true;
        KernelResult<PlatformDsc1CopySubmission> dsc1 = SubmitDsc1(
            scenario,
            scenario.First,
            scenario.Second,
            DisjointDscOffset);
        Assert.True(dsc1.IsSuccess, dsc1.Message);

        KernelResult<PlatformDmaSubmission> blocked = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            dmaGrant,
            prepared.Value!);
        Assert.False(blocked.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, blocked.Error);
        Assert.Equal(0, scenario.Provider.DmaSubmitCalls);

        KernelResult<PlatformDsc1CopyReceipt> stale = scenario.Kernel.CancelPlatformDsc1Copy(
            scenario.Subject,
            dsc1.Value! with
            {
                Generation = new PlatformDsc1SubmissionGeneration(
                    dsc1.Value!.Generation.Value + 1),
            });
        Assert.False(stale.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);

        KernelResult<PlatformDsc1CopyReceipt> forged = scenario.Kernel.CancelPlatformDsc1Copy(
            scenario.Subject,
            dsc1.Value! with
            {
                Destination = dsc1.Value!.Destination with
                {
                    Length = dsc1.Value.Destination.Length - 1,
                },
            });
        Assert.False(forged.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, forged.Error);
        Assert.Equal(0, scenario.Provider.Dsc1CancelCalls);

        KernelResult<PlatformDsc1CopyReceipt> draining = scenario.Kernel.CancelPlatformDsc1Copy(
            scenario.Subject,
            dsc1.Value!);
        Assert.False(draining.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, draining.Error);

        KernelResult<PlatformDmaSubmission> stillBlocked = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            dmaGrant,
            prepared.Value!);
        Assert.False(stillBlocked.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, stillBlocked.Error);
        Assert.Equal(0, scenario.Provider.DmaSubmitCalls);

        KernelResult<PlatformDsc1CopyReceipt> cancelled = scenario.Kernel.CancelPlatformDsc1Copy(
            scenario.Subject,
            dsc1.Value!);
        Assert.True(cancelled.IsSuccess, cancelled.Message);
        Assert.Equal(PlatformDsc1CopyOutcome.Cancelled, cancelled.Value!.Outcome);
        Assert.False(cancelled.Value.OutputPublished);

        KernelResult<PlatformDmaSubmission> submitted = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            dmaGrant,
            prepared.Value!);
        Assert.True(submitted.IsSuccess, submitted.Message);

        PlatformDmaGrant destinationGrant = CreateDmaGrant(
            scenario,
            scenario.Second,
            PlatformDmaDirection.DeviceReadsMemory);
        PlatformDmaSubmission destinationSubmitted = PrepareAndSubmitDma(
            scenario,
            destinationGrant);
        Assert.NotEqual(
            submitted.Value!.OperationId,
            destinationSubmitted.OperationId);
        Assert.Equal(2, scenario.Provider.DmaSubmitCalls);
    }

    [Fact]
    public void Dsc1CompletionPublishesOutputAndReleasesBothMappingUses()
    {
        Scenario scenario = CreateScenario(2214, 2340);
        scenario.First.Buffer.Span.Fill(0x6D);
        scenario.Second.Buffer.Span.Fill(0xA4);
        PlatformDmaGrant dmaGrant = CreateDmaGrant(
            scenario,
            scenario.First,
            PlatformDmaDirection.DeviceWritesMemory);
        KernelResult<PlatformDmaPrepareEvidence> prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            dmaGrant);
        Assert.True(prepared.IsSuccess, prepared.Message);
        KernelResult<PlatformDsc1CopySubmission> dsc1 = SubmitDsc1(
            scenario,
            scenario.First,
            scenario.Second,
            scenario.First.Mapping.Offset);
        Assert.True(dsc1.IsSuccess, dsc1.Message);

        Assert.True(scenario.Provider.CompleteLastDsc1().IsSuccess);
        KernelResult<PlatformDsc1CopyReceipt> completed = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            dsc1.Value!);

        Assert.True(completed.IsSuccess, completed.Message);
        Assert.Equal(PlatformDsc1CopyOutcome.Completed, completed.Value!.Outcome);
        Assert.True(completed.Value.OutputPublished);
        Assert.Equal(
            Enumerable.Repeat((byte)0x6D, (int)OperationLength).ToArray(),
            scenario.Second.Buffer.Span
                .Slice((int)scenario.Second.Mapping.Offset, (int)OperationLength)
                .ToArray());
        KernelResult<PlatformDmaSubmission> sourceDma = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            dmaGrant,
            prepared.Value!);
        Assert.True(sourceDma.IsSuccess, sourceDma.Message);

        // Prepare destination evidence only after DSC1 publication. This proves
        // that both exact reservations were released without treating earlier
        // prepare evidence as a CPU-mutation epoch guarantee.
        PlatformDmaGrant destinationGrant = CreateDmaGrant(
            scenario,
            scenario.Second,
            PlatformDmaDirection.DeviceReadsMemory);
        PlatformDmaSubmission destinationDma = PrepareAndSubmitDma(scenario, destinationGrant);
        Assert.NotEqual(sourceDma.Value!.OperationId, destinationDma.OperationId);
        Assert.Equal(2, scenario.Provider.DmaSubmitCalls);
    }

    [Fact]
    public void IndependentlyAuthorizedDistinctMappingsPermitConcurrentDmaAndDsc1()
    {
        Scenario scenario = CreateScenario(2203, 2230);
        scenario.Third.Buffer.Span.Fill(0x5A);
        scenario.Fourth.Buffer.Span.Fill(0xC3);
        PlatformDmaGrant dmaGrant = CreateDmaGrant(
            scenario,
            scenario.First,
            PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission dma = PrepareAndSubmitDma(scenario, dmaGrant);

        KernelResult<PlatformDsc1CopySubmission> dsc1 = SubmitDsc1(
            scenario,
            scenario.Third,
            scenario.Fourth,
            scenario.Third.Mapping.Offset);

        Assert.True(dsc1.IsSuccess, dsc1.Message);
        Assert.Equal(1, scenario.Provider.DmaSubmitCalls);
        Assert.Equal(1, scenario.Provider.Dsc1SubmitCalls);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Third.Buffer.Span[0];
        });
        scenario.First.Buffer.Span[0] = 0x19;

        Assert.True(scenario.Provider.CompleteLastDsc1().IsSuccess);
        KernelResult<PlatformDsc1CopyReceipt> completedDsc1 = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            dsc1.Value!);
        Assert.True(completedDsc1.IsSuccess, completedDsc1.Message);
        Assert.Equal(PlatformDsc1CopyOutcome.Completed, completedDsc1.Value!.Outcome);
        Assert.True(completedDsc1.Value.OutputPublished);
        Assert.Equal(
            Enumerable.Repeat((byte)0x5A, (int)OperationLength).ToArray(),
            scenario.Fourth.Buffer.Span
                .Slice((int)scenario.Fourth.Mapping.Offset, (int)OperationLength)
                .ToArray());

        CompleteAndFinalizeDma(scenario, dma);
    }

    [Fact]
    public void DmaDeniedPendingAndCompletedObservationRetainPinUntilPostVisibility()
    {
        Scenario scenario = CreateScenario(2204, 2240);
        PlatformDmaGrant dmaGrant = CreateDmaGrant(
            scenario,
            scenario.First,
            PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission dma = PrepareAndSubmitDma(scenario, dmaGrant);

        KernelResult<PlatformDmaCompletionEvidence> stale = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            dma with
            {
                Generation = new PlatformDmaOperationGeneration(
                    dma.Generation.Value + 1),
            });
        Assert.False(stale.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);

        KernelResult<PlatformDmaCompletionEvidence> forged = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            dma with
            {
                Range = new PlatformDmaRange(dma.Range.Offset, dma.Range.Length + 1),
            });
        Assert.False(forged.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, forged.Error);
        Assert.Equal(0, scenario.Provider.DmaCompletionCalls);

        scenario.Provider.DmaCompletionFailure = PlatformAuthorityStatus.Denied;
        KernelResult<PlatformDmaCompletionEvidence> denied = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            dma);
        Assert.False(denied.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, denied.Error);
        AssertDsc1Conflict(scenario);

        scenario.Provider.DmaCompletionFailure = null;
        scenario.Provider.DmaCompletionState = PlatformProviderDmaCompletionState.Pending;
        KernelResult<PlatformDmaCompletionEvidence> pending = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            dma);
        Assert.False(pending.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, pending.Error);
        AssertDsc1Conflict(scenario);

        scenario.Provider.DmaCompletionState = PlatformProviderDmaCompletionState.Completed;
        KernelResult<PlatformDmaCompletionEvidence> completed = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            dma);
        Assert.True(completed.IsSuccess, completed.Message);
        AssertDsc1Conflict(scenario, KernelError.PlatformBindingDraining);

        KernelResult<PlatformDmaPostCompletionVisibilityEvidence> visibility = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject,
            dma,
            completed.Value!);
        Assert.True(visibility.IsSuccess, visibility.Message);

        KernelResult<PlatformDsc1CopySubmission> accepted = SubmitDsc1(
            scenario,
            scenario.First,
            scenario.Second,
            DisjointDscOffset);
        Assert.True(accepted.IsSuccess, accepted.Message);
        Assert.True(scenario.Kernel.CancelPlatformDsc1Copy(
            scenario.Subject,
            accepted.Value!).IsSuccess);
    }

    [Theory]
    [InlineData(DmaSubmitFaultMode.ReturnedFaulted)]
    [InlineData(DmaSubmitFaultMode.MalformedSuccess)]
    [InlineData(DmaSubmitFaultMode.Throw)]
    public void AmbiguousDmaSubmitFaultPinsOnlyItsExactMapping(
        DmaSubmitFaultMode mode)
    {
        Scenario scenario = CreateScenario(2205 + (ulong)mode, 2250 + (ulong)mode);
        PlatformDmaGrant dmaGrant = CreateDmaGrant(
            scenario,
            scenario.First,
            PlatformDmaDirection.DeviceWritesMemory);
        KernelResult<PlatformDmaPrepareEvidence> prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            dmaGrant);
        Assert.True(prepared.IsSuccess, prepared.Message);

        scenario.Provider.DmaSubmitBehavior = mode;
        KernelResult<PlatformDmaSubmission> failed = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            dmaGrant,
            prepared.Value!);
        Assert.False(failed.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, failed.Error);

        AssertDsc1Conflict(scenario, KernelError.PlatformFaulted);
        Assert.Equal(
            KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, dmaGrant).Error);
        Assert.Equal(
            KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformRegionMapping(
                scenario.Subject,
                scenario.First.Mapping).Error);
        KernelResult<OwnedBuffer<byte>> moved = scenario.Kernel.TransferRegion(
            scenario.Subject,
            scenario.Target,
            scenario.First.Buffer);
        Assert.False(moved.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, moved.Error);

        scenario.Provider.DmaSubmitBehavior = DmaSubmitFaultMode.None;
        KernelResult<PlatformDsc1CopySubmission> distinct = SubmitDsc1(
            scenario,
            scenario.Third,
            scenario.Fourth,
            scenario.Third.Mapping.Offset);
        Assert.True(distinct.IsSuccess, distinct.Message);
        Assert.True(scenario.Kernel.CancelPlatformDsc1Copy(
            scenario.Subject,
            distinct.Value!).IsSuccess);
    }

    [Fact]
    public void Dsc1FaultQuarantinesDomainAndRetainsLocalReservations()
    {
        Scenario scenario = CreateScenario(2209, 2290);
        scenario.Second.Buffer.Span.Fill(0x9C);
        Span<byte> destinationAlias = scenario.Second.Buffer.Span;
        byte[] destinationBefore = destinationAlias.ToArray();
        KernelResult<PlatformDsc1CopySubmission> dsc1 = SubmitDsc1(
            scenario,
            scenario.First,
            scenario.Second,
            scenario.First.Mapping.Offset);
        Assert.True(dsc1.IsSuccess, dsc1.Message);

        PlatformDmaGrant sourceGrant = CreateDmaGrant(
            scenario,
            scenario.First,
            PlatformDmaDirection.DeviceReadsMemory);
        PlatformDmaGrant destinationGrant = CreateDmaGrant(
            scenario,
            scenario.Second,
            PlatformDmaDirection.DeviceWritesMemory);
        KernelResult<PlatformDmaPrepareEvidence> sourcePrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            sourceGrant);
        KernelResult<PlatformDmaPrepareEvidence> destinationPrepare = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            destinationGrant);
        Assert.True(sourcePrepare.IsSuccess, sourcePrepare.Message);
        Assert.True(destinationPrepare.IsSuccess, destinationPrepare.Message);

        scenario.Provider.Dsc1CompletionFaulted = true;
        KernelResult<PlatformDsc1CopyReceipt> faulted = scenario.Kernel.ObservePlatformDsc1Copy(
            scenario.Subject,
            dsc1.Value!);
        Assert.False(faulted.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, faulted.Error);

        KernelResult<PlatformDmaSubmission> sourceDma = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            sourceGrant,
            sourcePrepare.Value!);
        KernelResult<PlatformDmaSubmission> destinationDma = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            destinationGrant,
            destinationPrepare.Value!);
        Assert.False(sourceDma.IsSuccess);
        Assert.False(destinationDma.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, sourceDma.Error);
        Assert.Equal(KernelError.PlatformFaulted, destinationDma.Error);
        Assert.Equal(0, scenario.Provider.DmaSubmitCalls);
        Assert.Equal(destinationBefore, destinationAlias.ToArray());
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.First.Buffer.Span[0];
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Second.Buffer.Span[0];
        });
        Assert.Equal(
            KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformRegionMapping(
                scenario.Subject,
                scenario.First.Mapping).Error);
        Assert.Equal(
            KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformRegionMapping(
                scenario.Subject,
                scenario.Third.Mapping).Error);
    }

    [Fact]
    public void ProcessTeardownCancelsDsc1ButWaitsForDmaBeforeMappingAndRegionReclaim()
    {
        Scenario scenario = CreateScenario(2213, 2330);
        PlatformDmaGrant dmaGrant = CreateDmaGrant(
            scenario,
            scenario.First,
            PlatformDmaDirection.DeviceWritesMemory);
        PlatformDmaSubmission dma = PrepareAndSubmitDma(scenario, dmaGrant);
        KernelResult<PlatformDsc1CopySubmission> dsc1 = SubmitDsc1(
            scenario,
            scenario.Third,
            scenario.Fourth,
            scenario.Third.Mapping.Offset);
        Assert.True(dsc1.IsSuccess, dsc1.Message);

        KernelResult terminated = scenario.Kernel.TerminateProcess(scenario.Subject);

        Assert.False(terminated.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, terminated.Error);
        Assert.Equal(1, scenario.Provider.Dsc1CancelCalls);
        KernelResult<ProcessTeardownSnapshot> draining = scenario.Kernel.QueryProcessTeardown(scenario.Subject);
        Assert.True(draining.IsSuccess, draining.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformDraining, draining.Value!.Phase);
        Assert.False(draining.Value.LocalReclaimCompleted);
        Assert.False(draining.Value.PlatformDomainClosed);
        Assert.All(
            new[]
            {
                scenario.First.Buffer.Handle.RegionId,
                scenario.Third.Buffer.Handle.RegionId,
                scenario.Fourth.Buffer.Handle.RegionId,
            },
            regionId => Assert.Equal(
                RegionState.Owned,
                scenario.Kernel.Regions.Snapshot()
                    .Single(region => region.Handle.RegionId == regionId).State));

        CompleteAndFinalizeDma(scenario, dma);
        KernelResult<ProcessTeardownSnapshot> completed = scenario.Kernel.ObserveProcessTeardown(scenario.Subject);

        Assert.True(completed.IsSuccess, completed.Message);
        Assert.True(completed.Value!.LocalReclaimCompleted);
        Assert.True(completed.Value.PlatformDomainClosed);
        Assert.Equal(ProcessTeardownPhase.PlatformClosed, completed.Value.Phase);
        Assert.Equal(1, scenario.Provider.Dsc1CancelCalls);
        Assert.All(
            new[]
            {
                scenario.First.Buffer.Handle.RegionId,
                scenario.Second.Buffer.Handle.RegionId,
                scenario.Third.Buffer.Handle.RegionId,
                scenario.Fourth.Buffer.Handle.RegionId,
            },
            regionId => Assert.Equal(
                RegionState.Released,
                scenario.Kernel.Regions.Snapshot()
                    .Single(region => region.Handle.RegionId == regionId).State));
    }

    [Theory]
    [InlineData(FirstSubmitMechanism.Dma)]
    [InlineData(FirstSubmitMechanism.Dsc1)]
    public async Task InFlightConflictingSubmitLinearizesAndDenialPreservesApplicableLifetime(
        FirstSubmitMechanism first)
    {
        Scenario scenario = CreateScenario(2210 + (ulong)first, 2300 + (ulong)first);
        PlatformDmaGrant dmaGrant = CreateDmaGrant(
            scenario,
            scenario.First,
            PlatformDmaDirection.DeviceWritesMemory);
        KernelResult<PlatformDmaPrepareEvidence> prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            dmaGrant);
        Assert.True(prepared.IsSuccess, prepared.Message);
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        using ManualResetEventSlim contenderStarted = new ManualResetEventSlim();
        using ManualResetEventSlim contenderProviderEntered = new ManualResetEventSlim();

        if (first == FirstSubmitMechanism.Dma)
        {
            scenario.Provider.DmaSubmitEntered = entered;
            scenario.Provider.DmaSubmitRelease = release;
            scenario.Provider.DmaSubmitBehavior = DmaSubmitFaultMode.Denied;
            scenario.Provider.Dsc1SubmitEntered = contenderProviderEntered;
            Task<KernelResult<PlatformDmaSubmission>> firstTask = Task.Run(() => scenario.Kernel.SubmitPlatformDma(
                scenario.Subject,
                dmaGrant,
                prepared.Value!));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Task<KernelResult<PlatformDsc1CopySubmission>> waitingDsc1 = Task.Run(() =>
            {
                contenderStarted.Set();
                return SubmitDsc1(
                    scenario,
                    scenario.First,
                    scenario.Second,
                    DisjointDscOffset);
            });
            try
            {
                Assert.True(contenderStarted.Wait(TimeSpan.FromSeconds(5)));
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                KernelResult<PlatformDsc1CopySubmission> pending = await waitingDsc1.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(KernelError.PlatformBindingDraining, pending.Error);
                Assert.False(contenderProviderEntered.IsSet);
            }
            finally
            {
                release.Set();
            }

            KernelResult<PlatformDmaSubmission> denied = await firstTask;
            Assert.False(denied.IsSuccess);
            Assert.Equal(KernelError.PlatformDenied, denied.Error);
            Assert.Equal(KernelError.PlatformBindingDraining, (await waitingDsc1).Error);
            KernelResult<PlatformDsc1CopySubmission> accepted = SubmitDsc1(scenario, scenario.First, scenario.Second, DisjointDscOffset);
            Assert.True(accepted.IsSuccess, accepted.Message);
            Assert.True(contenderProviderEntered.IsSet);
            Assert.True(scenario.Kernel.CancelPlatformDsc1Copy(
                scenario.Subject,
                accepted.Value!).IsSuccess);
        }
        else
        {
            scenario.Provider.Dsc1SubmitEntered = entered;
            scenario.Provider.Dsc1SubmitRelease = release;
            scenario.Provider.Dsc1SubmitDenied = true;
            scenario.Provider.DmaSubmitEntered = contenderProviderEntered;
            Task<KernelResult<PlatformDsc1CopySubmission>> firstTask = Task.Run(() => SubmitDsc1(
                scenario,
                scenario.First,
                scenario.Second,
                DisjointDscOffset));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Task<KernelResult<PlatformDmaSubmission>> waitingDma = Task.Run(() =>
            {
                contenderStarted.Set();
                return scenario.Kernel.SubmitPlatformDma(
                    scenario.Subject,
                    dmaGrant,
                    prepared.Value!);
            });
            try
            {
                Assert.True(contenderStarted.Wait(TimeSpan.FromSeconds(5)));
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                Assert.True(waitingDma.IsCompleted);
                Assert.False(contenderProviderEntered.IsSet);
            }
            finally
            {
                release.Set();
            }

            KernelResult<PlatformDsc1CopySubmission> denied = await firstTask;
            Assert.False(denied.IsSuccess);
            Assert.Equal(KernelError.PlatformDenied, denied.Error);
            KernelResult<PlatformDmaSubmission> rejected = await waitingDma;
            Assert.False(rejected.IsSuccess);
            Assert.Equal(KernelError.PlatformBindingActive, rejected.Error);
            Assert.False(contenderProviderEntered.IsSet);
            KernelResult<PlatformDmaSubmission> stillRejected = scenario.Kernel.SubmitPlatformDma(
                scenario.Subject,
                dmaGrant,
                prepared.Value!);
            Assert.False(stillRejected.IsSuccess);
            Assert.Equal(KernelError.PlatformFaulted, stillRejected.Error);
            Assert.False(contenderProviderEntered.IsSet);
            Assert.False(scenario.Kernel.TerminateProcess(scenario.Subject).IsSuccess);
        }
    }

    [Theory]
    [InlineData(FirstSubmitMechanism.Dma)]
    [InlineData(FirstSubmitMechanism.Dsc1)]
    public async Task InFlightAcceptedSubmitWinsBeforeConflictingProviderEntry(
        FirstSubmitMechanism first)
    {
        Scenario scenario = CreateScenario(2215 + (ulong)first, 2350 + (ulong)first);
        PlatformDmaGrant dmaGrant = CreateDmaGrant(
            scenario,
            scenario.First,
            PlatformDmaDirection.DeviceWritesMemory);
        KernelResult<PlatformDmaPrepareEvidence> prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            dmaGrant);
        Assert.True(prepared.IsSuccess, prepared.Message);
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        using ManualResetEventSlim contenderStarted = new ManualResetEventSlim();
        using ManualResetEventSlim contenderProviderEntered = new ManualResetEventSlim();

        if (first == FirstSubmitMechanism.Dma)
        {
            scenario.Provider.DmaSubmitEntered = entered;
            scenario.Provider.DmaSubmitRelease = release;
            scenario.Provider.Dsc1SubmitEntered = contenderProviderEntered;
            Task<KernelResult<PlatformDmaSubmission>> firstTask = Task.Run(() => scenario.Kernel.SubmitPlatformDma(
                scenario.Subject,
                dmaGrant,
                prepared.Value!));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Task<KernelResult<PlatformDsc1CopySubmission>> waitingDsc1 = Task.Run(() =>
            {
                contenderStarted.Set();
                return SubmitDsc1(
                    scenario,
                    scenario.First,
                    scenario.Second,
                    DisjointDscOffset);
            });
            try
            {
                Assert.True(contenderStarted.Wait(TimeSpan.FromSeconds(5)));
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                KernelResult<PlatformDsc1CopySubmission> pending = await waitingDsc1.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(KernelError.PlatformBindingDraining, pending.Error);
                Assert.False(contenderProviderEntered.IsSet);
            }
            finally
            {
                release.Set();
            }

            KernelResult<PlatformDmaSubmission> accepted = await firstTask;
            Assert.True(accepted.IsSuccess, accepted.Message);
            KernelResult<PlatformDsc1CopySubmission> rejected = await waitingDsc1;
            Assert.False(rejected.IsSuccess);
            Assert.Equal(KernelError.PlatformBindingDraining, rejected.Error);
            Assert.False(contenderProviderEntered.IsSet);
            Assert.Equal(0, scenario.Provider.Dsc1SubmitCalls);
            CompleteAndFinalizeDma(scenario, accepted.Value!);
        }
        else
        {
            scenario.Provider.Dsc1SubmitEntered = entered;
            scenario.Provider.Dsc1SubmitRelease = release;
            scenario.Provider.DmaSubmitEntered = contenderProviderEntered;
            Task<KernelResult<PlatformDsc1CopySubmission>> firstTask = Task.Run(() => SubmitDsc1(
                scenario,
                scenario.First,
                scenario.Second,
                DisjointDscOffset));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Task<KernelResult<PlatformDmaSubmission>> waitingDma = Task.Run(() =>
            {
                contenderStarted.Set();
                return scenario.Kernel.SubmitPlatformDma(
                    scenario.Subject,
                    dmaGrant,
                    prepared.Value!);
            });
            try
            {
                Assert.True(contenderStarted.Wait(TimeSpan.FromSeconds(5)));
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                Assert.True(waitingDma.IsCompleted);
                Assert.False(contenderProviderEntered.IsSet);
            }
            finally
            {
                release.Set();
            }

            KernelResult<PlatformDsc1CopySubmission> accepted = await firstTask;
            Assert.True(accepted.IsSuccess, accepted.Message);
            KernelResult<PlatformDmaSubmission> rejected = await waitingDma;
            Assert.False(rejected.IsSuccess);
            Assert.Equal(KernelError.PlatformBindingActive, rejected.Error);
            Assert.False(contenderProviderEntered.IsSet);
            Assert.Equal(0, scenario.Provider.DmaSubmitCalls);
            Assert.True(scenario.Kernel.CancelPlatformDsc1Copy(
                scenario.Subject,
                accepted.Value!).IsSuccess);
        }
    }

    private static void AssertDsc1Conflict(
        Scenario scenario,
        KernelError expectedError = KernelError.PlatformBindingActive)
    {
        int before = scenario.Provider.Dsc1SubmitCalls;
        KernelResult<PlatformDsc1CopySubmission> conflict = SubmitDsc1(
            scenario,
            scenario.First,
            scenario.Second,
            DisjointDscOffset);
        Assert.False(conflict.IsSuccess);
        Assert.Equal(expectedError, conflict.Error);
        Assert.Equal(before, scenario.Provider.Dsc1SubmitCalls);
        scenario.First.Buffer.Span[0] = 0x11;
        scenario.Second.Buffer.Span[0] = 0x22;
    }

    private static PlatformDmaGrant CreateDmaGrant(
        Scenario scenario,
        MappedBuffer mapped,
        PlatformDmaDirection direction)
    {
        KernelResult<PlatformDmaGrant> grant = scenario.Kernel.BindPlatformDma(
            scenario.Subject,
            scenario.Device,
            mapped.Mapping,
            DmaOffset,
            OperationLength,
            direction);
        Assert.True(grant.IsSuccess, grant.Message);
        return grant.Value!;
    }

    private static PlatformDmaSubmission PrepareAndSubmitDma(
        Scenario scenario,
        PlatformDmaGrant grant)
    {
        KernelResult<PlatformDmaPrepareEvidence> prepared = scenario.Kernel.PreparePlatformDmaForDevice(
            scenario.Subject,
            grant);
        Assert.True(prepared.IsSuccess, prepared.Message);
        KernelResult<PlatformDmaSubmission> submitted = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            grant,
            prepared.Value!);
        Assert.True(submitted.IsSuccess, submitted.Message);
        return submitted.Value!;
    }

    private static void CompleteAndFinalizeDma(
        Scenario scenario,
        PlatformDmaSubmission submission)
    {
        scenario.Provider.DmaCompletionFailure = null;
        scenario.Provider.DmaCompletionState = PlatformProviderDmaCompletionState.Completed;
        KernelResult<PlatformDmaCompletionEvidence> completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            submission);
        Assert.True(completion.IsSuccess, completion.Message);
        KernelResult<PlatformDmaPostCompletionVisibilityEvidence> visibility = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject,
            submission,
            completion.Value!);
        Assert.True(visibility.IsSuccess, visibility.Message);
    }

    private static KernelResult<PlatformDsc1CopySubmission> SubmitDsc1(
        Scenario scenario,
        MappedBuffer source,
        MappedBuffer destination,
        long offset) =>
        SubmitDsc1(
            scenario,
            source.Buffer,
            source.Mapping.Mapping,
            destination,
            offset);

    private static KernelResult<PlatformDsc1CopySubmission> SubmitDsc1(
        Scenario scenario,
        OwnedBuffer<byte> sourceBuffer,
        PlatformRegionMapping sourceMapping,
        MappedBuffer destination,
        long offset) =>
        scenario.Kernel.SubmitPlatformDsc1Copy(
            scenario.Subject,
            scenario.Binding,
            scenario.ComputeCapability,
            sourceBuffer,
            new PlatformDsc1RegionRange(
                sourceMapping,
                offset,
                OperationLength),
            destination.Buffer,
            new PlatformDsc1RegionRange(
                destination.Mapping.Mapping,
                destination.Mapping.Offset,
                OperationLength));

    private static Scenario CreateScenario(ulong processId, ulong domainId)
    {
        CombinedProvider provider = new CombinedProvider();
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, processId, domainId);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(
            kernel,
            processId + 10_000,
            domainId + 10_000);
        SingProcess process = kernel.Processes.Resolve(subject).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(subject).Value!;
        PlatformDeviceRights deviceRights = PlatformDeviceRights.Configure |
                           PlatformDeviceRights.Read |
                           PlatformDeviceRights.Write;
        CapabilityId deviceCapability = Mint(
            kernel,
            subject,
            process.DomainId,
            ResourceKind.Device,
            $"device/dma-dsc1-interlock-{processId}",
            CapabilityRights.Configure | CapabilityRights.Read | CapabilityRights.Write);
        PlatformDeviceLease device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            deviceRights).Value!;
        Dsc1ComputeCapability computeCapability = new Dsc1ComputeCapability(Mint(
            kernel,
            subject,
            process.DomainId,
            ResourceKind.Compute,
            CapabilityResourceIds.Dsc1Copy,
            CapabilityRights.Execute));

        return new Scenario(
            kernel,
            provider,
            subject,
            target,
            binding,
            device,
            computeCapability,
            CreateMappedBuffer(kernel, subject, process.DomainId, binding),
            CreateMappedBuffer(kernel, subject, process.DomainId, binding),
            CreateMappedBuffer(kernel, subject, process.DomainId, binding),
            CreateMappedBuffer(kernel, subject, process.DomainId, binding));
    }

    private static MappedBuffer CreateMappedBuffer(
        RuntimeKernel kernel,
        ProcessHandle subject,
        DomainId domainId,
        PlatformDomainBinding binding)
    {
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(subject, 128).Value!;
        CapabilityId capability = Mint(
            kernel,
            subject,
            domainId,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write);
        KernelResult<PlatformOwnedRegionSliceMapping> mapping = kernel.MapPlatformOwnedRegionSlice(
            subject,
            binding,
            capability,
            buffer.Handle,
            16,
            96,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write);
        Assert.True(mapping.IsSuccess, mapping.Message);
        return new MappedBuffer(buffer, mapping.Value!, capability);
    }

    private static CapabilityId Mint(
        RuntimeKernel kernel,
        ProcessHandle subject,
        DomainId domainId,
        ResourceKind kind,
        string resourceId,
        CapabilityRights rights)
    {
        KernelResult<CapabilityDescriptorV1> minted = kernel.MintCapability(
            domainId,
            subject,
            kind,
            resourceId,
            rights);
        Assert.True(minted.IsSuccess, minted.Message);
        return minted.Value!.CapabilityId;
    }

    private sealed record MappedBuffer(
        OwnedBuffer<byte> Buffer,
        PlatformOwnedRegionSliceMapping Mapping,
        CapabilityId Capability);

    private sealed record Scenario(
        RuntimeKernel Kernel,
        CombinedProvider Provider,
        ProcessHandle Subject,
        ProcessHandle Target,
        PlatformDomainBinding Binding,
        PlatformDeviceLease Device,
        Dsc1ComputeCapability ComputeCapability,
        MappedBuffer First,
        MappedBuffer Second,
        MappedBuffer Third,
        MappedBuffer Fourth);

    public enum DmaSubmitFaultMode
    {
        None = 0,
        Denied,
        ReturnedFaulted,
        MalformedSuccess,
        Throw,
    }

    public enum FirstSubmitMechanism
    {
        Dma = 0,
        Dsc1,
    }

    private sealed class CombinedProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformCompletionProvider,
        IPlatformRegionRevocationProvider,
        IPlatformDeviceLeaseProvider,
        IPlatformOwnedRegionMappingProvider,
        IPlatformDmaGrantProvider,
        IPlatformDmaVisibilityProvider,
        IPlatformDmaSubmissionProvider,
        IPlatformDmaCompletionProvider,
        IPlatformDsc1ComputeProvider
    {
        private readonly HostPlatformAuthorityProvider _host = new(
            deferDsc1Completion: true);
        private readonly Dictionary<PlatformProviderDeviceLeaseId, PlatformProviderDeviceLease>
            _devices = [];
        private readonly Dictionary<PlatformProviderRegionMappingId, PlatformProviderOwnedRegionMapping>
            _mappings = [];
        private readonly Dictionary<PlatformOperationId, PlatformProviderRegionMappingLease>
            _mappingClosures = [];
        private readonly Dictionary<PlatformProviderDmaGrantId, PlatformProviderDmaGrant>
            _dmaGrants = [];
        private readonly Dictionary<PlatformProviderDmaGrantId, PlatformProviderDmaVisibilityCycle>
            _dmaCycles = [];
        private readonly Dictionary<PlatformProviderDmaGrantId, PlatformProviderDmaSubmission>
            _dmaSubmissions = [];
        private readonly HashSet<PlatformProviderDmaGrantId> _dmaCompleted = [];
        private readonly HashSet<PlatformProviderDmaGrantId> _dmaAcquired = [];
        private readonly Dictionary<
            PlatformOperationIdentity,
            PlatformProviderDsc1Completion> _dsc1PendingCancellationCompletions = [];
        private ulong _nextDeviceId = 1;
        private ulong _nextDmaGrantId = 1;
        private ulong _nextDmaCycle = 1;
        private ulong _nextDmaSubmissionId = 1;

        public int DmaSubmitCalls { get; private set; }
        public int DmaCompletionCalls { get; private set; }
        public int Dsc1SubmitCalls { get; private set; }
        public int Dsc1ObserveCalls { get; private set; }
        public int Dsc1CancelCalls { get; private set; }
        public DmaSubmitFaultMode DmaSubmitBehavior { get; set; }
        public PlatformAuthorityStatus? DmaCompletionFailure { get; set; }
        public PlatformProviderDmaCompletionState DmaCompletionState { get; set; } =
            PlatformProviderDmaCompletionState.Pending;
        public bool Dsc1SubmitDenied { get; set; }
        public bool Dsc1CancelPendingOnce { get; set; }
        public Action? BeforeDsc1Cancel { get; set; }
        public bool Dsc1CompletionFaulted { get; set; }
        public ManualResetEventSlim? DmaSubmitEntered { get; set; }
        public ManualResetEventSlim? DmaSubmitRelease { get; set; }
        public ManualResetEventSlim? Dsc1SubmitEntered { get; set; }
        public ManualResetEventSlim? Dsc1SubmitRelease { get; set; }

        public PlatformProviderDescriptor Descriptor => _host.Descriptor;

        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.NeutralDomains,
                PlatformDomainContract.ContractVersion,
                PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.OwnedRegionMapping,
                PlatformOwnedRegionMappingContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.IoDomainBinding,
                PlatformDeviceLeaseContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.DmaMapping,
                PlatformDmaLifecycleContract.ContractVersion,
                PlatformFeatureAvailability.RuntimeAdmission),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.Dsc1BulkCompute,
                PlatformDsc1ComputeContract.ContractVersion,
                PlatformFeatureAvailability.ModelOnly),
        });

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject) => _host.BindDomain(subject);

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            if (_devices.Count != 0 || _mappings.Count != 0 || _dmaGrants.Count != 0)
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Lower provider authority remains live.");
            }

            return _host.RevokeDomain(lease);
        }

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access) =>
            _host.MapOwnedRegion(domainLease, region, access);

        public PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSlice(
            PlatformProviderDomainLease domainLease,
            PlatformRegionSlice slice)
        {
            PlatformAuthorityResult<PlatformProviderRegionMappingLease> lease = _host.MapOwnedRegion(
                domainLease,
                slice.Region,
                slice.Access);
            if (!lease.IsSuccess)
            {
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    lease.Status,
                    lease.Message!);
            }

            PlatformProviderOwnedRegionMapping mapping = new PlatformProviderOwnedRegionMapping(lease.Value!, slice);
            _mappings.Add(mapping.Lease.MappingId, mapping);
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Ok(mapping);
        }

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy)
        {
            if (HasDmaGrantForMapping(mapping))
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    "A DMA grant still references the mapping.");
            }

            PlatformAuthorityResult revoked = _host.RevokeRegionMapping(mapping, policy);
            if (revoked.IsSuccess)
                _mappings.Remove(mapping.MappingId);
            return revoked;
        }

        public PlatformAuthorityResult<PlatformRegionRevocationTicket>
            BeginRegionMappingRevocation(
                PlatformProviderRegionMappingLease mapping,
                PlatformRegionRevocationPolicy policy)
        {
            if (HasDmaGrantForMapping(mapping))
            {
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "A DMA grant still references the mapping.");
            }

            PlatformAuthorityResult<PlatformRegionRevocationTicket> begun = _host.BeginRegionMappingRevocation(mapping, policy);
            if (begun.IsSuccess)
                _mappingClosures[begun.Value!.Operation.OperationId] = mapping;
            return begun;
        }

        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
            PlatformOperationIdentity operation)
        {
            PlatformAuthorityResult<PlatformCompletionReceipt> observed = _host.ObserveCompletion(operation);
            if (observed.IsSuccess && observed.Value!.ProvesClosure &&
                _mappingClosures.Remove(operation.OperationId, out PlatformProviderRegionMappingLease mapping))
            {
                _mappings.Remove(mapping.MappingId);
            }

            return observed;
        }

        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(
            PlatformProviderDomainLease domainLease,
            PlatformDeviceIdentity device,
            PlatformDeviceRights rights)
        {
            PlatformAuthorityResult request = PlatformDeviceLeaseContract.ValidateRequest(device, rights);
            if (!request.IsSuccess)
            {
                return PlatformAuthorityResult<PlatformProviderDeviceLease>.Fail(
                    request.Status,
                    request.Message!);
            }

            PlatformProviderDeviceLease lease = new PlatformProviderDeviceLease(
                new PlatformProviderDeviceLeaseId(_nextDeviceId++),
                new PlatformProviderLeaseGeneration(1),
                domainLease,
                device,
                rights);
            _devices.Add(lease.LeaseId, lease);
            return PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease)
        {
            if (_dmaGrants.Values.Any(grant => grant.DeviceLease == lease))
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    "A DMA grant still references the device.");
            }

            _devices.Remove(lease.LeaseId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderDmaGrant> BindDmaGrant(
            PlatformDmaGrantRequest request)
        {
            PlatformAuthorityResult validation = PlatformDmaGrantContract.ValidateRequest(request);
            if (!validation.IsSuccess)
            {
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                    validation.Status,
                    validation.Message!);
            }

            if (!_devices.ContainsKey(request.DeviceLease.LeaseId) ||
                !_mappings.TryGetValue(request.MappingLease.MappingId, out PlatformProviderOwnedRegionMapping mapping) ||
                mapping.Slice != request.MappingSlice)
            {
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Unknown exact device or mapping authority.");
            }

            PlatformProviderDmaGrant grant = new PlatformProviderDmaGrant(
                new PlatformProviderDmaGrantId(_nextDmaGrantId++),
                new PlatformProviderLeaseGeneration(1),
                request.DeviceLease,
                request.MappingLease,
                request.Range,
                request.Direction);
            _dmaGrants.Add(grant.GrantId, grant);
            return PlatformAuthorityResult<PlatformProviderDmaGrant>.Ok(grant);
        }

        public PlatformAuthorityResult RevokeDmaGrant(PlatformProviderDmaGrant grant)
        {
            if (_dmaSubmissions.ContainsKey(grant.GrantId))
            {
                if (!_dmaCompleted.Contains(grant.GrantId) ||
                    (grant.Direction != PlatformDmaDirection.DeviceReadsMemory &&
                     !_dmaAcquired.Contains(grant.GrantId)))
                {
                    return PlatformAuthorityResult.Fail(
                        PlatformAuthorityStatus.Denied,
                        "The exact DMA lifecycle has not closed.");
                }

                _dmaSubmissions.Remove(grant.GrantId);
            }

            _dmaCompleted.Remove(grant.GrantId);
            _dmaAcquired.Remove(grant.GrantId);
            _dmaCycles.Remove(grant.GrantId);
            _dmaGrants.Remove(grant.GrantId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>
            PrepareDmaGrantVisibility(PlatformProviderDmaGrant grant)
        {
            if (!_dmaGrants.TryGetValue(grant.GrantId, out PlatformProviderDmaGrant exact) ||
                exact != grant ||
                _dmaSubmissions.ContainsKey(grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "The DMA grant is not eligible for prepare.");
            }

            PlatformProviderDmaVisibilityCycle cycle = new PlatformProviderDmaVisibilityCycle(_nextDmaCycle++);
            _dmaCycles[grant.GrantId] = cycle;
            _dmaAcquired.Remove(grant.GrantId);
            return PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Ok(
                new PlatformProviderDmaPrepareEvidence(
                    grant.GrantId,
                    grant.Generation,
                    cycle,
                    grant.Direction,
                    PlatformMemoryVisibilityRequirement.PublicationFence,
                    PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied));
        }

        public PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>
            AcquireDmaGrantVisibility(PlatformProviderDmaGrant grant)
        {
            if (!_dmaGrants.TryGetValue(grant.GrantId, out PlatformProviderDmaGrant exact) ||
                exact != grant ||
                !_dmaCycles.TryGetValue(grant.GrantId, out PlatformProviderDmaVisibilityCycle cycle) ||
                !_dmaCompleted.Contains(grant.GrantId) ||
                _dmaAcquired.Contains(grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "No completed exact DMA cycle is available for acquire.");
            }

            _dmaAcquired.Add(grant.GrantId);
            return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Ok(
                new PlatformProviderDmaAcquireEvidence(
                    grant.GrantId,
                    grant.Generation,
                    cycle,
                    grant.Direction,
                    PlatformMemoryAcquireRequirement.AcquisitionFence,
                    PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied));
        }

        public PlatformAuthorityResult<PlatformProviderDmaSubmission> SubmitDma(
            PlatformProviderDmaSubmitRequest request)
        {
            DmaSubmitCalls++;
            DmaSubmitEntered?.Set();
            if (DmaSubmitRelease is { } release &&
                !release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Timed out waiting to release DMA submit.");
            }

            switch (DmaSubmitBehavior)
            {
                case DmaSubmitFaultMode.Denied:
                    return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Fail(
                        PlatformAuthorityStatus.Denied,
                        "Injected DMA submission denial.");
                case DmaSubmitFaultMode.ReturnedFaulted:
                    return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Fail(
                        PlatformAuthorityStatus.Faulted,
                        "Injected ambiguous DMA submission fault.");
                case DmaSubmitFaultMode.Throw:
                    throw new InvalidOperationException("Injected DMA submission exception.");
            }

            PlatformAuthorityResult validation = PlatformDmaSubmissionContract.ValidateRequest(request);
            if (!validation.IsSuccess)
            {
                return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Fail(
                    validation.Status,
                    validation.Message!);
            }

            if (!_dmaGrants.TryGetValue(request.Grant.GrantId, out PlatformProviderDmaGrant grant) ||
                grant != request.Grant ||
                !_dmaCycles.TryGetValue(request.Grant.GrantId, out PlatformProviderDmaVisibilityCycle cycle) ||
                cycle != request.PreparedCycle ||
                _dmaSubmissions.ContainsKey(request.Grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "DMA submission does not match the exact prepared cycle.");
            }

            PlatformProviderDmaSubmission submission = new PlatformProviderDmaSubmission(
                new PlatformProviderDmaSubmissionId(_nextDmaSubmissionId++),
                new PlatformProviderDmaSubmissionGeneration(1),
                request.Grant.GrantId,
                request.Grant.Generation,
                request.PreparedCycle,
                request.Grant.Range,
                request.Grant.Direction);
            _dmaSubmissions.Add(submission.GrantId, submission);
            return DmaSubmitBehavior == DmaSubmitFaultMode.MalformedSuccess
                ? PlatformAuthorityResult<PlatformProviderDmaSubmission>.Ok(
                    submission with
                    {
                        Range = new PlatformDmaRange(
                            submission.Range.Offset,
                            submission.Range.Length + 1),
                    })
                : PlatformAuthorityResult<PlatformProviderDmaSubmission>.Ok(submission);
        }

        public PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence>
            ObserveDmaCompletion(PlatformProviderDmaSubmission submission)
        {
            DmaCompletionCalls++;
            if (!_dmaSubmissions.TryGetValue(submission.GrantId, out PlatformProviderDmaSubmission exact) ||
                exact != submission)
            {
                return PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence>.Fail(
                    PlatformAuthorityStatus.Stale,
                    "Unknown exact DMA submission.");
            }

            if (DmaCompletionFailure is { } failure)
            {
                return PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence>.Fail(
                    failure,
                    "Injected DMA completion failure.");
            }

            if (DmaCompletionState == PlatformProviderDmaCompletionState.Completed)
                _dmaCompleted.Add(submission.GrantId);

            return PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence>.Ok(
                new PlatformProviderDmaCompletionEvidence(
                    submission.SubmissionId,
                    submission.Generation,
                    submission.GrantId,
                    submission.GrantGeneration,
                    submission.PreparedCycle,
                    submission.Range,
                    submission.Direction,
                    DmaCompletionState));
        }

        public PlatformAuthorityResult<PlatformProviderDsc1Submission> SubmitDsc1Copy(
            PlatformDsc1CopyRequest request)
        {
            Dsc1SubmitCalls++;
            Dsc1SubmitEntered?.Set();
            if (Dsc1SubmitRelease is { } release &&
                !release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Timed out waiting to release DSC1 submit.");
            }

            return Dsc1SubmitDenied
                ? PlatformAuthorityResult<PlatformProviderDsc1Submission>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Injected DSC1 submission denial.")
                : _host.SubmitDsc1Copy(request);
        }

        public PlatformAuthorityResult<PlatformProviderDsc1Completion>
            ObserveDsc1Completion(PlatformProviderDsc1Submission submission)
        {
            Dsc1ObserveCalls++;
            if (Dsc1CompletionFaulted)
            {
                return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Ok(
                    new PlatformProviderDsc1Completion(
                        submission,
                        new PlatformCompletionReceipt(
                            submission.Operation.OperationId,
                            submission.Operation.Generation,
                            submission.Operation.DomainLease,
                            PlatformCompletionState.Faulted),
                        PlatformDsc1CompletionDisposition.Faulted));
            }

            if (_dsc1PendingCancellationCompletions.Remove(
                    submission.Operation,
                    out PlatformProviderDsc1Completion cancelled))
            {
                return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Ok(
                    cancelled);
            }

            return _host.ObserveDsc1Completion(submission);
        }

        public PlatformAuthorityResult<PlatformProviderDsc1Completion> CancelDsc1(
            PlatformProviderDsc1Submission submission)
        {
            Dsc1CancelCalls++;
            BeforeDsc1Cancel?.Invoke();
            if (Dsc1CancelPendingOnce &&
                !_dsc1PendingCancellationCompletions.ContainsKey(
                    submission.Operation))
            {
                PlatformAuthorityResult<PlatformProviderDsc1Completion> cancelled = _host.CancelDsc1(submission);
                if (!cancelled.IsSuccess)
                    return cancelled;

                _dsc1PendingCancellationCompletions.Add(
                    submission.Operation,
                    cancelled.Value!);
                return PlatformAuthorityResult<PlatformProviderDsc1Completion>.Ok(
                    new PlatformProviderDsc1Completion(
                        submission,
                        new PlatformCompletionReceipt(
                            submission.Operation.OperationId,
                            submission.Operation.Generation,
                            submission.Operation.DomainLease,
                            PlatformCompletionState.Draining),
                        PlatformDsc1CompletionDisposition.Pending));
            }

            return _host.CancelDsc1(submission);
        }

        public PlatformAuthorityResult<PlatformProviderDsc1Completion> CompleteLastDsc1() =>
            _host.LastDsc1Submission is { } submission
                ? _host.CompleteDsc1Copy(submission)
                : PlatformAuthorityResult<PlatformProviderDsc1Completion>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "No DSC1 submission has been accepted.");

        private bool HasDmaGrantForMapping(
            PlatformProviderRegionMappingLease mapping) =>
            _dmaGrants.Values.Any(grant => grant.MappingLease == mapping);
    }
}
