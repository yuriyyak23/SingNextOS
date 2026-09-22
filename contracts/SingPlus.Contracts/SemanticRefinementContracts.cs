namespace SingPlus.Contracts;

public enum SemanticRequirementStrengthV1 : byte
{
    Mandatory = 1,
    Advisory = 2,
}

public enum SemanticGuaranteeSupportV1 : byte
{
    Unsupported = 1,
    Supported = 2,
}

public enum IsolationClassV1 : byte
{
    None = 1,
    DomainSeparated = 2,
    ConfidentialDomain = 3,
}

public enum PublicationEnforcementClassV1 : byte
{
    None = 1,
    VisibilityFence = 2,
    StagedWithholdingUntilDecision = 3,
}

public enum ReplayClassV1 : byte
{
    None = 1,
    Idempotent = 2,
    Deduplicated = 3,
}

public enum DeterminismClassV1 : byte
{
    None = 1,
    StableOrdering = 2,
    BitExact = 3,
}

public enum CancellationClassV1 : byte
{
    None = 1,
    BeforeDispatch = 2,
    ExactAcknowledgement = 3,
}

public enum ContainmentClassV1 : byte
{
    None = 1,
    ExactOperationProviderGenerationClosure = 2,
}

public enum LocalityClassV1 : byte
{
    Any = 1,
    ProviderLocal = 2,
    ConsumerDomainLocal = 3,
}

/// <summary>Immutable semantic requirement. It is not a permission or authority object.</summary>
public readonly record struct SemanticRequirementV1<TClass>(
    ushort Version,
    SemanticRequirementStrengthV1 Strength,
    TClass RequiredClass)
    where TClass : struct, Enum
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
    public bool AuthorizesEffect => false;
}

/// <summary>Immutable provider/runtime claim. Support is explicit and never authorizes OS work.</summary>
public readonly record struct SemanticGuaranteeV1<TClass>(
    ushort Version,
    SemanticGuaranteeSupportV1 Support,
    TClass ProvidedClass)
    where TClass : struct, Enum
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
    public bool AuthorizesEffect => false;
}

public enum SemanticRefinementStatusV1 : byte
{
    Refines = 1,
    AdvisoryUnmet = 2,
    MandatoryUnsupported = 3,
    ClassMismatch = 4,
    InvalidContract = 5,
}

/// <summary>Pure comparison result. Acceptance is compatibility only, never authorization.</summary>
public readonly record struct SemanticRefinementDecisionV1(
    ushort Version,
    SemanticRefinementStatusV1 Status)
{
    public const ushort CurrentVersion = 1;
    public bool IsAccepted => Status is SemanticRefinementStatusV1.Refines or SemanticRefinementStatusV1.AdvisoryUnmet;
    public bool IsSatisfied => Status == SemanticRefinementStatusV1.Refines;
    public bool AuthorizesExecution => false;
    public bool AuthorizesEffect => false;
}

public static class SemanticPartialOrdersV1
{
    public static bool IsolationRefines(IsolationClassV1 provided, IsolationClassV1 required) =>
        Defined(provided, required) && (provided, required) switch
        {
            (_, IsolationClassV1.None) => true,
            (IsolationClassV1.DomainSeparated or IsolationClassV1.ConfidentialDomain, IsolationClassV1.DomainSeparated) => true,
            (IsolationClassV1.ConfidentialDomain, IsolationClassV1.ConfidentialDomain) => true,
            _ => false,
        };

    public static bool PublicationRefines(PublicationEnforcementClassV1 provided, PublicationEnforcementClassV1 required) =>
        Defined(provided, required) && (provided, required) switch
        {
            (_, PublicationEnforcementClassV1.None) => true,
            (PublicationEnforcementClassV1.VisibilityFence or PublicationEnforcementClassV1.StagedWithholdingUntilDecision,
                PublicationEnforcementClassV1.VisibilityFence) => true,
            (PublicationEnforcementClassV1.StagedWithholdingUntilDecision,
                PublicationEnforcementClassV1.StagedWithholdingUntilDecision) => true,
            _ => false,
        };

    public static bool ReplayRefines(ReplayClassV1 provided, ReplayClassV1 required) =>
        Defined(provided, required) && (provided, required) switch
        {
            (_, ReplayClassV1.None) => true,
            (ReplayClassV1.Idempotent, ReplayClassV1.Idempotent) => true,
            (ReplayClassV1.Deduplicated, ReplayClassV1.Deduplicated) => true,
            _ => false,
        };

    public static bool DeterminismRefines(DeterminismClassV1 provided, DeterminismClassV1 required) =>
        Defined(provided, required) && (provided, required) switch
        {
            (_, DeterminismClassV1.None) => true,
            (DeterminismClassV1.StableOrdering or DeterminismClassV1.BitExact, DeterminismClassV1.StableOrdering) => true,
            (DeterminismClassV1.BitExact, DeterminismClassV1.BitExact) => true,
            _ => false,
        };

    public static bool CancellationRefines(CancellationClassV1 provided, CancellationClassV1 required) =>
        Defined(provided, required) && (provided, required) switch
        {
            (_, CancellationClassV1.None) => true,
            (CancellationClassV1.BeforeDispatch or CancellationClassV1.ExactAcknowledgement,
                CancellationClassV1.BeforeDispatch) => true,
            (CancellationClassV1.ExactAcknowledgement, CancellationClassV1.ExactAcknowledgement) => true,
            _ => false,
        };

    public static bool ContainmentRefines(ContainmentClassV1 provided, ContainmentClassV1 required) =>
        Defined(provided, required) && (provided, required) switch
        {
            (_, ContainmentClassV1.None) => true,
            (ContainmentClassV1.ExactOperationProviderGenerationClosure,
                ContainmentClassV1.ExactOperationProviderGenerationClosure) => true,
            _ => false,
        };

    public static bool LocalityRefines(LocalityClassV1 provided, LocalityClassV1 required) =>
        Defined(provided, required) && (provided, required) switch
        {
            (_, LocalityClassV1.Any) => true,
            (LocalityClassV1.ProviderLocal, LocalityClassV1.ProviderLocal) => true,
            (LocalityClassV1.ConsumerDomainLocal, LocalityClassV1.ConsumerDomainLocal) => true,
            _ => false,
        };

    public static bool ResourceAssuranceRefines(ResourceAssuranceV1 provided, ResourceAssuranceV1 required) =>
        Defined(provided, required) && (provided, required) switch
        {
            (_, ResourceAssuranceV1.AccountingOnly) => true,
            (ResourceAssuranceV1.RuntimeEnforced or ResourceAssuranceV1.EnforcedUpperBound or ResourceAssuranceV1.GuaranteedReservation,
                ResourceAssuranceV1.RuntimeEnforced) => true,
            (ResourceAssuranceV1.EnforcedUpperBound or ResourceAssuranceV1.GuaranteedReservation,
                ResourceAssuranceV1.EnforcedUpperBound) => true,
            (ResourceAssuranceV1.GuaranteedReservation, ResourceAssuranceV1.GuaranteedReservation) => true,
            _ => false,
        };

    private static bool Defined<TEnum>(TEnum left, TEnum right) where TEnum : struct, Enum =>
        Enum.IsDefined(left) && Enum.IsDefined(right);
}

public static class SemanticRefinementEvaluatorV1
{
    public static SemanticRefinementDecisionV1 Evaluate<TClass>(
        SemanticRequirementV1<TClass> requirement,
        SemanticGuaranteeV1<TClass> guarantee,
        Func<TClass, TClass, bool> refines)
        where TClass : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(refines);
        if (requirement.Version != SemanticRequirementV1<TClass>.CurrentVersion ||
            guarantee.Version != SemanticGuaranteeV1<TClass>.CurrentVersion ||
            !Enum.IsDefined(requirement.Strength) || !Enum.IsDefined(guarantee.Support) ||
            !Enum.IsDefined(requirement.RequiredClass) || !Enum.IsDefined(guarantee.ProvidedClass))
            return new(SemanticRefinementDecisionV1.CurrentVersion, SemanticRefinementStatusV1.InvalidContract);

        if (guarantee.Support == SemanticGuaranteeSupportV1.Unsupported)
            return new(SemanticRefinementDecisionV1.CurrentVersion,
                requirement.Strength == SemanticRequirementStrengthV1.Mandatory
                    ? SemanticRefinementStatusV1.MandatoryUnsupported
                    : SemanticRefinementStatusV1.AdvisoryUnmet);

        if (refines(guarantee.ProvidedClass, requirement.RequiredClass))
            return new(SemanticRefinementDecisionV1.CurrentVersion, SemanticRefinementStatusV1.Refines);

        return new(SemanticRefinementDecisionV1.CurrentVersion,
            requirement.Strength == SemanticRequirementStrengthV1.Mandatory
                ? SemanticRefinementStatusV1.ClassMismatch
                : SemanticRefinementStatusV1.AdvisoryUnmet);
    }
}
