using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    public KernelResult<PlatformDmaPageFaultResolutionEvidence> ResolvePlatformDmaPageFault(
        ProcessHandle subject,
        PlatformDmaSubmission submission,
        PlatformDmaRange faultRange,
        PlatformMemoryAccess requestedAccess,
        ulong faultSequence)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(
                resolved.Error, resolved.Message!);
        var trace = PlatformAuthority.CaptureDmaTrace(submission);
        try
        {
            return PlatformAuthority.ResolveDmaPageFault(submission, faultRange,
                requestedAccess, faultSequence, PlatformIdentity(resolved.Value!));
        }
        finally { PlatformAuthority.FlushDmaTrace(trace); }
    }
}
