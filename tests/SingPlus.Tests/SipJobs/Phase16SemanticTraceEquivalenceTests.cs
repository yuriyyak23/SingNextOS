namespace SingPlus.Tests.SipJobs;

public sealed class Phase16SemanticTraceEquivalenceTests
{
    [Fact]
    public void OrdinaryAndFusedAuthorityVisibleTracesAreEquivalentForExactTuple()
    {
        var ordinary = ExactExternalTrace();
        var fused = ExactExternalTrace(); // optimizer-internal scheduling events are intentionally absent

        var result = SipJobSemanticTrace.CompareAuthorityVisible(ordinary.Completed, fused.Completed);

        Assert.True(result.IsEquivalent);
        Assert.Equal(7, result.ComparedEvents);
    }

    [Theory]
    [InlineData("operation:43", "binding:sha256:abc")]
    [InlineData("operation:42", "binding:sha256:def")]
    public void OperationOrBindingIdentityDriftBreaksEquivalence(string operation, string binding)
    {
        var ordinary = ExactExternalTrace();
        var fused = ExactExternalTrace(operation, binding);

        var result = SipJobSemanticTrace.CompareAuthorityVisible(ordinary.Completed, fused.Completed);

        Assert.Equal(SipJobSemanticTraceError.TraceMismatch, result.Error);
        Assert.False(result.IsEquivalent);
    }

    [Fact]
    public void GenerationOutcomeOrderAndMissingEventCannotBeHiddenAsStuttering()
    {
        var ordinary = ExactExternalTrace();
        var differentGeneration = ExactExternalTrace(generation: 10);
        Assert.False(SipJobSemanticTrace.CompareAuthorityVisible(
            ordinary.Completed, differentGeneration.Completed).IsEquivalent);

        var shortened = ExactExternalTrace().Completed.Take(6).ToArray();
        Assert.False(SipJobSemanticTrace.CompareAuthorityVisible(
            ordinary.Completed, shortened).IsEquivalent);
    }

    [Fact]
    public void UnknownAndNonCanonicalEventsFailClosed()
    {
        var valid = ExactExternalTrace().Completed[0];
        Assert.Equal(SipJobSemanticTraceError.UnknownVersion,
            SipJobSemanticTrace.Validate(valid with { Version = 2 }));
        Assert.Equal(SipJobSemanticTraceError.UnknownEvent,
            SipJobSemanticTrace.Validate(valid with { Kind = (SipJobSemanticEventKind)int.MaxValue }));
        Assert.Equal(SipJobSemanticTraceError.UnknownOutcome,
            SipJobSemanticTrace.Validate(valid with { Outcome = (SipJobSemanticOutcome)int.MaxValue }));
        Assert.Equal(SipJobSemanticTraceError.NonCanonicalIdentity,
            SipJobSemanticTrace.Validate(valid with { OpaqueCorrelation = " operation:42 " }));
        Assert.Equal(SipJobSemanticTraceError.MissingExternalIdentity,
            SipJobSemanticTrace.Validate(valid with { ExternalOperationIdentity = null }));
        Assert.Equal(SipJobSemanticTraceError.UnexpectedExternalIdentity,
            SipJobSemanticTrace.Validate(valid with
            {
                Kind = SipJobSemanticEventKind.SessionPinned,
                ExternalOperationIdentity = "operation:42",
                SemanticBindingIdentity = "binding:sha256:abc"
            }));
    }

    [Fact]
    public void TraceRemainsEvidenceAndCannotAuthorizeOrSubmit()
    {
        var publicProperties = typeof(SipJobSemanticTraceEvent).GetProperties();
        Assert.DoesNotContain(publicProperties, property =>
            property.Name.Contains("Authoriz", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Submit", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Allowed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(SipJobSemanticTrace).GetMethods(), method =>
            method.Name.Contains("Authoriz", StringComparison.OrdinalIgnoreCase) ||
            method.Name.Contains("Submit", StringComparison.OrdinalIgnoreCase));
    }

    private static SipJobSemanticTrace ExactExternalTrace(
        string operation = "operation:42",
        string binding = "binding:sha256:abc",
        ulong generation = 9)
    {
        var trace = new SipJobSemanticTrace();
        foreach (var kind in new[]
        {
            SipJobSemanticEventKind.ExternalOperationSubmitted,
            SipJobSemanticEventKind.ProviderCompletionObserved,
            SipJobSemanticEventKind.VisibilityConfirmed,
            SipJobSemanticEventKind.OutputValidated,
            SipJobSemanticEventKind.ResponsePublished,
            SipJobSemanticEventKind.OwnershipSettled,
            SipJobSemanticEventKind.ReleaseCompleted,
        })
        {
            trace.RecordCompleted(kind, "correlation:matrix-1", generation,
                SipJobSemanticOutcome.Succeeded, operation, binding);
        }
        return trace;
    }
}
