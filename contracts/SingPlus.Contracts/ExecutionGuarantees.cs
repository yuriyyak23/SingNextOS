using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace SingPlus.Contracts;

public enum SemanticClaimLevelV1 : byte
{
    ModelOnly = 1,
    StaticAdmission = 2,
    RuntimeEnforced = 3,
    ExecutableAdapter = 4,
    EnforcedUpperBound = 5,
    GuaranteedReservation = 6,
    ProductionQualified = 7,
}

public enum GuaranteeEvidenceSourceV1 : byte
{
    None = 1,
    ContractSemantics = 2,
    RuntimeAdmissionReceipt = 3,
    RuntimeLegalityDecision = 4,
    LifecycleReceipt = 5,
    PublicationEvidenceGate = 6,
    CancellationReceipt = 7,
    MeasurementReceipt = 8,
}

public enum ExecutionAdmissionClassV1 : byte
{
    None = 1,
    IndependentRuntimeLegalityAndProviderAdmission = 2,
}

public enum MeasurementClassV1 : byte
{
    None = 1,
    BoundUsageEvidence = 2,
}

public enum ContentionClassV1 : byte
{
    None = 1,
    ControlledContention = 2,
}

public enum FailureHandlingClassV1 : byte
{
    None = 1,
    FailClosedExactGeneration = 2,
}

public enum RetireEvidenceClassV1 : byte
{
    None = 1,
    DistinctCompletionVisibilityPublicationRelease = 2,
}

public readonly record struct ProviderContractIdentityV1(
    string PackageId,
    string PackageVersion,
    string SchemaIdentity);

public readonly record struct ExecutionGuaranteeDimensionV1<TClass>(
    ushort Version,
    SemanticGuaranteeSupportV1 Support,
    TClass ProvidedClass,
    SemanticClaimLevelV1 ClaimLevel,
    GuaranteeEvidenceSourceV1 EvidenceSource)
    where TClass : struct, Enum
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
    public bool AuthorizesEffect => false;
}

public readonly record struct ExecutionGuaranteesDigestV1(string Value);

/// <summary>
/// Immutable provider/runtime claims. Each supported dimension names its evidence
/// source and claim ceiling; this descriptor never authorizes SingNext work.
/// </summary>
public sealed record ExecutionGuaranteesV1(
    ushort Version,
    ProviderContractIdentityV1 ProviderContract,
    string ProviderExecutionClass,
    ExecutionGuaranteeDimensionV1<ExecutionAdmissionClassV1> ExecutionAdmission,
    ExecutionGuaranteeDimensionV1<MeasurementClassV1> Measurement,
    ExecutionGuaranteeDimensionV1<ResourceAssuranceV1> ResourceEnforcement,
    ExecutionGuaranteeDimensionV1<CancellationClassV1> PreemptionCancellation,
    ExecutionGuaranteeDimensionV1<IsolationClassV1> Isolation,
    ExecutionGuaranteeDimensionV1<ContentionClassV1> Contention,
    ExecutionGuaranteeDimensionV1<PublicationEnforcementClassV1> VisibilityPublication,
    ExecutionGuaranteeDimensionV1<ReplayClassV1> Replay,
    ExecutionGuaranteeDimensionV1<DeterminismClassV1> Determinism,
    ExecutionGuaranteeDimensionV1<ContainmentClassV1> Containment,
    ExecutionGuaranteeDimensionV1<FailureHandlingClassV1> FailureHandling,
    ExecutionGuaranteeDimensionV1<LocalityClassV1> Locality,
    ExecutionGuaranteeDimensionV1<RetireEvidenceClassV1> RetireEvidence,
    ExecutionGuaranteesDigestV1 Digest = default)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
    public bool AuthorizesEffect => false;
    public bool AuthorizesPublication => false;
    public bool IsProviderAdmission => false;

    public ExecutionGuaranteesV1 Canonicalize()
    {
        if (Version != CurrentVersion) throw new NotSupportedException("Unknown execution-guarantees version.");
        if (string.IsNullOrWhiteSpace(ProviderContract.PackageId) ||
            string.IsNullOrWhiteSpace(ProviderContract.PackageVersion) ||
            string.IsNullOrWhiteSpace(ProviderContract.SchemaIdentity) ||
            ProviderContract.PackageId != ProviderContract.PackageId.Trim() ||
            ProviderContract.PackageVersion != ProviderContract.PackageVersion.Trim() ||
            ProviderContract.SchemaIdentity != ProviderContract.SchemaIdentity.Trim())
            throw new ArgumentException("Provider contract identity is non-canonical.");
        if (string.IsNullOrWhiteSpace(ProviderExecutionClass) ||
            ProviderExecutionClass != ProviderExecutionClass.Trim())
            throw new ArgumentException("Provider execution class is non-canonical.");

        Validate(ExecutionAdmission);
        Validate(Measurement);
        Validate(ResourceEnforcement);
        Validate(PreemptionCancellation);
        Validate(Isolation);
        Validate(Contention);
        Validate(VisibilityPublication);
        Validate(Replay);
        Validate(Determinism);
        Validate(Containment);
        Validate(FailureHandling);
        Validate(Locality);
        Validate(RetireEvidence);

        var canonical = this with { Digest = default };
        var expected = new ExecutionGuaranteesDigestV1(ComputeDigest(canonical));
        if (!string.IsNullOrEmpty(Digest.Value) && !string.Equals(Digest.Value, expected.Value, StringComparison.Ordinal))
            throw new ArgumentException("Execution-guarantees digest does not match canonical content.", nameof(Digest));
        return canonical with { Digest = expected };
    }

    public IReadOnlyList<(string Dimension, SemanticGuaranteeSupportV1 Support,
        SemanticClaimLevelV1 ClaimLevel, GuaranteeEvidenceSourceV1 EvidenceSource)> DimensionClaims() =>
        new ReadOnlyCollection<(string, SemanticGuaranteeSupportV1, SemanticClaimLevelV1, GuaranteeEvidenceSourceV1)>(
        [
            Claim("execution", ExecutionAdmission), Claim("measurement", Measurement),
            Claim("resource", ResourceEnforcement), Claim("preemption", PreemptionCancellation),
            Claim("isolation", Isolation), Claim("contention", Contention),
            Claim("visibility-publication", VisibilityPublication), Claim("replay", Replay),
            Claim("determinism", Determinism), Claim("containment", Containment),
            Claim("failure", FailureHandling), Claim("locality", Locality),
            Claim("retire-evidence", RetireEvidence),
        ]);

    private static (string, SemanticGuaranteeSupportV1, SemanticClaimLevelV1, GuaranteeEvidenceSourceV1)
        Claim<TClass>(string name, ExecutionGuaranteeDimensionV1<TClass> value) where TClass : struct, Enum =>
        (name, value.Support, value.ClaimLevel, value.EvidenceSource);

    private static void Validate<TClass>(ExecutionGuaranteeDimensionV1<TClass> value)
        where TClass : struct, Enum
    {
        if (value.Version != ExecutionGuaranteeDimensionV1<TClass>.CurrentVersion ||
            !Enum.IsDefined(value.Support) || !Enum.IsDefined(value.ProvidedClass) ||
            !Enum.IsDefined(value.ClaimLevel) || !Enum.IsDefined(value.EvidenceSource))
            throw new NotSupportedException($"Unknown guarantee dimension for {typeof(TClass).Name}.");
        if (value.Support == SemanticGuaranteeSupportV1.Unsupported)
        {
            if (value.ClaimLevel != SemanticClaimLevelV1.ModelOnly || value.EvidenceSource != GuaranteeEvidenceSourceV1.None)
                throw new ArgumentException("Unsupported dimensions must remain ModelOnly with no evidence source.");
        }
        else if (value.EvidenceSource == GuaranteeEvidenceSourceV1.None)
            throw new ArgumentException("Every supported dimension requires an exact evidence/enforcement source.");
    }

    private static string ComputeDigest(ExecutionGuaranteesV1 value)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(value.Version);
            writer.Write(value.ProviderContract.PackageId);
            writer.Write(value.ProviderContract.PackageVersion);
            writer.Write(value.ProviderContract.SchemaIdentity);
            writer.Write(value.ProviderExecutionClass);
            Write(writer, value.ExecutionAdmission); Write(writer, value.Measurement);
            Write(writer, value.ResourceEnforcement); Write(writer, value.PreemptionCancellation);
            Write(writer, value.Isolation); Write(writer, value.Contention);
            Write(writer, value.VisibilityPublication); Write(writer, value.Replay);
            Write(writer, value.Determinism); Write(writer, value.Containment);
            Write(writer, value.FailureHandling); Write(writer, value.Locality);
            Write(writer, value.RetireEvidence);
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void Write<TClass>(BinaryWriter writer, ExecutionGuaranteeDimensionV1<TClass> value)
        where TClass : struct, Enum
    {
        writer.Write(value.Version);
        writer.Write((byte)value.Support);
        writer.Write(Convert.ToUInt64(value.ProvidedClass));
        writer.Write((byte)value.ClaimLevel);
        writer.Write((byte)value.EvidenceSource);
    }
}
