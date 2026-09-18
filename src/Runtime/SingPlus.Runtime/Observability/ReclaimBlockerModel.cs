namespace SingPlus.Runtime;

// Cold diagnostic evidence only; these variants never grant authority.
#if SINGPLUS_PREVIEW_LANGUAGE
internal closed record class ReclaimBlockerCause;
#else
internal abstract record class ReclaimBlockerCause;
#endif

internal sealed record class ReclaimPendingCause : ReclaimBlockerCause;
internal sealed record class ReclaimErrorCause(KernelError Error) : ReclaimBlockerCause;

internal static class ReclaimBlockerCauseModel
{
    internal static ReclaimBlockerCause FromOptionalError(KernelError? error) =>
        error is { } value ? new ReclaimErrorCause(value) : new ReclaimPendingCause();

    internal static KernelError? ToOptionalError(ReclaimBlockerCause cause) => cause switch
    {
        ReclaimPendingCause => null,
        ReclaimErrorCause fault => fault.Error,
#if !SINGPLUS_PREVIEW_LANGUAGE
        _ => throw new ArgumentOutOfRangeException(nameof(cause))
#endif
    };
}
