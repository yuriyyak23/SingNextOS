using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<PlatformDmaCompletionEvidence> ObservePlatformDmaCompletion(
        ProcessHandle subject,
        PlatformDmaSubmission submission)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        // Completion observation drains an already-authorized external effect. It must remain
        // available after local capability revocation and while the process is Exiting.
        var identity = PlatformIdentity(resolved.Value!);
        return PlatformAuthority.ObserveDmaCompletion(submission, identity);
    }

    /// <summary>
    /// Observes the exact DMA completion while atomically publishing a local wakeup
    /// on the supplied process-owned endpoint. The event is notification only: the
    /// returned completion evidence and the existing post-completion visibility and
    /// authority-closure path remain the lifecycle truth.
    /// </summary>
    public KernelResult<PlatformDmaCompletionEvidence> ObservePlatformDmaCompletion(
        ProcessHandle subject,
        PlatformDmaSubmission submission,
        KernelEventEndpoint eventEndpoint)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        var process = resolved.Value!;
        if (process.State == ProcessState.Exiting)
        {
            return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                KernelError.InvalidTransition,
                "An Exiting process cannot accept new DMA completion-event delivery; use completion observation without an endpoint to drain the operation.");
        }

        var endpointValidation = _kernelEvents.Validate(subject, eventEndpoint);
        if (!endpointValidation.IsSuccess)
        {
            return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                endpointValidation.Error,
                endpointValidation.Message!);
        }

        var identity = PlatformIdentity(process);
        var submissionValidation = PlatformAuthority.ValidateDmaSubmissionIdentity(
            submission,
            identity);
        if (!submissionValidation.IsSuccess)
        {
            return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                submissionValidation.Error,
                submissionValidation.Message!);
        }

        var staged = _kernelEvents.Stage(
            subject,
            eventEndpoint,
            KernelEventClass.Completion,
            DmaCompletionEventSource(submission));
        if (!staged.IsSuccess)
        {
            return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                staged.Error,
                staged.Message!);
        }

        var completion = PlatformAuthority.ObserveDmaCompletion(submission, identity);
        if (!completion.IsSuccess)
        {
            var rollback = _kernelEvents.RollbackExact(subject, staged.Value!);
            if (!rollback.IsSuccess)
            {
                return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                    KernelError.PlatformFaulted,
                    "DMA completion observation failed and the exact staged local event could not be rolled back.");
            }

            return completion;
        }

        var committed = _kernelEvents.CommitExact(subject, staged.Value!);
        if (!committed.IsSuccess)
        {
            return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                KernelError.PlatformFaulted,
                "DMA completion was proven but the exact staged local event could not be committed.");
        }

        return completion;
    }

    private static string DmaCompletionEventSource(PlatformDmaSubmission submission) =>
        FormattableString.Invariant(
            $"platform/dma-completion-observed/v1/{submission.OperationId.Value}/{submission.Generation.Value}");
}
