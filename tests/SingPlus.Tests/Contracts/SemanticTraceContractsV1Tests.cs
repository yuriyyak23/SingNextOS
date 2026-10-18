using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class SemanticTraceContractsV1Tests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void PreSubmitAccountingMarkerCannotBeSubstitutedAfterEffectBoundary(int phase)
    {
        var trace = Kinds(SemanticTraceEventKindV1.Submit).ToList();
        if (phase > 0) trace.Add(Event(2, SemanticTraceEventKindV1.EffectPossible));
        if (phase is >= 2 and <= 5) trace.Add(Event(3, SemanticTraceEventKindV1.RetireOrComplete));
        if (phase is >= 3 and <= 5)
        {
            trace.Add(Event(4, SemanticTraceEventKindV1.Visible));
            trace.Add(Event(5, SemanticTraceEventKindV1.Published));
        }
        if (phase is 4 or 5) trace.Add(Event(6, SemanticTraceEventKindV1.Settled));
        if (phase == 5) trace.Add(Event(7, SemanticTraceEventKindV1.Released));
        if (phase == 6) trace.Add(Event(3, SemanticTraceEventKindV1.GenerationChanged) with { GenerationVectorDigest = new string('c', 64) });
        if (phase == 7) trace.Add(Event(3, SemanticTraceEventKindV1.Quarantined));
        trace.Add(Event(trace.Count + 1, SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit)
            with { GenerationVectorDigest = new string(phase == 6 ? 'c' : 'a', 64) });
        Assert.False(SemanticTraceValidatorV1.Validate(trace).IsValid);
    }

    [Fact]
    public void PreSubmitAccountingSurvivesCancellationLocalReleaseAndCannotImplyNoResourceClosure()
    {
        var before = Kinds(SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit,
            SemanticTraceEventKindV1.CancelledBeforeSubmit, SemanticTraceEventKindV1.LocalAuthorityReleasedBeforeSubmit);
        var late = Kinds(SemanticTraceEventKindV1.CancelledBeforeSubmit,
            SemanticTraceEventKindV1.LocalAuthorityReleasedBeforeSubmit, SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit);
        Assert.True(SemanticTraceValidatorV1.Validate(before).IsValid);
        Assert.True(SemanticTraceValidatorV1.Validate(late).IsValid);
        Assert.True(SemanticTraceValidatorV1.Validate(Kinds(SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit)).IsValid);
        Assert.Equal(SemanticTraceComparisonStatusV1.EventMismatch,
            SemanticTraceDifferentialV1.CompareAllowedProjection(before, late, new string('a', 64), new string('b', 64)).Status);
        var submitted = Kinds(SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit,
            SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.Quarantined);
        Assert.True(SemanticTraceValidatorV1.Validate(submitted).IsValid);
        Assert.False(SemanticTraceValidatorV1.Validate([.. submitted, Event(5,
            SemanticTraceEventKindV1.LocalAuthorityReleasedWithoutResourceBinding)]).IsValid);
        Assert.False(SemanticTraceValidatorV1.Validate([.. late, Event(4, SemanticTraceEventKindV1.Submit)]).IsValid);
        Assert.Equal(16, (byte)SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit);
        Assert.All(late, item => Assert.False(item.AuthorizesEffect));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void NonResourceLocalReleaseCannotBypassActiveAccountingOrDriftFacts(int phase)
    {
        var kinds = phase switch
        {
            0 => Array.Empty<SemanticTraceEventKindV1>(),
            1 => [SemanticTraceEventKindV1.Submit],
            2 => [SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible],
            3 => [SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.RetireOrComplete],
            4 => [SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible],
            5 => [SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible, SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Settled],
            6 => [SemanticTraceEventKindV1.CancelledBeforeSubmit],
            7 => [SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.ResourceAccountingQuarantined, SemanticTraceEventKindV1.Quarantined],
            _ => [SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined],
        };
        var trace = Kinds(kinds).ToList();
        if (phase == 8)
            for (var index = 2; index < trace.Count; index++)
                trace[index] = trace[index] with { GenerationVectorDigest = new string('c', 64) };
        trace.Add(Event(trace.Count + 1, SemanticTraceEventKindV1.LocalAuthorityReleasedWithoutResourceBinding)
            with { GenerationVectorDigest = new string(phase == 8 ? 'c' : 'a', 64) });
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder, SemanticTraceValidatorV1.Validate(trace).Status);
    }

    [Fact]
    public void NonResourceLocalReleaseIsTerminalObservationAndCannotReplaceGenericRelease()
    {
        foreach (var boundary in new[] { SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.Published,
                     SemanticTraceEventKindV1.EffectClosedWithoutPublication })
        {
            var prefix = boundary == SemanticTraceEventKindV1.Published
                ? Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                    SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible, boundary).ToList()
                : Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                    SemanticTraceEventKindV1.Quarantined).ToList();
            if (boundary == SemanticTraceEventKindV1.EffectClosedWithoutPublication)
                prefix.Add(Event(prefix.Count + 1, boundary));
            prefix.Add(Event(prefix.Count + 1, SemanticTraceEventKindV1.LocalAuthorityReleasedWithoutResourceBinding));
            Assert.True(SemanticTraceValidatorV1.Validate(prefix).IsValid);
            foreach (var kind in Enum.GetValues<SemanticTraceEventKindV1>())
                Assert.Equal(SemanticTraceValidationStatusV1.EventAfterRelease,
                    SemanticTraceValidatorV1.Validate([.. prefix, Event(prefix.Count + 1, kind)]).Status);
            var generic = prefix.ToArray();
            generic[^1] = generic[^1] with { Kind = SemanticTraceEventKindV1.Released };
            Assert.Equal(SemanticTraceComparisonStatusV1.InvalidCandidate,
                SemanticTraceDifferentialV1.CompareAllowedProjection(prefix, generic,
                    new string('a', 64), new string('b', 64)).Status);
        }
        Assert.Equal(15, (byte)SemanticTraceEventKindV1.LocalAuthorityReleasedWithoutResourceBinding);
    }

    [Theory]
    [InlineData(SemanticTraceEventKindV1.Submit)]
    [InlineData(SemanticTraceEventKindV1.EffectPossible)]
    [InlineData(SemanticTraceEventKindV1.RetireOrComplete)]
    [InlineData(SemanticTraceEventKindV1.Visible)]
    [InlineData(SemanticTraceEventKindV1.Published)]
    [InlineData(SemanticTraceEventKindV1.Quarantined)]
    [InlineData(SemanticTraceEventKindV1.GenerationChanged)]
    [InlineData(SemanticTraceEventKindV1.Settled)]
    [InlineData(SemanticTraceEventKindV1.Released)]
    [InlineData(SemanticTraceEventKindV1.EffectClosedWithoutPublication)]
    [InlineData(SemanticTraceEventKindV1.CancellationRequested)]
    [InlineData(SemanticTraceEventKindV1.ResourceAccountingQuarantined)]
    public void PreSubmitCancellationCannotAuthorizePostSubmitEvents(SemanticTraceEventKindV1 kind)
    {
        var prefix = Kinds(SemanticTraceEventKindV1.CancelledBeforeSubmit);
        var suffix = Event(2, kind);
        if (kind == SemanticTraceEventKindV1.GenerationChanged)
            suffix = suffix with { GenerationVectorDigest = new string('c', 64) };
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([.. prefix, suffix]).Status);
        Assert.False(SemanticTraceValidatorV1.Validate(Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.CancelledBeforeSubmit)).IsValid);
    }

    [Fact]
    public void PreSubmitBranchRetainsCommittedCancellationAndRequiresExactLocalRelease()
    {
        var cancelled = Kinds(SemanticTraceEventKindV1.CancelledBeforeSubmit);
        var repeated = Kinds(SemanticTraceEventKindV1.CancelledBeforeSubmit,
            SemanticTraceEventKindV1.CancelledBeforeSubmit, SemanticTraceEventKindV1.LocalAuthorityReleasedBeforeSubmit);
        Assert.True(SemanticTraceValidatorV1.Validate(cancelled).IsValid);
        Assert.True(SemanticTraceValidatorV1.Validate(repeated).IsValid);
        Assert.False(SemanticTraceValidatorV1.Validate(Kinds(SemanticTraceEventKindV1.LocalAuthorityReleasedBeforeSubmit)).IsValid);
        Assert.Equal(SemanticTraceComparisonStatusV1.LengthMismatch,
            SemanticTraceDifferentialV1.CompareAllowedProjection(cancelled, repeated,
                new string('a', 64), new string('b', 64)).Status);
        Assert.Equal(13, (byte)SemanticTraceEventKindV1.CancelledBeforeSubmit);
        Assert.Equal(14, (byte)SemanticTraceEventKindV1.LocalAuthorityReleasedBeforeSubmit);
        Assert.All(repeated, item => Assert.False(item.AuthorizesEffect));
    }

    [Fact]
    public void PreSubmitReleasedBranchRejectsLateEventsAndChangedGeneration()
    {
        var released = Kinds(SemanticTraceEventKindV1.CancelledBeforeSubmit,
            SemanticTraceEventKindV1.LocalAuthorityReleasedBeforeSubmit);
        foreach (var kind in Enum.GetValues<SemanticTraceEventKindV1>().Where(kind =>
                     kind != SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit))
            Assert.Equal(SemanticTraceValidationStatusV1.EventAfterRelease,
                SemanticTraceValidatorV1.Validate([.. released, Event(3, kind)]).Status);
        var stale = released.ToArray();
        stale[1] = stale[1] with { GenerationVectorDigest = new string('c', 64) };
        Assert.Equal(SemanticTraceValidationStatusV1.GenerationMismatch,
            SemanticTraceValidatorV1.Validate(stale).Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void AccountingQuarantineCannotPrecedeEffectOrResurrectSettledAccounting(int phase)
    {
        var prefix = phase switch
        {
            0 => new List<SemanticTraceEventV1>(),
            1 => Kinds(SemanticTraceEventKindV1.Submit).ToList(),
            2 => Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible,
                SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Settled).ToList(),
            3 => Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Settled).ToList(),
            _ => Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible,
                SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Settled,
                SemanticTraceEventKindV1.Released).ToList(),
        };
        prefix.Add(Event(prefix.Count + 1, SemanticTraceEventKindV1.ResourceAccountingQuarantined));
        Assert.False(SemanticTraceValidatorV1.Validate(prefix).IsValid);
    }

    [Fact]
    public void AccountingObservationCannotClearProviderQuarantineOrGenerationDrift()
    {
        var quarantined = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.ResourceAccountingQuarantined,
            SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Settled);
        Assert.True(SemanticTraceValidatorV1.Validate(quarantined).IsValid);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([.. quarantined, Event(7, SemanticTraceEventKindV1.Released)]).Status);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([.. quarantined, Event(7, SemanticTraceEventKindV1.Visible)]).Status);
        var drift = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible).ToList();
        drift.Add(Event(3, SemanticTraceEventKindV1.GenerationChanged) with { GenerationVectorDigest = new string('c', 64) });
        drift.Add(Event(4, SemanticTraceEventKindV1.ResourceAccountingQuarantined) with { GenerationVectorDigest = new string('c', 64) });
        Assert.True(SemanticTraceValidatorV1.Validate(drift).IsValid);
        drift.Add(Event(5, SemanticTraceEventKindV1.RetireOrComplete) with { GenerationVectorDigest = new string('c', 64) });
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder, SemanticTraceValidatorV1.Validate(drift).Status);
    }

    [Fact]
    public void AccountingQuarantineAndProviderQuarantineAreDistinctMandatoryObservations()
    {
        var accounting = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.ResourceAccountingQuarantined);
        var provider = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined);
        Assert.Equal(SemanticTraceComparisonStatusV1.EventMismatch,
            SemanticTraceDifferentialV1.CompareAllowedProjection(accounting, provider,
                new string('a', 64), new string('b', 64)).Status);
        Assert.Equal(12, (byte)SemanticTraceEventKindV1.ResourceAccountingQuarantined);
        Assert.Throws<NotSupportedException>(() => Event(1, (SemanticTraceEventKindV1)255).Validate());
    }

    [Theory]
    [InlineData(SemanticTraceEventKindV1.Visible)]
    [InlineData(SemanticTraceEventKindV1.Published)]
    [InlineData(SemanticTraceEventKindV1.Released)]
    [InlineData(SemanticTraceEventKindV1.RetireOrComplete)]
    public void LateCompletionNeverClearsQuarantine(SemanticTraceEventKindV1 forbidden)
    {
        var trace = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.RetireOrComplete);
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([.. trace, Event(5, forbidden)]).Status);
    }

    [Fact]
    public void LateCompletionRequiresFreshGenerationAndIndependentClosure()
    {
        var trace = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.RetireOrComplete,
            SemanticTraceEventKindV1.Settled);
        Assert.True(SemanticTraceValidatorV1.Validate([.. trace,
            Event(6, SemanticTraceEventKindV1.EffectClosedWithoutPublication),
            Event(7, SemanticTraceEventKindV1.Released)]).IsValid);
        var drift = Event(4, SemanticTraceEventKindV1.GenerationChanged) with { GenerationVectorDigest = new string('c', 64) };
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([.. trace.Take(3), drift,
                Event(5, SemanticTraceEventKindV1.RetireOrComplete) with { GenerationVectorDigest = new string('c', 64) }]).Status);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate(Kinds(SemanticTraceEventKindV1.Submit,
                SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.RetireOrComplete,
                SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.RetireOrComplete)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EarlySettlementNeverSubstitutesPublicationOrFreshClosureAfterDrift(bool visible)
    {
        var trace = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete).ToList();
        if (visible) trace.Add(Event(trace.Count + 1, SemanticTraceEventKindV1.Visible));
        trace.Add(Event(trace.Count + 1, SemanticTraceEventKindV1.Settled));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.False(SemanticTraceValidatorV1.Validate(trace.Append(Event(trace.Count + 1, SemanticTraceEventKindV1.Released))).IsValid);
        Assert.False(SemanticTraceValidatorV1.Validate(trace.Append(Event(trace.Count + 1, SemanticTraceEventKindV1.Settled))).IsValid);
        Assert.False(SemanticTraceValidatorV1.Validate(trace.Append(Event(trace.Count + 1, SemanticTraceEventKindV1.EffectClosedWithoutPublication))).IsValid);
        trace.Add(Event(trace.Count + 1, SemanticTraceEventKindV1.GenerationChanged) with { GenerationVectorDigest = new string('c', 64) });
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        var next = Event(trace.Count + 1, SemanticTraceEventKindV1.Published) with { GenerationVectorDigest = new string('c', 64) };
        Assert.False(SemanticTraceValidatorV1.Validate(trace.Append(next)).IsValid);
        trace.Add(next with { Kind = SemanticTraceEventKindV1.Quarantined });
        Assert.False(SemanticTraceValidatorV1.Validate(trace.Append(next with { Sequence = (ulong)trace.Count + 1, Kind = SemanticTraceEventKindV1.Settled })).IsValid);
        trace.Add(next with { Sequence = (ulong)trace.Count + 1, Kind = SemanticTraceEventKindV1.EffectClosedWithoutPublication });
        trace.Add(next with { Sequence = (ulong)trace.Count + 1, Kind = SemanticTraceEventKindV1.Released });
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferentialConsumerRejectsErasedSettlementHistoryBeforeComparison(bool generationDrift)
    {
        var reference = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible,
            SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Settled);
        var candidate = reference.ToList();
        if (generationDrift)
            candidate.Add(Event(7, SemanticTraceEventKindV1.GenerationChanged) with
                { GenerationVectorDigest = new string('c', 64) });
        var digest = generationDrift ? new string('c', 64) : reference[0].GenerationVectorDigest;
        candidate.Add(Event(candidate.Count + 1, SemanticTraceEventKindV1.Quarantined) with
            { GenerationVectorDigest = digest });
        candidate.Add(Event(candidate.Count + 1, SemanticTraceEventKindV1.Settled) with
            { GenerationVectorDigest = digest });
        var result = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, candidate,
            new string('a', 64), new string('b', 64));
        Assert.Equal(SemanticTraceComparisonStatusV1.InvalidCandidate, result.Status);
        Assert.False(result.IsEquivalent);
        Assert.NotNull(result.Difference);
        Assert.Equal(new string('a', 64), result.Difference.ReferenceSourceTupleDigest);
        Assert.Equal(new string('b', 64), result.Difference.CandidateSourceTupleDigest);
    }

    [Theory]
    [InlineData(SemanticTraceEventKindV1.Settled)]
    [InlineData(SemanticTraceEventKindV1.EffectClosedWithoutPublication)]
    public void GenerationChangeAfterUnsettledQuarantineRequiresFreshQuarantine(SemanticTraceEventKindV1 lateKind)
    {
        var trace = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined).ToList();
        trace.Add(Event(4, SemanticTraceEventKindV1.GenerationChanged) with
            { GenerationVectorDigest = new string('c', 64) });
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        var late = Event(5, lateKind) with { GenerationVectorDigest = new string('c', 64) };
        Assert.False(SemanticTraceValidatorV1.Validate(trace.Append(late)).IsValid);
        trace.Add(Event(5, SemanticTraceEventKindV1.Quarantined) with
            { GenerationVectorDigest = new string('c', 64) });
        trace.Add(late with { Sequence = 6 });
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
    }

    [Fact]
    public void DriftAfterSettledClosureRequiresFreshClosureWithoutSecondSettlement()
    {
        var trace = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.EffectClosedWithoutPublication,
            SemanticTraceEventKindV1.Settled).ToList();
        trace.Add(Event(6, SemanticTraceEventKindV1.GenerationChanged) with
            { GenerationVectorDigest = new string('c', 64) });
        var release = Event(7, SemanticTraceEventKindV1.Released) with
            { GenerationVectorDigest = new string('c', 64) };
        Assert.False(SemanticTraceValidatorV1.Validate(trace.Append(release)).IsValid);
        trace.Add(Event(7, SemanticTraceEventKindV1.Quarantined) with
            { GenerationVectorDigest = new string('c', 64) });
        trace.Add(Event(8, SemanticTraceEventKindV1.EffectClosedWithoutPublication) with
            { GenerationVectorDigest = new string('c', 64) });
        trace.Add(release with { Sequence = 9 });
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerationDriftCannotForgetSettlement(bool published)
    {
        var kinds = published
            ? new[] { SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible,
                SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Settled }
            : new[] { SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.Settled };
        var trace = Kinds(kinds).ToList();
        trace.Add(Event(trace.Count + 1, SemanticTraceEventKindV1.GenerationChanged) with
            { GenerationVectorDigest = new string('c', 64) });
        trace.Add(Event(trace.Count + 1, SemanticTraceEventKindV1.Quarantined) with
            { GenerationVectorDigest = new string('c', 64) });
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        trace.Add(Event(trace.Count + 1, SemanticTraceEventKindV1.Settled) with
            { GenerationVectorDigest = new string('c', 64) });
        Assert.False(SemanticTraceValidatorV1.Validate(trace).IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonpublicationQuarantineCannotForgetPriorSettlement(bool afterClosure)
    {
        var trace = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.Settled).ToList();
        if (afterClosure) trace.Add(Event(5, SemanticTraceEventKindV1.EffectClosedWithoutPublication));
        trace.Add(Event(trace.Count + 1, SemanticTraceEventKindV1.Quarantined));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        trace.Add(Event(trace.Count + 1, SemanticTraceEventKindV1.Settled));
        Assert.False(SemanticTraceValidatorV1.Validate(trace).IsValid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedQuarantineCannotForgetPostPublicationSettlement(bool settleBeforeQuarantine)
    {
        var trace = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible,
            SemanticTraceEventKindV1.Published,
            settleBeforeQuarantine ? SemanticTraceEventKindV1.Settled : SemanticTraceEventKindV1.Quarantined,
            settleBeforeQuarantine ? SemanticTraceEventKindV1.Quarantined : SemanticTraceEventKindV1.Settled,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.Settled);
        Assert.True(SemanticTraceValidatorV1.Validate(trace.Take(8)).IsValid);
        Assert.False(SemanticTraceValidatorV1.Validate(trace).IsValid);
    }

    [Fact]
    public void QuarantineAfterSettlementCannotErasePublicationHistory()
    {
        var trace = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible,
            SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Settled,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.EffectClosedWithoutPublication,
            SemanticTraceEventKindV1.Settled, SemanticTraceEventKindV1.Released);
        Assert.True(SemanticTraceValidatorV1.Validate(trace.Take(7)).IsValid);
        Assert.False(SemanticTraceValidatorV1.Validate(trace).IsValid);
    }

    [Theory]
    [InlineData(SemanticTraceEventKindV1.Quarantined)]
    [InlineData(SemanticTraceEventKindV1.GenerationChanged)]
    public void ReleasedOperationCannotReopenItsLifecycle(SemanticTraceEventKindV1 lateKind)
    {
        var prefix = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible,
            SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Settled,
            SemanticTraceEventKindV1.Released);
        Assert.True(SemanticTraceValidatorV1.Validate(prefix).IsValid);
        var late = Event(8, lateKind) with
        {
            GenerationVectorDigest = lateKind == SemanticTraceEventKindV1.GenerationChanged
                ? new string('c', 64) : prefix[0].GenerationVectorDigest,
        };
        Assert.False(SemanticTraceValidatorV1.Validate(prefix.Append(late)).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PrivateProviderSourceMetadataMustValidateBeforeErasure(int fault)
    {
        var source = fault switch { 0 => " ", 1 => new string('\uD800', 1), _ => new string('a', 257) };
        var trace = new[]
        {
            Provider(1, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.Submit),
            Provider(2, ProviderTraceDispositionV1.ProviderPrivate, default) with { ProviderIdentity = source },
            Provider(3, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.EffectPossible),
        };
        Assert.Throws<ArgumentException>(() => SemanticTraceProjectionV1.Project(trace));
    }

    [Fact]
    public void ValidPrivateSourceIdentityIsObservationRatherThanAuthenticatedProviderBinding()
    {
        var projected = SemanticTraceProjectionV1.Project([
            Provider(1, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.Submit),
            Provider(2, ProviderTraceDispositionV1.ProviderPrivate, default) with { ProviderIdentity = "other-provider" },
            Provider(3, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.EffectPossible),
        ]);
        Assert.Equal(2, projected.Count);
        Assert.True(SemanticTraceValidatorV1.Validate(projected).IsValid);
        Assert.All(projected, item => Assert.False(item.AuthorizesExecution));
    }

    [Fact]
    public void InvalidEventDiagnosticIsFailureShapeWhileDifferentialBindsSourceTuple()
    {
        var first = Event(1, SemanticTraceEventKindV1.Submit) with { OperationCorrelation = " " };
        var second = first with { OperationCorrelation = new string('\uD800', 1) };
        var left = SemanticTraceValidatorV1.Validate([first]);
        var right = SemanticTraceValidatorV1.Validate([second]);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidEvent, left.Status);
        Assert.Equal(left.Status, right.Status);
        Assert.Equal(left.Counterexample!.CanonicalId, right.Counterexample!.CanonicalId);
        Assert.False(left.Counterexample.AuthorizesExecution);
        var reference = Kinds(SemanticTraceEventKindV1.Submit);
        var a = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, [first], new string('a', 64), new string('b', 64));
        var b = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, [second], new string('a', 64), new string('c', 64));
        Assert.Equal(SemanticTraceComparisonStatusV1.InvalidCandidate, a.Status);
        Assert.NotEqual(a.Difference!.CanonicalId, b.Difference!.CanonicalId);
    }

    [Fact]
    public void CounterexampleIdentityIsStableAcrossCustomNumericCulture()
    {
        var original = global::System.Globalization.CultureInfo.CurrentCulture;
        var reference = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible);
        var candidate = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.Released);
        try
        {
            global::System.Globalization.CultureInfo.CurrentCulture = global::System.Globalization.CultureInfo.InvariantCulture;
            var validation = SemanticTraceValidatorV1.Validate(candidate).Counterexample!.CanonicalId;
            var difference = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, candidate,
                new string('a', 64), new string('b', 64)).Difference!.CanonicalId;
            var custom = (global::System.Globalization.CultureInfo)original.Clone();
            custom.NumberFormat.NegativeSign = "negative";
            custom.NumberFormat.PositiveSign = "positive";
            custom.NumberFormat.NumberDecimalSeparator = ":";
            custom.NumberFormat.NativeDigits = ["٠", "١", "٢", "٣", "٤", "٥", "٦", "٧", "٨", "٩"];
            global::System.Globalization.CultureInfo.CurrentCulture = custom;
            Assert.Equal(validation, SemanticTraceValidatorV1.Validate(candidate).Counterexample!.CanonicalId);
            var compared = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, candidate,
                new string('a', 64), new string('b', 64));
            Assert.Equal(difference, compared.Difference!.CanonicalId);
            Assert.NotEqual(difference, SemanticTraceDifferentialV1.CompareAllowedProjection(reference, candidate,
                new string('c', 64), new string('b', 64)).Difference!.CanonicalId);
        }
        finally { global::System.Globalization.CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TraceTokenLengthRefusalPrecedesMalformedUnicodeScan(bool overlong, bool source)
    {
        var token = new string('a', overlong ? 256 : 255) + '\uD800';
        var item = Event(1, SemanticTraceEventKindV1.Submit);
        item = source ? item with { Source = token } : item with { OperationCorrelation = token };
        var exception = Assert.Throws<ArgumentException>(() => item.Validate());
        Assert.Equal(source ? nameof(item.Source) : nameof(item.OperationCorrelation), exception.ParamName);
        if (overlong) Assert.Null(exception.InnerException);
        else Assert.IsType<global::System.Text.EncoderFallbackException>(exception.InnerException);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidEvent, SemanticTraceValidatorV1.Validate([item]).Status);
    }

    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void MalformedUnicodeTraceIdentifiersFailClosed(int codeUnit)
    {
        var malformed = new string((char)codeUnit, 1);
        var correlation = Event(1, SemanticTraceEventKindV1.Submit) with { OperationCorrelation = malformed };
        var source = Event(1, SemanticTraceEventKindV1.Submit) with { Source = malformed };
        Assert.Throws<ArgumentException>(() => correlation.Validate());
        Assert.Throws<ArgumentException>(() => source.Validate());
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidEvent,
            SemanticTraceValidatorV1.Validate([correlation]).Status);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidEvent,
            SemanticTraceValidatorV1.Validate([source]).Status);
    }

    [Fact]
    public void TraceTokensPreserveValidSupplementaryUnicodeAtExactByteBoundary()
    {
        var token = string.Concat(Enumerable.Repeat("\U0001f680", 64));
        var item = Event(1, SemanticTraceEventKindV1.Submit) with { OperationCorrelation = token, Source = "\ufffd" };
        Assert.Equal(item, item.Validate());
        Assert.True(SemanticTraceValidatorV1.Validate([item]).IsValid);
        Assert.Throws<ArgumentException>(() => (item with { OperationCorrelation = token + "a" }).Validate());
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectedPrivatePrefixCannotMakeGenerationReplayEquivalent(bool includePrefix)
    {
        var source = new List<ProviderTraceEventV1>();
        if (includePrefix)
            source.Add(Provider(1, ProviderTraceDispositionV1.ProviderPrivate, default));
        source.Add(Provider(2, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.Submit));
        source.Add(Provider(3, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.EffectPossible));
        source.Add(Provider(4, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.GenerationChanged)
            with { GenerationVectorDigest = new string('c', 64) });
        source.Add(Provider(5, ProviderTraceDispositionV1.ProviderPrivate, default)
            with { GenerationVectorDigest = new string('c', 64) });
        source.Add(Provider(6, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.GenerationChanged));

        var projected = SemanticTraceProjectionV1.Project(source);
        Assert.Equal(4, projected.Count);
        Assert.Equal(SemanticTraceValidationStatusV1.GenerationMismatch,
            SemanticTraceValidatorV1.Validate(projected).Status);
        Assert.Equal(SemanticTraceComparisonStatusV1.InvalidCandidate,
            SemanticTraceDifferentialV1.CompareAllowedProjection(
                Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible),
                projected, new string('a', 64), new string('b', 64)).Status);
    }

    [Fact]
    public void PrivatePrefixDriftMarkerCannotBecomeValidInitialSemanticEvent()
    {
        var projected = SemanticTraceProjectionV1.Project([
            Provider(1, ProviderTraceDispositionV1.ProviderPrivate, default),
            Provider(2, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.GenerationChanged)
                with { GenerationVectorDigest = new string('c', 64) },
        ]);
        Assert.Single(projected);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate(projected).Status);
        Assert.Equal(SemanticTraceComparisonStatusV1.InvalidCandidate,
            SemanticTraceDifferentialV1.CompareAllowedProjection(
                Kinds(SemanticTraceEventKindV1.Submit), projected,
                new string('a', 64), new string('b', 64)).Status);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerationTransitionCannotReuseAnObservedDigest(bool closeAndRelease)
    {
        var events = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined,
            SemanticTraceEventKindV1.EffectClosedWithoutPublication, SemanticTraceEventKindV1.Settled,
            SemanticTraceEventKindV1.Released).Select((item, index) => item with
            { GenerationVectorDigest = new string(index is 2 or 3 ? 'b' : 'a', 64) }).ToArray();
        if (!closeAndRelease) events = events[..5];
        Assert.Equal(SemanticTraceValidationStatusV1.GenerationMismatch, SemanticTraceValidatorV1.Validate(events).Status);
        var comparison = SemanticTraceDifferentialV1.CompareAllowedProjection(Kinds(
            SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible), events,
            new string('a', 64), new string('b', 64));
        Assert.Equal(SemanticTraceComparisonStatusV1.InvalidCandidate, comparison.Status);
    }

    [Fact]
    public void DistinctGenerationTransitionsRetainQuarantineAndExactClosureProjection()
    {
        var events = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined,
            SemanticTraceEventKindV1.EffectClosedWithoutPublication, SemanticTraceEventKindV1.Settled,
            SemanticTraceEventKindV1.Released).Select((item, index) => item with
            { GenerationVectorDigest = new string(index < 2 ? 'a' : index < 4 ? 'b' : 'c', 64) }).ToArray();
        Assert.True(SemanticTraceValidatorV1.Validate(events).IsValid);
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
