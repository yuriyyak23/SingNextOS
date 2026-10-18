using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class V6ManagedDurableOutputModelTests
{
    private static readonly PersistenceSemanticsV1 Semantics = new(1,
        V6ManagedDurableOutputModel.QualifiedProviderIdentity, "media:model:1",
        PersistenceDomainClassV1.NamedManagedModel,
        PersistenceOrderingV1.DataThenMetadataThenCommitRecord, 7, 11, true);

    [Fact]
    public void DurableCommitPrecedesTheSinglePublicationCallback()
    {
        var model = new V6ManagedDurableOutputModel(Semantics);
        var calls = 0;
        var result = Submit(model, Binding(), () => { calls++; return KernelResult.Ok(); });

        Assert.True(result.IsSuccess);
        Assert.Equal(1, calls);
        Assert.True(result.Value!.DurableSequence < result.Value.PublishedSequence);
        Assert.Equal(PersistEvidenceAssuranceV1.ModelOnly, result.Value.Assurance);
    }

    [Theory]
    [InlineData((int)V6DurabilityFaultPoint.CrashAfterWrite)]
    [InlineData((int)V6DurabilityFaultPoint.TornData)]
    [InlineData((int)V6DurabilityFaultPoint.CrashAfterDataPersist)]
    [InlineData((int)V6DurabilityFaultPoint.TornMetadata)]
    [InlineData((int)V6DurabilityFaultPoint.CrashAfterMetadataPersist)]
    [InlineData((int)V6DurabilityFaultPoint.ProviderLossBeforeDurable)]
    public void EveryPreCommitCrashPointRemainsInvisibleAndUnrecoverable(int faultValue)
    {
        var fault = (V6DurabilityFaultPoint)faultValue;
        var model = new V6ManagedDurableOutputModel(Semantics);
        var calls = 0;

        Assert.False(Submit(model, Binding(), () => { calls++; return KernelResult.Ok(); }, fault).IsSuccess);
        var recovered = model.Recover();
        Assert.Null(recovered.LastDurable);
        Assert.Null(recovered.Freshness);
        Assert.True(recovered.HasTornOrUncommittedState);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void CrashAfterDurableRecoversCommittedDataButDoesNotInventPublication()
    {
        var model = new V6ManagedDurableOutputModel(Semantics);
        var binding = Binding();
        var calls = 0;

        Assert.False(Submit(model, binding, () => { calls++; return KernelResult.Ok(); },
            V6DurabilityFaultPoint.CrashAfterDurableBeforePublication).IsSuccess);
        var recovered = model.Recover();
        Assert.NotNull(recovered.LastDurable);
        Assert.Equal(0UL, recovered.LastDurable!.Value.PublishedSequence);
        Assert.NotNull(recovered.Freshness);
        Assert.False(recovered.Freshness!.Value.HasFreshAdmission(3, 5, 7));
        Assert.True(recovered.Freshness.Value.HasFreshAdmission(4, 6, 8));
        Assert.Equal(0, calls);

        Assert.Equal(KernelError.StaleGeneration,
            Submit(model, binding, () => { calls++; return KernelResult.Ok(); }).Error);
        Assert.Equal(0, calls);
        Assert.True(Submit(model, binding, () => { calls++; return KernelResult.Ok(); },
            runtimeGeneration: 4, processGeneration: 6, providerAdmissionGeneration: 8).IsSuccess);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void PrecommitCrashRequiresFreshAdmissionForNewOperationCorrelation()
    {
        var model = new V6ManagedDurableOutputModel(Semantics);
        Assert.False(Submit(model, Binding(), KernelResult.Ok,
            V6DurabilityFaultPoint.TornData).IsSuccess);
        Assert.Null(model.Recover().LastDurable);
        var next = Binding() with { OperationCorrelation = "operation:43", OperationGeneration = 14 };
        var calls = 0;

        Assert.Equal(KernelError.StaleGeneration,
            Submit(model, next, () => { calls++; return KernelResult.Ok(); }).Error);
        Assert.Equal(0, calls);
        Assert.True(Submit(model, next, () => { calls++; return KernelResult.Ok(); },
            runtimeGeneration: 4, processGeneration: 6,
            providerAdmissionGeneration: 8).IsSuccess);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void PublishedReplayIsHistoricalButNewOperationNeedsFreshAdmissionAfterRecovery()
    {
        var model = new V6ManagedDurableOutputModel(Semantics);
        var original = Submit(model, Binding(), KernelResult.Ok);
        Assert.True(original.IsSuccess);
        _ = model.Recover();
        var next = Binding() with { OperationCorrelation = "operation:43", OperationGeneration = 14 };
        var calls = 0;

        Assert.Equal(original.Value, Submit(model, Binding(),
            () => { calls++; return KernelResult.Ok(); }).Value);
        Assert.Equal(KernelError.StaleGeneration,
            Submit(model, next, () => { calls++; return KernelResult.Ok(); }).Error);
        Assert.Equal(0, calls);
        Assert.True(Submit(model, next, () => { calls++; return KernelResult.Ok(); },
            runtimeGeneration: 4, processGeneration: 6,
            providerAdmissionGeneration: 8).IsSuccess);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(3UL, 6UL, 8UL)]
    [InlineData(4UL, 5UL, 8UL)]
    [InlineData(4UL, 6UL, 7UL)]
    public void EveryRecoveryAdmissionDimensionMustAdvanceForNewOperation(
        ulong runtime, ulong process, ulong provider)
    {
        var model = new V6ManagedDurableOutputModel(Semantics);
        Assert.False(Submit(model, Binding(), KernelResult.Ok,
            V6DurabilityFaultPoint.CrashAfterWrite).IsSuccess);
        _ = model.Recover();
        var next = Binding() with { OperationCorrelation = "operation:43", OperationGeneration = 14 };
        var calls = 0;

        Assert.Equal(KernelError.StaleGeneration,
            Submit(model, next, () => { calls++; return KernelResult.Ok(); },
                runtimeGeneration: runtime, processGeneration: process,
                providerAdmissionGeneration: provider).Error);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void DuplicateReplayReturnsTheSameReceiptAndNeverPublishesTwice()
    {
        var model = new V6ManagedDurableOutputModel(Semantics);
        var calls = 0;
        var first = Submit(model, Binding(), () => { calls++; return KernelResult.Ok(); });
        var replay = Submit(model, Binding(), () => { calls++; return KernelResult.Ok(); });

        Assert.True(first.IsSuccess);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DuplicateGenerationWithChangedContentIsReplayDivergence()
    {
        var model = new V6ManagedDurableOutputModel(Semantics);
        Assert.True(Submit(model, Binding(), KernelResult.Ok).IsSuccess);

        var changed = Binding() with { ContentDigest = new string('c', 64) };
        Assert.Equal(KernelError.ReplayDiverged, Submit(model, changed, KernelResult.Ok).Error);
    }

    [Fact]
    public void ReplayWithChangedRecoveryGenerationCannotReuseDurableEvidence()
    {
        var model = new V6ManagedDurableOutputModel(Semantics);
        Assert.True(Submit(model, Binding(), KernelResult.Ok).IsSuccess);

        var changed = Binding() with { RecoveryGeneration = 18 };
        var calls = 0;
        Assert.Equal(KernelError.StaleGeneration,
            Submit(model, changed, () => { calls++; return KernelResult.Ok(); }).Error);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void SecondCrashRequiresAdmissionFresherThanTheFirstRecoveryAttempt()
    {
        var model = new V6ManagedDurableOutputModel(Semantics);
        var binding = Binding();
        Assert.False(Submit(model, binding, KernelResult.Ok,
            V6DurabilityFaultPoint.CrashAfterDurableBeforePublication).IsSuccess);
        _ = model.Recover();

        var failedPublication = Submit(model, binding,
            () => KernelResult.Fail(KernelError.PlatformUnavailable, "owner unavailable"),
            runtimeGeneration: 4, processGeneration: 6, providerAdmissionGeneration: 8);
        Assert.Equal(KernelError.PlatformUnavailable, failedPublication.Error);

        var secondRecovery = model.Recover();
        Assert.True(secondRecovery.HasAmbiguousPublication);
        Assert.False(secondRecovery.Freshness!.Value.HasFreshAdmission(4, 6, 8));
        Assert.True(secondRecovery.Freshness.Value.HasFreshAdmission(5, 7, 9));
        Assert.True(model.ReconcileAmbiguousPublication(binding, KernelResult.Ok).IsSuccess);
        var calls = 0;
        Assert.Equal(KernelError.StaleGeneration,
            Submit(model, binding, () => { calls++; return KernelResult.Ok(); },
                runtimeGeneration: 4, processGeneration: 6, providerAdmissionGeneration: 8).Error);
        Assert.Equal(0, calls);
        Assert.True(Submit(model, binding, () => { calls++; return KernelResult.Ok(); },
            runtimeGeneration: 5, processGeneration: 7, providerAdmissionGeneration: 9).IsSuccess);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void UnpublishedRetryRevalidatesProviderAndMediaBeforePublication()
    {
        var model = new V6ManagedDurableOutputModel(Semantics);
        Assert.Equal(KernelError.PlatformUnavailable,
            Submit(model, Binding(), () => KernelResult.Fail(KernelError.PlatformUnavailable,
                "publication owner unavailable")).Error);
        Assert.True(model.ReconcileAmbiguousPublication(Binding(), KernelResult.Ok).IsSuccess);
        var calls = 0;
        var stale = model.PersistAndPublish(Binding(), 3, 5, 7,
            () => 8, () => 11, () => PersistenceDomainClassV1.NamedManagedModel,
            () => { calls++; return KernelResult.Ok(); });

        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PublicationCallbackDriftRetainsPossibleEffectWithoutPublishedEvidence(int drift)
    {
        var model = new V6ManagedDurableOutputModel(Semantics);
        var providerGeneration = 7UL;
        var mediaGeneration = 11UL;
        var domain = PersistenceDomainClassV1.NamedManagedModel;
        var callbackRan = false;

        var result = model.PersistAndPublish(Binding(), 3, 5, 7,
            () => providerGeneration, () => mediaGeneration, () => domain,
            () =>
            {
                callbackRan = true;
                if (drift == 0) providerGeneration++;
                if (drift == 1) mediaGeneration++;
                if (drift == 2) domain = PersistenceDomainClassV1.Eadr;
                return KernelResult.Ok();
            });

        Assert.True(callbackRan);
        Assert.Equal(drift == 2 ? KernelError.PlatformDenied : KernelError.StaleGeneration,
            result.Error);
        var recovered = model.Recover();
        Assert.True(recovered.HasAmbiguousPublication);
        Assert.Equal(0UL, recovered.LastDurable!.Value.PublishedSequence);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            Submit(model, Binding(), KernelResult.Ok).Error);
    }

    [Fact]
    public void ProviderOrMediaGenerationDriftPreventsDurableCommitAndPublication()
    {
        var provider = new V6ManagedDurableOutputModel(Semantics);
        var media = new V6ManagedDurableOutputModel(Semantics);
        var calls = 0;

        var staleProvider = provider.PersistAndPublish(Binding(), 3, 5, 7, () => 8, () => 11,
            () => PersistenceDomainClassV1.NamedManagedModel, () => { calls++; return KernelResult.Ok(); });
        var staleMedia = media.PersistAndPublish(Binding(), 3, 5, 7, () => 7, () => 12,
            () => PersistenceDomainClassV1.NamedManagedModel, () => { calls++; return KernelResult.Ok(); });

        Assert.Equal(KernelError.StaleGeneration, staleProvider.Error);
        Assert.Equal(KernelError.StaleGeneration, staleMedia.Error);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void PersistenceDomainDowngradeIsRejectedBeforeCommit()
    {
        var model = new V6ManagedDurableOutputModel(Semantics);
        var calls = 0;
        var result = model.PersistAndPublish(Binding(), 3, 5, 7, () => 7, () => 11,
            () => PersistenceDomainClassV1.Adr, () => { calls++; return KernelResult.Ok(); });

        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, calls);
        Assert.Null(model.Recover().LastDurable);
    }

    [Fact]
    public void TornNewWriteCannotReplaceTheLastCommittedValue()
    {
        var model = new V6ManagedDurableOutputModel(Semantics);
        var committed = Binding();
        Assert.True(Submit(model, committed, KernelResult.Ok).IsSuccess);
        var next = committed with { OperationCorrelation = "operation:43", OperationGeneration = 14 };
        Assert.False(Submit(model, next, KernelResult.Ok, V6DurabilityFaultPoint.TornMetadata).IsSuccess);

        var recovered = model.Recover();
        Assert.Equal(committed.OperationCorrelation, recovered.LastDurable!.Value.OperationCorrelation);
        Assert.True(recovered.HasTornOrUncommittedState);
    }

    [Fact]
    public void PublishingAnOlderDurableCommitDoesNotRollBackRecoveryHead()
    {
        var model = new V6ManagedDurableOutputModel(Semantics);
        var older = Binding();
        var newer = older with { OperationCorrelation = "operation:43", OperationGeneration = 14 };
        Assert.False(Submit(model, older, KernelResult.Ok,
            V6DurabilityFaultPoint.CrashAfterDurableBeforePublication).IsSuccess);
        Assert.True(Submit(model, newer, KernelResult.Ok).IsSuccess);
        Assert.Equal(newer.OperationCorrelation,
            model.Recover().LastDurable!.Value.OperationCorrelation);

        Assert.True(Submit(model, older, KernelResult.Ok,
            runtimeGeneration: 4, processGeneration: 6,
            providerAdmissionGeneration: 8).IsSuccess);
        Assert.Equal(newer.OperationCorrelation,
            model.Recover().LastDurable!.Value.OperationCorrelation);
    }

    [Fact]
    public void PublicationFailureRequiresClosureBeforeFreshRetryWithoutRestaging()
    {
        var model = new V6ManagedDurableOutputModel(Semantics);
        var first = Submit(model, Binding(), () => KernelResult.Fail(KernelError.PlatformUnavailable, "owner unavailable"));
        var blocked = Submit(model, Binding(), KernelResult.Ok);
        var deniedClosure = model.ReconcileAmbiguousPublication(Binding(),
            () => KernelResult.Fail(KernelError.ExternalEffectUncontained, "publication not closed"));
        Assert.Equal(KernelError.ExternalEffectUncontained, deniedClosure.Error);
        Assert.True(model.ReconcileAmbiguousPublication(Binding(), KernelResult.Ok).IsSuccess);
        var retry = Submit(model, Binding(), KernelResult.Ok,
            runtimeGeneration: 4, processGeneration: 6, providerAdmissionGeneration: 8);

        Assert.False(first.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, blocked.Error);
        Assert.True(retry.IsSuccess);
        Assert.True(retry.Value!.PublishedSequence > retry.Value.DurableSequence);
    }

    [Fact]
    public void RecoveryDuringPublicationQuarantinesPossibleEffectUntilExplicitClosure()
    {
        var model = new V6ManagedDurableOutputModel(Semantics);
        var binding = Binding();
        var calls = 0;
        var crossed = Submit(model, binding, () =>
        {
            calls++;
            var recovery = model.Recover();
            Assert.True(recovery.HasAmbiguousPublication);
            Assert.Equal(0UL, recovery.LastDurable!.Value.PublishedSequence);
            return KernelResult.Ok();
        });

        Assert.Equal(KernelError.ExternalEffectUncontained, crossed.Error);
        Assert.Equal(1, calls);
        Assert.Equal(0UL, model.Recover().LastDurable!.Value.PublishedSequence);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            Submit(model, binding, () => { calls++; return KernelResult.Ok(); },
                runtimeGeneration: 4, processGeneration: 6, providerAdmissionGeneration: 8).Error);
        Assert.Equal(1, calls);
        Assert.True(model.ReconcileAmbiguousPublication(binding, KernelResult.Ok).IsSuccess);
        Assert.True(Submit(model, binding, () => { calls++; return KernelResult.Ok(); },
            runtimeGeneration: 4, processGeneration: 6, providerAdmissionGeneration: 8).IsSuccess);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void PhysicalDomainsAreNotExecutableByTheManagedModel()
    {
        Assert.Throws<NotSupportedException>(() => new V6ManagedDurableOutputModel(
            Semantics with { DomainClass = PersistenceDomainClassV1.Eadr }));
    }

    private static DurableOutputBindingV1 Binding() => new(1, "operation:42", 13,
        new('a', 64), new('b', 64), 17, Semantics);

    private static KernelResult<ProviderPersistEvidenceV1> Submit(
        V6ManagedDurableOutputModel model, DurableOutputBindingV1 binding,
        Func<KernelResult> publish, V6DurabilityFaultPoint fault = V6DurabilityFaultPoint.None,
        ulong runtimeGeneration = 3, ulong processGeneration = 5,
        ulong providerAdmissionGeneration = 7) =>
        model.PersistAndPublish(binding, runtimeGeneration, processGeneration,
            providerAdmissionGeneration, () => 7, () => 11,
            () => PersistenceDomainClassV1.NamedManagedModel, publish, fault);
}
