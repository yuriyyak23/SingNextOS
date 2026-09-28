using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class SemanticTraceContractsV1Tests
{
    [Fact]
    public void CompleteStagedLifecycleIsValidAndEventsGrantNoAuthority()
    {
        var trace = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible,
            SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Settled,
            SemanticTraceEventKindV1.Released);

        var result = SemanticTraceValidatorV1.Validate(trace);

        Assert.True(result.IsValid);
        Assert.Null(result.Counterexample);
        Assert.All(trace, item =>
        {
            Assert.False(item.AuthorizesExecution);
            Assert.False(item.AuthorizesEffect);
            Assert.False(item.AuthorizesPublication);
        });
    }

    [Theory]
    [InlineData(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.RetireOrComplete)]
    [InlineData(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.Published)]
    [InlineData(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
        SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Published)]
    public void MissingEffectOrVisibilityTransitionFailsClosed(params SemanticTraceEventKindV1[] kinds)
    {
        var result = SemanticTraceValidatorV1.Validate(Kinds(kinds));
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder, result.Status);
        Assert.NotNull(result.Counterexample);
    }

    [Fact]
    public void GenerationChangeIsPreservedButDoesNotPermitInvalidPublication()
    {
        var validPrefix = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined,
            SemanticTraceEventKindV1.Settled).Select((item, index) => index < 2
                ? item : item with { GenerationVectorDigest = new string('c', 64) }).ToArray();
        var invalid = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Published).Select((item, index) => index < 2
                ? item : item with { GenerationVectorDigest = new string('c', 64) }).ToArray();

        Assert.True(SemanticTraceValidatorV1.Validate(validPrefix).IsValid);
        Assert.False(SemanticTraceValidatorV1.Validate(invalid).IsValid);
        Assert.False(SemanticTraceValidatorV1.Validate([
            .. validPrefix,
            Event(validPrefix.Length + 1, SemanticTraceEventKindV1.Released) with
            { GenerationVectorDigest = new string('c', 64) }
        ]).IsValid);
    }

    [Fact]
    public void GenerationMarkerRequiresChangedDigestAndEveryDigestChangeRequiresMarker()
    {
        var submitted = Event(1, SemanticTraceEventKindV1.Submit);
        var possible = Event(2, SemanticTraceEventKindV1.EffectPossible);
        var falseMarker = Event(3, SemanticTraceEventKindV1.GenerationChanged);
        Assert.Equal(SemanticTraceValidationStatusV1.GenerationMismatch,
            SemanticTraceValidatorV1.Validate([submitted, possible, falseMarker]).Status);

        var unannounced = Event(3, SemanticTraceEventKindV1.RetireOrComplete) with
        { GenerationVectorDigest = new string('c', 64) };
        Assert.Equal(SemanticTraceValidationStatusV1.GenerationMismatch,
            SemanticTraceValidatorV1.Validate([submitted, possible, unannounced]).Status);

        var marker = falseMarker with { GenerationVectorDigest = new string('c', 64) };
        var continued = Event(4, SemanticTraceEventKindV1.Quarantined) with
        { GenerationVectorDigest = new string('c', 64) };
        Assert.True(SemanticTraceValidatorV1.Validate([submitted, possible, marker, continued]).IsValid);

        var providerTrace = new[]
        {
            Provider(1, ProviderTraceDispositionV1.SecurityRelevant,
                SemanticTraceEventKindV1.Submit),
            Provider(2, ProviderTraceDispositionV1.SecurityRelevant,
                SemanticTraceEventKindV1.EffectPossible),
            Provider(3, ProviderTraceDispositionV1.ProviderPrivate,
                SemanticTraceEventKindV1.GenerationChanged) with
            { GenerationVectorDigest = new string('c', 64) },
            Provider(4, ProviderTraceDispositionV1.SecurityRelevant,
                SemanticTraceEventKindV1.Quarantined) with
            { GenerationVectorDigest = new string('c', 64) },
        };
        Assert.Throws<InvalidOperationException>(() =>
            SemanticTraceProjectionV1.Project(providerTrace));
    }

    [Fact]
    public void GenerationDriftCannotSplitSubmitFromPossibleEffect()
    {
        var submitted = Event(1, SemanticTraceEventKindV1.Submit);
        var changed = Event(2, SemanticTraceEventKindV1.GenerationChanged) with
        { GenerationVectorDigest = new string('c', 64) };
        var possible = Event(3, SemanticTraceEventKindV1.EffectPossible) with
        { GenerationVectorDigest = new string('c', 64) };

        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([submitted, changed]).Status);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([submitted, changed, possible]).Status);
        Assert.True(SemanticTraceValidatorV1.Validate([submitted,
            Event(2, SemanticTraceEventKindV1.EffectPossible)]).IsValid);
    }

    [Fact]
    public void QuarantineCannotSplitSubmitFromPossibleEffect()
    {
        var submitted = Event(1, SemanticTraceEventKindV1.Submit);
        var quarantined = Event(2, SemanticTraceEventKindV1.Quarantined);

        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([submitted, quarantined]).Status);
        Assert.True(SemanticTraceValidatorV1.Validate([
            submitted,
            Event(2, SemanticTraceEventKindV1.EffectPossible),
            Event(3, SemanticTraceEventKindV1.Quarantined),
        ]).IsValid);
    }

    [Fact]
    public void GenerationDriftAfterPossibleEffectRequiresQuarantineBeforeAnySuccessProjection()
    {
        var prefix = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.GenerationChanged).ToArray();
        prefix[2] = prefix[2] with { GenerationVectorDigest = new string('c', 64) };
        Assert.True(SemanticTraceValidatorV1.Validate(prefix).IsValid);

        foreach (var forbidden in new[]
                 {
                     SemanticTraceEventKindV1.RetireOrComplete,
                     SemanticTraceEventKindV1.Visible,
                     SemanticTraceEventKindV1.Published,
                     SemanticTraceEventKindV1.Settled,
                     SemanticTraceEventKindV1.Released,
                 })
        {
            var continuation = Event(4, forbidden) with
            { GenerationVectorDigest = new string('c', 64) };
            Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
                SemanticTraceValidatorV1.Validate([.. prefix, continuation]).Status);
        }

        var quarantined = Event(4, SemanticTraceEventKindV1.Quarantined) with
        { GenerationVectorDigest = new string('c', 64) };
        Assert.True(SemanticTraceValidatorV1.Validate([.. prefix, quarantined]).IsValid);
        var published = Event(5, SemanticTraceEventKindV1.Published) with
        { GenerationVectorDigest = new string('c', 64) };
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([.. prefix, quarantined, published]).Status);
    }

    [Fact]
    public void PublishedEffectCannotLaterBeClosedAsNeverPublished()
    {
        var published = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete,
            SemanticTraceEventKindV1.Visible,
            SemanticTraceEventKindV1.Published);
        var quarantined = Event(6, SemanticTraceEventKindV1.Quarantined);
        Assert.True(SemanticTraceValidatorV1.Validate([.. published, quarantined]).IsValid);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([.. published, quarantined,
                Event(7, SemanticTraceEventKindV1.EffectClosedWithoutPublication)]).Status);

        var drift = Event(6, SemanticTraceEventKindV1.GenerationChanged) with
        { GenerationVectorDigest = new string('c', 64) };
        var postDriftQuarantine = Event(7, SemanticTraceEventKindV1.Quarantined) with
        { GenerationVectorDigest = new string('c', 64) };
        var falseClosure = Event(8, SemanticTraceEventKindV1.EffectClosedWithoutPublication) with
        { GenerationVectorDigest = new string('c', 64) };
        Assert.True(SemanticTraceValidatorV1.Validate(
            [.. published, drift, postDriftQuarantine]).IsValid);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate(
                [.. published, drift, postDriftQuarantine, falseClosure]).Status);
    }

    [Fact]
    public void GenerationDriftAfterSettlementCannotAuthorizeReleaseProjection()
    {
        var publishedAndSettled = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete,
            SemanticTraceEventKindV1.Visible,
            SemanticTraceEventKindV1.Published,
            SemanticTraceEventKindV1.Settled);
        var changed = Event(7, SemanticTraceEventKindV1.GenerationChanged) with
        { GenerationVectorDigest = new string('c', 64) };
        var release = Event(8, SemanticTraceEventKindV1.Released) with
        { GenerationVectorDigest = new string('c', 64) };

        Assert.True(SemanticTraceValidatorV1.Validate([.. publishedAndSettled, changed]).IsValid);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([.. publishedAndSettled, changed, release]).Status);

        var closedWithoutPublication = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined,
            SemanticTraceEventKindV1.EffectClosedWithoutPublication,
            SemanticTraceEventKindV1.Settled);
        var changedAfterClosure = Event(6, SemanticTraceEventKindV1.GenerationChanged) with
        { GenerationVectorDigest = new string('c', 64) };
        var staleRelease = Event(7, SemanticTraceEventKindV1.Released) with
        { GenerationVectorDigest = new string('c', 64) };
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([
                .. closedWithoutPublication, changedAfterClosure, staleRelease]).Status);
    }

    [Fact]
    public void AmbiguousPublicationRequiresExactEffectClosureBeforeReleaseProjection()
    {
        var pending = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.RetireOrComplete,
            SemanticTraceEventKindV1.Visible, SemanticTraceEventKindV1.Quarantined);
        Assert.True(SemanticTraceValidatorV1.Validate(pending).IsValid);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([.. pending,
                Event(6, SemanticTraceEventKindV1.Released)]).Status);

        var closed = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.RetireOrComplete,
            SemanticTraceEventKindV1.Visible, SemanticTraceEventKindV1.Quarantined,
            SemanticTraceEventKindV1.EffectClosedWithoutPublication,
            SemanticTraceEventKindV1.Settled, SemanticTraceEventKindV1.Released);
        Assert.True(SemanticTraceValidatorV1.Validate(closed).IsValid);
        var settledBeforeClosure = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.RetireOrComplete,
            SemanticTraceEventKindV1.Visible, SemanticTraceEventKindV1.Quarantined,
            SemanticTraceEventKindV1.Settled,
            SemanticTraceEventKindV1.EffectClosedWithoutPublication,
            SemanticTraceEventKindV1.Released);
        Assert.True(SemanticTraceValidatorV1.Validate(settledBeforeClosure).IsValid);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate(Kinds(
                SemanticTraceEventKindV1.Submit,
                SemanticTraceEventKindV1.EffectPossible,
                SemanticTraceEventKindV1.EffectClosedWithoutPublication)).Status);
    }

    [Fact]
    public void CancellationRequestPreservesPossibleEffectAndRequiresTerminalEvidence()
    {
        var pending = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.CancellationRequested);
        Assert.True(SemanticTraceValidatorV1.Validate(pending).IsValid);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([.. pending,
                Event(4, SemanticTraceEventKindV1.Released)]).Status);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([.. pending,
                Event(4, SemanticTraceEventKindV1.CancellationRequested)]).Status);
        Assert.True(SemanticTraceValidatorV1.Validate(Kinds(
            SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.CancellationRequested,
            SemanticTraceEventKindV1.RetireOrComplete,
            SemanticTraceEventKindV1.Visible,
            SemanticTraceEventKindV1.Published)).IsValid);
        Assert.True(SemanticTraceValidatorV1.Validate(Kinds(
            SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.CancellationRequested,
            SemanticTraceEventKindV1.Quarantined)).IsValid);
    }

    [Fact]
    public void ProjectionErasesOnlyProviderPrivateEvents()
    {
        ProviderTraceEventV1[] source =
        [
            Provider(1, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.Submit),
            Provider(2, ProviderTraceDispositionV1.ProviderPrivate, default),
            Provider(3, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.EffectPossible),
        ];

        var projection = SemanticTraceProjectionV1.Project(source);

        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible],
            projection.Select(item => item.Kind));
        Assert.Equal([1UL, 3UL], projection.Select(item => item.Sequence));
    }

    [Theory]
    [InlineData(SemanticTraceEventKindV1.EffectPossible)]
    [InlineData(SemanticTraceEventKindV1.Published)]
    [InlineData(SemanticTraceEventKindV1.GenerationChanged)]
    [InlineData(SemanticTraceEventKindV1.EffectClosedWithoutPublication)]
    public void ProviderPrivateLabelCannotEraseSemanticTransition(SemanticTraceEventKindV1 kind)
    {
        var source = new[]
        {
            Provider(1, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.Submit),
            Provider(2, ProviderTraceDispositionV1.ProviderPrivate, kind),
            Provider(3, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.EffectPossible),
        };

        Assert.Throws<InvalidOperationException>(() => SemanticTraceProjectionV1.Project(source));
    }

    [Fact]
    public void ProviderPrivateLabelCannotHideCrossOperationOrReorderedEvidence()
    {
        var submit = Provider(1, ProviderTraceDispositionV1.SecurityRelevant,
            SemanticTraceEventKindV1.Submit);
        var privateEvent = Provider(2, ProviderTraceDispositionV1.ProviderPrivate, default);

        Assert.Throws<InvalidOperationException>(() => SemanticTraceProjectionV1.Project([
            submit, privateEvent with { OperationCorrelation = "different-operation" }
        ]));
        Assert.Throws<InvalidOperationException>(() => SemanticTraceProjectionV1.Project([
            submit, privateEvent with { Sequence = 1 }
        ]));
        Assert.Throws<ArgumentException>(() => SemanticTraceProjectionV1.Project([
            submit, privateEvent with { EvidenceDigest = new string('X', 64) }
        ]));
    }

    [Fact]
    public void ProviderPrivateTailCannotConcealGenerationDrift()
    {
        var source = new[]
        {
            Provider(1, ProviderTraceDispositionV1.SecurityRelevant,
                SemanticTraceEventKindV1.Submit),
            Provider(2, ProviderTraceDispositionV1.SecurityRelevant,
                SemanticTraceEventKindV1.EffectPossible),
            Provider(3, ProviderTraceDispositionV1.ProviderPrivate, default) with
            { GenerationVectorDigest = new string('c', 64) },
        };

        Assert.Throws<InvalidOperationException>(() => SemanticTraceProjectionV1.Project(source));
        Assert.Throws<InvalidOperationException>(() => SemanticTraceProjectionV1.Project([
            .. source,
            Provider(4, ProviderTraceDispositionV1.ProviderPrivate, default),
        ]));
    }

    [Fact]
    public void ProviderPrivatePrefixCannotConcealGenerationDrift()
    {
        var privatePrefix = Provider(1, ProviderTraceDispositionV1.ProviderPrivate, default);
        var sameGeneration = Provider(2, ProviderTraceDispositionV1.SecurityRelevant,
            SemanticTraceEventKindV1.Submit);
        var projected = SemanticTraceProjectionV1.Project([privatePrefix, sameGeneration]);
        Assert.Equal(SemanticTraceEventKindV1.Submit, Assert.Single(projected).Kind);

        var changedPrivate = Provider(2, ProviderTraceDispositionV1.ProviderPrivate, default) with
        { GenerationVectorDigest = new string('c', 64) };
        Assert.Throws<InvalidOperationException>(() => SemanticTraceProjectionV1.Project([
            privatePrefix, changedPrivate,
        ]));
        Assert.Throws<InvalidOperationException>(() => SemanticTraceProjectionV1.Project([
            privatePrefix, sameGeneration with { GenerationVectorDigest = new string('c', 64) },
        ]));
    }

    [Fact]
    public void TraceAndSourceTupleDigestsRejectNonCanonicalCase()
    {
        var trace = Event(1, SemanticTraceEventKindV1.Submit);
        Assert.Throws<ArgumentException>(() => (trace with
        {
            GenerationVectorDigest = new string('A', 64),
        }).Validate());
        Assert.Throws<ArgumentException>(() => (trace with
        {
            EvidenceDigest = new string('B', 64),
        }).Validate());
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidEvent,
            SemanticTraceValidatorV1.Validate([trace with
            {
                EvidenceDigest = new string('B', 64),
            }]).Status);
        Assert.Throws<ArgumentException>(() => SemanticTraceDifferentialV1.CompareAllowedProjection(
            [trace], [trace], new string('A', 64), new string('b', 64)));
    }

    [Fact]
    public void CounterexampleIsReproducibleAndSensitiveToMutatedTransition()
    {
        var first = SemanticTraceValidatorV1.Validate(Kinds(
            SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.Published));
        var second = SemanticTraceValidatorV1.Validate(Kinds(
            SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.Published));
        var different = SemanticTraceValidatorV1.Validate(Kinds(
            SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.Released));

        Assert.Equal(first.Counterexample!.CanonicalId, second.Counterexample!.CanonicalId);
        Assert.NotEqual(first.Counterexample.CanonicalId, different.Counterexample!.CanonicalId);
        Assert.False(first.Counterexample.AuthorizesExecution);
    }

    [Fact]
    public void MixedOperationCounterexampleDistinguishesTheOffendingIdentity()
    {
        var first = Event(1, SemanticTraceEventKindV1.Submit);
        var second = Event(2, SemanticTraceEventKindV1.EffectPossible);
        var one = SemanticTraceValidatorV1.Validate([
            first, second with { OperationCorrelation = "operation-2" }]);
        var repeated = SemanticTraceValidatorV1.Validate([
            first, second with { OperationCorrelation = "operation-2" }]);
        var other = SemanticTraceValidatorV1.Validate([
            first, second with { OperationCorrelation = "operation-3" }]);

        Assert.Equal(SemanticTraceValidationStatusV1.MixedOperation, one.Status);
        Assert.Equal(one.Counterexample!.CanonicalId, repeated.Counterexample!.CanonicalId);
        Assert.NotEqual(one.Counterexample.CanonicalId, other.Counterexample!.CanonicalId);
    }

    [Fact]
    public void MixedOperationDuplicateSequenceAndEventsAfterReleaseAreRejected()
    {
        var mixed = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible).ToArray();
        mixed[1] = mixed[1] with { OperationCorrelation = "operation-2" };
        Assert.Equal(SemanticTraceValidationStatusV1.MixedOperation,
            SemanticTraceValidatorV1.Validate(mixed).Status);

        var duplicate = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible).ToArray();
        duplicate[1] = duplicate[1] with { Sequence = 1 };
        Assert.Equal(SemanticTraceValidationStatusV1.NonMonotonicSequence,
            SemanticTraceValidatorV1.Validate(duplicate).Status);

        var released = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible,
            SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Settled,
            SemanticTraceEventKindV1.Released, SemanticTraceEventKindV1.GenerationChanged);
        Assert.Equal(SemanticTraceValidationStatusV1.EventAfterRelease,
            SemanticTraceValidatorV1.Validate(released).Status);
    }

    [Fact]
    public void DifferentialCounterexampleIsTupleBoundAndNonAuthoritative()
    {
        var reference = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.Quarantined);
        var candidate = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.RetireOrComplete);
        var first = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, candidate,
            new string('a', 64), new string('b', 64));
        var rebound = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, candidate,
            new string('a', 64), new string('c', 64));

        Assert.Equal(SemanticTraceComparisonStatusV1.EventMismatch, first.Status);
        Assert.NotEqual(first.Difference!.CanonicalId, rebound.Difference!.CanonicalId);
        Assert.False(first.Difference.AuthorizesExecution);
        Assert.False(first.Difference.AuthorizesPublication);
    }

    [Fact]
    public void InvalidCandidateDifferenceIdIncludesTheValidationCounterexample()
    {
        var reference = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible);
        var invalidPublish = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.Published);
        var invalidRelease = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.Released);
        var tuple = new string('a', 64);

        var first = SemanticTraceDifferentialV1.CompareAllowedProjection(
            reference, invalidPublish, tuple, tuple);
        var repeated = SemanticTraceDifferentialV1.CompareAllowedProjection(
            reference, invalidPublish, tuple, tuple);
        var changed = SemanticTraceDifferentialV1.CompareAllowedProjection(
            reference, invalidRelease, tuple, tuple);

        Assert.Equal(SemanticTraceComparisonStatusV1.InvalidCandidate, first.Status);
        Assert.Equal(first.Difference!.CanonicalId, repeated.Difference!.CanonicalId);
        Assert.NotEqual(first.Difference.CanonicalId, changed.Difference!.CanonicalId);

        var mixedTwo = reference.ToArray();
        mixedTwo[1] = mixedTwo[1] with { OperationCorrelation = "operation-2" };
        var mixedThree = reference.ToArray();
        mixedThree[1] = mixedThree[1] with { OperationCorrelation = "operation-3" };
        var differentIdentity = SemanticTraceDifferentialV1.CompareAllowedProjection(
            reference, mixedTwo, tuple, tuple);
        var anotherIdentity = SemanticTraceDifferentialV1.CompareAllowedProjection(
            reference, mixedThree, tuple, tuple);
        Assert.Equal(SemanticTraceComparisonStatusV1.InvalidCandidate, differentIdentity.Status);
        Assert.NotEqual(differentIdentity.Difference!.CanonicalId,
            anotherIdentity.Difference!.CanonicalId);
    }

    [Fact]
    public void DifferentialComparisonAcceptsTheSameAllowedProjectionAcrossPrivateDetails()
    {
        var reference = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.Quarantined);
        var candidate = reference.Select(item => item with
        {
            OperationCorrelation = "other-operation",
            Source = "heterogeneous-provider",
            Sequence = item.Sequence + 10,
            EvidenceDigest = new string('c', 64),
        }).ToArray();

        var result = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, candidate,
            new string('a', 64), new string('b', 64));

        Assert.True(result.IsEquivalent);
        Assert.Null(result.Difference);
    }

    private static IReadOnlyList<SemanticTraceEventV1> Kinds(params SemanticTraceEventKindV1[] kinds) =>
        kinds.Select((kind, index) => Event(index + 1, kind)).ToArray();

    private static SemanticTraceEventV1 Event(int sequence, SemanticTraceEventKindV1 kind) =>
        new(1, "operation-1", checked((ulong)sequence), kind, "provider",
            new string('a', 64), new string('b', 64));

    private static ProviderTraceEventV1 Provider(
        ulong sequence,
        ProviderTraceDispositionV1 disposition,
        SemanticTraceEventKindV1 kind) =>
        new(1, "operation-1", sequence, disposition, kind, "provider",
            new string('a', 64), new string('b', 64));
}
