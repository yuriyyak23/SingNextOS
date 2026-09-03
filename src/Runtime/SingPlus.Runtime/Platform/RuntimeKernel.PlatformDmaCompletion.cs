using SingPlus.Contracts;

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
        var identity = new PlatformDomainIdentity(
            resolved.Value!.DomainId,
            subject.Generation);
        return PlatformAuthority.ObserveDmaCompletion(submission, identity);
    }
}
