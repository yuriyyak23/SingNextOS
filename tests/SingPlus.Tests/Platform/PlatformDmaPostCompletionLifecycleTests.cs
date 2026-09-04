using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class PlatformDmaPostCompletionLifecycleTests
{
    [Fact]
    public void WriteDmaRequiresPostCompletionAcquireBeforeControlledReleaseAndCpuTransfer()
    {
        var scenario = CreateScenario(1801, 1810, PlatformDmaDirection.DeviceWritesMemory);
        var submission = PrepareAndSubmit(scenario);

        var blockedTransfer = scenario.Kernel.TransferRegion(
            scenario.Subject,
            scenario.Target,
            scenario.Buffer);
        Assert.False(blockedTransfer.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, blockedTransfer.Error);

        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;
        var completion = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission);
        Assert.True(completion.IsSuccess, completion.Message);

        var earlyRevoke = scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant);
        Assert.False(earlyRevoke.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, earlyRevoke.Error);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);

        var visibility = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject,
            submission,
            completion.Value!);
        Assert.True(visibility.IsSuccess, visibility.Message);
        Assert.True(visibility.Value!.IsSatisfied);
        Assert.Equal(
            PlatformDmaPostCompletionVisibilityRequirement.AcquisitionFence,
            visibility.Value.Requirement);
        Assert.Equal(
            PlatformDmaPostCompletionVisibilityOutcome.AcquisitionFenceSatisfied,
            visibility.Value.Outcome);
        Assert.Equal(submission.OperationId, visibility.Value.OperationId);
        Assert.Equal(submission.Generation, visibility.Value.OperationGeneration);
        Assert.Equal(submission.GrantId, visibility.Value.GrantId);
        Assert.Equal(submission.GrantGeneration, visibility.Value.GrantGeneration);
        Assert.Equal(submission.PreparedCycle, visibility.Value.PreparedCycle);
        Assert.Equal(1, scenario.Provider.AcquireCalls);

        AssertSuccess(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant));
        AssertSuccess(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping));
        AssertSuccess(scenario.Kernel.RevokePlatformDevice(scenario.Subject, scenario.Device));
        AssertSuccess(scenario.Kernel.RevokePlatformDomain(scenario.Subject, scenario.Binding));
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(1, scenario.Provider.MappingRevokeCalls);
        Assert.Equal(1, scenario.Provider.DeviceRevokeCalls);

        var moved = scenario.Kernel.TransferRegion(
            scenario.Subject,
            scenario.Target,
            scenario.Buffer);
        Assert.True(moved.IsSuccess, moved.Message);
        Assert.NotEqual(scenario.Buffer.Handle.Generation, moved.Value!.Handle.Generation);

        var feature = scenario.Kernel.QueryPlatformFeatures().Resolve(PlatformFeatureFamily.DmaMapping);
        Assert.Equal(PlatformDmaLifecycleContract.ContractVersion, feature.ContractVersion);
        Assert.Equal(PlatformFeatureAvailability.RuntimeAdmission, feature.Availability);
        Assert.NotEqual(PlatformFeatureAvailability.Executable, feature.Availability);

        var forbidden = new[]
        {
            "PlatformProvider", "Neutral", "Physical", "BusAddress", "Iommu",
            "PageTable", "Pte", "Descriptor", "Queue", "Vector", "Controller",
            "Vmcs", "Vmx", "Lane", "Opcode",
        };
        foreach (var member in typeof(PlatformDmaPostCompletionVisibilityEvidence).GetMembers(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            var signature = member.ToString() ?? member.Name;
            foreach (var term in forbidden)
                Assert.DoesNotContain(term, signature, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DeviceReadOnlyCompletionNeedsNoAcquireButStillConsumesSubmittedCycle()
    {
        var scenario = CreateScenario(1802, 1820, PlatformDmaDirection.DeviceReadsMemory);
        var submission = PrepareAndSubmit(scenario);
        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;
        var completion = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission);
        Assert.True(completion.IsSuccess, completion.Message);

        var visibility = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject,
            submission,
            completion.Value!);
        Assert.True(visibility.IsSuccess, visibility.Message);
        Assert.True(visibility.Value!.IsSatisfied);
        Assert.Equal(PlatformDmaPostCompletionVisibilityRequirement.None, visibility.Value.Requirement);
        Assert.Equal(PlatformDmaPostCompletionVisibilityOutcome.NotRequired, visibility.Value.Outcome);
        Assert.Equal(0, scenario.Provider.AcquireCalls);

        var replaySubmit = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            new PlatformDmaPrepareEvidence(
                submission.GrantId,
                submission.GrantGeneration,
                submission.PreparedCycle,
                submission.Direction,
                PlatformMemoryVisibilityRequirement.PublicationFence,
                PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied));
        Assert.False(replaySubmit.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, replaySubmit.Error);

        AssertSuccess(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant));
        AssertSuccess(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping));
        AssertSuccess(scenario.Kernel.RevokePlatformDevice(scenario.Subject, scenario.Device));
    }

    [Fact]
    public void CompletionAndVisibilityEvidenceMustMatchExactOperationGenerationAndCycle()
    {
        var scenario = CreateScenario(1803, 1830, PlatformDmaDirection.DeviceWritesMemory);
        var submission = PrepareAndSubmit(scenario);

        var fabricatedEarlyCompletion = new PlatformDmaCompletionEvidence(
            submission.OperationId,
            submission.Generation,
            submission.GrantId,
            submission.GrantGeneration,
            submission.PreparedCycle,
            submission.Range,
            submission.Direction);
        var beforeCompletion = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject,
            submission,
            fabricatedEarlyCompletion);
        Assert.False(beforeCompletion.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, beforeCompletion.Error);
        Assert.Equal(0, scenario.Provider.AcquireCalls);

        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;
        var completion = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission);
        Assert.True(completion.IsSuccess, completion.Message);

        var staleGeneration = completion.Value! with
        {
            OperationGeneration = new PlatformDmaOperationGeneration(
                completion.Value.OperationGeneration.Value + 1),
        };
        Assert.Equal(
            KernelError.StaleGeneration,
            scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
                scenario.Subject,
                submission,
                staleGeneration).Error);

        var wrongCycle = completion.Value with
        {
            PreparedCycle = new PlatformDmaVisibilityCycle(completion.Value.PreparedCycle.Value + 1),
        };
        Assert.Equal(
            KernelError.PlatformDenied,
            scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
                scenario.Subject,
                submission,
                wrongCycle).Error);

        var wrongGrantGeneration = completion.Value with
        {
            GrantGeneration = new PlatformDmaGrantGeneration(
                completion.Value.GrantGeneration.Value + 1),
        };
        Assert.Equal(
            KernelError.StaleGeneration,
            scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
                scenario.Subject,
                submission,
                wrongGrantGeneration).Error);
        Assert.Equal(0, scenario.Provider.AcquireCalls);

        var valid = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject,
            submission,
            completion.Value);
        Assert.True(valid.IsSuccess, valid.Message);
        Assert.Equal(1, scenario.Provider.AcquireCalls);

        var replay = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject,
            submission,
            completion.Value);
        Assert.False(replay.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingNotFound, replay.Error);
        Assert.Equal(1, scenario.Provider.AcquireCalls);
    }

    [Fact]
    public void PreSubmitAcquireCannotSatisfyFreshPostCompletionAcquire()
    {
        var scenario = CreateScenario(1804, 1840, PlatformDmaDirection.Bidirectional);
        var firstPrepare = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant);
        Assert.True(firstPrepare.IsSuccess, firstPrepare.Message);
        var preSubmitAcquire = scenario.Kernel.AcquirePlatformDmaForCpu(scenario.Subject, scenario.Grant);
        Assert.True(preSubmitAcquire.IsSuccess, preSubmitAcquire.Message);
        Assert.Equal(firstPrepare.Value!.Cycle, preSubmitAcquire.Value!.Cycle);
        Assert.Equal(1, scenario.Provider.AcquireCalls);

        var secondPrepare = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant);
        Assert.True(secondPrepare.IsSuccess, secondPrepare.Message);
        Assert.NotEqual(firstPrepare.Value.Cycle, secondPrepare.Value!.Cycle);
        var submit = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            scenario.Grant,
            secondPrepare.Value);
        Assert.True(submit.IsSuccess, submit.Message);

        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;
        var completion = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submit.Value!);
        Assert.True(completion.IsSuccess, completion.Message);

        var post = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject,
            submit.Value!,
            completion.Value!);
        Assert.True(post.IsSuccess, post.Message);
        Assert.Equal(secondPrepare.Value.Cycle, post.Value!.PreparedCycle);
        Assert.Equal(2, scenario.Provider.AcquireCalls);
    }

    [Fact]
    public void MalformedPostCompletionAcquireFaultPinsGrantMappingAndCpuReuse()
    {
        var scenario = CreateScenario(1805, 1850, PlatformDmaDirection.DeviceWritesMemory);
        var submission = PrepareAndSubmit(scenario);
        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;
        var completion = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission);
        Assert.True(completion.IsSuccess, completion.Message);

        scenario.Provider.AcquireMutation = AcquireMutation.WrongCycle;
        var visibility = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject,
            submission,
            completion.Value!);
        Assert.False(visibility.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, visibility.Error);

        Assert.Equal(
            KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(
            KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping).Error);
        Assert.Equal(
            KernelError.PlatformFaulted,
            scenario.Kernel.RevokePlatformDevice(scenario.Subject, scenario.Device).Error);
        var transfer = scenario.Kernel.TransferRegion(
            scenario.Subject,
            scenario.Target,
            scenario.Buffer);
        Assert.False(transfer.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, transfer.Error);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        Assert.Equal(0, scenario.Provider.DeviceRevokeCalls);
    }

    [Fact]
    public void CapabilityRevokeStopsNewEffectsButCompletionDrainCanStillReachReclaim()
    {
        var scenario = CreateScenario(1806, 1860, PlatformDmaDirection.DeviceWritesMemory);
        var submission = PrepareAndSubmit(scenario);

        var revoke = scenario.Kernel.RevokeCapability(scenario.DeviceCapability);
        Assert.False(revoke.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, revoke.Error);

        var newPrepare = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, scenario.Grant);
        Assert.False(newPrepare.IsSuccess);

        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;
        var completion = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission);
        Assert.True(completion.IsSuccess, completion.Message);
        var visibility = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject,
            submission,
            completion.Value!);
        Assert.True(visibility.IsSuccess, visibility.Message);

        AssertSuccess(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant));
        AssertSuccess(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping));

        var moved = scenario.Kernel.TransferRegion(
            scenario.Subject,
            scenario.Target,
            scenario.Buffer);
        Assert.True(moved.IsSuccess, moved.Message);
    }

    [Fact]
    public void ProcessExitReclaimsOnlyAfterCompletionPostVisibilityAndAuthorityClosure()
    {
        var scenario = CreateScenario(1807, 1870, PlatformDmaDirection.DeviceWritesMemory);
        var submission = PrepareAndSubmit(scenario);

        var terminate = scenario.Kernel.TerminateProcess(scenario.Subject);
        Assert.False(terminate.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, terminate.Error);
        var before = scenario.Kernel.QueryProcessTeardown(scenario.Subject);
        Assert.True(before.IsSuccess, before.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformDraining, before.Value!.Phase);
        Assert.False(before.Value.LocalReclaimCompleted);

        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;
        var completion = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission);
        Assert.True(completion.IsSuccess, completion.Message);
        var visibility = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject,
            submission,
            completion.Value!);
        Assert.True(visibility.IsSuccess, visibility.Message);

        var progress = scenario.Kernel.ObserveProcessTeardown(scenario.Subject);
        Assert.True(progress.IsSuccess, progress.Message);
        Assert.True(progress.Value!.LocalReclaimCompleted);
        Assert.Equal(ProcessTeardownPhase.PlatformClosed, progress.Value.Phase);
        Assert.True(progress.Value.PlatformDomainClosed);
        Assert.Equal(0, progress.Value.PendingPlatformMappings);
        Assert.Equal(1, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(1, scenario.Provider.MappingRevokeCalls);
        Assert.Equal(1, scenario.Provider.DeviceRevokeCalls);

        var reclaimed = scenario.Kernel.Regions.Snapshot()
            .Single(region => region.Handle.RegionId == scenario.Buffer.Handle.RegionId);
        Assert.Equal(RegionState.Released, reclaimed.State);
    }

    [Fact]
    public void CompletionFaultInjectionKeepsEndToEndReclaimPinned()
    {
        var scenario = CreateScenario(1808, 1880, PlatformDmaDirection.DeviceWritesMemory);
        var submission = PrepareAndSubmit(scenario);
        scenario.Provider.CompletionFailure = PlatformAuthorityStatus.Faulted;

        var completion = scenario.Kernel.ObservePlatformDmaCompletion(scenario.Subject, submission);
        Assert.False(completion.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, completion.Error);

        var terminate = scenario.Kernel.TerminateProcess(scenario.Subject);
        Assert.False(terminate.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, terminate.Error);
        var teardown = scenario.Kernel.QueryProcessTeardown(scenario.Subject);
        Assert.True(teardown.IsSuccess, teardown.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, teardown.Value!.Phase);
        Assert.False(teardown.Value.LocalReclaimCompleted);
        Assert.False(teardown.Value.PlatformDomainClosed);

        var region = scenario.Kernel.Regions.Snapshot()
            .Single(item => item.Handle.RegionId == scenario.Buffer.Handle.RegionId);
        Assert.Equal(RegionState.Owned, region.State);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        Assert.Equal(0, scenario.Provider.DeviceRevokeCalls);
    }

    [Fact]
    public void ExactDmaCompletionPublishesOneGenerationBoundNotificationWithoutReclaim()
    {
        var scenario = CreateScenario(1810, 1910, PlatformDmaDirection.DeviceWritesMemory);
        var endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var submission = PrepareAndSubmit(scenario);

        var pending = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            submission,
            endpoint);
        Assert.False(pending.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, pending.Error);
        Assert.Equal(1, scenario.Provider.CompletionCalls);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).Error);

        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;
        var completed = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            submission,
            endpoint);
        Assert.True(completed.IsSuccess, completed.Message);
        Assert.True(completed.Value!.IsSatisfied);
        Assert.Equal(2, scenario.Provider.CompletionCalls);

        var delivered = scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint);
        Assert.True(delivered.IsSuccess, delivered.Message);
        Assert.Equal(endpoint, delivered.Value!.Endpoint);
        Assert.Equal(KernelEventClass.Completion, delivered.Value.EventClass);
        Assert.Equal(
            FormattableString.Invariant(
                $"platform/dma-completion-observed/v1/{submission.OperationId.Value}/{submission.Generation.Value}"),
            delivered.Value.SourceResourceId);
        var eventOverload = typeof(RuntimeKernel).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(method =>
                method.Name == nameof(RuntimeKernel.ObservePlatformDmaCompletion) &&
                method.GetParameters().Length == 3);
        var eventSignature = eventOverload.ToString() ?? eventOverload.Name;
        foreach (var forbidden in new[]
                 {
                     "PlatformProvider", "Neutral", "Physical", "BusAddress", "Iommu",
                     "PageTable", "Pte", "Descriptor", "Queue", "Vector", "Controller",
                     "Vmcs", "Vmx", "Lane", "Opcode",
                 })
        {
            Assert.DoesNotContain(forbidden, eventSignature, StringComparison.OrdinalIgnoreCase);
        }

        var earlyRevoke = scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant);
        Assert.False(earlyRevoke.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, earlyRevoke.Error);
        var earlyTransfer = scenario.Kernel.TransferRegion(
            scenario.Subject,
            scenario.Target,
            scenario.Buffer);
        Assert.False(earlyTransfer.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, earlyTransfer.Error);

        var replay = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            submission,
            endpoint);
        Assert.False(replay.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, replay.Error);
        Assert.Equal(2, scenario.Provider.CompletionCalls);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).Error);

        var visibility = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject,
            submission,
            completed.Value);
        Assert.True(visibility.IsSuccess, visibility.Message);
        Assert.Equal(1, scenario.Provider.AcquireCalls);

        AssertSuccess(scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant));
        AssertSuccess(scenario.Kernel.RevokePlatformRegionMapping(scenario.Subject, scenario.Mapping));
        AssertSuccess(scenario.Kernel.RevokePlatformDevice(scenario.Subject, scenario.Device));
        AssertSuccess(scenario.Kernel.RevokePlatformDomain(scenario.Subject, scenario.Binding));

        var moved = scenario.Kernel.TransferRegion(
            scenario.Subject,
            scenario.Target,
            scenario.Buffer);
        Assert.True(moved.IsSuccess, moved.Message);
    }

    [Fact]
    public void StaleForgedAndForeignEventDeliveryInputsFailBeforeProviderObservation()
    {
        var scenario = CreateScenario(1811, 1920, PlatformDmaDirection.DeviceReadsMemory);
        var endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var foreignEndpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Target).Value!;
        var closedEndpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        AssertSuccess(scenario.Kernel.CloseKernelEventEndpoint(scenario.Subject, closedEndpoint));
        var submission = PrepareAndSubmit(scenario);

        var staleSubject = scenario.Subject with { Generation = scenario.Subject.Generation + 1 };
        Assert.Equal(
            KernelError.StaleHandle,
            scenario.Kernel.ObservePlatformDmaCompletion(
                staleSubject,
                submission,
                endpoint).Error);
        Assert.Equal(
            KernelError.WrongEndpointOwner,
            scenario.Kernel.ObservePlatformDmaCompletion(
                scenario.Subject,
                submission,
                foreignEndpoint).Error);
        Assert.Equal(
            KernelError.StaleGeneration,
            scenario.Kernel.ObservePlatformDmaCompletion(
                scenario.Subject,
                submission,
                endpoint with
                {
                    Generation = new KernelEventEndpointGeneration(endpoint.Generation.Value + 1),
                }).Error);
        Assert.Equal(
            KernelError.EndpointNotFound,
            scenario.Kernel.ObservePlatformDmaCompletion(
                scenario.Subject,
                submission,
                closedEndpoint).Error);

        Assert.Equal(
            KernelError.StaleGeneration,
            scenario.Kernel.ObservePlatformDmaCompletion(
                scenario.Subject,
                submission with
                {
                    Generation = new PlatformDmaOperationGeneration(submission.Generation.Value + 1),
                },
                endpoint).Error);
        Assert.Equal(
            KernelError.PlatformDenied,
            scenario.Kernel.ObservePlatformDmaCompletion(
                scenario.Subject,
                submission with
                {
                    OperationId = new PlatformDmaOperationId(submission.OperationId.Value + 1),
                },
                endpoint).Error);
        Assert.Equal(
            KernelError.StaleGeneration,
            scenario.Kernel.ObservePlatformDmaCompletion(
                scenario.Subject,
                submission with
                {
                    GrantGeneration = new PlatformDmaGrantGeneration(
                        submission.GrantGeneration.Value + 1),
                },
                endpoint).Error);
        Assert.Equal(
            KernelError.PlatformDenied,
            scenario.Kernel.ObservePlatformDmaCompletion(
                scenario.Subject,
                submission with
                {
                    PreparedCycle = new PlatformDmaVisibilityCycle(submission.PreparedCycle.Value + 1),
                },
                endpoint).Error);
        Assert.Equal(0, scenario.Provider.CompletionCalls);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).Error);
    }

    [Fact]
    public void BusyEndpointBackpressuresBeforeCompletionObservationAndRetryDeliversOnce()
    {
        var scenario = CreateScenario(1812, 1930, PlatformDmaDirection.DeviceReadsMemory);
        var second = CreateAdditionalDmaGrant(scenario);
        var endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var firstSubmission = PrepareAndSubmit(scenario);
        var secondSubmission = PrepareAndSubmit(scenario, second.Grant);
        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;

        var first = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            firstSubmission,
            endpoint);
        Assert.True(first.IsSuccess, first.Message);
        Assert.Equal(1, scenario.Provider.CompletionCalls);

        var blocked = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            secondSubmission,
            endpoint);
        Assert.False(blocked.IsSuccess);
        Assert.Equal(KernelError.CapacityExhausted, blocked.Error);
        Assert.Equal(1, scenario.Provider.CompletionCalls);

        var firstEvent = scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint);
        Assert.True(firstEvent.IsSuccess, firstEvent.Message);
        var retried = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            secondSubmission,
            endpoint);
        Assert.True(retried.IsSuccess, retried.Message);
        Assert.Equal(2, scenario.Provider.CompletionCalls);

        var secondEvent = scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint);
        Assert.True(secondEvent.IsSuccess, secondEvent.Message);
        Assert.NotEqual(firstEvent.Value!.Sequence, secondEvent.Value!.Sequence);
        Assert.NotEqual(firstEvent.Value.SourceResourceId, secondEvent.Value.SourceResourceId);

        var replay = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            secondSubmission,
            endpoint);
        Assert.False(replay.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, replay.Error);
        Assert.Equal(2, scenario.Provider.CompletionCalls);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).Error);
    }

    [Fact]
    public void ProviderFailureRollsBackReservationWithoutPrematureEventPublication()
    {
        var scenario = CreateScenario(1813, 1940, PlatformDmaDirection.DeviceReadsMemory);
        var endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var submission = PrepareAndSubmit(scenario);
        scenario.Provider.CompletionFailure = PlatformAuthorityStatus.Denied;

        var denied = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            submission,
            endpoint);
        Assert.False(denied.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, denied.Error);
        Assert.Equal(1, scenario.Provider.CompletionCalls);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).Error);

        scenario.Provider.CompletionFailure = null;
        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;
        var retry = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            submission,
            endpoint);
        Assert.True(retry.IsSuccess, retry.Message);
        Assert.Equal(2, scenario.Provider.CompletionCalls);
        Assert.True(scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentCompletionObserversCallProviderAndPublishOnlyOnce()
    {
        var scenario = CreateScenario(1816, 1970, PlatformDmaDirection.DeviceReadsMemory);
        var firstEndpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var secondEndpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var submission = PrepareAndSubmit(scenario);
        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scenario.Provider.CompletionEntered = entered;
        scenario.Provider.CompletionRelease = release;
        var firstWait = scenario.Kernel.WaitForKernelEventAsync(
            scenario.Subject,
            firstEndpoint).AsTask();

        var firstTask = Task.Run(() => scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            submission,
            firstEndpoint));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var concurrent = scenario.Kernel.ObservePlatformDmaCompletion(
                scenario.Subject,
                submission,
                secondEndpoint);
            Assert.False(concurrent.IsSuccess);
            Assert.Equal(KernelError.PlatformBindingDraining, concurrent.Error);
            Assert.Equal(1, scenario.Provider.CompletionCalls);
            Assert.Equal(
                KernelError.ResponseNotAvailable,
                scenario.Kernel.ConsumeKernelEvent(scenario.Subject, firstEndpoint).Error);
            Assert.Equal(
                KernelError.ResponseNotAvailable,
                scenario.Kernel.ConsumeKernelEvent(scenario.Subject, secondEndpoint).Error);
            Assert.Equal(
                KernelError.PlatformBindingDraining,
                scenario.Kernel.CloseKernelEventEndpoint(
                    scenario.Subject,
                    firstEndpoint).Error);
            Assert.False(firstWait.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        var first = await firstTask;
        Assert.True(first.IsSuccess, first.Message);
        Assert.Equal(1, scenario.Provider.CompletionCalls);
        var firstEvent = await firstWait;
        Assert.True(firstEvent.IsSuccess, firstEvent.Message);
        Assert.Equal(KernelEventClass.Completion, firstEvent.Value!.EventClass);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(scenario.Subject, firstEndpoint).Error);
        AssertSuccess(scenario.Kernel.CloseKernelEventEndpoint(
            scenario.Subject,
            firstEndpoint));
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(scenario.Subject, secondEndpoint).Error);

        var replay = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            submission,
            secondEndpoint);
        Assert.False(replay.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, replay.Error);
        Assert.Equal(1, scenario.Provider.CompletionCalls);
    }

    [Fact]
    public async Task WaitCancellationLeavesDmaAndEndpointAuthorityIntact()
    {
        var scenario = CreateScenario(1817, 1980, PlatformDmaDirection.DeviceWritesMemory);
        var endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var submission = PrepareAndSubmit(scenario);
        using var cancellation = new CancellationTokenSource();
        var wait = scenario.Kernel.WaitForKernelEventAsync(
            scenario.Subject,
            endpoint,
            cancellation.Token).AsTask();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(0, scenario.Provider.CompletionCalls);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(
            KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(
            KernelError.PlatformBindingActive,
            scenario.Kernel.TransferRegion(
                scenario.Subject,
                scenario.Target,
                scenario.Buffer).Error);

        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;
        var completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            submission,
            endpoint);
        Assert.True(completion.IsSuccess, completion.Message);
        Assert.Equal(1, scenario.Provider.CompletionCalls);

        var delivered = await scenario.Kernel.WaitForKernelEventAsync(
            scenario.Subject,
            endpoint);
        Assert.True(delivered.IsSuccess, delivered.Message);
        Assert.Equal(KernelEventClass.Completion, delivered.Value!.EventClass);
        Assert.Equal(endpoint, delivered.Value.Endpoint);

        // Notification delivery still cannot release a write-capable DMA grant
        // or return the buffer before exact post-completion visibility.
        Assert.Equal(
            KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(
            KernelError.PlatformBindingActive,
            scenario.Kernel.TransferRegion(
                scenario.Subject,
                scenario.Target,
                scenario.Buffer).Error);
    }

    [Fact]
    public void EndpointCancellationDoesNotCancelDmaDrainOrPermitEarlyReclaim()
    {
        var scenario = CreateScenario(1814, 1950, PlatformDmaDirection.DeviceWritesMemory);
        var cancelledEndpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var exitEndpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var submission = PrepareAndSubmit(scenario);

        AssertSuccess(scenario.Kernel.CloseKernelEventEndpoint(
            scenario.Subject,
            cancelledEndpoint));
        Assert.Equal(
            KernelError.EndpointNotFound,
            scenario.Kernel.ObservePlatformDmaCompletion(
                scenario.Subject,
                submission,
                cancelledEndpoint).Error);
        Assert.Equal(0, scenario.Provider.CompletionCalls);
        Assert.Equal(
            KernelError.PlatformBindingDraining,
            scenario.Kernel.RevokePlatformDma(scenario.Subject, scenario.Grant).Error);
        Assert.Equal(
            KernelError.PlatformBindingActive,
            scenario.Kernel.TransferRegion(
                scenario.Subject,
                scenario.Target,
                scenario.Buffer).Error);

        var terminate = scenario.Kernel.TerminateProcess(scenario.Subject);
        Assert.False(terminate.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, terminate.Error);
        Assert.Equal(
            KernelError.InvalidTransition,
            scenario.Kernel.ObservePlatformDmaCompletion(
                scenario.Subject,
                submission,
                exitEndpoint).Error);
        Assert.Equal(0, scenario.Provider.CompletionCalls);
        Assert.Equal(0, scenario.Provider.DmaRevokeCalls);
        Assert.Equal(0, scenario.Provider.MappingRevokeCalls);
        Assert.Equal(0, scenario.Provider.DeviceRevokeCalls);

        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Completed;
        var completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            submission);
        Assert.True(completion.IsSuccess, completion.Message);
        var visibility = scenario.Kernel.FinalizePlatformDmaPostCompletionVisibility(
            scenario.Subject,
            submission,
            completion.Value!);
        Assert.True(visibility.IsSuccess, visibility.Message);

        var progress = scenario.Kernel.ObserveProcessTeardown(scenario.Subject);
        Assert.True(progress.IsSuccess, progress.Message);
        Assert.True(progress.Value!.LocalReclaimCompleted);
        Assert.True(progress.Value.PlatformDomainClosed);
        var reclaimed = scenario.Kernel.Regions.Snapshot()
            .Single(region => region.Handle.RegionId == scenario.Buffer.Handle.RegionId);
        Assert.Equal(RegionState.Released, reclaimed.State);

        var (_, recycled) = TestFixtures.Create(
            scenario.Kernel,
            scenario.Subject.ProcessId.Value,
            scenario.Binding.Subject.DomainId.Value + 10_000,
            generation: 2);
        var callsBeforeStaleDelivery = scenario.Provider.CompletionCalls;
        Assert.Equal(
            KernelError.StaleHandle,
            scenario.Kernel.ObservePlatformDmaCompletion(
                scenario.Subject,
                submission,
                cancelledEndpoint).Error);
        Assert.Equal(
            KernelError.WrongEndpointOwner,
            scenario.Kernel.ObservePlatformDmaCompletion(
                recycled,
                submission,
                cancelledEndpoint).Error);
        Assert.Equal(callsBeforeStaleDelivery, scenario.Provider.CompletionCalls);
    }

    [Fact]
    public void FaultedProviderCompletionStatePublishesNoEventAndPinsReclaim()
    {
        var scenario = CreateScenario(1815, 1960, PlatformDmaDirection.DeviceWritesMemory);
        var endpoint = scenario.Kernel.CreateKernelEventEndpoint(scenario.Subject).Value!;
        var submission = PrepareAndSubmit(scenario);
        scenario.Provider.CompletionState = PlatformProviderDmaCompletionState.Faulted;

        var completion = scenario.Kernel.ObservePlatformDmaCompletion(
            scenario.Subject,
            submission,
            endpoint);
        Assert.False(completion.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, completion.Error);
        Assert.Equal(1, scenario.Provider.CompletionCalls);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            scenario.Kernel.ConsumeKernelEvent(scenario.Subject, endpoint).Error);
        Assert.Equal(
            KernelError.PlatformFaulted,
            scenario.Kernel.TerminateProcess(scenario.Subject).Error);
        var region = scenario.Kernel.Regions.Snapshot()
            .Single(item => item.Handle.RegionId == scenario.Buffer.Handle.RegionId);
        Assert.Equal(RegionState.Owned, region.State);
    }

    private static void AssertSuccess(KernelResult result) =>
        Assert.True(result.IsSuccess, $"{result.Error}: {result.Message}");

    private static PlatformDmaSubmission PrepareAndSubmit(Scenario scenario)
        => PrepareAndSubmit(scenario, scenario.Grant);

    private static PlatformDmaSubmission PrepareAndSubmit(
        Scenario scenario,
        PlatformDmaGrant grant)
    {
        var prepare = scenario.Kernel.PreparePlatformDmaForDevice(scenario.Subject, grant);
        Assert.True(prepare.IsSuccess, prepare.Message);
        var submit = scenario.Kernel.SubmitPlatformDma(
            scenario.Subject,
            grant,
            prepare.Value!);
        Assert.True(submit.IsSuccess, submit.Message);
        return submit.Value!;
    }

    private static (
        OwnedBuffer<byte> Buffer,
        PlatformOwnedRegionSliceMapping Mapping,
        PlatformDmaGrant Grant) CreateAdditionalDmaGrant(Scenario scenario)
    {
        var access = scenario.Grant.Direction switch
        {
            PlatformDmaDirection.DeviceReadsMemory => PlatformMemoryAccess.Read,
            PlatformDmaDirection.DeviceWritesMemory => PlatformMemoryAccess.Write,
            PlatformDmaDirection.Bidirectional => PlatformMemoryAccess.Read | PlatformMemoryAccess.Write,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var rights = CapabilityRights.Map;
        if ((access & PlatformMemoryAccess.Read) != 0) rights |= CapabilityRights.Read;
        if ((access & PlatformMemoryAccess.Write) != 0) rights |= CapabilityRights.Write;

        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Subject, 512).Value!;
        var capability = Mint(
            scenario.Kernel,
            scenario.Subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            rights);
        var mapping = scenario.Kernel.MapPlatformOwnedRegionSlice(
            scenario.Subject,
            scenario.Binding,
            capability,
            buffer.Handle,
            96,
            192,
            access).Value!;
        var grant = scenario.Kernel.BindPlatformDma(
            scenario.Subject,
            scenario.Device,
            mapping,
            24,
            64,
            scenario.Grant.Direction).Value!;
        return (buffer, mapping, grant);
    }

    private static Scenario CreateScenario(
        ulong processId,
        ulong domainId,
        PlatformDmaDirection direction)
    {
        var provider = new LifecycleProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, processId, domainId);
        var (_, target) = TestFixtures.Create(kernel, processId + 1000, domainId + 1000);
        var binding = kernel.BindPlatformDomain(subject).Value!;

        var deviceRights = PlatformDeviceRights.Configure;
        var mappingAccess = PlatformMemoryAccess.None;
        switch (direction)
        {
            case PlatformDmaDirection.DeviceReadsMemory:
                deviceRights |= PlatformDeviceRights.Read;
                mappingAccess |= PlatformMemoryAccess.Read;
                break;
            case PlatformDmaDirection.DeviceWritesMemory:
                deviceRights |= PlatformDeviceRights.Write;
                mappingAccess |= PlatformMemoryAccess.Write;
                break;
            case PlatformDmaDirection.Bidirectional:
                deviceRights |= PlatformDeviceRights.Read | PlatformDeviceRights.Write;
                mappingAccess |= PlatformMemoryAccess.Read | PlatformMemoryAccess.Write;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(direction));
        }

        var deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            $"device/dma-lifecycle-{processId}",
            ToCapabilityRights(deviceRights));
        var device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            deviceRights).Value!;

        var buffer = kernel.AllocateBuffer<byte>(subject, 512).Value!;
        var memoryRights = CapabilityRights.Map;
        if ((mappingAccess & PlatformMemoryAccess.Read) != 0) memoryRights |= CapabilityRights.Read;
        if ((mappingAccess & PlatformMemoryAccess.Write) != 0) memoryRights |= CapabilityRights.Write;
        var memoryCapability = Mint(
            kernel,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),
            memoryRights);
        var mapping = kernel.MapPlatformOwnedRegionSlice(
            subject,
            binding,
            memoryCapability,
            buffer.Handle,
            64,
            256,
            mappingAccess).Value!;
        var grant = kernel.BindPlatformDma(
            subject,
            device,
            mapping,
            32,
            64,
            direction).Value!;

        return new Scenario(
            kernel,
            provider,
            subject,
            target,
            binding,
            deviceCapability,
            device,
            buffer,
            mapping,
            grant);
    }

    private static CapabilityId Mint(
        RuntimeKernel kernel,
        ProcessHandle subject,
        ResourceKind kind,
        string resourceId,
        CapabilityRights rights)
    {
        var process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        var minted = kernel.MintCapability(
            process.Value!.DomainId,
            subject,
            kind,
            resourceId,
            rights);
        Assert.True(minted.IsSuccess, minted.Message);
        return minted.Value!.CapabilityId;
    }

    private static CapabilityRights ToCapabilityRights(PlatformDeviceRights rights)
    {
        var result = CapabilityRights.None;
        if ((rights & PlatformDeviceRights.Read) != 0) result |= CapabilityRights.Read;
        if ((rights & PlatformDeviceRights.Write) != 0) result |= CapabilityRights.Write;
        if ((rights & PlatformDeviceRights.Configure) != 0) result |= CapabilityRights.Configure;
        return result;
    }

    private sealed record Scenario(
        RuntimeKernel Kernel,
        LifecycleProvider Provider,
        ProcessHandle Subject,
        ProcessHandle Target,
        PlatformDomainBinding Binding,
        CapabilityId DeviceCapability,
        PlatformDeviceLease Device,
        OwnedBuffer<byte> Buffer,
        PlatformOwnedRegionSliceMapping Mapping,
        PlatformDmaGrant Grant);

    public enum AcquireMutation
    {
        None = 0,
        WrongCycle,
        WrongGrantGeneration,
    }

    private sealed class LifecycleProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformDeviceLeaseProvider,
        IPlatformOwnedRegionMappingProvider,
        IPlatformRegionRevocationProvider,
        IPlatformDmaGrantProvider,
        IPlatformDmaVisibilityProvider,
        IPlatformDmaSubmissionProvider,
        IPlatformDmaCompletionProvider
    {
        private readonly Dictionary<PlatformProviderDeviceLeaseId, PlatformProviderDeviceLease> _devices = [];
        private readonly Dictionary<PlatformProviderRegionMappingId, PlatformProviderOwnedRegionMapping> _mappings = [];
        private readonly Dictionary<PlatformProviderDmaGrantId, PlatformProviderDmaGrant> _grants = [];
        private readonly Dictionary<PlatformProviderDmaGrantId, PlatformProviderDmaVisibilityCycle> _cycles = [];
        private readonly HashSet<PlatformProviderDmaGrantId> _acquired = [];
        private readonly Dictionary<PlatformProviderDmaGrantId, PlatformProviderDmaSubmission> _submissions = [];
        private readonly HashSet<PlatformProviderDmaGrantId> _completed = [];
        private readonly Dictionary<PlatformOperationId, PlatformProviderRegionMappingLease> _mappingClosures = [];
        private PlatformProviderDomainLease? _domain;
        private ulong _nextDevice = 1;
        private ulong _nextMapping = 1;
        private ulong _nextGrant = 1;
        private ulong _nextCycle = 1;
        private ulong _nextSubmission = 1;
        private ulong _nextOperation = 1;

        public int CompletionCalls { get; private set; }
        public int AcquireCalls { get; private set; }
        public int DmaRevokeCalls { get; private set; }
        public int MappingRevokeCalls { get; private set; }
        public int DeviceRevokeCalls { get; private set; }
        public PlatformProviderDmaCompletionState CompletionState { get; set; } =
            PlatformProviderDmaCompletionState.Pending;
        public PlatformAuthorityStatus? CompletionFailure { get; set; }
        public ManualResetEventSlim? CompletionEntered { get; set; }
        public ManualResetEventSlim? CompletionRelease { get; set; }
        public AcquireMutation AcquireMutation { get; set; }

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("dma-lifecycle-model"),
            1,
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
                PlatformFeatureFamily.IoDomainBinding,
                PlatformDeviceLeaseContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.DmaMapping,
                PlatformDmaLifecycleContract.ContractVersion,
                PlatformFeatureAvailability.RuntimeAdmission),
        });

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(
            PlatformDomainIdentity subject)
        {
            var lease = new PlatformProviderDomainLease(
                new PlatformProviderDomainLeaseId(1),
                new PlatformProviderLeaseGeneration(1),
                subject);
            _domain = lease;
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            if (_devices.Count != 0 || _mappings.Count != 0 || _grants.Count != 0)
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Lower platform authority remains live.");
            }

            _domain = null;
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(
            PlatformProviderDomainLease domainLease,
            PlatformDeviceIdentity device,
            PlatformDeviceRights rights)
        {
            var lease = new PlatformProviderDeviceLease(
                new PlatformProviderDeviceLeaseId(_nextDevice++),
                new PlatformProviderLeaseGeneration(1),
                domainLease,
                device,
                rights);
            _devices.Add(lease.LeaseId, lease);
            return PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease)
        {
            DeviceRevokeCalls++;
            if (_grants.Values.Any(grant => grant.DeviceLease == lease))
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    "DMA grant remains live.");
            }

            _devices.Remove(lease.LeaseId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access)
        {
            var mapped = MapOwnedRegionSlice(
                domainLease,
                new PlatformRegionSlice(region, 0, region.ByteLength, access));
            return mapped.IsSuccess
                ? PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(mapped.Value!.Lease)
                : PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                    mapped.Status,
                    mapped.Message!);
        }

        public PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> MapOwnedRegionSlice(
            PlatformProviderDomainLease domainLease,
            PlatformRegionSlice slice)
        {
            if (_domain != domainLease)
            {
                return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Fail(
                    PlatformAuthorityStatus.WrongDomain,
                    "Wrong domain.");
            }

            var lease = new PlatformProviderRegionMappingLease(
                new PlatformProviderRegionMappingId(_nextMapping++),
                new PlatformProviderLeaseGeneration(1),
                domainLease,
                slice.Region,
                slice.Access);
            var mapping = new PlatformProviderOwnedRegionMapping(lease, slice);
            _mappings.Add(lease.MappingId, mapping);
            return PlatformAuthorityResult<PlatformProviderOwnedRegionMapping>.Ok(mapping);
        }

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy)
        {
            if (_grants.Values.Any(grant => grant.MappingLease == mapping))
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Denied,
                    "DMA grant remains live.");
            }

            _mappings.Remove(mapping.MappingId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy)
        {
            MappingRevokeCalls++;
            if (!_mappings.TryGetValue(mapping.MappingId, out var exact) || exact.Lease != mapping)
            {
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                    PlatformAuthorityStatus.Stale,
                    "Unknown exact provider mapping.");
            }

            if (_grants.Values.Any(grant => grant.MappingLease == mapping))
            {
                return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "DMA grant remains live.");
            }

            var operation = new PlatformOperationIdentity(
                new PlatformOperationId(_nextOperation++),
                new PlatformOperationGeneration(1),
                mapping.DomainLease);
            _mappingClosures.Add(operation.OperationId, mapping);
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(
                new PlatformRegionRevocationTicket(
                    mapping.MappingId,
                    mapping.Generation,
                    operation));
        }

        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(
            PlatformOperationIdentity operation)
        {
            if (!_mappingClosures.TryGetValue(operation.OperationId, out var mapping))
            {
                return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                    PlatformAuthorityStatus.Stale,
                    "Unknown provider mapping closure operation.");
            }

            if (operation.Generation.Value != 1 || operation.DomainLease != mapping.DomainLease)
            {
                return PlatformAuthorityResult<PlatformCompletionReceipt>.Fail(
                    PlatformAuthorityStatus.Stale,
                    "Stale provider mapping closure identity.");
            }

            _mappingClosures.Remove(operation.OperationId);
            _mappings.Remove(mapping.MappingId);
            return PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(
                new PlatformCompletionReceipt(
                    operation.OperationId,
                    operation.Generation,
                    operation.DomainLease,
                    PlatformCompletionState.Closed));
        }

        public PlatformAuthorityResult<PlatformProviderDmaGrant> BindDmaGrant(
            PlatformDmaGrantRequest request)
        {
            var validation = PlatformDmaGrantContract.ValidateRequest(request);
            if (!validation.IsSuccess)
            {
                return PlatformAuthorityResult<PlatformProviderDmaGrant>.Fail(
                    validation.Status,
                    validation.Message!);
            }

            var grant = new PlatformProviderDmaGrant(
                new PlatformProviderDmaGrantId(_nextGrant++),
                new PlatformProviderLeaseGeneration(1),
                request.DeviceLease,
                request.MappingLease,
                request.Range,
                request.Direction);
            _grants.Add(grant.GrantId, grant);
            return PlatformAuthorityResult<PlatformProviderDmaGrant>.Ok(grant);
        }

        public PlatformAuthorityResult RevokeDmaGrant(PlatformProviderDmaGrant grant)
        {
            DmaRevokeCalls++;
            if (!_grants.TryGetValue(grant.GrantId, out var exact) || exact != grant)
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Stale,
                    "Unknown DMA grant.");
            }

            if (_submissions.ContainsKey(grant.GrantId))
            {
                if (!_completed.Contains(grant.GrantId))
                {
                    return PlatformAuthorityResult.Fail(
                        PlatformAuthorityStatus.Denied,
                        "DMA completion is not proven.");
                }

                if ((grant.Direction is PlatformDmaDirection.DeviceWritesMemory or PlatformDmaDirection.Bidirectional) &&
                    !_acquired.Contains(grant.GrantId))
                {
                    return PlatformAuthorityResult.Fail(
                        PlatformAuthorityStatus.Denied,
                        "Post-completion acquire is still required.");
                }

                _submissions.Remove(grant.GrantId);
            }

            _completed.Remove(grant.GrantId);
            _acquired.Remove(grant.GrantId);
            _cycles.Remove(grant.GrantId);
            _grants.Remove(grant.GrantId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence> PrepareDmaGrantVisibility(
            PlatformProviderDmaGrant grant)
        {
            if (!_grants.TryGetValue(grant.GrantId, out var exact) || exact != grant ||
                _submissions.ContainsKey(grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Grant is not eligible for prepare.");
            }

            var cycle = new PlatformProviderDmaVisibilityCycle(_nextCycle++);
            _cycles[grant.GrantId] = cycle;
            _acquired.Remove(grant.GrantId);
            return PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>.Ok(
                new PlatformProviderDmaPrepareEvidence(
                    grant.GrantId,
                    grant.Generation,
                    cycle,
                    grant.Direction,
                    PlatformMemoryVisibilityRequirement.PublicationFence,
                    PlatformMemoryVisibilityOutcome.PublicationFenceSatisfied));
        }

        public PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence> AcquireDmaGrantVisibility(
            PlatformProviderDmaGrant grant)
        {
            AcquireCalls++;
            if (!_grants.TryGetValue(grant.GrantId, out var exact) || exact != grant ||
                !_cycles.TryGetValue(grant.GrantId, out var cycle) ||
                _acquired.Contains(grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "No exact DMA cycle is available for acquire.");
            }

            if (_submissions.ContainsKey(grant.GrantId) && !_completed.Contains(grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Submitted DMA has not completed.");
            }

            _acquired.Add(grant.GrantId);
            var evidence = new PlatformProviderDmaAcquireEvidence(
                grant.GrantId,
                grant.Generation,
                cycle,
                grant.Direction,
                PlatformMemoryAcquireRequirement.AcquisitionFence,
                PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied);
            return AcquireMutation switch
            {
                AcquireMutation.WrongCycle => PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Ok(
                    evidence with
                    {
                        Cycle = new PlatformProviderDmaVisibilityCycle(evidence.Cycle.Value + 1),
                    }),
                AcquireMutation.WrongGrantGeneration => PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Ok(
                    evidence with
                    {
                        GrantGeneration = new PlatformProviderLeaseGeneration(
                            evidence.GrantGeneration.Value + 1),
                    }),
                _ => PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>.Ok(evidence),
            };
        }

        public PlatformAuthorityResult<PlatformProviderDmaSubmission> SubmitDma(
            PlatformProviderDmaSubmitRequest request)
        {
            var validation = PlatformDmaSubmissionContract.ValidateRequest(request);
            if (!validation.IsSuccess)
            {
                return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Fail(
                    validation.Status,
                    validation.Message!);
            }

            if (!_grants.TryGetValue(request.Grant.GrantId, out var exact) ||
                exact != request.Grant ||
                !_cycles.TryGetValue(request.Grant.GrantId, out var cycle) ||
                cycle != request.PreparedCycle ||
                _acquired.Contains(request.Grant.GrantId) ||
                _submissions.ContainsKey(request.Grant.GrantId))
            {
                return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Fail(
                    PlatformAuthorityStatus.Denied,
                    "Submission does not match the exact prepared grant cycle.");
            }

            var submission = new PlatformProviderDmaSubmission(
                new PlatformProviderDmaSubmissionId(_nextSubmission++),
                new PlatformProviderDmaSubmissionGeneration(1),
                request.Grant.GrantId,
                request.Grant.Generation,
                request.PreparedCycle,
                request.Grant.Range,
                request.Grant.Direction);
            _submissions.Add(request.Grant.GrantId, submission);
            return PlatformAuthorityResult<PlatformProviderDmaSubmission>.Ok(submission);
        }

        public PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence> ObserveDmaCompletion(
            PlatformProviderDmaSubmission submission)
        {
            CompletionCalls++;
            CompletionEntered?.Set();
            CompletionRelease?.Wait(TimeSpan.FromSeconds(10));
            if (!_submissions.TryGetValue(submission.GrantId, out var exact) || exact != submission)
            {
                return PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence>.Fail(
                    PlatformAuthorityStatus.Stale,
                    "Unknown exact provider DMA submission.");
            }

            if (CompletionFailure is { } failure)
            {
                return PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence>.Fail(
                    failure,
                    "Injected completion failure.");
            }

            if (CompletionState == PlatformProviderDmaCompletionState.Completed)
                _completed.Add(submission.GrantId);

            return PlatformAuthorityResult<PlatformProviderDmaCompletionEvidence>.Ok(
                new PlatformProviderDmaCompletionEvidence(
                    submission.SubmissionId,
                    submission.Generation,
                    submission.GrantId,
                    submission.GrantGeneration,
                    submission.PreparedCycle,
                    submission.Range,
                    submission.Direction,
                    CompletionState));
        }
    }
}
