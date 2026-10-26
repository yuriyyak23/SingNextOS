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
        KernelEventEndpoint eventEndpoint) => BindPlatformInterruptCore(subject, deviceLease, irqCapabilityId, eventEndpoint, null);

    private KernelResult<PlatformIrqBinding> BindPlatformInterruptCore(ProcessHandle subject, PlatformDeviceLease deviceLease,
        CapabilityId irqCapabilityId, KernelEventEndpoint eventEndpoint,
        (ProcessHandle Principal, CapabilityId Capability)? controllerSource,
        Func<Func<KernelResult>, KernelResult>? sourceCommit = null)
    {
        KernelResult CommitSource(Func<KernelResult> commit) => sourceCommit is null
            ? CommitControllerIrq(controllerSource, commit) : sourceCommit(commit);
        KernelResult<SingProcess> resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult<PlatformIrqBinding>.Fail(resolved.Error, resolved.Message!);
        KernelResult effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
        if (!effect.IsSuccess)
            return KernelResult<PlatformIrqBinding>.Fail(effect.Error, effect.Message!);
        lock (_platformMemoryUseGate)
        {
            KernelResult pending = CommitSource(() =>
            {
                if (_pendingPlatformIrqCapabilities.Count == int.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "IRQ capability admission accounting is exhausted.");
                _pendingPlatformIrqCapabilities.Add(irqCapabilityId);
                return KernelResult.Ok();
            });
            if (!pending.IsSuccess) return KernelResult<PlatformIrqBinding>.Fail(pending.Error, pending.Message!);
        }
        try
        {
            KernelResult admitted;
            lock (_platformMemoryUseGate)
                admitted = CommitSource(() => _kernelEvents.BeginIrqBindingAdmission(subject, eventEndpoint));
            if (!admitted.IsSuccess)
                return KernelResult<PlatformIrqBinding>.Fail(admitted.Error, admitted.Message!);
            try { return BindPlatformInterruptWithEndpointAdmission(subject, deviceLease, irqCapabilityId, eventEndpoint, controllerSource, sourceCommit); }
            finally { _kernelEvents.EndIrqBindingAdmission(eventEndpoint); }
        }
        finally { lock (_platformMemoryUseGate) _pendingPlatformIrqCapabilities.Remove(irqCapabilityId); }
    }
    private KernelResult<PlatformIrqBinding> BindPlatformInterruptWithEndpointAdmission(
        ProcessHandle subject,
        PlatformDeviceLease deviceLease,
        CapabilityId irqCapabilityId,
        KernelEventEndpoint eventEndpoint, (ProcessHandle Principal, CapabilityId Capability)? controllerSource,
        Func<Func<KernelResult>, KernelResult>? sourceCommit)
    {
        KernelResult CommitSource(Func<KernelResult> commit) => sourceCommit is null
            ? CommitControllerIrq(controllerSource, commit) : sourceCommit(commit);
        KernelResult<SingProcess> resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformIrqBinding>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        SingProcess process = resolved.Value!;
        KernelResult effect = EnsureProcessAcceptsNewEffects(process);
        if (!effect.IsSuccess)
        {
            return KernelResult<PlatformIrqBinding>.Fail(
                effect.Error,
                effect.Message!);
        }

        KernelResult endpointValidation = _kernelEvents.Validate(subject, eventEndpoint);
        if (!endpointValidation.IsSuccess)
        {
            return KernelResult<PlatformIrqBinding>.Fail(
                endpointValidation.Error,
                endpointValidation.Message!);
        }

        PlatformDomainIdentity identity = PlatformIdentity(process);
        KernelResult deviceValidation = PlatformAuthority.ValidateDeviceLease(deviceLease, identity);
        if (!deviceValidation.IsSuccess)
        {
            return KernelResult<PlatformIrqBinding>.Fail(
                deviceValidation.Error,
                deviceValidation.Message!);
        }

        KernelResult<CapabilityDescriptorV1> capability = CapabilityAuthority.Validate(
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

        CapabilityDescriptorV1 descriptor = capability.Value!;
        if (descriptor.ResourceKind != ResourceKind.Irq ||
            !CapabilityResourceIds.TryParseIrq(descriptor.ResourceId, out IrqResourceId irqResource))
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

        PlatformInterruptSourceIdentity source = new PlatformInterruptSourceIdentity(
            irqResource.SourceResourceId,
            irqResource.Trigger switch
            {
                IrqTriggerMode.Edge => PlatformInterruptTrigger.Edge,
                IrqTriggerMode.Level => PlatformInterruptTrigger.Level,
                _ => throw new ArgumentOutOfRangeException(nameof(irqResource.Trigger)),
            });
        PlatformAuthorityResult request = PlatformIrqBindingContract.ValidateRequest(source);
        if (!request.IsSuccess)
        {
            return KernelResult<PlatformIrqBinding>.Fail(
                KernelError.PlatformDenied,
                request.Message ?? "The semantic interrupt source is invalid.");
        }

        KernelResult RevalidateSource()
        {
            lock (_platformMemoryUseGate)
            {
                KernelResult<SingProcess> current = Processes.Resolve(subject);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            if (!ReferenceEquals(current.Value, process))
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact IRQ process incarnation changed.");
                KernelResult currentEffect = EnsureProcessAcceptsNewEffects(current.Value!);
            if (!currentEffect.IsSuccess) return currentEffect;
                KernelResult<CapabilityDescriptorV1> currentCapability = CapabilityAuthority.Validate(irqCapabilityId,
                current.Value!.DomainId, subject.Generation, CapabilityRights.Signal);
            if (!currentCapability.IsSuccess)
                return KernelResult.Fail(currentCapability.Error, currentCapability.Message!);
            return CommitSource(() => _kernelEvents.CommitIrqBindingAdmission(subject, eventEndpoint, static () => KernelResult.Ok()));
            }
        }

        return PlatformAuthority.BindIrq(deviceLease, identity, irqCapabilityId, source, eventEndpoint,
            RevalidateSource, (candidate, commit) =>
            {
                lock (_platformMemoryUseGate)
                {
                    return CommitSource(() => CapabilityAuthority.CommitIrqBindingAdmission(irqCapabilityId,
                        process.DomainId, subject.Generation, descriptor.ResourceId, () =>
                        {
                            // Capability validation may call an injected clock: recheck the exact process afterward.
                            KernelResult<SingProcess> current = Processes.Resolve(subject);
                            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                            if (!ReferenceEquals(current.Value, process))
                                return KernelResult.Fail(KernelError.StaleGeneration, "Exact IRQ process incarnation changed.");
                            KernelResult currentEffect = EnsureProcessAcceptsNewEffects(current.Value!);
                            if (!currentEffect.IsSuccess) return currentEffect;
                            return _kernelEvents.CommitIrqBindingAdmission(subject, eventEndpoint, () =>
                            {
                                KernelResult published = commit();
                                if (published.IsSuccess) TrackPlatformIrqBinding(subject, candidate);
                                return published;
                            });
                        }));
                }
            }, admissionConsumer: begin =>
            {
                lock (_platformMemoryUseGate) return CommitSource(begin);
            }, reserveEndpointSource: () => _kernelEvents.ReserveIrqSource(subject, eventEndpoint));
    }

    private KernelResult CommitControllerIrq((ProcessHandle Principal, CapabilityId Capability)? source, Func<KernelResult> commit)
    {
        if (source is not { } controller) return commit();
        KernelResult<SingProcess> principal = Processes.Resolve(controller.Principal);
        if (!principal.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, principal.Message!);
        KernelResult effect = EnsureProcessAcceptsNewEffects(principal.Value!);
        if (!effect.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, effect.Message!);
        return CapabilityAuthority.CommitSupervisorIrqBinding(controller.Capability, principal.Value!.DomainId, controller.Principal.Generation, commit);
    }

    private KernelResult<KernelEventEndpoint> CreateComponentIrqEndpoint(ProcessHandle subject,
        (ProcessHandle Principal, CapabilityId Capability)? source,
        Func<Func<KernelResult>, KernelResult>? sourceCommit = null)
    {
        if (sourceCommit is not null)
            return CommitRestoreValue(sourceCommit, () => CreateKernelEventEndpoint(subject));
        if (source is not { } controller) return CreateKernelEventEndpoint(subject);
        lock (_platformMemoryUseGate)
        {
            KernelResult<SingProcess> principal = Processes.Resolve(controller.Principal);
            if (!principal.IsSuccess) return KernelResult<KernelEventEndpoint>.Fail(KernelError.SupervisorDenied, principal.Message!);
            KernelResult effect = EnsureProcessAcceptsNewEffects(principal.Value!);
            if (!effect.IsSuccess) return KernelResult<KernelEventEndpoint>.Fail(KernelError.SupervisorDenied, effect.Message!);
            return CapabilityAuthority.CommitSupervisorIrqEndpoint(controller.Capability, principal.Value!.DomainId,
                controller.Principal.Generation, () => CreateKernelEventEndpoint(subject));
        }
    }
    public KernelResult<PlatformInterruptPollResult> PollPlatformInterrupt(ProcessHandle subject, PlatformIrqBinding binding)
    {
        KernelResult<SingProcess> resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess) return KernelResult<PlatformInterruptPollResult>.Fail(resolved.Error, resolved.Message!);
        KernelResult effect = EnsureProcessAcceptsNewEffects(resolved.Value!);
        if (!effect.IsSuccess) return KernelResult<PlatformInterruptPollResult>.Fail(effect.Error, effect.Message!);
        KernelResult endpoint = _kernelEvents.Validate(subject, binding.EventEndpoint);
        if (!endpoint.IsSuccess) return KernelResult<PlatformInterruptPollResult>.Fail(endpoint.Error, endpoint.Message!);
        KernelResult admitted = PlatformAuthority.BeginIrqDelivery(binding, PlatformIdentity(resolved.Value!), out Action? release, out Action? fault, out Func<PlatformProviderInterruptDeliverySequence, Func<CapabilityId, Func<KernelResult>, KernelResult>, KernelResult>? complete);
        if (!admitted.IsSuccess) return KernelResult<PlatformInterruptPollResult>.Fail(admitted.Error, admitted.Message!);
        try { return PollPlatformInterruptAdmitted(subject, binding, complete!, fault!); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            fault!();
            return KernelResult<PlatformInterruptPollResult>.Fail(KernelError.PlatformFaulted,
                $"IRQ delivery lost continuity; exact binding remains pinned: {exception.Message}");
        }
        finally { release!(); }
    }
    private KernelResult<PlatformInterruptPollResult> PollPlatformInterruptAdmitted(
        ProcessHandle subject,
        PlatformIrqBinding binding,
        Func<PlatformProviderInterruptDeliverySequence, Func<CapabilityId, Func<KernelResult>, KernelResult>, KernelResult> complete,
        Action fault)
    {
        KernelResult<SingProcess> resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
        {
            return KernelResult<PlatformInterruptPollResult>.Fail(
                resolved.Error,
                resolved.Message!);
        }

        SingProcess process = resolved.Value!;
        if (process.State == ProcessState.Exiting)
        {
            return KernelResult<PlatformInterruptPollResult>.Fail(
                KernelError.InvalidTransition,
                "An Exiting process cannot accept new interrupt delivery.");
        }

        KernelResult endpointValidation = _kernelEvents.Validate(subject, binding.EventEndpoint);
        if (!endpointValidation.IsSuccess)
        {
            return KernelResult<PlatformInterruptPollResult>.Fail(
                endpointValidation.Error,
                endpointValidation.Message!);
        }

        PlatformDomainIdentity identity = PlatformIdentity(process);
        KernelResult validation = PlatformAuthority.ValidateIrqBinding(binding, identity);
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
                KernelResult<SingProcess> current = Processes.Resolve(subject);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, process))
                    return KernelResult.Fail(KernelError.StaleGeneration, "IRQ delivery process incarnation changed.");
                KernelResult active = EnsureProcessAcceptsNewEffects(current.Value!);
                if (!active.IsSuccess) return active;
                KernelResult<CapabilityDescriptorV1> source = CapabilityAuthority.Validate(sourceId, process.DomainId, subject.Generation, CapabilityRights.Signal);
                if (!source.IsSuccess) return KernelResult.Fail(source.Error, source.Message!);
                if (source.Value!.ResourceKind != ResourceKind.Irq ||
                    !CapabilityResourceIds.TryParseIrq(source.Value.ResourceId, out IrqResourceId irq) ||
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
                        KernelResult<SingProcess> exact = Processes.Resolve(subject);
                        if (!exact.IsSuccess) return KernelResult.Fail(exact.Error, exact.Message!);
                        if (!ReferenceEquals(exact.Value, process))
                            return KernelResult.Fail(KernelError.StaleGeneration, "IRQ delivery process changed during final admission.");
                        KernelResult accepting = EnsureProcessAcceptsNewEffects(exact.Value!);
                        if (!accepting.IsSuccess) return accepting;
                        KernelResult endpoint = _kernelEvents.Validate(subject, binding.EventEndpoint);
                        return endpoint.IsSuccess ? admit() : endpoint;
                    });
            }
        }
        KernelResult<PlatformIrqDeliveryEvidence> observed = PlatformAuthority.PollIrq(binding, identity, AuthorizeSource);
        if (!observed.IsSuccess)
        {
            return KernelResult<PlatformInterruptPollResult>.Fail(
                observed.Error,
                observed.Message!);
        }

        PlatformIrqDeliveryEvidence delivery = observed.Value!;
        if (!delivery.DeliveryAvailable)
        {
            return KernelResult<PlatformInterruptPollResult>.Ok(
                new PlatformInterruptPollResult(false, null));
        }

        KernelResult<KernelEvent> staged = _kernelEvents.Stage(
            subject,
            binding.EventEndpoint,
            KernelEventClass.ExternalSignal,
            binding.Source.ResourceId, out Func<KernelResult>? rollbackOriginal);
        if (!staged.IsSuccess)
        {
            // No provider completion occurs: the exact external delivery remains pending.
            return KernelResult<PlatformInterruptPollResult>.Fail(
                staged.Error,
                staged.Message!);
        }

        bool localConsumed = false;
        try
        {
            KernelResult completed = complete(delivery.ProviderSequence, AuthorizeSource);
            if (!completed.IsSuccess)
            {
                KernelResult rollback = rollbackOriginal!();
                localConsumed = rollback.IsSuccess;
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

            KernelResult<KernelEvent> committed = _kernelEvents.CommitExact(subject, staged.Value!);
            localConsumed = committed.IsSuccess;
            if (!committed.IsSuccess)
            {
                fault();
                return KernelResult<PlatformInterruptPollResult>.Fail(
                    KernelError.PlatformFaulted,
                    "Interrupt delivery completed but the exact staged local event could not be committed.");
            }

            return KernelResult<PlatformInterruptPollResult>.Ok(
                new PlatformInterruptPollResult(true, committed.Value!));
        }
        finally
        {
            // Consume only the original local reservation; provider containment remains independent.
            if (!localConsumed) _ = rollbackOriginal?.Invoke();
        }
    }

    public KernelResult RevokePlatformInterrupt(
        ProcessHandle subject,
        PlatformIrqBinding binding)
    {
        KernelResult<SingProcess> resolved = Processes.Resolve(subject);
        if (!resolved.IsSuccess)
            return KernelResult.Fail(resolved.Error, resolved.Message!);

        PlatformDomainIdentity identity = PlatformIdentity(resolved.Value!);
        KernelResult revoke = PlatformAuthority.RevokeIrq(binding, identity);
        if (revoke.IsSuccess)
            UntrackPlatformIrqBinding(binding);
        return revoke;
    }

    internal KernelResult CascadePlatformIrqCapabilityRevocation(CapabilityId capabilityId)
    {
        KernelResult original = PlatformAuthority.ResolveOriginalIrqBindsForCapabilityCleanup(id => CapabilityAuthority.DependsOnCapability(id,capabilityId));
        if (!original.IsSuccess) return original;
        KernelResult? firstFailure = null;
        foreach (PlatformIrqBinding binding in PlatformAuthority.BeginIrqCapabilityRevocation(capabilityId, id => CapabilityAuthority.DependsOnCapability(id, capabilityId)))
        {
            KernelResult revoke = PlatformAuthority.RevokeIrq(
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
        lock (_platformMemoryUseGate)
        {
        if (_pendingPlatformIrqCapabilities.Any(id => CapabilityAuthority.DependsOnCapability(id, capabilityId)))
            return KernelResult.Fail(KernelError.PlatformBindingDraining,
                "An admitted IRQ effect must settle before capability closure.");
        }
        IReadOnlyList<object> endpointSources = _kernelEvents.SnapshotIrqSources();
        if (PlatformAuthority.HasUnresolvedIrqEffect(id => CapabilityAuthority.DependsOnCapability(id, capabilityId), endpointSources))
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "An IRQ effect lacks an exact closure receipt; parent remains pinned.");
        return KernelResult.Ok();
    }

    private KernelResult AdvancePlatformIrqBindingsForDevice(PlatformDeviceLease deviceLease)
    {
        KernelResult original = PlatformAuthority.ResolveOriginalIrqBindsForCleanup(deviceLease.DomainBinding.Subject);
        if (!original.IsSuccess) return original;
        KernelResult? firstFailure = null;
        foreach (PlatformIrqBinding binding in PlatformAuthority.ActiveIrqBindingsForDevice(deviceLease))
        {
            KernelResult revoke = PlatformAuthority.RevokeIrq(
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
        foreach (PlatformIrqBinding binding in PlatformAuthority.ActiveIrqBindingsForEndpoint(endpoint))
        {
            KernelResult revoke = PlatformAuthority.RevokeIrq(
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
        PlatformDomainIdentity identity = PlatformIdentity(process);
        KernelResult original = PlatformAuthority.ResolveOriginalIrqBindsForCleanup(identity);
        if (!original.IsSuccess) return original;
        IReadOnlyList<PlatformIrqBinding> bindings = PlatformAuthority.ActiveIrqBindingsForProcess(identity);
        KernelResult? firstFailure = null;
        foreach (PlatformIrqBinding binding in bindings)
        {
            KernelResult revoke = PlatformAuthority.RevokeIrq(binding, identity);
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
        if (!_processPlatformIrqBindings.TryGetValue(process, out List<PlatformIrqBinding>? bindings))
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
        foreach (KeyValuePair<ProcessHandle, List<PlatformIrqBinding>> entry in _processPlatformIrqBindings.ToArray())
        {
            entry.Value.RemoveAll(existing => existing.BindingId == binding.BindingId);
            if (entry.Value.Count == 0)
                _processPlatformIrqBindings.Remove(entry.Key);
        }
        }
    }
}
