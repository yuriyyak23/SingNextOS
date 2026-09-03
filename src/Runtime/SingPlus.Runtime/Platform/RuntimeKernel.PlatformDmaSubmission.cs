using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<PlatformDmaSubmission> SubmitPlatformDma(
        ProcessHandle subject,
        PlatformDmaGrant grant,
        PlatformDmaPrepareEvidence prepareEvidence)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformDmaSubmission>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        var process = resolved.Value!;
        var effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
        {
            return KernelResult<PlatformDmaSubmission>.Fail(
                effect.Error,
                effect.Message!);
        }

        var identity = new PlatformDomainIdentity(process.DomainId, subject.Generation);
        return PlatformAuthority.SubmitDmaGrant(
            grant,
            prepareEvidence,
            identity);
    }
}
