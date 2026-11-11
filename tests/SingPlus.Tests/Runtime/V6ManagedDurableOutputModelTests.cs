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
        V6ManagedDurableOutputModel model = new V6ManagedDurableOutputModel(Semantics);
        int calls = 0;
        KernelResult<ProviderPersistEvidenceV1> result = Submit(model, Binding(), () => { calls++; return KernelResult.Ok(); });

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
        V6DurabilityFaultPoint fault = (V6DurabilityFaultPoint)faultValue;
        V6ManagedDurableOutputModel model = new V6ManagedDurableOutputModel(Semantics);
        int calls = 0;

        Assert.False(Submit(model, Binding(), () => { calls++; return KernelResult.Ok(); }, fault).IsSuccess);
        V6DurableRecoverySnapshot recovered = model.Recover();
        Assert.Null(recovered.LastDurable);
        Assert.Null(recovered.Freshness);
        Assert.True(recovered.HasTornOrUncommittedState);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void CrashAfterDurableRecoversCommittedDataButDoesNotInventPublication()
    {
        V6ManagedDurableOutputModel model = new V6ManagedDurableOutputModel(Semantics);
        DurableOutputBindingV1 binding = Binding();
        int calls = 0;

        Assert.False(Submit(model, binding, () => { calls++; return KernelResult.Ok(); },
            V6DurabilityFaultPoint.CrashAfterDurableBeforePublication).IsSuccess);
        V6DurableRecoverySnapshot recovered = model.Recover();
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
        V6ManagedDurableOutputModel model = new V6ManagedDurableOutputModel(Semantics);
        Assert.False(Submit(model, Binding(), KernelResult.Ok,
            V6DurabilityFaultPoint.TornData).IsSuccess);
        Assert.Null(model.Recover().LastDurable);
        DurableOutputBindingV1 next = Binding() with { OperationCorrelation = "operation:43", OperationGeneration = 14 };
        int calls = 0;

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
        V6ManagedDurableOutputModel model = new V6ManagedDurableOutputModel(Semantics);
        KernelResult<ProviderPersistEvidenceV1> original = Submit(model, Binding(), KernelResult.Ok);
        Assert.True(original.IsSuccess);
        _ = model.Recover();
        DurableOutputBindingV1 next = Binding() with { OperationCorrelation = "operation:43", OperationGeneration = 14 };
        int calls = 0;

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
        V6ManagedDurableOutputModel model = new V6ManagedDurableOutputModel(Semantics);
        Assert.False(Submit(model, Binding(), KernelResult.Ok,
            V6DurabilityFaultPoint.CrashAfterWrite).IsSuccess);
        _ = model.Recover();
        DurableOutputBindingV1 next = Binding() with { OperationCorrelation = "operation:43", OperationGeneration = 14 };
        int calls = 0;

        Assert.Equal(KernelError.StaleGeneration,
            Submit(model, next, () => { calls++; return KernelResult.Ok(); },
                runtimeGeneration: runtime, processGeneration: process,
                providerAdmissionGeneration: provider).Error);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void DuplicateReplayReturnsTheSameReceiptAndNeverPublishesTwice()
    {
        V6ManagedDurableOutputModel model = new V6ManagedDurableOutputModel(Semantics);
        int calls = 0;
        KernelResult<ProviderPersistEvidenceV1> first = Submit(model, Binding(), () => { calls++; return KernelResult.Ok(); });
        KernelResult<ProviderPersistEvidenceV1> replay = Submit(model, Binding(), () => { calls++; return KernelResult.Ok(); });

        Assert.True(first.IsSuccess);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DuplicateGenerationWithChangedContentIsReplayDivergence()
    {
        V6ManagedDurableOutputModel model = new V6ManagedDurableOutputModel(Semantics);
        Assert.True(Submit(model, Binding(), KernelResult.Ok).IsSuccess);

        DurableOutputBindingV1 changed = Binding() with { ContentDigest = new string('c', 64) };
        Assert.Equal(KernelError.ReplayDiverged, Submit(model, changed, KernelResult.Ok).Error);
    }

    [Fact]
    public void ReplayWithChangedRecoveryGenerationCannotReuseDurableEvidence()
    {
        V6ManagedDurableOutputModel model = new V6ManagedDurableOutputModel(Semantics);
        Assert.True(Submit(model, Binding(), KernelResult.Ok).IsSuccess);

        DurableOutputBindingV1 changed = Binding() with { RecoveryGeneration = 18 };
        int calls = 0;
        Assert.Equal(KernelError.StaleGeneration,
            Submit(model, changed, () => { calls++; return KernelResult.Ok(); }).Error);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void SecondCrashRequiresAdmissionFresherThanTheFirstRecoveryAttempt()
    {
        V6ManagedDurableOutputModel model = new V6ManagedDurableOutputModel(Semantics);
        DurableOutputBindingV1 binding = Binding();
        Assert.False(Submit(model, binding, KernelResult.Ok,
            V6DurabilityFaultPoint.CrashAfterDurableBeforePublication).IsSuccess);
        _ = model.Recover();

        KernelResult<ProviderPersistEvidenceV1> failedPublication = Submit(model, binding,
            () => KernelResult.Fail(KernelError.PlatformUnavailable, "owner unavailable"),
            runtimeGeneration: 4, processGeneration: 6, providerAdmissionGeneration: 8);
        Assert.Equal(KernelError.PlatformUnavailable, failedPublication.Error);

        V6DurableRecoverySnapshot secondRecovery = model.Recover();
        Assert.True(secondRecovery.HasAmbiguousPublication);
        Assert.False(secondRecovery.Freshness!.Value.HasFreshAdmission(4, 6, 8));
        Assert.True(secondRecovery.Freshness.Value.HasFreshAdmission(5, 7, 9));
        Assert.True(model.ReconcileAmbiguousPublication(binding, KernelResult.Ok).IsSuccess);
        int calls = 0;
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
        V6ManagedDurableOutputModel model = new V6ManagedDurableOutputModel(Semantics);
        Assert.Equal(KernelError.PlatformUnavailable,
            Submit(model, Binding(), () => KernelResult.Fail(KernelError.PlatformUnavailable,
                "publication owner unavailable")).Error);
        Assert.True(model.ReconcileAmbiguousPublication(Binding(), KernelResult.Ok).IsSuccess);
        int calls = 0;
        KernelResult<ProviderPersistEvidenceV1> stale = model.PersistAndPublish(Binding(), 3, 5, 7,
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
        V6ManagedDurableOutputModel model = new V6ManagedDurableOutputModel(Semantics);
        ulong providerGeneration = 7UL;
        ulong mediaGeneration = 11UL;
        PersistenceDomainClassV1 domain = PersistenceDomainClassV1.NamedManagedModel;
        bool callbackRan = false;

        KernelResult<ProviderPersistEvidenceV1> result = model.PersistAndPublish(Binding(), 3, 5, 7,
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
        V6DurableRecoverySnapshot recovered = model.Recover();
        Assert.True(recovered.HasAmbiguousPublication);
        Assert.Equal(0UL, recovered.LastDurable!.Value.PublishedSequence);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            Submit(model, Binding(), KernelResult.Ok).Error);
    }

    [Fact]
    public void ProviderOrMediaGenerationDriftPreventsDurableCommitAndPublication()
    {
        V6ManagedDurableOutputModel provider = new V6ManagedDurableOutputModel(Semantics);
        V6ManagedDurableOutputModel media = new V6ManagedDurableOutputModel(Semantics);
        int calls = 0;

        KernelResult<ProviderPersistEvidenceV1> staleProvider = provider.PersistAndPublish(Binding(), 3, 5, 7, () => 8, () => 11,
            () => PersistenceDomainClassV1.NamedManagedModel, () => { calls++; return KernelResult.Ok(); });
        KernelResult<ProviderPersistEvidenceV1> staleMedia = media.PersistAndPublish(Binding(), 3, 5, 7, () => 7, () => 12,
            () => PersistenceDomainClassV1.NamedManagedModel, () => { calls++; return KernelResult.Ok(); });

        Assert.Equal(KernelError.StaleGeneration, staleProvider.Error);
        Assert.Equal(KernelError.StaleGeneration, staleMedia.Error);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void PersistenceDomainDowngradeIsRejectedBeforeCommit()
    {
        V6ManagedDurableOutputModel model = new V6ManagedDurableOutputModel(Semantics);
        int calls = 0;
        KernelResult<ProviderPersistEvidenceV1> result = model.PersistAndPublish(Binding(), 3, 5, 7, () => 7, () => 11,
            () => PersistenceDomainClassV1.Adr, () => { calls++; return KernelResult.Ok(); });

        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, calls);
        Assert.Null(model.Recover().LastDurable);
    }

    [Fact]
    public void TornNewWriteCannotReplaceTheLastCommittedValue()
    {
        V6ManagedDurableOutputModel model = new V6ManagedDurableOutputModel(Semantics);
        DurableOutputBindingV1 committed = Binding();
        Assert.True(Submit(model, committed, KernelResult.Ok).IsSuccess);
        DurableOutputBindingV1 next = committed with { OperationCorrelation = "operation:43", OperationGeneration = 14 };
        Assert.False(Submit(model, next, KernelResult.Ok, V6DurabilityFaultPoint.TornMetadata).IsSuccess);

        V6DurableRecoverySnapshot recovered = model.Recover();
        Assert.Equal(committed.OperationCorrelation, recovered.LastDurable!.Value.OperationCorrelation);
        Assert.True(recovered.HasTornOrUncommittedState);
    }

    [Fact]
    public void PublishingAnOlderDurableCommitDoesNotRollBackRecoveryHead()
    {
        V6ManagedDurableOutputModel model = new V6ManagedDurableOutputModel(Semantics);
        DurableOutputBindingV1 older = Binding();
        DurableOutputBindingV1 newer = older with { OperationCorrelation = "operation:43", OperationGeneration = 14 };
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
        V6ManagedDurableOutputModel model = new V6ManagedDurableOutputModel(Semantics);
        KernelResult<ProviderPersistEvidenceV1> first = Submit(model, Binding(), () => KernelResult.Fail(KernelError.PlatformUnavailable, "owner unavailable"));
        KernelResult<ProviderPersistEvidenceV1> blocked = Submit(model, Binding(), KernelResult.Ok);
        KernelResult deniedClosure = model.ReconcileAmbiguousPublication(Binding(),
            () => KernelResult.Fail(KernelError.ExternalEffectUncontained, "publication not closed"));
        Assert.Equal(KernelError.ExternalEffectUncontained, deniedClosure.Error);
        Assert.True(model.ReconcileAmbiguousPublication(Binding(), KernelResult.Ok).IsSuccess);
        KernelResult<ProviderPersistEvidenceV1> retry = Submit(model, Binding(), KernelResult.Ok,
            runtimeGeneration: 4, processGeneration: 6, providerAdmissionGeneration: 8);

        Assert.False(first.IsSuccess);
        Assert.Equal(KernelError.ExternalEffectUncontained, blocked.Error);
        Assert.True(retry.IsSuccess);
        Assert.True(retry.Value!.PublishedSequence > retry.Value.DurableSequence);
    }

    [Fact]
    public void RecoveryDuringPublicationQuarantinesPossibleEffectUntilExplicitClosure()
    {
        V6ManagedDurableOutputModel model = new V6ManagedDurableOutputModel(Semantics);
        DurableOutputBindingV1 binding = Binding();
        int calls = 0;
        KernelResult<ProviderPersistEvidenceV1> crossed = Submit(model, binding, () =>
        {
            calls++;
            V6DurableRecoverySnapshot recovery = model.Recover();
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
