using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    internal KernelResult<PlatformDmaCompletionEvidence> GetOrObservePlatformDmaCompletion(
        ProcessHandle subject,
        PlatformDmaSubmission submission)
    {
        KernelResult<SingProcess> resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                resolved.Error, resolved.Message!);
        PlatformAuthorityBridge.DmaTraceObserver? trace = PlatformAuthority.CaptureDmaTrace(submission);
        KernelResult<PlatformDmaCompletionEvidence> proven;
        try { proven = PlatformAuthority.GetProvenDmaCompletion(submission, PlatformIdentity(resolved.Value!)); }
        finally { PlatformAuthority.FlushDmaTrace(trace); }
        return proven.Error == KernelError.PlatformBindingDraining
            ? ObservePlatformDmaCompletion(subject, submission)
            : proven;
    }

    public KernelResult<PlatformDmaCompletionEvidence> ObservePlatformDmaCompletion(
        ProcessHandle subject,
        PlatformDmaSubmission submission)
    {
        KernelResult<SingProcess> resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        // Completion observation drains an already-authorized external effect. It must remain
        // available after local capability revocation and while the process is Exiting.
        PlatformDomainIdentity identity = PlatformIdentity(resolved.Value!);
        PlatformAuthorityBridge.DmaTraceObserver? trace = PlatformAuthority.CaptureDmaTrace(submission);
        try { return PlatformAuthority.ObserveDmaCompletion(submission, identity); }
        finally { PlatformAuthority.FlushDmaTrace(trace); }
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
        KernelResult<SingProcess> resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        SingProcess process = resolved.Value!;
        if (process.State == ProcessState.Exiting)
        {
            return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                KernelError.InvalidTransition,
                "An Exiting process cannot accept new DMA completion-event delivery; use completion observation without an endpoint to drain the operation.");
        }

        KernelResult endpointValidation = _kernelEvents.Validate(subject, eventEndpoint);
        if (!endpointValidation.IsSuccess)
        {
            return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                endpointValidation.Error,
                endpointValidation.Message!);
        }

        PlatformDomainIdentity identity = PlatformIdentity(process);
        PlatformAuthorityBridge.DmaTraceObserver? trace = PlatformAuthority.CaptureDmaTrace(submission);
        KernelResult submissionValidation;
        try { submissionValidation = PlatformAuthority.ValidateDmaSubmissionIdentity(submission, identity); }
        finally { PlatformAuthority.FlushDmaTrace(trace); }
        if (!submissionValidation.IsSuccess)
        {
            return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                submissionValidation.Error,
                submissionValidation.Message!);
        }

        KernelResult<KernelEvent> staged = _kernelEvents.Stage(
            subject,
            eventEndpoint,
            KernelEventClass.Completion,
            DmaCompletionEventSource(submission), out Func<KernelResult>? rollbackOriginal);
        if (!staged.IsSuccess)
        {
            return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                staged.Error,
                staged.Message!);
        }

        bool localConsumed = false;
        try
        {
            KernelResult<PlatformDmaCompletionEvidence> completion;
            try { completion = PlatformAuthority.ObserveDmaCompletion(submission, identity); }
            finally { PlatformAuthority.FlushDmaTrace(trace); }
            if (!completion.IsSuccess)
            {
                KernelResult rollback = rollbackOriginal!();
                localConsumed = rollback.IsSuccess;
                if (!rollback.IsSuccess)
                {
                    return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                        KernelError.PlatformFaulted,
                        "DMA completion observation failed and the exact staged local event could not be rolled back.");
                }

                return completion;
            }

            KernelResult<KernelEvent> committed = _kernelEvents.CommitExact(subject, staged.Value!);
            localConsumed = committed.IsSuccess;
            if (!committed.IsSuccess)
            {
                return KernelResult<PlatformDmaCompletionEvidence>.Fail(
                    KernelError.PlatformFaulted,
                    "DMA completion was proven but the exact staged local event could not be committed.");
            }

            return completion;
        }
        finally
        {
            // Local visibility refusal only. Original DMA completion/uncertainty
            // and backing remain with their existing authority owners.
            if (!localConsumed) _ = rollbackOriginal!();
        }
    }

    private static string DmaCompletionEventSource(PlatformDmaSubmission submission) =>
        FormattableString.Invariant(
            $"platform/dma-completion-observed/v1/{submission.OperationId.Value}/{submission.Generation.Value}");
}
