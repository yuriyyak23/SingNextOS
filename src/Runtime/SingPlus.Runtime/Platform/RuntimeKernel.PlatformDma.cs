using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private readonly Dictionary<ProcessHandle, List<PlatformDmaGrant>> _processPlatformDmaGrants = [];

    public KernelResult<PlatformDmaGrant> BindPlatformDma(
        ProcessHandle subject,
        PlatformDeviceLease deviceLease,
        PlatformOwnedRegionSliceMapping mapping,
        long offset,
        long length,
        PlatformDmaDirection direction)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformDmaGrant>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        var process = resolved.Value!;
        var effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
        {
            return KernelResult<PlatformDmaGrant>.Fail(
                effect.Error,
                effect.Message!);
        }

        var identity = PlatformIdentity(process);
        var device = PlatformAuthority.ValidateDeviceLease(deviceLease, identity);
        if (!device.IsSuccess)
        {
            return KernelResult<PlatformDmaGrant>.Fail(
                device.Error,
                device.Message!);
        }

        var exactMapping = PlatformAuthority.ValidateExactMapping(mapping, identity);
        if (!exactMapping.IsSuccess)
        {
            return KernelResult<PlatformDmaGrant>.Fail(
                exactMapping.Error,
                exactMapping.Message!);
        }

        var grant = PlatformAuthority.BindDmaGrant(
            deviceLease,
            mapping,
            identity,
            new PlatformDmaRange(offset, length),
            direction);
        if (grant.IsSuccess)
            TrackPlatformDmaGrant(subject, grant.Value!);
        return grant;
    }

    public KernelResult RevokePlatformDma(
        ProcessHandle subject,
        PlatformDmaGrant grant)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult.Fail(resolved.Error, resolved.Message!);

        var identity = PlatformIdentity(resolved.Value!);
        var revoke = PlatformAuthority.RevokeDmaGrant(grant, identity);
        if (revoke.IsSuccess)
            UntrackPlatformDmaGrant(grant);
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

            UntrackPlatformDmaGrant(grant);
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

            UntrackPlatformDmaGrant(grant);
        }

        return firstFailure ?? KernelResult.Ok();
    }

    private KernelResult AdvancePlatformDmaGrantsForProcess(
        SingProcess process,
        ProcessHandle handle)
    {
        if (!_processPlatformDmaGrants.TryGetValue(handle, out var grants) || grants.Count == 0)
            return KernelResult.Ok();

        var identity = PlatformIdentity(process);
        KernelResult? firstFailure = null;
        foreach (var grant in grants.ToArray())
        {
            var revoke = PlatformAuthority.RevokeDmaGrant(grant, identity);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            UntrackPlatformDmaGrant(grant);
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
    }
}
