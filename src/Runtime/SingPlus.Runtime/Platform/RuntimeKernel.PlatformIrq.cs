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
    private readonly List<CapabilityId> _pendingPlatformIrqCapabilities = [];

    public KernelResult<PlatformIrqBinding> BindPlatformInterrupt(
        ProcessHandle subject,
        PlatformDeviceLease deviceLease,
        CapabilityId irqCapabilityId,
        KernelEventEndpoint eventEndpoint)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult<PlatformIrqBinding>.Fail(resolved.Error, resolved.Message!);
        var effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
        if (!effect.IsSuccess)
            return KernelResult<PlatformIrqBinding>.Fail(effect.Error, effect.Message!);
        lock (_platformMemoryUseGate)
        {
            if (_pendingPlatformIrqCapabilities.Count == int.MaxValue)
                return KernelResult<PlatformIrqBinding>.Fail(KernelError.CapacityExhausted,
                    "IRQ capability admission accounting is exhausted.");
            _pendingPlatformIrqCapabilities.Add(irqCapabilityId);
        }
        try
        {
            var admitted = _kernelEvents.BeginIrqBindingAdmission(subject, eventEndpoint);
            if (!admitted.IsSuccess)
                return KernelResult<PlatformIrqBinding>.Fail(admitted.Error, admitted.Message!);
            try { return BindPlatformInterruptWithEndpointAdmission(subject, deviceLease, irqCapabilityId, eventEndpoint); }
            finally { _kernelEvents.EndIrqBindingAdmission(eventEndpoint); }
        }
        finally { lock (_platformMemoryUseGate) _pendingPlatformIrqCapabilities.Remove(irqCapabilityId); }
    }
    private KernelResult<PlatformIrqBinding> BindPlatformInterruptWithEndpointAdmission(
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

        KernelResult RevalidateSource()
        {
            var current = Processes.Resolve(subject);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            if (!ReferenceEquals(current.Value, process))
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact IRQ process incarnation changed.");
            var currentEffect = EnsureProcessAcceptsNewEffects(current.Value!);
            if (!currentEffect.IsSuccess) return currentEffect;
            var currentCapability = CapabilityAuthority.Validate(irqCapabilityId,
                current.Value!.DomainId, subject.Generation, CapabilityRights.Signal);
            if (!currentCapability.IsSuccess)
                return KernelResult.Fail(currentCapability.Error, currentCapability.Message!);
            return _kernelEvents.CommitIrqBindingAdmission(subject, eventEndpoint, static () => KernelResult.Ok());
        }

        return PlatformAuthority.BindIrq(deviceLease, identity, irqCapabilityId, source, eventEndpoint,
            RevalidateSource, (candidate, commit) =>
            {
                lock (_platformMemoryUseGate)
                {
                    return CapabilityAuthority.CommitIrqBindingAdmission(irqCapabilityId,
                        process.DomainId, subject.Generation, descriptor.ResourceId, () =>
                        {
                            // Capability validation may call an injected clock: recheck the exact process afterward.
                            var current = Processes.Resolve(subject);
                            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                            if (!ReferenceEquals(current.Value, process))
                                return KernelResult.Fail(KernelError.StaleGeneration, "Exact IRQ process incarnation changed.");
                            var currentEffect = EnsureProcessAcceptsNewEffects(current.Value!);
                            if (!currentEffect.IsSuccess) return currentEffect;
                            return _kernelEvents.CommitIrqBindingAdmission(subject, eventEndpoint, () =>
                            {
                                var published = commit();
                                if (published.IsSuccess) TrackPlatformIrqBinding(subject, candidate);
                                return published;
                            });
                        });
                }
            });
    }
    public KernelResult<PlatformInterruptPollResult> PollPlatformInterrupt(ProcessHandle subject, PlatformIrqBinding binding)
    {
        var resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess) return KernelResult<PlatformInterruptPollResult>.Fail(resolved.Error, resolved.Message!);
        var effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
        if (!effect.IsSuccess) return KernelResult<PlatformInterruptPollResult>.Fail(effect.Error, effect.Message!);
        var endpoint = _kernelEvents.Validate(subject, binding.EventEndpoint);
        if (!endpoint.IsSuccess) return KernelResult<PlatformInterruptPollResult>.Fail(endpoint.Error, endpoint.Message!);
        var admitted = PlatformAuthority.BeginIrqDelivery(binding, PlatformIdentity(resolved.Value!));
        if (!admitted.IsSuccess) return KernelResult<PlatformInterruptPollResult>.Fail(admitted.Error, admitted.Message!);
        try { return PollPlatformInterruptAdmitted(subject, binding); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            PlatformAuthority.FaultIrqDelivery(binding);
            return KernelResult<PlatformInterruptPollResult>.Fail(KernelError.PlatformFaulted,
                $"IRQ delivery lost continuity; exact binding remains pinned: {exception.Message}");
        }
        finally { PlatformAuthority.EndIrqDelivery(binding); }
    }
    private KernelResult<PlatformInterruptPollResult> PollPlatformInterruptAdmitted(
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

        KernelResult AuthorizeSource(CapabilityId sourceId, Func<KernelResult> admit)
        {
            lock (_platformMemoryUseGate)
            {
                var current = Processes.Resolve(subject);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, process))
                    return KernelResult.Fail(KernelError.StaleGeneration, "IRQ delivery process incarnation changed.");
                var active = EnsureProcessAcceptsNewEffects(current.Value!);
                if (!active.IsSuccess) return active;
                var source = CapabilityAuthority.Validate(sourceId, process.DomainId, subject.Generation, CapabilityRights.Signal);
                if (!source.IsSuccess) return KernelResult.Fail(source.Error, source.Message!);
                if (source.Value!.ResourceKind != ResourceKind.Irq ||
                    !CapabilityResourceIds.TryParseIrq(source.Value.ResourceId, out var irq) ||
                    irq.DeviceResourceId != binding.DeviceLease.Device.ResourceId || irq.SourceResourceId != binding.Source.ResourceId ||
                    (irq.Trigger == IrqTriggerMode.Edge ? PlatformInterruptTrigger.Edge : PlatformInterruptTrigger.Level) != binding.Source.Trigger)
                    return KernelResult.Fail(KernelError.WrongCapabilityResource, "IRQ delivery source does not match exact binding.");
                current = Processes.Resolve(subject);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, process))
                    return KernelResult.Fail(KernelError.StaleGeneration, "IRQ delivery process incarnation changed during source validation.");
                active = EnsureProcessAcceptsNewEffects(current.Value!);
                if (!active.IsSuccess) return active;
                return CapabilityAuthority.CommitIrqBindingAdmission(sourceId, process.DomainId,
                    subject.Generation, source.Value.ResourceId, () =>
                    {
                        var exact = Processes.Resolve(subject);
                        if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
                        if (!ReferenceEquals(exact.Value, process))
                            return KernelResult.Fail(KernelError.StaleGeneration, "IRQ delivery process changed during final admission.");
                        var accepting = EnsureProcessAcceptsNewEffects(exact.Value!);
                        if (!accepting.IsSuccess) return accepting;
                        var endpoint = _kernelEvents.Validate(subject, binding.EventEndpoint);
                        return endpoint.IsSuccess ? admit() : endpoint;
                    });
            }
        }
        var observed = PlatformAuthority.PollIrq(binding, identity, AuthorizeSource);
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
            delivery.ProviderSequence, AuthorizeSource);
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
            PlatformAuthority.FaultIrqDelivery(binding);
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
        foreach (var binding in PlatformAuthority.BeginIrqCapabilityRevocation(capabilityId, id => CapabilityAuthority.DependsOnCapability(id, capabilityId)))
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

        if (firstFailure is { } failed) return failed;
        if (_pendingPlatformIrqCapabilities.Any(id => CapabilityAuthority.DependsOnCapability(id, capabilityId)))
            return KernelResult.Fail(KernelError.PlatformBindingDraining,
                "An admitted IRQ effect must settle before capability closure.");
        if (PlatformAuthority.HasUnresolvedIrqEffect(id => CapabilityAuthority.DependsOnCapability(id, capabilityId)))
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "An IRQ effect lacks an exact closure receipt; parent remains pinned.");
        return KernelResult.Ok();
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
        PlatformIrqBinding[] bindings;
        lock (_platformMemoryUseGate)
        {
            if (!_processPlatformIrqBindings.TryGetValue(handle, out var tracked) || tracked.Count == 0)
                return KernelResult.Ok();
            bindings = tracked.ToArray();
        }

        var identity = PlatformIdentity(process);
        KernelResult? firstFailure = null;
        foreach (var binding in bindings)
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
        lock (_platformMemoryUseGate)
        {
        if (!_processPlatformIrqBindings.TryGetValue(process, out var bindings))
        {
            bindings = [];
            _processPlatformIrqBindings.Add(process, bindings);
        }

        if (!bindings.Any(existing => existing.BindingId == binding.BindingId))
            bindings.Add(binding);
        }
    }

    private void UntrackPlatformIrqBinding(PlatformIrqBinding binding)
    {
        lock (_platformMemoryUseGate)
        {
        foreach (var entry in _processPlatformIrqBindings.ToArray())
        {
            entry.Value.RemoveAll(existing => existing.BindingId == binding.BindingId);
            if (entry.Value.Count == 0)
                _processPlatformIrqBindings.Remove(entry.Key);
        }
        }
    }
}
