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
/// Authorizes local authority release only after provider closure or independently proven
/// effect containment. ProviderUnavailable is diagnostic state and never proves either.
/// </summary>
public readonly record struct ReleasePlan(
    bool ProviderResourcesClosed,
    bool ProviderUnavailable,
    bool ProviderEffectContained = false);

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
