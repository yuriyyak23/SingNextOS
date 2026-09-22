namespace SingPlus.Contracts;

public static class ExternalOperationContract
{
    public const uint Version = 1;
}

public readonly record struct ExternalOperationId(ulong Value);
public readonly record struct OperationGeneration(ulong Value);
public readonly record struct ExternalOperationHandle(
    ExternalOperationId OperationId,
    OperationGeneration Generation);
public readonly record struct OperationBindingId(ulong Value);
public readonly record struct OperationBinding(
    ExternalOperationHandle Operation,
    OperationBindingId BindingId,
    ulong Generation);

public readonly record struct ExternalServiceIdentity(string Value);

public enum ExternalCancellationSupport
{
    BeforeSubmissionOnly = 0,
    ProviderCooperative = 1
}

/// <summary>Non-authoritative requirement mapping onto the existing external-operation lifecycle.</summary>
public readonly record struct ExternalCancellationSemanticsV1(
    ushort Version,
    CancellationClassV1 CancellationClass,
    ExternalCancellationSupport RequiredAdmissionSupport,
    bool RequiresExactProviderAcknowledgement)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesCancellation => false;
    public bool ProvesClosure => false;

    public ExternalCancellationSemanticsV1 Canonicalize()
    {
        if (Version != CurrentVersion) throw new NotSupportedException($"Cancellation semantics version {Version} is unsupported.");
        if (!Enum.IsDefined(CancellationClass) || !Enum.IsDefined(RequiredAdmissionSupport))
            throw new ArgumentOutOfRangeException(nameof(CancellationClass));
        var expected = ExternalCancellationSemantics.ForClass(CancellationClass);
        if (this != expected) throw new ArgumentException("Cancellation semantics are noncanonical.");
        return this;
    }
}

public static class ExternalCancellationSemantics
{
    public static ExternalCancellationSemanticsV1 ForClass(CancellationClassV1 value) => value switch
    {
        CancellationClassV1.None => new(1, value, ExternalCancellationSupport.BeforeSubmissionOnly, false),
        CancellationClassV1.BeforeDispatch => new(1, value, ExternalCancellationSupport.BeforeSubmissionOnly, false),
        CancellationClassV1.ExactAcknowledgement => new(1, value, ExternalCancellationSupport.ProviderCooperative, true),
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}

/// <summary>Exact provider evidence only. It cannot release SingNext authority state.</summary>
public readonly record struct ContainmentClosureReceiptV1(
    ushort Version,
    ExternalOperationHandle Operation,
    string ProviderIdentity,
    ulong ProviderGeneration,
    Guid ReceiptId)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesRelease => false;
    public bool AuthorizesReclaim => false;
    public bool AuthorizesExecution => false;

    public ContainmentClosureReceiptV1 Canonicalize()
    {
        if (Version != CurrentVersion) throw new NotSupportedException($"Containment receipt version {Version} is unsupported.");
        if (Operation.OperationId.Value == 0 || Operation.Generation.Value == 0 ||
            ProviderGeneration == 0 || ReceiptId == Guid.Empty ||
            string.IsNullOrWhiteSpace(ProviderIdentity) || ProviderIdentity != ProviderIdentity.Trim())
            throw new ArgumentException("Containment receipt identity/generation is incomplete or noncanonical.");
        return this;
    }
}

public enum ExternalOperationState
{
    Prepared = 0,
    Admitted = 1,
    Submitted = 2,
    DeviceComplete = 3,
    Visible = 4,
    Published = 5,
    Released = 6
}

public enum ExternalOperationDisposition
{
    Active = 0,
    CancellationPending = 1,
    Cancelled = 2,
    Completed = 3,
    Discarded = 4,
    Published = 5,
    ProviderLost = 6,
    Faulted = 7
}

public enum ExternalOperationCompletionDisposition
{
    Completed = 0,
    Cancelled = 1,
    Faulted = 2
}

public enum ExternalVisibilityRequirement
{
    None = 0,
    ConsumerDomain = 1,
    PublicationFence = 2
}

public enum ExternalVisibilityClassV1 : byte
{
    CoherentImmediate = 1,
    AcquireRequired = 2,
    StagedCopyBack = 3,
    ProviderPrivateUntilCommit = 4,
    DirectCoherentWrite = 5,
}

/// <summary>Non-authoritative mapping onto existing Region and ExternalOperation owners.</summary>
public readonly record struct ExternalVisibilitySemanticsV1(
    ushort Version,
    ExternalVisibilityClassV1 VisibilityClass,
    RegionUseMode RegionUseMode,
    ExternalVisibilityRequirement Requirement,
    ExternalPublicationPolicy PublicationPolicy,
    bool RequiresExactMutationEpoch,
    bool RequiresPublicationDecision)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesVisibility => false;
    public bool AuthorizesPublication => false;

    public ExternalVisibilitySemanticsV1 Canonicalize()
    {
        if (Version != CurrentVersion) throw new NotSupportedException($"Visibility semantics version {Version} is unsupported.");
        if (!Enum.IsDefined(VisibilityClass) || !Enum.IsDefined(RegionUseMode) ||
            !Enum.IsDefined(Requirement) || !Enum.IsDefined(PublicationPolicy))
            throw new ArgumentOutOfRangeException(nameof(VisibilityClass));
        var expected = ExternalVisibilitySemantics.ForClass(VisibilityClass);
        if (this != expected) throw new ArgumentException("Visibility semantics are noncanonical.");
        return this;
    }
}

public static class ExternalVisibilitySemantics
{
    public static ExternalVisibilitySemanticsV1 ForClass(ExternalVisibilityClassV1 value) => value switch
    {
        ExternalVisibilityClassV1.CoherentImmediate => new(1, value, RegionUseMode.ReadOnly,
            ExternalVisibilityRequirement.None, ExternalPublicationPolicy.DirectCoherent, true, false),
        ExternalVisibilityClassV1.AcquireRequired => new(1, value, RegionUseMode.ReadOnly,
            ExternalVisibilityRequirement.ConsumerDomain, ExternalPublicationPolicy.DirectCoherent, true, false),
        ExternalVisibilityClassV1.StagedCopyBack => new(1, value, RegionUseMode.StagedOutput,
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged, true, true),
        ExternalVisibilityClassV1.ProviderPrivateUntilCommit => new(1, value, RegionUseMode.DevicePrivate,
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged, true, true),
        ExternalVisibilityClassV1.DirectCoherentWrite => new(1, value, RegionUseMode.DirectCoherentWrite,
            ExternalVisibilityRequirement.ConsumerDomain, ExternalPublicationPolicy.DirectCoherent, true, false),
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}

public enum ExternalPublicationPolicy
{
    Staged = 0,
    DirectCoherent = 1
}

public enum ExternalEffectClass
{
    StagedReversibleUntilPublish = 0,
    SnapshotOrIdempotenceRequired = 1,
    IrreversibleBarrier = 2
}

public enum ExternalReplayProtection
{
    None = 0,
    SnapshotRestore = 1,
    Idempotent = 2,
    Deduplicated = 3
}

public enum ExternalEffectBoundaryState
{
    NotCrossed = 0,
    StagedPending = 1,
    ExternallyVisible = 2,
    Irreversible = 3
}

public readonly record struct ExternalEffectPolicy(
    ExternalEffectClass EffectClass,
    ExternalReplayProtection ReplayProtection,
    bool ReplayConsumerAcknowledged);

/// <summary>Immutable semantic description only; it never grants effect or publication authority.</summary>
public readonly record struct EffectSemanticsV1(
    ushort Version,
    bool Staged,
    bool Reversible,
    bool LocallyIrreversible,
    bool ExternallyObservable,
    bool Durable,
    bool Compensatable,
    bool Idempotent,
    bool Commutative)
{
    public const ushort CurrentVersion = 1;

    public EffectSemanticsV1 Canonicalize()
    {
        if (Version != CurrentVersion)
            throw new NotSupportedException($"Effect semantics version {Version} is unsupported.");
        if (Staged && (LocallyIrreversible || ExternallyObservable || Durable))
            throw new ArgumentException("A staged effect cannot already be irreversible, externally observable, or durable.");
        if (Reversible && LocallyIrreversible)
            throw new ArgumentException("An effect cannot be both reversible and locally irreversible.");
        if (Compensatable && Staged)
            throw new ArgumentException("Compensation describes an already-observable effect, not withheld staged state.");
        return this;
    }

    public bool AuthorizesEffect => false;
    public bool AuthorizesPublication => false;
}

public static class ExternalEffectSemantics
{
    /// <summary>One-way conservative mapping. No reverse inference is defined.</summary>
    public static EffectSemanticsV1 FromPolicy(ExternalEffectPolicy policy)
    {
        if (!Enum.IsDefined(policy.EffectClass) || !Enum.IsDefined(policy.ReplayProtection))
            throw new ArgumentOutOfRangeException(nameof(policy));
        return (policy.EffectClass switch
        {
            ExternalEffectClass.StagedReversibleUntilPublish =>
                new EffectSemanticsV1(1, true, true, false, false, false, false, false, false),
            ExternalEffectClass.SnapshotOrIdempotenceRequired =>
                new EffectSemanticsV1(1, false, false, true, true, false, false,
                    policy.ReplayProtection is ExternalReplayProtection.Idempotent or ExternalReplayProtection.Deduplicated,
                    false),
            ExternalEffectClass.IrreversibleBarrier =>
                new EffectSemanticsV1(1, false, false, true, true, false, false, false, false),
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        }).Canonicalize();
    }
}

public readonly record struct OperationDependencySnapshot(
    ulong PlatformGeneration,
    ulong DeviceGeneration,
    ulong BindingGeneration = 0,
    ulong BackingGeneration = 0);

public readonly record struct OperationRegionUseRequest(
    RegionHandle Region,
    RegionUseMode Mode,
    RegionUseRange Range);

public sealed record OperationPreparation(
    ExternalOperationHandle Operation,
    RegionOwner Principal,
    IReadOnlyList<OperationRegionUseRequest> RegionUses,
    ExternalVisibilityRequirement VisibilityRequirement,
    ExternalPublicationPolicy PublicationPolicy,
    ExternalEffectPolicy EffectPolicy);

public sealed record OperationAdmissionSnapshot(
    ExternalOperationHandle Operation,
    RegionOwner Principal,
    IReadOnlyList<RegionUseDescriptor> RegionUses,
    OperationDependencySnapshot Dependencies,
    ExternalServiceIdentity ServiceIdentity = default,
    ExternalCancellationSupport CancellationSupport = ExternalCancellationSupport.BeforeSubmissionOnly,
    ExternalEffectClass EffectClass = ExternalEffectClass.StagedReversibleUntilPublish,
    ExternalPublicationPolicy PublicationPolicy = ExternalPublicationPolicy.Staged,
    CancellationScopeHandle? CancellationScope = null,
    uint ContractVersion = ExternalOperationContract.Version);

public enum ExternalOperationReceiptStatus
{
    Prepared = 0,
    Admitted = 1,
    Submitted = 2,
    DeviceComplete = 3,
    Visible = 4,
    Published = 5,
    Released = 6,
    Failed = 7,
    Stale = 8
}

public readonly record struct ExternalOperationReceipt(
    uint ContractVersion,
    ExternalOperationHandle Operation,
    ExternalOperationReceiptStatus Status,
    ExternalOperationDisposition Disposition);

public static class ExternalOperationReceipts
{
    public static ExternalOperationReceipt FromSnapshot(ExternalOperationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var status = snapshot.Disposition is ExternalOperationDisposition.ProviderLost or ExternalOperationDisposition.Faulted
            ? ExternalOperationReceiptStatus.Failed
            : snapshot.State switch
            {
                ExternalOperationState.Prepared => ExternalOperationReceiptStatus.Prepared,
                ExternalOperationState.Admitted => ExternalOperationReceiptStatus.Admitted,
                ExternalOperationState.Submitted => ExternalOperationReceiptStatus.Submitted,
                ExternalOperationState.DeviceComplete => ExternalOperationReceiptStatus.DeviceComplete,
                ExternalOperationState.Visible => ExternalOperationReceiptStatus.Visible,
                ExternalOperationState.Published => ExternalOperationReceiptStatus.Published,
                ExternalOperationState.Released => ExternalOperationReceiptStatus.Released,
                _ => ExternalOperationReceiptStatus.Failed
            };
        return new(ExternalOperationContract.Version, snapshot.Operation, status, snapshot.Disposition);
    }

    public static ExternalOperationReceipt Stale(ExternalOperationHandle operation) =>
        new(ExternalOperationContract.Version, operation, ExternalOperationReceiptStatus.Stale, ExternalOperationDisposition.Discarded);
}

public readonly record struct OperationCompletion(
    OperationBinding Binding,
    ExternalOperationCompletionDisposition Disposition);

public readonly record struct OperationVisibilityEvidence(
    OperationBinding Binding,
    ExternalVisibilityRequirement Requirement,
    bool Satisfied);

public readonly record struct PublicationPlan(ExternalPublicationPolicy Policy);

/// <summary>
/// Exact decision emitted by the existing SingNext publication owner for one in-flight staged action.
/// It is consumed synchronously at that owner boundary and is not a reusable permit.
/// </summary>
public readonly record struct ExternalPublicationDecisionV1(
    ushort Version,
    OperationBinding Binding,
    OperationDependencySnapshot Dependencies,
    ExternalPublicationPolicy Policy,
    ulong DecisionGeneration)
{
    public const ushort CurrentVersion = 1;
    public bool IsReusablePermit => false;
    public bool AuthorizesNewExecution => false;
}

/// <summary>
/// ProviderResourcesClosed is consumed only by the existing lifecycle owner. The legacy
/// ProviderEffectContained bit is evidence-only and deliberately ignored by release logic.
/// ProviderUnavailable is diagnostic state and never proves closure.
/// </summary>
public readonly record struct ReleasePlan(
    bool ProviderResourcesClosed,
    bool ProviderUnavailable,
    bool ProviderEffectContained = false)
{
    public bool IsAuthority => false;
}

public readonly record struct ExternalOperationTransition(
    ulong Sequence,
    ExternalOperationState From,
    ExternalOperationState To,
    string Event);

public sealed record ExternalOperationSnapshot(
    ExternalOperationHandle Operation,
    RegionOwner Principal,
    ExternalOperationState State,
    ExternalOperationDisposition Disposition,
    OperationAdmissionSnapshot? Admission,
    OperationBinding? Binding,
    ExternalVisibilityRequirement VisibilityRequirement,
    ExternalPublicationPolicy PublicationPolicy,
    ExternalEffectPolicy EffectPolicy,
    ExternalEffectBoundaryState EffectBoundary,
    IReadOnlyList<ExternalOperationTransition> Transitions);
