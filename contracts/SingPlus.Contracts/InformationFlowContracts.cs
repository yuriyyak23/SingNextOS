namespace SingPlus.Contracts;

public enum ConfidentialityClassV1 : byte { Public = 1, Protected = 2, Restricted = 3 }
public enum IntegrityClassV1 : byte { Untrusted = 1, Validated = 2, Trusted = 3 }

/// <summary>A non-authoritative data classification used only by enabled protected contours.</summary>
public readonly record struct DataLabelV1(
    ushort Version,
    ConfidentialityClassV1 Confidentiality,
    IntegrityClassV1 Integrity)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesRead => false;
    public bool AuthorizesWrite => false;
    public bool AuthorizesDeclassification => false;

    public DataLabelV1 Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(Confidentiality) || !Enum.IsDefined(Integrity))
            throw new NotSupportedException("Data label version or lattice element is unsupported.");
        return this;
    }
}

public readonly record struct FlowPolicyV1(ushort Version, DataLabelV1 MaximumSinkLabel)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesAccess => false;
    public FlowPolicyV1 Validate()
    {
        if (Version != CurrentVersion) throw new NotSupportedException("Flow policy version is unsupported.");
        MaximumSinkLabel.Validate();
        return this;
    }
}

public static class DataLabelLatticeV1
{
    public static DataLabelV1 Join(DataLabelV1 left, DataLabelV1 right)
    {
        left.Validate(); right.Validate();
        return new(1,
            (ConfidentialityClassV1)Math.Max((byte)left.Confidentiality, (byte)right.Confidentiality),
            (IntegrityClassV1)Math.Min((byte)left.Integrity, (byte)right.Integrity));
    }

    public static bool CanFlowTo(DataLabelV1 source, FlowPolicyV1 sink)
    {
        try { source.Validate(); sink.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return false; }
        return source.Confidentiality <= sink.MaximumSinkLabel.Confidentiality &&
               source.Integrity >= sink.MaximumSinkLabel.Integrity;
    }
}

public enum InformationFlowTransitionKindV1 : byte { Declassify = 1, Endorse = 2 }

public readonly record struct InformationFlowTransitionV1(
    ushort Version,
    InformationFlowTransitionKindV1 Kind,
    DataLabelV1 Source,
    DataLabelV1 Target)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesTransition => false;

    public InformationFlowTransitionV1 Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(Kind))
            throw new NotSupportedException("Information-flow transition version or kind is unsupported.");
        Source.Validate(); Target.Validate();
        var valid = Kind switch
        {
            InformationFlowTransitionKindV1.Declassify =>
                Target.Confidentiality < Source.Confidentiality && Target.Integrity == Source.Integrity,
            InformationFlowTransitionKindV1.Endorse =>
                Target.Integrity > Source.Integrity && Target.Confidentiality == Source.Confidentiality,
            _ => false,
        };
        if (!valid) throw new ArgumentException("Transition must change exactly one label dimension in its authorized direction.");
        return this;
    }
}

public sealed record ProtectedRegionLabelDescriptorV1(
    RegionHandle Region,
    DataLabelV1 Label,
    ulong LabelGeneration)
{
    public bool AuthorizesRegionAccess => false;
}

/// <summary>
/// Non-authoritative label sidecar for one named value in a closed Sip/SipJob plan.
/// The plan and schema digests prevent a valid label from being replayed onto a
/// different generated value edge.
/// </summary>
public sealed record ProtectedValueLabelSidecarV1(
    ushort Version,
    string PlanDigest,
    string ValueId,
    string ValueSchemaDigest,
    DataLabelV1 Label,
    ulong LabelGeneration)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesAccess => false;

    public ProtectedValueLabelSidecarV1 Validate()
    {
        if (Version != CurrentVersion)
            throw new NotSupportedException("Protected value label sidecar version is unsupported.");
        if (!CanonicalIdentity(ValueId) || !UpperHexDigest(PlanDigest) || !LowerHexDigest(ValueSchemaDigest) || LabelGeneration == 0)
            throw new ArgumentException("Protected value label sidecar identity, digest, and generation must be canonical.");
        Label.Validate();
        return this;
    }

    private static bool CanonicalIdentity(string value) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= 256;

    private static bool LowerHexDigest(string value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool UpperHexDigest(string value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');
}

public enum ProtectedAcceleratorValueKindV1 : byte { Input = 1, Intermediate = 2, Output = 3 }

/// <summary>
/// Provider-neutral label sideband. It describes a value observed during one
/// accelerator operation and never grants execution, memory, or region access.
/// </summary>
public sealed record ProtectedAcceleratorLabelSidebandV1(
    ushort Version,
    string OperationId,
    string ValueId,
    ProtectedAcceleratorValueKindV1 ValueKind,
    uint Sequence,
    DataLabelV1 Label,
    ulong LabelGeneration,
    ulong ProviderGeneration,
    ulong OperationGeneration)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesAccess => false;
    public bool AuthorizesExecution => false;

    public ProtectedAcceleratorLabelSidebandV1 Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(ValueKind))
            throw new NotSupportedException("Protected accelerator label sideband version or value kind is unsupported.");
        if (!CanonicalIdentity(OperationId) || !CanonicalIdentity(ValueId) || Sequence == 0 ||
            LabelGeneration == 0 || ProviderGeneration == 0 || OperationGeneration == 0)
            throw new ArgumentException("Protected accelerator label sideband identity, sequence, and generations must be canonical.");
        Label.Validate();
        return this;
    }

    private static bool CanonicalIdentity(string value) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= 256;
}
