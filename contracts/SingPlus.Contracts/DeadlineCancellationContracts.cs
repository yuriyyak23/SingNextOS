namespace SingPlus.Contracts;

public static class DeadlineCancellationContract
{
    public const uint Version = 1;
}

public readonly record struct CancellationScopeId(ulong Value);
public readonly record struct CancellationScopeGeneration(ulong Value);
public readonly record struct CancellationScopeHandle(
    CancellationScopeId ScopeId,
    CancellationScopeGeneration Generation);

/// <summary>A timestamp from the runtime's monotonic TimeProvider clock.</summary>
public readonly record struct MonotonicDeadline(long Timestamp);

public enum DeadlineClockClass
{
    MonotonicRuntime = 0,
}

public readonly record struct CancellationRequest(
    CancellationScopeHandle Scope,
    ulong ExpectedSequence,
    uint ContractVersion = DeadlineCancellationContract.Version);

public enum CancellationDisposition
{
    Active = 0,
    CancelledBeforeEffect,
    CancellationRequested,
    TooLateEffectMayExist,
    CompletedBeforeCancellation,
    ProviderClosurePending,
    ProviderEffectContained,
    Unsupported,
    Stale,
}

public enum TimeoutDisposition
{
    NotExpired = 0,
    ExpiredWaitingMayStop,
}

public sealed record CancellationObservation(
    CancellationScopeHandle Scope,
    ProcessHandle Owner,
    CancellationScopeHandle? Parent,
    MonotonicDeadline? EffectiveDeadline,
    bool CancellationRequested,
    TimeoutDisposition Timeout,
    CancellationDisposition Disposition,
    ulong Sequence,
    uint ContractVersion = DeadlineCancellationContract.Version)
{
    // This DTO is evidence/coordination state. Resource authorities remain the only
    // source of truth for effect closure, ownership release, and reclaim.
    public bool AuthorizesEffect => false;
    public bool AuthorizesReclaim => false;
}
