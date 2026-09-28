namespace SingPlus.Contracts;

/// <summary>
/// Result of comparing a presented generation with the generation held by the
/// authoritative owner of a resource. This value is validation evidence only;
/// it does not identify an authority and cannot authorize an effect.
/// </summary>
public enum GenerationMatch
{
    Invalid = 0,
    Exact = 1,
    Stale = 2,
}

/// <summary>
/// Shared mechanical comparison for generation-bearing handles. Callers must
/// obtain <paramref name="authoritativeGeneration"/> from the resource's
/// existing authoritative registry. Unequal generations are deliberately not
/// ordered: both older and fabricated future values are stale.
/// </summary>
public static class OperabilityGeneration
{
    public static GenerationMatch Compare(ulong authoritativeGeneration, ulong presentedGeneration)
    {
        if (authoritativeGeneration == 0 || presentedGeneration == 0)
            return GenerationMatch.Invalid;

        return authoritativeGeneration == presentedGeneration
            ? GenerationMatch.Exact
            : GenerationMatch.Stale;
    }
}
