using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SingPlus.Contracts;

public sealed class SingProcessManifestV1
{
    public const string CurrentSchemaId = "SingProcessManifestV1";
    public const int CurrentSchemaVersion = 1;

    private readonly CapabilityRequirementV1[] _requiredCapabilities;
    private readonly string[] _requiredContracts;

    public SingProcessManifestV1(
        ProcessId processId,
        DomainId domainId,
        ulong generation,
        string entryIdentity,
        ExecutionRole executionRole,
        MemoryProfile memoryProfile,
        IEnumerable<CapabilityRequirementV1>? requiredCapabilities = null,
        IEnumerable<string>? requiredContracts = null,
        ResourceLimitsV1? resourceLimits = null,
        string schemaId = CurrentSchemaId,
        int schemaVersion = CurrentSchemaVersion)
    {
        if (!string.Equals(schemaId, CurrentSchemaId, StringComparison.Ordinal))
            throw new ArgumentException($"Unsupported schema id '{schemaId}'.", nameof(schemaId));
        if (schemaVersion != CurrentSchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, "Unsupported manifest schema version.");
        if (processId.Value == 0) throw new ArgumentOutOfRangeException(nameof(processId));
        if (domainId.Value == 0) throw new ArgumentOutOfRangeException(nameof(domainId));
        if (generation == 0) throw new ArgumentOutOfRangeException(nameof(generation));
        if (string.IsNullOrWhiteSpace(entryIdentity)) throw new ArgumentException("Entry identity is required.", nameof(entryIdentity));
        if (!Enum.IsDefined(executionRole)) throw new ArgumentOutOfRangeException(nameof(executionRole));
        if (!Enum.IsDefined(memoryProfile)) throw new ArgumentOutOfRangeException(nameof(memoryProfile));

        var limits = resourceLimits ?? ResourceLimitsV1.Default;
        if (limits.MaxMemoryBytes <= 0 || limits.MaxRegions <= 0 || limits.MaxChannels <= 0 || limits.MaxPendingMessages <= 0)
            throw new ArgumentOutOfRangeException(nameof(resourceLimits), "All resource limits must be positive.");

        _requiredCapabilities = (requiredCapabilities ?? []).
            OrderBy(static c => c.ResourceKind).
            ThenBy(static c => c.ResourceId, StringComparer.Ordinal).
            ThenBy(static c => (int)c.Rights).
            ToArray();

        var capabilityKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in _requiredCapabilities)
        {
            if (!Enum.IsDefined(capability.ResourceKind)) throw new ArgumentOutOfRangeException(nameof(requiredCapabilities));
            if (string.IsNullOrWhiteSpace(capability.ResourceId)) throw new ArgumentException("Capability resource id is required.", nameof(requiredCapabilities));
            if (capability.Rights == CapabilityRights.None) throw new ArgumentException("Capability rights cannot be empty.", nameof(requiredCapabilities));
            var key = $"{(int)capability.ResourceKind}:{capability.ResourceId}";
            if (!capabilityKeys.Add(key)) throw new ArgumentException($"Duplicate capability requirement '{key}'.", nameof(requiredCapabilities));
        }

        _requiredContracts = (requiredContracts ?? []).OrderBy(static c => c, StringComparer.Ordinal).ToArray();
        var contractSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in _requiredContracts)
        {
            if (string.IsNullOrWhiteSpace(contract)) throw new ArgumentException("Contract identity is required.", nameof(requiredContracts));
            if (!contractSet.Add(contract)) throw new ArgumentException($"Duplicate contract '{contract}'.", nameof(requiredContracts));
        }

        SchemaId = schemaId;
        SchemaVersion = schemaVersion;
        ProcessId = processId;
        DomainId = domainId;
        Generation = generation;
        EntryIdentity = entryIdentity;
        ExecutionRole = executionRole;
        MemoryProfile = memoryProfile;
        ResourceLimits = limits;
        ContractDigest = ComputeStringSetDigest(_requiredContracts);
    }

    public string SchemaId { get; }
    public int SchemaVersion { get; }
    public ProcessId ProcessId { get; }
    public DomainId DomainId { get; }
    public ulong Generation { get; }
    public string EntryIdentity { get; }
    public ExecutionRole ExecutionRole { get; }
    public MemoryProfile MemoryProfile { get; }
    public IReadOnlyList<CapabilityRequirementV1> RequiredCapabilities => _requiredCapabilities;
    public IReadOnlyList<string> RequiredContracts => _requiredContracts;
    public ResourceLimitsV1 ResourceLimits { get; }
    public string ContractDigest { get; }

    public byte[] SerializeCanonical()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString(nameof(SchemaId), SchemaId);
            writer.WriteNumber(nameof(SchemaVersion), SchemaVersion);
            writer.WriteNumber(nameof(ProcessId), ProcessId.Value);
            writer.WriteNumber(nameof(DomainId), DomainId.Value);
            writer.WriteNumber(nameof(Generation), Generation);
            writer.WriteString(nameof(EntryIdentity), EntryIdentity);
            writer.WriteString(nameof(ExecutionRole), ExecutionRole.ToString());
            writer.WriteString(nameof(MemoryProfile), MemoryProfile.ToString());
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
            writer.WritePropertyName(nameof(RequiredContracts));
            writer.WriteStartArray();
            foreach (var contract in _requiredContracts) writer.WriteStringValue(contract);
            writer.WriteEndArray();
            writer.WritePropertyName(nameof(ResourceLimits));
            writer.WriteStartObject();
            writer.WriteNumber(nameof(ResourceLimitsV1.MaxMemoryBytes), ResourceLimits.MaxMemoryBytes);
            writer.WriteNumber(nameof(ResourceLimitsV1.MaxRegions), ResourceLimits.MaxRegions);
            writer.WriteNumber(nameof(ResourceLimitsV1.MaxChannels), ResourceLimits.MaxChannels);
            writer.WriteNumber(nameof(ResourceLimitsV1.MaxPendingMessages), ResourceLimits.MaxPendingMessages);
            writer.WriteEndObject();
            writer.WriteString(nameof(ContractDigest), ContractDigest);
            writer.WriteEndObject();
            writer.Flush();
        }
        return stream.ToArray();
    }

    public string ComputeDigest() => Digest(SerializeCanonical());

    private static string ComputeStringSetDigest(IEnumerable<string> values) => Digest(Encoding.UTF8.GetBytes(string.Join("\n", values)));

    public static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
