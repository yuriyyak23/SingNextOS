using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace SingPlus.Contracts;

public enum SemanticExtensionRequirement : byte
{
    Optional = 1,
    Mandatory = 2,
}

/// <summary>A stable semantic class name. It is evidence vocabulary, never an authority identifier.</summary>
public readonly record struct SemanticExtensionClassId(string Value)
{
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct CanonicalSemanticExtensionDigest(string Value)
{
    public override string ToString() => Value ?? string.Empty;
}

public enum SemanticExtensionMatchStatus : byte
{
    Refines = 1,
    InvalidContract = 2,
    UnknownMandatory = 3,
    MissingMandatory = 4,
    OptionalAbsenceNotAllowed = 5,
    GuaranteeDoesNotRefine = 6,
}

public readonly record struct SemanticExtensionMatchDecision(
    SemanticExtensionMatchStatus Status,
    SemanticExtensionClassId ClassId)
{
    public bool IsAccepted => Status == SemanticExtensionMatchStatus.Refines;
    public bool AuthorizesExecution => false;
    public bool AuthorizesEffect => false;
}

/// <summary>
/// One canonical extension clause. Payload bytes are copied and exposed through a fresh array so
/// mutation after admission cannot change the digest. The clause grants no permission.
/// </summary>
public sealed record SemanticExtensionClauseV1
{
    public const ushort CurrentVersion = 1;
    public const int MaxClassIdBytes = 96;
    public const int MaxSchemaIdBytes = 160;
    public const int MaxPayloadBytes = 64 * 1024;

    private readonly byte[] _payload;

    private SemanticExtensionClauseV1(
        ushort version,
        SemanticExtensionClassId classId,
        string schemaId,
        ushort schemaVersion,
        SemanticExtensionRequirement requirement,
        byte[] payload)
    {
        Version = version;
        ClassId = classId;
        SchemaId = schemaId;
        SchemaVersion = schemaVersion;
        Requirement = requirement;
        _payload = payload;
    }

    public ushort Version { get; }
    public SemanticExtensionClassId ClassId { get; }
    public string SchemaId { get; }
    public ushort SchemaVersion { get; }
    public SemanticExtensionRequirement Requirement { get; }
    public ReadOnlyMemory<byte> Payload => _payload.ToArray();
    public bool AuthorizesExecution => false;
    public bool AuthorizesEffect => false;

    public static SemanticExtensionClauseV1 Create(
        SemanticExtensionClassId classId,
        string schemaId,
        ushort schemaVersion,
        SemanticExtensionRequirement requirement,
        ReadOnlySpan<byte> payload) =>
        CreateCore(CurrentVersion, classId, schemaId, schemaVersion, requirement, payload);

    internal static SemanticExtensionClauseV1 CreateCore(
        ushort version,
        SemanticExtensionClassId classId,
        string schemaId,
        ushort schemaVersion,
        SemanticExtensionRequirement requirement,
        ReadOnlySpan<byte> payload)
    {
        ValidateToken(classId.Value, MaxClassIdBytes, nameof(classId));
        ValidateToken(schemaId, MaxSchemaIdBytes, nameof(schemaId));
        if (version != CurrentVersion || schemaVersion == 0 || !Enum.IsDefined(requirement))
            throw new NotSupportedException("Semantic extension version, schema version, or requirement is unsupported.");
        if (payload.Length > MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), $"Extension payload exceeds {MaxPayloadBytes} bytes.");
        return new(version, new(classId.Value), schemaId, schemaVersion, requirement, payload.ToArray());
    }

    private static void ValidateToken(string? value, int maximumUtf8Bytes, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(value) > maximumUtf8Bytes)
            throw new ArgumentException("Semantic extension identifiers must be bounded canonical UTF-8 tokens.", parameter);
    }
}

public sealed record OperationSemanticExtensionsV1
{
    public const ushort CurrentVersion = 1;
    private readonly SemanticExtensionSetV1 _set;

    private OperationSemanticExtensionsV1(SemanticExtensionSetV1 set) => _set = set;

    public ushort Version => _set.Version;
    public IReadOnlyList<SemanticExtensionClauseV1> Clauses => _set.Clauses;
    public CanonicalSemanticExtensionDigest Digest => _set.Digest;
    public bool AuthorizesExecution => false;
    public bool AuthorizesEffect => false;

    public static OperationSemanticExtensionsV1 Create(IEnumerable<SemanticExtensionClauseV1> clauses) =>
        new(SemanticExtensionSetV1.Create(clauses));

    public byte[] SerializeCanonical() => _set.SerializeCanonical();
    public static OperationSemanticExtensionsV1 ParseCanonical(ReadOnlySpan<byte> bytes) =>
        new(SemanticExtensionSetV1.ParseCanonical(bytes));
}

public sealed record ExecutionGuaranteeExtensionsV1
{
    public const ushort CurrentVersion = 1;
    private readonly SemanticExtensionSetV1 _set;

    private ExecutionGuaranteeExtensionsV1(SemanticExtensionSetV1 set) => _set = set;

    public ushort Version => _set.Version;
    public IReadOnlyList<SemanticExtensionClauseV1> Clauses => _set.Clauses;
    public CanonicalSemanticExtensionDigest Digest => _set.Digest;
    public bool AuthorizesExecution => false;
    public bool AuthorizesEffect => false;

    public static ExecutionGuaranteeExtensionsV1 Create(IEnumerable<SemanticExtensionClauseV1> clauses) =>
        new(SemanticExtensionSetV1.Create(clauses));

    public byte[] SerializeCanonical() => _set.SerializeCanonical();
    public static ExecutionGuaranteeExtensionsV1 ParseCanonical(ReadOnlySpan<byte> bytes) =>
        new(SemanticExtensionSetV1.ParseCanonical(bytes));
}

public static class SemanticExtensionRefinementV1
{
    public static SemanticExtensionMatchDecision Evaluate(
        OperationSemanticExtensionsV1 requirements,
        ExecutionGuaranteeExtensionsV1 guarantees,
        IReadOnlySet<SemanticExtensionClassId> supportedClasses,
        Func<SemanticExtensionClauseV1, SemanticExtensionClauseV1, bool> refines,
        Func<SemanticExtensionClauseV1, bool> permitsOptionalAbsence)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(guarantees);
        ArgumentNullException.ThrowIfNull(supportedClasses);
        ArgumentNullException.ThrowIfNull(refines);
        ArgumentNullException.ThrowIfNull(permitsOptionalAbsence);

        var provided = guarantees.Clauses.ToDictionary(clause => clause.ClassId);
        foreach (var required in requirements.Clauses)
        {
            if (!supportedClasses.Contains(required.ClassId))
            {
                if (required.Requirement == SemanticExtensionRequirement.Mandatory)
                    return new(SemanticExtensionMatchStatus.UnknownMandatory, required.ClassId);
                if (!permitsOptionalAbsence(required))
                    return new(SemanticExtensionMatchStatus.OptionalAbsenceNotAllowed, required.ClassId);
                continue;
            }

            if (!provided.TryGetValue(required.ClassId, out var guarantee))
            {
                if (required.Requirement == SemanticExtensionRequirement.Mandatory)
                    return new(SemanticExtensionMatchStatus.MissingMandatory, required.ClassId);
                if (!permitsOptionalAbsence(required))
                    return new(SemanticExtensionMatchStatus.OptionalAbsenceNotAllowed, required.ClassId);
                continue;
            }

            if (!refines(guarantee, required))
                return new(SemanticExtensionMatchStatus.GuaranteeDoesNotRefine, required.ClassId);
        }

        return new(SemanticExtensionMatchStatus.Refines, default);
    }
}

public readonly record struct SemanticGenerationSnapshotV1(string Owner, ulong Generation);

/// <summary>Digest binding V1 objects, extension sets, and live-owner generation snapshots.</summary>
public sealed record SemanticBindingExtensionSetV1
{
    public const ushort CurrentVersion = 1;
    public const int MaxGenerationSnapshots = 32;

    private SemanticBindingExtensionSetV1(
        string obligationsV1Digest,
        string guaranteesV1Digest,
        CanonicalSemanticExtensionDigest operationExtensionsDigest,
        CanonicalSemanticExtensionDigest guaranteeExtensionsDigest,
        IReadOnlyList<SemanticGenerationSnapshotV1> generations,
        CanonicalSemanticExtensionDigest digest)
    {
        ObligationsV1Digest = obligationsV1Digest;
        GuaranteesV1Digest = guaranteesV1Digest;
        OperationExtensionsDigest = operationExtensionsDigest;
        GuaranteeExtensionsDigest = guaranteeExtensionsDigest;
        Generations = generations;
        Digest = digest;
    }

    public ushort Version => CurrentVersion;
    public string ObligationsV1Digest { get; }
    public string GuaranteesV1Digest { get; }
    public CanonicalSemanticExtensionDigest OperationExtensionsDigest { get; }
    public CanonicalSemanticExtensionDigest GuaranteeExtensionsDigest { get; }
    public IReadOnlyList<SemanticGenerationSnapshotV1> Generations { get; }
    public CanonicalSemanticExtensionDigest Digest { get; }
    public bool AuthorizesExecution => false;
    public bool AuthorizesEffect => false;

    public static SemanticBindingExtensionSetV1 Create(
        string obligationsV1Digest,
        string guaranteesV1Digest,
        OperationSemanticExtensionsV1 operationExtensions,
        ExecutionGuaranteeExtensionsV1 guaranteeExtensions,
        IEnumerable<SemanticGenerationSnapshotV1> generations)
    {
        ArgumentNullException.ThrowIfNull(operationExtensions);
        ArgumentNullException.ThrowIfNull(guaranteeExtensions);
        ArgumentNullException.ThrowIfNull(generations);
        ValidateSha256(obligationsV1Digest, nameof(obligationsV1Digest));
        ValidateSha256(guaranteesV1Digest, nameof(guaranteesV1Digest));
        var exact = generations.OrderBy(item => item.Owner, StringComparer.Ordinal).ToArray();
        if (exact.Length > MaxGenerationSnapshots || exact.Any(item => item.Generation == 0 ||
                string.IsNullOrWhiteSpace(item.Owner) || item.Owner != item.Owner.Trim() || item.Owner.Any(char.IsControl)) ||
            exact.Select(item => item.Owner).Distinct(StringComparer.Ordinal).Count() != exact.Length)
            throw new ArgumentException("Generation snapshot is invalid, duplicated, or exceeds its bound.", nameof(generations));

        var payload = new List<byte>();
        CanonicalEncoding.WriteUInt16(payload, CurrentVersion);
        CanonicalEncoding.WriteString(payload, obligationsV1Digest);
        CanonicalEncoding.WriteString(payload, guaranteesV1Digest);
        CanonicalEncoding.WriteString(payload, operationExtensions.Digest.Value);
        CanonicalEncoding.WriteString(payload, guaranteeExtensions.Digest.Value);
        CanonicalEncoding.WriteUInt16(payload, checked((ushort)exact.Length));
        foreach (var generation in exact)
        {
            CanonicalEncoding.WriteString(payload, generation.Owner);
            CanonicalEncoding.WriteUInt64(payload, generation.Generation);
        }
        var digest = CanonicalEncoding.Digest(payload.ToArray());
        return new(obligationsV1Digest, guaranteesV1Digest, operationExtensions.Digest,
            guaranteeExtensions.Digest, Array.AsReadOnly(exact), digest);
    }

    private static void ValidateSha256(string? value, string parameter)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A canonical SHA-256 hex digest is required.", parameter);
    }
}

internal sealed class SemanticExtensionSetV1
{
    internal const int MaxClauses = 64;
    internal const int MaxCanonicalBytes = 1024 * 1024;
    private static readonly byte[] Magic = "SNXSEM1"u8.ToArray();

    private SemanticExtensionSetV1(IReadOnlyList<SemanticExtensionClauseV1> clauses, byte[] bytes)
    {
        Version = 1;
        Clauses = clauses;
        _bytes = bytes;
        Digest = CanonicalEncoding.Digest(bytes);
    }

    private readonly byte[] _bytes;
    internal ushort Version { get; }
    internal IReadOnlyList<SemanticExtensionClauseV1> Clauses { get; }
    internal CanonicalSemanticExtensionDigest Digest { get; }
    internal byte[] SerializeCanonical() => _bytes.ToArray();

    internal static SemanticExtensionSetV1 Create(IEnumerable<SemanticExtensionClauseV1> clauses)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        var exact = clauses.ToArray();
        if (exact.Length > MaxClauses)
            throw new ArgumentOutOfRangeException(nameof(clauses), $"At most {MaxClauses} clauses are permitted.");
        if (exact.Any(clause => clause is null))
            throw new ArgumentException("Null semantic extension clause.", nameof(clauses));
        var ordered = exact.OrderBy(clause => clause.ClassId.Value, StringComparer.Ordinal).ToArray();
        if (ordered.Select(clause => clause.ClassId).Distinct().Count() != ordered.Length)
            throw new ArgumentException("Duplicate or conflicting semantic extension class.", nameof(clauses));

        var bytes = new List<byte>(Magic.Length + 4);
        bytes.AddRange(Magic);
        CanonicalEncoding.WriteUInt16(bytes, 1);
        CanonicalEncoding.WriteUInt16(bytes, checked((ushort)ordered.Length));
        foreach (var clause in ordered)
        {
            CanonicalEncoding.WriteUInt16(bytes, clause.Version);
            CanonicalEncoding.WriteString(bytes, clause.ClassId.Value);
            CanonicalEncoding.WriteString(bytes, clause.SchemaId);
            CanonicalEncoding.WriteUInt16(bytes, clause.SchemaVersion);
            bytes.Add((byte)clause.Requirement);
            CanonicalEncoding.WriteBytes(bytes, clause.Payload.Span);
            if (bytes.Count > MaxCanonicalBytes)
                throw new ArgumentOutOfRangeException(nameof(clauses), "Canonical extension set exceeds its byte bound.");
        }
        return new(Array.AsReadOnly(ordered), bytes.ToArray());
    }

    internal static SemanticExtensionSetV1 ParseCanonical(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxCanonicalBytes || bytes.Length < Magic.Length + 4 || !bytes[..Magic.Length].SequenceEqual(Magic))
            throw new FormatException("Semantic extension set header or size is invalid.");
        var reader = new CanonicalReader(bytes[Magic.Length..]);
        var version = reader.ReadUInt16();
        var count = reader.ReadUInt16();
        if (version != 1 || count > MaxClauses)
            throw new NotSupportedException("Semantic extension set version or clause count is unsupported.");
        var clauses = new SemanticExtensionClauseV1[count];
        for (var index = 0; index < count; index++)
        {
            var clauseVersion = reader.ReadUInt16();
            var classId = reader.ReadString(SemanticExtensionClauseV1.MaxClassIdBytes);
            var schemaId = reader.ReadString(SemanticExtensionClauseV1.MaxSchemaIdBytes);
            var schemaVersion = reader.ReadUInt16();
            var requirement = (SemanticExtensionRequirement)reader.ReadByte();
            var payload = reader.ReadBytes(SemanticExtensionClauseV1.MaxPayloadBytes);
            clauses[index] = SemanticExtensionClauseV1.CreateCore(clauseVersion, new(classId), schemaId,
                schemaVersion, requirement, payload);
        }
        reader.EnsureComplete();
        var parsed = Create(clauses);
        if (!parsed._bytes.AsSpan().SequenceEqual(bytes))
            throw new FormatException("Semantic extension set is not in canonical order or form.");
        return parsed;
    }
}

internal static class CanonicalEncoding
{
    internal static CanonicalSemanticExtensionDigest Digest(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexStringLower(SHA256.HashData(bytes)));

    internal static void WriteUInt16(List<byte> target, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        target.AddRange(bytes);
    }

    internal static void WriteUInt64(List<byte> target, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        target.AddRange(bytes);
    }

    internal static void WriteString(List<byte> target, string value) => WriteBytes(target, Encoding.UTF8.GetBytes(value));

    internal static void WriteBytes(List<byte> target, ReadOnlySpan<byte> value)
    {
        if (value.Length > int.MaxValue)
            throw new OverflowException("Canonical field length overflow.");
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        target.AddRange(length);
        target.AddRange(value);
    }
}

internal ref struct CanonicalReader
{
    private ReadOnlySpan<byte> _remaining;
    internal CanonicalReader(ReadOnlySpan<byte> bytes) => _remaining = bytes;

    internal byte ReadByte()
    {
        Require(1);
        var value = _remaining[0];
        _remaining = _remaining[1..];
        return value;
    }

    internal ushort ReadUInt16()
    {
        Require(2);
        var value = BinaryPrimitives.ReadUInt16BigEndian(_remaining);
        _remaining = _remaining[2..];
        return value;
    }

    internal string ReadString(int maximumBytes)
    {
        var bytes = ReadBytes(maximumBytes);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException exception) { throw new FormatException("Invalid UTF-8 in canonical semantic extension.", exception); }
    }

    internal byte[] ReadBytes(int maximumBytes)
    {
        Require(4);
        var length = BinaryPrimitives.ReadInt32BigEndian(_remaining);
        _remaining = _remaining[4..];
        if (length < 0 || length > maximumBytes)
            throw new FormatException("Canonical field length is invalid or exceeds its bound.");
        Require(length);
        var value = _remaining[..length].ToArray();
        _remaining = _remaining[length..];
        return value;
    }

    internal void EnsureComplete()
    {
        if (!_remaining.IsEmpty)
            throw new FormatException("Trailing bytes in canonical semantic extension set.");
    }

    private readonly void Require(int count)
    {
        if (count < 0 || _remaining.Length < count)
            throw new FormatException("Truncated canonical semantic extension set.");
    }
}
