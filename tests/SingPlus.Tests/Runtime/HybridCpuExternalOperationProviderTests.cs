using Hc = HybridCPU.ExternalRuntime.Contracts;
using HybridCPU.ExternalRuntime;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class HybridCpuExternalOperationProviderTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ReconciliationReleaseUsesFreshSourceAndPreservesOriginalUntilActualCommit(int mode)
    {
        SubmissionClock clock = new SubmissionClock();
        Scenario s = CreateScenario(clock);
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(s.Provider.Admit(Semantic()).Receipt).Request;
        s.Provider.Submit(request);
        ExternalOperationSnapshot original = Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot());
        Hc.ExternalGenerationSet current = Generations(Guid.NewGuid());
        Hc.ExternalGenerationSet changed = Generations(Guid.NewGuid());
        s.Provider.Reconfigure(current);
        s.Provider.BeforeReconciliationReleaseQualificationHook = () =>
        {
            s.Provider.BeforeReconciliationReleaseQualificationHook = null;
            Task worker = Task.Run(() =>
            {
                s.Provider.Reconfigure(mode == 2 ? current : changed);
                Assert.Equal(Hc.ExternalOperationProviderPollStatus.Pending,
                    s.Provider.Admit(Semantic()).Status);
                if (mode == 1) s.Provider.Reconfigure(current);
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Reconciliation observation must run outside owner/provider gates.");
            worker.GetAwaiter().GetResult();
        };
        if (mode == 3)
        {
            s.Provider.BeforeReconciliationReleaseQualificationHook = null;
            Assert.True(s.Kernel.Traces.Start(s.Process, 16, TraceOverflowPolicy.DropWithMarker, TraceVisibilityClass.Self).IsSuccess);
            clock.Next = () =>
            {
                Task worker = Task.Run(() =>
                {
                    Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(original.Operation).Value!.State);
                    s.Provider.Reconfigure(changed);
                });
                Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Release trace must run after pure commit outside locks.");
                worker.GetAwaiter().GetResult();
            };
        }
        KernelResult released = s.Provider.ReconcileReconfiguredOperation(request, current, true);
        Assert.Equal(mode >= 2, released.IsSuccess);
        Assert.Equal(original.Binding, s.Kernel.ExternalOperations.Query(original.Operation).Value!.Binding);
        if (mode < 2)
        {
            Assert.Equal(KernelError.StaleGeneration, released.Error);
            Assert.False(s.Kernel.ReleaseRegion(s.Process, s.Input).IsSuccess);
            Hc.ExternalGenerationSet actual = mode == 1 ? current : changed;
            Assert.True(s.Provider.ReconcileReconfiguredOperation(request, actual, true).IsSuccess);
        }
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(original.Operation).Value!.State);
        Assert.True(s.Kernel.ReleaseRegion(s.Process, s.Input).IsSuccess);
        Assert.True(s.Kernel.ReleaseRegion(s.Process, s.Output).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void OrdinaryReleaseRevalidatesProviderAfterLossTraceAndRetainsPostCommitFact(int mode)
    {
        SubmissionClock clock = new SubmissionClock();
        Scenario s = CreateScenario(clock);
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(s.Provider.Admit(Semantic()).Receipt).Request;
        s.Provider.Submit(request);
        ExternalOperationSnapshot original = Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot());
        Hc.ExternalGenerationSet changed = Generations(Guid.NewGuid());
        if (mode == 3) Assert.True(s.Kernel.RecordExternalOperationProviderLoss(s.Process, original.Operation).IsSuccess);
        Assert.True(s.Kernel.Traces.Start(s.Process, 16, TraceOverflowPolicy.DropWithMarker, TraceVisibilityClass.Self).IsSuccess);
        clock.Next = () =>
        {
            Task worker = Task.Run(() =>
            {
                Assert.Equal(mode == 3 ? ExternalOperationState.Released : ExternalOperationState.Submitted,
                    s.Kernel.ExternalOperations.Query(original.Operation).Value!.State);
                s.Provider.Reconfigure(mode == 2 ? request.Generations : changed);
                if (mode == 1) s.Provider.Reconfigure(request.Generations);
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Loss/release trace must run outside provider and owner commit locks.");
            worker.GetAwaiter().GetResult();
        };

        KernelResult released = s.Provider.Release(request, true, providerUnavailable: mode != 3);

        Assert.Equal(mode >= 2, released.IsSuccess);
        ExternalOperationSnapshot actual = s.Kernel.ExternalOperations.Query(original.Operation).Value!;
        Assert.Equal(original.Binding, actual.Binding);
        if (mode < 2)
        {
            Assert.Equal(KernelError.StaleGeneration, released.Error);
            Assert.Equal(ExternalOperationState.Submitted, actual.State);
            Assert.False(s.Kernel.ReleaseRegion(s.Process, s.Input).IsSuccess);
            Assert.True(s.Provider.ReconcileReconfiguredOperation(request,
                mode == 1 ? request.Generations : changed, true).IsSuccess);
        }
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(original.Operation).Value!.State);
        Assert.True(s.Kernel.ReleaseRegion(s.Process, s.Input).IsSuccess);
        Assert.True(s.Kernel.ReleaseRegion(s.Process, s.Output).IsSuccess);
    }

    [Fact]
    public void FailedDuplicateSubmitNeverRewindsDeliveredProgressOrRepeatsPublication()
    {
        Scenario s = CreateScenario();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(s.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalRuntimeOutcome.Succeeded, s.Provider.Submit(request).Receipt!.Outcome);
        Assert.True(s.Provider.RecordDeviceCompletion(request).IsSuccess);
        void DeliverOnce(Hc.ExternalOperationStage expected)
        {
            Assert.Equal(expected, s.Provider.Poll(request).Receipt!.Stage);
            Assert.Equal(Hc.ExternalRuntimeOutcome.Faulted, s.Provider.Submit(request).Receipt!.Outcome);
            Assert.Equal(Hc.ExternalOperationProviderPollStatus.Pending, s.Provider.Poll(request).Status);
        }
        DeliverOnce(Hc.ExternalOperationStage.DeviceComplete);
        Assert.True(s.Provider.RecordVisibility(request).IsSuccess);
        DeliverOnce(Hc.ExternalOperationStage.Visible);
        int effects = 0;
        Assert.True(s.Provider.Publish(request, () => effects++).IsSuccess);
        DeliverOnce(Hc.ExternalOperationStage.Published);
        Assert.False(s.Provider.Publish(request, () => effects++).IsSuccess);
        Assert.Equal(1, effects);
        ExternalOperationSnapshot original = Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot());
        Assert.Single(original.Transitions, item => item.Event == "Submitted");
        Assert.True(s.Provider.Release(request, true).IsSuccess);
        Assert.Equal(Hc.ExternalOperationStage.Released, s.Provider.Poll(request).Receipt!.Stage);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale, s.Provider.Submit(request).Status);
        Assert.True(s.Kernel.ReleaseRegion(s.Process, s.Input).IsSuccess);
        Assert.True(s.Kernel.ReleaseRegion(s.Process, s.Output).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void PollFinalCommitRefusesSourceOrOwnerDriftAndPreservesCleanup(int fault)
    {
        Scenario s = CreateScenario();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(s.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalRuntimeOutcome.Succeeded, s.Provider.Submit(request).Receipt!.Outcome);
        Assert.True(s.Provider.RecordDeviceCompletion(request).IsSuccess);
        ExternalOperationSnapshot original = Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot());
        Hc.ExternalGenerationSet replacement = fault == 2 ? request.Generations : Generations(Guid.NewGuid());
        Hc.ExternalGenerationSet current = fault == 1 ? request.Generations : replacement;
        s.Provider.BeforePollPublicationQualificationHook = () =>
        {
            s.Provider.BeforePollPublicationQualificationHook = null;
            Task worker = Task.Run(() =>
            {
                Assert.Equal(Hc.ExternalOperationProviderPollStatus.Pending, s.Provider.Poll(request).Status);
                if (fault <= 2)
                {
                    s.Provider.Reconfigure(replacement);
                    if (fault == 1) s.Provider.Reconfigure(request.Generations);
                }
                else if (fault == 3)
                    Assert.False(s.Kernel.RecordExternalOperationVisibility(s.Process,
                        new(original.Binding!.Value, ExternalVisibilityRequirement.PublicationFence, false)).IsSuccess);
                else
                {
                    Assert.True(s.Kernel.CancelExternalOperation(s.Process, original.Operation, false).IsSuccess);
                    Assert.True(s.Kernel.ReleaseExternalOperation(s.Process, original.Operation, new(true, false)).IsSuccess);
                }
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Poll observation must run outside provider/owner locks.");
            worker.GetAwaiter().GetResult();
        };
        Hc.ExternalOperationProviderPollResult result = s.Provider.Poll(request);
        Assert.Equal(fault == 2, result.Receipt!.Outcome == Hc.ExternalRuntimeOutcome.Succeeded);
        if (fault <= 1)
        {
            Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale, result.Status);
            Assert.Equal(original.Binding, Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot()).Binding);
            Assert.False(s.Kernel.ReleaseRegion(s.Process, s.Input).IsSuccess);
            Assert.True(s.Provider.ReconcileReconfiguredOperation(request, current, true).IsSuccess);
        }
        else if (fault == 2)
        {
            Assert.Equal(Hc.ExternalOperationStage.DeviceComplete, result.Receipt.Stage);
            Assert.Equal(Hc.ExternalOperationProviderPollStatus.Pending, s.Provider.Poll(request).Status);
            Assert.True(s.Provider.RecordVisibility(request).IsSuccess);
            Assert.Equal(Hc.ExternalOperationStage.Visible, s.Provider.Poll(request).Receipt!.Stage);
            Assert.Equal(Hc.ExternalOperationCancellationOutcome.Ambiguous, s.Provider.RequestCancellation(request).Outcome);
            Assert.True(s.Provider.Release(request, true).IsSuccess);
        }
        else
        {
            Assert.Equal(Hc.ExternalRuntimeOutcome.Faulted, result.Receipt.Outcome);
            if (fault == 3)
            {
                Assert.False(s.Kernel.ReleaseRegion(s.Process, s.Input).IsSuccess);
                Assert.True(s.Provider.Release(request, true).IsSuccess);
            }
        }
        Assert.True(s.Kernel.ReleaseRegion(s.Process, s.Input).IsSuccess);
        Assert.True(s.Kernel.ReleaseRegion(s.Process, s.Output).IsSuccess);
    }

    [Fact]
    public void PollFaultThrowBalancesTransitionWithoutAdvancingProgress()
    {
        Scenario s = CreateScenario();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(s.Provider.Admit(Semantic()).Receipt).Request;
        s.Provider.Submit(request);
        Assert.True(s.Provider.RecordDeviceCompletion(request).IsSuccess);
        s.Provider.BeforePollPublicationQualificationHook = () =>
        {
            s.Provider.BeforePollPublicationQualificationHook = null;
            Assert.Equal(Hc.ExternalOperationProviderPollStatus.Pending, s.Provider.Poll(request).Status);
            throw new InvalidOperationException("poll fault");
        };
        Assert.Throws<InvalidOperationException>(() => s.Provider.Poll(request));
        Assert.Equal(Hc.ExternalOperationStage.DeviceComplete, s.Provider.Poll(request).Receipt!.Stage);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Pending, s.Provider.Poll(request).Status);
        Assert.Single(Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot()).Transitions, item => item.Event == "Submitted");
        s.Provider.RequestCancellation(request);
        Assert.True(s.Provider.Release(request, true).IsSuccess);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    public void DeferredProviderDriftRefusesUsableAdmissionOrSubmissionAndKeepsExactCleanup(int stage, int mode)
    {
        SubmissionClock clock = new SubmissionClock();
        Scenario s = CreateScenario(clock);
        Hc.ExternalOperationRequest? request = stage == 2
            ? Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(s.Provider.Admit(Semantic()).Receipt).Request : null;
        Hc.ExternalGenerationSet initial = Generations(Guid.Parse("5aa6b3e1-1865-4f5a-a498-e401616d8f20"));
        Hc.ExternalGenerationSet replacement = mode == 2 ? initial : Generations(Guid.NewGuid());
        Hc.ExternalGenerationSet current = mode == 1 ? initial : replacement;
        Assert.True(s.Kernel.Traces.Start(s.Process, 32, TraceOverflowPolicy.DropWithMarker, TraceVisibilityClass.Self).IsSuccess);
        int callbacks = 0;
        void Observe()
        {
            ExternalOperationSnapshot original = Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot());
            ExternalOperationState expected = stage == 0 ? ExternalOperationState.Prepared
                : stage == 1 ? ExternalOperationState.Admitted : ExternalOperationState.Submitted;
            if (original.State != expected) { clock.Next = Observe; return; }
            callbacks++;
            Task worker = Task.Run(() =>
            {
                s.Provider.Reconfigure(replacement);
                if (mode == 1) s.Provider.Reconfigure(initial);
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Generation callback must run outside provider/owner gates.");
            worker.GetAwaiter().GetResult();
        }
        clock.Next = Observe;
        Hc.ExternalOperationProviderPollResult result = stage == 2 ? s.Provider.Submit(request!) : s.Provider.Admit(Semantic());
        Assert.Equal(1, callbacks);
        ExternalOperationSnapshot original = Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot());
        if (mode != 2)
        {
            Assert.NotEqual(Hc.ExternalRuntimeOutcome.Succeeded, result.Receipt!.Outcome);
            if (stage == 0) Assert.Null(original.Admission);
            if (stage != 2)
            {
                Assert.Equal(ExternalOperationState.Released, original.State);
                Assert.True(s.Kernel.ReleaseRegion(s.Process, s.Input).IsSuccess);
            }
            else
            {
                Assert.Equal(ExternalOperationDisposition.ProviderLost, original.Disposition);
                Assert.NotNull(original.Binding);
                Assert.False(s.Kernel.ReleaseRegion(s.Process, s.Input).IsSuccess);
                Assert.True(s.Provider.ReconcileReconfiguredOperation(request!, current, true).IsSuccess);
                Assert.True(s.Kernel.ReleaseRegion(s.Process, s.Input).IsSuccess);
            }
        }
        else
        {
            Assert.Equal(Hc.ExternalRuntimeOutcome.Succeeded, result.Receipt!.Outcome);
            request ??= Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(result.Receipt).Request;
            if (stage == 2)
            {
                Assert.True(s.Provider.RecordDeviceCompletion(request, ExternalOperationCompletionDisposition.Cancelled).IsSuccess);
                Assert.True(s.Provider.Release(request, true).IsSuccess);
            }
            else Assert.Equal(Hc.ExternalOperationCancellationOutcome.ConfirmedBeforeSubmit,
                s.Provider.RequestCancellation(request).Outcome);
        }
        Assert.True(s.Kernel.ReleaseRegion(s.Process, s.Output).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedAdmissionWithSubmittedOriginalRetainsCleanupWithoutNewEffects(bool duringPreparation)
    {
        SubmissionClock clock = new SubmissionClock();
        Scenario scenario = CreateScenario(clock);
        Assert.True(scenario.Kernel.Traces.Start(scenario.Process, 32,
            TraceOverflowPolicy.DropWithMarker, TraceVisibilityClass.Self).IsSuccess);
        OperationBinding? binding = null;
        void Observe()
        {
            ExternalOperationSnapshot original = Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot());
            if (original.State != (duringPreparation ? ExternalOperationState.Prepared : ExternalOperationState.Admitted))
            {
                clock.Next = Observe;
                return;
            }
            Task worker = Task.Run(() =>
            {
                if (duringPreparation)
                    Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Process, original.Operation,
                        new(7, 11, 13, 17)).IsSuccess);
                KernelResult<OperationBinding> submitted = scenario.Kernel.RecordExternalOperationSubmission(scenario.Process,
                    original.Operation, new(7, 11, 13, 17));
                Assert.True(submitted.IsSuccess, submitted.Message);
                binding = submitted.Value;
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Admission trace must run outside owner/provider gates.");
            worker.GetAwaiter().GetResult();
        }
        clock.Next = Observe;
        Hc.ExternalOperationProviderPollResult failed = scenario.Provider.Admit(Semantic());
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Faulted, failed.Status);
        Hc.ExternalOperationAdmissionReceipt receipt = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(failed.Receipt);
        Assert.Equal(Hc.ExternalRuntimeOutcome.Faulted, receipt.Outcome);
        Assert.NotNull(binding);
        Assert.Equal(binding, Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot()).Binding);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale, scenario.Provider.Admit(Semantic()).Status);
        Assert.Equal(Hc.ExternalRuntimeOutcome.Faulted, scenario.Provider.Submit(receipt.Request).Receipt!.Outcome);
        int effects = 0;
        Assert.False(scenario.Provider.Publish(receipt.Request, () => effects++).IsSuccess);
        Assert.Equal(0, effects);
        Assert.False(scenario.Provider.Release(receipt.Request, false).IsSuccess);
        Assert.False(scenario.Kernel.ReleaseRegion(scenario.Process, scenario.Input).IsSuccess);
        Assert.True(scenario.Provider.RecordDeviceCompletion(receipt.Request, ExternalOperationCompletionDisposition.Cancelled).IsSuccess);
        Assert.True(scenario.Provider.Release(receipt.Request, true).IsSuccess);
        Assert.True(scenario.Kernel.ReleaseRegion(scenario.Process, scenario.Input).IsSuccess);
        Assert.True(scenario.Kernel.ReleaseRegion(scenario.Process, scenario.Output).IsSuccess);
    }

    [Fact]
    public void LateSubmissionRefusalRetainsExactBindingForActualCompletionAndRelease()
    {
        SubmissionClock clock = new SubmissionClock();
        Scenario scenario = CreateScenario(clock);
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(scenario.Provider.Admit(Semantic()).Receipt).Request;
        ExternalOperationSnapshot original = Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot());
        Assert.True(scenario.Kernel.Traces.Start(scenario.Process, 16,
            TraceOverflowPolicy.DropWithMarker, TraceVisibilityClass.Self).IsSuccess);
        int callbacks = 0;
        clock.Next = () =>
        {
            callbacks++;
            Task worker = Task.Run(() =>
                Assert.True(scenario.Kernel.CancelExternalOperation(scenario.Process, original.Operation, false).IsSuccess));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Submission trace must run outside owner/provider gates.");
            worker.GetAwaiter().GetResult();
        };
        Hc.ExternalOperationProgressReceipt refused = Assert.IsType<Hc.ExternalOperationProgressReceipt>(scenario.Provider.Submit(request).Receipt);
        Assert.Equal(1, callbacks);
        Assert.Equal(Hc.ExternalRuntimeOutcome.Faulted, refused.Outcome);
        ExternalOperationSnapshot submitted = Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot());
        Assert.NotNull(submitted.Binding);
        Assert.Equal(ExternalOperationDisposition.CancellationPending, submitted.Disposition);
        Assert.False(scenario.Provider.Release(request, providerResourcesClosed: false).IsSuccess);
        Assert.False(scenario.Kernel.ReleaseRegion(scenario.Process, scenario.Input).IsSuccess);
        Assert.True(scenario.Provider.RecordDeviceCompletion(request, ExternalOperationCompletionDisposition.Cancelled).IsSuccess);
        Assert.Equal(submitted.Binding, Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot()).Binding);
        Assert.True(scenario.Provider.Release(request, providerResourcesClosed: true, providerEffectContained: true).IsSuccess);
        Assert.True(scenario.Kernel.ReleaseRegion(scenario.Process, scenario.Input).IsSuccess);
        Assert.True(scenario.Kernel.ReleaseRegion(scenario.Process, scenario.Output).IsSuccess);
        Assert.False(scenario.Provider.RecordDeviceCompletion(request).IsSuccess);
    }

    private sealed class SubmissionClock : TimeProvider
    {
        public Action? Next;
        public override long GetTimestamp()
        {
            Action? callback = Next;
            Next = null;
            callback?.Invoke();
            return base.GetTimestamp();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void InitialStagedProviderAdmissionRejectsDriftAndAbaBeforeActionButPreservesNoOp(int mode)
    {
        Scenario s = CreateScenario();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(s.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted, s.Provider.Submit(request).Receipt!.Stage);
        Assert.True(s.Provider.RecordDeviceCompletion(request).IsSuccess);
        Assert.True(s.Provider.RecordVisibility(request).IsSuccess);
        ExternalOperationSnapshot before = Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot());
        Hc.ExternalGenerationSet current = mode == 2 ? request.Generations : Generations(Guid.NewGuid());
        int actionCalls = 0;
        s.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = () =>
        {
            // A distinct thread must acquire the adapter gate before owner admission.
            Thread changing = new Thread(() =>
            {
                s.Provider.Reconfigure(current);
                if (mode == 1) s.Provider.Reconfigure(current = request.Generations);
            });
            changing.Start();
            Assert.True(changing.Join(TimeSpan.FromSeconds(5)));
            bool nestedAction = false;
            Assert.False(s.Provider.Publish(request, () => nestedAction = true).IsSuccess);
            Assert.False(nestedAction);
            Assert.Equal(before.Transitions, Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot()).Transitions);
        };
        KernelResult result;
        try
        {
            result = s.Provider.Publish(request, () =>
            {
                actionCalls++;
                // An admitted action runs outside the provider admission gate.
                Thread noOp = new Thread(() => s.Provider.Reconfigure(current));
                noOp.Start();
                Assert.True(noOp.Join(TimeSpan.FromSeconds(5)));
                s.Input.Span.CopyTo(s.Output.Span);
            });
        }
        finally { s.Kernel.ExternalPublicationBeforeAdmissionQualificationHook = null; }
        ExternalOperationSnapshot snapshot = Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot());
        if (mode == 2)
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(1, actionCalls);
            Assert.Equal(s.Input.Span.ToArray(), s.Output.Span.ToArray());
            Assert.Equal(ExternalOperationState.Published, snapshot.State);
            Assert.DoesNotContain(snapshot.Transitions, item => item.Event is "ProviderLost" or "PublicationEffectAmbiguous");
            Assert.True(s.Provider.Release(request, providerResourcesClosed: true).IsSuccess);
            return;
        }
        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(0, actionCalls);
        Assert.All(s.Output.Span.ToArray(), item => Assert.Equal(0, item));
        Assert.Equal(ExternalOperationState.Visible, snapshot.State);
        Assert.Equal(ExternalOperationDisposition.ProviderLost, snapshot.Disposition);
        Assert.Single(snapshot.Transitions, item => item.Event == "ProviderLost");
        Assert.DoesNotContain(snapshot.Transitions, item => item.Event is "Published" or "Released" or "PublicationEffectAmbiguous");
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale, s.Provider.Poll(request).Status);
        Assert.Equal(KernelError.StaleGeneration, s.Provider.Release(request, true).Error);
        Assert.True(s.Provider.ReconcileReconfiguredOperation(request, current, false).IsSuccess);
        Assert.True(s.Kernel.Regions.Validate(s.Output.Handle, s.Owner).IsSuccess);
        // Existing controlled closure assertion exercises retry, not physical containment.
        Assert.True(s.Provider.ReconcileReconfiguredOperation(request, current, true).IsSuccess);
        Assert.Equal(ExternalOperationState.Released, Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot()).State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LateStagedProviderCommitRejectsConcurrentDriftAndAbaButPreservesNoOp(int mode)
    {
        Scenario s = CreateScenario();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(s.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted, s.Provider.Submit(request).Receipt!.Stage);
        Assert.True(s.Provider.RecordDeviceCompletion(request).IsSuccess);
        Assert.True(s.Provider.RecordVisibility(request).IsSuccess);
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        Task<KernelResult> publication = Task.Run(() => s.Provider.Publish(request, () =>
        {
            s.Input.Span.CopyTo(s.Output.Span);
            entered.Set();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
        }));
        Hc.ExternalGenerationSet current = mode == 2 ? request.Generations : Generations(Guid.NewGuid());
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            s.Provider.Reconfigure(current);
            if (mode == 1) s.Provider.Reconfigure(current = request.Generations);
            Assert.Equal(Hc.ExternalOperationProviderPollStatus.Pending, s.Provider.Poll(request).Status);
            bool nestedAction = false;
            Assert.False(s.Provider.Publish(request, () => nestedAction = true).IsSuccess);
            Assert.False(nestedAction);
        }
        finally { resume.Set(); }
        KernelResult result = await publication;
        ExternalOperationSnapshot snapshot = Assert.Single(s.Kernel.ExternalOperations.InspectionSnapshot());
        Assert.Equal(s.Input.Span.ToArray(), s.Output.Span.ToArray());
        if (mode == 2)
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(ExternalOperationState.Published, snapshot.State);
            foreach (Hc.ExternalOperationStage stage in new[] { Hc.ExternalOperationStage.DeviceComplete,
                Hc.ExternalOperationStage.Visible, Hc.ExternalOperationStage.Published })
                Assert.Equal(stage, s.Provider.Poll(request).Receipt!.Stage);
            return;
        }
        Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
        Assert.Equal(ExternalOperationState.Visible, snapshot.State);
        Assert.Equal(ExternalEffectBoundaryState.PossiblyExternallyVisible, snapshot.EffectBoundary);
        Assert.Single(snapshot.Transitions, item => item.Event == "PublicationEffectAmbiguous");
        Assert.Single(snapshot.Transitions, item => item.Event == "ProviderLost");
        Assert.DoesNotContain(snapshot.Transitions, item => item.Event is "Published" or "Released");
        IReadOnlyList<SemanticTraceEventV1> trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot, new string('a', 64));
        Assert.Equal(2, trace.Count(item => item.Kind == SemanticTraceEventKindV1.Quarantined));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        ExternalOperationTransition last = snapshot.Transitions.Last();
        ExternalOperationSnapshot duplicate = snapshot with { Transitions = [.. snapshot.Transitions,
            last with { Sequence = last.Sequence + 1 }] };
        Assert.Throws<NotSupportedException>(() =>
            V6ExternalOperationTraceProjection.ProjectPublishedPrefix(duplicate, new string('a', 64)));
        Assert.Throws<InvalidOperationException>(() =>
            V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot with
            { PublicationPolicy = ExternalPublicationPolicy.DirectCoherent }, new string('a', 64)));
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale, s.Provider.Poll(request).Status);
        // Completion of the transition applies pending generations and admits an
        // exact reconciliation retry; neither its bool nor drift proves closure.
        Assert.True(s.Provider.ReconcileReconfiguredOperation(request, current, false).IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            s.Provider.ReconcileReconfiguredOperation(request, current, true).Error);
        Assert.True(s.Kernel.Regions.Validate(s.Output.Handle, s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NonResourceProviderReleaseProjectsOnlyActualLocalAuthorityFact(int phase)
    {
        Scenario scenario = CreateScenario();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(scenario.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted, scenario.Provider.Submit(request).Receipt!.Stage);
        if (phase == 0)
        {
            Assert.True(scenario.Provider.RecordDeviceCompletion(request).IsSuccess);
            ExternalOperationSnapshot completed = Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot());
            ExternalOperationSnapshot invented = completed with { State = ExternalOperationState.Released,
                Transitions = [.. completed.Transitions, new(completed.Transitions[^1].Sequence + 1,
                    completed.State, ExternalOperationState.Released, "Released")] };
            Assert.Throws<InvalidOperationException>(() => V6ExternalOperationTraceProjection.ProjectPublishedPrefix(invented, new string('a', 64)));
            Assert.True(scenario.Provider.RecordVisibility(request).IsSuccess);
            Assert.True(scenario.Provider.Publish(request, () => scenario.Input.Span.CopyTo(scenario.Output.Span)).IsSuccess);
        }
        else if (phase == 1 || phase == 3)
            Assert.True(scenario.Provider.RecordDeviceCompletion(request, phase == 1
                ? ExternalOperationCompletionDisposition.Faulted : ExternalOperationCompletionDisposition.Cancelled).IsSuccess);
        else
            Assert.True(scenario.Kernel.ExternalOperations.RecordProviderLoss(
                Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot()).Operation).IsSuccess);
        ExternalOperationSnapshot before = Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot());
        Assert.Equal(KernelError.InvalidTransition, scenario.Kernel.ExternalOperations.QueryResourceBinding(before.Operation).Error);
        Assert.False(scenario.Provider.Release(request, providerResourcesClosed: false).IsSuccess);
        Assert.Equal(before.Transitions, Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot()).Transitions);
        Assert.True(scenario.Provider.Release(request, providerResourcesClosed: true).IsSuccess);
        ExternalOperationSnapshot released = Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot());
        Assert.True(scenario.Provider.Release(request, providerResourcesClosed: true).IsSuccess);
        Assert.Equal(released.Transitions, Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot()).Transitions);
        IReadOnlyList<SemanticTraceEventV1> trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(released, new string('a', 64));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.Equal(SemanticTraceEventKindV1.LocalAuthorityReleasedWithoutResourceBinding, trace[^1].Kind);
        Assert.DoesNotContain(trace, item => item.Kind is SemanticTraceEventKindV1.Settled or
            SemanticTraceEventKindV1.EffectClosedWithoutPublication or SemanticTraceEventKindV1.Released);
        Assert.Equal(phase != 0, trace.Any(item => item.Kind == SemanticTraceEventKindV1.Quarantined));
        Assert.False(trace[^1].AuthorizesEffect);
    }

    [Fact]
    public void PackagedAdapterSessionDrivesExactSingNextProviderStages()
    {
        Scenario scenario = CreateScenario();
        CapturingProvider capturingProvider = new CapturingProvider(scenario.Provider);
        ExternalOperationAdapterSession session = new ExternalOperationAdapterSession(capturingProvider);
        Hc.ExternalOperationSemanticRequest semantic = Semantic();

        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Admit(semantic));
        Assert.Equal(1, capturingProvider.AdmitCount);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Submit(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Submit(semantic.Correlation));

        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationRequest>(capturingProvider.AdmittedRequest);
        Assert.True(scenario.Provider.RecordDeviceCompletion(request).IsSuccess);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Poll(semantic.Correlation));
        Assert.Equal(0, scenario.Output.Span[0]);

        Assert.True(scenario.Provider.RecordVisibility(request).IsSuccess);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Poll(semantic.Correlation));
        Assert.Equal(0, scenario.Output.Span[0]);

        Assert.True(scenario.Provider.Publish(request,
            () => scenario.Input.Span.CopyTo(scenario.Output.Span)).IsSuccess);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Poll(semantic.Correlation));
        Assert.Equal(scenario.Input.Span.ToArray(), scenario.Output.Span.ToArray());

        Assert.True(scenario.Provider.Release(request, providerResourcesClosed: true).IsSuccess);
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Poll(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Poll(semantic.Correlation));
    }

    [Fact]
    public void PackagedAdapterSessionDoesNotTreatAmbiguousCancelAsClosure()
    {
        Scenario scenario = CreateScenario();
        ExternalOperationAdapterSession session = new ExternalOperationAdapterSession(scenario.Provider);
        Hc.ExternalOperationSemanticRequest semantic = Semantic();
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Submit(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Cancel(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Poll(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Admit(semantic));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Submit(semantic.Correlation));
        Assert.Equal(0, scenario.Output.Span[0]);
    }

    [Fact]
    public void PackagedAdapterSessionKeepsPossibleEffectStaleAfterProviderReconfiguration()
    {
        Scenario scenario = CreateScenario();
        ExternalOperationAdapterSession session = new ExternalOperationAdapterSession(scenario.Provider);
        Hc.ExternalOperationSemanticRequest semantic = Semantic();

        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Admit(semantic));
        Assert.Equal(ExternalOperationAdapterStatus.Accepted, session.Submit(semantic.Correlation));
        scenario.Provider.Reconfigure(Generations(Guid.NewGuid()));

        Assert.Equal(ExternalOperationAdapterStatus.Stale, session.Poll(semantic.Correlation));
        Assert.Equal(ExternalOperationAdapterStatus.Rejected, session.Submit(semantic.Correlation));
        Assert.Equal(0, scenario.Output.Span[0]);
        Assert.True(scenario.Kernel.Regions.Validate(scenario.Output.Handle, scenario.Owner).IsSuccess);
    }

    [Fact]
    public void CancellationReceiptDistinguishesPreSubmitClosureFromPendingPostSubmitEffect()
    {
        Scenario before = CreateScenario();
        Hc.ExternalOperationRequest beforeRequest = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            before.Provider.Admit(Semantic()).Receipt).Request;
        Hc.ExternalOperationCancellationReceipt preSubmit = before.Provider.RequestCancellation(beforeRequest);
        Assert.Equal(Hc.ExternalOperationCancellationOutcome.ConfirmedBeforeSubmit,
            preSubmit.Outcome);
        Assert.True(preSubmit.IsConfirmed);
        ExternalOperationSnapshot closedAdmission = Assert.Single(before.Kernel.ExternalOperations.InspectionSnapshot());
        Assert.Equal(ExternalOperationState.Released, closedAdmission.State);
        Assert.Equal(ExternalOperationDisposition.Cancelled, closedAdmission.Disposition);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale,
            before.Provider.Submit(beforeRequest).Status);

        Scenario after = CreateScenario();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            after.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted,
            after.Provider.Submit(request).Receipt!.Stage);
        Hc.ExternalOperationCancellationReceipt postSubmit = after.Provider.RequestCancellation(request);
        Assert.Equal(Hc.ExternalOperationCancellationOutcome.Ambiguous,
            postSubmit.Outcome);
        Assert.False(postSubmit.IsConfirmed);
        Assert.False(after.Provider.Release(request, providerResourcesClosed: false).IsSuccess);
        Assert.True(after.Kernel.Regions.Validate(after.Output.Handle, after.Owner).IsSuccess);

        Assert.True(after.Provider.RecordDeviceCompletion(request).IsSuccess);
        Assert.True(after.Provider.RecordVisibility(request).IsSuccess);
        Assert.True(after.Provider.Publish(request,
            () => after.Input.Span.CopyTo(after.Output.Span)).IsSuccess);
        Assert.Equal(after.Input.Span.ToArray(), after.Output.Span.ToArray());
    }

    [Fact]
    public void VersionedAdapterDrivesExactSingNextOsLifecycleWithoutConflatingStages()
    {
        Scenario scenario = CreateScenario();
        Hc.ExternalOperationSemanticRequest semantic = Semantic();

        Hc.ExternalOperationProviderPollResult admitted = scenario.Provider.Admit(semantic);
        Hc.ExternalOperationAdmissionReceipt admission = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(admitted.Receipt);
        Assert.Equal(Hc.ExternalOperationStage.Admitted, admission.Stage);

        Hc.ExternalOperationProviderPollResult submitted = scenario.Provider.Submit(admission.Request);
        Assert.Equal(Hc.ExternalOperationStage.Submitted, submitted.Receipt!.Stage);

        Assert.True(scenario.Provider.RecordDeviceCompletion(admission.Request).IsSuccess);
        Hc.ExternalOperationProviderPollResult completed = scenario.Provider.Poll(admission.Request);
        Assert.IsType<Hc.ExternalOperationCompletionReceipt>(completed.Receipt);
        Assert.Equal(Hc.ExternalOperationStage.DeviceComplete, completed.Receipt!.Stage);
        Assert.Equal(0, scenario.Output.Span[0]);

        Assert.True(scenario.Provider.RecordVisibility(admission.Request).IsSuccess);
        Hc.ExternalOperationProviderPollResult visible = scenario.Provider.Poll(admission.Request);
        Assert.Equal(Hc.ExternalOperationStage.Visible, visible.Receipt!.Stage);
        Assert.Equal(0, scenario.Output.Span[0]);

        Assert.True(scenario.Provider.Publish(admission.Request,
            () => scenario.Input.Span.CopyTo(scenario.Output.Span)).IsSuccess);
        Hc.ExternalOperationProviderPollResult published = scenario.Provider.Poll(admission.Request);
        Assert.IsType<Hc.ExternalOperationPublicationReceipt>(published.Receipt);
        Assert.Equal(Hc.ExternalOperationStage.Published, published.Receipt!.Stage);
        Assert.Equal(scenario.Input.Span.ToArray(), scenario.Output.Span.ToArray());

        Assert.True(scenario.Provider.Release(admission.Request, providerResourcesClosed: true).IsSuccess);
        Hc.ExternalOperationProviderPollResult released = scenario.Provider.Poll(admission.Request);
        Assert.IsType<Hc.ExternalOperationReleaseReceipt>(released.Receipt);
        Assert.Equal(Hc.ExternalOperationStage.Released, released.Receipt!.Stage);
    }

    [Fact]
    public void AdapterRejectsDuplicateCrossOperationAndReconfiguredGenerationFailClosed()
    {
        Scenario scenario = CreateScenario();
        Hc.ExternalOperationSemanticRequest semantic = Semantic();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(scenario.Provider.Admit(semantic).Receipt).Request;

        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale, scenario.Provider.Admit(semantic).Status);
        Hc.ExternalOperationRequest wrong = new Hc.ExternalOperationRequest(
            new(new(Guid.NewGuid()), request.Operation.Generation), request.Scope, request.ContractVersion,
            request.Generations, request.Correlation, request.EffectClass,
            request.VisibilityRequirement, request.CancellationMode);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale, scenario.Provider.Submit(wrong).Status);
        Assert.Equal(Hc.ExternalOperationStage.Submitted, scenario.Provider.Submit(request).Receipt!.Stage);
        Assert.Equal(KernelError.InvalidTransition, scenario.Provider.RecordDeviceCompletion(wrong).Error);
        Assert.Equal(KernelError.InvalidTransition, scenario.Provider.RecordVisibility(wrong).Error);
        bool publicationRan = false;
        Assert.Equal(KernelError.ExternalOperationNotFound,
            scenario.Provider.Publish(wrong, () => publicationRan = true).Error);
        Assert.False(publicationRan);
        Assert.Equal(KernelError.ExternalOperationNotFound,
            scenario.Provider.Release(wrong, providerResourcesClosed: true).Error);

        scenario.Provider.Reconfigure(Generations(Guid.NewGuid()));
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale, scenario.Provider.Submit(request).Status);
        Assert.Equal(Hc.ExternalOperationCancellationOutcome.Stale,
            scenario.Provider.RequestCancellation(request).Outcome);
        Assert.Equal(0, scenario.Output.Span[0]);
    }

    [Fact]
    public void ReconfigureAfterSubmitRejectsStaleClosureAndKeepsRegionPinned()
    {
        Scenario scenario = CreateScenario();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            scenario.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted,
            scenario.Provider.Submit(request).Receipt!.Stage);

        scenario.Provider.Reconfigure(Generations(Guid.NewGuid()));

        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale,
            scenario.Provider.Poll(request).Status);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Provider.Release(request, providerResourcesClosed: true,
                providerUnavailable: true, providerEffectContained: true).Error);
        Assert.True(scenario.Kernel.Regions.Validate(scenario.Output.Handle, scenario.Owner).IsSuccess);
        Assert.All(scenario.Output.Span.ToArray(), value => Assert.Equal(0, value));
        ExternalOperationSnapshot owner = Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot());
        Assert.Equal(ExternalOperationDisposition.ProviderLost, owner.Disposition);
        Assert.Equal(ExternalOperationState.Submitted, owner.State);
    }

    [Fact]
    public void ReconfigureBeforeSubmitClosesLocalAdmissionWithoutInventingEffect()
    {
        Scenario scenario = CreateScenario();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            scenario.Provider.Admit(Semantic()).Receipt).Request;

        scenario.Provider.Reconfigure(Generations(Guid.NewGuid()));

        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale,
            scenario.Provider.Submit(request).Status);
        ExternalOperationSnapshot owner = Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot());
        Assert.Equal(ExternalOperationState.Released, owner.State);
        Assert.Equal(ExternalOperationDisposition.Cancelled, owner.Disposition);
        Assert.All(scenario.Output.Span.ToArray(), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReconfigureAfterCompletionOrVisibilityProjectsProviderLossWithoutPublication(bool visible)
    {
        Scenario scenario = CreateScenario();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            scenario.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted,
            scenario.Provider.Submit(request).Receipt!.Stage);
        Assert.True(scenario.Provider.RecordDeviceCompletion(request).IsSuccess);
        if (visible) Assert.True(scenario.Provider.RecordVisibility(request).IsSuccess);

        scenario.Provider.Reconfigure(Generations(Guid.NewGuid()));

        ExternalOperationSnapshot owner = Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot());
        Assert.Equal(visible ? ExternalOperationState.Visible : ExternalOperationState.DeviceComplete,
            owner.State);
        Assert.Equal(ExternalOperationDisposition.ProviderLost, owner.Disposition);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Provider.Publish(request, () => throw new Exception("stale publication")).Error);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Provider.Release(request, providerResourcesClosed: true).Error);
        Assert.All(scenario.Output.Span.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void ReconfigureAfterReleasePreservesPublishedTerminalDecision()
    {
        Scenario scenario = CreateScenario();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            scenario.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted,
            scenario.Provider.Submit(request).Receipt!.Stage);
        Assert.True(scenario.Provider.RecordDeviceCompletion(request).IsSuccess);
        Assert.True(scenario.Provider.RecordVisibility(request).IsSuccess);
        Assert.True(scenario.Provider.Publish(request,
            () => scenario.Input.Span.CopyTo(scenario.Output.Span)).IsSuccess);
        Assert.True(scenario.Provider.Release(request, providerResourcesClosed: true).IsSuccess);

        scenario.Provider.Reconfigure(Generations(Guid.NewGuid()));

        ExternalOperationSnapshot owner = Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot());
        Assert.Equal(ExternalOperationState.Released, owner.State);
        Assert.Equal(ExternalOperationDisposition.Published, owner.Disposition);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale,
            scenario.Provider.Poll(request).Status);
    }

    [Fact]
    public void ReconfiguredOperationRequiresCurrentGenerationAndExplicitClosureForReconciliation()
    {
        Scenario scenario = CreateScenario();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            scenario.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted,
            scenario.Provider.Submit(request).Receipt!.Stage);
        Hc.ExternalGenerationSet replacement = Generations(Guid.NewGuid());
        scenario.Provider.Reconfigure(replacement);

        Assert.Equal(KernelError.StaleGeneration,
            scenario.Provider.ReconcileReconfiguredOperation(request, request.Generations,
                providerResourcesClosed: true).Error);
        Assert.True(scenario.Provider.ReconcileReconfiguredOperation(request, replacement,
            providerResourcesClosed: false).IsSuccess);
        Assert.True(scenario.Kernel.Regions.Validate(scenario.Output.Handle, scenario.Owner).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Provider.Release(request, providerResourcesClosed: true).Error);
        Assert.True(scenario.Provider.ReconcileReconfiguredOperation(request, replacement,
            providerResourcesClosed: true).IsSuccess);
        Assert.All(scenario.Output.Span.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void ReconfigureGenerationAbaNeverReauthorizesOldRequest()
    {
        Scenario scenario = CreateScenario();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            scenario.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted,
            scenario.Provider.Submit(request).Receipt!.Stage);
        scenario.Provider.Reconfigure(Generations(Guid.NewGuid()));
        scenario.Provider.Reconfigure(request.Generations);

        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale,
            scenario.Provider.Poll(request).Status);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Provider.RecordDeviceCompletion(request).Error);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Provider.Publish(request, () => throw new Exception("stale publication")).Error);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Provider.Release(request, providerResourcesClosed: true).Error);
        Assert.True(scenario.Provider.ReconcileReconfiguredOperation(request, request.Generations,
            providerResourcesClosed: false).IsSuccess);
        Assert.True(scenario.Kernel.Regions.Validate(scenario.Output.Handle, scenario.Owner).IsSuccess);
    }

    [Fact]
    public void ReconfigureGenerationAbaInsideAmbiguousPublicationCannotReauthorizeOldRequest()
    {
        Scenario scenario = CreateScenario();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            scenario.Provider.Admit(Semantic()).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted,
            scenario.Provider.Submit(request).Receipt!.Stage);
        Assert.True(scenario.Provider.RecordDeviceCompletion(request).IsSuccess);
        Assert.True(scenario.Provider.RecordVisibility(request).IsSuccess);
        Hc.ExternalGenerationSet changed = Generations(Guid.NewGuid());

        Assert.Equal(KernelError.ExternalEffectUncontained,
            scenario.Provider.Publish(request, () =>
        {
            scenario.Provider.Reconfigure(changed);
            scenario.Provider.Reconfigure(request.Generations);
            throw new InvalidOperationException("publication outcome unknown");
        }).Error);

        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale,
            scenario.Provider.Poll(request).Status);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Provider.Release(request, providerResourcesClosed: true).Error);
        Assert.Equal(ExternalOperationDisposition.ProviderLost,
            Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot()).Disposition);
        Assert.True(scenario.Kernel.Regions.Validate(scenario.Output.Handle, scenario.Owner).IsSuccess);
        Assert.All(scenario.Output.Span.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void CompletionWithoutVisibilityAndUnavailableReleaseNeverPublishOrReclaim()
    {
        Scenario scenario = CreateScenario();
        Hc.ExternalOperationSemanticRequest semantic = Semantic();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(scenario.Provider.Admit(semantic).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted, scenario.Provider.Submit(request).Receipt!.Stage);
        Assert.True(scenario.Provider.RecordDeviceCompletion(request).IsSuccess);

        bool publicationRan = false;
        KernelResult publish = scenario.Provider.Publish(request, () => publicationRan = true);
        KernelResult release = scenario.Provider.Release(request, providerResourcesClosed: false);

        Assert.False(publish.IsSuccess);
        Assert.False(release.IsSuccess);
        Assert.False(publicationRan);
        Assert.True(scenario.Kernel.Regions.Validate(scenario.Output.Handle, scenario.Owner).IsSuccess);
    }

    [Fact]
    public void FaultedCompletionAndProviderLossNeverBecomePendingOrSuccessfulReceipts()
    {
        Scenario faulted = CreateScenario();
        Hc.ExternalOperationSemanticRequest semantic = Semantic();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(faulted.Provider.Admit(semantic).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted, faulted.Provider.Submit(request).Receipt!.Stage);
        Assert.True(faulted.Provider.RecordDeviceCompletion(
            request, ExternalOperationCompletionDisposition.Faulted).IsSuccess);
        Hc.ExternalOperationProviderPollResult fault = faulted.Provider.Poll(request);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Faulted, fault.Status);
        Assert.Equal(Hc.ExternalRuntimeOutcome.Faulted, fault.Receipt!.Outcome);

        Scenario lost = CreateScenario();
        request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(lost.Provider.Admit(semantic).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted, lost.Provider.Submit(request).Receipt!.Stage);
        Assert.False(lost.Provider.Release(request, providerResourcesClosed: false,
            providerUnavailable: true).IsSuccess);
        Hc.ExternalOperationProviderPollResult unavailable = lost.Provider.Poll(request);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Unavailable, unavailable.Status);
        Assert.Equal(Hc.ExternalRuntimeOutcome.Unknown, unavailable.Receipt!.Outcome);
        Assert.All(lost.Output.Span.ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void PollDeliversEveryReachedStageInOrderAndTerminalReplayIsStale()
    {
        Scenario scenario = CreateScenario();
        Hc.ExternalOperationSemanticRequest semantic = Semantic();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(scenario.Provider.Admit(semantic).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted, scenario.Provider.Submit(request).Receipt!.Stage);
        Assert.True(scenario.Provider.RecordDeviceCompletion(request).IsSuccess);
        Assert.True(scenario.Provider.RecordVisibility(request).IsSuccess);
        Assert.True(scenario.Provider.Publish(request,
            () => scenario.Input.Span.CopyTo(scenario.Output.Span)).IsSuccess);
        Assert.True(scenario.Provider.Release(request, providerResourcesClosed: true).IsSuccess);

        Assert.Equal(Hc.ExternalOperationStage.DeviceComplete, scenario.Provider.Poll(request).Receipt!.Stage);
        Assert.Equal(Hc.ExternalOperationStage.Visible, scenario.Provider.Poll(request).Receipt!.Stage);
        Assert.Equal(Hc.ExternalOperationStage.Published, scenario.Provider.Poll(request).Receipt!.Stage);
        Assert.Equal(Hc.ExternalOperationStage.Released, scenario.Provider.Poll(request).Receipt!.Stage);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale, scenario.Provider.Poll(request).Status);
    }

    [Fact]
    public void ProviderUnavailableAndContainmentClaimAreNotClosureButResourceCloseCanRelease()
    {
        Scenario scenario = CreateScenario();
        Hc.ExternalOperationSemanticRequest semantic = Semantic();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(scenario.Provider.Admit(semantic).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted, scenario.Provider.Submit(request).Receipt!.Stage);

        KernelResult unavailable = scenario.Provider.Release(request,
            providerResourcesClosed: false, providerUnavailable: true);
        Assert.False(unavailable.IsSuccess);
        Assert.True(scenario.Kernel.Regions.Validate(scenario.Output.Handle, scenario.Owner).IsSuccess);

        KernelResult contained = scenario.Provider.Release(request, providerResourcesClosed: false,
            providerUnavailable: true, providerEffectContained: true);
        Assert.False(contained.IsSuccess);
        Assert.True(scenario.Kernel.Regions.Validate(scenario.Output.Handle, scenario.Owner).IsSuccess);

        KernelResult closed = scenario.Provider.Release(request, providerResourcesClosed: true,
            providerUnavailable: true);
        Assert.True(closed.IsSuccess, closed.Message);
    }

    [Fact]
    public void PublicationCallbackReentryIsFailClosedAndGenerationChangeWaitsForBoundaryExit()
    {
        Scenario scenario = CreateScenario();
        Hc.ExternalOperationSemanticRequest semantic = Semantic();
        Hc.ExternalOperationRequest request = Assert.IsType<Hc.ExternalOperationAdmissionReceipt>(
            scenario.Provider.Admit(semantic).Receipt).Request;
        Assert.Equal(Hc.ExternalOperationStage.Submitted,
            scenario.Provider.Submit(request).Receipt!.Stage);
        Assert.True(scenario.Provider.RecordDeviceCompletion(request).IsSuccess);
        Assert.True(scenario.Provider.RecordVisibility(request).IsSuccess);

        Hc.ExternalOperationProviderPollResult? nested = null;
        Hc.ExternalOperationProviderPollResult? admissionDuringReset = null;
        KernelResult? prematureReconciliation = null;
        Hc.ExternalGenerationSet replacement = Generations(Guid.Parse("fa6c6d88-d2ee-4dd6-8d09-cbbf0735aac8"));
        KernelResult published = scenario.Provider.Publish(request, () =>
        {
            scenario.Provider.Reconfigure(replacement);
            nested = scenario.Provider.Poll(request);
            admissionDuringReset = scenario.Provider.Admit(new(
                Hc.ExternalOperationContract.Version,
                new(Guid.NewGuid()),
                Hc.ExternalEffectClass.NonIdempotent,
                Hc.ExternalVisibilityRequirement.StagedOutput,
                Hc.ExternalCancellationMode.ExactAcknowledgement,
                Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish));
            prematureReconciliation = scenario.Provider.ReconcileReconfiguredOperation(request,
                replacement, providerResourcesClosed: true);
            scenario.Input.Span.CopyTo(scenario.Output.Span);
        });

        Assert.Equal(KernelError.ExternalEffectUncontained, published.Error);
        Assert.NotNull(nested);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Pending, nested.Status);
        Assert.Equal(request.Generations, nested.CurrentGenerations);
        Assert.NotNull(admissionDuringReset);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Pending, admissionDuringReset.Status);
        Assert.Null(admissionDuringReset.Receipt);
        Assert.Equal(KernelError.StaleGeneration, prematureReconciliation!.Value.Error);
        Assert.Equal(Hc.ExternalOperationProviderPollStatus.Stale,
            scenario.Provider.Poll(request).Status);
        Assert.Equal(scenario.Input.Span.ToArray(), scenario.Output.Span.ToArray());
        Assert.Equal(ExternalOperationDisposition.ProviderLost,
            Assert.Single(scenario.Kernel.ExternalOperations.InspectionSnapshot()).Disposition);
    }

    private static Scenario CreateScenario(TimeProvider? clock = null)
    {
        RuntimeKernel kernel = new RuntimeKernel(null, clock);
        (SingProcess _, ProcessHandle process) = TestFixtures.Create(kernel, 1701, 1702);
        SingProcess resolved = kernel.Processes.Resolve(process).Value!;
        RegionOwner owner = new RegionOwner(resolved.DomainId, process.Generation);
        OwnedBuffer<byte> input = kernel.AllocateBuffer<byte>(process, 8).Value!;
        OwnedBuffer<byte> output = kernel.AllocateBuffer<byte>(process, 8).Value!;
        for (int index = 0; index < input.Length; index++) input.Span[index] = (byte)(index + 1);
        Hc.ExternalGenerationSet generations = Generations(Guid.Parse("5aa6b3e1-1865-4f5a-a498-e401616d8f20"));
        HybridCpuExternalOperationProvider provider = new HybridCpuExternalOperationProvider(kernel, process,
        [
            new(input.Handle, RegionUseMode.ReadOnly, new(0, input.Length)),
            new(output.Handle, RegionUseMode.StagedOutput, new(0, output.Length))
        ], new(7, 11, 13, 17), new("singnextos-runtime"),
            new(new(Guid.Parse("229b207b-feef-450d-b2fc-e498bfdb76df")), new(1)), generations);
        return new(kernel, owner, input, output, provider, process);
    }

    private static Hc.ExternalOperationSemanticRequest Semantic() => new(
        Hc.ExternalOperationContract.Version,
        new(Guid.Parse("4bcf3f23-e366-4849-a5d6-48dd062ad932")),
        Hc.ExternalEffectClass.NonIdempotent,
        Hc.ExternalVisibilityRequirement.StagedOutput,
        Hc.ExternalCancellationMode.ExactAcknowledgement,
        Hc.ExternalReplayEffectClass.StagedReversibleUntilPublish);

    private static Hc.ExternalGenerationSet Generations(Guid value) =>
        new(Hc.ExternalOperationContract.Version, [value]);

    private sealed record Scenario(RuntimeKernel Kernel, RegionOwner Owner,
        OwnedBuffer<byte> Input, OwnedBuffer<byte> Output,
        HybridCpuExternalOperationProvider Provider, ProcessHandle Process);

    private sealed class CapturingProvider(HybridCpuExternalOperationProvider inner) :
        Hc.IExternalOperationProvider, Hc.IExternalOperationCancellationProvider
    {
        public Hc.ExternalOperationRequest? AdmittedRequest { get; private set; }
        public int AdmitCount { get; private set; }

        public Hc.ExternalOperationProviderPollResult Admit(Hc.ExternalOperationSemanticRequest semantic)
        {
            AdmitCount++;
            Hc.ExternalOperationProviderPollResult result = inner.Admit(semantic);
            if (result.Receipt is Hc.ExternalOperationAdmissionReceipt receipt)
                AdmittedRequest = receipt.Request;
            return result;
        }

        public Hc.ExternalOperationProviderPollResult Submit(Hc.ExternalOperationRequest request) => inner.Submit(request);
        public Hc.ExternalOperationProviderPollResult Poll(Hc.ExternalOperationRequest request) => inner.Poll(request);
        public Hc.ExternalOperationProviderPollResult Cancel(Hc.ExternalOperationRequest request) => inner.Cancel(request);
        public Hc.ExternalOperationCancellationReceipt RequestCancellation(Hc.ExternalOperationRequest request) =>
            inner.RequestCancellation(request);
    }
}
