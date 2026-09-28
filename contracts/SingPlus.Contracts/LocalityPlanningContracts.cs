namespace SingPlus.Contracts;

public readonly record struct LocalityCostEstimateV1(
    ushort Version,
    string ProviderClass,
    ulong EstimateGeneration,
    long ObservedAtUnixMilliseconds,
    long ValidUntilUnixMilliseconds,
    ulong DataMotionBytes,
    ulong EstimatedLatencyNanoseconds,
    ushort ContentionPermille,
    ushort ConfidencePermille)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesPlacement => false;
    public bool AuthorizesDataMotion => false;

    public LocalityCostEstimateV1 Validate()
    {
        if (Version != CurrentVersion)
            throw new NotSupportedException("Locality cost estimate version is unsupported.");
        if (string.IsNullOrWhiteSpace(ProviderClass) || ProviderClass != ProviderClass.Trim() ||
            ProviderClass.Any(char.IsControl) || ProviderClass.Length > 128)
            throw new ArgumentException("Provider class must be a bounded canonical provider-neutral identity.");
        if (EstimateGeneration == 0 || ObservedAtUnixMilliseconds < 0 ||
            ValidUntilUnixMilliseconds <= ObservedAtUnixMilliseconds || DataMotionBytes == 0 ||
            ContentionPermille > 1000 || ConfidencePermille > 1000)
            throw new ArgumentException("Locality cost estimate values are invalid.");
        return this;
    }

    public bool IsFreshAt(long unixMilliseconds) =>
        unixMilliseconds >= ObservedAtUnixMilliseconds && unixMilliseconds < ValidUntilUnixMilliseconds;
}

public enum DataMotionModeV1 : byte
{
    HostCopyThenReleaseSource = 1,
    ProviderDmaThenReleaseSource = 2,
}

/// <summary>Advisory plan. Exact owners still validate and commit every movement consequence.</summary>
public readonly record struct DataMotionPlanV1(
    ushort Version,
    string SourceProviderClass,
    string DestinationProviderClass,
    ulong SourceEstimateGeneration,
    ulong DestinationEstimateGeneration,
    ulong SourceRegionGeneration,
    ulong SourceMutationGeneration,
    ulong ByteLength,
    ulong MaximumTemporaryChargeBytes,
    DataMotionModeV1 Mode)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesRegionAccess => false;
    public bool AuthorizesMovement => false;
    public bool ReservesBudget => false;

    public DataMotionPlanV1 Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(Mode))
            throw new NotSupportedException("Data-motion plan version or mode is unsupported.");
        ValidateProvider(SourceProviderClass);
        ValidateProvider(DestinationProviderClass);
        if (SourceEstimateGeneration == 0 || DestinationEstimateGeneration == 0 ||
            SourceRegionGeneration == 0 || SourceMutationGeneration == 0 || ByteLength == 0 ||
            MaximumTemporaryChargeBytes < ByteLength)
            throw new ArgumentException("Data-motion plan generations or byte bounds are invalid.");
        return this;
    }

    private static void ValidateProvider(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl) || value.Length > 128)
            throw new ArgumentException("Data-motion provider identity is not canonical.");
    }
}

public static class LocalityPlanningPolicyV1
{
    public static DataMotionPlanV1 PlanHostMove(
        LocalityCostEstimateV1 source,
        IEnumerable<LocalityCostEstimateV1> destinations,
        long nowUnixMilliseconds,
        ulong sourceRegionGeneration,
        ulong sourceMutationGeneration,
        ulong maximumTemporaryChargeBytes)
    {
        var exactSource = source.Validate();
        if (!exactSource.IsFreshAt(nowUnixMilliseconds))
            throw new InvalidOperationException("Source locality evidence is stale.");
        var candidates = destinations.Select(static item => item.Validate())
            .Where(item => item.IsFreshAt(nowUnixMilliseconds) &&
                           item.DataMotionBytes == exactSource.DataMotionBytes)
            .OrderBy(Score)
            .ThenByDescending(static item => item.ConfidencePermille)
            .ThenBy(static item => item.ProviderClass, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
            throw new InvalidOperationException("No fresh compatible locality destination exists.");
        var selected = candidates[0];
        return new DataMotionPlanV1(1, exactSource.ProviderClass, selected.ProviderClass,
            exactSource.EstimateGeneration, selected.EstimateGeneration, sourceRegionGeneration,
            sourceMutationGeneration, exactSource.DataMotionBytes, maximumTemporaryChargeBytes,
            DataMotionModeV1.HostCopyThenReleaseSource).Validate();
    }

    private static decimal Score(LocalityCostEstimateV1 estimate) =>
        estimate.EstimatedLatencyNanoseconds * (1m + estimate.ContentionPermille / 1000m);
}

/// <summary>
/// Reproducible A/B result over provider observations. This is modeled-cost evidence,
/// not elapsed-time measurement, placement authority, or permission to promote policy.
/// </summary>
public readonly record struct LocalityPlanningExperimentV1(
    ushort Version,
    string ReferenceProviderClass,
    ulong ReferenceEstimateGeneration,
    ulong ReferenceAdjustedCostUnits,
    string SelectedProviderClass,
    ulong SelectedEstimateGeneration,
    ulong SelectedAdjustedCostUnits,
    ushort ModeledImprovementPermille)
{
    public const ushort CurrentVersion = 1;
    public bool ObservedModeledAdvantage => SelectedAdjustedCostUnits < ReferenceAdjustedCostUnits;
    public bool AuthorizesPlacement => false;
    public bool AuthorizesMovement => false;
    public bool QualifiesElapsedTimePerformance => false;
    public bool SupportsPolicyPromotion => false;

    public LocalityPlanningExperimentV1 Validate()
    {
        if (Version != CurrentVersion || string.IsNullOrWhiteSpace(ReferenceProviderClass) ||
            string.IsNullOrWhiteSpace(SelectedProviderClass) || ReferenceEstimateGeneration == 0 ||
            SelectedEstimateGeneration == 0 || ReferenceAdjustedCostUnits == 0 ||
            SelectedAdjustedCostUnits == 0 || ModeledImprovementPermille > 1000)
            throw new ArgumentException("Locality A/B result is incomplete or invalid.");
        if ((SelectedAdjustedCostUnits < ReferenceAdjustedCostUnits) != (ModeledImprovementPermille > 0))
            throw new ArgumentException("Locality A/B improvement does not match the observed adjusted costs.");
        return this;
    }
}

public static class LocalityPlanningExperimentPolicyV1
{
    public static LocalityPlanningExperimentV1 CompareAgainstReference(
        LocalityCostEstimateV1 source,
        IEnumerable<LocalityCostEstimateV1> destinations,
        string referenceProviderClass,
        long nowUnixMilliseconds,
        ulong sourceRegionGeneration,
        ulong sourceMutationGeneration,
        ulong maximumTemporaryChargeBytes)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        var candidates = destinations.Select(static item => item.Validate()).ToArray();
        var plan = LocalityPlanningPolicyV1.PlanHostMove(source, candidates, nowUnixMilliseconds,
            sourceRegionGeneration, sourceMutationGeneration, maximumTemporaryChargeBytes);
        var reference = candidates.SingleOrDefault(item =>
            StringComparer.Ordinal.Equals(item.ProviderClass, referenceProviderClass));
        if (reference.EstimateGeneration == 0 || !reference.IsFreshAt(nowUnixMilliseconds) ||
            reference.DataMotionBytes != source.DataMotionBytes)
            throw new InvalidOperationException("The reference arm lacks fresh compatible cost evidence.");
        var selected = candidates.Single(item =>
            item.EstimateGeneration == plan.DestinationEstimateGeneration &&
            StringComparer.Ordinal.Equals(item.ProviderClass, plan.DestinationProviderClass));
        var referenceCost = AdjustedCost(reference);
        var selectedCost = AdjustedCost(selected);
        var improvement = selectedCost < referenceCost
            ? (ushort)Math.Min(1000UL, (ulong)(((UInt128)(referenceCost - selectedCost) * 1000) / referenceCost))
            : (ushort)0;
        return new LocalityPlanningExperimentV1(LocalityPlanningExperimentV1.CurrentVersion,
            reference.ProviderClass, reference.EstimateGeneration, referenceCost,
            selected.ProviderClass, selected.EstimateGeneration, selectedCost, improvement).Validate();
    }

    private static ulong AdjustedCost(LocalityCostEstimateV1 estimate)
    {
        var value = (UInt128)estimate.EstimatedLatencyNanoseconds *
            (1000U + estimate.ContentionPermille) / 1000U;
        return value > ulong.MaxValue ? ulong.MaxValue : (ulong)value;
    }
}

public enum DataMotionLifecycleStateV1 : byte
{
    Planned = 1,
    SourcePinned = 2,
    Copied = 3,
    SourceReleased = 4,
    Completed = 5,
}

public sealed record DataMotionReceiptV1(
    ushort Version,
    string Correlation,
    DataMotionPlanV1 Plan,
    ulong SourceProcessGeneration,
    ulong DestinationProcessGeneration,
    ulong DestinationRegionGeneration,
    IReadOnlyList<DataMotionLifecycleStateV1> Lifecycle)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesRegionAccess => false;
    public bool AuthorizesMovement => false;

    public DataMotionReceiptV1 Validate()
    {
        if (Version != CurrentVersion || SourceProcessGeneration == 0 ||
            DestinationProcessGeneration == 0 || DestinationRegionGeneration == 0)
            throw new NotSupportedException("Data-motion receipt version or generation is unsupported.");
        Plan.Validate();
        if (string.IsNullOrWhiteSpace(Correlation) || Correlation != Correlation.Trim() ||
            Correlation.Any(char.IsControl) || Correlation.Length > 256)
            throw new ArgumentException("Data-motion correlation is not canonical.");
        DataMotionLifecycleStateV1[] expected =
        [
            DataMotionLifecycleStateV1.Planned, DataMotionLifecycleStateV1.SourcePinned,
            DataMotionLifecycleStateV1.Copied, DataMotionLifecycleStateV1.SourceReleased,
            DataMotionLifecycleStateV1.Completed,
        ];
        if (Lifecycle is null || !Lifecycle.SequenceEqual(expected))
            throw new ArgumentException("Data-motion lifecycle is incomplete or non-canonical.");
        return this with { Lifecycle = Array.AsReadOnly(Lifecycle.ToArray()) };
    }
}
