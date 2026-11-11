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
        SingProcess process;
        PlatformDomainIdentity identity;
        lock (_platformMemoryUseGate)
        {
            KernelResult<SingProcess> resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformDmaPrepareEvidence>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        process = resolved.Value!;
            KernelResult effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
        {
            return KernelResult<PlatformDmaPrepareEvidence>.Fail(
                effect.Error,
                effect.Message!);
        }

        identity = PlatformIdentity(process);
            KernelResult usable = ValidateDmaGrantRegionUsability(grant, identity);
        if (!usable.IsSuccess)
            return KernelResult<PlatformDmaPrepareEvidence>.Fail(usable.Error, usable.Message!);
        }
        return PlatformAuthority.PrepareDmaGrantVisibility(grant, identity, commit =>
        {
            lock (_platformMemoryUseGate)
            {
                KernelResult<SingProcess> current = Processes.Resolve(subject);
                if (!current.IsSuccess) return KernelResult<PlatformDmaPrepareEvidence>.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, process))
                    return KernelResult<PlatformDmaPrepareEvidence>.Fail(KernelError.StaleGeneration, "DMA prepare process incarnation changed.");
                KernelResult ready = EnsureProcessAcceptsNewEffects(process);
                if (!ready.IsSuccess) return KernelResult<PlatformDmaPrepareEvidence>.Fail(ready.Error, ready.Message!);
                RegionOwner owner = new RegionOwner(identity.DomainId, subject.Generation);
                KernelResult<object> gate = Regions.GetPlatformMappingCompletionGate(grant.Mapping.Region, owner);
                if (!gate.IsSuccess) return KernelResult<PlatformDmaPrepareEvidence>.Fail(gate.Error, gate.Message!);
                lock (gate.Value!)
                {
                    KernelResult usable = Regions.ValidatePlatformMappingRegionUsabilityLocked(grant.Mapping.Region, owner);
                    return usable.IsSuccess ? commit() : KernelResult<PlatformDmaPrepareEvidence>.Fail(usable.Error, usable.Message!);
                }
            }
        });
    }

    public KernelResult<PlatformDmaAcquireEvidence> AcquirePlatformDmaForCpu(
        ProcessHandle subject,
        PlatformDmaGrant grant)
    {
        SingProcess process;
        PlatformDomainIdentity identity;
        lock (_platformMemoryUseGate)
        {
            KernelResult<SingProcess> resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformDmaAcquireEvidence>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        process = resolved.Value!;
        identity = PlatformIdentity(process);
            KernelResult ready = EnsureProcessAcceptsNewEffects(process);
        if (!ready.IsSuccess) return KernelResult<PlatformDmaAcquireEvidence>.Fail(ready.Error, ready.Message!);
            KernelResult usable = ValidateDmaGrantRegionUsability(grant, identity);
        if (!usable.IsSuccess) return KernelResult<PlatformDmaAcquireEvidence>.Fail(usable.Error, usable.Message!);
        }
        return PlatformAuthority.AcquireDmaGrantVisibility(grant, identity, commit =>
        {
            lock (_platformMemoryUseGate)
            {
                KernelResult<SingProcess> current = Processes.Resolve(subject);
                if (!current.IsSuccess) return KernelResult<PlatformDmaAcquireEvidence>.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, process))
                    return KernelResult<PlatformDmaAcquireEvidence>.Fail(KernelError.StaleGeneration, "Acquire process incarnation changed.");
                KernelResult ready = EnsureProcessAcceptsNewEffects(process);
                if (!ready.IsSuccess) return KernelResult<PlatformDmaAcquireEvidence>.Fail(ready.Error, ready.Message!);
                RegionOwner owner = new RegionOwner(identity.DomainId, subject.Generation);
                KernelResult<object> gate = Regions.GetPlatformMappingCompletionGate(grant.Mapping.Region, owner);
                if (!gate.IsSuccess) return KernelResult<PlatformDmaAcquireEvidence>.Fail(gate.Error, gate.Message!);
                lock (gate.Value!)
                {
                    KernelResult usable = Regions.ValidatePlatformMappingRegionUsabilityLocked(grant.Mapping.Region, owner);
                    return usable.IsSuccess ? commit() : KernelResult<PlatformDmaAcquireEvidence>.Fail(usable.Error, usable.Message!);
                }
            }
        });
    }
}
