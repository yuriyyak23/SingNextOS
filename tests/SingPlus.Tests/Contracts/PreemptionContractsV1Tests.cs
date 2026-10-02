using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class PreemptionContractsV1Tests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ResumeBindingRejectsMalformedUnicodeCorrelation(int surrogate)
    {
        var correlation = new string(surrogate == 0 ? '\uD800' : '\uDC00', 1);
        Assert.Throws<ArgumentException>(() =>
            new ResumeBindingV1(1, correlation, Digest, Digest, 1, 2, 3, 4).Validate());
    }

    [Fact]
    public void ResumeBindingPreservesValidSupplementaryUnicodeAtExistingCharacterLimit()
    {
        var correlation = string.Concat(Enumerable.Repeat("\U0001F680", 128));
        Assert.Equal(256, correlation.Length);
        Assert.Equal(correlation, new ResumeBindingV1(1, correlation, Digest, Digest, 1, 2, 3, 4).Validate().OperationCorrelation);
    }

    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void RestartOnlyGuaranteeIsRequestEvidenceAndNeverAuthority()
    {
        var guarantee = new PreemptionGuaranteeV1(1, PreemptionClassV1.RestartOnly,
            PreemptionEffectSemanticsV1.RequestOnly, 0, false).Validate();

        Assert.False(guarantee.AuthorizesPreemption);
        Assert.False(guarantee.AuthorizesRestart);
        Assert.False(guarantee.AuthorizesResume);
    }

    [Fact]
    public void RestartOnlyAndSafePointClaimsFailClosedWhenTheirShapeIsFalse()
    {
        Assert.Throws<ArgumentException>(() => new PreemptionGuaranteeV1(1,
            PreemptionClassV1.RestartOnly, PreemptionEffectSemanticsV1.RequestOnly, 1, false).Validate());
        Assert.Throws<ArgumentException>(() => new PreemptionGuaranteeV1(1,
            PreemptionClassV1.SafePoint, PreemptionEffectSemanticsV1.ContainedAtSafePoint, 0, false).Validate());
        Assert.Throws<ArgumentException>(() => new PreemptionGuaranteeV1(1,
            PreemptionClassV1.StateCapture, PreemptionEffectSemanticsV1.ContainedAtSafePoint, 1, false).Validate());
    }

    [Fact]
    public void ResumeBindingCorrelatesExactGenerationsWithoutPreservingAuthority()
    {
        var binding = new ResumeBindingV1(1, "operation-7", Digest, Digest, 1, 2, 3, 4).Validate();

        Assert.False(binding.AuthorizesExecution);
        Assert.False(binding.AuthorizesRegionAccess);
        Assert.False(binding.PreservesCapability);
        Assert.Throws<ArgumentException>(() => (binding with { RuntimeGeneration = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (binding with { CapturedStateDigest = "not-a-digest" }).Validate());
        Assert.Throws<ArgumentException>(() => (binding with
        { CapturedStateDigest = Digest.ToUpperInvariant() }).Validate());
        Assert.Throws<ArgumentException>(() => (binding with
        { SemanticBindingDigest = Digest.ToUpperInvariant() }).Validate());
    }

    [Fact]
    public void RestartLifecycleRequiresRequestedDrainContainmentAndRestartInExactOrder()
    {
        PreemptionLifecycleEventV1[] valid =
        [new(1, 1, PreemptionLifecycleEventKindV1.Requested, Digest),
         new(1, 2, PreemptionLifecycleEventKindV1.Draining, Digest),
         new(1, 3, PreemptionLifecycleEventKindV1.Contained, Digest),
         new(1, 4, PreemptionLifecycleEventKindV1.Restarted, Digest)];

        Assert.True(PreemptionLifecycleValidatorV1.IsValidRestartOnly(valid));
        Assert.False(PreemptionLifecycleValidatorV1.IsValidRestartOnly(valid.Where(item =>
            item.Kind != PreemptionLifecycleEventKindV1.Contained)));
        Assert.False(PreemptionLifecycleValidatorV1.IsValidRestartOnly(
            [valid[0], valid[2], valid[1], valid[3]]));
        Assert.All(valid, item => Assert.False(item.AuthorizesExecution));
    }

    [Fact]
    public void SafePointLifecycleRequiresReachedBeforeContainedAndOneEvidenceIdentity()
    {
        PreemptionLifecycleEventV1[] valid =
        [new(1, 1, PreemptionLifecycleEventKindV1.Requested, Digest),
         new(1, 2, PreemptionLifecycleEventKindV1.Draining, Digest),
         new(1, 3, PreemptionLifecycleEventKindV1.SafePointReached, Digest),
         new(1, 4, PreemptionLifecycleEventKindV1.Contained, Digest)];

        Assert.True(PreemptionLifecycleValidatorV1.IsValidSafePoint(valid));
        Assert.False(PreemptionLifecycleValidatorV1.IsValidSafePoint(
            [valid[0], valid[1], valid[3], valid[2]]));
        Assert.False(PreemptionLifecycleValidatorV1.IsValidSafePoint(
            [valid[0], valid[1], valid[2], valid[3] with { EvidenceDigest = new('f', 64) }]));
    }
}
