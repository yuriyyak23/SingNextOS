using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace SingPlus.Contracts;

[Flags]
public enum LoweringFootprintAccessV1 : byte
{
    Read = 1,
    Write = 2,
}

public readonly record struct LoweringFootprintFactV1(
    string Symbol,
    ulong Offset,
    ulong Length,
    LoweringFootprintAccessV1 Access)
{
    public LoweringFootprintFactV1 Validate()
    {
        CompilerLoweringEvidenceValidationV1.Token(Symbol, nameof(Symbol));
        const LoweringFootprintAccessV1 supported =
            LoweringFootprintAccessV1.Read | LoweringFootprintAccessV1.Write;
        if (Length == 0 || Offset > ulong.MaxValue - Length || Access == 0 || (Access & ~supported) != 0)
            throw new ArgumentException("Lowering footprint range or access is invalid.");
        return this;
    }
}

public readonly record struct LoweringAliasFactV1(string LeftSymbol, string RightSymbol, bool Disjoint)
{
    public LoweringAliasFactV1 Validate()
    {
        CompilerLoweringEvidenceValidationV1.Token(LeftSymbol, nameof(LeftSymbol));
        CompilerLoweringEvidenceValidationV1.Token(RightSymbol, nameof(RightSymbol));
        if (LeftSymbol == RightSymbol || !Disjoint)
            throw new ArgumentException("Only explicit disjointness between different symbols is supported.");
        return this;
    }
}

public readonly record struct LoweringOrderingFactV1(string ConstraintIdentity, bool Preserved, bool FenceEmitted)
{
    public LoweringOrderingFactV1 Validate()
    {
        CompilerLoweringEvidenceValidationV1.Token(ConstraintIdentity, nameof(ConstraintIdentity));
        if (!Preserved)
            throw new ArgumentException("An unpreserved source ordering constraint cannot be evidence.");
        return this;
    }
}

public readonly record struct LoweringNumericModeFactV1(
    string OperationClass,
    string RoundingMode,
    string OverflowMode,
    ushort VectorWidthBits)
{
    public LoweringNumericModeFactV1 Validate()
    {
        CompilerLoweringEvidenceValidationV1.Token(OperationClass, nameof(OperationClass));
        CompilerLoweringEvidenceValidationV1.Token(RoundingMode, nameof(RoundingMode));
        CompilerLoweringEvidenceValidationV1.Token(OverflowMode, nameof(OverflowMode));
        if (VectorWidthBits == 0 || VectorWidthBits > 4096 || (VectorWidthBits & (VectorWidthBits - 1)) != 0)
            throw new ArgumentException("Numeric vector width must be a bounded power of two.");
        return this;
    }
}

/// <summary>
/// Immutable compiler location and live-state shape for a potential safe point. The fact is
/// useful only after an independent runtime/provider check and never requests preemption.
/// </summary>
public readonly record struct LoweringSafePointFactV1(
    string SafePointIdentity,
    ulong EncodedAddress,
    string LiveStateDigest)
{
    public LoweringSafePointFactV1 Validate()
    {
        CompilerLoweringEvidenceValidationV1.Token(SafePointIdentity, nameof(SafePointIdentity));
        CompilerLoweringEvidenceValidationV1.Digest(LiveStateDigest, nameof(LiveStateDigest));
        return this with { LiveStateDigest = LiveStateDigest.ToLowerInvariant() };
    }
}

/// <summary>
/// Conservative static upper bound derived from immutable lowering artifacts. It is an
/// advisory admission input only: it does not reserve capacity or predict elapsed time.
/// </summary>
public readonly record struct LoweringStaticResourceEstimateFactV1(
    string ResourceIdentity,
    ulong UpperBound,
    string Unit)
{
    public bool AdvisoryOnly => true;
    public bool ReservesResources => false;
    public bool GuaranteesCapacity => false;

    public LoweringStaticResourceEstimateFactV1 Validate()
    {
        CompilerLoweringEvidenceValidationV1.Token(ResourceIdentity, nameof(ResourceIdentity));
        CompilerLoweringEvidenceValidationV1.Token(Unit, nameof(Unit));
        if (UpperBound == 0)
            throw new ArgumentOutOfRangeException(nameof(UpperBound),
                "A static resource estimate must carry a non-zero conservative upper bound.");
        return this;
    }
}

/// <summary>Immutable compiler facts bound to one exact output. The object grants no live permission.</summary>
public sealed record CompilerLoweringEvidenceV1
{
    public const ushort CurrentVersion = 1;
    public const string CurrentSchemaId = "singnext.compiler-lowering-evidence";
    private readonly ReadOnlyCollection<LoweringFootprintFactV1> _footprints;
    private readonly ReadOnlyCollection<LoweringAliasFactV1> _aliasFacts;
    private readonly ReadOnlyCollection<LoweringOrderingFactV1> _orderingFacts;
    private readonly ReadOnlyCollection<LoweringNumericModeFactV1> _numericFacts;
    private readonly ReadOnlyCollection<LoweringSafePointFactV1> _safePointMap;
    private readonly ReadOnlyCollection<LoweringStaticResourceEstimateFactV1> _staticResourceEstimates;

    private CompilerLoweringEvidenceV1(
        string compilerContractVersion, string toolchainDigest, string inputIrDigest,
        string outputBinaryOrBundleDigest, string producerDigest,
        LoweringFootprintFactV1[] footprints, LoweringAliasFactV1[] aliasFacts,
        LoweringOrderingFactV1[] orderingFacts, LoweringNumericModeFactV1[] numericFacts,
        LoweringSafePointFactV1[] safePointMap,
        LoweringStaticResourceEstimateFactV1[] staticResourceEstimates)
    {
        CompilerContractVersion = compilerContractVersion;
        ToolchainDigest = toolchainDigest;
        InputIrDigest = inputIrDigest;
        OutputBinaryOrBundleDigest = outputBinaryOrBundleDigest;
        ProducerDigest = producerDigest;
        _footprints = Array.AsReadOnly(footprints);
        _aliasFacts = Array.AsReadOnly(aliasFacts);
        _orderingFacts = Array.AsReadOnly(orderingFacts);
        _numericFacts = Array.AsReadOnly(numericFacts);
        _safePointMap = Array.AsReadOnly(safePointMap);
        _staticResourceEstimates = Array.AsReadOnly(staticResourceEstimates);
    }

    public ushort SchemaVersion => CurrentVersion;
    public string SchemaId => CurrentSchemaId;
    public string CompilerContractVersion { get; }
    public string ToolchainDigest { get; }
    public string InputIrDigest { get; }
    public string OutputBinaryOrBundleDigest { get; }
    public string ProducerDigest { get; }
    public IReadOnlyList<LoweringFootprintFactV1> Footprints => _footprints;
    public IReadOnlyList<LoweringAliasFactV1> AliasFacts => _aliasFacts;
    public IReadOnlyList<LoweringOrderingFactV1> OrderingFacts => _orderingFacts;
    public IReadOnlyList<LoweringNumericModeFactV1> NumericFacts => _numericFacts;
    public IReadOnlyList<LoweringSafePointFactV1> SafePointMap => _safePointMap;
    public IReadOnlyList<LoweringStaticResourceEstimateFactV1> StaticResourceEstimates =>
        _staticResourceEstimates;
    public bool AuthorizesExecution => false;
    public bool ProvesRuntimeLegality => false;
    public bool ProvesCurrentAuthority => false;
    public bool ProvesProviderAvailability => false;

    public static CompilerLoweringEvidenceV1 Create(
        string compilerContractVersion, string toolchainDigest, string inputIrDigest,
        string outputBinaryOrBundleDigest, string producerDigest,
        IEnumerable<LoweringFootprintFactV1> footprints,
        IEnumerable<LoweringAliasFactV1>? aliasFacts = null,
        IEnumerable<LoweringOrderingFactV1>? orderingFacts = null,
        IEnumerable<LoweringNumericModeFactV1>? numericFacts = null,
        IEnumerable<LoweringSafePointFactV1>? safePointMap = null,
        IEnumerable<LoweringStaticResourceEstimateFactV1>? staticResourceEstimates = null)
    {
        CompilerLoweringEvidenceValidationV1.Token(compilerContractVersion, nameof(compilerContractVersion));
        CompilerLoweringEvidenceValidationV1.Digest(toolchainDigest, nameof(toolchainDigest));
        CompilerLoweringEvidenceValidationV1.Digest(inputIrDigest, nameof(inputIrDigest));
        CompilerLoweringEvidenceValidationV1.Digest(outputBinaryOrBundleDigest, nameof(outputBinaryOrBundleDigest));
        CompilerLoweringEvidenceValidationV1.Digest(producerDigest, nameof(producerDigest));
        var footprintArray = (footprints ?? throw new ArgumentNullException(nameof(footprints)))
            .Select(static fact => fact.Validate()).OrderBy(static fact => fact.Symbol, StringComparer.Ordinal)
            .ThenBy(static fact => fact.Offset).ThenBy(static fact => fact.Length).ToArray();
        var aliasArray = (aliasFacts ?? []).Select(static fact => fact.Validate())
            .OrderBy(static fact => fact.LeftSymbol, StringComparer.Ordinal)
            .ThenBy(static fact => fact.RightSymbol, StringComparer.Ordinal).ToArray();
        var orderingArray = (orderingFacts ?? []).Select(static fact => fact.Validate())
            .OrderBy(static fact => fact.ConstraintIdentity, StringComparer.Ordinal).ToArray();
        var numericArray = (numericFacts ?? []).Select(static fact => fact.Validate())
            .OrderBy(static fact => fact.OperationClass, StringComparer.Ordinal).ToArray();
        var safePointArray = (safePointMap ?? []).Select(static fact => fact.Validate())
            .OrderBy(static fact => fact.SafePointIdentity, StringComparer.Ordinal).ToArray();
        var resourceEstimateArray = (staticResourceEstimates ?? [])
            .Select(static fact => fact.Validate())
            .OrderBy(static fact => fact.ResourceIdentity, StringComparer.Ordinal).ToArray();
        RejectDuplicates(footprintArray.Select(static fact => $"{fact.Symbol}:{fact.Offset}:{fact.Length}:{(byte)fact.Access}"), "footprint");
        RejectDuplicates(aliasArray.Select(static fact => $"{fact.LeftSymbol}:{fact.RightSymbol}"), "alias");
        RejectDuplicates(orderingArray.Select(static fact => fact.ConstraintIdentity), "ordering");
        RejectDuplicates(numericArray.Select(static fact => fact.OperationClass), "numeric mode");
        RejectDuplicates(safePointArray.Select(static fact => fact.SafePointIdentity), "safe-point identity");
        RejectDuplicates(resourceEstimateArray.Select(static fact => fact.ResourceIdentity),
            "static resource estimate");
        ValidateAliasFacts(footprintArray, aliasArray);
        return new(compilerContractVersion, toolchainDigest.ToLowerInvariant(), inputIrDigest.ToLowerInvariant(),
            outputBinaryOrBundleDigest.ToLowerInvariant(), producerDigest.ToLowerInvariant(),
            footprintArray, aliasArray, orderingArray, numericArray, safePointArray,
            resourceEstimateArray);
    }

    public byte[] SerializeCanonical()
    {
        var lines = new List<string>
        {
            $"schema={SchemaId}/{SchemaVersion}", $"compiler={CompilerContractVersion}",
            $"toolchain={ToolchainDigest}", $"input={InputIrDigest}",
            $"output={OutputBinaryOrBundleDigest}", $"producer={ProducerDigest}",
        };
        AppendStaticFacts(lines);
        return Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
    }

    public string EvidenceDigest => Convert.ToHexStringLower(SHA256.HashData(SerializeCanonical()));

    /// <summary>
    /// Digest of the immutable semantic facts only. A runtime requirement may bind this
    /// digest independently of compiler/output identities; it is evidence, never authority.
    /// </summary>
    public string StaticFactSetDigest
    {
        get
        {
            var lines = new List<string> { $"facts={SchemaId}/{SchemaVersion}" };
            AppendStaticFacts(lines);
            return Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")));
        }
    }

    /// <summary>Digest of the optional location/state-shape map, excluding all live state.</summary>
    public string SafePointMapDigest
    {
        get
        {
            var lines = new List<string> { $"safe-points={SchemaId}/{SchemaVersion}" };
            AppendSafePointFacts(lines);
            return Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")));
        }
    }

    /// <summary>Digest of advisory static upper bounds, excluding live capacity state.</summary>
    public string StaticResourceEstimateDigest
    {
        get
        {
            var lines = new List<string> { $"resource-estimates={SchemaId}/{SchemaVersion}" };
            AppendStaticResourceEstimates(lines);
            return Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")));
        }
    }

    private void AppendStaticFacts(List<string> lines)
    {
        lines.AddRange(_footprints.Select(static fact => $"footprint={fact.Symbol},{fact.Offset},{fact.Length},{(byte)fact.Access}"));
        lines.AddRange(_aliasFacts.Select(static fact => $"alias={fact.LeftSymbol},{fact.RightSymbol},disjoint"));
        lines.AddRange(_orderingFacts.Select(static fact => $"ordering={fact.ConstraintIdentity},preserved,{(fact.FenceEmitted ? 1 : 0)}"));
        lines.AddRange(_numericFacts.Select(static fact => $"numeric={fact.OperationClass},{fact.RoundingMode},{fact.OverflowMode},{fact.VectorWidthBits}"));
        AppendSafePointFacts(lines);
        AppendStaticResourceEstimates(lines);
    }

    private void AppendSafePointFacts(List<string> lines) =>
        lines.AddRange(_safePointMap.Select(static fact =>
            $"safe-point={fact.SafePointIdentity},{fact.EncodedAddress},{fact.LiveStateDigest}"));

    private void AppendStaticResourceEstimates(List<string> lines) =>
        lines.AddRange(_staticResourceEstimates.Select(static fact =>
            $"resource-estimate={fact.ResourceIdentity},{fact.UpperBound},{fact.Unit},advisory"));

    private static void RejectDuplicates(IEnumerable<string> identities, string category)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (identities.Any(identity => !seen.Add(identity)))
            throw new ArgumentException($"Duplicate {category} fact is not canonical.");
    }

    private static void ValidateAliasFacts(
        IReadOnlyList<LoweringFootprintFactV1> footprints, IReadOnlyList<LoweringAliasFactV1> aliases)
    {
        var symbols = footprints.Select(static fact => fact.Symbol).ToHashSet(StringComparer.Ordinal);
        foreach (var alias in aliases)
            if (!symbols.Contains(alias.LeftSymbol) || !symbols.Contains(alias.RightSymbol))
                throw new ArgumentException("Alias facts must reference declared footprint symbols.");
    }
}

internal static class CompilerLoweringEvidenceValidationV1
{
    internal static void Token(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 256 ||
            value.Any(character => char.IsControl(character) || character is '|' or ',' or '='))
            throw new ArgumentException("Lowering evidence token must be canonical and bounded.", parameter);
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Lowering evidence token must contain valid Unicode.", parameter, exception);
        }
    }

    internal static void Digest(string? value, string parameter)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Lowering evidence digest must be canonical SHA-256 hex.", parameter);
    }
}

public readonly record struct CompilerLoweringEvidenceEnvelopeV1(
    ushort SchemaVersion,
    string SchemaId,
    string EvidenceDigest,
    CompilerLoweringEvidenceV1 Evidence)
{
    public bool AuthorizesExecution => false;

    public static CompilerLoweringEvidenceEnvelopeV1 Create(CompilerLoweringEvidenceV1 evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new(CompilerLoweringEvidenceV1.CurrentVersion, CompilerLoweringEvidenceV1.CurrentSchemaId,
            evidence.EvidenceDigest, evidence);
    }

    public CompilerLoweringEvidenceEnvelopeV1 Validate()
    {
        ArgumentNullException.ThrowIfNull(Evidence);
        if (SchemaVersion != CompilerLoweringEvidenceV1.CurrentVersion ||
            SchemaId != CompilerLoweringEvidenceV1.CurrentSchemaId)
            throw new NotSupportedException("Compiler lowering evidence schema is unsupported.");
        CompilerLoweringEvidenceValidationV1.Digest(EvidenceDigest, nameof(EvidenceDigest));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(EvidenceDigest), Convert.FromHexString(Evidence.EvidenceDigest)))
            throw new ArgumentException("Compiler lowering evidence digest does not match its canonical facts.");
        return this;
    }
}
