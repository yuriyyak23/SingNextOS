using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class ExternalEffectPolicyTests
{
    [Fact]
    public void CoherentCapabilityWithoutEffectClassificationCannotSubmit()
    {
        var s = CreateScenario();
        var prepared = Prepare(s, ExternalPublicationPolicy.DirectCoherent, null);
        Assert.False(prepared.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, prepared.Error);
    }

    [Fact]
    public void DirectSnapshotClassRequiresConcreteReplayProtection()
    {
        var s = CreateScenario();
        var missing = Prepare(s, ExternalPublicationPolicy.DirectCoherent,
            new(ExternalEffectClass.SnapshotOrIdempotenceRequired, ExternalReplayProtection.None, true));
        var valid = Prepare(s, ExternalPublicationPolicy.DirectCoherent,
            new(ExternalEffectClass.SnapshotOrIdempotenceRequired, ExternalReplayProtection.Deduplicated, true));
        Assert.False(missing.IsSuccess);
        Assert.True(valid.IsSuccess, valid.Message);
    }

    [Fact]
    public void DirectIrreversibleBoundaryIsCrossedAtSubmitAndResetCannotClaimRollback()
    {
        var s = CreateScenario();
        var prepared = Prepare(s, ExternalPublicationPolicy.DirectCoherent,
            new(ExternalEffectClass.IrreversibleBarrier, ExternalReplayProtection.None, true)).Value!;
        Assert.True(s.Kernel.AdmitExternalOperation(s.Handle, prepared.Operation, new(1, 1)).IsSuccess);
        Assert.True(s.Kernel.RecordExternalOperationSubmission(s.Handle, prepared.Operation, new(1, 1)).IsSuccess);
        var submitted = s.Kernel.QueryExternalOperation(s.Handle, prepared.Operation).Value!;
        Assert.Equal(ExternalEffectBoundaryState.Irreversible, submitted.EffectBoundary);

        var reset = s.Kernel.RecordExternalOperationProviderLoss(s.Handle, prepared.Operation);
        Assert.True(reset.IsSuccess);
        Assert.Equal(ExternalEffectBoundaryState.Irreversible, reset.Value!.EffectBoundary);
        Assert.Equal(ExternalOperationDisposition.Faulted, reset.Value.Disposition);
        Assert.DoesNotContain(reset.Value.Transitions, transition => transition.Event.Contains("Rollback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StagedBoundaryBecomesVisibleOnlyAtPublicationAndPublishesOnce()
    {
        var s = CreateScenario();
        var prepared = Prepare(s, ExternalPublicationPolicy.Staged, null).Value!;
        Assert.True(s.Kernel.AdmitExternalOperation(s.Handle, prepared.Operation, new(1, 1)).IsSuccess);
        var binding = s.Kernel.RecordExternalOperationSubmission(s.Handle, prepared.Operation, new(1, 1)).Value!;
        Assert.Equal(ExternalEffectBoundaryState.StagedPending,
            s.Kernel.QueryExternalOperation(s.Handle, prepared.Operation).Value!.EffectBoundary);
        Assert.True(s.Kernel.RecordExternalOperationCompletion(s.Handle, new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(s.Kernel.RecordExternalOperationVisibility(s.Handle, new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        var publications = 0;
        var first = s.Kernel.PublishExternalOperation(s.Handle, prepared.Operation, new(1, 1), new(ExternalPublicationPolicy.Staged), () => publications++);
        var duplicate = s.Kernel.PublishExternalOperation(s.Handle, prepared.Operation, new(1, 1), new(ExternalPublicationPolicy.Staged), () => publications++);
        Assert.True(first.IsSuccess);
        Assert.False(duplicate.IsSuccess);
        Assert.Equal(1, publications);
        Assert.Equal(ExternalEffectBoundaryState.ExternallyVisible, first.Value!.EffectBoundary);
    }

    [Fact]
    public void ReadOnlyCoherentObservationIsNotForcedIntoReplayBarrier()
    {
        var s = CreateScenario(readOnlyOutput: true);
        var prepared = Prepare(s, ExternalPublicationPolicy.Staged, null);
        Assert.True(prepared.IsSuccess, prepared.Message);
        Assert.Equal(ExternalEffectClass.StagedReversibleUntilPublish, prepared.Value!.EffectPolicy.EffectClass);
        Assert.Equal(ExternalReplayProtection.None, prepared.Value.EffectPolicy.ReplayProtection);
    }

    private static KernelResult<OperationPreparation> Prepare(Scenario s, ExternalPublicationPolicy publication, ExternalEffectPolicy? effect) =>
        s.Kernel.PrepareExternalOperation(s.Handle,
            [new(s.Input.Handle, RegionUseMode.ReadOnly, new(0, 8)),
             new(s.Output.Handle, publication == ExternalPublicationPolicy.DirectCoherent ? RegionUseMode.ExclusiveWrite : RegionUseMode.ReadOnly, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, publication, effect);

    private static Scenario CreateScenario(bool readOnlyOutput = false)
    {
        var kernel = new RuntimeKernel();
        var (_, handle) = TestFixtures.Create(kernel, 981, 982);
        return new(kernel, handle, kernel.AllocateBuffer<byte>(handle, 8).Value!, kernel.AllocateBuffer<byte>(handle, 8).Value!);
    }

    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Handle,
        SingPlus.Sip.OwnedBuffer<byte> Input, SingPlus.Sip.OwnedBuffer<byte> Output);
}
