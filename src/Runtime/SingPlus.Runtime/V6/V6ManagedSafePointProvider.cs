using System.Security.Cryptography;
using System.Text;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal enum V6ManagedSafePointStatus
{
    Draining = 1,
    Contained,
    Quarantined,
}

internal sealed record V6ManagedSafePointReceipt(
    string ProviderIdentity,
    string OperationCorrelation,
    ulong OperationGeneration,
    ulong ProviderGeneration,
    ulong RuntimeGeneration,
    ulong RequestGeneration,
    ulong ObservedLatencyNanoseconds,
    PreemptionGuaranteeV1 Guarantee,
    IReadOnlyList<PreemptionLifecycleEventV1> Lifecycle)
{
    internal bool AuthorizesPreemption => false;
    internal bool AuthorizesExecution => false;
    internal bool ProvesPhysicalLatency => false;
}

/// <summary>
/// Deterministic managed reference provider for the P08 safe-point lifecycle. The callback is
/// the provider-owned executable hook and runs outside the provider lock. A receipt is admitted
/// only when exact generations remain current and the observed managed latency is within the
/// advertised bound. The receipt is evidence and never execution or containment authority.
/// </summary>
internal sealed class V6ManagedSafePointProvider
{
    private sealed record Request(ulong Generation, V6ManagedSafePointStatus Status);

    private readonly object _sync = new();
    private readonly Dictionary<string, Request> _requests = [];
    private readonly TimeProvider _timeProvider;
    private ulong _providerGeneration;
    private ulong _runtimeGeneration;
    private ulong _nextRequestGeneration;
    private bool _providerGenerationExhausted;
    private bool _runtimeGenerationExhausted;

    internal V6ManagedSafePointProvider(
        string providerIdentity,
        ulong maximumSafePointLatencyNanoseconds,
        ulong providerGeneration = 1,
        ulong runtimeGeneration = 1,
        TimeProvider? timeProvider = null)
    {
        ProviderIdentity = ValidateToken(providerIdentity);
        if (maximumSafePointLatencyNanoseconds < 100)
            throw new ArgumentOutOfRangeException(nameof(maximumSafePointLatencyNanoseconds),
                "The managed safe-point bound must be at least one TimeSpan tick (100 ns).");
        _providerGeneration = providerGeneration != 0 ? providerGeneration :
            throw new ArgumentOutOfRangeException(nameof(providerGeneration));
        _runtimeGeneration = runtimeGeneration != 0 ? runtimeGeneration :
            throw new ArgumentOutOfRangeException(nameof(runtimeGeneration));
        _timeProvider = timeProvider ?? TimeProvider.System;
        Guarantee = new PreemptionGuaranteeV1(PreemptionGuaranteeV1.CurrentVersion,
            PreemptionClassV1.SafePoint, PreemptionEffectSemanticsV1.ContainedAtSafePoint,
            maximumSafePointLatencyNanoseconds, false).Validate();
    }

    internal string ProviderIdentity { get; }
    internal PreemptionGuaranteeV1 Guarantee { get; }
    internal ulong ProviderGeneration { get { lock (_sync) return _providerGeneration; } }
    internal ulong RuntimeGeneration { get { lock (_sync) return _runtimeGeneration; } }

    /// <summary>
    /// Revalidates one compiler-derived safe-point selection against this provider's live
    /// generations. Exact-output and policy-digest binding remain the PCL verifier's job;
    /// this check only confirms that the current provider/runtime contour accepts the exact
    /// location and live-state shape. It creates no request and grants no authority.
    /// </summary>
    internal KernelResult ValidateLoweringSafePointMap(
        IReadOnlyList<LoweringSafePointFactV1> safePointMap,
        string safePointIdentity,
        ulong encodedAddress,
        string liveStateDigest,
        ulong expectedProviderGeneration,
        ulong expectedRuntimeGeneration)
    {
        ArgumentNullException.ThrowIfNull(safePointMap);
        try
        {
            ValidateToken(safePointIdentity);
            if (liveStateDigest.Length != 64 || liveStateDigest.Any(static value => !Uri.IsHexDigit(value)))
                throw new ArgumentException("Managed safe-point live-state digest is invalid.");
        }
        catch (ArgumentException exception)
        { return KernelResult.Fail(KernelError.InvalidMessage, exception.Message); }

        lock (_sync)
        {
            if (_providerGenerationExhausted || _runtimeGenerationExhausted ||
                expectedProviderGeneration == 0 || expectedRuntimeGeneration == 0 ||
                expectedProviderGeneration != _providerGeneration ||
                expectedRuntimeGeneration != _runtimeGeneration)
                return KernelResult.Fail(KernelError.StaleGeneration,
                    "Managed safe-point map dependencies are stale.");
            var matches = safePointMap.Where(point =>
                string.Equals(point.SafePointIdentity, safePointIdentity, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || matches[0].EncodedAddress != encodedAddress ||
                !FixedDigest(matches[0].LiveStateDigest, liveStateDigest))
                return KernelResult.Fail(KernelError.PlatformDenied,
                    "Managed safe-point map does not contain the exact provider-selected location and state shape.");
            return KernelResult.Ok();
        }
    }

    internal KernelResult<V6ManagedSafePointReceipt> RequestSafePoint(
        string operationCorrelation,
        ulong operationGeneration,
        ulong expectedProviderGeneration,
        ulong expectedRuntimeGeneration,
        Func<KernelResult> providerSafePoint)
    {
        ArgumentNullException.ThrowIfNull(providerSafePoint);
        try { ValidateToken(operationCorrelation); }
        catch (ArgumentException exception)
        { return KernelResult<V6ManagedSafePointReceipt>.Fail(KernelError.InvalidMessage, exception.Message); }
        if (operationGeneration == 0 || expectedProviderGeneration == 0 || expectedRuntimeGeneration == 0)
            return KernelResult<V6ManagedSafePointReceipt>.Fail(KernelError.InvalidMessage,
                "Safe-point dependency generations must be non-zero.");

        ulong requestGeneration;
        lock (_sync)
        {
            if (_providerGenerationExhausted || _runtimeGenerationExhausted ||
                expectedProviderGeneration != _providerGeneration ||
                expectedRuntimeGeneration != _runtimeGeneration)
                return KernelResult<V6ManagedSafePointReceipt>.Fail(KernelError.StaleGeneration,
                    "Managed safe-point provider or runtime generation is stale.");
            if (_requests.ContainsKey(operationCorrelation))
                return KernelResult<V6ManagedSafePointReceipt>.Fail(KernelError.InvalidTransition,
                    "The exact operation already has a safe-point attempt; ambiguous or terminal attempts cannot be retried under the same correlation.");
            if (_nextRequestGeneration == ulong.MaxValue)
                return KernelResult<V6ManagedSafePointReceipt>.Fail(KernelError.CapacityExhausted,
                    "Managed safe-point request generation space is exhausted.");
            requestGeneration = ++_nextRequestGeneration;
            _requests[operationCorrelation] = new(requestGeneration, V6ManagedSafePointStatus.Draining);
        }

        long started;
        try { started = _timeProvider.GetTimestamp(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Quarantine(operationCorrelation, requestGeneration);
            return KernelResult<V6ManagedSafePointReceipt>.Fail(KernelError.PlatformFaulted,
                $"Managed safe-point clock failed before the provider hook: {exception.Message}");
        }
        KernelResult callback;
        try { callback = providerSafePoint(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { callback = KernelResult.Fail(KernelError.PlatformUnavailable, exception.Message); }
        ulong elapsedNanoseconds;
        try
        {
            var completed = _timeProvider.GetTimestamp();
            var elapsed = _timeProvider.GetElapsedTime(started, completed);
            if (elapsed < TimeSpan.Zero)
                throw new InvalidOperationException("Managed safe-point clock moved backwards.");
            elapsedNanoseconds = checked((ulong)elapsed.Ticks * 100UL);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            Quarantine(operationCorrelation, requestGeneration);
            return KernelResult<V6ManagedSafePointReceipt>.Fail(KernelError.PlatformFaulted,
                exception.Message);
        }

        lock (_sync)
        {
            if (!_requests.TryGetValue(operationCorrelation, out var current) ||
                current.Generation != requestGeneration || current.Status != V6ManagedSafePointStatus.Draining ||
                _providerGenerationExhausted || _runtimeGenerationExhausted ||
                expectedProviderGeneration != _providerGeneration ||
                expectedRuntimeGeneration != _runtimeGeneration)
                return KernelResult<V6ManagedSafePointReceipt>.Fail(KernelError.StaleGeneration,
                    "Managed safe-point dependencies changed while the provider hook was in flight.");
            if (!callback.IsSuccess)
            {
                _requests[operationCorrelation] = current with { Status = V6ManagedSafePointStatus.Quarantined };
                return KernelResult<V6ManagedSafePointReceipt>.Fail(callback.Error, callback.Message!);
            }
            if (elapsedNanoseconds > Guarantee.MaximumSafePointLatencyNanoseconds)
            {
                _requests[operationCorrelation] = current with { Status = V6ManagedSafePointStatus.Quarantined };
                return KernelResult<V6ManagedSafePointReceipt>.Fail(KernelError.DeadlineExpired,
                    "Managed provider reached a safe point after its advertised latency bound.");
            }

            var evidence = EvidenceDigest(operationCorrelation, operationGeneration,
                expectedProviderGeneration, expectedRuntimeGeneration, requestGeneration,
                elapsedNanoseconds, Guarantee.MaximumSafePointLatencyNanoseconds);
            PreemptionLifecycleEventV1[] lifecycle =
            [new(1, 1, PreemptionLifecycleEventKindV1.Requested, evidence),
             new(1, 2, PreemptionLifecycleEventKindV1.Draining, evidence),
             new(1, 3, PreemptionLifecycleEventKindV1.SafePointReached, evidence),
             new(1, 4, PreemptionLifecycleEventKindV1.Contained, evidence)];
            _requests[operationCorrelation] = current with { Status = V6ManagedSafePointStatus.Contained };
            return KernelResult<V6ManagedSafePointReceipt>.Ok(new(ProviderIdentity,
                operationCorrelation, operationGeneration, expectedProviderGeneration,
                expectedRuntimeGeneration, requestGeneration, elapsedNanoseconds, Guarantee,
                Array.AsReadOnly(lifecycle)));
        }
    }

    internal void ResetProvider()
    {
        lock (_sync)
        {
            QuarantineDraining();
            if (_providerGeneration == ulong.MaxValue)
                _providerGenerationExhausted = true;
            else if (!_providerGenerationExhausted)
                _providerGeneration++;
        }
    }

    internal void ResetRuntime()
    {
        lock (_sync)
        {
            QuarantineDraining();
            if (_runtimeGeneration == ulong.MaxValue)
                _runtimeGenerationExhausted = true;
            else if (!_runtimeGenerationExhausted)
                _runtimeGeneration++;
        }
    }

    private void Quarantine(string correlation, ulong generation)
    {
        lock (_sync)
            if (_requests.TryGetValue(correlation, out var current) && current.Generation == generation)
                _requests[correlation] = current with { Status = V6ManagedSafePointStatus.Quarantined };
    }

    private void QuarantineDraining()
    {
        foreach (var key in _requests.Where(static item =>
                     item.Value.Status == V6ManagedSafePointStatus.Draining).Select(static item => item.Key).ToArray())
            _requests[key] = _requests[key] with { Status = V6ManagedSafePointStatus.Quarantined };
    }

    private static string EvidenceDigest(string correlation, ulong operationGeneration,
        ulong providerGeneration, ulong runtimeGeneration, ulong requestGeneration,
        ulong elapsedNanoseconds, ulong maximumNanoseconds)
    {
        var payload = $"1|{correlation}|{operationGeneration}|{providerGeneration}|{runtimeGeneration}|" +
                      $"{requestGeneration}|{elapsedNanoseconds}|{maximumNanoseconds}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string ValidateToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(value) > 256)
            throw new ArgumentException("Managed provider identity or operation correlation is not canonical.");
        return value;
    }

    private static bool FixedDigest(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left), Convert.FromHexString(right));
        }
        catch (FormatException) { return false; }
    }
}
