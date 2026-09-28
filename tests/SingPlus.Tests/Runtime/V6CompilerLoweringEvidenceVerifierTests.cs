using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class V6CompilerLoweringEvidenceVerifierTests
{
    private static readonly byte[] Output = "exact-output"u8.ToArray();
    private static V6LoweringEvidenceExpectation Expected => new(
        "compiler-contract-v6", D('a'), D('d'), 7, true,
        Envelope().Evidence.StaticFactSetDigest);

    [Fact]
    public void ExactEvidenceStillRequiresLiveAuthorityAndRuntimeLegality()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        var authorityCalls = 0;
        var legalityCalls = 0;
        var result = verifier.VerifyForAdmission(Envelope(), Output, Expected,
            () => { authorityCalls++; return KernelResult.Ok(); },
            () => { legalityCalls++; return KernelResult.Ok(); });

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.EvidenceAccepted);
        Assert.False(result.Value.AuthorizesExecution);
        Assert.Equal(1, authorityCalls);
        Assert.Equal(1, legalityCalls);
    }

    [Fact]
    public void BinaryMutationRejectsPreviouslyValidEvidence()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        var mutated = Output.ToArray();
        mutated[0] ^= 1;

        Assert.Equal(KernelError.PlatformDenied,
            verifier.VerifyForAdmission(Envelope(), mutated, Expected,
                KernelResult.Ok, KernelResult.Ok).Error);
    }

    [Fact]
    public void SchemaToolchainCompilerAndProducerMutationFailClosed()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        var badSchema = Envelope() with { SchemaVersion = 2 };
        Assert.Equal(KernelError.InvalidMessage,
            verifier.VerifyForAdmission(badSchema, Output, Expected, KernelResult.Ok, KernelResult.Ok).Error);
        foreach (var expectation in new[]
                 {
                     Expected with { ToolchainDigest = D('9') },
                     Expected with { CompilerContractVersion = "compiler-contract-v7" },
                     Expected with { ProducerDigest = D('8') },
                 })
            Assert.Equal(KernelError.PlatformDenied,
                verifier.VerifyForAdmission(Envelope(), Output, expectation,
                    KernelResult.Ok, KernelResult.Ok).Error);
    }

    [Fact]
    public void MissingMandatoryFootprintIsRejectedButOptionalAbsenceFallsBack()
    {
        var empty = CompilerLoweringEvidenceV1.Create("compiler-contract-v6", D('a'), D('b'),
            Sha(Output), D('d'), []);
        var verifier = new V6CompilerLoweringEvidenceVerifier();

        Assert.Equal(KernelError.PlatformDenied,
            verifier.VerifyForAdmission(CompilerLoweringEvidenceEnvelopeV1.Create(empty), Output,
                Expected, KernelResult.Ok, KernelResult.Ok).Error);
        var fallback = verifier.VerifyForAdmission(null, Output,
            Expected with { FootprintRequired = false }, KernelResult.Ok, KernelResult.Ok);
        Assert.True(fallback.IsSuccess);
        Assert.True(fallback.Value!.UsedFallback);
        Assert.False(fallback.Value.EvidenceAccepted);
    }

    [Fact]
    public void ForgedAliasClaimIsRejectedAgainstIndependentFactSetExpectation()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        var valid = Envelope().Evidence;
        var forged = CompilerLoweringEvidenceV1.Create(valid.CompilerContractVersion,
            valid.ToolchainDigest, valid.InputIrDigest, valid.OutputBinaryOrBundleDigest,
            valid.ProducerDigest, valid.Footprints, [], valid.OrderingFacts, valid.NumericFacts);
        var liveCalls = 0;
        var result = verifier.VerifyForAdmission(
            CompilerLoweringEvidenceEnvelopeV1.Create(forged), Output, Expected,
            () => { liveCalls++; return KernelResult.Ok(); },
            () => { liveCalls++; return KernelResult.Ok(); });

        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, liveCalls);
    }

    [Fact]
    public void SubstitutedDescriptorFootprintRangeIsRejectedBeforeLiveChecks()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        var valid = Envelope().Evidence;
        var substituted = CompilerLoweringEvidenceV1.Create(valid.CompilerContractVersion,
            valid.ToolchainDigest, valid.InputIrDigest, valid.OutputBinaryOrBundleDigest,
            valid.ProducerDigest,
            [new("input", 8, 64, LoweringFootprintAccessV1.Read),
             new("output", 0, 32, LoweringFootprintAccessV1.Write)],
            valid.AliasFacts, valid.OrderingFacts, valid.NumericFacts,
            valid.SafePointMap, valid.StaticResourceEstimates);
        var liveCalls = 0;

        var result = verifier.VerifyForAdmission(
            CompilerLoweringEvidenceEnvelopeV1.Create(substituted), Output, Expected,
            () => { liveCalls++; return KernelResult.Ok(); },
            () => { liveCalls++; return KernelResult.Ok(); });

        Assert.Equal(KernelError.PlatformDenied, result.Error);
        Assert.Equal(0, liveCalls);
    }

    [Fact]
    public void RequiredFootprintWithoutIndependentFactSetDigestFailsClosed()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();

        Assert.Equal(KernelError.PlatformDenied,
            verifier.VerifyForAdmission(Envelope(), Output,
                Expected with { RequiredStaticFactSetDigest = null },
                KernelResult.Ok, KernelResult.Ok).Error);
    }

    [Fact]
    public void ValidEvidenceCannotOverrideRuntimeLegalityDenial()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        var result = verifier.VerifyForAdmission(Envelope(), Output, Expected,
            KernelResult.Ok, () => KernelResult.Fail(KernelError.PlatformDenied, "runtime denied"));

        Assert.Equal(KernelError.PlatformDenied, result.Error);
    }

    [Fact]
    public void CacheSavesStaticVerificationButNeverCachesLiveDecisions()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        var liveCalls = 0;
        KernelResult Live() { liveCalls++; return KernelResult.Ok(); }

        var first = verifier.VerifyForAdmission(Envelope(), Output, Expected, Live, KernelResult.Ok);
        var second = verifier.VerifyForAdmission(Envelope(), Output, Expected, Live, KernelResult.Ok);

        Assert.False(first.Value!.CacheHit);
        Assert.True(second.Value!.CacheHit);
        Assert.Equal(1, verifier.StaticVerificationCount);
        Assert.Equal(2, liveCalls);
    }

    [Fact]
    public void AbsenceOfOptionalProofPreservesLiveAdmissionDecisionsAcrossStateChanges()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        var optional = Expected with { FootprintRequired = false };
        var authority = KernelResult.Ok();
        var legality = KernelResult.Ok();
        var authorityCalls = 0;
        var legalityCalls = 0;
        KernelResult CheckAuthority() { authorityCalls++; return authority; }
        KernelResult CheckLegality() { legalityCalls++; return legality; }

        KernelResult<V6LoweringAdmissionReceipt> WithProof() =>
            verifier.VerifyForAdmission(Envelope(), Output, optional, CheckAuthority, CheckLegality);
        KernelResult<V6LoweringAdmissionReceipt> WithoutProof() =>
            verifier.VerifyForAdmission(null, Output, optional, CheckAuthority, CheckLegality);

        var accepted = WithProof();
        var reference = WithoutProof();
        Assert.True(accepted.IsSuccess);
        Assert.True(reference.IsSuccess);
        Assert.True(accepted.Value!.EvidenceAccepted);
        Assert.True(reference.Value!.UsedFallback);
        Assert.False(reference.Value.AuthorizesExecution);

        authority = KernelResult.Fail(KernelError.StaleGeneration, "revoked");
        Assert.Equal(KernelError.StaleGeneration, WithProof().Error);
        Assert.Equal(KernelError.StaleGeneration, WithoutProof().Error);
        Assert.Equal(4, authorityCalls);
        Assert.Equal(2, legalityCalls);

        authority = KernelResult.Ok();
        legality = KernelResult.Fail(KernelError.PlatformDenied, "runtime legality denied");
        Assert.Equal(KernelError.PlatformDenied, WithProof().Error);
        Assert.Equal(KernelError.PlatformDenied, WithoutProof().Error);
        Assert.Equal(6, authorityCalls);
        Assert.Equal(4, legalityCalls);
        Assert.Equal(1, verifier.StaticVerificationCount);
    }

    [Fact]
    public void VerifierPolicyGenerationInvalidatesTheStaticCache()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        Assert.True(verifier.VerifyForAdmission(Envelope(), Output, Expected,
            KernelResult.Ok, KernelResult.Ok).IsSuccess);
        Assert.True(verifier.VerifyForAdmission(Envelope(), Output,
            Expected with { VerifierPolicyGeneration = 8 }, KernelResult.Ok, KernelResult.Ok).IsSuccess);

        Assert.Equal(2, verifier.StaticVerificationCount);
    }

    [Fact]
    public void StaticDecisionCacheIsBoundedAndEvictionRechecksWithoutSkippingLiveGates()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        var envelope = Envelope();
        var liveCalls = 0;
        KernelResult Live() { liveCalls++; return KernelResult.Ok(); }

        var first = verifier.VerifyForAdmission(envelope, Output, Expected, Live, Live);
        Assert.True(first.IsSuccess);
        for (ulong generation = 8;
             generation < (ulong)V6CompilerLoweringEvidenceVerifier.MaximumCachedStaticDecisions + 8;
             generation++)
        {
            var next = verifier.VerifyForAdmission(envelope, Output,
                Expected with { VerifierPolicyGeneration = generation }, Live, Live);
            Assert.True(next.IsSuccess, next.Message);
            Assert.False(next.Value!.CacheHit);
        }

        Assert.Equal(V6CompilerLoweringEvidenceVerifier.MaximumCachedStaticDecisions,
            verifier.CachedStaticDecisionCount);
        var afterEviction = verifier.VerifyForAdmission(envelope, Output, Expected, Live, Live);
        Assert.True(afterEviction.IsSuccess);
        Assert.False(afterEviction.Value!.CacheHit);
        Assert.Equal(V6CompilerLoweringEvidenceVerifier.MaximumCachedStaticDecisions,
            verifier.CachedStaticDecisionCount);
        Assert.Equal(V6CompilerLoweringEvidenceVerifier.MaximumCachedStaticDecisions + 2,
            verifier.StaticVerificationCount);
        Assert.Equal(2 * verifier.StaticVerificationCount, liveCalls);
    }

    [Fact]
    public void RequiredFactSetDigestParticipatesInCacheIdentity()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        Assert.True(verifier.VerifyForAdmission(Envelope(), Output, Expected,
            KernelResult.Ok, KernelResult.Ok).IsSuccess);

        var substituted = verifier.VerifyForAdmission(Envelope(), Output,
            Expected with { RequiredStaticFactSetDigest = D('f') },
            KernelResult.Ok, KernelResult.Ok);

        Assert.Equal(KernelError.PlatformDenied, substituted.Error);
        Assert.Equal(2, verifier.StaticVerificationCount);
    }

    [Fact]
    public void DelimiterInContractCannotCollideWithDifferentToolchainExpectation()
    {
        var valid = Envelope().Evidence;
        var colonContract = CompilerLoweringEvidenceV1.Create("compiler:contract-v6",
            valid.ToolchainDigest, valid.InputIrDigest, valid.OutputBinaryOrBundleDigest,
            valid.ProducerDigest, valid.Footprints, valid.AliasFacts, valid.OrderingFacts,
            valid.NumericFacts, valid.SafePointMap, valid.StaticResourceEstimates);
        var envelope = CompilerLoweringEvidenceEnvelopeV1.Create(colonContract);
        var matching = Expected with
        {
            CompilerContractVersion = "compiler:contract-v6",
            RequiredStaticFactSetDigest = colonContract.StaticFactSetDigest
        };
        var collidingText = matching with
        {
            CompilerContractVersion = "compiler",
            ToolchainDigest = $"contract-v6:{valid.ToolchainDigest}"
        };
        var verifier = new V6CompilerLoweringEvidenceVerifier();

        Assert.True(verifier.VerifyForAdmission(envelope, Output, matching,
            KernelResult.Ok, KernelResult.Ok).IsSuccess);
        Assert.Equal(KernelError.PlatformDenied,
            verifier.VerifyForAdmission(envelope, Output, collidingText,
                KernelResult.Ok, KernelResult.Ok).Error);
        Assert.Equal(2, verifier.StaticVerificationCount);
    }

    [Fact]
    public void InvalidOptionalEvidenceFallsBackWithoutGrantingPermission()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        var result = verifier.VerifyForAdmission(Envelope(), Output,
            Expected with { ToolchainDigest = D('9'), FootprintRequired = false },
            KernelResult.Ok, KernelResult.Ok);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.UsedFallback);
        Assert.False(result.Value.EvidenceAccepted);
        Assert.False(result.Value.AuthorizesExecution);
    }

    [Fact]
    public void CanonicalCompiledMetadataReachesAdmissionAndStillRunsLiveChecks()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        var metadata = CompilerLoweringEvidenceMetadataCodecV1.Emit(Envelope());
        var liveCalls = 0;
        var result = verifier.VerifyMetadataForAdmission(metadata, Output, Expected,
            () => { liveCalls++; return KernelResult.Ok(); }, KernelResult.Ok);

        Assert.True(result.IsSuccess, result.Message);
        Assert.True(result.Value!.EvidenceAccepted);
        Assert.Equal(1, liveCalls);
    }

    [Fact]
    public void MutatedMetadataIsInvalidAndAbsentOptionalMetadataUsesFallback()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        var metadata = CompilerLoweringEvidenceMetadataCodecV1.Emit(Envelope());
        metadata[^1] ^= 1;
        Assert.Equal(KernelError.InvalidMessage,
            verifier.VerifyMetadataForAdmission(metadata, Output, Expected,
                KernelResult.Ok, KernelResult.Ok).Error);

        var fallback = verifier.VerifyMetadataForAdmission(null, Output,
            Expected with { FootprintRequired = false }, KernelResult.Ok, KernelResult.Ok);
        Assert.True(fallback.IsSuccess);
        Assert.True(fallback.Value!.UsedFallback);
    }

    [Fact]
    public void RequiredSafePointMapNeedsExactPolicyBindingAndLiveValidationEveryUse()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        var envelope = Envelope();
        var expectation = Expected with
        {
            SafePointMapRequired = true,
            RequiredSafePointMapDigest = envelope.Evidence.SafePointMapDigest,
        };
        var safePointChecks = 0;
        var provider = new V6ManagedSafePointProvider(
            "managed-safe-point-model-v1", 1_000, 7, 11);
        KernelResult Validate(IReadOnlyList<LoweringSafePointFactV1> map)
        {
            safePointChecks++;
            return provider.ValidateLoweringSafePointMap(
                map, "managed-sp-0", 0, D('e'), 7, 11);
        }

        var first = verifier.VerifyForAdmission(envelope, Output, expectation,
            KernelResult.Ok, KernelResult.Ok, Validate);
        var second = verifier.VerifyForAdmission(envelope, Output, expectation,
            KernelResult.Ok, KernelResult.Ok, Validate);

        Assert.True(first.IsSuccess, first.Message);
        Assert.True(second.IsSuccess, second.Message);
        Assert.True(first.Value!.SafePointMapAccepted);
        Assert.True(second.Value!.CacheHit);
        Assert.Equal(2, safePointChecks);

        provider.ResetProvider();
        var stale = verifier.VerifyForAdmission(envelope, Output, expectation,
            KernelResult.Ok, KernelResult.Ok, Validate);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.Equal(3, safePointChecks);
    }

    [Fact]
    public void SafePointMapCannotBeUsedWithoutLiveValidatorOrWithSubstitutedDigest()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        var required = Expected with
        {
            SafePointMapRequired = true,
            RequiredSafePointMapDigest = Envelope().Evidence.SafePointMapDigest,
        };

        Assert.Equal(KernelError.PlatformDenied,
            verifier.VerifyForAdmission(Envelope(), Output, required,
                KernelResult.Ok, KernelResult.Ok).Error);
        Assert.Equal(KernelError.PlatformDenied,
            verifier.VerifyForAdmission(Envelope(), Output,
                required with { RequiredSafePointMapDigest = D('9') },
                KernelResult.Ok, KernelResult.Ok, _ => KernelResult.Ok()).Error);
    }

    [Fact]
    public void StaticResourceEstimatesRequireExactDigestAndFreshAdvisoryPolicyEveryUse()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        var envelope = Envelope();
        var expectation = Expected with
        {
            StaticResourceEstimatesRequired = true,
            RequiredStaticResourceEstimateDigest =
                envelope.Evidence.StaticResourceEstimateDigest,
        };
        var policy = new V6StaticResourceEstimatePolicy(13,
        [
            new("canonical-basic-block-count", 8, "blocks"),
            new("canonical-instruction-count", 64, "instructions"),
            new("maximum-static-operands-per-instruction", 16, "operands"),
        ]);
        var checks = 0;
        KernelResult Validate(IReadOnlyList<LoweringStaticResourceEstimateFactV1> estimates)
        {
            checks++;
            return policy.Validate(estimates, 13);
        }

        var first = verifier.VerifyForAdmission(envelope, Output, expectation,
            KernelResult.Ok, KernelResult.Ok, liveStaticResourceEstimateCheck: Validate);
        var second = verifier.VerifyForAdmission(envelope, Output, expectation,
            KernelResult.Ok, KernelResult.Ok, liveStaticResourceEstimateCheck: Validate);

        Assert.True(first.IsSuccess, first.Message);
        Assert.True(second.IsSuccess, second.Message);
        Assert.True(first.Value!.StaticResourceEstimatesAccepted);
        Assert.True(second.Value!.CacheHit);
        Assert.Equal(2, checks);
        Assert.True(policy.AdvisoryOnly);
        Assert.False(policy.ReservesResources);
        Assert.False(policy.GuaranteesCapacity);

        policy.RotateGeneration();
        var stale = verifier.VerifyForAdmission(envelope, Output, expectation,
            KernelResult.Ok, KernelResult.Ok, liveStaticResourceEstimateCheck: Validate);
        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.Equal(3, checks);
    }

    [Fact]
    public void ResourceEstimateSubstitutionMissingValidatorAndExceededPolicyFailClosed()
    {
        var verifier = new V6CompilerLoweringEvidenceVerifier();
        var required = Expected with
        {
            StaticResourceEstimatesRequired = true,
            RequiredStaticResourceEstimateDigest =
                Envelope().Evidence.StaticResourceEstimateDigest,
        };
        Assert.Equal(KernelError.PlatformDenied,
            verifier.VerifyForAdmission(Envelope(), Output, required,
                KernelResult.Ok, KernelResult.Ok).Error);
        Assert.Equal(KernelError.PlatformDenied,
            verifier.VerifyForAdmission(Envelope(), Output,
                required with { RequiredStaticResourceEstimateDigest = D('9') },
                KernelResult.Ok, KernelResult.Ok,
                liveStaticResourceEstimateCheck: _ => KernelResult.Ok()).Error);

        var restrictive = new V6StaticResourceEstimatePolicy(13,
        [
            new("canonical-basic-block-count", 8, "blocks"),
            new("canonical-instruction-count", 31, "instructions"),
            new("maximum-static-operands-per-instruction", 16, "operands"),
        ]);
        Assert.Equal(KernelError.BudgetExceeded,
            verifier.VerifyForAdmission(Envelope(), Output, required,
                KernelResult.Ok, KernelResult.Ok,
                liveStaticResourceEstimateCheck: estimates => restrictive.Validate(estimates, 13)).Error);
    }

    private static CompilerLoweringEvidenceEnvelopeV1 Envelope()
    {
        var evidence = CompilerLoweringEvidenceV1.Create("compiler-contract-v6", D('a'), D('b'),
            Sha(Output), D('d'),
            [new("input", 0, 64, LoweringFootprintAccessV1.Read),
             new("output", 0, 32, LoweringFootprintAccessV1.Write)],
            [new("input", "output", true)],
            [new("release-before-publish", true, true)],
            [new("vector-add", "nearest-even", "wrap", 128)],
            [new("managed-sp-0", 0, D('e'))],
            [new("canonical-basic-block-count", 4, "blocks"),
             new("canonical-instruction-count", 32, "instructions"),
             new("maximum-static-operands-per-instruction", 8, "operands")]);
        return CompilerLoweringEvidenceEnvelopeV1.Create(evidence);
    }

    private static string Sha(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static string D(char value) => new(value, 64);
}
