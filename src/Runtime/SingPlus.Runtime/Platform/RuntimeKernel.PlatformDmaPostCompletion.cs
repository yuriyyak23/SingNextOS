using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private KernelResult<PlatformDmaPostCompletionVisibilityEvidence> GetOrFinalizePlatformDmaCopyVisibility(
        ProcessHandle subject, PlatformDmaSubmission submission, PlatformDmaCompletionEvidence completion)
    {
        var process = Processes.Resolve(subject);
        if (!process.IsSuccess) return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(process.Error, process.Message!);
        var trace = PlatformAuthority.CaptureDmaTrace(submission);
        KernelResult<PlatformDmaPostCompletionVisibilityEvidence> proven;
        try { proven = PlatformAuthority.GetProvenDmaCopyVisibility(submission, completion, PlatformIdentity(process.Value!)); }
        finally { PlatformAuthority.FlushDmaTrace(trace); }
        return proven.Error == KernelError.PlatformBindingDraining
            ? FinalizePlatformDmaPostCompletionVisibility(subject, submission, completion) : proven;
    }
    public KernelResult<PlatformDmaPostCompletionVisibilityEvidence> FinalizePlatformDmaPostCompletionVisibility(
        ProcessHandle subject, PlatformDmaSubmission submission, PlatformDmaCompletionEvidence completionEvidence)
    {
        PlatformAuthorityBridge.DmaTraceObserver? trace = null;
        try
        {
            SingPlus.Sip.SingProcess expected;
            PlatformDomainIdentity identity;
            lock (_platformMemoryUseGate)
            {
                trace = PlatformAuthority.CaptureDmaTrace(submission);
                var resolved = Processes.Resolve(subject);
                if (!resolved.IsSuccess)
                    return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(resolved.Error, resolved.Message!);
                expected = resolved.Value!;
                identity = PlatformIdentity(expected);
            }
            // Existing cleanup must remain callable while Exiting and after capability revoke.
            return PlatformAuthority.FinalizeDmaPostCompletionVisibility(submission, completionEvidence, identity, commit =>
            {
                lock (_platformMemoryUseGate)
                {
                    var current = Processes.Resolve(subject);
                    if (!current.IsSuccess)
                        return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(current.Error, current.Message!);
                    if (!ReferenceEquals(current.Value, expected))
                        return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(KernelError.StaleGeneration, "Completed visibility process incarnation changed.");
                    return commit();
                }
            });
        }
        finally { PlatformAuthority.FlushDmaTrace(trace); }
    }
}
