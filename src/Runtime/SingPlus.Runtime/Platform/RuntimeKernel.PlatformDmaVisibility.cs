using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<PlatformDmaPrepareEvidence> PreparePlatformDmaForDevice(
        ProcessHandle subject,
        PlatformDmaGrant grant)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformDmaPrepareEvidence>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        var process = resolved.Value!;
        var effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
        {
            return KernelResult<PlatformDmaPrepareEvidence>.Fail(
                effect.Error,
                effect.Message!);
        }

        var identity = new PlatformDomainIdentity(process.DomainId, subject.Generation);
        return PlatformAuthority.PrepareDmaGrantVisibility(grant, identity);
    }

    public KernelResult<PlatformDmaAcquireEvidence> AcquirePlatformDmaForCpu(
        ProcessHandle subject,
        PlatformDmaGrant grant)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformDmaAcquireEvidence>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        var process = resolved.Value!;
        var identity = new PlatformDomainIdentity(process.DomainId, subject.Generation);
        return PlatformAuthority.AcquireDmaGrantVisibility(grant, identity);
    }
}
