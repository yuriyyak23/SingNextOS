using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<PlatformDmaSubmission> SubmitPlatformDma(
        ProcessHandle subject,
        PlatformDmaGrant grant,
        PlatformDmaPrepareEvidence prepareEvidence)
    {
        lock (_platformMemoryUseGate)
            return SubmitPlatformDmaLocked(subject, grant, prepareEvidence);
    }

    private KernelResult<PlatformDmaSubmission> SubmitPlatformDmaLocked(
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

        var identity = PlatformIdentity(process);
        return PlatformAuthority.SubmitDmaGrant(
            grant,
            prepareEvidence,
            identity);
    }
}
