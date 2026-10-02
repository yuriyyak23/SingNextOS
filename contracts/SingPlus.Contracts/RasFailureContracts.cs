namespace SingPlus.Contracts;

public enum ProviderHealthStateV1 : byte
{
    Healthy = 1,
    Degraded = 2,
    Failed = 3,
    Reconfigured = 4,
}

public enum FailureConsequenceKindV1 : byte
{
    None = 1,
    RegionSubrangeQuarantine = 2,
    InFlightOperationAmbiguity = 3,
    RebindRequired = 4,
}

public readonly record struct FailureDomainScopeV1(
    string ProviderId,
    string FailureDomainId,
    ulong ProviderGeneration,
    ulong FailureDomainGeneration)
{
    public FailureDomainScopeV1 Validate()
    {
        if (string.IsNullOrWhiteSpace(ProviderId) || string.IsNullOrWhiteSpace(FailureDomainId) ||
            ProviderId.Length > 128 || FailureDomainId.Length > 128)
            throw new ArgumentException("Failure-domain identifiers must be non-empty and bounded.");
        if (ProviderId != ProviderId.Trim() || FailureDomainId != FailureDomainId.Trim() ||
            ProviderId.Any(char.IsControl) || FailureDomainId.Any(char.IsControl))
            throw new ArgumentException("Failure-domain identifiers must be canonical tokens.");
        try
        {
            var utf8 = new System.Text.UTF8Encoding(false, true);
            _ = utf8.GetByteCount(ProviderId);
            _ = utf8.GetByteCount(FailureDomainId);
        }
        catch (System.Text.EncoderFallbackException exception)
        {
            throw new ArgumentException("Failure-domain identifiers must contain valid Unicode scalar values.", exception);
        }
        if (ProviderGeneration == 0 || FailureDomainGeneration == 0)
            throw new ArgumentException("Failure-domain generations must be non-zero.");
        return this;
    }
}

/// <summary>
/// Provider-owned observation. It is evidence only: it cannot mutate RegionAuthority,
/// close an external operation, reclaim resources, or grant execution.
/// </summary>
public readonly record struct ProviderHealthEvidenceV1(
    ushort Version,
    FailureDomainScopeV1 Scope,
    ulong ObservationSequence,
    ProviderHealthStateV1 Health,
    ProviderFaultClassV1 FaultClass,
    RegionUseRange? AffectedRange)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesRegionMutation => false;
    public bool ProvesExternalOperationClosure => false;
    public bool AuthorizesReclaim => false;

    public ProviderHealthEvidenceV1 Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(Health) || !Enum.IsDefined(FaultClass))
            throw new NotSupportedException("Provider-health evidence version or class is unsupported.");
        Scope.Validate();
        if (ObservationSequence == 0)
            throw new ArgumentException("Provider-health observation sequence must be non-zero.");
        if (AffectedRange is { } range && (range.Offset < 0 || range.Length <= 0))
            throw new ArgumentException("Provider-health affected range is invalid.");
        if (Health == ProviderHealthStateV1.Healthy && AffectedRange is not null)
            throw new ArgumentException("Healthy evidence cannot identify a damaged range.");
        if (Health == ProviderHealthStateV1.Reconfigured && FaultClass != ProviderFaultClassV1.Recovery)
            throw new ArgumentException("Reconfiguration evidence must use the recovery fault class.");
        return this;
    }
}

/// <summary>Non-authoritative policy projection consumed by the existing consequence owners.</summary>
public readonly record struct FailureConsequenceV1(
    ushort Version,
    FailureDomainScopeV1 Scope,
    ulong EvidenceObservationSequence,
    FailureConsequenceKindV1 Kind,
    RegionUseRange? AffectedRange)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesRegionMutation => false;
    public bool ProvesClosure => false;
    public bool AuthorizesReclaim => false;

    public FailureConsequenceV1 Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(Kind))
            throw new NotSupportedException("Failure consequence version or kind is unsupported.");
        Scope.Validate();
        if (EvidenceObservationSequence == 0)
            throw new ArgumentException("Failure consequence must correlate a non-zero evidence sequence.");
        if ((Kind == FailureConsequenceKindV1.RegionSubrangeQuarantine) != (AffectedRange is not null))
            throw new ArgumentException("Only a Region subrange quarantine carries an affected range.");
        if (AffectedRange is { } range && (range.Offset < 0 || range.Length <= 0))
            throw new ArgumentException("Failure consequence range is invalid.");
        return this;
    }
}

public static class FailureConsequencePolicyV1
{
    public static FailureConsequenceV1 Project(ProviderHealthEvidenceV1 evidence)
    {
        var exact = evidence.Validate();
        var kind = exact.Health switch
        {
            ProviderHealthStateV1.Healthy => FailureConsequenceKindV1.None,
            ProviderHealthStateV1.Reconfigured => FailureConsequenceKindV1.RebindRequired,
            _ when exact.AffectedRange is not null => FailureConsequenceKindV1.RegionSubrangeQuarantine,
            _ => FailureConsequenceKindV1.InFlightOperationAmbiguity,
        };
        return new FailureConsequenceV1(FailureConsequenceV1.CurrentVersion, exact.Scope,
            exact.ObservationSequence, kind, exact.AffectedRange).Validate();
    }
}

public readonly record struct RegionDamageHandle(ulong DamageId, ulong Generation);

public enum RegionDamageStateV1 : byte
{
    Quarantined = 1,
}

public sealed record RegionDamageDescriptorV1(
    RegionDamageHandle Handle,
    RegionHandle Region,
    FailureDomainScopeV1 FailureDomain,
    ulong EvidenceObservationSequence,
    RegionUseRange Range,
    RegionDamageStateV1 State);

/// <summary>
/// Provider/backing replacement evidence consumed by RegionAuthority. It correlates closure and
/// replacement but cannot clear damage, mutate a Region, or authorize reclaim by itself.
/// </summary>
public readonly record struct RegionBackingReplacementEvidenceV1(
    ushort Version,
    RegionDamageHandle Damage,
    FailureDomainScopeV1 NextFailureDomain,
    ulong EvidenceObservationSequence,
    ulong ReplacementBackingGeneration,
    RegionUseRange Range,
    string PriorEffectClosureDigest,
    string ReplacementBackingDigest)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesRegionMutation => false;
    public bool ProvesEffectClosure => false;
    public bool AuthorizesReclaim => false;

    public RegionBackingReplacementEvidenceV1 Validate()
    {
        if (Version != CurrentVersion)
            throw new NotSupportedException("Region backing replacement evidence version is unsupported.");
        if (Damage.DamageId == 0 || Damage.Generation == 0 || EvidenceObservationSequence == 0 ||
            ReplacementBackingGeneration == 0 || Range.Offset < 0 || Range.Length <= 0)
            throw new ArgumentException("Region backing replacement identities, generations, and range must be materialized.");
        NextFailureDomain.Validate();
        ValidateDigest(PriorEffectClosureDigest, nameof(PriorEffectClosureDigest));
        ValidateDigest(ReplacementBackingDigest, nameof(ReplacementBackingDigest));
        return this;
    }

    private static void ValidateDigest(string? value, string parameter)
    {
        if (value is null || value.Length != 64 ||
            value.Any(character => !Uri.IsHexDigit(character) || character is >= 'A' and <= 'F'))
            throw new ArgumentException("Replacement correlation must be lowercase SHA-256 hex.", parameter);
    }
}

public readonly record struct RegionBackingReplacementReceiptV1(
    ushort Version,
    RegionDamageHandle ReplacedDamage,
    RegionHandle Region,
    MutationEpoch RegionMutationEpoch,
    FailureDomainScopeV1 FailureDomain,
    ulong EvidenceObservationSequence,
    ulong ReplacementBackingGeneration,
    RegionUseRange Range)
{
    public const ushort CurrentVersion = 1;
    public bool GrantsRegionAuthority => false;
    public bool AuthorizesReclaim => false;
}
