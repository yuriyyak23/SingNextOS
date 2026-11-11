using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class ExternalOperationLifecycleTests
{
    [Theory]
    [InlineData(false, false, false)] [InlineData(false, true, false)]
    [InlineData(true, false, false)] [InlineData(true, true, false)]
    [InlineData(true, false, true)] [InlineData(true, true, true)]
    [InlineData(true, false, false, true)] [InlineData(true, true, false, true)]
    public void PublicationRouteLossRetainsOriginalDecisionAndActualEffect(bool afterAction, bool copied, bool actionThrows, bool directOwner = false)
    {
        Scenario s = CreateScenario();
        ProcessHandle admin = TestFixtures.Create(s.Kernel, 3, 30).Handle;
        CapabilityId capability = s.Kernel.MintCapability(new(30), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle account = s.Kernel.AdmitProcessBudget(admin, capability, s.Owner, "publication-route-loss",
            [new(ServiceBudgetDimension.ExternalOperations, 1)]).Value!.ProcessBudget;
        OperationPreparation prepared = Prepare(s, ExternalPublicationPolicy.Staged);
        Assert.True(s.Kernel.AdmitExternalOperation(s.Owner, prepared.Operation, Dependencies).IsSuccess);
        OperationBinding binding = s.Kernel.RecordExternalOperationSubmission(s.Owner, prepared.Operation, Dependencies).Value!;
        Assert.True(s.Kernel.RecordExternalOperationCompletion(s.Owner, new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(s.Kernel.RecordExternalOperationVisibility(s.Owner, new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        IDictionary routes = (global::System.Collections.IDictionary)typeof(ExternalOperationAuthority)
            .GetField("_operations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.ExternalOperations)!;
        object original = routes[prepared.Operation.OperationId]!;
        void LoseRoute()
        {
            if (copied) routes[prepared.Operation.OperationId] = typeof(object)
                .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)!;
            else routes.Remove(prepared.Operation.OperationId);
        }
        int actions = 0;
        Action<ExternalPublicationDecisionV1> action = _ =>
            {
                actions++; s.Output.Span.Fill(0x5A);
                if (directOwner) LoseRoute();
                if (actionThrows) { LoseRoute(); throw new InvalidOperationException("Lost route after output."); }
            };
        KernelResult<ExternalOperationSnapshot> result = directOwner
            ? s.Kernel.ExternalOperations.Publish(prepared.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), action)
            : s.Kernel.PublishExternalOperation(s.Owner, prepared.Operation, Dependencies,
            new(ExternalPublicationPolicy.Staged), action,
            sourceCommit: commit => { if (afterAction) LoseRoute(); return commit(); },
            sourceAdmission: admit => { KernelResult<ExternalOperationSnapshot> admitted = admit(); if (!afterAction) LoseRoute(); return admitted; });
        Assert.False(result.IsSuccess);
        Assert.Equal(afterAction ? 1 : 0, actions);
        routes[prepared.Operation.OperationId] = original;
        Assert.False((bool)original.GetType().GetProperty("PublicationInFlight")!.GetValue(original)!);
        if (afterAction)
        {
            Assert.All(s.Output.Span.ToArray(), value => Assert.Equal(0x5A, value));
            Assert.Equal(ExternalEffectBoundaryState.PossiblyExternallyVisible,
                s.Kernel.QueryExternalOperation(s.Owner, prepared.Operation).Value!.EffectBoundary);
            Assert.False(s.Kernel.ReleaseExternalOperation(s.Owner, prepared.Operation, new(true, true)).IsSuccess);
            Assert.Contains(s.Kernel.QueryBudget(account).Value!.Usage,
                usage => usage.Dimension == ServiceBudgetDimension.ExternalOperations && usage.Used == 1);
        }
        else
        {
            Assert.All(s.Output.Span.ToArray(), value => Assert.Equal(0, value));
            Assert.True(s.Kernel.PublishExternalOperation(s.Owner, prepared.Operation, Dependencies,
                new(ExternalPublicationPolicy.Staged), () => actions++).IsSuccess);
            Assert.Equal(1, actions);
            Assert.True(s.Kernel.ReleaseExternalOperation(s.Owner, prepared.Operation, new(true, true)).IsSuccess);
            Assert.Contains(s.Kernel.QueryBudget(account).Value!.Usage,
                usage => usage.Dimension == ServiceBudgetDimension.ExternalOperations && usage.Used == 0);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void LostExternalChargeRouteCannotLoseExactReleasedOperationDebit(int fault)
    {
        Scenario s = CreateScenario();
        ProcessHandle admin = TestFixtures.Create(s.Kernel, 3, 30).Handle;
        CapabilityId capability = s.Kernel.MintCapability(new(30), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle account = s.Kernel.AdmitProcessBudget(admin, capability, s.Owner, "lost-operation-route",
            [new(ServiceBudgetDimension.ExternalOperations, 1)]).Value!.ProcessBudget;
        OperationPreparation prepared = Prepare(s, ExternalPublicationPolicy.Staged);
        Assert.True(s.Kernel.AdmitExternalOperation(s.Owner, prepared.Operation, Dependencies).IsSuccess);
        Dictionary<ExternalOperationHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> associations = (Dictionary<ExternalOperationHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)>)
            typeof(RuntimeKernel).GetField("_externalOperationBudgetReservations", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(s.Kernel)!;
        (ProcessHandle Owner, BudgetReservationHandle Reservation) original = associations[prepared.Operation];
        if (fault == 1)
        {
            IDictionary routes = (global::System.Collections.IDictionary)typeof(ExternalOperationAuthority)
                .GetField("_operations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.ExternalOperations)!;
            object record = routes[prepared.Operation.OperationId]!;
            routes[prepared.Operation.OperationId] = record.GetType()
                .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(record, null)!;
            Assert.False(s.Kernel.CancelExternalOperation(s.Owner, prepared.Operation, false).IsSuccess);
            Assert.False(s.Kernel.ReleaseExternalOperation(s.Owner, prepared.Operation, new(false, false)).IsSuccess);
            Assert.Equal(BudgetReservationState.Active, s.Kernel.QueryBudget(original.Reservation).Value!.State);
            routes[prepared.Operation.OperationId] = record;
        }
        if (fault == 2) associations[prepared.Operation] = (admin, original.Reservation);
        else associations.Remove(prepared.Operation);
        Assert.True(s.Kernel.CancelExternalOperation(s.Owner, prepared.Operation, false).IsSuccess);
        Assert.True(s.Kernel.ReleaseExternalOperation(s.Owner, prepared.Operation, new(false, false)).IsSuccess);
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(prepared.Operation).Value!.State);
        if (fault == 2)
        {
            Assert.Equal(BudgetReservationState.Active, s.Kernel.QueryBudget(original.Reservation).Value!.State);
            associations.Remove(prepared.Operation);
            Assert.True(s.Kernel.ReleaseExternalOperation(s.Owner, prepared.Operation, new(false, false)).IsSuccess);
        }
        Assert.Equal(BudgetReservationState.Released, s.Kernel.QueryBudget(original.Reservation).Value!.State);
        Assert.Contains(s.Kernel.QueryBudget(account).Value!.Usage,
            u => u.Dimension == ServiceBudgetDimension.ExternalOperations && u.Used == 0);
        Assert.True(s.Kernel.ReleaseExternalOperation(s.Owner, prepared.Operation, new(false, false)).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProviderLossDuringPublicationRetainsPossibleEffectAndRefusesCommit(bool sourceHook, bool actionThrows)
    {
        Scenario s = CreateScenario();
        OperationPreparation prepared = Prepare(s, ExternalPublicationPolicy.Staged);
        Assert.True(s.Kernel.AdmitExternalOperation(s.Owner, prepared.Operation, Dependencies).IsSuccess);
        OperationBinding binding = s.Kernel.RecordExternalOperationSubmission(s.Owner, prepared.Operation, Dependencies).Value!;
        Assert.True(s.Kernel.RecordExternalOperationCompletion(s.Owner, new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(s.Kernel.RecordExternalOperationVisibility(s.Owner, new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        Action<ExternalPublicationDecisionV1> action = _ =>
        {
            s.Output.Span.Fill(0x5A);
            entered.Set();
            Assert.True(resume.Wait(TimeSpan.FromSeconds(10)));
            if (actionThrows) throw new InvalidOperationException("Injected loss after possible output.");
        };
        Task<KernelResult<ExternalOperationSnapshot>> publication = Task.Run(() => sourceHook
            ? s.Kernel.PublishExternalOperation(s.Owner, prepared.Operation, Dependencies,
                new(ExternalPublicationPolicy.Staged), action, commit => commit())
            : s.Kernel.PublishExternalOperation(s.Owner, prepared.Operation, Dependencies,
                new(ExternalPublicationPolicy.Staged), action));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(s.Kernel.RecordExternalOperationProviderLoss(s.Owner, prepared.Operation).IsSuccess);
            Assert.True(s.Kernel.RecordExternalOperationProviderLoss(s.Owner, prepared.Operation).IsSuccess);
            Assert.False(s.Kernel.ReleaseExternalOperation(s.Owner, prepared.Operation, new(true, true)).IsSuccess);
        }
        finally { resume.Set(); }
        Assert.Equal(KernelError.ExternalEffectUncontained, (await publication).Error);
        ExternalOperationSnapshot snapshot = s.Kernel.QueryExternalOperation(s.Owner, prepared.Operation).Value!;
        Assert.Equal(ExternalOperationState.Visible, snapshot.State);
        Assert.Equal(ExternalEffectBoundaryState.PossiblyExternallyVisible, snapshot.EffectBoundary);
        Assert.Single(snapshot.Transitions, transition => transition.Event == "ProviderLost");
        Assert.Single(snapshot.Transitions, transition => transition.Event == "PublicationEffectAmbiguous");
        Assert.DoesNotContain(snapshot.Transitions, transition => transition.Event == "Published");
        Assert.False(s.Kernel.ReleaseExternalOperation(s.Owner, prepared.Operation, new(true, true)).IsSuccess);
        bool called = false;
        Assert.False(s.Kernel.PublishExternalOperation(s.Owner, prepared.Operation, Dependencies,
            new(ExternalPublicationPolicy.Staged), () => called = true).IsSuccess);
        Assert.False(called);
        // The callback did write. Refusal does not prove rollback or provider closure.
        Assert.All(s.Output.Span.ToArray(), value => Assert.Equal(0x5A, value));
        IReadOnlyList<SemanticTraceEventV1> trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot, new string('a', 64));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.DoesNotContain(trace, item => item.Kind is SemanticTraceEventKindV1.Published or
            SemanticTraceEventKindV1.Released or SemanticTraceEventKindV1.EffectClosedWithoutPublication);
    }

    [Theory]
    [InlineData(ExternalPublicationPolicy.Staged)]
    [InlineData(ExternalPublicationPolicy.DirectCoherent)]
    public async Task CompletionLossRaceCannotEraseLossOrAuthorizePublication(ExternalPublicationPolicy policy)
    {
        for (int sample = 0; sample < 16; sample++)
        {
            Scenario s = CreateScenario();
            OperationPreparation prepared = Prepare(s, policy);
            Assert.True(s.Kernel.AdmitExternalOperation(s.Owner, prepared.Operation, Dependencies).IsSuccess);
            OperationBinding binding = s.Kernel.RecordExternalOperationSubmission(s.Owner, prepared.Operation, Dependencies).Value!;
            using ManualResetEventSlim start = new ManualResetEventSlim();
            Task<KernelResult<ExternalOperationSnapshot>> completion = Task.Run(() => { start.Wait(); return s.Kernel.RecordExternalOperationCompletion(s.Owner,
                new(binding, ExternalOperationCompletionDisposition.Completed)); });
            Task<KernelResult<ExternalOperationSnapshot>> loss = Task.Run(() => { start.Wait(); return s.Kernel.RecordExternalOperationProviderLoss(s.Owner, prepared.Operation); });
            start.Set();
            KernelResult<ExternalOperationSnapshot> completed = await completion;
            Assert.True((await loss).IsSuccess);
            if (!completed.IsSuccess) Assert.Equal(KernelError.InvalidTransition, completed.Error);
            ExternalOperationSnapshot snapshot = s.Kernel.QueryExternalOperation(s.Owner, prepared.Operation).Value!;
            Assert.Equal(policy == ExternalPublicationPolicy.Staged ? ExternalOperationDisposition.ProviderLost : ExternalOperationDisposition.Faulted, snapshot.Disposition);
            Assert.Single(snapshot.Transitions, transition => transition.Event == "ProviderLost");
            Assert.False(s.Kernel.RecordExternalOperationVisibility(s.Owner,
                new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
            bool action = false;
            Assert.False(s.Kernel.PublishExternalOperation(s.Owner, prepared.Operation, Dependencies,
                new(policy), () => action = true).IsSuccess);
            Assert.False(action);
            IReadOnlyList<SemanticTraceEventV1> trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot, new string('a', 64));
            Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        }
    }

    [Theory]
    [InlineData(ExternalPublicationPolicy.Staged)]
    [InlineData(ExternalPublicationPolicy.DirectCoherent)]
    public async Task ConcurrentProviderLossCommitsOneOwnerFactWithoutClosure(ExternalPublicationPolicy policy)
    {
        Scenario s = CreateScenario();
        OperationPreparation prepared = Prepare(s, policy);
        Assert.True(s.Kernel.AdmitExternalOperation(s.Owner, prepared.Operation, Dependencies).IsSuccess);
        Assert.True(s.Kernel.RecordExternalOperationSubmission(s.Owner, prepared.Operation, Dependencies).IsSuccess);
        KernelResult<ExternalOperationSnapshot>[] losses = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            s.Kernel.RecordExternalOperationProviderLoss(s.Owner, prepared.Operation))));
        Assert.All(losses, loss => Assert.True(loss.IsSuccess, loss.Message));
        ExternalOperationSnapshot snapshot = s.Kernel.QueryExternalOperation(s.Owner, prepared.Operation).Value!;
        Assert.Single(snapshot.Transitions, transition => transition.Event == "ProviderLost");
        Assert.Equal(policy == ExternalPublicationPolicy.Staged ? ExternalOperationDisposition.ProviderLost : ExternalOperationDisposition.Faulted, snapshot.Disposition);
        ExternalOperationTransition last = snapshot.Transitions.Last();
        ExternalOperationSnapshot duplicateLoss = snapshot with
        {
            Transitions = snapshot.Transitions.Append(last with { Sequence = last.Sequence + 1 }).ToArray()
        };
        Assert.Throws<NotSupportedException>(() =>
            V6ExternalOperationTraceProjection.ProjectPublishedPrefix(duplicateLoss, new string('a', 64)));
        int count = snapshot.Transitions.Count;
        Assert.True(s.Kernel.RecordExternalOperationProviderLoss(s.Owner, prepared.Operation).IsSuccess);
        Assert.Equal(count, s.Kernel.QueryExternalOperation(s.Owner, prepared.Operation).Value!.Transitions.Count);
        Assert.False(s.Kernel.ReleaseExternalOperation(s.Owner, prepared.Operation, new(false, true)).IsSuccess);
        IReadOnlyList<SemanticTraceEventV1> trace = V6ExternalOperationTraceProjection.ProjectPublishedPrefix(snapshot, new string('a', 64));
        Assert.Single(trace, item => item.Kind == SemanticTraceEventKindV1.Quarantined);
        Assert.DoesNotContain(trace, item => item.Kind is SemanticTraceEventKindV1.Released or SemanticTraceEventKindV1.EffectClosedWithoutPublication);
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
    }

    [Theory]
    [InlineData(ExternalPublicationPolicy.Staged, ExternalOperationCompletionDisposition.Completed)]
    [InlineData(ExternalPublicationPolicy.Staged, ExternalOperationCompletionDisposition.Cancelled)]
    [InlineData(ExternalPublicationPolicy.Staged, ExternalOperationCompletionDisposition.Faulted)]
    [InlineData(ExternalPublicationPolicy.DirectCoherent, ExternalOperationCompletionDisposition.Completed)]
    [InlineData(ExternalPublicationPolicy.DirectCoherent, ExternalOperationCompletionDisposition.Cancelled)]
    [InlineData(ExternalPublicationPolicy.DirectCoherent, ExternalOperationCompletionDisposition.Faulted)]
    public void LateTerminalResponseCannotReplaceProviderLoss(ExternalPublicationPolicy policy,
        ExternalOperationCompletionDisposition disposition)
    {
        Scenario s = CreateScenario();
        OperationPreparation prepared = Prepare(s, policy);
        Assert.True(s.Kernel.AdmitExternalOperation(s.Owner, prepared.Operation, Dependencies).IsSuccess);
        OperationBinding binding = s.Kernel.RecordExternalOperationSubmission(s.Owner, prepared.Operation, Dependencies).Value!;
        ExternalOperationSnapshot lost = s.Kernel.RecordExternalOperationProviderLoss(s.Owner, prepared.Operation).Value!;
        KernelResult<ExternalOperationSnapshot> late = s.Kernel.RecordExternalOperationCompletion(s.Owner, new(binding, disposition));
        Assert.Equal(KernelError.InvalidTransition, late.Error);
        ExternalOperationSnapshot forged = lost with
        {
            State = ExternalOperationState.DeviceComplete,
            Disposition = disposition switch
            {
                ExternalOperationCompletionDisposition.Completed => ExternalOperationDisposition.Completed,
                ExternalOperationCompletionDisposition.Cancelled => ExternalOperationDisposition.Cancelled,
                _ => ExternalOperationDisposition.Faulted
            },
            Transitions = [.. lost.Transitions, new(lost.Transitions[^1].Sequence + 1,
                ExternalOperationState.Submitted, ExternalOperationState.DeviceComplete,
                disposition == ExternalOperationCompletionDisposition.Completed ? "DeviceComplete" : $"DeviceComplete:{disposition}")]
        };
        Assert.Throws<InvalidOperationException>(() =>
            V6ExternalOperationTraceProjection.ProjectPublishedPrefix(forged, new string('a', 64)));
        ExternalOperationSnapshot current = s.Kernel.QueryExternalOperation(s.Owner, prepared.Operation).Value!;
        Assert.Equal(lost.Disposition, current.Disposition);
        Assert.Equal(lost.Transitions, current.Transitions);
        Assert.False(s.Kernel.RecordExternalOperationVisibility(s.Owner,
            new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        bool action = false;
        Assert.False(s.Kernel.PublishExternalOperation(s.Owner, prepared.Operation, Dependencies,
            new(policy), () => action = true).IsSuccess);
        Assert.False(action);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void PublicationSourceHookReplyCannotCreateOrUndoOwnerFact(int fault)
    {
        Scenario s = CreateScenario();
        MockExternalProvider provider = new MockExternalProvider(s.Kernel, s.Owner);
        OperationPreparation prepared = Prepare(s, ExternalPublicationPolicy.Staged);
        Assert.True(s.Kernel.AdmitExternalOperation(s.Owner, prepared.Operation, Dependencies).IsSuccess);
        OperationBinding binding = provider.Submit(prepared.Operation, Dependencies).Value!;
        Assert.True(provider.Complete(binding, ExternalOperationCompletionDisposition.Completed).IsSuccess);
        ExternalOperationSnapshot visible = provider.MakeVisible(binding, ExternalVisibilityRequirement.PublicationFence, true).Value!;
        Func<KernelResult<ExternalOperationSnapshot>>? delayed = null;
        KernelResult<ExternalOperationSnapshot> result = s.Kernel.PublishExternalOperation(s.Owner, prepared.Operation, Dependencies,
            new(ExternalPublicationPolicy.Staged), _ => s.Output.Span.Fill(0x5A), commit =>
            {
                delayed = commit;
                object gate = s.Kernel.ExternalOperations.GetType().GetField("_gate",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.ExternalOperations)!;
                Assert.False(Monitor.IsEntered(gate));
                if (fault == 0) return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.CapabilityRevoked, "Injected refusal.");
                if (fault == 1) throw new InvalidOperationException("Injected source hook failure.");
                if (fault == 2) return KernelResult<ExternalOperationSnapshot>.Ok(visible);
                if (fault == 6)
                {
                    KernelResult<ExternalOperationSnapshot> crossThread = default;
                    Thread thread = new Thread(() => crossThread = commit());
                    thread.Start();
                    Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
                    Assert.False(crossThread.IsSuccess);
                    return KernelResult<ExternalOperationSnapshot>.Ok(visible);
                }
                KernelResult<ExternalOperationSnapshot> committed = commit();
                Assert.True(committed.IsSuccess);
                if (fault == 3) return KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformFaulted, "Reply lost after commit.");
                if (fault == 5) throw new InvalidOperationException("Reply threw after commit.");
                Assert.False(commit().IsSuccess);
                return KernelResult<ExternalOperationSnapshot>.Ok(visible);
            });
        Assert.Equal(fault is 3 or 4 or 5, result.IsSuccess);
        Assert.False(delayed!().IsSuccess);
        ExternalOperationSnapshot current = s.Kernel.QueryExternalOperation(s.Owner, prepared.Operation).Value!;
        Assert.Equal(0x5A, s.Output.Span[0]);
        if (fault is 3 or 4 or 5)
        {
            Assert.Equal(ExternalOperationState.Published, current.State);
            Assert.Equal(ExternalOperationState.Published, result.Value!.State);
        }
        else
        {
            Assert.Equal(ExternalOperationDisposition.Faulted, current.Disposition);
            Assert.Equal(KernelError.ExternalEffectUncontained, result.Error);
            Assert.False(s.Kernel.ReleaseExternalOperation(s.Owner, prepared.Operation, new(false, false)).IsSuccess);
        }
    }

    private static readonly OperationDependencySnapshot Dependencies = new(7, 11);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DeniedAttachedBudgetReleaseRetainsExactAssociationForExistingRetry(bool teardown, bool virtualContext)
    {
        Scenario s = CreateScenario();
        ProcessHandle admin = TestFixtures.Create(s.Kernel, 3, 30).Handle;
        CapabilityId capability = s.Kernel.MintCapability(new(30), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle account = s.Kernel.AdmitProcessBudget(admin, capability, s.Owner, "release-retention",
            [new(ServiceBudgetDimension.ExternalOperations, 1)]).Value!.ProcessBudget;
        OperationPreparation prepared = Prepare(s, ExternalPublicationPolicy.Staged);
        KernelResult<OperationAdmissionSnapshot> admitted = virtualContext
            ? s.Kernel.AdmitExternalOperationForVirtualContext(s.Owner, prepared.Operation, Dependencies)
            : s.Kernel.AdmitExternalOperation(s.Owner, prepared.Operation, Dependencies);
        Assert.True(admitted.IsSuccess, admitted.Message);
        // Observation of the existing association, not an authority or closure receipt.
        Dictionary<ExternalOperationHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> associations = (Dictionary<ExternalOperationHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)>)
            typeof(RuntimeKernel).GetField("_externalOperationBudgetReservations", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(s.Kernel)!;
        (ProcessHandle Owner, BudgetReservationHandle Reservation) charge = associations[prepared.Operation];
        Assert.Equal(s.Owner, charge.Owner);
        Assert.True(s.Kernel.Budgets.QuarantineLease(s.Owner, charge.Reservation).IsSuccess);
        ExternalOperationHandle stale = prepared.Operation with { Generation = new(prepared.Operation.Generation.Value + 1) };
        Assert.False(s.Kernel.ReleaseExternalOperation(s.Owner, stale, new(false, false)).IsSuccess);
        Assert.Equal(charge, associations[prepared.Operation]);
        if (teardown) Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        else
        {
            Assert.True(s.Kernel.CancelExternalOperation(s.Owner, prepared.Operation, false).IsSuccess);
            Assert.True(s.Kernel.ReleaseExternalOperation(s.Owner, prepared.Operation, new(false, false)).IsSuccess);
            Assert.True(s.Kernel.ReleaseExternalOperation(s.Owner, prepared.Operation, new(false, false)).IsSuccess);
        }
        Assert.Equal(ExternalOperationState.Released, s.Kernel.ExternalOperations.Query(prepared.Operation).Value!.State);
        Assert.Equal(charge, associations[prepared.Operation]);
        Assert.Equal(BudgetReservationState.Quarantined, s.Kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.Contains(s.Kernel.QueryBudget(account).Value!.Usage,
            u => u.Dimension == ServiceBudgetDimension.ExternalOperations && u.Used == 1);
        // No invented settlement or zero-charge reconciliation to make the retry pass.
        Assert.False(s.Kernel.ReleaseBudget(s.Owner, charge.Reservation).IsSuccess);
    }

    [Fact]
    public void SuccessfulAttachedBudgetReleaseRemovesAssociationAndDoesNotRefundTwice()
    {
        Scenario s = CreateScenario();
        ProcessHandle admin = TestFixtures.Create(s.Kernel, 3, 30).Handle;
        CapabilityId capability = s.Kernel.MintCapability(new(30), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle account = s.Kernel.AdmitProcessBudget(admin, capability, s.Owner, "release-success",
            [new(ServiceBudgetDimension.ExternalOperations, 1)]).Value!.ProcessBudget;
        OperationPreparation prepared = Prepare(s, ExternalPublicationPolicy.Staged);
        Assert.True(s.Kernel.AdmitExternalOperation(s.Owner, prepared.Operation, Dependencies).IsSuccess);
        Assert.True(s.Kernel.CancelExternalOperation(s.Owner, prepared.Operation, false).IsSuccess);
        Assert.True(s.Kernel.ReleaseExternalOperation(s.Owner, prepared.Operation, new(false, false)).IsSuccess);
        Assert.True(s.Kernel.ReleaseExternalOperation(s.Owner, prepared.Operation, new(false, false)).IsSuccess);
        Dictionary<ExternalOperationHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> associations = (Dictionary<ExternalOperationHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)>)
            typeof(RuntimeKernel).GetField("_externalOperationBudgetReservations", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(s.Kernel)!;
        Assert.Empty(associations);
        Assert.Contains(s.Kernel.QueryBudget(account).Value!.Usage,
            u => u.Dimension == ServiceBudgetDimension.ExternalOperations && u.Used == 0);
    }

    [Theory]
    [InlineData(-1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(int.MaxValue, false, false)]
    [InlineData(-1, true, false)]
    [InlineData(2, true, false)]
    [InlineData(int.MaxValue, true, false)]
    [InlineData(-1, false, true)]
    [InlineData(2, false, true)]
    [InlineData(int.MaxValue, false, true)]
    [InlineData(-1, true, true)]
    [InlineData(2, true, true)]
    [InlineData(int.MaxValue, true, true)]
    public void UnknownCancellationSupportCannotMutateScopeBudgetOrRegionAdmission(
        int unknown, bool virtualContext, bool requested)
    {
        Scenario s = CreateScenario();
        OperationPreparation prepared = Prepare(s, ExternalPublicationPolicy.Staged);
        ProcessHandle admin = TestFixtures.Create(s.Kernel, 3, 30).Handle;
        CapabilityId capability = s.Kernel.MintCapability(new(30), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle account = s.Kernel.AdmitProcessBudget(admin, capability, s.Owner, "unknown-cancellation",
            [new(ServiceBudgetDimension.ExternalOperations, 1)]).Value!.ProcessBudget;
        CancellationScopeHandle scope = s.Kernel.CreateCancellationScope(s.Owner).Value!.Scope;
        if (requested) Assert.True(s.Kernel.RequestCancellation(s.Owner, scope).IsSuccess);
        CancellationObservation beforeScope = s.Kernel.ObserveCancellation(s.Owner, scope).Value!;
        BudgetAccountSnapshot beforeBudget = s.Kernel.QueryBudget(account).Value!;
        KernelResult<OperationAdmissionSnapshot> result = virtualContext
            ? s.Kernel.AdmitExternalOperationForVirtualContext(s.Owner, prepared.Operation, Dependencies,
                cancellationSupport: (ExternalCancellationSupport)unknown, cancellationScope: scope)
            : s.Kernel.AdmitExternalOperation(s.Owner, prepared.Operation, Dependencies,
                cancellationSupport: (ExternalCancellationSupport)unknown, cancellationScope: scope);
        Assert.Equal(KernelError.InvalidTransition, result.Error);
        ExternalOperationSnapshot after = s.Kernel.QueryExternalOperation(s.Owner, prepared.Operation).Value!;
        Assert.Equal(ExternalOperationState.Prepared, after.State);
        Assert.Equal(ExternalOperationDisposition.Active, after.Disposition);
        Assert.Null(after.Admission);
        Assert.Single(after.Transitions);
        Assert.Empty(s.Kernel.Regions.SnapshotUses());
        Assert.Equal(beforeScope, s.Kernel.ObserveCancellation(s.Owner, scope).Value!);
        Assert.Equal(beforeBudget.Usage, s.Kernel.QueryBudget(account).Value!.Usage);
        KernelResult<OperationAdmissionSnapshot> known = virtualContext
            ? s.Kernel.AdmitExternalOperationForVirtualContext(s.Owner, prepared.Operation, Dependencies,
                cancellationSupport: ExternalCancellationSupport.ProviderCooperative, cancellationScope: scope)
            : s.Kernel.AdmitExternalOperation(s.Owner, prepared.Operation, Dependencies,
                cancellationSupport: ExternalCancellationSupport.ProviderCooperative, cancellationScope: scope);
        if (requested) Assert.Equal(KernelError.DeadlineExpired, known.Error);
        else
        {
            Assert.True(known.IsSuccess, known.Message);
            Assert.Equal(ExternalCancellationSupport.ProviderCooperative, known.Value!.CancellationSupport);
            Assert.Contains(s.Kernel.QueryBudget(account).Value!.Usage,
                u => u.Dimension == ServiceBudgetDimension.ExternalOperations && u.Used == 1);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void OwnerAdmissionRejectsUnknownCancellationSupportBeforeUseAcquisition(int unknown)
    {
        Scenario s = CreateScenario();
        OperationPreparation prepared = Prepare(s, ExternalPublicationPolicy.Staged);
        Assert.Equal(KernelError.InvalidTransition, s.Kernel.ExternalOperations.Admit(
            prepared.Operation, Dependencies, cancellationSupport: (ExternalCancellationSupport)unknown).Error);
        Assert.Equal(KernelError.InvalidTransition, s.Kernel.ExternalOperations.AdmitForExactPlatformMappings(
            prepared.Operation, Dependencies, cancellationSupport: (ExternalCancellationSupport)unknown).Error);
        ExternalOperationSnapshot after = s.Kernel.QueryExternalOperation(s.Owner, prepared.Operation).Value!;
        Assert.Equal(ExternalOperationState.Prepared, after.State);
        Assert.Null(after.Admission);
        Assert.Single(after.Transitions);
        Assert.Empty(s.Kernel.Regions.SnapshotUses());
        Assert.True(s.Kernel.AdmitExternalOperation(s.Owner, prepared.Operation, Dependencies,
            cancellationSupport: ExternalCancellationSupport.BeforeSubmissionOnly).IsSuccess);
    }

    [Fact]
    public void MockNonCxlOperationTraversesAllSevenStatesWithoutConflation()
    {
        Scenario scenario = CreateScenario();
        MockExternalProvider provider = new MockExternalProvider(scenario.Kernel, scenario.Owner);
        OperationPreparation preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        Assert.Equal(ExternalOperationState.Prepared, scenario.Kernel.QueryExternalOperation(scenario.Owner, preparation.Operation).Value!.State);

        KernelResult<OperationAdmissionSnapshot> admission = scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies);
        Assert.True(admission.IsSuccess, admission.Message);
        Assert.Equal(ExternalOperationState.Admitted, scenario.Kernel.QueryExternalOperation(scenario.Owner, preparation.Operation).Value!.State);

        KernelResult<OperationBinding> binding = provider.Submit(preparation.Operation, Dependencies);
        Assert.True(binding.IsSuccess, binding.Message);
        Assert.Equal(ExternalOperationState.Submitted, scenario.Kernel.QueryExternalOperation(scenario.Owner, preparation.Operation).Value!.State);

        KernelResult<ExternalOperationSnapshot> completion = provider.Complete(binding.Value!, ExternalOperationCompletionDisposition.Completed);
        Assert.True(completion.IsSuccess, completion.Message);
        Assert.Equal(ExternalOperationState.DeviceComplete, completion.Value!.State);
        Assert.Equal(0, scenario.Output.Span[0]);

        KernelResult<ExternalOperationSnapshot> visible = provider.MakeVisible(binding.Value!, ExternalVisibilityRequirement.PublicationFence, satisfied: true);
        Assert.True(visible.IsSuccess, visible.Message);
        Assert.Equal(ExternalOperationState.Visible, visible.Value!.State);
        Assert.Equal(0, scenario.Output.Span[0]);

        KernelResult<ExternalOperationSnapshot> published = scenario.Kernel.PublishExternalOperation(
            scenario.Owner,
            preparation.Operation,
            Dependencies,
            new PublicationPlan(ExternalPublicationPolicy.Staged),
            () => scenario.Output.Span[0] = 42);
        Assert.True(published.IsSuccess, published.Message);
        Assert.Equal(ExternalOperationState.Published, published.Value!.State);
        Assert.Equal(42, scenario.Output.Span[0]);

        KernelResult<ExternalOperationSnapshot> released = scenario.Kernel.ReleaseExternalOperation(
            scenario.Owner,
            preparation.Operation,
            new ReleasePlan(ProviderResourcesClosed: true, ProviderUnavailable: false));
        Assert.True(released.IsSuccess, released.Message);
        Assert.Equal(ExternalOperationState.Released, released.Value!.State);
        Assert.Equal(ExternalOperationDisposition.Published, released.Value.Disposition);
        Assert.Equal(
            new[] { "Prepared", "Admitted", "Submitted", "DeviceComplete", "Visible", "Published", "Released" },
            released.Value.Transitions.Select(transition => transition.Event));
        Assert.True(scenario.Output.IsValid);
    }

    [Fact]
    public void MultiRegionReleaseFailureLeavesEveryUsePinned()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        Assert.True(scenario.Kernel.AdmitExternalOperation(
            scenario.Owner, preparation.Operation, Dependencies).IsSuccess);
        Assert.True(scenario.Kernel.CancelExternalOperation(
            scenario.Owner, preparation.Operation, false).IsSuccess);

        object index = typeof(RegionAuthority).GetField("_regions",
            global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.GetValue(scenario.Kernel.Regions)!;
        IEnumerable records = (global::System.Collections.IEnumerable)index.GetType()
            .GetProperty("Values")!.GetValue(index)!;
        object outputRecord = Assert.Single(records.Cast<object>(), record =>
            (RegionId)record.GetType().GetProperty("Id")!.GetValue(record)! ==
            scenario.Output.Handle.RegionId);
        outputRecord.GetType().GetProperty("MutationEpoch")!.SetValue(outputRecord,
            new MutationEpoch(ulong.MaxValue));

        KernelResult<ExternalOperationSnapshot> release = scenario.Kernel.ReleaseExternalOperation(
            scenario.Owner, preparation.Operation, new(false, false));

        Assert.Equal(KernelError.CapacityExhausted, release.Error);
        Assert.Equal(2, scenario.Kernel.Regions.SnapshotUses().Count(use =>
            use.State == RegionUseState.Active));
        Assert.Equal(ExternalOperationState.Admitted,
            scenario.Kernel.QueryExternalOperation(scenario.Owner, preparation.Operation).Value!.State);
    }

    [Fact]
    public void FailedAdmissionCleanupRetainsAcquiredUseUnderOperationOwner()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation prepared = scenario.Kernel.PrepareExternalOperation(scenario.Owner,
            [
                new OperationRegionUseRequest(scenario.Output.Handle,
                    RegionUseMode.StagedOutput, new(0, 8)),
                new OperationRegionUseRequest(scenario.Input.Handle with
                {
                    Generation = new RegionGeneration(999)
                }, RegionUseMode.ReadOnly, new(0, 8))
            ], ExternalVisibilityRequirement.PublicationFence,
            ExternalPublicationPolicy.Staged).Value!;
        object index = typeof(RegionAuthority).GetField("_regions",
            global::System.Reflection.BindingFlags.Instance |
            global::System.Reflection.BindingFlags.NonPublic)!.GetValue(scenario.Kernel.Regions)!;
        IEnumerable records = (global::System.Collections.IEnumerable)index.GetType()
            .GetProperty("Values")!.GetValue(index)!;
        object outputRecord = Assert.Single(records.Cast<object>(), record =>
            (RegionId)record.GetType().GetProperty("Id")!.GetValue(record)! ==
            scenario.Output.Handle.RegionId);
        outputRecord.GetType().GetProperty("MutationEpoch")!.SetValue(outputRecord,
            new MutationEpoch(ulong.MaxValue - 1));

        KernelResult<OperationAdmissionSnapshot> admission = scenario.Kernel.AdmitExternalOperation(
            scenario.Owner, prepared.Operation, Dependencies);

        Assert.Equal(KernelError.CapacityExhausted, admission.Error);
        ExternalOperationSnapshot operation = scenario.Kernel.QueryExternalOperation(
            scenario.Owner, prepared.Operation).Value!;
        Assert.Equal(ExternalOperationState.Prepared, operation.State);
        Assert.Equal(ExternalOperationDisposition.Cancelled, operation.Disposition);
        Assert.Single(operation.Admission!.RegionUses);
        Assert.Equal(RegionUseState.Active,
            Assert.Single(scenario.Kernel.Regions.SnapshotUses()).State);
        Assert.Equal(KernelError.InvalidTransition,
            scenario.Kernel.RecordExternalOperationSubmission(
                scenario.Owner, prepared.Operation, Dependencies).Error);
        Assert.Equal(KernelError.CapacityExhausted,
            scenario.Kernel.ReleaseExternalOperation(
                scenario.Owner, prepared.Operation, new(false, false)).Error);
    }

    [Fact]
    public void FailedAdmissionWithSuccessfulCleanupLeavesNoRegionUse()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation prepared = scenario.Kernel.PrepareExternalOperation(scenario.Owner,
            [
                new OperationRegionUseRequest(scenario.Output.Handle,
                    RegionUseMode.StagedOutput, new(0, 8)),
                new OperationRegionUseRequest(scenario.Input.Handle with
                {
                    Generation = new RegionGeneration(999)
                }, RegionUseMode.ReadOnly, new(0, 8))
            ], ExternalVisibilityRequirement.PublicationFence,
            ExternalPublicationPolicy.Staged).Value!;

        KernelResult<OperationAdmissionSnapshot> admission = scenario.Kernel.AdmitExternalOperation(
            scenario.Owner, prepared.Operation, Dependencies);

        Assert.Equal(KernelError.StaleGeneration, admission.Error);
        Assert.Equal(RegionUseState.Released,
            Assert.Single(scenario.Kernel.Regions.SnapshotUses()).State);
        ExternalOperationSnapshot operation = scenario.Kernel.QueryExternalOperation(
            scenario.Owner, prepared.Operation).Value!;
        Assert.Equal(ExternalOperationState.Prepared, operation.State);
        Assert.Equal(ExternalOperationDisposition.Active, operation.Disposition);
        Assert.Null(operation.Admission);
    }

    [Fact]
    public void SubmitWithoutAdmissionAndAllSuccessfulStateSkipsFailClosed()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);

        AssertInvalid(scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies));
        OperationBinding unsubmittedBinding = new OperationBinding(preparation.Operation, new OperationBindingId(1), 1);
        AssertInvalid(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner, new(unsubmittedBinding, ExternalOperationCompletionDisposition.Completed)));
        AssertInvalid(scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner, new(unsubmittedBinding, ExternalVisibilityRequirement.PublicationFence, true)));
        AssertInvalid(scenario.Kernel.PublishExternalOperation(scenario.Owner, preparation.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), () => { }));
        AssertInvalid(scenario.Kernel.ReleaseExternalOperation(scenario.Owner, preparation.Operation, new(true, false)));

        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies).IsSuccess);
        AssertInvalid(scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies));
        AssertInvalid(scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner, new(unsubmittedBinding, ExternalVisibilityRequirement.PublicationFence, true)));
        AssertInvalid(scenario.Kernel.PublishExternalOperation(scenario.Owner, preparation.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), () => { }));
        AssertInvalid(scenario.Kernel.ReleaseExternalOperation(scenario.Owner, preparation.Operation, new(true, false)));

        OperationBinding binding = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies).Value!;
        AssertInvalid(scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies));
        AssertInvalid(scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner, new(binding, ExternalVisibilityRequirement.PublicationFence, true)));
        AssertInvalid(scenario.Kernel.PublishExternalOperation(scenario.Owner, preparation.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), () => { }));
        AssertInvalid(scenario.Kernel.ReleaseExternalOperation(scenario.Owner, preparation.Operation, new(true, false)));

        Assert.True(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner, new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        AssertInvalid(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner, new(binding, ExternalOperationCompletionDisposition.Completed)));
        AssertInvalid(scenario.Kernel.PublishExternalOperation(scenario.Owner, preparation.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), () => { }));

        Assert.True(scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner, new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        AssertInvalid(scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner, new(binding, ExternalVisibilityRequirement.PublicationFence, true)));
        AssertInvalid(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner, new(binding, ExternalOperationCompletionDisposition.Completed)));

        Assert.True(scenario.Kernel.PublishExternalOperation(scenario.Owner, preparation.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), () => { }).IsSuccess);
        AssertInvalid(scenario.Kernel.PublishExternalOperation(scenario.Owner, preparation.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), () => { }));
        AssertInvalid(scenario.Kernel.CancelExternalOperation(scenario.Owner, preparation.Operation, true));

        Assert.True(scenario.Kernel.ReleaseExternalOperation(scenario.Owner, preparation.Operation, new(true, false)).IsSuccess);
        AssertInvalid(scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies));
        AssertInvalid(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner, new(binding, ExternalOperationCompletionDisposition.Completed)));
        AssertInvalid(scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner, new(binding, ExternalVisibilityRequirement.PublicationFence, true)));
        AssertInvalid(scenario.Kernel.PublishExternalOperation(scenario.Owner, preparation.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), () => { }));
        Assert.True(scenario.Kernel.ReleaseExternalOperation(scenario.Owner, preparation.Operation, new(true, false)).IsSuccess);
    }

    [Fact]
    public void WrongAndStaleCompletionNeverAdvanceTheExactOperation()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies).IsSuccess);
        OperationBinding binding = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies).Value!;

        ExternalOperationHandle staleOperation = preparation.Operation with { Generation = new OperationGeneration(99) };
        KernelResult<ExternalOperationSnapshot> stale = scenario.Kernel.ExternalOperations.RecordCompletion(new(new OperationBinding(staleOperation, binding.BindingId, binding.Generation), ExternalOperationCompletionDisposition.Completed));
        Assert.False(stale.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);

        OperationBinding wrongBinding = binding with { BindingId = new OperationBindingId(binding.BindingId.Value + 1) };
        AssertInvalid(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner, new(wrongBinding, ExternalOperationCompletionDisposition.Completed)));
        Assert.Equal(ExternalOperationState.Submitted, scenario.Kernel.QueryExternalOperation(scenario.Owner, preparation.Operation).Value!.State);
    }

    [Fact]
    public void MutationBetweenVisibilityAndPublishDiscardsStagedResult()
    {
        Scenario scenario = CreateScenario();
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(scenario.Kernel, 2, 20);
        OperationPreparation preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies).IsSuccess);
        OperationBinding binding = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies).Value!;
        Assert.True(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner, new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner, new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        Assert.True(scenario.Kernel.TransferRegion(scenario.Owner, target, scenario.Input).IsSuccess);
        bool publicationRan = false;

        KernelResult<ExternalOperationSnapshot> publish = scenario.Kernel.PublishExternalOperation(
            scenario.Owner,
            preparation.Operation,
            Dependencies,
            new(ExternalPublicationPolicy.Staged),
            () => publicationRan = true);

        Assert.False(publish.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, publish.Error);
        Assert.False(publicationRan);
        Assert.Equal(ExternalOperationDisposition.Discarded, scenario.Kernel.QueryExternalOperation(scenario.Owner, preparation.Operation).Value!.Disposition);
    }

    [Fact]
    public void VisibilityFailureCanNeverPublish()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies).IsSuccess);
        OperationBinding binding = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies).Value!;
        Assert.True(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner, new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);

        KernelResult<ExternalOperationSnapshot> visibility = scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner, new(binding, ExternalVisibilityRequirement.PublicationFence, false));
        bool publicationRan = false;
        KernelResult<ExternalOperationSnapshot> publish = scenario.Kernel.PublishExternalOperation(scenario.Owner, preparation.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), () => publicationRan = true);

        Assert.False(visibility.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, visibility.Error);
        Assert.False(publish.IsSuccess);
        Assert.False(publicationRan);
    }

    [Fact]
    public void DependencyGenerationIsCheckedBeforeSubmitAndPublish()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies).IsSuccess);
        OperationDependencySnapshot staleDependencies = Dependencies with { DeviceGeneration = 12 };
        KernelResult<OperationBinding> staleSubmit = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, staleDependencies);
        Assert.False(staleSubmit.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, staleSubmit.Error);

        OperationBinding binding = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies).Value!;
        Assert.True(scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner, new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(scenario.Kernel.RecordExternalOperationVisibility(scenario.Owner, new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        KernelResult<ExternalOperationSnapshot> stalePublish = scenario.Kernel.PublishExternalOperation(scenario.Owner, preparation.Operation, staleDependencies, new(ExternalPublicationPolicy.Staged), () => Assert.Fail("stale publication ran"));
        Assert.False(stalePublish.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, stalePublish.Error);
    }

    [Fact]
    public void CancellationRulesAreExplicitBeforeAndAfterSubmission()
    {
        Scenario beforeSubmit = CreateScenario();
        OperationPreparation prepared = Prepare(beforeSubmit, ExternalPublicationPolicy.Staged);
        Assert.True(beforeSubmit.Kernel.CancelExternalOperation(beforeSubmit.Owner, prepared.Operation, false).IsSuccess);
        Assert.True(beforeSubmit.Kernel.ReleaseExternalOperation(beforeSubmit.Owner, prepared.Operation, new(false, false)).IsSuccess);

        Scenario afterSubmit = CreateScenario();
        OperationPreparation submitted = Prepare(afterSubmit, ExternalPublicationPolicy.Staged);
        Assert.True(afterSubmit.Kernel.AdmitExternalOperation(afterSubmit.Owner, submitted.Operation, Dependencies).IsSuccess);
        OperationBinding binding = afterSubmit.Kernel.RecordExternalOperationSubmission(afterSubmit.Owner, submitted.Operation, Dependencies).Value!;
        KernelResult<ExternalOperationSnapshot> cancellation = afterSubmit.Kernel.CancelExternalOperation(afterSubmit.Owner, submitted.Operation, providerCancellationSupported: false);
        Assert.True(cancellation.IsSuccess);
        Assert.Equal(ExternalOperationDisposition.CancellationPending, cancellation.Value!.Disposition);
        AssertInvalid(afterSubmit.Kernel.ReleaseExternalOperation(afterSubmit.Owner, submitted.Operation, new(false, false)));
        Assert.True(afterSubmit.Kernel.RecordExternalOperationCompletion(afterSubmit.Owner, new(binding, ExternalOperationCompletionDisposition.Cancelled)).IsSuccess);
        Assert.True(afterSubmit.Kernel.ReleaseExternalOperation(afterSubmit.Owner, submitted.Operation, new(true, false)).IsSuccess);

        Scenario direct = CreateScenario();
        OperationPreparation directPrepared = Prepare(direct, ExternalPublicationPolicy.DirectCoherent);
        Assert.True(direct.Kernel.AdmitExternalOperation(direct.Owner, directPrepared.Operation, Dependencies).IsSuccess);
        OperationBinding directBinding = direct.Kernel.RecordExternalOperationSubmission(direct.Owner, directPrepared.Operation, Dependencies).Value!;
        Assert.True(direct.Kernel.RecordExternalOperationCompletion(direct.Owner, new(directBinding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        KernelResult<ExternalOperationSnapshot> directCancel = direct.Kernel.CancelExternalOperation(direct.Owner, directPrepared.Operation, true);
        Assert.True(directCancel.IsSuccess);
        Assert.Equal(ExternalOperationDisposition.Faulted, directCancel.Value!.Disposition);
        AssertInvalid(direct.Kernel.ReleaseExternalOperation(direct.Owner, directPrepared.Operation, new(true, false)));
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(direct.Kernel, 2, 20);
        KernelResult<OwnedBuffer<byte>> transfer = direct.Kernel.TransferRegion(direct.Owner, target, direct.Output);
        Assert.False(transfer.IsSuccess);
        Assert.Equal(KernelError.RegionUseConflict, transfer.Error);
    }

    [Fact]
    public void ProviderLossRequiresExplicitClosureOrContainmentBeforeLocalRelease()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies).IsSuccess);
        _ = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies).Value!;

        KernelResult<ExternalOperationSnapshot> lost = scenario.Kernel.RecordExternalOperationProviderLoss(scenario.Owner, preparation.Operation);
        Assert.True(lost.IsSuccess, lost.Message);
        Assert.Equal(ExternalOperationDisposition.ProviderLost, lost.Value!.Disposition);
        KernelResult<ExternalOperationSnapshot> unavailableOnly = scenario.Kernel.ReleaseExternalOperation(scenario.Owner, preparation.Operation, new(false, true));
        Assert.False(unavailableOnly.IsSuccess);
        Assert.Equal(KernelError.InvalidTransition, unavailableOnly.Error);
        KernelResult<ExternalOperationSnapshot> untrustedContainment = scenario.Kernel.ReleaseExternalOperation(scenario.Owner,
            preparation.Operation, new(false, true, ProviderEffectContained: true));
        Assert.False(untrustedContainment.IsSuccess);
        Assert.Equal(KernelError.InvalidTransition, untrustedContainment.Error);
        KernelResult<ExternalOperationSnapshot> released = scenario.Kernel.ReleaseExternalOperation(scenario.Owner, preparation.Operation, new(true, true));
        Assert.True(released.IsSuccess, released.Message);
        Assert.True(scenario.Kernel.ReleaseExternalOperation(scenario.Owner, preparation.Operation, new(true, true)).IsSuccess);
        Assert.DoesNotContain(released.Value!.Transitions, transition => transition.Event == "Published");
        Assert.True(scenario.Kernel.Regions.Validate(scenario.Output.Handle, new(new DomainId(10), scenario.Owner.Generation)).IsSuccess);
    }

    [Theory]
    [InlineData(ExternalPublicationPolicy.Staged, ExternalOperationDisposition.ProviderLost)]
    [InlineData(ExternalPublicationPolicy.DirectCoherent, ExternalOperationDisposition.Faulted)]
    public void LateCancellationCannotEraseProviderLossConsequence(
        ExternalPublicationPolicy policy, ExternalOperationDisposition consequence)
    {
        Scenario scenario = CreateScenario();
        OperationPreparation preparation = Prepare(scenario, policy);
        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner,
            preparation.Operation, Dependencies).IsSuccess);
        _ = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner,
            preparation.Operation, Dependencies).Value!;
        Assert.True(scenario.Kernel.RecordExternalOperationProviderLoss(scenario.Owner,
            preparation.Operation).IsSuccess);

        KernelResult<ExternalOperationSnapshot> cancelled = scenario.Kernel.CancelExternalOperation(scenario.Owner,
            preparation.Operation, providerCancellationSupported: true);

        Assert.True(cancelled.IsSuccess, cancelled.Message);
        Assert.Equal(consequence, cancelled.Value!.Disposition);
        Assert.Equal(consequence,
            scenario.Kernel.QueryExternalOperation(scenario.Owner,
                preparation.Operation).Value!.Disposition);
        Assert.Equal(KernelError.InvalidTransition,
            scenario.Kernel.ReleaseExternalOperation(scenario.Owner,
                preparation.Operation, new(false, true)).Error);
    }

    [Fact]
    public void ProviderLossInvalidatesWritableUseAtTerminalMutationEpochWithoutReclaim()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner,
            preparation.Operation, Dependencies).IsSuccess);
        _ = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner,
            preparation.Operation, Dependencies).Value!;
        RegionUseDescriptor use = scenario.Kernel.QueryExternalOperation(scenario.Owner,
            preparation.Operation).Value!.Admission!.RegionUses.Single(candidate =>
            candidate.Region == scenario.Output.Handle);
        object regions = typeof(RegionAuthority).GetField("_regions",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.Regions)!;
        IEnumerable records = (global::System.Collections.IEnumerable)regions.GetType()
            .GetProperty("Values")!.GetValue(regions)!;
        object record = records.Cast<object>().Single(candidate =>
            Equals(candidate.GetType().GetProperty("Id")!.GetValue(candidate),
                scenario.Output.Handle.RegionId));
        record.GetType().GetProperty("MutationEpoch")!.SetValue(record,
            new MutationEpoch(ulong.MaxValue));
        RegionOwner principal = scenario.Kernel.QueryExternalOperation(scenario.Owner,
            preparation.Operation).Value!.Admission!.Principal;
        Assert.Equal(KernelError.CapacityExhausted,
            scenario.Kernel.Regions.InvalidateUse(use.Handle, principal).Error);

        KernelResult<ExternalOperationSnapshot> lost = scenario.Kernel.RecordExternalOperationProviderLoss(scenario.Owner,
            preparation.Operation);

        Assert.True(lost.IsSuccess, lost.Message);
        Assert.Equal(ExternalOperationDisposition.ProviderLost, lost.Value!.Disposition);
        Assert.Equal(RegionUseState.Invalidated,
            scenario.Kernel.Regions.SnapshotUses().Single(candidate =>
                candidate.Handle == use.Handle).State);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Kernel.ValidateRegionUse(scenario.Owner, use.Handle).Error);
        Assert.Equal(ulong.MaxValue,
            scenario.Kernel.Regions.Snapshot().Single(candidate =>
                candidate.Handle == scenario.Output.Handle).MutationEpoch.Value);
        Assert.Equal(KernelError.RegionUseConflict,
            scenario.Kernel.AcquireRegionUse(scenario.Owner, scenario.Output.Handle,
                RegionUseMode.ReadOnly, use.Range).Error);
        Assert.Equal(KernelError.CapacityExhausted,
            scenario.Kernel.ReleaseExternalOperation(scenario.Owner, preparation.Operation,
                new(ProviderResourcesClosed: true, ProviderUnavailable: true)).Error);
    }

    [Fact]
    public void ProviderLossWithBrokenRegionUseBindingReportsFailureAndRetainsOwnerPin()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner,
            preparation.Operation, Dependencies).IsSuccess);
        _ = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner,
            preparation.Operation, Dependencies).Value!;
        RegionUseDescriptor outputUse = scenario.Kernel.QueryExternalOperation(scenario.Owner,
            preparation.Operation).Value!.Admission!.RegionUses.Single(candidate =>
            candidate.Region == scenario.Output.Handle);
        object regions = typeof(RegionAuthority).GetField("_regions",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel.Regions)!;
        IEnumerable records = (global::System.Collections.IEnumerable)regions.GetType()
            .GetProperty("Values")!.GetValue(regions)!;
        object record = records.Cast<object>().Single(candidate =>
            Equals(candidate.GetType().GetProperty("Id")!.GetValue(candidate),
                scenario.Output.Handle.RegionId));
        object uses = record.GetType().GetProperty("Uses")!.GetValue(record)!;
        IEnumerable useRecords = (global::System.Collections.IEnumerable)uses.GetType()
            .GetProperty("Values")!.GetValue(uses)!;
        object storedUse = useRecords.Cast<object>().Single(candidate =>
            Equals(candidate.GetType().GetProperty("Handle")!.GetValue(candidate),
                outputUse.Handle));
        storedUse.GetType().GetProperty("ReleaseOwnerOperation")!.SetValue(storedUse,
            new ExternalOperationHandle(new ExternalOperationId(999), new OperationGeneration(1)));

        KernelResult<ExternalOperationSnapshot> lost = scenario.Kernel.RecordExternalOperationProviderLoss(scenario.Owner,
            preparation.Operation);

        Assert.Equal(KernelError.ExternalEffectUncontained, lost.Error);
        Assert.Equal(ExternalOperationDisposition.ProviderLost,
            scenario.Kernel.QueryExternalOperation(scenario.Owner,
                preparation.Operation).Value!.Disposition);
        Assert.Equal(KernelError.InvalidTransition,
            scenario.Kernel.ReleaseExternalOperation(scenario.Owner, preparation.Operation,
                new(ProviderResourcesClosed: true, ProviderUnavailable: true)).Error);
    }

    [Fact]
    public void CallerCancellationFlagCannotUpgradeStoredAdmissionSupport()
    {
        Scenario beforeDispatchOnly = CreateScenario();
        OperationPreparation first = Prepare(beforeDispatchOnly, ExternalPublicationPolicy.Staged);
        Assert.True(beforeDispatchOnly.Kernel.AdmitExternalOperation(
            beforeDispatchOnly.Owner, first.Operation, Dependencies,
            cancellationSupport: ExternalCancellationSupport.BeforeSubmissionOnly).IsSuccess);
        _ = beforeDispatchOnly.Kernel.RecordExternalOperationSubmission(
            beforeDispatchOnly.Owner, first.Operation, Dependencies).Value!;

        ExternalOperationSnapshot firstCancellation = beforeDispatchOnly.Kernel.CancelExternalOperation(
            beforeDispatchOnly.Owner, first.Operation, providerCancellationSupported: true).Value!;
        Assert.Equal("DrainRequired", firstCancellation.Transitions[^1].Event);

        Scenario cooperative = CreateScenario();
        OperationPreparation second = Prepare(cooperative, ExternalPublicationPolicy.Staged);
        Assert.True(cooperative.Kernel.AdmitExternalOperation(
            cooperative.Owner, second.Operation, Dependencies,
            cancellationSupport: ExternalCancellationSupport.ProviderCooperative).IsSuccess);
        _ = cooperative.Kernel.RecordExternalOperationSubmission(
            cooperative.Owner, second.Operation, Dependencies).Value!;

        ExternalOperationSnapshot secondCancellation = cooperative.Kernel.CancelExternalOperation(
            cooperative.Owner, second.Operation, providerCancellationSupported: false).Value!;
        Assert.Equal("ProviderCancellationRequested", secondCancellation.Transitions[^1].Event);
    }

    [Fact]
    public async Task CancelAndRetireRaceLinearizesWithoutPublicationOrRelease()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation prepared = Prepare(scenario, ExternalPublicationPolicy.Staged);
        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner, prepared.Operation, Dependencies,
            cancellationSupport: ExternalCancellationSupport.ProviderCooperative).IsSuccess);
        OperationBinding binding = scenario.Kernel.RecordExternalOperationSubmission(
            scenario.Owner, prepared.Operation, Dependencies).Value!;
        using ManualResetEventSlim start = new ManualResetEventSlim(false);
        Task<KernelResult<ExternalOperationSnapshot>> cancel = Task.Run(() =>
        {
            start.Wait();
            return scenario.Kernel.CancelExternalOperation(scenario.Owner, prepared.Operation, true);
        });
        Task<KernelResult<ExternalOperationSnapshot>> retire = Task.Run(() =>
        {
            start.Wait();
            return scenario.Kernel.RecordExternalOperationCompletion(scenario.Owner,
                new(binding, ExternalOperationCompletionDisposition.Cancelled));
        });
        start.Set();
        KernelResult<ExternalOperationSnapshot>[] results = await Task.WhenAll(cancel, retire);

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Message));
        ExternalOperationSnapshot final = scenario.Kernel.QueryExternalOperation(scenario.Owner, prepared.Operation).Value!;
        Assert.Equal(ExternalOperationState.DeviceComplete, final.State);
        Assert.Contains(final.Disposition,
            new[] { ExternalOperationDisposition.Cancelled, ExternalOperationDisposition.Discarded });
        Assert.DoesNotContain(final.Transitions, transition => transition.Event is "Published" or "Released");
        Assert.Equal(KernelError.InvalidTransition,
            scenario.Kernel.ReleaseExternalOperation(scenario.Owner, prepared.Operation, new(false, false)).Error);
    }

    [Fact]
    public void ProcessTeardownCancelsPreSubmitWorkAndDrainsAcceptedWork()
    {
        Scenario preSubmit = CreateScenario();
        OperationPreparation admitted = Prepare(preSubmit, ExternalPublicationPolicy.Staged);
        Assert.True(preSubmit.Kernel.AdmitExternalOperation(preSubmit.Owner, admitted.Operation, Dependencies).IsSuccess);
        Assert.True(preSubmit.Kernel.TerminateProcess(preSubmit.Owner).IsSuccess);
        Assert.Equal(ExternalOperationState.Released, preSubmit.Kernel.ExternalOperations.Query(admitted.Operation).Value!.State);

        Scenario accepted = CreateScenario();
        OperationPreparation submitted = Prepare(accepted, ExternalPublicationPolicy.Staged);
        Assert.True(accepted.Kernel.AdmitExternalOperation(accepted.Owner, submitted.Operation, Dependencies).IsSuccess);
        OperationBinding binding = accepted.Kernel.RecordExternalOperationSubmission(accepted.Owner, submitted.Operation, Dependencies).Value!;
        KernelResult firstTeardown = accepted.Kernel.TerminateProcess(accepted.Owner);
        Assert.False(firstTeardown.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, firstTeardown.Error);
        Assert.True(accepted.Kernel.RecordExternalOperationCompletion(accepted.Owner, new(binding, ExternalOperationCompletionDisposition.Cancelled)).IsSuccess);
        KernelResult completionOnly = accepted.Kernel.TerminateProcess(accepted.Owner);
        Assert.False(completionOnly.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, completionOnly.Error);
        Assert.True(accepted.Kernel.ReleaseExternalOperation(accepted.Owner, submitted.Operation, new(true, false)).IsSuccess);
        Assert.True(accepted.Kernel.TerminateProcess(accepted.Owner).IsSuccess);
        Assert.Equal(ExternalOperationState.Released, accepted.Kernel.ExternalOperations.Query(submitted.Operation).Value!.State);
    }

    [Fact]
    public void PublicLifecycleSurfaceContainsNoCxlOrRawProviderIdentity()
    {
        string[] forbidden = new[] { "Cxl", "Hdm", "Dpa", "Hpa", "Pasid", "Requester", "PhysicalAddress", "FabricRoute", "FabricPort", "ProviderToken" };
        string[] names = typeof(ExternalOperationHandle).Assembly.GetExportedTypes()
            .Where(type => type.Name.Contains("ExternalOperation", StringComparison.Ordinal) || type.Name.StartsWith("Operation", StringComparison.Ordinal) || type == typeof(PublicationPlan) || type == typeof(ReleasePlan))
            .SelectMany(type => new[] { type.Name }
                .Concat(type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(property => property.Name)))
            .ToArray();

        Assert.NotEmpty(names);
        Assert.DoesNotContain(names, name => forbidden.Any(token => name.Contains(token, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void AdmissionReceiptCarriesOpaqueServicePolicyAndIndependentVersion()
    {
        Scenario scenario = CreateScenario();
        OperationPreparation preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        KernelResult<OperationAdmissionSnapshot> admission = scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies,
            new ExternalServiceIdentity("accelerator-class-a"), ExternalCancellationSupport.ProviderCooperative);

        Assert.True(admission.IsSuccess, admission.Message);
        Assert.Equal(ExternalOperationContract.Version, admission.Value!.ContractVersion);
        Assert.Equal("accelerator-class-a", admission.Value.ServiceIdentity.Value);
        Assert.Equal(ExternalCancellationSupport.ProviderCooperative, admission.Value.CancellationSupport);
        Assert.Equal(preparation.EffectPolicy.EffectClass, admission.Value.EffectClass);
        Assert.Equal(preparation.PublicationPolicy, admission.Value.PublicationPolicy);
    }

    [Fact]
    public void ReceiptVocabularyDistinguishesEveryCrossProjectTerminalOutcome()
    {
        ExternalOperationReceiptStatus[] statuses = Enum.GetValues<ExternalOperationReceiptStatus>();
        Assert.Contains(ExternalOperationReceiptStatus.Submitted, statuses);
        Assert.Contains(ExternalOperationReceiptStatus.DeviceComplete, statuses);
        Assert.Contains(ExternalOperationReceiptStatus.Visible, statuses);
        Assert.Contains(ExternalOperationReceiptStatus.Published, statuses);
        Assert.Contains(ExternalOperationReceiptStatus.Released, statuses);
        Assert.Contains(ExternalOperationReceiptStatus.Failed, statuses);
        Assert.Contains(ExternalOperationReceiptStatus.Stale, statuses);

        Scenario scenario = CreateScenario();
        OperationPreparation preparation = Prepare(scenario, ExternalPublicationPolicy.Staged);
        Assert.True(scenario.Kernel.AdmitExternalOperation(scenario.Owner, preparation.Operation, Dependencies).IsSuccess);
        OperationBinding binding = scenario.Kernel.RecordExternalOperationSubmission(scenario.Owner, preparation.Operation, Dependencies).Value!;
        ExternalOperationReceipt submitted = ExternalOperationReceipts.FromSnapshot(
            scenario.Kernel.QueryExternalOperation(scenario.Owner, preparation.Operation).Value!);
        Assert.Equal(ExternalOperationReceiptStatus.Submitted, submitted.Status);
        Assert.Equal(ExternalOperationContract.Version, submitted.ContractVersion);

        Assert.True(scenario.Kernel.RecordExternalOperationProviderLoss(scenario.Owner, preparation.Operation).IsSuccess);
        ExternalOperationReceipt failed = ExternalOperationReceipts.FromSnapshot(
            scenario.Kernel.QueryExternalOperation(scenario.Owner, preparation.Operation).Value!);
        Assert.Equal(ExternalOperationReceiptStatus.Failed, failed.Status);
        ExternalOperationReceipt stale = ExternalOperationReceipts.Stale(preparation.Operation with { Generation = new(99) });
        Assert.Equal(ExternalOperationReceiptStatus.Stale, stale.Status);
    }

    private static Scenario CreateScenario()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 1, 10);
        return new Scenario(
            kernel,
            owner,
            kernel.AllocateBuffer<byte>(owner, 8).Value!,
            kernel.AllocateBuffer<byte>(owner, 8).Value!);
    }

    private static OperationPreparation Prepare(Scenario scenario, ExternalPublicationPolicy policy)
    {
        KernelResult<OperationPreparation> prepared = scenario.Kernel.PrepareExternalOperation(
            scenario.Owner,
            new[]
            {
                new OperationRegionUseRequest(scenario.Input.Handle, RegionUseMode.ReadOnly, new(0, 8)),
                new OperationRegionUseRequest(scenario.Output.Handle, policy == ExternalPublicationPolicy.Staged ? RegionUseMode.StagedOutput : RegionUseMode.ExclusiveWrite, new(0, 8))
            },
            ExternalVisibilityRequirement.PublicationFence,
            policy,
            policy == ExternalPublicationPolicy.DirectCoherent
                ? new ExternalEffectPolicy(ExternalEffectClass.IrreversibleBarrier, ExternalReplayProtection.None, true)
                : null);
        Assert.True(prepared.IsSuccess, prepared.Message);
        return prepared.Value!;
    }

    private static void AssertInvalid<T>(KernelResult<T> result)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.InvalidTransition, result.Error);
    }

    private sealed record Scenario(
        RuntimeKernel Kernel,
        ProcessHandle Owner,
        OwnedBuffer<byte> Input,
        OwnedBuffer<byte> Output);

    private sealed class MockExternalProvider(RuntimeKernel kernel, ProcessHandle principal)
    {
        public KernelResult<OperationBinding> Submit(ExternalOperationHandle operation, OperationDependencySnapshot dependencies) =>
            kernel.RecordExternalOperationSubmission(principal, operation, dependencies);

        public KernelResult<ExternalOperationSnapshot> Complete(OperationBinding binding, ExternalOperationCompletionDisposition disposition) =>
            kernel.RecordExternalOperationCompletion(principal, new OperationCompletion(binding, disposition));

        public KernelResult<ExternalOperationSnapshot> MakeVisible(OperationBinding binding, ExternalVisibilityRequirement requirement, bool satisfied) =>
            kernel.RecordExternalOperationVisibility(principal, new OperationVisibilityEvidence(binding, requirement, satisfied));
    }
}
