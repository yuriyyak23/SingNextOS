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
                else _=UntrackPlatformDmaGrant(candidate);
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
        if(!HasOriginalDmaCopyPayloadRoute(grant))return KernelResult.Fail(KernelError.PlatformFaulted,"Original DMA payload route changed; reserved views retained.");
        }
        var revoke = PlatformAuthority.RevokeDmaGrant(grant, identity);
        if (revoke.IsSuccess)
            lock (_platformMemoryUseGate)
            {
                if(!HasOriginalDmaCopyPayloadRoute(grant))return KernelResult.Fail(KernelError.PlatformFaulted,"Original DMA payload route changed after Close; reserved views retained.");
                return UntrackPlatformDmaGrant(grant);
            }
        return revoke;
    }

    private KernelResult AdvancePlatformDmaGrantsForDevice(PlatformDeviceLease deviceLease)
    {
        KernelResult? firstFailure = null;
        var grants = PlatformAuthority.ActiveDmaGrantsForDevice(deviceLease)
            .Concat(RetainedDmaCopyBackingGrants(grant => grant.DeviceLease == deviceLease)).Distinct().ToArray();
        foreach (var grant in grants)
        {
            var revoke = RetirePlatformDmaGrantAndOriginalPeer(
                grant,
                grant.DeviceLease.DomainBinding.Subject);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            lock (_platformMemoryUseGate)
            {var reclaimed=UntrackPlatformDmaGrant(grant);if(!reclaimed.IsSuccess)firstFailure??=reclaimed;}
        }

        return firstFailure ?? KernelResult.Ok();
    }

    private KernelResult AdvancePlatformDmaGrantsForMapping(PlatformRegionMapping mapping)
    {
        KernelResult? firstFailure = null;
        var grants = PlatformAuthority.ActiveDmaGrantsForMapping(mapping)
            .Concat(RetainedDmaCopyBackingGrants(grant => grant.Mapping.Mapping == mapping)).Distinct().ToArray();
        foreach (var grant in grants)
        {
            var revoke = RetirePlatformDmaGrantAndOriginalPeer(
                grant,
                grant.DeviceLease.DomainBinding.Subject);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            lock (_platformMemoryUseGate)
            {var reclaimed=UntrackPlatformDmaGrant(grant);if(!reclaimed.IsSuccess)firstFailure??=reclaimed;}
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
            _processPlatformDmaGrants.TryGetValue(handle, out var tracked);
            var identityForBacking = PlatformIdentity(process);
            grants = (tracked ?? []).Concat(PlatformAuthority.ActiveDmaGrantsForSubject(identityForBacking))
                .Concat(RetainedDmaCopyBackingGrants(grant => grant.DeviceLease.DomainBinding.Subject == identityForBacking)).Distinct().ToArray();
        }

        var identity = PlatformIdentity(process);
        KernelResult? firstFailure = null;
        foreach (var grant in grants)
        {
            if (PlatformAuthority.IsOriginalDmaGrantClosureConsumed(grant, identity) && RetainedDmaCopyBackingGrants(original => original == grant).Length == 0)
            {
                lock (_platformMemoryUseGate)
            {var reclaimed=UntrackPlatformDmaGrant(grant);if(!reclaimed.IsSuccess)firstFailure??=reclaimed;}
                continue;
            }
            var revoke = RetirePlatformDmaGrantAndOriginalPeer(grant, identity);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            lock (_platformMemoryUseGate)
            {var reclaimed=UntrackPlatformDmaGrant(grant);if(!reclaimed.IsSuccess)firstFailure??=reclaimed;}
        }

        return firstFailure ?? KernelResult.Ok();
    }

    private PlatformDmaGrant[] RetainedDmaCopyBackingGrants(Func<PlatformDmaGrant,bool> matches)
    {
        lock (_platformMemoryUseGate)
            // Candidates only: malformed/foreign routes must reach the scoped
            // retirement guard rather than disappear from cleanup inventory.
            return _dmaCopyPayloads.Values
                .SelectMany(payload => new[] { payload.SourceGrant,payload.DestinationGrant }).Where(matches).Distinct().ToArray();
    }

    private KernelResult RetirePlatformDmaGrantAndOriginalPeer(PlatformDmaGrant grant,PlatformDomainIdentity identity)
    {
        lock(_platformMemoryUseGate)
            if(!HasOriginalDmaCopyPayloadRoute(grant))return KernelResult.Fail(KernelError.PlatformFaulted,"Original DMA payload route changed; reserved views retained.");
        var pair = PlatformAuthority.OriginalCopyRetirementGrants(grant,identity);
        if (!pair.IsSuccess) return KernelResult.Fail(pair.Error,pair.Message!);
        KernelResult? firstFailure = null;
        foreach (var original in pair.Value!)
        {
            lock(_platformMemoryUseGate)
                if(!HasOriginalDmaCopyPayloadRoute(original))
                {firstFailure??=KernelResult.Fail(KernelError.PlatformFaulted,"Original DMA payload route changed before peer Close.");continue;}
            var subject = original.DeviceLease.DomainBinding.Subject;
            if (!PlatformAuthority.IsOriginalDmaGrantClosureConsumed(original,subject))
            {
                var closed = PlatformAuthority.RevokeDmaGrant(original,subject);
                if (!closed.IsSuccess) { firstFailure ??= closed; continue; }
            }
            lock (_platformMemoryUseGate)
            {
                if(!HasOriginalDmaCopyPayloadRoute(original))
                {firstFailure??=KernelResult.Fail(KernelError.PlatformFaulted,"Original DMA payload route changed after peer Close; reserved views retained.");continue;}
                var reclaimed=UntrackPlatformDmaGrant(original);if(!reclaimed.IsSuccess)firstFailure??=reclaimed;
            }
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

    private KernelResult UntrackPlatformDmaGrant(PlatformDmaGrant grant)
    {
        if(!HasOriginalDmaCopyPayloadRoute(grant))return KernelResult.Fail(KernelError.PlatformFaulted,"Original DMA payload route changed during local reclaim.");
        foreach (var entry in _dmaCopyPayloads.ToArray())
        {
            var payload = entry.Value;
            if(payload.SourceGrant!=grant && payload.DestinationGrant!=grant)continue;
            if (!IsOriginalDmaCopyPayload(payload) || entry.Key!=payload.SourceGrant.GrantId || _dmaCopyPayloadCalls.Contains(payload.SourceGrant.GrantId) || _dmaCopyPayloadCalls.Contains(payload.DestinationGrant.GrantId)) continue;
            if (!PlatformAuthority.IsOriginalDmaGrantClosureConsumed(payload.SourceGrant,payload.SourceGrant.DeviceLease.DomainBinding.Subject) ||
                !PlatformAuthority.IsOriginalDmaGrantClosureConsumed(payload.DestinationGrant,payload.DestinationGrant.DeviceLease.DomainBinding.Subject)) continue;
            try{payload.Dispose();}
            catch(InvalidOperationException exception){return KernelResult.Fail(KernelError.PlatformFaulted,$"Original DMA reserved views retained for retry: {exception.Message}");}
            _dmaCopyPayloads.Remove(entry.Key); // Both views ended; native closure alone did not reclaim storage.
        }
        foreach (var entry in _processPlatformDmaGrants.ToArray())
        {
            entry.Value.RemoveAll(existing => existing.GrantId == grant.GrantId);
            if (entry.Value.Count == 0)
                _processPlatformDmaGrants.Remove(entry.Key);
        }
        UntrackComponentDriverDma(grant);
        return KernelResult.Ok();
    }
}
