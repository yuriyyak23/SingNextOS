using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public readonly record struct PlatformIrqBindingId(ulong Value);
public readonly record struct PlatformIrqBindingGeneration(ulong Value);

public readonly record struct PlatformIrqBinding(
    PlatformIrqBindingId BindingId,
    PlatformIrqBindingGeneration Generation,
    PlatformDeviceLease DeviceLease,
    PlatformInterruptSourceIdentity Source,
    KernelEventEndpoint EventEndpoint);

internal readonly record struct PlatformIrqDeliveryEvidence(
    bool DeliveryAvailable,
    PlatformProviderInterruptDeliverySequence ProviderSequence);

public sealed partial class PlatformAuthorityBridge
{
    private sealed class IrqBindingRecord(
        PlatformIrqBinding binding,
        PlatformProviderIrqBinding providerBinding,
        CapabilityId authorityCapabilityId)
    {
        public PlatformIrqBinding Binding { get; } = binding;
        public PlatformProviderIrqBinding ProviderBinding { get; } = providerBinding;
        public CapabilityId AuthorityCapabilityId { get; } = authorityCapabilityId;
        public bool LocalAuthorizationRevoked { get; set; }
        public bool PlatformClosed { get; set; }
    }

    private readonly Dictionary<PlatformIrqBindingId, IrqBindingRecord> _irqBindings = [];
    private ulong _nextIrqBindingId = 1;

    internal KernelResult<PlatformIrqBinding> BindIrq(
        PlatformDeviceLease deviceLease,
        PlatformDomainIdentity expectedSubject,
        CapabilityId authorityCapabilityId,
        PlatformInterruptSourceIdentity source,
        KernelEventEndpoint eventEndpoint)
    {
        var deviceValidation = ValidateDeviceLease(deviceLease, expectedSubject);
        if (!deviceValidation.IsSuccess)
        {
            return KernelResult<PlatformIrqBinding>.Fail(
                deviceValidation.Error,
                deviceValidation.Message!);
        }

        var requestValidation = PlatformIrqBindingContract.ValidateRequest(source);
        if (!requestValidation.IsSuccess)
        {
            return KernelResult<PlatformIrqBinding>.Fail(
                KernelError.PlatformDenied,
                requestValidation.Message ?? "The platform interrupt request is invalid.");
        }

        if ((deviceLease.Rights & PlatformDeviceRights.Configure) == 0)
        {
            return KernelResult<PlatformIrqBinding>.Fail(
                KernelError.InsufficientRights,
                "The platform device lease does not carry Configure authority required for interrupt routing.");
        }

        if (_provider is not IPlatformIrqBindingProvider irqProvider)
        {
            return KernelResult<PlatformIrqBinding>.Fail(
                KernelError.PlatformUnsupported,
                "The bound platform provider does not expose semantic interrupt bindings.");
        }

        if (_irqBindings.Values.Any(record =>
                !record.PlatformClosed &&
                record.Binding.DeviceLease.LeaseId == deviceLease.LeaseId &&
                record.Binding.Source == source))
        {
            return KernelResult<PlatformIrqBinding>.Fail(
                KernelError.PlatformBindingActive,
                "The exact semantic interrupt source already has a live binding for this device lifetime.");
        }

        var deviceRecord = _deviceLeases[deviceLease.LeaseId];
        var providerResult = irqProvider.BindInterrupt(
            deviceRecord.ProviderLease,
            source);
        if (!providerResult.IsSuccess)
        {
            return FromProviderFailure<PlatformIrqBinding>(
                providerResult.Status,
                providerResult.Message);
        }

        var providerBinding = providerResult.Value!;
        var providerValidation = PlatformIrqBindingContract.ValidateBinding(
            deviceRecord.ProviderLease,
            source,
            providerBinding);
        if (!providerValidation.IsSuccess)
        {
            _ = irqProvider.RevokeInterrupt(providerBinding);
            return KernelResult<PlatformIrqBinding>.Fail(
                KernelError.PlatformFaulted,
                providerValidation.Message ?? "The provider returned malformed interrupt authority.");
        }

        var binding = new PlatformIrqBinding(
            new PlatformIrqBindingId(_nextIrqBindingId++),
            new PlatformIrqBindingGeneration(1),
            deviceLease,
            source,
            eventEndpoint);
        _irqBindings.Add(
            binding.BindingId,
            new IrqBindingRecord(binding, providerBinding, authorityCapabilityId));
        return KernelResult<PlatformIrqBinding>.Ok(binding);
    }

    internal KernelResult<PlatformIrqDeliveryEvidence> PollIrq(
        PlatformIrqBinding binding,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateIrqBinding(binding, expectedSubject);
        if (!validation.IsSuccess)
        {
            return KernelResult<PlatformIrqDeliveryEvidence>.Fail(
                validation.Error,
                validation.Message!);
        }

        if (_provider is not IPlatformIrqBindingProvider irqProvider)
        {
            return KernelResult<PlatformIrqDeliveryEvidence>.Fail(
                KernelError.PlatformFaulted,
                "The provider that materialized the interrupt binding no longer exposes interrupt delivery.");
        }

        var record = _irqBindings[binding.BindingId];
        var observed = irqProvider.PollInterrupt(record.ProviderBinding);
        if (!observed.IsSuccess)
        {
            return FromProviderFailure<PlatformIrqDeliveryEvidence>(
                observed.Status,
                observed.Message);
        }

        var observation = observed.Value!;
        var observationValidation = PlatformIrqBindingContract.ValidateObservation(
            record.ProviderBinding,
            observation);
        if (!observationValidation.IsSuccess)
        {
            return KernelResult<PlatformIrqDeliveryEvidence>.Fail(
                KernelError.PlatformFaulted,
                observationValidation.Message ?? "The provider returned malformed interrupt delivery evidence.");
        }

        return KernelResult<PlatformIrqDeliveryEvidence>.Ok(
            new PlatformIrqDeliveryEvidence(
                observation.DeliveryAvailable,
                observation.Sequence));
    }

    internal KernelResult CompleteIrqDelivery(
        PlatformIrqBinding binding,
        PlatformDomainIdentity expectedSubject,
        PlatformProviderInterruptDeliverySequence sequence)
    {
        var validation = ValidateIrqBindingIdentity(binding, expectedSubject);
        if (!validation.IsSuccess) return validation;

        if (sequence.Value == 0)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "Interrupt completion requires an exact nonzero provider delivery sequence.");
        }

        if (_provider is not IPlatformIrqBindingProvider irqProvider)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The provider that materialized the interrupt binding no longer exposes interrupt completion.");
        }

        var record = _irqBindings[binding.BindingId];
        var completed = irqProvider.CompleteInterruptDelivery(
            record.ProviderBinding,
            sequence);
        if (!completed.IsSuccess)
            return FromProviderFailure(completed.Status, completed.Message);
        return KernelResult.Ok();
    }

    internal KernelResult RevokeIrq(
        PlatformIrqBinding binding,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateIrqBindingIdentity(binding, expectedSubject);
        if (!validation.IsSuccess) return validation;

        var record = _irqBindings[binding.BindingId];
        if (record.PlatformClosed)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingRevoked,
                "The platform interrupt binding has already been closed.");
        }

        if (_provider is not IPlatformIrqBindingProvider irqProvider)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The provider that materialized the interrupt binding no longer exposes interrupt closure.");
        }

        var revoked = irqProvider.RevokeInterrupt(record.ProviderBinding);
        if (!revoked.IsSuccess)
        {
            if (revoked.Status == PlatformAuthorityStatus.Revoked)
            {
                record.PlatformClosed = true;
                return KernelResult.Ok();
            }

            return FromProviderFailure(revoked.Status, revoked.Message);
        }

        record.PlatformClosed = true;
        return KernelResult.Ok();
    }

    internal KernelResult ValidateIrqBinding(
        PlatformIrqBinding binding,
        PlatformDomainIdentity expectedSubject)
    {
        var validation = ValidateIrqBindingIdentity(binding, expectedSubject);
        if (!validation.IsSuccess) return validation;

        var record = _irqBindings[binding.BindingId];
        if (record.LocalAuthorizationRevoked)
        {
            return KernelResult.Fail(
                KernelError.CapabilityRevoked,
                "The local capability that authorized this interrupt binding has been revoked.");
        }

        if (record.PlatformClosed)
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingRevoked,
                "The platform interrupt binding has been closed.");
        }

        return ValidateDeviceLease(binding.DeviceLease, expectedSubject);
    }

    internal IReadOnlyList<PlatformIrqBinding> BeginIrqCapabilityRevocation(
        CapabilityId capabilityId)
    {
        var affected = _irqBindings.Values
            .Where(record =>
                !record.PlatformClosed &&
                record.AuthorityCapabilityId == capabilityId)
            .OrderBy(record => record.Binding.BindingId.Value)
            .ToArray();

        foreach (var record in affected)
            record.LocalAuthorizationRevoked = true;

        return affected.Select(static record => record.Binding).ToArray();
    }

    internal IReadOnlyList<PlatformIrqBinding> ActiveIrqBindingsForDevice(
        PlatformDeviceLease deviceLease) =>
        _irqBindings.Values
            .Where(record =>
                !record.PlatformClosed &&
                record.Binding.DeviceLease.LeaseId == deviceLease.LeaseId)
            .OrderBy(record => record.Binding.BindingId.Value)
            .Select(static record => record.Binding)
            .ToArray();

    internal IReadOnlyList<PlatformIrqBinding> ActiveIrqBindingsForEndpoint(
        KernelEventEndpoint endpoint) =>
        _irqBindings.Values
            .Where(record =>
                !record.PlatformClosed &&
                record.Binding.EventEndpoint == endpoint)
            .OrderBy(record => record.Binding.BindingId.Value)
            .Select(static record => record.Binding)
            .ToArray();

    private KernelResult ValidateIrqBindingIdentity(
        PlatformIrqBinding binding,
        PlatformDomainIdentity expectedSubject)
    {
        if (!_irqBindings.TryGetValue(binding.BindingId, out var record))
        {
            return KernelResult.Fail(
                KernelError.PlatformBindingNotFound,
                "The platform interrupt binding does not exist.");
        }

        if (record.Binding.Generation != binding.Generation)
        {
            return KernelResult.Fail(
                KernelError.StaleGeneration,
                "The platform interrupt binding generation is stale.");
        }

        if (record.Binding != binding)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The platform interrupt binding identity is malformed.");
        }

        // Structural closure/completion remains possible after local authorization dies.
        return ValidateDeviceLeaseIdentity(binding.DeviceLease, expectedSubject);
    }
}
