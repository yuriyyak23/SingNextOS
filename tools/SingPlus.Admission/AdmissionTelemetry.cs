using System.Diagnostics;
using System.Text.Json;

namespace SingPlus.Admission;

public enum AdmissionTelemetryKind : byte
{
    VerificationCompleted,
    ViolationObserved
}

public readonly record struct AdmissionTelemetryEvent(
    int Sequence,
    AdmissionTelemetryKind Kind,
    string Method,
    string Detail,
    string? TraceId);

public static class AdmissionTelemetry
{
    public static readonly ActivitySource ActivitySource = new("SingPlus.Admission");
    private static readonly byte[] Newline = [(byte)'\n'];

    public static Activity StartObservation()
    {
        // ActivitySource alone returns null without a listener. Opt-in evidence still needs correlation.
        // This Activity is created only after verification and never enters proof or admission inputs.
        return ActivitySource.StartActivity("Verify") ?? new Activity("SingPlus.Admission.Verify").Start();
    }

    // Evidence only. No Activity or JSON value is an operation handle or capability.
    public static IReadOnlyList<AdmissionTelemetryEvent> Observe(AdmissionVerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var traceId = Activity.Current?.TraceId.ToString();
        var events = new List<AdmissionTelemetryEvent>
        {
            new(0, AdmissionTelemetryKind.VerificationCompleted, result.Proof.Root,
                result.IsAdmitted ? "Admitted" : "Rejected", traceId)
        };
        var sequence = 1;
        foreach (var violation in result.Violations.OrderBy(static v => v.CanonicalKey, StringComparer.Ordinal))
            events.Add(new(sequence++, AdmissionTelemetryKind.ViolationObserved, violation.Method,
                violation.Operation + ":" + violation.Detail, traceId));
        return events;
    }

    public static async Task WriteNdjsonAsync(Stream output, IEnumerable<AdmissionTelemetryEvent> events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(events);
        foreach (var item in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false, SkipValidation = false }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("Sequence", item.Sequence);
                writer.WriteString("Kind", item.Kind.ToString());
                writer.WriteString("Method", item.Method);
                writer.WriteString("Detail", item.Detail);
                if (item.TraceId is null) writer.WriteNull("TraceId");
                else writer.WriteString("TraceId", item.TraceId);
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken);
            }
            await output.WriteAsync(Newline.AsMemory(), cancellationToken);
        }
    }
}
