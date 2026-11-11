using System.Globalization;
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
        List<SemanticTraceEventV1> trace = Kinds(SemanticTraceEventKindV1.Submit).ToList();
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
        IReadOnlyList<SemanticTraceEventV1> before = Kinds(SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit,
            SemanticTraceEventKindV1.CancelledBeforeSubmit, SemanticTraceEventKindV1.LocalAuthorityReleasedBeforeSubmit);
        IReadOnlyList<SemanticTraceEventV1> late = Kinds(SemanticTraceEventKindV1.CancelledBeforeSubmit,
            SemanticTraceEventKindV1.LocalAuthorityReleasedBeforeSubmit, SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit);
        Assert.True(SemanticTraceValidatorV1.Validate(before).IsValid);
        Assert.True(SemanticTraceValidatorV1.Validate(late).IsValid);
        Assert.True(SemanticTraceValidatorV1.Validate(Kinds(SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit)).IsValid);
        Assert.Equal(SemanticTraceComparisonStatusV1.EventMismatch,
            SemanticTraceDifferentialV1.CompareAllowedProjection(before, late, new string('a', 64), new string('b', 64)).Status);
        IReadOnlyList<SemanticTraceEventV1> submitted = Kinds(SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit,
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
        SemanticTraceEventKindV1[] kinds = phase switch
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
        List<SemanticTraceEventV1> trace = Kinds(kinds).ToList();
        if (phase == 8)
            for (int index = 2; index < trace.Count; index++)
                trace[index] = trace[index] with { GenerationVectorDigest = new string('c', 64) };
        trace.Add(Event(trace.Count + 1, SemanticTraceEventKindV1.LocalAuthorityReleasedWithoutResourceBinding)
            with { GenerationVectorDigest = new string(phase == 8 ? 'c' : 'a', 64) });
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder, SemanticTraceValidatorV1.Validate(trace).Status);
    }

    [Fact]
    public void NonResourceLocalReleaseIsTerminalObservationAndCannotReplaceGenericRelease()
    {
        foreach (SemanticTraceEventKindV1 boundary in new[] { SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.Published,
                     SemanticTraceEventKindV1.EffectClosedWithoutPublication })
        {
            List<SemanticTraceEventV1> prefix = boundary == SemanticTraceEventKindV1.Published
                ? Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                    SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible, boundary).ToList()
                : Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                    SemanticTraceEventKindV1.Quarantined).ToList();
            if (boundary == SemanticTraceEventKindV1.EffectClosedWithoutPublication)
                prefix.Add(Event(prefix.Count + 1, boundary));
            prefix.Add(Event(prefix.Count + 1, SemanticTraceEventKindV1.LocalAuthorityReleasedWithoutResourceBinding));
            Assert.True(SemanticTraceValidatorV1.Validate(prefix).IsValid);
            foreach (SemanticTraceEventKindV1 kind in Enum.GetValues<SemanticTraceEventKindV1>())
                Assert.Equal(SemanticTraceValidationStatusV1.EventAfterRelease,
                    SemanticTraceValidatorV1.Validate([.. prefix, Event(prefix.Count + 1, kind)]).Status);
            SemanticTraceEventV1[] generic = prefix.ToArray();
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
        IReadOnlyList<SemanticTraceEventV1> prefix = Kinds(SemanticTraceEventKindV1.CancelledBeforeSubmit);
        SemanticTraceEventV1 suffix = Event(2, kind);
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
        IReadOnlyList<SemanticTraceEventV1> cancelled = Kinds(SemanticTraceEventKindV1.CancelledBeforeSubmit);
        IReadOnlyList<SemanticTraceEventV1> repeated = Kinds(SemanticTraceEventKindV1.CancelledBeforeSubmit,
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
        IReadOnlyList<SemanticTraceEventV1> released = Kinds(SemanticTraceEventKindV1.CancelledBeforeSubmit,
            SemanticTraceEventKindV1.LocalAuthorityReleasedBeforeSubmit);
        foreach (SemanticTraceEventKindV1 kind in Enum.GetValues<SemanticTraceEventKindV1>().Where(kind =>
                     kind != SemanticTraceEventKindV1.ResourceAccountingQuarantinedBeforeSubmit))
            Assert.Equal(SemanticTraceValidationStatusV1.EventAfterRelease,
                SemanticTraceValidatorV1.Validate([.. released, Event(3, kind)]).Status);
        SemanticTraceEventV1[] stale = released.ToArray();
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
        List<SemanticTraceEventV1> prefix = phase switch
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
        IReadOnlyList<SemanticTraceEventV1> quarantined = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.ResourceAccountingQuarantined,
            SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Settled);
        Assert.True(SemanticTraceValidatorV1.Validate(quarantined).IsValid);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([.. quarantined, Event(7, SemanticTraceEventKindV1.Released)]).Status);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([.. quarantined, Event(7, SemanticTraceEventKindV1.Visible)]).Status);
        List<SemanticTraceEventV1> drift = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible).ToList();
        drift.Add(Event(3, SemanticTraceEventKindV1.GenerationChanged) with { GenerationVectorDigest = new string('c', 64) });
        drift.Add(Event(4, SemanticTraceEventKindV1.ResourceAccountingQuarantined) with { GenerationVectorDigest = new string('c', 64) });
        Assert.True(SemanticTraceValidatorV1.Validate(drift).IsValid);
        drift.Add(Event(5, SemanticTraceEventKindV1.RetireOrComplete) with { GenerationVectorDigest = new string('c', 64) });
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder, SemanticTraceValidatorV1.Validate(drift).Status);
    }

    [Fact]
    public void AccountingQuarantineAndProviderQuarantineAreDistinctMandatoryObservations()
    {
        IReadOnlyList<SemanticTraceEventV1> accounting = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.ResourceAccountingQuarantined);
        IReadOnlyList<SemanticTraceEventV1> provider = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
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
        IReadOnlyList<SemanticTraceEventV1> trace = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.RetireOrComplete);
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([.. trace, Event(5, forbidden)]).Status);
    }

    [Fact]
    public void LateCompletionRequiresFreshGenerationAndIndependentClosure()
    {
        IReadOnlyList<SemanticTraceEventV1> trace = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.RetireOrComplete,
            SemanticTraceEventKindV1.Settled);
        Assert.True(SemanticTraceValidatorV1.Validate([.. trace,
            Event(6, SemanticTraceEventKindV1.EffectClosedWithoutPublication),
            Event(7, SemanticTraceEventKindV1.Released)]).IsValid);
        SemanticTraceEventV1 drift = Event(4, SemanticTraceEventKindV1.GenerationChanged) with { GenerationVectorDigest = new string('c', 64) };
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
        List<SemanticTraceEventV1> trace = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete).ToList();
        if (visible) trace.Add(Event(trace.Count + 1, SemanticTraceEventKindV1.Visible));
        trace.Add(Event(trace.Count + 1, SemanticTraceEventKindV1.Settled));
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        Assert.False(SemanticTraceValidatorV1.Validate(trace.Append(Event(trace.Count + 1, SemanticTraceEventKindV1.Released))).IsValid);
        Assert.False(SemanticTraceValidatorV1.Validate(trace.Append(Event(trace.Count + 1, SemanticTraceEventKindV1.Settled))).IsValid);
        Assert.False(SemanticTraceValidatorV1.Validate(trace.Append(Event(trace.Count + 1, SemanticTraceEventKindV1.EffectClosedWithoutPublication))).IsValid);
        trace.Add(Event(trace.Count + 1, SemanticTraceEventKindV1.GenerationChanged) with { GenerationVectorDigest = new string('c', 64) });
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        SemanticTraceEventV1 next = Event(trace.Count + 1, SemanticTraceEventKindV1.Published) with { GenerationVectorDigest = new string('c', 64) };
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
        IReadOnlyList<SemanticTraceEventV1> reference = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible,
            SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Settled);
        List<SemanticTraceEventV1> candidate = reference.ToList();
        if (generationDrift)
            candidate.Add(Event(7, SemanticTraceEventKindV1.GenerationChanged) with
                { GenerationVectorDigest = new string('c', 64) });
        string digest = generationDrift ? new string('c', 64) : reference[0].GenerationVectorDigest;
        candidate.Add(Event(candidate.Count + 1, SemanticTraceEventKindV1.Quarantined) with
            { GenerationVectorDigest = digest });
        candidate.Add(Event(candidate.Count + 1, SemanticTraceEventKindV1.Settled) with
            { GenerationVectorDigest = digest });
        SemanticTraceComparisonResultV1 result = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, candidate,
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
        List<SemanticTraceEventV1> trace = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined).ToList();
        trace.Add(Event(4, SemanticTraceEventKindV1.GenerationChanged) with
            { GenerationVectorDigest = new string('c', 64) });
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
        SemanticTraceEventV1 late = Event(5, lateKind) with { GenerationVectorDigest = new string('c', 64) };
        Assert.False(SemanticTraceValidatorV1.Validate(trace.Append(late)).IsValid);
        trace.Add(Event(5, SemanticTraceEventKindV1.Quarantined) with
            { GenerationVectorDigest = new string('c', 64) });
        trace.Add(late with { Sequence = 6 });
        Assert.True(SemanticTraceValidatorV1.Validate(trace).IsValid);
    }

    [Fact]
    public void DriftAfterSettledClosureRequiresFreshClosureWithoutSecondSettlement()
    {
        List<SemanticTraceEventV1> trace = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.EffectClosedWithoutPublication,
            SemanticTraceEventKindV1.Settled).ToList();
        trace.Add(Event(6, SemanticTraceEventKindV1.GenerationChanged) with
            { GenerationVectorDigest = new string('c', 64) });
        SemanticTraceEventV1 release = Event(7, SemanticTraceEventKindV1.Released) with
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
        SemanticTraceEventKindV1[] kinds = published
            ? new[] { SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible,
                SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Settled }
            : new[] { SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
                SemanticTraceEventKindV1.Quarantined, SemanticTraceEventKindV1.Settled };
        List<SemanticTraceEventV1> trace = Kinds(kinds).ToList();
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
        List<SemanticTraceEventV1> trace = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
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
        IReadOnlyList<SemanticTraceEventV1> trace = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
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
        IReadOnlyList<SemanticTraceEventV1> trace = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
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
        IReadOnlyList<SemanticTraceEventV1> prefix = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible,
            SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Settled,
            SemanticTraceEventKindV1.Released);
        Assert.True(SemanticTraceValidatorV1.Validate(prefix).IsValid);
        SemanticTraceEventV1 late = Event(8, lateKind) with
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
        string source = fault switch { 0 => " ", 1 => new string('\uD800', 1), _ => new string('a', 257) };
        ProviderTraceEventV1[] trace = new[]
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
        IReadOnlyList<SemanticTraceEventV1> projected = SemanticTraceProjectionV1.Project([
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
        SemanticTraceEventV1 first = Event(1, SemanticTraceEventKindV1.Submit) with { OperationCorrelation = " " };
        SemanticTraceEventV1 second = first with { OperationCorrelation = new string('\uD800', 1) };
        SemanticTraceValidationResultV1 left = SemanticTraceValidatorV1.Validate([first]);
        SemanticTraceValidationResultV1 right = SemanticTraceValidatorV1.Validate([second]);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidEvent, left.Status);
        Assert.Equal(left.Status, right.Status);
        Assert.Equal(left.Counterexample!.CanonicalId, right.Counterexample!.CanonicalId);
        Assert.False(left.Counterexample.AuthorizesExecution);
        IReadOnlyList<SemanticTraceEventV1> reference = Kinds(SemanticTraceEventKindV1.Submit);
        SemanticTraceComparisonResultV1 a = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, [first], new string('a', 64), new string('b', 64));
        SemanticTraceComparisonResultV1 b = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, [second], new string('a', 64), new string('c', 64));
        Assert.Equal(SemanticTraceComparisonStatusV1.InvalidCandidate, a.Status);
        Assert.NotEqual(a.Difference!.CanonicalId, b.Difference!.CanonicalId);
    }

    [Fact]
    public void CounterexampleIdentityIsStableAcrossCustomNumericCulture()
    {
        CultureInfo original = global::System.Globalization.CultureInfo.CurrentCulture;
        IReadOnlyList<SemanticTraceEventV1> reference = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible);
        IReadOnlyList<SemanticTraceEventV1> candidate = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.Released);
        try
        {
            global::System.Globalization.CultureInfo.CurrentCulture = global::System.Globalization.CultureInfo.InvariantCulture;
            string validation = SemanticTraceValidatorV1.Validate(candidate).Counterexample!.CanonicalId;
            string difference = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, candidate,
                new string('a', 64), new string('b', 64)).Difference!.CanonicalId;
            CultureInfo custom = (global::System.Globalization.CultureInfo)original.Clone();
            custom.NumberFormat.NegativeSign = "negative";
            custom.NumberFormat.PositiveSign = "positive";
            custom.NumberFormat.NumberDecimalSeparator = ":";
            custom.NumberFormat.NativeDigits = ["٠", "١", "٢", "٣", "٤", "٥", "٦", "٧", "٨", "٩"];
            global::System.Globalization.CultureInfo.CurrentCulture = custom;
            Assert.Equal(validation, SemanticTraceValidatorV1.Validate(candidate).Counterexample!.CanonicalId);
            SemanticTraceComparisonResultV1 compared = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, candidate,
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
        string token = new string('a', overlong ? 256 : 255) + '\uD800';
        SemanticTraceEventV1 item = Event(1, SemanticTraceEventKindV1.Submit);
        item = source ? item with { Source = token } : item with { OperationCorrelation = token };
        ArgumentException exception = Assert.Throws<ArgumentException>(() => item.Validate());
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
        string malformed = new string((char)codeUnit, 1);
        SemanticTraceEventV1 correlation = Event(1, SemanticTraceEventKindV1.Submit) with { OperationCorrelation = malformed };
        SemanticTraceEventV1 source = Event(1, SemanticTraceEventKindV1.Submit) with { Source = malformed };
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
        string token = string.Concat(Enumerable.Repeat("\U0001f680", 64));
        SemanticTraceEventV1 item = Event(1, SemanticTraceEventKindV1.Submit) with { OperationCorrelation = token, Source = "\ufffd" };
        Assert.Equal(item, item.Validate());
        Assert.True(SemanticTraceValidatorV1.Validate([item]).IsValid);
        Assert.Throws<ArgumentException>(() => (item with { OperationCorrelation = token + "a" }).Validate());
    }

    [Fact]
    public void CompleteStagedLifecycleIsValidAndEventsGrantNoAuthority()
    {
        IReadOnlyList<SemanticTraceEventV1> trace = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible,
            SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Settled,
            SemanticTraceEventKindV1.Released);

        SemanticTraceValidationResultV1 result = SemanticTraceValidatorV1.Validate(trace);

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
        SemanticTraceValidationResultV1 result = SemanticTraceValidatorV1.Validate(Kinds(kinds));
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder, result.Status);
        Assert.NotNull(result.Counterexample);
    }

    [Fact]
    public void GenerationChangeIsPreservedButDoesNotPermitInvalidPublication()
    {
        SemanticTraceEventV1[] validPrefix = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined,
            SemanticTraceEventKindV1.Settled).Select((item, index) => index < 2
                ? item : item with { GenerationVectorDigest = new string('c', 64) }).ToArray();
        SemanticTraceEventV1[] invalid = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
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
        SemanticTraceEventV1 submitted = Event(1, SemanticTraceEventKindV1.Submit);
        SemanticTraceEventV1 possible = Event(2, SemanticTraceEventKindV1.EffectPossible);
        SemanticTraceEventV1 falseMarker = Event(3, SemanticTraceEventKindV1.GenerationChanged);
        Assert.Equal(SemanticTraceValidationStatusV1.GenerationMismatch,
            SemanticTraceValidatorV1.Validate([submitted, possible, falseMarker]).Status);

        SemanticTraceEventV1 unannounced = Event(3, SemanticTraceEventKindV1.RetireOrComplete) with
        { GenerationVectorDigest = new string('c', 64) };
        Assert.Equal(SemanticTraceValidationStatusV1.GenerationMismatch,
            SemanticTraceValidatorV1.Validate([submitted, possible, unannounced]).Status);

        SemanticTraceEventV1 marker = falseMarker with { GenerationVectorDigest = new string('c', 64) };
        SemanticTraceEventV1 continued = Event(4, SemanticTraceEventKindV1.Quarantined) with
        { GenerationVectorDigest = new string('c', 64) };
        Assert.True(SemanticTraceValidatorV1.Validate([submitted, possible, marker, continued]).IsValid);

        ProviderTraceEventV1[] providerTrace = new[]
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
        SemanticTraceEventV1 submitted = Event(1, SemanticTraceEventKindV1.Submit);
        SemanticTraceEventV1 changed = Event(2, SemanticTraceEventKindV1.GenerationChanged) with
        { GenerationVectorDigest = new string('c', 64) };
        SemanticTraceEventV1 possible = Event(3, SemanticTraceEventKindV1.EffectPossible) with
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
        SemanticTraceEventV1 submitted = Event(1, SemanticTraceEventKindV1.Submit);
        SemanticTraceEventV1 quarantined = Event(2, SemanticTraceEventKindV1.Quarantined);

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
        SemanticTraceEventV1[] prefix = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.GenerationChanged).ToArray();
        prefix[2] = prefix[2] with { GenerationVectorDigest = new string('c', 64) };
        Assert.True(SemanticTraceValidatorV1.Validate(prefix).IsValid);

        foreach (SemanticTraceEventKindV1 forbidden in new[]
                 {
                     SemanticTraceEventKindV1.RetireOrComplete,
                     SemanticTraceEventKindV1.Visible,
                     SemanticTraceEventKindV1.Published,
                     SemanticTraceEventKindV1.Settled,
                     SemanticTraceEventKindV1.Released,
                 })
        {
            SemanticTraceEventV1 continuation = Event(4, forbidden) with
            { GenerationVectorDigest = new string('c', 64) };
            Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
                SemanticTraceValidatorV1.Validate([.. prefix, continuation]).Status);
        }

        SemanticTraceEventV1 quarantined = Event(4, SemanticTraceEventKindV1.Quarantined) with
        { GenerationVectorDigest = new string('c', 64) };
        Assert.True(SemanticTraceValidatorV1.Validate([.. prefix, quarantined]).IsValid);
        SemanticTraceEventV1 published = Event(5, SemanticTraceEventKindV1.Published) with
        { GenerationVectorDigest = new string('c', 64) };
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([.. prefix, quarantined, published]).Status);
    }

    [Fact]
    public void PublishedEffectCannotLaterBeClosedAsNeverPublished()
    {
        IReadOnlyList<SemanticTraceEventV1> published = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete,
            SemanticTraceEventKindV1.Visible,
            SemanticTraceEventKindV1.Published);
        SemanticTraceEventV1 quarantined = Event(6, SemanticTraceEventKindV1.Quarantined);
        Assert.True(SemanticTraceValidatorV1.Validate([.. published, quarantined]).IsValid);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([.. published, quarantined,
                Event(7, SemanticTraceEventKindV1.EffectClosedWithoutPublication)]).Status);

        SemanticTraceEventV1 drift = Event(6, SemanticTraceEventKindV1.GenerationChanged) with
        { GenerationVectorDigest = new string('c', 64) };
        SemanticTraceEventV1 postDriftQuarantine = Event(7, SemanticTraceEventKindV1.Quarantined) with
        { GenerationVectorDigest = new string('c', 64) };
        SemanticTraceEventV1 falseClosure = Event(8, SemanticTraceEventKindV1.EffectClosedWithoutPublication) with
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
        IReadOnlyList<SemanticTraceEventV1> publishedAndSettled = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete,
            SemanticTraceEventKindV1.Visible,
            SemanticTraceEventKindV1.Published,
            SemanticTraceEventKindV1.Settled);
        SemanticTraceEventV1 changed = Event(7, SemanticTraceEventKindV1.GenerationChanged) with
        { GenerationVectorDigest = new string('c', 64) };
        SemanticTraceEventV1 release = Event(8, SemanticTraceEventKindV1.Released) with
        { GenerationVectorDigest = new string('c', 64) };

        Assert.True(SemanticTraceValidatorV1.Validate([.. publishedAndSettled, changed]).IsValid);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([.. publishedAndSettled, changed, release]).Status);

        IReadOnlyList<SemanticTraceEventV1> closedWithoutPublication = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined,
            SemanticTraceEventKindV1.EffectClosedWithoutPublication,
            SemanticTraceEventKindV1.Settled);
        SemanticTraceEventV1 changedAfterClosure = Event(6, SemanticTraceEventKindV1.GenerationChanged) with
        { GenerationVectorDigest = new string('c', 64) };
        SemanticTraceEventV1 staleRelease = Event(7, SemanticTraceEventKindV1.Released) with
        { GenerationVectorDigest = new string('c', 64) };
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([
                .. closedWithoutPublication, changedAfterClosure, staleRelease]).Status);
    }

    [Fact]
    public void AmbiguousPublicationRequiresExactEffectClosureBeforeReleaseProjection()
    {
        IReadOnlyList<SemanticTraceEventV1> pending = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.RetireOrComplete,
            SemanticTraceEventKindV1.Visible, SemanticTraceEventKindV1.Quarantined);
        Assert.True(SemanticTraceValidatorV1.Validate(pending).IsValid);
        Assert.Equal(SemanticTraceValidationStatusV1.InvalidLifecycleOrder,
            SemanticTraceValidatorV1.Validate([.. pending,
                Event(6, SemanticTraceEventKindV1.Released)]).Status);

        IReadOnlyList<SemanticTraceEventV1> closed = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.RetireOrComplete,
            SemanticTraceEventKindV1.Visible, SemanticTraceEventKindV1.Quarantined,
            SemanticTraceEventKindV1.EffectClosedWithoutPublication,
            SemanticTraceEventKindV1.Settled, SemanticTraceEventKindV1.Released);
        Assert.True(SemanticTraceValidatorV1.Validate(closed).IsValid);
        IReadOnlyList<SemanticTraceEventV1> settledBeforeClosure = Kinds(SemanticTraceEventKindV1.Submit,
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
        IReadOnlyList<SemanticTraceEventV1> pending = Kinds(SemanticTraceEventKindV1.Submit,
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

        IReadOnlyList<SemanticTraceEventV1> projection = SemanticTraceProjectionV1.Project(source);

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
        ProviderTraceEventV1[] source = new[]
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
        ProviderTraceEventV1 submit = Provider(1, ProviderTraceDispositionV1.SecurityRelevant,
            SemanticTraceEventKindV1.Submit);
        ProviderTraceEventV1 privateEvent = Provider(2, ProviderTraceDispositionV1.ProviderPrivate, default);

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
        ProviderTraceEventV1[] source = new[]
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
        ProviderTraceEventV1 privatePrefix = Provider(1, ProviderTraceDispositionV1.ProviderPrivate, default);
        ProviderTraceEventV1 sameGeneration = Provider(2, ProviderTraceDispositionV1.SecurityRelevant,
            SemanticTraceEventKindV1.Submit);
        IReadOnlyList<SemanticTraceEventV1> projected = SemanticTraceProjectionV1.Project([privatePrefix, sameGeneration]);
        Assert.Equal(SemanticTraceEventKindV1.Submit, Assert.Single(projected).Kind);

        ProviderTraceEventV1 changedPrivate = Provider(2, ProviderTraceDispositionV1.ProviderPrivate, default) with
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
        List<ProviderTraceEventV1> source = new List<ProviderTraceEventV1>();
        if (includePrefix)
            source.Add(Provider(1, ProviderTraceDispositionV1.ProviderPrivate, default));
        source.Add(Provider(2, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.Submit));
        source.Add(Provider(3, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.EffectPossible));
        source.Add(Provider(4, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.GenerationChanged)
            with { GenerationVectorDigest = new string('c', 64) });
        source.Add(Provider(5, ProviderTraceDispositionV1.ProviderPrivate, default)
            with { GenerationVectorDigest = new string('c', 64) });
        source.Add(Provider(6, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.GenerationChanged));

        IReadOnlyList<SemanticTraceEventV1> projected = SemanticTraceProjectionV1.Project(source);
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
        IReadOnlyList<SemanticTraceEventV1> projected = SemanticTraceProjectionV1.Project([
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
        SemanticTraceEventV1 trace = Event(1, SemanticTraceEventKindV1.Submit);
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
        SemanticTraceValidationResultV1 first = SemanticTraceValidatorV1.Validate(Kinds(
            SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.Published));
        SemanticTraceValidationResultV1 second = SemanticTraceValidatorV1.Validate(Kinds(
            SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.Published));
        SemanticTraceValidationResultV1 different = SemanticTraceValidatorV1.Validate(Kinds(
            SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.Released));

        Assert.Equal(first.Counterexample!.CanonicalId, second.Counterexample!.CanonicalId);
        Assert.NotEqual(first.Counterexample.CanonicalId, different.Counterexample!.CanonicalId);
        Assert.False(first.Counterexample.AuthorizesExecution);
    }

    [Fact]
    public void MixedOperationCounterexampleDistinguishesTheOffendingIdentity()
    {
        SemanticTraceEventV1 first = Event(1, SemanticTraceEventKindV1.Submit);
        SemanticTraceEventV1 second = Event(2, SemanticTraceEventKindV1.EffectPossible);
        SemanticTraceValidationResultV1 one = SemanticTraceValidatorV1.Validate([
            first, second with { OperationCorrelation = "operation-2" }]);
        SemanticTraceValidationResultV1 repeated = SemanticTraceValidatorV1.Validate([
            first, second with { OperationCorrelation = "operation-2" }]);
        SemanticTraceValidationResultV1 other = SemanticTraceValidatorV1.Validate([
            first, second with { OperationCorrelation = "operation-3" }]);

        Assert.Equal(SemanticTraceValidationStatusV1.MixedOperation, one.Status);
        Assert.Equal(one.Counterexample!.CanonicalId, repeated.Counterexample!.CanonicalId);
        Assert.NotEqual(one.Counterexample.CanonicalId, other.Counterexample!.CanonicalId);
    }

    [Fact]
    public void MixedOperationDuplicateSequenceAndEventsAfterReleaseAreRejected()
    {
        SemanticTraceEventV1[] mixed = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible).ToArray();
        mixed[1] = mixed[1] with { OperationCorrelation = "operation-2" };
        Assert.Equal(SemanticTraceValidationStatusV1.MixedOperation,
            SemanticTraceValidatorV1.Validate(mixed).Status);

        SemanticTraceEventV1[] duplicate = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible).ToArray();
        duplicate[1] = duplicate[1] with { Sequence = 1 };
        Assert.Equal(SemanticTraceValidationStatusV1.NonMonotonicSequence,
            SemanticTraceValidatorV1.Validate(duplicate).Status);

        IReadOnlyList<SemanticTraceEventV1> released = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.RetireOrComplete, SemanticTraceEventKindV1.Visible,
            SemanticTraceEventKindV1.Published, SemanticTraceEventKindV1.Settled,
            SemanticTraceEventKindV1.Released, SemanticTraceEventKindV1.GenerationChanged);
        Assert.Equal(SemanticTraceValidationStatusV1.EventAfterRelease,
            SemanticTraceValidatorV1.Validate(released).Status);
    }

    [Fact]
    public void DifferentialCounterexampleIsTupleBoundAndNonAuthoritative()
    {
        IReadOnlyList<SemanticTraceEventV1> reference = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.Quarantined);
        IReadOnlyList<SemanticTraceEventV1> candidate = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.RetireOrComplete);
        SemanticTraceComparisonResultV1 first = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, candidate,
            new string('a', 64), new string('b', 64));
        SemanticTraceComparisonResultV1 rebound = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, candidate,
            new string('a', 64), new string('c', 64));

        Assert.Equal(SemanticTraceComparisonStatusV1.EventMismatch, first.Status);
        Assert.NotEqual(first.Difference!.CanonicalId, rebound.Difference!.CanonicalId);
        Assert.False(first.Difference.AuthorizesExecution);
        Assert.False(first.Difference.AuthorizesPublication);
    }

    [Fact]
    public void InvalidCandidateDifferenceIdIncludesTheValidationCounterexample()
    {
        IReadOnlyList<SemanticTraceEventV1> reference = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible);
        IReadOnlyList<SemanticTraceEventV1> invalidPublish = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.Published);
        IReadOnlyList<SemanticTraceEventV1> invalidRelease = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.Released);
        string tuple = new string('a', 64);

        SemanticTraceComparisonResultV1 first = SemanticTraceDifferentialV1.CompareAllowedProjection(
            reference, invalidPublish, tuple, tuple);
        SemanticTraceComparisonResultV1 repeated = SemanticTraceDifferentialV1.CompareAllowedProjection(
            reference, invalidPublish, tuple, tuple);
        SemanticTraceComparisonResultV1 changed = SemanticTraceDifferentialV1.CompareAllowedProjection(
            reference, invalidRelease, tuple, tuple);

        Assert.Equal(SemanticTraceComparisonStatusV1.InvalidCandidate, first.Status);
        Assert.Equal(first.Difference!.CanonicalId, repeated.Difference!.CanonicalId);
        Assert.NotEqual(first.Difference.CanonicalId, changed.Difference!.CanonicalId);

        SemanticTraceEventV1[] mixedTwo = reference.ToArray();
        mixedTwo[1] = mixedTwo[1] with { OperationCorrelation = "operation-2" };
        SemanticTraceEventV1[] mixedThree = reference.ToArray();
        mixedThree[1] = mixedThree[1] with { OperationCorrelation = "operation-3" };
        SemanticTraceComparisonResultV1 differentIdentity = SemanticTraceDifferentialV1.CompareAllowedProjection(
            reference, mixedTwo, tuple, tuple);
        SemanticTraceComparisonResultV1 anotherIdentity = SemanticTraceDifferentialV1.CompareAllowedProjection(
            reference, mixedThree, tuple, tuple);
        Assert.Equal(SemanticTraceComparisonStatusV1.InvalidCandidate, differentIdentity.Status);
        Assert.NotEqual(differentIdentity.Difference!.CanonicalId,
            anotherIdentity.Difference!.CanonicalId);
    }

    [Fact]
    public void DifferentialComparisonAcceptsTheSameAllowedProjectionAcrossPrivateDetails()
    {
        IReadOnlyList<SemanticTraceEventV1> reference = Kinds(SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible, SemanticTraceEventKindV1.Quarantined);
        SemanticTraceEventV1[] candidate = reference.Select(item => item with
        {
            OperationCorrelation = "other-operation",
            Source = "heterogeneous-provider",
            Sequence = item.Sequence + 10,
            EvidenceDigest = new string('c', 64),
        }).ToArray();

        SemanticTraceComparisonResultV1 result = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, candidate,
            new string('a', 64), new string('b', 64));

        Assert.True(result.IsEquivalent);
        Assert.Null(result.Difference);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerationTransitionCannotReuseAnObservedDigest(bool closeAndRelease)
    {
        SemanticTraceEventV1[] events = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined,
            SemanticTraceEventKindV1.GenerationChanged, SemanticTraceEventKindV1.Quarantined,
            SemanticTraceEventKindV1.EffectClosedWithoutPublication, SemanticTraceEventKindV1.Settled,
            SemanticTraceEventKindV1.Released).Select((item, index) => item with
            { GenerationVectorDigest = new string(index is 2 or 3 ? 'b' : 'a', 64) }).ToArray();
        if (!closeAndRelease) events = events[..5];
        Assert.Equal(SemanticTraceValidationStatusV1.GenerationMismatch, SemanticTraceValidatorV1.Validate(events).Status);
        SemanticTraceComparisonResultV1 comparison = SemanticTraceDifferentialV1.CompareAllowedProjection(Kinds(
            SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible), events,
            new string('a', 64), new string('b', 64));
        Assert.Equal(SemanticTraceComparisonStatusV1.InvalidCandidate, comparison.Status);
    }

    [Fact]
    public void DistinctGenerationTransitionsRetainQuarantineAndExactClosureProjection()
    {
        SemanticTraceEventV1[] events = Kinds(SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
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
