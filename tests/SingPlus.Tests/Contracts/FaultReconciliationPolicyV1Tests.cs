using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class FaultReconciliationPolicyV1Tests
{
    [Theory]
    [InlineData(ProviderFaultClassV1.Omission)]
    [InlineData(ProviderFaultClassV1.Reordering)]
    [InlineData(ProviderFaultClassV1.Recovery)]
    public void BoundedEvidenceQueryRequiresExplicitLocalFallibleAssumption(ProviderFaultClassV1 fault)
    {
        var decision = ReconciliationPolicyEvaluatorV1.Evaluate(new(1,
            ProviderTrustClassV1.LocalTrustedFallible, fault, 3));

        Assert.Equal(ReconciliationDispositionV1.RetryEvidenceQuery, decision.Disposition);
        Assert.Equal(3U, decision.MaximumEvidenceQueryAttempts);
        Assert.True(decision.RequiresExactGenerationEvidence);
        Assert.False(decision.ProvesClosure);
        Assert.False(decision.AuthorizesRelease);
    }

    [Theory]
    [InlineData(ProviderFaultClassV1.CrashStop)]
    [InlineData(ProviderFaultClassV1.Duplication)]
    [InlineData(ProviderFaultClassV1.Partition)]
    public void RetryCountCannotTurnUncertaintyIntoClosure(ProviderFaultClassV1 fault)
    {
        var decision = ReconciliationPolicyEvaluatorV1.Evaluate(new(1,
            ProviderTrustClassV1.LocalTrustedFallible, fault, 8));

        Assert.Equal(ReconciliationDispositionV1.QuarantinePendingExactEvidence, decision.Disposition);
        Assert.Equal(0U, decision.MaximumEvidenceQueryAttempts);
        Assert.False(decision.ProvesClosure);
    }

    [Theory]
    [InlineData(ProviderFaultClassV1.CorruptEvidence)]
    [InlineData(ProviderFaultClassV1.MaliciousProvider)]
    public void UntrustedEvidenceIsRejectedWithoutSecurityClaim(ProviderFaultClassV1 fault)
    {
        var decision = ReconciliationPolicyEvaluatorV1.Evaluate(new(1,
            ProviderTrustClassV1.LocalTrustedFallible, fault, 4));

        Assert.Equal(ReconciliationDispositionV1.RejectEvidenceAndQuarantine, decision.Disposition);
        Assert.False(decision.AuthorizesExecution);
        Assert.False(decision.AuthorizesRelease);
    }

    [Theory]
    [InlineData(ProviderTrustClassV1.RemoteAuthenticated)]
    [InlineData(ProviderTrustClassV1.Attested)]
    [InlineData(ProviderTrustClassV1.ByzantineTolerant)]
    public void UnimplementedTrustContoursRemainUnsupported(ProviderTrustClassV1 trust)
    {
        var decision = ReconciliationPolicyEvaluatorV1.Evaluate(new(1, trust,
            ProviderFaultClassV1.Omission, 3));
        Assert.Equal(ReconciliationDispositionV1.UnsupportedTrustContour, decision.Disposition);
    }

    [Fact]
    public void UnknownVersionClassAndUnboundedAttemptCountFailClosed()
    {
        var decisions = new[]
        {
            ReconciliationPolicyEvaluatorV1.Evaluate(new(2, ProviderTrustClassV1.LocalTrustedFallible,
                ProviderFaultClassV1.Omission, 1)),
            ReconciliationPolicyEvaluatorV1.Evaluate(new(1, (ProviderTrustClassV1)0,
                ProviderFaultClassV1.Omission, 1)),
            ReconciliationPolicyEvaluatorV1.Evaluate(new(1, ProviderTrustClassV1.LocalTrustedFallible,
                (ProviderFaultClassV1)255, 1)),
            ReconciliationPolicyEvaluatorV1.Evaluate(new(1, ProviderTrustClassV1.LocalTrustedFallible,
                ProviderFaultClassV1.Omission, 65)),
        };

        Assert.All(decisions, decision =>
            Assert.Equal(ReconciliationDispositionV1.UnsupportedTrustContour, decision.Disposition));
    }
}
