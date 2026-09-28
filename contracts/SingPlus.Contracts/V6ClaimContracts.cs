namespace SingPlus.Contracts;

public enum V6ClaimLevel : byte
{
    ModelOnly = 1,
    StaticAdmission = 2,
    RuntimeEnforced = 3,
    ExecutableAdapter = 4,
    EnforcedUpperBound = 5,
    GuaranteedReservation = 6,
    ProductionQualified = 7,
    FutureGated = 8,
}

/// <summary>Immutable qualification identity. It records evidence but grants no authority.</summary>
public sealed record V6ClaimEvidenceTuple(
    ushort Version,
    string Contour,
    V6ClaimLevel Claim,
    string SingNextSourceIdentity,
    string ProviderSourceIdentity,
    string ToolchainIdentity,
    string ContractIdentity,
    string ProviderIdentity,
    ulong ProviderGeneration,
    string FeatureGateSetDigest,
    string QualificationArtifactDigest)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
    public bool AuthorizesEffect => false;
    public bool AuthorizesPublication => false;

    public V6ClaimEvidenceTuple Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(Claim) || ProviderGeneration == 0)
            throw new NotSupportedException("Claim tuple version, level, or provider generation is unsupported.");
        string[] tokens = [Contour, SingNextSourceIdentity, ProviderSourceIdentity, ToolchainIdentity,
            ContractIdentity, ProviderIdentity];
        if (tokens.Any(value => string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl)))
            throw new ArgumentException("Claim tuple contains a missing or non-canonical identity.");
        ValidateDigest(FeatureGateSetDigest, nameof(FeatureGateSetDigest));
        ValidateDigest(QualificationArtifactDigest, nameof(QualificationArtifactDigest));
        return this;
    }

    private static void ValidateDigest(string? value, string parameter)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Claim tuple digest must be canonical SHA-256 hex.", parameter);
    }
}
