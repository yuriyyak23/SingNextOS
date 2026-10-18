using System.Text.Json;

namespace SingPlus.Contracts;

public sealed class DriverManifestV1
{
    public const string CurrentSchemaId = "SingDriverManifestV1";
    public const int CurrentSchemaVersion = 1;

    private readonly CapabilityRequirementV1[] _requiredCapabilities;

    public DriverManifestV1(string driverId, IEnumerable<CapabilityRequirementV1> requiredCapabilities)
    {
        if (string.IsNullOrWhiteSpace(driverId)) throw new ArgumentException("Driver id is required.", nameof(driverId));
        DriverId = driverId;
        _requiredCapabilities = requiredCapabilities.OrderBy(static c => c.ResourceKind).ThenBy(static c => c.ResourceId, StringComparer.Ordinal).ThenBy(static c => (int)c.Rights).ToArray();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in _requiredCapabilities)
        {
            if (string.IsNullOrWhiteSpace(capability.ResourceId) || capability.Rights == CapabilityRights.None) throw new ArgumentException("Invalid driver capability requirement.", nameof(requiredCapabilities));
            if (!keys.Add($"{(int)capability.ResourceKind}:{capability.ResourceId}")) throw new ArgumentException("Duplicate driver capability requirement.", nameof(requiredCapabilities));
        }
    }

    public string SchemaId => CurrentSchemaId;
    public int SchemaVersion => CurrentSchemaVersion;
    public string DriverId { get; }
    public ExecutionRole ExecutionRole => ExecutionRole.Driver;
    public IReadOnlyList<CapabilityRequirementV1> RequiredCapabilities => _requiredCapabilities;

    public byte[] SerializeCanonical()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString(nameof(SchemaId), SchemaId);
            writer.WriteNumber(nameof(SchemaVersion), SchemaVersion);
            writer.WriteString(nameof(DriverId), DriverId);
            writer.WriteString(nameof(ExecutionRole), ExecutionRole.ToString());
            writer.WritePropertyName(nameof(RequiredCapabilities));
            writer.WriteStartArray();
            foreach (var capability in _requiredCapabilities)
            {
                writer.WriteStartObject();
                writer.WriteString(nameof(CapabilityRequirementV1.ResourceKind), capability.ResourceKind.ToString());
                writer.WriteString(nameof(CapabilityRequirementV1.ResourceId), capability.ResourceId);
                writer.WriteNumber(nameof(CapabilityRequirementV1.Rights), (int)capability.Rights);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }
        return stream.ToArray();
    }

    public string ComputeDigest() => SingProcessManifestV1.Digest(SerializeCanonical());
}
