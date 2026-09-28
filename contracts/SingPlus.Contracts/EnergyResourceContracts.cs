namespace SingPlus.Contracts;

public enum EnergyEvidenceClaimV1 : byte
{
    MeasurementOnly = 1,
    EnforcedUpperBound = 2,
}

public enum EnergyEvidenceAssuranceV1 : byte
{
    ModelOnly = 1,
    HardwareCounter = 2,
    HardwareEnforced = 3,
}

public enum ThermalStateV1 : byte
{
    Unknown = 1,
    Nominal = 2,
    Throttled = 3,
    Emergency = 4,
}

public enum DvfsStateV1 : byte
{
    Unknown = 1,
    Fixed = 2,
    Dynamic = 3,
}

/// <summary>Provider measurement/enforcement evidence. It is not budget or execution authority.</summary>
public readonly record struct ProviderEnergyEvidenceV1(
    ushort Version,
    string ProviderIdentity,
    string OperationCorrelation,
    EnergyEvidenceClaimV1 Claim,
    EnergyEvidenceAssuranceV1 Assurance,
    ulong ProviderGeneration,
    ulong CounterGeneration,
    ulong EvidenceSequence,
    ulong CounterStart,
    ulong CounterEnd,
    ulong MeasuredMicrojoules,
    ulong EnforcedUpperBoundMicrojoules,
    uint AttributionErrorPartsPerMillion,
    uint ConfidencePartsPerMillion,
    ThermalStateV1 ThermalState,
    DvfsStateV1 DvfsState,
    string EnforcementMechanism)
{
    public const ushort CurrentVersion = 1;
    public bool GrantsBudgetAuthority => false;
    public bool AuthorizesExecution => false;
    public bool GuaranteesDeadline => false;
    public bool GuaranteesPerformance => false;

    public ProviderEnergyEvidenceV1 Validate()
    {
        if (Version != CurrentVersion || !Enum.IsDefined(Claim) || !Enum.IsDefined(Assurance) ||
            !Enum.IsDefined(ThermalState) || !Enum.IsDefined(DvfsState))
            throw new NotSupportedException("Energy evidence version or vocabulary is unsupported.");
        ValidateIdentity(ProviderIdentity, nameof(ProviderIdentity));
        ValidateIdentity(OperationCorrelation, nameof(OperationCorrelation));
        if (ProviderGeneration == 0 || CounterGeneration == 0 || EvidenceSequence == 0)
            throw new ArgumentException("Energy evidence generations must be non-zero.");
        if (CounterEnd < CounterStart || MeasuredMicrojoules != CounterEnd - CounterStart)
            throw new ArgumentException("Energy measurement must match a non-wrapping counter interval.");
        if (AttributionErrorPartsPerMillion > 1_000_000 || ConfidencePartsPerMillion > 1_000_000)
            throw new ArgumentException("Energy attribution error and confidence must be bounded parts-per-million values.");
        if (Claim == EnergyEvidenceClaimV1.MeasurementOnly)
        {
            if (EnforcedUpperBoundMicrojoules != 0 || !string.IsNullOrEmpty(EnforcementMechanism))
                throw new ArgumentException("Measurement-only evidence cannot claim an enforced cap.");
        }
        else
        {
            ValidateIdentity(EnforcementMechanism, nameof(EnforcementMechanism));
            if (EnforcedUpperBoundMicrojoules == 0 ||
                MeasuredMicrojoules > EnforcedUpperBoundMicrojoules)
                throw new ArgumentException("Enforced energy evidence must name and satisfy a non-zero upper bound.");
        }
        return this;
    }

    private static void ValidateIdentity(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() ||
            value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("Energy evidence identity must be canonical and bounded.", parameter);
    }
}

public readonly record struct EnergyExecutionBindingV1(
    ushort Version,
    BudgetReservationHandle Reservation,
    string ProviderIdentity,
    string OperationCorrelation,
    ulong MaximumEnergyMicrojoules,
    ulong ProviderGeneration,
    ulong CounterGeneration)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
    public bool AuthorizesBudgetSettlement => false;

    public EnergyExecutionBindingV1 Validate()
    {
        if (Version != CurrentVersion)
            throw new NotSupportedException("Energy execution binding version is unsupported.");
        if (string.IsNullOrWhiteSpace(ProviderIdentity) || ProviderIdentity != ProviderIdentity.Trim() ||
            ProviderIdentity.Length > 256 || ProviderIdentity.Any(char.IsControl))
            throw new ArgumentException("Energy provider identity is not canonical.");
        if (Reservation.ReservationId.Value == 0 || Reservation.Generation.Value == 0 ||
            MaximumEnergyMicrojoules == 0 || ProviderGeneration == 0 || CounterGeneration == 0 ||
            string.IsNullOrWhiteSpace(OperationCorrelation) || OperationCorrelation != OperationCorrelation.Trim())
            throw new ArgumentException("Energy execution binding is incomplete.");
        return this;
    }
}

public enum EnergyEvidenceMatchCodeV1 : byte
{
    Exact = 1,
    MeasurementNotEnforcement = 2,
    WrongProvider = 3,
    WrongOperation = 4,
    StaleProviderGeneration = 5,
    StaleCounterGeneration = 6,
    ExceedsReservedEnvelope = 7,
}

public static class EnergyEvidenceMatcherV1
{
    public static EnergyEvidenceMatchCodeV1 Match(
        EnergyExecutionBindingV1 binding,
        ProviderEnergyEvidenceV1 evidence)
    {
        binding.Validate();
        evidence.Validate();
        if (evidence.Claim != EnergyEvidenceClaimV1.EnforcedUpperBound)
            return EnergyEvidenceMatchCodeV1.MeasurementNotEnforcement;
        if (evidence.ProviderIdentity != binding.ProviderIdentity)
            return EnergyEvidenceMatchCodeV1.WrongProvider;
        if (evidence.OperationCorrelation != binding.OperationCorrelation)
            return EnergyEvidenceMatchCodeV1.WrongOperation;
        if (evidence.ProviderGeneration != binding.ProviderGeneration)
            return EnergyEvidenceMatchCodeV1.StaleProviderGeneration;
        if (evidence.CounterGeneration != binding.CounterGeneration)
            return EnergyEvidenceMatchCodeV1.StaleCounterGeneration;
        return evidence.EnforcedUpperBoundMicrojoules > binding.MaximumEnergyMicrojoules ||
               evidence.MeasuredMicrojoules > binding.MaximumEnergyMicrojoules
            ? EnergyEvidenceMatchCodeV1.ExceedsReservedEnvelope
            : EnergyEvidenceMatchCodeV1.Exact;
    }
}
