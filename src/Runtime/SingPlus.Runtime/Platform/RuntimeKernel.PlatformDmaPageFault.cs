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
            var identity = PlatformIdentity(resolved.Value!);
            var exactGrant = PlatformAuthority.ResolveDmaSubmissionGrant(submission, identity);
            if (!exactGrant.IsSuccess)
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(
                    exactGrant.Error, exactGrant.Message!);
            var usable = Regions.ValidatePlatformMappingRegionUsability(exactGrant.Value!.Mapping.Region,
                new RegionOwner(identity.DomainId, identity.ProcessGeneration));
            if (!usable.IsSuccess)
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(usable.Error, usable.Message!);
            return PlatformAuthority.ResolveDmaPageFault(submission, faultRange,
                requestedAccess, faultSequence, identity, () =>
                    Regions.ValidatePlatformMappingRegionUsability(exactGrant.Value!.Mapping.Region,
                        new RegionOwner(identity.DomainId, identity.ProcessGeneration)));
        }
        finally { PlatformAuthority.FlushDmaTrace(trace); }
    }
}
