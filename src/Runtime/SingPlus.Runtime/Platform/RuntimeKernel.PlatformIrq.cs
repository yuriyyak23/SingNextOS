using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public readonly record struct PlatformInterruptPollResult(
    bool DeliveryAvailable,
    KernelEvent? Event);

public sealed partial class RuntimeKernel
{
    private readonly Dictionary<ProcessHandle, List<PlatformIrqBinding>> _processPlatformIrqBindings = [];

    public KernelResult<PlatformIrqBinding> BindPlatformInterrupt(
        ProcessHandle subject,
        PlatformDeviceLease deviceLease,
        CapabilityId irqCapabilityId,
        KernelEventEndpoint eventEndpoint)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformIrqBinding>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        var process = resolved.Value!;
        var effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
        {
            return KernelResult<PlatformIrqBinding>.Fail(
                effect.Error,
                effect.Message!);
        }

        var endpointValidation = _kernelEvents.Validate(subject, eventEndpoint);
        if (!endpointValidation.IsSuccess)
        {
            return KernelResult<PlatformIrqBinding>.Fail(
                endpointValidation.Error,
                endpointValidation.Message!);
        }

        var identity = PlatformIdentity(process);
        var deviceValidation = PlatformAuthority.ValidateDeviceLease(deviceLease, identity);
        if (!deviceValidation.IsSuccess)
        {
            return KernelResult<PlatformIrqBinding>.Fail(
                deviceValidation.Error,
                deviceValidation.Message!);
        }

        var capability = CapabilityAuthority.Validate(
            irqCapabilityId,
            process.DomainId,
            subject.Generation,
            CapabilityRights.Signal);
        if (!capability.IsSuccess)
        {
            return KernelResult<PlatformIrqBinding>.Fail(
                capability.Error,
                capability.Message!);
        }

        var descriptor = capability.Value!;
        if (descriptor.ResourceKind != ResourceKind.Irq ||
            !CapabilityResourceIds.TryParseIrq(descriptor.ResourceId, out var irqResource))
        {
            return KernelResult<PlatformIrqBinding>.Fail(
                KernelError.WrongCapabilityResource,
                "The local capability does not authorize a canonical semantic interrupt source.");
        }

        if (!string.Equals(
                irqResource.DeviceResourceId,
                deviceLease.Device.ResourceId,
                StringComparison.Ordinal))
        {
            return KernelResult<PlatformIrqBinding>.Fail(
                KernelError.WrongCapabilityResource,
                "The interrupt capability belongs to a different semantic device resource.");
        }

        var source = new PlatformInterruptSourceIdentity(
            irqResource.SourceResourceId,
            irqResource.Trigger switch
            {
                IrqTriggerMode.Edge => PlatformInterruptTrigger.Edge,
                IrqTriggerMode.Level => PlatformInterruptTrigger.Level,
                _ => throw new ArgumentOutOfRangeException(nameof(irqResource.Trigger)),
            });
        var request = PlatformIrqBindingContract.ValidateRequest(source);
        if (!request.IsSuccess)
        {
            return KernelResult<PlatformIrqBinding>.Fail(
                KernelError.PlatformDenied,
                request.Message ?? "The semantic interrupt source is invalid.");
        }

        var binding = PlatformAuthority.BindIrq(
            deviceLease,
            identity,
            irqCapabilityId,
            source,
            eventEndpoint);
        if (binding.IsSuccess)
            TrackPlatformIrqBinding(subject, binding.Value!);
        return binding;
    }

    public KernelResult<PlatformInterruptPollResult> PollPlatformInterrupt(
        ProcessHandle subject,
        PlatformIrqBinding binding)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformInterruptPollResult>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        var process = resolved.Value!;
        if (process.State == ProcessState.Exiting)
        {
            return KernelResult<PlatformInterruptPollResult>.Fail(
                KernelError.InvalidTransition,
                "An Exiting process cannot accept new interrupt delivery.");
        }

        var endpointValidation = _kernelEvents.Validate(subject, binding.EventEndpoint);
        if (!endpointValidation.IsSuccess)
        {
            return KernelResult<PlatformInterruptPollResult>.Fail(
                endpointValidation.Error,
                endpointValidation.Message!);
        }

        var identity = PlatformIdentity(process);
        var validation = PlatformAuthority.ValidateIrqBinding(binding, identity);
        if (!validation.IsSuccess)
        {
            return KernelResult<PlatformInterruptPollResult>.Fail(
                validation.Error,
                validation.Message!);
        }

        var observed = PlatformAuthority.PollIrq(binding, identity);
        if (!observed.IsSuccess)
        {
            return KernelResult<PlatformInterruptPollResult>.Fail(
                observed.Error,
                observed.Message!);
        }

        var delivery = observed.Value!;
        if (!delivery.DeliveryAvailable)
        {
            return KernelResult<PlatformInterruptPollResult>.Ok(
                new PlatformInterruptPollResult(false, null));
        }

        var staged = _kernelEvents.Stage(
            subject,
            binding.EventEndpoint,
            KernelEventClass.ExternalSignal,
            binding.Source.ResourceId);
        if (!staged.IsSuccess)
        {
            // No provider completion occurs: the exact external delivery remains pending.
            return KernelResult<PlatformInterruptPollResult>.Fail(
                staged.Error,
                staged.Message!);
        }

        var completed = PlatformAuthority.CompleteIrqDelivery(
            binding,
            identity,
            delivery.ProviderSequence);
        if (!completed.IsSuccess)
        {
            var rollback = _kernelEvents.RollbackExact(subject, staged.Value!);
            if (!rollback.IsSuccess)
            {
                return KernelResult<PlatformInterruptPollResult>.Fail(
                    KernelError.PlatformFaulted,
                    "Interrupt completion failed and the exact staged local event could not be rolled back.");
            }

            return KernelResult<PlatformInterruptPollResult>.Fail(
                completed.Error,
                completed.Message!);
        }

        var committed = _kernelEvents.CommitExact(subject, staged.Value!);
        if (!committed.IsSuccess)
        {
            return KernelResult<PlatformInterruptPollResult>.Fail(
                KernelError.PlatformFaulted,
                "Interrupt delivery completed but the exact staged local event could not be committed.");
        }

        return KernelResult<PlatformInterruptPollResult>.Ok(
            new PlatformInterruptPollResult(true, committed.Value!));
    }

    public KernelResult RevokePlatformInterrupt(
        ProcessHandle subject,
        PlatformIrqBinding binding)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult.Fail(resolved.Error, resolved.Message!);

        var identity = PlatformIdentity(resolved.Value!);
        var revoke = PlatformAuthority.RevokeIrq(binding, identity);
        if (revoke.IsSuccess)
            UntrackPlatformIrqBinding(binding);
        return revoke;
    }

    internal KernelResult CascadePlatformIrqCapabilityRevocation(CapabilityId capabilityId)
    {
        KernelResult? firstFailure = null;
        foreach (var binding in PlatformAuthority.BeginIrqCapabilityRevocation(capabilityId))
        {
            var revoke = PlatformAuthority.RevokeIrq(
                binding,
                binding.DeviceLease.DomainBinding.Subject);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            UntrackPlatformIrqBinding(binding);
        }

        return firstFailure ?? KernelResult.Ok();
    }

    private KernelResult AdvancePlatformIrqBindingsForDevice(PlatformDeviceLease deviceLease)
    {
        KernelResult? firstFailure = null;
        foreach (var binding in PlatformAuthority.ActiveIrqBindingsForDevice(deviceLease))
        {
            var revoke = PlatformAuthority.RevokeIrq(
                binding,
                binding.DeviceLease.DomainBinding.Subject);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            UntrackPlatformIrqBinding(binding);
        }

        return firstFailure ?? KernelResult.Ok();
    }

    private KernelResult AdvancePlatformIrqBindingsForEndpoint(KernelEventEndpoint endpoint)
    {
        KernelResult? firstFailure = null;
        foreach (var binding in PlatformAuthority.ActiveIrqBindingsForEndpoint(endpoint))
        {
            var revoke = PlatformAuthority.RevokeIrq(
                binding,
                binding.DeviceLease.DomainBinding.Subject);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            UntrackPlatformIrqBinding(binding);
        }

        return firstFailure ?? KernelResult.Ok();
    }

    private KernelResult AdvancePlatformIrqBindingsForProcess(
        SingProcess process,
        ProcessHandle handle)
    {
        if (!_processPlatformIrqBindings.TryGetValue(handle, out var bindings) || bindings.Count == 0)
            return KernelResult.Ok();

        var identity = PlatformIdentity(process);
        KernelResult? firstFailure = null;
        foreach (var binding in bindings.ToArray())
        {
            var revoke = PlatformAuthority.RevokeIrq(binding, identity);
            if (!revoke.IsSuccess)
            {
                firstFailure ??= revoke;
                continue;
            }

            UntrackPlatformIrqBinding(binding);
        }

        return firstFailure ?? KernelResult.Ok();
    }

    private void TrackPlatformIrqBinding(
        ProcessHandle process,
        PlatformIrqBinding binding)
    {
        if (!_processPlatformIrqBindings.TryGetValue(process, out var bindings))
        {
            bindings = [];
            _processPlatformIrqBindings.Add(process, bindings);
        }

        if (!bindings.Any(existing => existing.BindingId == binding.BindingId))
            bindings.Add(binding);
    }

    private void UntrackPlatformIrqBinding(PlatformIrqBinding binding)
    {
        foreach (var entry in _processPlatformIrqBindings.ToArray())
        {
            entry.Value.RemoveAll(existing => existing.BindingId == binding.BindingId);
            if (entry.Value.Count == 0)
                _processPlatformIrqBindings.Remove(entry.Key);
        }
    }
}
