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
        Func<Func<KernelResult>, KernelResult>? sourceCommit = null)
    {
        KernelResult CommitSource(Func<KernelResult> commit) => sourceCommit is null
            ? CommitControllerMmio(source, commit) : sourceCommit(commit);
        lock (_platformMemoryUseGate)
        {
            var pending = CommitSource(() =>
            {
                if (_pendingPlatformMmioCapabilities.Count == int.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "MMIO admission accounting is exhausted.");
                _pendingPlatformMmioCapabilities.Add(mmioCapabilityId);
                return KernelResult.Ok();
            });
            if (!pending.IsSuccess) return KernelResult<PlatformMmioLease>.Fail(pending.Error, pending.Message!);
        }
        try { return BindPlatformMmioAdmitted(subject, deviceLease, mmioCapabilityId, offset, length, access, source, sourceCommit); }
        finally { lock (_platformMemoryUseGate) _pendingPlatformMmioCapabilities.Remove(mmioCapabilityId); }
    }

    private KernelResult<PlatformMmioLease> BindPlatformMmioAdmitted(
        ProcessHandle subject,
        PlatformDeviceLease deviceLease,
        CapabilityId mmioCapabilityId,
        long offset,
        long length,
        PlatformMmioAccess access, (ProcessHandle Principal, CapabilityId Capability)? source,
        Func<Func<KernelResult>, KernelResult>? sourceCommit)
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

        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformMmioLease>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        var process = resolved.Value!;
        var effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
        {
            return KernelResult<PlatformMmioLease>.Fail(
                effect.Error,
                effect.Message!);
        }

        var identity = PlatformIdentity(process);
        var deviceValidation = PlatformAuthority.ValidateDeviceLease(deviceLease, identity);
        if (!deviceValidation.IsSuccess)
        {
            return KernelResult<PlatformMmioLease>.Fail(
                deviceValidation.Error,
                deviceValidation.Message!);
        }

        var requiredCapabilityRights = CapabilityRights.Map;
        if ((access & PlatformMmioAccess.Read) != 0)
            requiredCapabilityRights |= CapabilityRights.Read;
        if ((access & PlatformMmioAccess.Write) != 0)
            requiredCapabilityRights |= CapabilityRights.Write;

        var capability = CapabilityAuthority.Validate(
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

        var descriptor = capability.Value!;
        if (descriptor.ResourceKind != ResourceKind.MmioRegion ||
            !CapabilityResourceIds.TryParseMmioRegion(descriptor.ResourceId, out var mmioResource))
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

        var region = new PlatformMmioRegionIdentity(
            mmioResource.RegionResourceId,
            mmioResource.ByteLength);
        var range = new PlatformMmioRange(offset, length);
        var request = PlatformMmioLeaseContract.ValidateRequest(region, range, access);
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
                        var current = Processes.Resolve(subject);
                        if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                        if (!ReferenceEquals(current.Value, process))
                            return KernelResult.Fail(KernelError.StaleGeneration, "Exact MMIO process incarnation changed.");
                        var accepting = EnsureProcessAcceptsNewEffects(current.Value!);
                        return accepting.IsSuccess ? commit() : accepting;
                    }));
        }
        var lease = PlatformAuthority.BindMmio(
            deviceLease,
            identity,
            mmioCapabilityId,
            region,
            range,
            access, Authorize, (candidate, commit) => Authorize(() =>
            {
                var result = commit();
                if (result.IsSuccess) TrackPlatformMmioLease(subject, candidate);
                return result;
            }), admissionConsumer: source is null && sourceCommit is null ? null : Authorize);
        return lease;
    }

    private KernelResult CommitControllerMmio((ProcessHandle Principal, CapabilityId Capability)? source, Func<KernelResult> commit)
    {
        if (source is not { } controller) return commit();
        var principal = Processes.Resolve(controller.Principal);
        if (!principal.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, principal.Message!);
        var effect = EnsureProcessAcceptsNewEffects(principal.Value!);
        if (!effect.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, effect.Message!);
        return CapabilityAuthority.CommitSupervisorMmioBinding(controller.Capability, principal.Value!.DomainId,
            controller.Principal.Generation, commit);
    }

    public KernelResult RevokePlatformMmio(
        ProcessHandle subject,
        PlatformMmioLease lease)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult.Fail(resolved.Error, resolved.Message!);

        var identity = PlatformIdentity(resolved.Value!);
        var revoke = PlatformAuthority.RevokeMmio(lease, identity);
        if (revoke.IsSuccess)
            UntrackPlatformMmioLease(lease);
        return revoke;
    }

    internal KernelResult CascadePlatformMmioCapabilityRevocation(CapabilityId capabilityId)
    {
        KernelResult? firstFailure = null;
        foreach (var lease in PlatformAuthority.BeginMmioCapabilityRevocation(capabilityId, id => CapabilityAuthority.DependsOnCapability(id, capabilityId)))
        {
            var revoke = PlatformAuthority.RevokeMmio(
                lease,
                lease.DeviceLease.DomainBinding.Subject);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            UntrackPlatformMmioLease(lease);
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
        KernelResult? firstFailure = null;
        foreach (var lease in PlatformAuthority.ActiveMmioLeasesForDevice(deviceLease))
        {
            var revoke = PlatformAuthority.RevokeMmio(
                lease,
                lease.DeviceLease.DomainBinding.Subject);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            UntrackPlatformMmioLease(lease);
        }

        return firstFailure ?? KernelResult.Ok();
    }

    private KernelResult AdvancePlatformMmioLeasesForProcess(
        SingProcess process,
        ProcessHandle handle)
    {
        var identity = PlatformIdentity(process);
        var leases = PlatformAuthority.ActiveMmioLeasesForProcess(identity);
        KernelResult? firstFailure = null;
        foreach (var lease in leases.ToArray())
        {
            var revoke = PlatformAuthority.RevokeMmio(lease, identity);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            UntrackPlatformMmioLease(lease);
        }

        return firstFailure ?? KernelResult.Ok();
    }

    private void TrackPlatformMmioLease(ProcessHandle process, PlatformMmioLease lease)
    {
        if (!_processPlatformMmioLeases.TryGetValue(process, out var leases))
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
        foreach (var entry in _processPlatformMmioLeases.ToArray())
        {
            entry.Value.RemoveAll(existing => existing.LeaseId == lease.LeaseId);
            if (entry.Value.Count == 0)
                _processPlatformMmioLeases.Remove(entry.Key);
        }
        }
    }
}
