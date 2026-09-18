using System.Security.Cryptography;
using System.Text.Json;

namespace SingPlus.Contracts;

public sealed partial class ServiceManifestV1
{
    public byte[] SerializeCanonical()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString(nameof(SchemaId), SchemaId);
            writer.WriteNumber(nameof(SchemaVersion), SchemaVersion);
            writer.WriteString(nameof(Identity), Identity.Name);
            writer.WriteString(nameof(Version), Version.Value);
            writer.WriteString(nameof(ImageDigest), ImageDigest);
            writer.WriteString(nameof(Process), Process.ComputeDigest());
            writer.WriteString(nameof(EntryPoint), EntryPoint);
            WriteProvided(writer);
            WriteDependencies(writer);
            WritePlatformRequirements(writer);
            WriteResourceRequirements(writer);
            WriteBudgets(writer);
            WritePolicies(writer);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public string ComputeDigest() => Convert.ToHexString(SHA256.HashData(SerializeCanonical())).ToLowerInvariant();

    private void ValidatePolicies()
    {
        if (!Enum.IsDefined(RestartPolicy.Mode) ||
            (RestartPolicy.Mode == ServiceRestartMode.Never && (RestartPolicy.MaximumAttempts != 0 || RestartPolicy.InitialBackoffMilliseconds != 0 || RestartPolicy.MaximumBackoffMilliseconds != 0 || RestartPolicy.WindowMilliseconds != 0)) ||
            (RestartPolicy.Mode != ServiceRestartMode.Never && (RestartPolicy.MaximumAttempts == 0 || RestartPolicy.InitialBackoffMilliseconds == 0 || RestartPolicy.MaximumBackoffMilliseconds < RestartPolicy.InitialBackoffMilliseconds || RestartPolicy.WindowMilliseconds == 0)))
            throw new ArgumentException("Restart policy is inconsistent.", nameof(RestartPolicy));
        if (!Enum.IsDefined(DrainPolicy.TimeoutAction) || DrainPolicy.TimeoutMilliseconds == 0)
            throw new ArgumentException("Drain policy requires a defined timeout action and positive timeout.", nameof(DrainPolicy));
        if (!Enum.IsDefined(CheckpointPolicy.Mode))
            throw new ArgumentException("Checkpoint policy mode is undefined.", nameof(CheckpointPolicy));
        if (!Enum.IsDefined(TelemetryPolicy.Visibility) ||
            (TelemetryPolicy.Visibility == ServiceTelemetryVisibility.None) != (TelemetryPolicy.MaximumBufferedBytes == 0))
            throw new ArgumentException("Telemetry policy visibility and buffer limit are inconsistent.", nameof(TelemetryPolicy));
        if (Compatibility.MinimumRuntimeContractVersion == 0 ||
            (Compatibility.MaximumRuntimeContractVersion is { } maximum && maximum < Compatibility.MinimumRuntimeContractVersion))
            throw new ArgumentException("Compatibility contract range is invalid.", nameof(Compatibility));
    }

    private void WriteProvided(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(nameof(ProvidedContracts)); writer.WriteStartArray();
        foreach (var item in _provided) { writer.WriteStartArray(); writer.WriteStringValue(item.ServiceName); WriteContract(writer, item.Contract); writer.WriteEndArray(); }
        writer.WriteEndArray();
    }

    private void WriteDependencies(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(nameof(Dependencies)); writer.WriteStartArray();
        foreach (var item in _dependencies) { writer.WriteStartArray(); writer.WriteNumberValue((int)item.Kind); WriteContract(writer, item.Contract); writer.WriteEndArray(); }
        writer.WriteEndArray();
    }

    private void WritePlatformRequirements(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(nameof(PlatformRequirements)); writer.WriteStartArray();
        foreach (var item in _platformRequirements) { writer.WriteStartArray(); writer.WriteNumberValue((int)item.Family); writer.WriteNumberValue(item.MinimumContractVersion); writer.WriteNumberValue((int)item.Availability); writer.WriteNumberValue((int)item.Criticality); writer.WriteEndArray(); }
        writer.WriteEndArray();
    }

    private void WriteResourceRequirements(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(nameof(ResourceRequirements)); writer.WriteStartArray();
        foreach (var item in _resourceRequirements) { writer.WriteStartArray(); writer.WriteNumberValue((int)item.Kind); if (item.ResourceKind is { } kind) writer.WriteNumberValue((int)kind); else writer.WriteNullValue(); if (item.ResourceId is { } id) writer.WriteStringValue(id); else writer.WriteNullValue(); writer.WriteNumberValue((int)item.Rights); writer.WriteEndArray(); }
        writer.WriteEndArray();
    }

    private void WriteBudgets(Utf8JsonWriter writer)
    {
        writer.WritePropertyName(nameof(BudgetRequests)); writer.WriteStartArray();
        foreach (var item in _budgetRequests) { writer.WriteStartArray(); writer.WriteNumberValue((int)item.Dimension); writer.WriteNumberValue(item.Limit); writer.WriteEndArray(); }
        writer.WriteEndArray();
    }

    private void WritePolicies(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("Policies"); writer.WriteStartObject();
        writer.WriteNumber("RestartMode", (int)RestartPolicy.Mode); writer.WriteNumber("RestartMaximumAttempts", RestartPolicy.MaximumAttempts); writer.WriteNumber("RestartInitialBackoffMilliseconds", RestartPolicy.InitialBackoffMilliseconds); writer.WriteNumber("RestartMaximumBackoffMilliseconds", RestartPolicy.MaximumBackoffMilliseconds); writer.WriteNumber("RestartWindowMilliseconds", RestartPolicy.WindowMilliseconds);
        writer.WriteNumber("DrainTimeoutMilliseconds", DrainPolicy.TimeoutMilliseconds); writer.WriteNumber("DrainTimeoutAction", (int)DrainPolicy.TimeoutAction);
        writer.WriteNumber("CheckpointMode", (int)CheckpointPolicy.Mode);
        writer.WriteNumber("TelemetryVisibility", (int)TelemetryPolicy.Visibility); writer.WriteNumber("TelemetryMaximumBufferedBytes", TelemetryPolicy.MaximumBufferedBytes);
        writer.WriteNumber("MinimumRuntimeContractVersion", Compatibility.MinimumRuntimeContractVersion); if (Compatibility.MaximumRuntimeContractVersion is { } maximum) writer.WriteNumber("MaximumRuntimeContractVersion", maximum); else writer.WriteNull("MaximumRuntimeContractVersion");
        writer.WriteEndObject();
    }

    private static void WriteContract(Utf8JsonWriter writer, ServiceContractIdentity contract)
    {
        writer.WriteStartArray(); writer.WriteStringValue(contract.Name); writer.WriteStringValue(contract.Version); writer.WriteStringValue(contract.Digest); writer.WriteEndArray();
    }
}
