using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal readonly record struct V6StaticResourceEstimateLimit(
    string ResourceIdentity,
    ulong MaximumUpperBound,
    string Unit)
{
    internal V6StaticResourceEstimateLimit Validate()
    {
        _ = new LoweringStaticResourceEstimateFactV1(
            ResourceIdentity, MaximumUpperBound, Unit).Validate();
        return this;
    }
}

/// <summary>
/// Generation-bound live policy check for compiler advisory estimates. This object owns no
/// capacity and performs no reservation or accounting mutation; authoritative resource
/// admission remains with ResourceBudgetAuthority and the selected provider.
/// </summary>
internal sealed class V6StaticResourceEstimatePolicy
{
    private readonly object _sync = new();
    private readonly IReadOnlyDictionary<string, V6StaticResourceEstimateLimit> _limits;
    private ulong _generation;

    internal V6StaticResourceEstimatePolicy(
        ulong generation,
        IEnumerable<V6StaticResourceEstimateLimit> limits)
    {
        if (generation == 0) throw new ArgumentOutOfRangeException(nameof(generation));
        var exact = (limits ?? throw new ArgumentNullException(nameof(limits)))
            .Select(static limit => limit.Validate()).ToArray();
        if (exact.Length == 0 || exact.Select(static limit => limit.ResourceIdentity)
                .Distinct(StringComparer.Ordinal).Count() != exact.Length)
            throw new ArgumentException("Static resource estimate policy limits are empty or duplicate.",
                nameof(limits));
        _generation = generation;
        _limits = exact.ToDictionary(static limit => limit.ResourceIdentity,
            StringComparer.Ordinal);
    }

    internal ulong Generation { get { lock (_sync) return _generation; } }
    internal bool AdvisoryOnly => true;
    internal bool ReservesResources => false;
    internal bool GuaranteesCapacity => false;

    internal KernelResult Validate(
        IReadOnlyList<LoweringStaticResourceEstimateFactV1> estimates,
        ulong expectedGeneration)
    {
        ArgumentNullException.ThrowIfNull(estimates);
        lock (_sync)
        {
            if (expectedGeneration == 0 || expectedGeneration != _generation)
                return KernelResult.Fail(KernelError.StaleGeneration,
                    "Static resource estimate policy generation is stale.");
            if (estimates.Count != _limits.Count)
                return KernelResult.Fail(KernelError.PlatformDenied,
                    "Static resource estimate set does not match the live policy dimensions.");
            foreach (var estimate in estimates)
            {
                LoweringStaticResourceEstimateFactV1 exact;
                try { exact = estimate.Validate(); }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
                { return KernelResult.Fail(KernelError.InvalidMessage, exception.Message); }
                if (!_limits.TryGetValue(exact.ResourceIdentity, out var limit) ||
                    !string.Equals(exact.Unit, limit.Unit, StringComparison.Ordinal) ||
                    exact.UpperBound > limit.MaximumUpperBound)
                    return KernelResult.Fail(KernelError.BudgetExceeded,
                        "A static resource estimate exceeds or mismatches the live advisory policy.");
            }
            return KernelResult.Ok();
        }
    }

    internal void RotateGeneration()
    {
        lock (_sync) _generation = checked(_generation + 1);
    }
}
