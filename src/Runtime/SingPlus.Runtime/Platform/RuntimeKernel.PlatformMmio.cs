using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private readonly Dictionary<ProcessHandle, List<PlatformMmioLease>> _processPlatformMmioLeases = [];
    private readonly List<CapabilityId> _pendingPlatformMmioCapabilities = [];

    public KernelResult<PlatformMmioLease> BindPlatformMmio(
        ProcessHandle subject, PlatformDeviceLease deviceLease, CapabilityId mmioCapabilityId,
        long offset, long length, PlatformMmioAccess access) => BindPlatformMmioCore(subject, deviceLease, mmioCapabilityId, offset, length, access, null);

    private KernelResult<PlatformMmioLease> BindPlatformMmioCore(ProcessHandle subject, PlatformDeviceLease deviceLease,
        CapabilityId mmioCapabilityId, long offset, long length, PlatformMmioAccess access,
        (ProcessHandle Principal, CapabilityId Capability)? source,
        Func<Func<KernelResult>, KernelResult>? sourceCommit = null,MmioBufferPayload? backing = null)
    {
        KernelResult CommitSource(Func<KernelResult> commit) => sourceCommit is null
            ? CommitControllerMmio(source, commit) : sourceCommit(commit);
        lock (_platformMemoryUseGate)
        {
            KernelResult pending = CommitSource(() =>
            {
                if (_pendingPlatformMmioCapabilities.Count == int.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "MMIO admission accounting is exhausted.");
                _pendingPlatformMmioCapabilities.Add(mmioCapabilityId);
                return KernelResult.Ok();
            });
            if (!pending.IsSuccess) return KernelResult<PlatformMmioLease>.Fail(pending.Error, pending.Message!);
        }
        try { return BindPlatformMmioAdmitted(subject, deviceLease, mmioCapabilityId, offset, length, access, source, sourceCommit,backing); }
        finally { lock (_platformMemoryUseGate) _pendingPlatformMmioCapabilities.Remove(mmioCapabilityId); }
    }

    private KernelResult<PlatformMmioLease> BindPlatformMmioAdmitted(
        ProcessHandle subject,
        PlatformDeviceLease deviceLease,
        CapabilityId mmioCapabilityId,
        long offset,
        long length,
        PlatformMmioAccess access, (ProcessHandle Principal, CapabilityId Capability)? source,
        Func<Func<KernelResult>, KernelResult>? sourceCommit,MmioBufferPayload? backing)
    {
        KernelResult CommitSource(Func<KernelResult> commit) => sourceCommit is null
            ? CommitControllerMmio(source, commit) : sourceCommit(commit);
        if (access == PlatformMmioAccess.None ||
            (access & ~(PlatformMmioAccess.Read | PlatformMmioAccess.Write)) != 0)
        {
            return KernelResult<PlatformMmioLease>.Fail(
                KernelError.PlatformDenied,
                "Platform MMIO access must be Read, Write, or Read|Write.");
        }

        KernelResult<SingProcess> resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformMmioLease>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        SingProcess process = resolved.Value!;
        KernelResult effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
        {
            return KernelResult<PlatformMmioLease>.Fail(
                effect.Error,
                effect.Message!);
        }

        PlatformDomainIdentity identity = PlatformIdentity(process);
        KernelResult deviceValidation = PlatformAuthority.ValidateDeviceLease(deviceLease, identity);
        if (!deviceValidation.IsSuccess)
        {
            return KernelResult<PlatformMmioLease>.Fail(
                deviceValidation.Error,
                deviceValidation.Message!);
        }

        CapabilityRights requiredCapabilityRights = CapabilityRights.Map;
        if ((access & PlatformMmioAccess.Read) != 0)
            requiredCapabilityRights |= CapabilityRights.Read;
        if ((access & PlatformMmioAccess.Write) != 0)
            requiredCapabilityRights |= CapabilityRights.Write;

        KernelResult<CapabilityDescriptorV1> capability = CapabilityAuthority.Validate(
            mmioCapabilityId,
            process.DomainId,
            subject.Generation,
            requiredCapabilityRights);
        if (!capability.IsSuccess)
        {
            return KernelResult<PlatformMmioLease>.Fail(
                capability.Error,
                capability.Message!);
        }

        CapabilityDescriptorV1 descriptor = capability.Value!;
        if (descriptor.ResourceKind != ResourceKind.MmioRegion ||
            !CapabilityResourceIds.TryParseMmioRegion(descriptor.ResourceId, out MmioRegionResourceId mmioResource))
        {
            return KernelResult<PlatformMmioLease>.Fail(
                KernelError.WrongCapabilityResource,
                "The local capability does not authorize a canonical semantic MMIO region.");
        }

        if (!string.Equals(
                mmioResource.DeviceResourceId,
                deviceLease.Device.ResourceId,
                StringComparison.Ordinal))
        {
            return KernelResult<PlatformMmioLease>.Fail(
                KernelError.WrongCapabilityResource,
                "The MMIO region capability belongs to a different semantic device resource.");
        }

        PlatformMmioRegionIdentity region = new PlatformMmioRegionIdentity(
            mmioResource.RegionResourceId,
            mmioResource.ByteLength);
        PlatformMmioRange range = new PlatformMmioRange(offset, length);
        PlatformAuthorityResult request = PlatformMmioLeaseContract.ValidateRequest(region, range, access);
        if (!request.IsSuccess)
        {
            return KernelResult<PlatformMmioLease>.Fail(
                KernelError.PlatformDenied,
                request.Message ?? "The requested MMIO range is invalid.");
        }

        KernelResult Authorize(Func<KernelResult> commit)
        {
            lock (_platformMemoryUseGate)
                return CommitSource(() => CapabilityAuthority.CommitMmioAdmission(mmioCapabilityId, process.DomainId,
                    subject.Generation, requiredCapabilityRights, descriptor.ResourceId, () =>
                    {
                        KernelResult<SingProcess> current = Processes.Resolve(subject);
                        if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                        if (!ReferenceEquals(current.Value, process))
                            return KernelResult.Fail(KernelError.StaleGeneration, "Exact MMIO process incarnation changed.");
                        KernelResult accepting = EnsureProcessAcceptsNewEffects(current.Value!);
                        if(accepting.IsSuccess && backing is not null)
                        {
                            if(!ReferenceEquals(backing.Original,backing) || backing.Use is not { } use || backing.Reservation?.IsActive!=true || backing.Backing?.ByteLength!=region.ByteLength)
                                return KernelResult.Fail(KernelError.StaleGeneration,"Original MMIO backing changed before admission/publication.");
                            KernelResult<RegionUseDescriptor> live =Regions.ValidateUse(use,backing.Owner);if(!live.IsSuccess)return KernelResult.Fail(live.Error,live.Message!);
                        }
                        return accepting.IsSuccess ? commit() : accepting;
                    }));
        }
        KernelResult<PlatformMmioLease> lease = PlatformAuthority.BindMmio(
            deviceLease,
            identity,
            mmioCapabilityId,
            region,
            range,
            access, Authorize, (candidate, commit) => Authorize(() =>
            {
                KernelResult result = commit();
                if (result.IsSuccess) TrackPlatformMmioLease(subject, candidate);
                return result;
            }), admissionConsumer: source is null && sourceCommit is null ? null : Authorize,backing:backing);
        return lease;
    }

    private KernelResult CommitControllerMmio((ProcessHandle Principal, CapabilityId Capability)? source, Func<KernelResult> commit)
    {
        if (source is not { } controller) return commit();
        KernelResult<SingProcess> principal = Processes.Resolve(controller.Principal);
        if (!principal.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, principal.Message!);
        KernelResult effect = EnsureProcessAcceptsNewEffects(principal.Value!);
        if (!effect.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, effect.Message!);
        return CapabilityAuthority.CommitSupervisorMmioBinding(controller.Capability, principal.Value!.DomainId,
            controller.Principal.Generation, commit);
    }

    public KernelResult RevokePlatformMmio(
        ProcessHandle subject,
        PlatformMmioLease lease)
    {
        KernelResult<SingProcess> resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult.Fail(resolved.Error, resolved.Message!);

        PlatformDomainIdentity identity = PlatformIdentity(resolved.Value!);
        KernelResult revoke = PlatformAuthority.RevokeMmio(lease, identity);
        if (revoke.IsSuccess)
        {
            UntrackPlatformMmioLease(lease);
            return PlatformAuthority.ConsumeOriginalMmioBacking(identity,Regions);
        }
        return revoke;
    }

    internal KernelResult CascadePlatformMmioCapabilityRevocation(CapabilityId capabilityId)
    {
        KernelResult? firstFailure = null;
        foreach (PlatformMmioLease lease in PlatformAuthority.BeginMmioCapabilityRevocation(capabilityId, id => CapabilityAuthority.DependsOnCapability(id, capabilityId)))
        {
            KernelResult revoke = PlatformAuthority.RevokeMmio(
                lease,
                lease.DeviceLease.DomainBinding.Subject);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            UntrackPlatformMmioLease(lease);
            KernelResult backing =PlatformAuthority.ConsumeOriginalMmioBacking(lease.DeviceLease.DomainBinding.Subject,Regions);
            if(!backing.IsSuccess)firstFailure ??= backing;
        }

        if (firstFailure is { } failed) return failed;
        lock (_platformMemoryUseGate)
        {
        if (_pendingPlatformMmioCapabilities.Any(id => CapabilityAuthority.DependsOnCapability(id, capabilityId)))
            return KernelResult.Fail(KernelError.PlatformBindingDraining, "MMIO admission must settle before capability closure.");
        }
        if (PlatformAuthority.HasUnresolvedMmioEffect(id => CapabilityAuthority.DependsOnCapability(id, capabilityId)))
            return KernelResult.Fail(KernelError.PlatformFaulted, "MMIO receipt/closure is unresolved; parent remains pinned.");
        return KernelResult.Ok();
    }

    private KernelResult AdvancePlatformMmioLeasesForDevice(PlatformDeviceLease deviceLease)
    {
        KernelResult originalMaps = PlatformAuthority.ResolveOriginalMmioMapsForCleanup(deviceLease.DomainBinding.Subject);
        if (!originalMaps.IsSuccess) return originalMaps;
        KernelResult? firstFailure = null;
        foreach (PlatformMmioLease lease in PlatformAuthority.ActiveMmioLeasesForDevice(deviceLease))
        {
            KernelResult revoke = PlatformAuthority.RevokeMmio(
                lease,
                lease.DeviceLease.DomainBinding.Subject);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            UntrackPlatformMmioLease(lease);
        }

        KernelResult consumed =PlatformAuthority.ConsumeOriginalMmioBacking(deviceLease.DomainBinding.Subject,Regions);
        return firstFailure ?? consumed;
    }

    private KernelResult AdvancePlatformMmioLeasesForProcess(
        SingProcess process,
        ProcessHandle handle)
    {
        PlatformDomainIdentity identity = PlatformIdentity(process);
        lock (_platformMemoryUseGate)
        {
            KernelResult rollback = Regions.ConsumeUnadmittedMmioBacking(new(process.DomainId, handle.Generation));
            if (!rollback.IsSuccess) return rollback;
        }
        KernelResult originalMaps = PlatformAuthority.ResolveOriginalMmioMapsForCleanup(identity);
        if (!originalMaps.IsSuccess) return originalMaps;
        _processPlatformMmioLeases.TryGetValue(handle,out List<PlatformMmioLease>? tracked);
        PlatformMmioLease[] leases = (tracked ?? []).Concat(PlatformAuthority.ActiveMmioLeasesForProcess(identity)).Distinct().ToArray();
        KernelResult? firstFailure = null;
        foreach (PlatformMmioLease lease in leases.ToArray())
        {
            if (PlatformAuthority.IsOriginalMmioClosureConsumed(lease,identity)) { UntrackPlatformMmioLease(lease); continue; }
            KernelResult revoke = PlatformAuthority.RevokeMmio(lease, identity);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            UntrackPlatformMmioLease(lease);
        }

        KernelResult consumed =PlatformAuthority.ConsumeOriginalMmioBacking(identity,Regions);
        return firstFailure ?? consumed;
    }

    private void TrackPlatformMmioLease(ProcessHandle process, PlatformMmioLease lease)
    {
        if (!_processPlatformMmioLeases.TryGetValue(process, out List<PlatformMmioLease>? leases))
        {
            leases = [];
            _processPlatformMmioLeases.Add(process, leases);
        }

        if (!leases.Any(existing => existing.LeaseId == lease.LeaseId))
            leases.Add(lease);
    }

    private void UntrackPlatformMmioLease(PlatformMmioLease lease)
    {
        lock (_platformMemoryUseGate)
        {
        foreach (KeyValuePair<ProcessHandle, List<PlatformMmioLease>> entry in _processPlatformMmioLeases.ToArray())
        {
            entry.Value.RemoveAll(existing => existing.LeaseId == lease.LeaseId);
            if (entry.Value.Count == 0)
                _processPlatformMmioLeases.Remove(entry.Key);
        }
        }
    }
}
