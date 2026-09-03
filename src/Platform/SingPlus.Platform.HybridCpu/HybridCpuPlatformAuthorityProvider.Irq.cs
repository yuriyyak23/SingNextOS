using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformIrqBindingProvider
{
    private sealed class IrqBindingRecord(
        PlatformProviderIrqBinding binding,
        NeutralInterruptLease hybridCpuLease)
    {
        public PlatformProviderIrqBinding Binding { get; } = binding;
        public NeutralInterruptLease HybridCpuLease { get; } = hybridCpuLease;
        public bool Revoked { get; set; }
    }

    private readonly Dictionary<PlatformProviderIrqBindingId, IrqBindingRecord> _irqBindings = [];
    private ulong _nextProviderIrqBindingId = 1;

    public PlatformAuthorityResult<PlatformProviderIrqBinding> BindInterrupt(
        PlatformProviderDeviceLease deviceLease,
        PlatformInterruptSourceIdentity source)
    {
        var device = ValidateProviderDeviceForInterrupt(deviceLease);
        if (!device.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(
                device.Status,
                device.Message ?? "The provider device lease is not live for interrupt binding.");
        }

        var request = PlatformIrqBindingContract.ValidateRequest(source);
        if (!request.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(
                request.Status,
                request.Message ?? "The semantic interrupt source is invalid.");
        }

        if (_irqBindings.Values.Any(record =>
                !record.Revoked &&
                record.Binding.DeviceLease == deviceLease &&
                record.Binding.Source == source))
        {
            return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(
                PlatformAuthorityStatus.Denied,
                "The exact semantic interrupt source already has a live provider binding.");
        }

        var deviceRecord = _deviceLeases[deviceLease.LeaseId];
        var neutralSource = new NeutralInterruptSourceIdentity(
            source.ResourceId,
            ToNeutralInterruptTrigger(source.Trigger));
        var external = _runtime.BindInterrupt(
            deviceRecord.HybridCpuLease,
            neutralSource);
        if (!external.IsBound)
            return FromNeutralInterruptBindFailure(external);

        if (external.Lease.DeviceLease != deviceRecord.HybridCpuLease ||
            external.Lease.Source != neutralSource)
        {
            _ = _runtime.CloseInterrupt(external.Lease);
            return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(
                PlatformAuthorityStatus.Faulted,
                "HybridCPU returned interrupt authority that does not match the exact provider request.");
        }

        var binding = new PlatformProviderIrqBinding(
            new PlatformProviderIrqBindingId(NextNonZero(ref _nextProviderIrqBindingId)),
            new PlatformProviderLeaseGeneration(1),
            deviceRecord.Lease,
            source);
        _irqBindings.Add(
            binding.BindingId,
            new IrqBindingRecord(binding, external.Lease));
        return PlatformAuthorityResult<PlatformProviderIrqBinding>.Ok(binding);
    }

    public PlatformAuthorityResult<PlatformInterruptDeliveryObservation> PollInterrupt(
        PlatformProviderIrqBinding binding)
    {
        var validation = ValidateProviderInterruptBinding(binding);
        if (!validation.IsSuccess)
        {
            return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(
                validation.Status,
                validation.Message ?? "The provider interrupt binding is not live.");
        }

        var record = _irqBindings[binding.BindingId];
        var external = _runtime.PollInterrupt(record.HybridCpuLease);
        if (!external.IsObserved)
            return FromNeutralInterruptPollFailure(external);

        if (external.Lease != record.HybridCpuLease)
        {
            return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(
                PlatformAuthorityStatus.Faulted,
                "HybridCPU returned interrupt delivery evidence for a different neutral binding.");
        }

        return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Ok(
            new PlatformInterruptDeliveryObservation(
                record.Binding,
                external.DeliveryAvailable,
                external.DeliveryAvailable
                    ? new PlatformProviderInterruptDeliverySequence(external.Sequence.Value)
                    : default));
    }

    public PlatformAuthorityResult CompleteInterruptDelivery(
        PlatformProviderIrqBinding binding,
        PlatformProviderInterruptDeliverySequence sequence)
    {
        var validation = ValidateProviderInterruptBinding(binding);
        if (!validation.IsSuccess) return validation;

        if (sequence.Value == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "Provider interrupt completion requires an exact nonzero delivery sequence.");
        }

        var record = _irqBindings[binding.BindingId];
        var external = _runtime.CompleteInterruptDelivery(
            record.HybridCpuLease,
            new NeutralInterruptDeliverySequence(sequence.Value));
        return external.Decision switch
        {
            NeutralInterruptCompleteDecision.Completed => PlatformAuthorityResult.Ok(),
            NeutralInterruptCompleteDecision.NoPendingDelivery => PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                external.Reason),
            NeutralInterruptCompleteDecision.WrongSequence => PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                external.Reason),
            NeutralInterruptCompleteDecision.Revoked => PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                external.Reason),
            NeutralInterruptCompleteDecision.Stale => PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                external.Reason),
            NeutralInterruptCompleteDecision.NotFound => PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                external.Reason),
            _ => PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                external.Reason),
        };
    }

    public PlatformAuthorityResult RevokeInterrupt(PlatformProviderIrqBinding binding)
    {
        var validation = ValidateProviderInterruptBindingIdentity(binding);
        if (!validation.IsSuccess) return validation;

        var record = _irqBindings[binding.BindingId];
        if (record.Revoked)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The provider interrupt binding has already been revoked.");
        }

        var external = _runtime.CloseInterrupt(record.HybridCpuLease);
        switch (external.Decision)
        {
            case NeutralInterruptCloseDecision.Closed:
                record.Revoked = true;
                return PlatformAuthorityResult.Ok();

            case NeutralInterruptCloseDecision.Revoked:
                record.Revoked = true;
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Revoked,
                    external.Reason);

            case NeutralInterruptCloseDecision.Stale:
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Stale,
                    external.Reason);

            case NeutralInterruptCloseDecision.NotFound:
            case NeutralInterruptCloseDecision.Faulted:
            default:
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Faulted,
                    external.Reason);
        }
    }

    internal bool HasActiveProviderInterruptBindings(PlatformProviderDeviceLease deviceLease) =>
        _irqBindings.Values.Any(record =>
            !record.Revoked && record.Binding.DeviceLease == deviceLease);

    private PlatformAuthorityResult ValidateProviderDeviceForInterrupt(
        PlatformProviderDeviceLease deviceLease)
    {
        if (!_deviceLeases.TryGetValue(deviceLease.LeaseId, out var record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider device lease does not exist.");
        }

        if (record.Lease.Generation != deviceLease.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider device lease generation is stale.");
        }

        if (record.Lease != deviceLease)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The provider device lease identity is malformed.");
        }

        if (record.Revoked)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The provider device lease has been revoked.");
        }

        if ((deviceLease.Rights & PlatformDeviceRights.Configure) == 0)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "Provider interrupt binding requires Configure authority on the exact device lease.");
        }

        return PlatformAuthorityResult.Ok();
    }

    private PlatformAuthorityResult ValidateProviderInterruptBinding(
        PlatformProviderIrqBinding binding)
    {
        var identity = ValidateProviderInterruptBindingIdentity(binding);
        if (!identity.IsSuccess) return identity;

        var record = _irqBindings[binding.BindingId];
        if (record.Revoked)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Revoked,
                "The provider interrupt binding has been revoked.");
        }

        return ValidateProviderDeviceForInterrupt(binding.DeviceLease);
    }

    private PlatformAuthorityResult ValidateProviderInterruptBindingIdentity(
        PlatformProviderIrqBinding binding)
    {
        if (!_irqBindings.TryGetValue(binding.BindingId, out var record))
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Denied,
                "The provider interrupt binding does not exist.");
        }

        if (record.Binding.Generation != binding.Generation)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Stale,
                "The provider interrupt binding generation is stale.");
        }

        if (record.Binding != binding)
        {
            return PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Faulted,
                "The provider interrupt binding identity is malformed.");
        }

        return PlatformAuthorityResult.Ok();
    }

    private static NeutralInterruptTrigger ToNeutralInterruptTrigger(
        PlatformInterruptTrigger trigger) =>
        trigger switch
        {
            PlatformInterruptTrigger.Edge => NeutralInterruptTrigger.Edge,
            PlatformInterruptTrigger.Level => NeutralInterruptTrigger.Level,
            _ => throw new ArgumentOutOfRangeException(nameof(trigger)),
        };

    private static PlatformAuthorityResult<PlatformProviderIrqBinding> FromNeutralInterruptBindFailure(
        NeutralInterruptBindResult result)
    {
        var status = result.Decision switch
        {
            NeutralInterruptBindDecision.InvalidSource => PlatformAuthorityStatus.Denied,
            NeutralInterruptBindDecision.InsufficientDeviceRights => PlatformAuthorityStatus.Denied,
            NeutralInterruptBindDecision.AlreadyBound => PlatformAuthorityStatus.Denied,
            NeutralInterruptBindDecision.Revoked => PlatformAuthorityStatus.Revoked,
            NeutralInterruptBindDecision.Stale => PlatformAuthorityStatus.Stale,
            NeutralInterruptBindDecision.NotFound => PlatformAuthorityStatus.Faulted,
            _ => PlatformAuthorityStatus.Faulted,
        };
        return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(status, result.Reason);
    }

    private static PlatformAuthorityResult<PlatformInterruptDeliveryObservation> FromNeutralInterruptPollFailure(
        NeutralInterruptPollResult result)
    {
        var status = result.Decision switch
        {
            NeutralInterruptPollDecision.Revoked => PlatformAuthorityStatus.Revoked,
            NeutralInterruptPollDecision.Stale => PlatformAuthorityStatus.Stale,
            NeutralInterruptPollDecision.NotFound => PlatformAuthorityStatus.Faulted,
            _ => PlatformAuthorityStatus.Faulted,
        };
        return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(status, result.Reason);
    }
}
