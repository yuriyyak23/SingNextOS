using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    /// <summary>
    /// Observes one exact DSC1 terminal outcome while publishing a local wakeup on
    /// the supplied process-generation-bound endpoint. The event is notification
    /// only; the returned receipt and the DSC1 lifecycle remain the authority truth.
    /// </summary>
    public KernelResult<PlatformDsc1CopyReceipt> ObservePlatformDsc1Copy(
        ProcessHandle subject,
        PlatformDsc1CopySubmission submission,
        KernelEventEndpoint eventEndpoint)
    {
        Action? notify = null;
        Func<KernelResult>? rollbackOriginal = null;
        bool localConsumed = false;
        try
        {
            lock (_platformMemoryUseGate)
            {
                var resolved = Processes.Resolve(subject);
                if (!resolved.IsSuccess)
                {
                    return KernelResult<PlatformDsc1CopyReceipt>.Fail(
                        resolved.Error,
                        resolved.Message!);
                }

                if (resolved.Value!.State == ProcessState.Exiting)
                {
                    return KernelResult<PlatformDsc1CopyReceipt>.Fail(
                        KernelError.InvalidTransition,
                        "An Exiting process cannot accept new DSC1 terminal-event delivery; use endpoint-free observation or cancellation to drain the operation.");
                }

                var endpointValidation = _kernelEvents.Validate(subject, eventEndpoint);
                if (!endpointValidation.IsSuccess)
                {
                    return KernelResult<PlatformDsc1CopyReceipt>.Fail(
                        endpointValidation.Error,
                        endpointValidation.Message!);
                }

                // Reject stale, forged and cross-process continuations before reserving
                // event capacity or reaching the provider.
                var payload = ResolveDsc1Payload(subject, submission);
                if (!payload.IsSuccess)
                {
                    return KernelResult<PlatformDsc1CopyReceipt>.Fail(
                        payload.Error,
                        payload.Message!);
                }

                var staged = _kernelEvents.Stage(
                    subject,
                    eventEndpoint,
                    KernelEventClass.Completion,
                    Dsc1TerminalEventSource(submission), out rollbackOriginal);
                if (!staged.IsSuccess)
                {
                    return KernelResult<PlatformDsc1CopyReceipt>.Fail(
                        staged.Error,
                        staged.Message!);
                }

                // Finalization publishes Completed output (or settles Cancelled without
                // output) and releases the exact reservations before a waiter can wake.
                var terminal = ObservePlatformDsc1CopyLocked(subject, submission);
                if (!terminal.IsSuccess)
                {
                    var rollback = rollbackOriginal!();
                    localConsumed = rollback.IsSuccess;
                    if (!rollback.IsSuccess)
                    {
                        return KernelResult<PlatformDsc1CopyReceipt>.Fail(
                            KernelError.PlatformFaulted,
                            "DSC1 terminal observation failed and the exact staged local event could not be rolled back.");
                    }

                    return terminal;
                }

                var committed = _kernelEvents.CommitExactDeferred(subject, staged.Value!, out notify);
                localConsumed = committed.IsSuccess;
                if (!committed.IsSuccess)
                {
                    return KernelResult<PlatformDsc1CopyReceipt>.Fail(
                        KernelError.PlatformFaulted,
                        "DSC1 terminal state was settled but the exact staged local event could not be committed.");
                }

                return terminal;
            }
        }
        finally
        {
            // This consumes only the exact local visibility reservation; it never
            // grants completion, output publication or backing release authority.
            if (!localConsumed) _ = rollbackOriginal?.Invoke();
            notify?.Invoke();
        }
    }

    private static string Dsc1TerminalEventSource(
        PlatformDsc1CopySubmission submission) =>
        FormattableString.Invariant(
            $"platform/dsc1-terminal-observed/v1/{submission.SubmissionId.Value}/{submission.Generation.Value}");
}
