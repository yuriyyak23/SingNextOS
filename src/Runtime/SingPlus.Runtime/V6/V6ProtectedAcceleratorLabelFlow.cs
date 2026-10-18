using System.Collections.Immutable;
using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal enum ProtectedAcceleratorLabelError { None = 0, Malformed, OperationMismatch, SequenceMismatch, LabelLaundering }

internal readonly record struct ProtectedAcceleratorLabelResult(
    ImmutableArray<ProtectedAcceleratorLabelSidebandV1> Sideband,
    ProtectedAcceleratorLabelError Error,
    string? Detail)
{
    internal bool IsSuccess => Error == ProtectedAcceleratorLabelError.None;
}

/// <summary>Provider-neutral validation for one protected accelerator operation.</summary>
internal static class V6ProtectedAcceleratorLabelFlow
{
    internal static ProtectedAcceleratorLabelResult Verify(
        IEnumerable<ProtectedAcceleratorLabelSidebandV1> sideband)
    {
        ArgumentNullException.ThrowIfNull(sideband);
        ProtectedAcceleratorLabelSidebandV1[] values;
        try
        {
            values = sideband.ToArray();
            foreach (var value in values) value.Validate();
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Fail(ProtectedAcceleratorLabelError.Malformed, exception.Message);
        }

        if (values.Length < 2 || values[0].ValueKind != ProtectedAcceleratorValueKindV1.Input ||
            values[^1].ValueKind != ProtectedAcceleratorValueKindV1.Output ||
            values.Skip(1).SkipLast(1).Any(static value => value.ValueKind != ProtectedAcceleratorValueKindV1.Intermediate) ||
            values.Select(static value => value.ValueId).Distinct(StringComparer.Ordinal).Count() != values.Length)
            return Fail(ProtectedAcceleratorLabelError.Malformed, "Sideband must contain input, optional intermediates, and output in order.");

        var first = values[0];
        var previousLabelGeneration = first.LabelGeneration;
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (value.Sequence != index + 1)
                return Fail(ProtectedAcceleratorLabelError.SequenceMismatch, "Sideband sequence must be contiguous and start at one.");
            if (!string.Equals(value.OperationId, first.OperationId, StringComparison.Ordinal) || value.ProviderGeneration != first.ProviderGeneration ||
                value.OperationGeneration != first.OperationGeneration)
                return Fail(ProtectedAcceleratorLabelError.OperationMismatch, "Every sideband value must bind the same provider operation generation.");
            if (value.Label != first.Label || value.LabelGeneration < previousLabelGeneration)
                return Fail(ProtectedAcceleratorLabelError.LabelLaundering, "Accelerator intermediates and output cannot implicitly change or roll back the input label.");
            previousLabelGeneration = value.LabelGeneration;
        }

        return new(values.ToImmutableArray(), ProtectedAcceleratorLabelError.None, null);
    }

    private static ProtectedAcceleratorLabelResult Fail(ProtectedAcceleratorLabelError error, string detail) =>
        new([], error, detail);
}
