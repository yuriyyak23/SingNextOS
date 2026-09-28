using System.Security.Cryptography;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal sealed record V6LoweringEvidenceExpectation(
    string CompilerContractVersion,
    string ToolchainDigest,
    string ProducerDigest,
    ulong VerifierPolicyGeneration,
    bool FootprintRequired,
    string? RequiredStaticFactSetDigest = null,
    bool SafePointMapRequired = false,
    string? RequiredSafePointMapDigest = null,
    bool StaticResourceEstimatesRequired = false,
    string? RequiredStaticResourceEstimateDigest = null);

internal sealed record V6LoweringAdmissionReceipt(
    string OutputDigest,
    string? EvidenceDigest,
    bool EvidenceAccepted,
    bool UsedFallback,
    bool CacheHit,
    bool SafePointMapAccepted,
    bool StaticResourceEstimatesAccepted)
{
    internal bool AuthorizesExecution => false;
}

internal sealed class V6CompilerLoweringEvidenceVerifier
{
    internal const int MaximumCachedStaticDecisions = 1024;

    private readonly record struct StaticVerificationKey(
        ulong VerifierPolicyGeneration,
        string CompilerContractVersion,
        string ToolchainDigest,
        string ProducerDigest,
        string OutputDigest,
        string EvidenceDigest,
        bool FootprintRequired,
        string? RequiredStaticFactSetDigest,
        bool SafePointMapRequired,
        string? RequiredSafePointMapDigest,
        bool StaticResourceEstimatesRequired,
        string? RequiredStaticResourceEstimateDigest);

    private readonly object _sync = new();
    private readonly Dictionary<StaticVerificationKey, bool> _cache = new();
    private readonly Queue<StaticVerificationKey> _cacheInsertionOrder = new();
    internal int StaticVerificationCount { get; private set; }
    internal int CachedStaticDecisionCount
    {
        get { lock (_sync) return _cache.Count; }
    }

    internal KernelResult<V6LoweringAdmissionReceipt> VerifyMetadataForAdmission(
        ReadOnlyMemory<byte>? metadata,
        ReadOnlySpan<byte> exactOutput,
        V6LoweringEvidenceExpectation expectation,
        Func<KernelResult> liveAuthorityCheck,
        Func<KernelResult> runtimeLegalityCheck,
        Func<IReadOnlyList<LoweringSafePointFactV1>, KernelResult>? liveSafePointMapCheck = null,
        Func<IReadOnlyList<LoweringStaticResourceEstimateFactV1>, KernelResult>?
            liveStaticResourceEstimateCheck = null)
    {
        CompilerLoweringEvidenceEnvelopeV1? envelope = null;
        if (metadata is { } present)
        {
            try { envelope = CompilerLoweringEvidenceMetadataCodecV1.Parse(present.Span); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            { return KernelResult<V6LoweringAdmissionReceipt>.Fail(KernelError.InvalidMessage, exception.Message); }
        }
        return VerifyForAdmission(envelope, exactOutput, expectation, liveAuthorityCheck,
            runtimeLegalityCheck, liveSafePointMapCheck, liveStaticResourceEstimateCheck);
    }

    internal KernelResult<V6LoweringAdmissionReceipt> VerifyForAdmission(
        CompilerLoweringEvidenceEnvelopeV1? envelope,
        ReadOnlySpan<byte> exactOutput,
        V6LoweringEvidenceExpectation expectation,
        Func<KernelResult> liveAuthorityCheck,
        Func<KernelResult> runtimeLegalityCheck,
        Func<IReadOnlyList<LoweringSafePointFactV1>, KernelResult>? liveSafePointMapCheck = null,
        Func<IReadOnlyList<LoweringStaticResourceEstimateFactV1>, KernelResult>?
            liveStaticResourceEstimateCheck = null)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(liveAuthorityCheck);
        ArgumentNullException.ThrowIfNull(runtimeLegalityCheck);
        var outputDigest = Convert.ToHexStringLower(SHA256.HashData(exactOutput));
        var accepted = false;
        var fallback = envelope is null;
        var cacheHit = false;
        string? evidenceDigest = null;
        IReadOnlyList<LoweringSafePointFactV1>? acceptedSafePoints = null;
        IReadOnlyList<LoweringStaticResourceEstimateFactV1>? acceptedResourceEstimates = null;

        if (envelope is not null)
        {
            CompilerLoweringEvidenceV1 evidence;
            try { evidence = envelope.Value.Validate().Evidence; }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            { return KernelResult<V6LoweringAdmissionReceipt>.Fail(KernelError.InvalidMessage, exception.Message); }
            evidenceDigest = envelope.Value.EvidenceDigest;
            var key = new StaticVerificationKey(
                expectation.VerifierPolicyGeneration, expectation.CompilerContractVersion,
                expectation.ToolchainDigest, expectation.ProducerDigest, outputDigest, evidenceDigest,
                expectation.FootprintRequired, expectation.RequiredStaticFactSetDigest,
                expectation.SafePointMapRequired, expectation.RequiredSafePointMapDigest,
                expectation.StaticResourceEstimatesRequired,
                expectation.RequiredStaticResourceEstimateDigest);
            lock (_sync)
            {
                cacheHit = _cache.TryGetValue(key, out accepted);
                if (!cacheHit)
                {
                    StaticVerificationCount++;
                    accepted = EvidenceMatches(evidence, outputDigest, expectation);
                    if (_cache.Count == MaximumCachedStaticDecisions)
                        _cache.Remove(_cacheInsertionOrder.Dequeue());
                    _cache[key] = accepted;
                    _cacheInsertionOrder.Enqueue(key);
                }
            }
            if (!accepted && (expectation.FootprintRequired || expectation.SafePointMapRequired ||
                              expectation.StaticResourceEstimatesRequired))
                return KernelResult<V6LoweringAdmissionReceipt>.Fail(KernelError.PlatformDenied,
                    "Required compiler lowering evidence did not match the exact static tuple.");
            if (accepted && expectation.SafePointMapRequired)
                acceptedSafePoints = evidence.SafePointMap;
            if (accepted && expectation.StaticResourceEstimatesRequired)
                acceptedResourceEstimates = evidence.StaticResourceEstimates;
            fallback = !accepted;
        }
        else if (expectation.FootprintRequired || expectation.SafePointMapRequired ||
                 expectation.StaticResourceEstimatesRequired)
        {
            return KernelResult<V6LoweringAdmissionReceipt>.Fail(KernelError.PlatformDenied,
                "This feature requires exact compiler lowering evidence.");
        }

        if (acceptedSafePoints is not null)
        {
            if (liveSafePointMapCheck is null)
                return KernelResult<V6LoweringAdmissionReceipt>.Fail(KernelError.PlatformDenied,
                    "A required safe-point map has no live runtime/provider validator.");
            KernelResult safePointDecision;
            try { safePointDecision = liveSafePointMapCheck(acceptedSafePoints); }
            catch (Exception exception)
            { return KernelResult<V6LoweringAdmissionReceipt>.Fail(KernelError.PlatformUnavailable, exception.Message); }
            if (!safePointDecision.IsSuccess)
                return KernelResult<V6LoweringAdmissionReceipt>.Fail(
                    safePointDecision.Error, safePointDecision.Message!);
        }

        if (acceptedResourceEstimates is not null)
        {
            if (liveStaticResourceEstimateCheck is null)
                return KernelResult<V6LoweringAdmissionReceipt>.Fail(KernelError.PlatformDenied,
                    "Required static resource estimates have no live policy validator.");
            KernelResult resourceDecision;
            try { resourceDecision = liveStaticResourceEstimateCheck(acceptedResourceEstimates); }
            catch (Exception exception)
            { return KernelResult<V6LoweringAdmissionReceipt>.Fail(KernelError.PlatformUnavailable, exception.Message); }
            if (!resourceDecision.IsSuccess)
                return KernelResult<V6LoweringAdmissionReceipt>.Fail(
                    resourceDecision.Error, resourceDecision.Message!);
        }

        foreach (var check in new[] { liveAuthorityCheck, runtimeLegalityCheck })
        {
            KernelResult decision;
            try { decision = check(); }
            catch (Exception exception) { return KernelResult<V6LoweringAdmissionReceipt>.Fail(KernelError.PlatformUnavailable, exception.Message); }
            if (!decision.IsSuccess)
                return KernelResult<V6LoweringAdmissionReceipt>.Fail(decision.Error, decision.Message!);
        }
        return KernelResult<V6LoweringAdmissionReceipt>.Ok(new(outputDigest, evidenceDigest,
            accepted, fallback, cacheHit, acceptedSafePoints is not null,
            acceptedResourceEstimates is not null));
    }

    private static bool EvidenceMatches(
        CompilerLoweringEvidenceV1 evidence, string outputDigest, V6LoweringEvidenceExpectation expected) =>
        evidence.CompilerContractVersion == expected.CompilerContractVersion &&
        evidence.ToolchainDigest == expected.ToolchainDigest &&
        evidence.ProducerDigest == expected.ProducerDigest &&
        evidence.OutputBinaryOrBundleDigest == outputDigest &&
        (!expected.FootprintRequired ||
         (evidence.Footprints.Count > 0 &&
          FixedDigest(evidence.StaticFactSetDigest, expected.RequiredStaticFactSetDigest))) &&
        (expected.RequiredStaticFactSetDigest is null ||
         FixedDigest(evidence.StaticFactSetDigest, expected.RequiredStaticFactSetDigest)) &&
        (!expected.SafePointMapRequired ||
         (evidence.SafePointMap.Count > 0 &&
          FixedDigest(evidence.SafePointMapDigest, expected.RequiredSafePointMapDigest))) &&
        (expected.RequiredSafePointMapDigest is null ||
         FixedDigest(evidence.SafePointMapDigest, expected.RequiredSafePointMapDigest)) &&
        (!expected.StaticResourceEstimatesRequired ||
         (evidence.StaticResourceEstimates.Count > 0 &&
          FixedDigest(evidence.StaticResourceEstimateDigest,
              expected.RequiredStaticResourceEstimateDigest))) &&
        (expected.RequiredStaticResourceEstimateDigest is null ||
         FixedDigest(evidence.StaticResourceEstimateDigest,
             expected.RequiredStaticResourceEstimateDigest));

    private static bool FixedDigest(string actual, string? expected)
    {
        if (expected is null || expected.Length != 64 || expected.Any(static value => !Uri.IsHexDigit(value)))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actual), Convert.FromHexString(expected));
    }
}
