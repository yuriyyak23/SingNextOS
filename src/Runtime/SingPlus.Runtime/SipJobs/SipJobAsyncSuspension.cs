using System.Collections.Immutable;

namespace SingPlus.Runtime;

internal enum SipJobSuspendedValueKind
{
    ClosedValue = 0,
    InvocationCorrelation,
    CancellationCorrelation,
    SessionPinIdentity,
    RegionUseIdentity,
    CapabilityLeaseIdentity,
}

internal sealed record SipJobSuspendedValueDescriptor(
    uint Version,
    string SlotId,
    SipJobSuspendedValueKind Kind,
    string SchemaOrOwnerId);

internal sealed record SipJobAsyncSuspensionDescriptor
{
    internal SipJobAsyncSuspensionDescriptor(
        uint version,
        string stageId,
        IEnumerable<SipJobSuspendedValueDescriptor> values,
        IEnumerable<string> declaredGateSet)
    {
        Version = version;
        StageId = stageId;
        Values = values.ToImmutableArray();
        DeclaredGateSet = declaredGateSet.ToImmutableArray();
    }

    internal uint Version { get; }
    internal string StageId { get; }
    internal ImmutableArray<SipJobSuspendedValueDescriptor> Values { get; }
    internal ImmutableArray<string> DeclaredGateSet { get; }
}

internal enum SipJobAsyncSuspensionError
{
    None = 0,
    UnknownVersion,
    Malformed,
    UnsupportedGate,
    UnsupportedState,
}

internal readonly record struct SipJobAsyncSuspensionResult(
    ImmutableArray<string> SlotIds,
    SipJobAsyncSuspensionError Error,
    string? Detail)
{
    internal bool IsSuccess => Error == SipJobAsyncSuspensionError.None;
}

internal static class SipJobAsyncSuspensionVerifier
{
    internal const uint Version = 1;
    internal const string LinearGate = "FG-JOB-LINEAR";
    internal const string AsyncGate = "FG-ASYNC-STAGE";

    internal static SipJobAsyncSuspensionResult Verify(SipJobAsyncSuspensionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Values.Any(static value => value is null))
            return Fail(SipJobAsyncSuspensionError.Malformed, "Suspended state cannot contain null entries.");
        if (descriptor.Version != Version || descriptor.Values.Any(static value => value.Version != Version))
            return Fail(SipJobAsyncSuspensionError.UnknownVersion, "Unsupported async suspension version.");
        if (descriptor.DeclaredGateSet is not [LinearGate, AsyncGate])
            return Fail(SipJobAsyncSuspensionError.UnsupportedGate, "Async suspension requires the exact closed gate set.");
        if (!Canonical(descriptor.StageId) || descriptor.Values.Length == 0 ||
            descriptor.Values.Any(static value => !Canonical(value.SlotId) || !Canonical(value.SchemaOrOwnerId)) ||
            descriptor.Values.Select(static value => value.SlotId).Distinct(StringComparer.Ordinal).Count() != descriptor.Values.Length)
            return Fail(SipJobAsyncSuspensionError.Malformed, "Suspended state identities must be non-empty, canonical, and unique.");
        if (descriptor.Values.Any(static value => !Enum.IsDefined(value.Kind)))
            return Fail(SipJobAsyncSuspensionError.UnsupportedState, "Unknown suspended state kind.");

        foreach (var value in descriptor.Values)
        {
            var valid = value.Kind switch
            {
                SipJobSuspendedValueKind.ClosedValue =>
                    value.SchemaOrOwnerId.StartsWith("schema:", StringComparison.Ordinal) &&
                    value.SchemaOrOwnerId.Length > "schema:".Length,
                SipJobSuspendedValueKind.InvocationCorrelation => value.SchemaOrOwnerId == "EndpointSessionInvocationRegistry",
                SipJobSuspendedValueKind.CancellationCorrelation => value.SchemaOrOwnerId == "CancellationScopeAuthority",
                SipJobSuspendedValueKind.SessionPinIdentity => value.SchemaOrOwnerId == "EndpointSessionRegistry",
                SipJobSuspendedValueKind.RegionUseIdentity => value.SchemaOrOwnerId == "RegionAuthority",
                SipJobSuspendedValueKind.CapabilityLeaseIdentity => value.SchemaOrOwnerId == "CapabilityAuthority",
                _ => false,
            };
            if (!valid)
                return Fail(SipJobAsyncSuspensionError.UnsupportedState,
                    $"Suspended slot '{value.SlotId}' does not name an exact closed schema or lifecycle owner.");
        }

        return new(descriptor.Values.Select(static value => value.SlotId).ToImmutableArray(),
            SipJobAsyncSuspensionError.None, null);
    }

    private static bool Canonical(string value) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim();

    private static SipJobAsyncSuspensionResult Fail(SipJobAsyncSuspensionError error, string detail) =>
        new([], error, detail);
}
