using System.Collections.ObjectModel;

namespace SingPlus.Contracts;

public enum QualificationVerticalStepV1 : byte
{
    AppOrSipIntent = 1,
    CapabilitySessionRegionAdmission = 2,
    BudgetReserved = 3,
    ObligationsConstructed = 4,
    PlannerSelected = 5,
    ProviderAdmitted = 6,
    GuaranteesConstructed = 7,
    RefinementAccepted = 8,
    SemanticBindingCreated = 9,
    RuntimeLegalityAccepted = 10,
    Submitted = 11,
    RetiredOrDeviceComplete = 12,
    Visible = 13,
    Published = 14,
    BudgetSettled = 15,
    RegionReleasedOrReclaimed = 16,
}

public enum QualificationFailureStateV1 : byte
{
    None = 1,
    CompensatedBeforeEffect = 2,
    ContainedAfterPossibleEffect = 3,
    Quarantined = 4,
}

/// <summary>Audit evidence for one owner transition. It is never authority or permission.</summary>
public readonly record struct QualificationTransitionEvidenceV1(
    ushort Version,
    ulong Sequence,
    QualificationVerticalStepV1 Step,
    string Owner,
    string InputEvidenceDigest,
    string GenerationVectorDigest,
    QualificationFailureStateV1 FailureState,
    string LinearizationPoint,
    string RollbackOrCompensationRule)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
    public bool AuthorizesPublication => false;

    public QualificationTransitionEvidenceV1 Validate()
    {
        if (Version != CurrentVersion || Sequence == 0 || !Enum.IsDefined(Step) || !Enum.IsDefined(FailureState))
            throw new NotSupportedException("Qualification transition version or vocabulary is unsupported.");
        QualificationVerticalValidationV1.Token(Owner, nameof(Owner));
        QualificationVerticalValidationV1.Digest(InputEvidenceDigest, nameof(InputEvidenceDigest));
        QualificationVerticalValidationV1.Digest(GenerationVectorDigest, nameof(GenerationVectorDigest));
        QualificationVerticalValidationV1.Token(LinearizationPoint, nameof(LinearizationPoint));
        QualificationVerticalValidationV1.Token(RollbackOrCompensationRule, nameof(RollbackOrCompensationRule));
        return this;
    }
}

public sealed record FirstQualificationVerticalEvidenceV1
{
    private readonly ReadOnlyCollection<QualificationTransitionEvidenceV1> _transitions;

    private FirstQualificationVerticalEvidenceV1(QualificationTransitionEvidenceV1[] transitions) =>
        _transitions = Array.AsReadOnly(transitions);

    public ushort Version => 1;
    public IReadOnlyList<QualificationTransitionEvidenceV1> Transitions => _transitions;
    public bool AuthorizesExecution => false;
    public bool ReplacesOwnerState => false;

    public static FirstQualificationVerticalEvidenceV1 Create(
        IEnumerable<QualificationTransitionEvidenceV1> transitions)
    {
        var exact = (transitions ?? throw new ArgumentNullException(nameof(transitions)))
            .Select(static transition => transition.Validate()).ToArray();
        var required = Enum.GetValues<QualificationVerticalStepV1>();
        if (!exact.Select(static transition => transition.Step).SequenceEqual(required) ||
            !exact.Select(static transition => transition.Sequence)
                .SequenceEqual(Enumerable.Range(1, required.Length).Select(static value => (ulong)value)))
            throw new ArgumentException("Qualification transitions must contain the exact ordered vertical once.");
        return new(exact.ToArray());
    }
}

internal static class QualificationVerticalValidationV1
{
    internal static void Token(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("Qualification transition text must be canonical and bounded.", parameter);
    }

    internal static void Digest(string? value, string parameter)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Qualification transition digest must be canonical SHA-256 hex.", parameter);
    }
}
