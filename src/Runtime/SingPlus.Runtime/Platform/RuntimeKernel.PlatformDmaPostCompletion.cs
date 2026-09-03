using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<PlatformDmaPostCompletionVisibilityEvidence> FinalizePlatformDmaPostCompletionVisibility(
        ProcessHandle subject,
        PlatformDmaSubmission submission,
        PlatformDmaCompletionEvidence completionEvidence)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformDmaPostCompletionVisibilityEvidence>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        // This advances an already-authorized external lifetime. It must remain callable
        // after local capability revocation and while the process is Exiting.
        var identity = new PlatformDomainIdentity(
            resolved.Value!.DomainId,
            subject.Generation);
        return PlatformAuthority.FinalizeDmaPostCompletionVisibility(
            submission,
            completionEvidence,
            identity);
    }
}
