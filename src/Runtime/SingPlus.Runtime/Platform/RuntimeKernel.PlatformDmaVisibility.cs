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
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformDmaPrepareEvidence>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        process = resolved.Value!;
        var effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
        {
            return KernelResult<PlatformDmaPrepareEvidence>.Fail(
                effect.Error,
                effect.Message!);
        }

        identity = PlatformIdentity(process);
        var usable = ValidateDmaGrantRegionUsability(grant, identity);
        if (!usable.IsSuccess)
            return KernelResult<PlatformDmaPrepareEvidence>.Fail(usable.Error, usable.Message!);
        }
        return PlatformAuthority.PrepareDmaGrantVisibility(grant, identity, commit =>
        {
            lock (_platformMemoryUseGate)
            {
                var current = Processes.Resolve(subject);
                if (!current.IsSuccess) return KernelResult<PlatformDmaPrepareEvidence>.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, process))
                    return KernelResult<PlatformDmaPrepareEvidence>.Fail(KernelError.StaleGeneration, "DMA prepare process incarnation changed.");
                var ready = EnsureProcessAcceptsNewEffects(process);
                if (!ready.IsSuccess) return KernelResult<PlatformDmaPrepareEvidence>.Fail(ready.Error, ready.Message!);
                var owner = new RegionOwner(identity.DomainId, subject.Generation);
                var gate = Regions.GetPlatformMappingCompletionGate(grant.Mapping.Region, owner);
                if (!gate.IsSuccess) return KernelResult<PlatformDmaPrepareEvidence>.Fail(gate.Error, gate.Message!);
                lock (gate.Value!)
                {
                    var usable = Regions.ValidatePlatformMappingRegionUsabilityLocked(grant.Mapping.Region, owner);
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
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformDmaAcquireEvidence>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        process = resolved.Value!;
        identity = PlatformIdentity(process);
        var ready = EnsureProcessAcceptsNewEffects(process);
        if (!ready.IsSuccess) return KernelResult<PlatformDmaAcquireEvidence>.Fail(ready.Error, ready.Message!);
        var usable = ValidateDmaGrantRegionUsability(grant, identity);
        if (!usable.IsSuccess) return KernelResult<PlatformDmaAcquireEvidence>.Fail(usable.Error, usable.Message!);
        }
        return PlatformAuthority.AcquireDmaGrantVisibility(grant, identity, commit =>
        {
            lock (_platformMemoryUseGate)
            {
                var current = Processes.Resolve(subject);
                if (!current.IsSuccess) return KernelResult<PlatformDmaAcquireEvidence>.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, process))
                    return KernelResult<PlatformDmaAcquireEvidence>.Fail(KernelError.StaleGeneration, "Acquire process incarnation changed.");
                var ready = EnsureProcessAcceptsNewEffects(process);
                if (!ready.IsSuccess) return KernelResult<PlatformDmaAcquireEvidence>.Fail(ready.Error, ready.Message!);
                var owner = new RegionOwner(identity.DomainId, subject.Generation);
                var gate = Regions.GetPlatformMappingCompletionGate(grant.Mapping.Region, owner);
                if (!gate.IsSuccess) return KernelResult<PlatformDmaAcquireEvidence>.Fail(gate.Error, gate.Message!);
                lock (gate.Value!)
                {
                    var usable = Regions.ValidatePlatformMappingRegionUsabilityLocked(grant.Mapping.Region, owner);
                    return usable.IsSuccess ? commit() : KernelResult<PlatformDmaAcquireEvidence>.Fail(usable.Error, usable.Message!);
                }
            }
        });
    }
}
