using System.Buffers.Binary;

namespace SingPlus.Contracts;

public enum FailureContainmentSemanticsV1 : byte
{
    None = 1,
    QuarantineAmbiguousEffect = 2,
    ExactSubrangeQuarantine = 3,
}

public enum FailureRecoverySemanticsV1 : byte
{
    RebindRequired = 1,
    FreshAdmissionRequired = 2,
}

/// <summary>Static failure requirement/guarantee. It is not health, closure, or reclaim evidence.</summary>
public readonly record struct FailureSemanticsV1(
    ushort Version,
    FailureContainmentSemanticsV1 Containment,
    FailureRecoverySemanticsV1 Recovery,
    bool ExplicitEffectClosureRequired)
{
    public const ushort CurrentVersion = 1;
    public const int CanonicalSize = 5;
    public static SemanticExtensionClassId ExtensionClassId => new("failure.semantics");
    public bool AuthorizesRegionMutation => false;
    public bool ProvesEffectClosure => false;
    public bool AuthorizesReclaim => false;

    public FailureSemanticsV1 Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(Containment) || !Enum.IsDefined(Recovery))
            throw new NotSupportedException("Failure semantics version or class is unsupported.");
        if (Containment != FailureContainmentSemanticsV1.None && !ExplicitEffectClosureRequired)
            throw new ArgumentException("Failure containment requires explicit effect closure before reclaim.");
        return this;
    }

    public byte[] SerializeCanonical()
    {
        var exact = Validate();
        var bytes = new byte[CanonicalSize];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, exact.Version);
        bytes[2] = (byte)exact.Containment;
        bytes[3] = (byte)exact.Recovery;
        bytes[4] = exact.ExplicitEffectClosureRequired ? (byte)1 : (byte)0;
        return bytes;
    }

    public static FailureSemanticsV1 ParseCanonical(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != CanonicalSize || bytes[4] > 1)
            throw new FormatException("Failure semantics payload is not canonical.");
        return new FailureSemanticsV1(BinaryPrimitives.ReadUInt16BigEndian(bytes),
            (FailureContainmentSemanticsV1)bytes[2], (FailureRecoverySemanticsV1)bytes[3],
            bytes[4] == 1).Validate();
    }

    public SemanticExtensionClauseV1 ToClause(SemanticExtensionRequirement requirement) =>
        SemanticExtensionClauseV1.Create(ExtensionClassId, "singnext.failure-semantics/1",
            CurrentVersion, requirement, SerializeCanonical());
}

public static class FailureSemanticPartialOrderV1
{
    public static bool Refines(FailureSemanticsV1 provided, FailureSemanticsV1 required)
    {
        try { provided.Validate(); required.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return false; }
        return ContainmentRefines(provided.Containment, required.Containment) &&
               RecoveryRefines(provided.Recovery, required.Recovery) &&
               (!required.ExplicitEffectClosureRequired || provided.ExplicitEffectClosureRequired);
    }

    private static bool ContainmentRefines(
        FailureContainmentSemanticsV1 provided, FailureContainmentSemanticsV1 required) =>
        required == FailureContainmentSemanticsV1.None || provided == required ||
        provided == FailureContainmentSemanticsV1.ExactSubrangeQuarantine &&
        required == FailureContainmentSemanticsV1.QuarantineAmbiguousEffect;

    private static bool RecoveryRefines(
        FailureRecoverySemanticsV1 provided, FailureRecoverySemanticsV1 required) =>
        provided == required || provided == FailureRecoverySemanticsV1.FreshAdmissionRequired &&
        required == FailureRecoverySemanticsV1.RebindRequired;
}

public enum DurablePublicationSemanticsV1 : byte
{
    VolatileStaged = 1,
    DurableBeforePublication = 2,
}

public enum RecoveryIntegritySemanticsV1 : byte
{
    None = 1,
    DigestValidated = 2,
    GenerationAndAntiRollbackValidated = 3,
}

public enum DurabilityDomainRequirementV1 : byte
{
    AnyNamedDomain = 1,
    ManagedModel = 2,
    Adr = 3,
    Eadr = 4,
    CxlPersistentMemory = 5,
}

public enum DurabilityAssuranceSemanticsV1 : byte
{
    ModelOnly = 1,
    HardwareQualified = 2,
}

/// <summary>Static durability requirement/guarantee. Provider/media generations remain live side evidence.</summary>
public readonly record struct DurabilitySemanticsV1(
    ushort Version,
    DurablePublicationSemanticsV1 Publication,
    RecoveryIntegritySemanticsV1 RecoveryIntegrity,
    DurabilityDomainRequirementV1 Domain,
    DurabilityAssuranceSemanticsV1 Assurance)
{
    public const ushort CurrentVersion = 1;
    public const int CanonicalSize = 6;
    public static SemanticExtensionClassId ExtensionClassId => new("durability.semantics");
    public bool AuthorizesPublication => false;
    public bool ProvesPhysicalPersistence => false;
    public bool RestoresAuthority => false;

    public DurabilitySemanticsV1 Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(Publication) ||
            !Enum.IsDefined(RecoveryIntegrity) || !Enum.IsDefined(Domain) || !Enum.IsDefined(Assurance))
            throw new NotSupportedException("Durability semantics version or class is unsupported.");
        if ((Domain is DurabilityDomainRequirementV1.Adr or DurabilityDomainRequirementV1.Eadr or
            DurabilityDomainRequirementV1.CxlPersistentMemory) &&
            Assurance != DurabilityAssuranceSemanticsV1.HardwareQualified)
            throw new ArgumentException("A physical persistence-domain claim requires hardware-qualified assurance.");
        if (Publication == DurablePublicationSemanticsV1.DurableBeforePublication &&
            RecoveryIntegrity == RecoveryIntegritySemanticsV1.None)
            throw new ArgumentException("Durable publication requires recovery integrity semantics.");
        return this;
    }

    public byte[] SerializeCanonical()
    {
        var exact = Validate();
        var bytes = new byte[CanonicalSize];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, exact.Version);
        bytes[2] = (byte)exact.Publication;
        bytes[3] = (byte)exact.RecoveryIntegrity;
        bytes[4] = (byte)exact.Domain;
        bytes[5] = (byte)exact.Assurance;
        return bytes;
    }

    public static DurabilitySemanticsV1 ParseCanonical(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != CanonicalSize)
            throw new FormatException("Durability semantics payload is not canonical.");
        return new DurabilitySemanticsV1(BinaryPrimitives.ReadUInt16BigEndian(bytes),
            (DurablePublicationSemanticsV1)bytes[2], (RecoveryIntegritySemanticsV1)bytes[3],
            (DurabilityDomainRequirementV1)bytes[4],
            (DurabilityAssuranceSemanticsV1)bytes[5]).Validate();
    }

    public SemanticExtensionClauseV1 ToClause(SemanticExtensionRequirement requirement) =>
        SemanticExtensionClauseV1.Create(ExtensionClassId, "singnext.durability-semantics/1",
            CurrentVersion, requirement, SerializeCanonical());
}

public static class DurabilitySemanticPartialOrderV1
{
    public static bool Refines(DurabilitySemanticsV1 provided, DurabilitySemanticsV1 required)
    {
        try { provided.Validate(); required.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return false; }
        return PublicationRefines(provided.Publication, required.Publication) &&
               Convert.ToByte(provided.RecoveryIntegrity) >= Convert.ToByte(required.RecoveryIntegrity) &&
               DomainRefines(provided.Domain, required.Domain) &&
               Convert.ToByte(provided.Assurance) >= Convert.ToByte(required.Assurance);
    }

    private static bool PublicationRefines(
        DurablePublicationSemanticsV1 provided, DurablePublicationSemanticsV1 required) =>
        provided == required || provided == DurablePublicationSemanticsV1.DurableBeforePublication &&
        required == DurablePublicationSemanticsV1.VolatileStaged;

    private static bool DomainRefines(
        DurabilityDomainRequirementV1 provided, DurabilityDomainRequirementV1 required) =>
        required == DurabilityDomainRequirementV1.AnyNamedDomain || provided == required;
}

public static class FailureDurabilityExtensionRefinementV1
{
    private static readonly IReadOnlySet<SemanticExtensionClassId> Supported =
        new HashSet<SemanticExtensionClassId>
        { FailureSemanticsV1.ExtensionClassId, DurabilitySemanticsV1.ExtensionClassId };

    public static SemanticExtensionMatchDecision Evaluate(
        OperationSemanticExtensionsV1 requirements,
        ExecutionGuaranteeExtensionsV1 guarantees) =>
        SemanticExtensionRefinementV1.Evaluate(requirements, guarantees, Supported, Refines,
            static optional => optional.Requirement == SemanticExtensionRequirement.Optional);

    private static bool Refines(SemanticExtensionClauseV1 provided, SemanticExtensionClauseV1 required)
    {
        if (provided.ClassId != required.ClassId)
            return false;
        try
        {
            if (provided.ClassId == FailureSemanticsV1.ExtensionClassId)
                return FailureSemanticPartialOrderV1.Refines(
                    FailureSemanticsV1.ParseCanonical(provided.Payload.Span),
                    FailureSemanticsV1.ParseCanonical(required.Payload.Span));
            if (provided.ClassId == DurabilitySemanticsV1.ExtensionClassId)
                return DurabilitySemanticPartialOrderV1.Refines(
                    DurabilitySemanticsV1.ParseCanonical(provided.Payload.Span),
                    DurabilitySemanticsV1.ParseCanonical(required.Payload.Span));
            return false;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or NotSupportedException)
        {
            return false;
        }
    }
}
