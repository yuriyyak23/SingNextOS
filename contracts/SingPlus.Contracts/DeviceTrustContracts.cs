namespace SingPlus.Contracts;

public enum DeviceTrustAssuranceV1 : byte
{
    ModelOnly = 1,
    EmulatorAttested = 2,
    HardwareAttested = 3,
}

public enum DeviceTrustDecisionCodeV1 : byte
{
    Satisfied = 1,
    WrongDevice = 2,
    WrongFirmware = 3,
    InsufficientAssurance = 4,
    StaleProviderTrustGeneration = 5,
    StaleDeviceGeneration = 6,
    StaleAssignmentGeneration = 7,
    StaleResetGeneration = 8,
    StalePolicyGeneration = 9,
}

/// <summary>A policy predicate only. It grants no device, Region, execution, or publication authority.</summary>
public readonly record struct TrustObligationV1(
    ushort Version,
    string DeviceIdentity,
    string RequiredFirmwareMeasurementDigest,
    DeviceTrustAssuranceV1 MinimumAssurance,
    ulong ProviderTrustGeneration,
    ulong DeviceGeneration,
    ulong AssignmentGeneration,
    ulong ResetGeneration,
    ulong PolicyGeneration)
{
    public const ushort CurrentVersion = 1;
    public bool GrantsAuthority => false;

    public TrustObligationV1 Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(MinimumAssurance))
            throw new NotSupportedException("Trust obligation version or mandatory assurance class is unsupported.");
        DeviceTrustValidationV1.ValidateIdentity(DeviceIdentity);
        DeviceTrustValidationV1.ValidateDigest(RequiredFirmwareMeasurementDigest);
        DeviceTrustValidationV1.ValidateGenerations(ProviderTrustGeneration, DeviceGeneration,
            AssignmentGeneration, ResetGeneration, PolicyGeneration);
        return this;
    }
}

/// <summary>Provider observation only. Evidence is never a capability, lease, or effect permit.</summary>
public readonly record struct TrustEvidenceV1(
    ushort Version,
    string DeviceIdentity,
    string FirmwareMeasurementDigest,
    DeviceTrustAssuranceV1 Assurance,
    ulong ProviderTrustGeneration,
    ulong DeviceGeneration,
    ulong AssignmentGeneration,
    ulong ResetGeneration,
    ulong PolicyGeneration,
    ulong EvidenceSequence)
{
    public const ushort CurrentVersion = 1;
    public bool GrantsAuthority => false;
    public bool AuthorizesExecution => false;
    public bool AuthorizesRegionAccess => false;

    public TrustEvidenceV1 Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(Assurance))
            throw new NotSupportedException("Trust evidence version or assurance class is unsupported.");
        DeviceTrustValidationV1.ValidateIdentity(DeviceIdentity);
        DeviceTrustValidationV1.ValidateDigest(FirmwareMeasurementDigest);
        DeviceTrustValidationV1.ValidateGenerations(ProviderTrustGeneration, DeviceGeneration,
            AssignmentGeneration, ResetGeneration, PolicyGeneration, EvidenceSequence);
        return this;
    }
}

public readonly record struct DeviceTrustPredicateDecisionV1(
    ushort Version,
    DeviceTrustDecisionCodeV1 Code)
{
    public const ushort CurrentVersion = 1;
    public bool IsSatisfied => Code == DeviceTrustDecisionCodeV1.Satisfied;
    public bool GrantsAuthority => false;
}

public static class DeviceTrustPredicateEvaluatorV1
{
    public static DeviceTrustPredicateDecisionV1 Evaluate(
        TrustObligationV1 obligation,
        TrustEvidenceV1 evidence)
    {
        obligation.Validate();
        evidence.Validate();
        var code = obligation.DeviceIdentity != evidence.DeviceIdentity
            ? DeviceTrustDecisionCodeV1.WrongDevice
            : obligation.RequiredFirmwareMeasurementDigest != evidence.FirmwareMeasurementDigest
                ? DeviceTrustDecisionCodeV1.WrongFirmware
                : evidence.Assurance < obligation.MinimumAssurance
                    ? DeviceTrustDecisionCodeV1.InsufficientAssurance
                    : obligation.ProviderTrustGeneration != evidence.ProviderTrustGeneration
                        ? DeviceTrustDecisionCodeV1.StaleProviderTrustGeneration
                        : obligation.DeviceGeneration != evidence.DeviceGeneration
                            ? DeviceTrustDecisionCodeV1.StaleDeviceGeneration
                            : obligation.AssignmentGeneration != evidence.AssignmentGeneration
                                ? DeviceTrustDecisionCodeV1.StaleAssignmentGeneration
                                : obligation.ResetGeneration != evidence.ResetGeneration
                                    ? DeviceTrustDecisionCodeV1.StaleResetGeneration
                                    : obligation.PolicyGeneration != evidence.PolicyGeneration
                                        ? DeviceTrustDecisionCodeV1.StalePolicyGeneration
                                        : DeviceTrustDecisionCodeV1.Satisfied;
        return new(DeviceTrustPredicateDecisionV1.CurrentVersion, code);
    }
}

internal static class DeviceTrustValidationV1
{
    internal static void ValidateIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("Device trust identity must be canonical and bounded.");
    }

    internal static void ValidateDigest(string? value)
    {
        if (value is null || value.Length != 64 || value.Any(character =>
                !Uri.IsHexDigit(character) || (character >= 'A' && character <= 'F')))
            throw new ArgumentException("Device trust measurement must be lowercase SHA-256 hex.");
    }

    internal static void ValidateGenerations(params ulong[] values)
    {
        if (values.Any(static value => value == 0))
            throw new ArgumentException("Every trust dependency generation must be non-zero.");
    }
}
