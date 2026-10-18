using SingPlus.Contracts;
using SingPlus.Platform;
namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    internal KernelResult ConsumeOriginalMmioBacking(PlatformDomainIdentity subject,RegionAuthority regions)
    {
        MmioBufferPayload[] payloads;
        lock(_secureDomainLifecycleGate)
        {
            var facts=DeviceDependencyRecordsLocked().Where(d=>d.Lease.DomainBinding.Subject==subject)
                .SelectMany(d=>d.OriginalMmioMaps).Where(f=>f.Payload is not null && HasConsumedOriginalMmioMap(f)).ToArray();
            if(facts.Any(f=>!ReferenceEquals(f.Payload!.Original,f.Payload) || !ReferenceEquals(f.Payload.AdmissionOwner,f) ||
                !ReferenceEquals(f.Request.Backing,f.Payload.Backing) || !ReferenceEquals(f.Source.OwnerSelf,f.Source) ||
                !f.Source.OriginalMmioMaps.Any(original=>ReferenceEquals(original,f))))
                return KernelResult.Fail(KernelError.StaleGeneration,"Original MMIO backing owner changed; retain use.");
            payloads=facts.Select(f=>f.Payload!).ToArray();
        }
        // Runtime storage/Region release outside platform admission/commit locks.
        foreach(var payload in payloads)
        {
            var release=regions.ReleaseMmioUse(payload);if(!release.IsSuccess)return release;
            try{payload.ReleaseBytes();}
            catch(InvalidOperationException exception)
            {
                return KernelResult.Fail(KernelError.PlatformFaulted,$"Original MMIO reserved bytes remain for exact cleanup retry: {exception.Message}");
            }
        }
        return KernelResult.Ok();
    }
}
