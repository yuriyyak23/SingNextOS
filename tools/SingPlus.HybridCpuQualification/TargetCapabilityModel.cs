using System.Security.Cryptography;
using System.Text.Json;

namespace SingPlus.HybridCpuQualification;

// Target claims must come from an explicit provider description, never from host BCL or ISA detection.
public enum TargetNumericCapability : byte
{
    FP16Arithmetic,
    BFloat16Storage,
    BFloat16Arithmetic,
    Vector64,
    Vector128,
    Vector256,
    CrossLaneZip,
    CrossLaneUnzip,
    VectorConcat
}

public enum UnsupportedCapabilityDisposition : byte
{
    SoftwareLowering,
    LibraryCall,
    CompileTimeUnsupported
}

// One outcome per query. Native eligibility is a provider claim, not an implemented ISA instruction.
public enum TargetLoweringChoice : byte
{
    CompileTimeUnsupported = 0,
    SoftwareLowering = 1,
    LibraryCall = 2,
    NativeCapabilityConfirmed = 3
}

public sealed class TargetCapabilityDescription
{
    public const int SchemaVersion = 1;
    private readonly HashSet<TargetNumericCapability> _confirmed;

    public TargetCapabilityDescription(string providerId, IEnumerable<TargetNumericCapability> confirmed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(confirmed);
        if (providerId.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-')))
            throw new ArgumentException("Provider ID must use a stable ASCII identifier.", nameof(providerId));
        ProviderId = providerId;
        _confirmed = new HashSet<TargetNumericCapability>();
        foreach (var capability in confirmed)
        {
            if (!Enum.IsDefined(capability)) throw new ArgumentOutOfRangeException(nameof(confirmed));
            if (!_confirmed.Add(capability)) throw new ArgumentException("Target capability claims must be unique.", nameof(confirmed));
        }
    }

    public string ProviderId { get; }

    public byte[] SerializeCanonical()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("SchemaVersion", SchemaVersion);
            writer.WriteString("ProviderId", ProviderId);
            writer.WritePropertyName("ConfirmedCapabilities");
            writer.WriteStartArray();
            foreach (var capability in _confirmed.OrderBy(static capability => (byte)capability))
                writer.WriteNumberValue((byte)capability);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public string CanonicalDigest => Convert.ToHexString(SHA256.HashData(SerializeCanonical())).ToLowerInvariant();

    public static TargetCapabilityDescription DeserializeCanonical(ReadOnlySpan<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Target capability description must be an object.");
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 3 || properties[0].Name != "SchemaVersion" ||
                properties[1].Name != "ProviderId" || properties[2].Name != "ConfirmedCapabilities" ||
                properties[0].Value.ValueKind != JsonValueKind.Number ||
                !properties[0].Value.TryGetInt32(out var version) || version != SchemaVersion ||
                properties[1].Value.ValueKind != JsonValueKind.String ||
                properties[2].Value.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Target capability description schema or topology is unsupported.");
            var ids = new List<TargetNumericCapability>();
            var previous = -1;
            foreach (var item in properties[2].Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var id) ||
                    id <= previous || id > byte.MaxValue || !Enum.IsDefined((TargetNumericCapability)id))
                    throw new InvalidDataException("Target capability IDs must be known, unique and sorted.");
                ids.Add((TargetNumericCapability)id);
                previous = id;
            }
            var description = new TargetCapabilityDescription(properties[1].Value.GetString()!, ids);
            if (!description.SerializeCanonical().AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("Target capability description is not canonical JSON.");
            return description;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Target capability description is invalid JSON.", ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("Target capability provider or claim is invalid.", ex);
        }
    }

    public TargetLoweringChoice Decide(TargetNumericCapability requested, UnsupportedCapabilityDisposition fallback)
    {
        if (!Enum.IsDefined(requested)) throw new ArgumentOutOfRangeException(nameof(requested));
        if (!Enum.IsDefined(fallback)) throw new ArgumentOutOfRangeException(nameof(fallback));
        if (_confirmed.Contains(requested)) return TargetLoweringChoice.NativeCapabilityConfirmed;
        return fallback switch
        {
            UnsupportedCapabilityDisposition.SoftwareLowering => TargetLoweringChoice.SoftwareLowering,
            UnsupportedCapabilityDisposition.LibraryCall => TargetLoweringChoice.LibraryCall,
            _ => TargetLoweringChoice.CompileTimeUnsupported
        };
    }
}
