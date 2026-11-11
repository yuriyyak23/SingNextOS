using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

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
        KernelResult<SingProcess> resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(
                resolved.Error, resolved.Message!);
        PlatformAuthorityBridge.DmaTraceObserver? trace = PlatformAuthority.CaptureDmaTrace(submission);
        try
        {
            PlatformDomainIdentity identity = PlatformIdentity(resolved.Value!);
            KernelResult<PlatformDmaGrant> exactGrant = PlatformAuthority.ResolveDmaSubmissionGrant(submission, identity);
            if (!exactGrant.IsSuccess)
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(
                    exactGrant.Error, exactGrant.Message!);
            KernelResult initialUsable = Regions.ValidatePlatformMappingRegionUsability(exactGrant.Value!.Mapping.Region,
                new RegionOwner(identity.DomainId, identity.ProcessGeneration));
            if (!initialUsable.IsSuccess)
                return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(initialUsable.Error, initialUsable.Message!);
            return PlatformAuthority.ResolveDmaPageFault(submission, faultRange,
                requestedAccess, faultSequence, identity, commit =>
                {
                    lock (_platformMemoryUseGate)
                    {
                        KernelResult<SingProcess> current = Processes.Resolve(subject);
                        if (!current.IsSuccess)
                            return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(current.Error, current.Message!);
                        if (!ReferenceEquals(current.Value, resolved.Value))
                            return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(KernelError.StaleGeneration, "Page-fault process incarnation changed.");
                        KernelResult ready = EnsureProcessAcceptsNewEffects(current.Value!);
                        if (!ready.IsSuccess)
                            return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(ready.Error, ready.Message!);
                        RegionOwner owner = new RegionOwner(identity.DomainId, subject.Generation);
                        KernelResult<object> gate = Regions.GetPlatformMappingCompletionGate(exactGrant.Value!.Mapping.Region, owner);
                        if (!gate.IsSuccess)
                            return KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(gate.Error, gate.Message!);
                        lock (gate.Value!)
                        {
                            KernelResult usable = Regions.ValidatePlatformMappingRegionUsabilityLocked(exactGrant.Value.Mapping.Region, owner);
                            return usable.IsSuccess ? commit()
                                : KernelResult<PlatformDmaPageFaultResolutionEvidence>.Fail(usable.Error, usable.Message!);
                        }
                    }
                });
        }
        finally { PlatformAuthority.FlushDmaTrace(trace); }
    }
}
