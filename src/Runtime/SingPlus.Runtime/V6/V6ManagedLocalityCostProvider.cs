using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal interface IV6LocalityCostEvidenceProvider
{
    KernelResult<LocalityCostEstimateV1> QueryFresh(string providerClass, long nowUnixMilliseconds);
    KernelResult<ulong> CurrentGeneration(string providerClass);
}

/// <summary>
/// Managed observation source for provider-neutral locality costs. Observations are
/// advisory only; this provider owns freshness and replay protection, never authority.
/// </summary>
internal sealed class V6ManagedLocalityCostProvider : IV6LocalityCostEvidenceProvider
{
    private readonly object gate = new();
    private readonly Dictionary<string, LocalityCostEstimateV1> estimates =
        new(StringComparer.Ordinal);

    internal KernelResult Publish(LocalityCostEstimateV1 estimate)
    {
        try { estimate.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return KernelResult.Fail(KernelError.InvalidMessage, exception.Message); }

        lock (gate)
        {
            if (estimates.TryGetValue(estimate.ProviderClass, out var current) &&
                estimate.EstimateGeneration <= current.EstimateGeneration)
                return KernelResult.Fail(KernelError.StaleGeneration,
                    "Locality cost evidence is stale or replayed.");
            estimates[estimate.ProviderClass] = estimate;
        }
        return KernelResult.Ok();
    }

    public KernelResult<LocalityCostEstimateV1> QueryFresh(
        string providerClass, long nowUnixMilliseconds)
    {
        if (!CanonicalProviderClass(providerClass) || nowUnixMilliseconds < 0)
            return KernelResult<LocalityCostEstimateV1>.Fail(KernelError.InvalidMessage,
                "Locality query identity or time is invalid.");
        lock (gate)
        {
            if (!estimates.TryGetValue(providerClass, out var estimate))
                return KernelResult<LocalityCostEstimateV1>.Fail(KernelError.PlatformUnavailable,
                    "No locality cost evidence exists for the provider class.");
            return estimate.IsFreshAt(nowUnixMilliseconds)
                ? KernelResult<LocalityCostEstimateV1>.Ok(estimate)
                : KernelResult<LocalityCostEstimateV1>.Fail(KernelError.StaleGeneration,
                    "Locality cost evidence is outside its freshness interval.");
        }
    }

    public KernelResult<ulong> CurrentGeneration(string providerClass)
    {
        if (!CanonicalProviderClass(providerClass))
            return KernelResult<ulong>.Fail(KernelError.InvalidMessage,
                "Locality provider identity is invalid.");
        lock (gate)
            return estimates.TryGetValue(providerClass, out var estimate)
                ? KernelResult<ulong>.Ok(estimate.EstimateGeneration)
                : KernelResult<ulong>.Fail(KernelError.StaleGeneration,
                    "Locality evidence was invalidated or is unavailable.");
    }

    internal void Restart()
    {
        lock (gate) estimates.Clear();
    }

    private static bool CanonicalProviderClass(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() &&
        value.Length <= 128 && !value.Any(char.IsControl);
}
