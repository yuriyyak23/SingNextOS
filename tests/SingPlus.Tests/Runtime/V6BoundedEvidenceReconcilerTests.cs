using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class V6BoundedEvidenceReconcilerTests
{
    [Fact]
    public void TransientFailuresStopAtFirstExactGenerationBoundEvidence()
    {
        uint calls = 0;
        var result = V6BoundedEvidenceReconciler.Execute(Policy(4), Scope, 7, attempt =>
        {
            calls++;
            return attempt < 3
                ? KernelResult<ProviderHealthEvidenceV1>.Fail(KernelError.PlatformUnavailable, "not ready")
                : KernelResult<ProviderHealthEvidenceV1>.Ok(Evidence(7));
        });

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(3U, calls);
        Assert.Equal(3U, result.Value!.Attempts);
        Assert.Equal(V6EvidenceReconciliationStatus.ExactEvidenceObserved, result.Value.Status);
        Assert.Equal(Evidence(7), result.Value.ExactEvidence);
        AssertNoAuthority(result.Value);
    }

    [Fact]
    public void AttemptExhaustionRequiresQuarantineAndNeverProvesClosure()
    {
        uint calls = 0;
        var result = V6BoundedEvidenceReconciler.Execute(Policy(3), Scope, 7, _ =>
        {
            calls++;
            return KernelResult<ProviderHealthEvidenceV1>.Fail(KernelError.PlatformUnavailable, "omitted");
        }).Value!;

        Assert.Equal(3U, calls);
        Assert.Equal(3U, result.Attempts);
        Assert.Equal(V6EvidenceReconciliationStatus.QuarantinePendingExactEvidence, result.Status);
        Assert.Null(result.ExactEvidence);
        Assert.Equal(KernelError.PlatformUnavailable, result.LastQueryError);
        AssertNoAuthority(result);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void WrongScopeOrReplayedSequenceIsRejectedWithoutRetry(bool wrongScope, bool staleSequence)
    {
        uint calls = 0;
        ProviderHealthEvidenceV1 supplied = Evidence(staleSequence ? 6UL : 7UL);
        if (wrongScope) supplied = supplied with { Scope = Scope with { ProviderGeneration = 12 } };

        var result = V6BoundedEvidenceReconciler.Execute(Policy(4), Scope, 7, _ =>
        {
            calls++;
            return KernelResult<ProviderHealthEvidenceV1>.Ok(supplied);
        }).Value!;

        Assert.Equal(1U, calls);
        Assert.Equal(V6EvidenceReconciliationStatus.EvidenceRejected, result.Status);
        Assert.Equal(KernelError.StaleGeneration, result.LastQueryError);
        Assert.Null(result.ExactEvidence);
        AssertNoAuthority(result);
    }

    [Theory]
    [InlineData(ProviderFaultClassV1.CrashStop, 2)]
    [InlineData(ProviderFaultClassV1.CorruptEvidence, 3)]
    public void NonRetryDispositionsNeverInvokeProvider(
        ProviderFaultClassV1 fault, int expected)
    {
        uint calls = 0;
        var policy = new ReconciliationPolicyV1(1,
            ProviderTrustClassV1.LocalTrustedFallible, fault, 4);

        var result = V6BoundedEvidenceReconciler.Execute(policy, Scope, 7, _ =>
        {
            calls++;
            return KernelResult<ProviderHealthEvidenceV1>.Ok(Evidence(7));
        }).Value!;

        Assert.Equal(0U, calls);
        Assert.Equal(0U, result.Attempts);
        Assert.Equal((V6EvidenceReconciliationStatus)expected, result.Status);
        AssertNoAuthority(result);
    }

    [Fact]
    public void ProviderExceptionsAreBoundedAndCollapseToQuarantine()
    {
        uint calls = 0;
        var result = V6BoundedEvidenceReconciler.Execute(Policy(2), Scope, 7, _ =>
        {
            calls++;
            throw new InvalidOperationException("provider fault");
        }).Value!;

        Assert.Equal(2U, calls);
        Assert.Equal(2U, result.Attempts);
        Assert.Equal(V6EvidenceReconciliationStatus.QuarantinePendingExactEvidence, result.Status);
        AssertNoAuthority(result);
    }

    [Fact]
    public void InvalidExpectedTupleFailsBeforeProviderQuery()
    {
        uint calls = 0;
        var result = V6BoundedEvidenceReconciler.Execute(Policy(2),
            Scope with { FailureDomainGeneration = 0 }, 7, _ =>
            {
                calls++;
                return KernelResult<ProviderHealthEvidenceV1>.Ok(Evidence(7));
            });

        Assert.False(result.IsSuccess);
        Assert.Equal(KernelError.InvalidMessage, result.Error);
        Assert.Equal(0U, calls);
    }

    private static readonly FailureDomainScopeV1 Scope = new("provider:p10", "bank:0", 11, 13);

    private static ReconciliationPolicyV1 Policy(uint attempts) => new(1,
        ProviderTrustClassV1.LocalTrustedFallible, ProviderFaultClassV1.Omission, attempts);

    private static ProviderHealthEvidenceV1 Evidence(ulong sequence) => new(1, Scope, sequence,
        ProviderHealthStateV1.Degraded, ProviderFaultClassV1.Omission, new(8, 8));

    private static void AssertNoAuthority(V6EvidenceReconciliationReceipt receipt)
    {
        Assert.False(receipt.ProvesClosure);
        Assert.False(receipt.AuthorizesRelease);
        Assert.False(receipt.AuthorizesReclaim);
        Assert.False(receipt.AuthorizesExecution);
        Assert.False(receipt.PolicyDecision.ProvesClosure);
        Assert.False(receipt.PolicyDecision.AuthorizesRelease);
    }
}
