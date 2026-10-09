using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private readonly Dictionary<ProcessHandle, List<PlatformDmaGrant>> _processPlatformDmaGrants = [];

    public KernelResult<PlatformDmaGrant> BindPlatformDma(
        ProcessHandle subject, PlatformDeviceLease deviceLease, PlatformOwnedRegionSliceMapping mapping,
        long offset, long length, PlatformDmaDirection direction)
    {
        PlatformDomainIdentity identity;
        lock (_platformMemoryUseGate)
        {
            var resolved = Processes.Resolve(subject);
            if (!resolved.IsSuccess) return KernelResult<PlatformDmaGrant>.Fail(resolved.Error, resolved.Message!);
            identity = PlatformIdentity(resolved.Value!);
        }
        return PlatformAuthority.BindDmaGrant(deviceLease, mapping, identity,
            new PlatformDmaRange(offset, length), direction, () =>
            {
                var current = Processes.Resolve(subject);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                var accepts = EnsureProcessAcceptsNewEffects(current.Value!);
                if (!accepts.IsSuccess) return accepts;
                return Regions.ValidatePlatformMappingRegionUsability(mapping.Mapping.Region,
                    new RegionOwner(current.Value!.DomainId, subject.Generation));
            }, commit => { lock (_platformMemoryUseGate) return commit(); },
            (candidate, retain) =>
            {
                // Trusted local mutation inside ownerCommit only.
                if (retain) TrackPlatformDmaGrant(subject, candidate);
                else UntrackPlatformDmaGrant(candidate);
            }, (deviceSource, mappingSource, commit) =>
            {
                var current = Processes.Resolve(subject);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                return CapabilityAuthority.CommitDeviceLeaseAdmission(deviceSource, current.Value!.DomainId,
                    subject.Generation, ToDeviceCapabilityRights(deviceLease.Rights), deviceLease.Device.ResourceId, () =>
                    {
                        var rights = CapabilityRights.Map;
                        if ((mapping.Mapping.Access & PlatformMemoryAccess.Read) != 0) rights |= CapabilityRights.Read;
                        if ((mapping.Mapping.Access & PlatformMemoryAccess.Write) != 0) rights |= CapabilityRights.Write;
                        var source = CapabilityAuthority.Validate(mappingSource, current.Value.DomainId, subject.Generation, rights);
                        if (!source.IsSuccess) return KernelResult.Fail(source.Error, source.Message!);
                        if (source.Value!.ResourceKind != ResourceKind.MemoryRegion ||
                            source.Value.ResourceId != CapabilityResourceIds.MemoryRegion(mapping.Mapping.Region.RegionId))
                            return KernelResult.Fail(KernelError.WrongCapabilityResource, "DMA mapping source changed.");
                        return commit();
                    });
            });
    }
    public KernelResult RevokePlatformDma(
        ProcessHandle subject,
        PlatformDmaGrant grant)
    {
        PlatformDomainIdentity identity;
        lock (_platformMemoryUseGate)
        {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult.Fail(resolved.Error, resolved.Message!);

        identity = PlatformIdentity(resolved.Value!);
        }
        var revoke = PlatformAuthority.RevokeDmaGrant(grant, identity);
        if (revoke.IsSuccess)
            lock (_platformMemoryUseGate) UntrackPlatformDmaGrant(grant);
        return revoke;
    }

    private KernelResult AdvancePlatformDmaGrantsForDevice(PlatformDeviceLease deviceLease)
    {
        KernelResult? firstFailure = null;
        foreach (var grant in PlatformAuthority.ActiveDmaGrantsForDevice(deviceLease))
        {
            var revoke = PlatformAuthority.RevokeDmaGrant(
                grant,
                grant.DeviceLease.DomainBinding.Subject);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            lock (_platformMemoryUseGate) UntrackPlatformDmaGrant(grant);
        }

        return firstFailure ?? KernelResult.Ok();
    }

    private KernelResult AdvancePlatformDmaGrantsForMapping(PlatformRegionMapping mapping)
    {
        KernelResult? firstFailure = null;
        foreach (var grant in PlatformAuthority.ActiveDmaGrantsForMapping(mapping))
        {
            var revoke = PlatformAuthority.RevokeDmaGrant(
                grant,
                grant.DeviceLease.DomainBinding.Subject);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            lock (_platformMemoryUseGate) UntrackPlatformDmaGrant(grant);
        }

        return firstFailure ?? KernelResult.Ok();
    }

    private KernelResult AdvancePlatformDmaGrantsForProcess(
        SingProcess process,
        ProcessHandle handle)
    {
        PlatformDmaGrant[] grants;
        lock (_platformMemoryUseGate)
        {
            if (!_processPlatformDmaGrants.TryGetValue(handle, out var tracked) || tracked.Count == 0)
                return KernelResult.Ok();
            grants = tracked.ToArray();
        }

        var identity = PlatformIdentity(process);
        KernelResult? firstFailure = null;
        foreach (var grant in grants)
        {
            var revoke = PlatformAuthority.RevokeDmaGrant(grant, identity);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            lock (_platformMemoryUseGate) UntrackPlatformDmaGrant(grant);
        }

        return firstFailure ?? KernelResult.Ok();
    }

    private void TrackPlatformDmaGrant(ProcessHandle process, PlatformDmaGrant grant)
    {
        if (!_processPlatformDmaGrants.TryGetValue(process, out var grants))
        {
            grants = [];
            _processPlatformDmaGrants.Add(process, grants);
        }

        if (!grants.Any(existing => existing.GrantId == grant.GrantId))
            grants.Add(grant);
    }

    private void UntrackPlatformDmaGrant(PlatformDmaGrant grant)
    {
        foreach (var entry in _processPlatformDmaGrants.ToArray())
        {
            entry.Value.RemoveAll(existing => existing.GrantId == grant.GrantId);
            if (entry.Value.Count == 0)
                _processPlatformDmaGrants.Remove(entry.Key);
        }
        UntrackComponentDriverDma(grant);
    }
}
