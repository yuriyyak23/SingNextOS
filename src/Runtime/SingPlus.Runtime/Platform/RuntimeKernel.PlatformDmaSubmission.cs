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
        SingProcess process;
        PlatformDomainIdentity identity;
        lock (_platformMemoryUseGate)
        {
            KernelResult<SingProcess> resolved = Processes.Resolve(subject);
            if (!resolved.IsSuccess) return KernelResult<PlatformDmaSubmission>.Fail(resolved.Error, resolved.Message!);
            process = resolved.Value!;
            KernelResult ready = EnsureProcessAcceptsNewEffects(process);
            if (!ready.IsSuccess) return KernelResult<PlatformDmaSubmission>.Fail(ready.Error, ready.Message!);
            identity = PlatformIdentity(process);
            KernelResult usable = ValidateDmaGrantRegionUsability(grant, identity);
            if (!usable.IsSuccess) return KernelResult<PlatformDmaSubmission>.Fail(usable.Error, usable.Message!);
        }
        return PlatformAuthority.SubmitDmaGrant(grant, prepareEvidence, identity, commit =>
        {
            lock (_platformMemoryUseGate)
            {
                KernelResult<SingProcess> current = Processes.Resolve(subject);
                if (!current.IsSuccess) return KernelResult<PlatformDmaSubmission>.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, process))
                    return KernelResult<PlatformDmaSubmission>.Fail(KernelError.StaleGeneration, "DMA submit process incarnation changed.");
                KernelResult ready = EnsureProcessAcceptsNewEffects(process);
                if (!ready.IsSuccess) return KernelResult<PlatformDmaSubmission>.Fail(ready.Error, ready.Message!);
                RegionOwner owner = new RegionOwner(identity.DomainId, subject.Generation);
                KernelResult<object> gate = Regions.GetPlatformMappingCompletionGate(grant.Mapping.Region, owner);
                if (!gate.IsSuccess) return KernelResult<PlatformDmaSubmission>.Fail(gate.Error, gate.Message!);
                lock (gate.Value!)
                {
                    KernelResult usable = Regions.ValidatePlatformMappingRegionUsabilityLocked(grant.Mapping.Region, owner);
                    return usable.IsSuccess ? commit() : KernelResult<PlatformDmaSubmission>.Fail(usable.Error, usable.Message!);
                }
            }
        });
    }
    private KernelResult RevalidatePlatformDmaRegionSubmission(
        ProcessHandle subject, PlatformDmaGrant grant, ulong? expectedMutationEpoch = null)
    {
        KernelResult<SingProcess> resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        SingProcess process = resolved.Value!;
        KernelResult effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess) return effect;
        RegionOwner owner = new RegionOwner(process.DomainId, subject.Generation);
        return Regions.ValidatePlatformMappingRegionUsability(grant.Mapping.Region, owner, expectedMutationEpoch);
    }

    private KernelResult ValidateDmaGrantRegionUsability(PlatformDmaGrant grant, PlatformDomainIdentity identity)
    {
        KernelResult exact = PlatformAuthority.ValidateDmaGrant(grant, identity);
        if (!exact.IsSuccess) return exact;
        return Regions.ValidatePlatformMappingRegionUsability(grant.Mapping.Region,
            new RegionOwner(identity.DomainId, identity.ProcessGeneration));
    }
}
