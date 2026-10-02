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
        var usable = ValidateDmaGrantRegionUsability(grant, identity);
        if (!usable.IsSuccess)
            return KernelResult<PlatformDmaSubmission>.Fail(usable.Error, usable.Message!);
        return PlatformAuthority.SubmitDmaGrant(
            grant,
            prepareEvidence,
            identity,
            () => RevalidatePlatformDmaRegionSubmission(subject, grant));
    }

    private KernelResult RevalidatePlatformDmaRegionSubmission(
        ProcessHandle subject, PlatformDmaGrant grant, ulong? expectedMutationEpoch = null)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var process = resolved.Value!;
        var effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess) return effect;
        var owner = new RegionOwner(process.DomainId, subject.Generation);
        return Regions.ValidatePlatformMappingRegionUsability(grant.Mapping.Region, owner, expectedMutationEpoch);
    }

    private KernelResult ValidateDmaGrantRegionUsability(PlatformDmaGrant grant, PlatformDomainIdentity identity)
    {
        var exact = PlatformAuthority.ValidateDmaGrant(grant, identity);
        if (!exact.IsSuccess) return exact;
        return Regions.ValidatePlatformMappingRegionUsability(grant.Mapping.Region,
            new RegionOwner(identity.DomainId, identity.ProcessGeneration));
    }
}
