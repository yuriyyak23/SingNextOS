using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private readonly Dictionary<ProcessHandle, List<PlatformDeviceLease>> _processPlatformDeviceLeases = [];

    public KernelResult<PlatformDeviceLease> BindPlatformDevice(
        ProcessHandle subject,
        PlatformDomainBinding binding,
        CapabilityId deviceCapabilityId,
        PlatformDeviceRights rights)
    {
        var requestValidation = PlatformDeviceLeaseContract.ValidateRequest(
            new PlatformDeviceIdentity("device-validation-placeholder"),
            rights);
        if (!requestValidation.IsSuccess)
        {
            return KernelResult<PlatformDeviceLease>.Fail(
                KernelError.PlatformDenied,
                requestValidation.Message ?? "The requested platform device rights are invalid.");
        }

        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformDeviceLease>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        var process = resolved.Value!;
        var effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
        {
            return KernelResult<PlatformDeviceLease>.Fail(
                effect.Error,
                effect.Message!);
        }

        var identity = PlatformIdentity(process);
        var bindingValidation = PlatformAuthority.ValidateDomain(binding, identity);
        if (!bindingValidation.IsSuccess)
        {
            return KernelResult<PlatformDeviceLease>.Fail(
                bindingValidation.Error,
                bindingValidation.Message!);
        }

        var capabilityRights = ToCapabilityRights(rights);
        var capability = CapabilityAuthority.Validate(
            deviceCapabilityId,
            process.DomainId,
            subject.Generation,
            capabilityRights);
        if (!capability.IsSuccess)
        {
            return KernelResult<PlatformDeviceLease>.Fail(
                capability.Error,
                capability.Message!);
        }

        var descriptor = capability.Value!;
        if (descriptor.ResourceKind != ResourceKind.Device)
        {
            return KernelResult<PlatformDeviceLease>.Fail(
                KernelError.WrongCapabilityResource,
                "The local capability does not authorize a semantic device resource.");
        }

        var device = new PlatformDeviceIdentity(descriptor.ResourceId);
        var exactRequest = PlatformDeviceLeaseContract.ValidateRequest(device, rights);
        if (!exactRequest.IsSuccess)
        {
            return KernelResult<PlatformDeviceLease>.Fail(
                KernelError.PlatformDenied,
                exactRequest.Message ?? "The local device capability resource identity is invalid.");
        }

        var lease = PlatformAuthority.BindDevice(
            binding,
            identity,
            deviceCapabilityId,
            device,
            rights);
        if (lease.IsSuccess)
            TrackPlatformDeviceLease(subject, lease.Value!);
        return lease;
    }

    public KernelResult RevokePlatformDevice(
        ProcessHandle subject,
        PlatformDeviceLease lease)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult.Fail(resolved.Error, resolved.Message!);

        var dma = AdvancePlatformDmaGrantsForDevice(lease);
        if (!dma.IsSuccess) return dma;

        var irq = AdvancePlatformIrqBindingsForDevice(lease);
        if (!irq.IsSuccess) return irq;

        var mmio = AdvancePlatformMmioLeasesForDevice(lease);
        if (!mmio.IsSuccess) return mmio;

        var identity = PlatformIdentity(resolved.Value!);
        var revoke = PlatformAuthority.RevokeDevice(lease, identity);
        if (revoke.IsSuccess)
            UntrackPlatformDeviceLease(lease);
        return revoke;
    }

    internal KernelResult CascadePlatformDeviceCapabilityRevocation(CapabilityId capabilityId)
    {
        KernelResult? firstFailure = null;
        foreach (var lease in PlatformAuthority.BeginDeviceCapabilityRevocation(capabilityId))
        {
            var dma = AdvancePlatformDmaGrantsForDevice(lease);
            if (!dma.IsSuccess)
            {
                firstFailure ??= dma;
                continue;
            }

            var irq = AdvancePlatformIrqBindingsForDevice(lease);
            if (!irq.IsSuccess)
            {
                firstFailure ??= irq;
                continue;
            }

            var mmio = AdvancePlatformMmioLeasesForDevice(lease);
            if (!mmio.IsSuccess)
            {
                firstFailure ??= mmio;
                continue;
            }

            var revoke = PlatformAuthority.RevokeDevice(
                lease,
                lease.DomainBinding.Subject);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            UntrackPlatformDeviceLease(lease);
        }

        return firstFailure ?? KernelResult.Ok();
    }

    private KernelResult AdvancePlatformDeviceLeasesForProcess(
        SingProcess process,
        ProcessHandle handle)
    {
        var dma = AdvancePlatformDmaGrantsForProcess(process, handle);
        if (!dma.IsSuccess) return dma;

        var irq = AdvancePlatformIrqBindingsForProcess(process, handle);
        if (!irq.IsSuccess) return irq;

        var mmio = AdvancePlatformMmioLeasesForProcess(process, handle);
        if (!mmio.IsSuccess) return mmio;

        if (!_processPlatformDeviceLeases.TryGetValue(handle, out var leases) || leases.Count == 0)
            return KernelResult.Ok();

        var identity = PlatformIdentity(process);
        KernelResult? firstFailure = null;
        foreach (var lease in leases.ToArray())
        {
            var revoke = PlatformAuthority.RevokeDevice(lease, identity);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            UntrackPlatformDeviceLease(lease);
        }

        return firstFailure ?? KernelResult.Ok();
    }

    private void TrackPlatformDeviceLease(
        ProcessHandle process,
        PlatformDeviceLease lease)
    {
        if (!_processPlatformDeviceLeases.TryGetValue(process, out var leases))
        {
            leases = [];
            _processPlatformDeviceLeases.Add(process, leases);
        }

        if (!leases.Any(existing => existing.LeaseId == lease.LeaseId))
            leases.Add(lease);
    }

    private void UntrackPlatformDeviceLease(PlatformDeviceLease lease)
    {
        foreach (var entry in _processPlatformDeviceLeases.ToArray())
        {
            entry.Value.RemoveAll(existing => existing.LeaseId == lease.LeaseId);
            if (entry.Value.Count == 0)
                _processPlatformDeviceLeases.Remove(entry.Key);
        }
    }

    private static CapabilityRights ToCapabilityRights(PlatformDeviceRights rights)
    {
        var result = CapabilityRights.None;
        if ((rights & PlatformDeviceRights.Read) != 0)
            result |= CapabilityRights.Read;
        if ((rights & PlatformDeviceRights.Write) != 0)
            result |= CapabilityRights.Write;
        if ((rights & PlatformDeviceRights.Configure) != 0)
            result |= CapabilityRights.Configure;
        return result;
    }
}
