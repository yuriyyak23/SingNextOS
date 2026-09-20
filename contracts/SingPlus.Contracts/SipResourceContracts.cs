namespace SingPlus.Contracts;

public enum SipResourceDonationPolicyV1
{
    None = 0,
    AcceptNarrowed = 1,
}

public readonly record struct SipResourceRequirementV1
{
    public const int CurrentVersion = 1;

    public SipResourceRequirementV1(
        int version,
        ResourceClassV1 resourceClass,
        ResourceUnitV1 unit,
        ulong maximumAmount,
        string semanticScope,
        ResourceAssuranceV1 assuranceCeiling,
        SipResourceDonationPolicyV1 donationPolicy)
    {
        if (version != CurrentVersion)
            throw new ArgumentOutOfRangeException(nameof(version), version, "Unsupported SIP resource requirement version.");
        if (!Enum.IsDefined(resourceClass) || !Enum.IsDefined(unit) ||
            !Enum.IsDefined(assuranceCeiling) || !Enum.IsDefined(donationPolicy))
            throw new ArgumentException("SIP resource requirement contains an unknown enum value.");
        if (maximumAmount == 0 || maximumAmount == ulong.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumAmount), maximumAmount,
                "SIP resource maximum must be finite and non-zero.");
        if (string.IsNullOrWhiteSpace(semanticScope))
            throw new ArgumentException("SIP resource semantic scope is required.", nameof(semanticScope));
        if (resourceClass != ResourceClassV1.ComputeTime || unit != ResourceUnitV1.Nanoseconds)
            throw new NotSupportedException("Only the P01 compute-time/nanoseconds resource family is supported.");

        Version = version;
        ResourceClass = resourceClass;
        Unit = unit;
        MaximumAmount = maximumAmount;
        SemanticScope = semanticScope;
        AssuranceCeiling = assuranceCeiling;
        DonationPolicy = donationPolicy;
    }

    public int Version { get; }
    public ResourceClassV1 ResourceClass { get; }
    public ResourceUnitV1 Unit { get; }
    public ulong MaximumAmount { get; }
    public string SemanticScope { get; }
    public ResourceAssuranceV1 AssuranceCeiling { get; }
    public SipResourceDonationPolicyV1 DonationPolicy { get; }

    public string CanonicalIdentity =>
        $"{Version}:{(int)ResourceClass}:{(int)Unit}:{MaximumAmount}:{SemanticScope}:{(int)AssuranceCeiling}:{(int)DonationPolicy}";
}
