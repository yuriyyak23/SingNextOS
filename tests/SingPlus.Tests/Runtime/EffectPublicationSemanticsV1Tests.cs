using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class EffectPublicationSemanticsV1Tests
{
    [Fact]
    public void ExistingPoliciesMapOneWayWithoutInventingDurabilityCompensationOrCommutativity()
    {
        var staged = ExternalEffectSemantics.FromPolicy(new(
            ExternalEffectClass.StagedReversibleUntilPublish, ExternalReplayProtection.None, false));
        var snapshot = ExternalEffectSemantics.FromPolicy(new(
            ExternalEffectClass.SnapshotOrIdempotenceRequired, ExternalReplayProtection.SnapshotRestore, true));
        var idempotent = ExternalEffectSemantics.FromPolicy(new(
            ExternalEffectClass.SnapshotOrIdempotenceRequired, ExternalReplayProtection.Idempotent, true));
        var irreversible = ExternalEffectSemantics.FromPolicy(new(
            ExternalEffectClass.IrreversibleBarrier, ExternalReplayProtection.None, true));

        Assert.True(staged.Staged && staged.Reversible);
        Assert.False(staged.ExternallyObservable || staged.AuthorizesEffect || staged.AuthorizesPublication);
        Assert.True(snapshot.LocallyIrreversible && snapshot.ExternallyObservable);
        Assert.False(snapshot.Idempotent);
        Assert.True(idempotent.Idempotent);
        Assert.True(irreversible.LocallyIrreversible && irreversible.ExternallyObservable);
        Assert.All(new[] { staged, snapshot, idempotent, irreversible }, value =>
        {
            Assert.False(value.Durable);
            Assert.False(value.Compensatable);
            Assert.False(value.Commutative);
        });
    }

    [Fact]
    public void InvalidOrUnknownTraitCombinationsFailClosed()
    {
        Assert.Throws<NotSupportedException>(() => new EffectSemanticsV1(2, false, false,
            false, false, false, false, false, false).Canonicalize());
        Assert.Throws<ArgumentException>(() => new EffectSemanticsV1(1, true, true,
            false, true, false, false, false, false).Canonicalize());
        Assert.Throws<ArgumentException>(() => new EffectSemanticsV1(1, false, true,
            true, false, false, false, false, false).Canonicalize());
        Assert.Throws<ArgumentOutOfRangeException>(() => ExternalEffectSemantics.FromPolicy(
            new((ExternalEffectClass)99, ExternalReplayProtection.None, false)));
    }

    [Fact]
    public async Task StagedDecisionIsExactCallbackRunsOutsideOwnerLockAndRacesCannotCrossIt()
    {
        var context = Ready(ExternalPublicationPolicy.Staged);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        ExternalPublicationDecisionV1 observed = default;
        var publish = Task.Run(() => context.Kernel.PublishExternalOperation(context.Owner,
            context.Operation, Dependencies, new(ExternalPublicationPolicy.Staged), decision =>
            {
                observed = decision;
                entered.Set();
                Assert.True(Task.Run(() => context.Kernel.QueryExternalOperation(context.Owner, context.Operation))
                    .Wait(TimeSpan.FromSeconds(2)));
                release.Wait(TimeSpan.FromSeconds(5));
            }));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var cancel = context.Kernel.CancelExternalOperation(context.Owner, context.Operation, true);
        var loss = context.Kernel.RecordExternalOperationProviderLoss(context.Owner, context.Operation);
        Assert.Equal(KernelError.InvalidTransition, cancel.Error);
        Assert.Equal(KernelError.InvalidTransition, loss.Error);
        release.Set();
        var result = await publish.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(ExternalPublicationDecisionV1.CurrentVersion, observed.Version);
        Assert.Equal(context.Binding, observed.Binding);
        Assert.Equal(Dependencies, observed.Dependencies);
        Assert.False(observed.IsReusablePermit);
        Assert.False(observed.AuthorizesNewExecution);
        Assert.Equal(ExternalOperationState.Published, result.Value!.State);
    }

    [Fact]
    public void DirectCoherentContourNeverExecutesFictitiousWithheldPublicationAction()
    {
        var context = Ready(ExternalPublicationPolicy.DirectCoherent);
        var callback = false;

        var result = context.Kernel.PublishExternalOperation(context.Owner, context.Operation,
            Dependencies, new(ExternalPublicationPolicy.DirectCoherent), _ => callback = true);

        Assert.True(result.IsSuccess, result.Message);
        Assert.False(callback);
        Assert.Equal(ExternalEffectBoundaryState.Irreversible, result.Value!.EffectBoundary);
        Assert.Contains(result.Value.Transitions, transition => transition.Event == "DirectPublicationObserved");
        Assert.DoesNotContain(result.Value.Transitions, transition => transition.Event == "PublicationDecided");
    }

    [Fact]
    public void StagedProviderActionFailureDiscardsAndCannotBeRetriedAsFreshDecision()
    {
        var context = Ready(ExternalPublicationPolicy.Staged);

        var failed = context.Kernel.PublishExternalOperation(context.Owner, context.Operation,
            Dependencies, new(ExternalPublicationPolicy.Staged),
            (ExternalPublicationDecisionV1 _) => throw new InvalidOperationException("provider fault"));
        var replay = context.Kernel.PublishExternalOperation(context.Owner, context.Operation,
            Dependencies, new(ExternalPublicationPolicy.Staged), _ => Assert.Fail("discarded action replayed"));

        Assert.Equal(KernelError.PlatformFaulted, failed.Error);
        Assert.Equal(KernelError.InvalidTransition, replay.Error);
        Assert.Equal(ExternalOperationDisposition.Discarded,
            context.Kernel.QueryExternalOperation(context.Owner, context.Operation).Value!.Disposition);
    }

    private static ReadyContext Ready(ExternalPublicationPolicy policy)
    {
        var kernel = new RuntimeKernel();
        var owner = TestFixtures.Create(kernel, 1300, 2400).Handle;
        var input = kernel.AllocateBuffer<byte>(owner, 8).Value!.Handle;
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!.Handle;
        var preparation = kernel.PrepareExternalOperation(owner,
            [new(input, RegionUseMode.ReadOnly, new(0, 8)),
             new(output, policy == ExternalPublicationPolicy.Staged ? RegionUseMode.StagedOutput : RegionUseMode.ExclusiveWrite, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, policy,
            policy == ExternalPublicationPolicy.DirectCoherent
                ? new(ExternalEffectClass.IrreversibleBarrier, ExternalReplayProtection.None, true)
                : null).Value!;
        Assert.True(kernel.AdmitExternalOperation(owner, preparation.Operation, Dependencies).IsSuccess);
        var binding = kernel.RecordExternalOperationSubmission(owner, preparation.Operation, Dependencies).Value!;
        Assert.True(kernel.RecordExternalOperationCompletion(owner,
            new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(kernel.RecordExternalOperationVisibility(owner,
            new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        return new(kernel, owner, preparation.Operation, binding);
    }

    private static readonly OperationDependencySnapshot Dependencies = new(7, 11, 13, 17);
    private sealed record ReadyContext(RuntimeKernel Kernel, ProcessHandle Owner,
        ExternalOperationHandle Operation, OperationBinding Binding);
}
