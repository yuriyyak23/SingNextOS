using SingPlus.Contracts;

namespace SingPlus.Runtime;

/// <summary>
/// Pure value mapping into dimensions already owned by ResourceBudgetAuthority.
/// It neither reserves capacity nor upgrades provider-local resources into OS authority.
/// </summary>
internal static class ResourceEnvelopeBudgetMapping
{
    internal static KernelResult<IReadOnlyList<BudgetAmount>> Map(
        IReadOnlyList<ResourceEnvelopeV1> envelopes)
    {
        if (envelopes is null || envelopes.Count == 0)
            return KernelResult<IReadOnlyList<BudgetAmount>>.Fail(KernelError.InvalidMessage,
                "A non-empty semantic envelope set is required.");

        var mapped = new List<BudgetAmount>(envelopes.Count);
        var classes = new HashSet<ResourceClassV1>();
        foreach (var item in envelopes)
        {
            ResourceEnvelopeV1 envelope;
            try { envelope = item.Canonicalize(); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
            {
                return KernelResult<IReadOnlyList<BudgetAmount>>.Fail(
                    KernelError.InvalidMessage, exception.Message);
            }
            if (!classes.Add(envelope.ResourceClass))
                return KernelResult<IReadOnlyList<BudgetAmount>>.Fail(KernelError.InvalidMessage,
                    "Semantic resource classes must be unique within one atomic reservation set.");

            var amount = envelope.ResourceClass switch
            {
                ResourceClassV1.ComputeTime when envelope.Family == ResourceDimensionFamilyV1.Time &&
                    envelope.Unit == ResourceUnitV1.Nanoseconds && envelope.WindowNanoseconds == 0 =>
                    new BudgetAmount(ServiceBudgetDimension.ComputeTimeNanoseconds, envelope.Amount),
                _ => default,
            };
            if (amount.Amount == 0)
                return KernelResult<IReadOnlyList<BudgetAmount>>.Fail(KernelError.PlatformUnsupported,
                    $"{envelope.ResourceClass} is provider-local or lacks an exact SingNext budget dimension mapping.");
            mapped.Add(amount);
        }

        if (mapped.Select(static item => item.Dimension).Distinct().Count() != mapped.Count)
            return KernelResult<IReadOnlyList<BudgetAmount>>.Fail(KernelError.InvalidMessage,
                "Semantic envelopes alias one SingNext budget dimension.");
        return KernelResult<IReadOnlyList<BudgetAmount>>.Ok(
            mapped.OrderBy(static item => item.Dimension).ToArray());
    }
}
