using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    internal KernelResult<PlatformVirtualEventReceipt> InjectChildEvent(
        PlatformChildBinding child, PlatformVirtualEventClass eventClass, string sourceResourceId)
    {
        var resolved = ResolveChild(child);
        if (!resolved.IsSuccess) return KernelResult<PlatformVirtualEventReceipt>.Fail(resolved.Error, resolved.Message!);
        if (!SupportsChildFeature(PlatformFeatureFamily.ChildEventDelivery, PlatformVirtualEventContract.ContractVersion) ||
            _provider is not IPlatformVirtualEventProvider provider)
            return KernelResult<PlatformVirtualEventReceipt>.Fail(KernelError.PlatformUnsupported, "Virtual-event provider is unavailable.");
        ChildBindingRecord record = resolved.Value!;
        var request = new PlatformVirtualEventRequest(record.ProviderLease, eventClass, sourceResourceId);
        var requestValidation = PlatformVirtualEventContract.ValidateRequest(request, record.State);
        if (!requestValidation.IsSuccess)
            return FromProviderFailure<PlatformVirtualEventReceipt>(requestValidation.Status, requestValidation.Message);
        PlatformBackendEpoch backendEpoch;
        lock (_secureDomainLifecycleGate)
        {
            if (record.TransitionInFlight || !PlatformVirtualEventContract.ValidateRequest(request, record.State).IsSuccess)
                return KernelResult<PlatformVirtualEventReceipt>.Fail(KernelError.PlatformBindingActive,
                    "Child lifecycle changed before virtual-event injection.");
            record.PendingChildEffects++;
            backendEpoch = BackendEpoch;
        }
        try
        {
            PlatformAuthorityResult<PlatformVirtualEventReceipt> result;
            try { result = provider.InjectVirtualEvent(request); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (_secureDomainLifecycleGate) record.State = PlatformChildDomainState.Faulted;
                return KernelResult<PlatformVirtualEventReceipt>.Fail(KernelError.PlatformFaulted,
                    $"Virtual-event injection may have taken effect without a receipt: {exception.Message}");
            }
            lock (_secureDomainLifecycleGate)
            {
                if (BackendEpoch != backendEpoch || record.State == PlatformChildDomainState.Faulted)
                {
                    record.State = PlatformChildDomainState.Faulted;
                    return KernelResult<PlatformVirtualEventReceipt>.Fail(KernelError.PlatformFaulted,
                        "Backend or child changed during virtual-event injection.");
                }
                if (!result.IsSuccess)
                {
                    record.State = PlatformChildDomainState.Faulted;
                    return FromProviderFailure<PlatformVirtualEventReceipt>(result.Status, result.Message);
                }
                var evidence = result.Value!;
                var validation = PlatformVirtualEventContract.ValidateReceipt(request, evidence);
                if (!validation.IsSuccess || evidence.Sequence <= record.LastEventSequence)
                {
                    record.State = PlatformChildDomainState.Faulted;
                    return KernelResult<PlatformVirtualEventReceipt>.Fail(KernelError.PlatformFaulted,
                        validation.IsSuccess ? "Virtual-event evidence sequence is stale or replayed." : validation.Message!);
                }
                record.LastEventSequence = evidence.Sequence;
                return KernelResult<PlatformVirtualEventReceipt>.Ok(evidence);
            }
        }
        finally { lock (_secureDomainLifecycleGate) record.PendingChildEffects--; }
    }

    internal KernelResult<PlatformVirtualTrapEvidence> ObserveChildTrap(PlatformChildBinding child)
    {
        var resolved = ResolveChild(child);
        if (!resolved.IsSuccess) return KernelResult<PlatformVirtualTrapEvidence>.Fail(resolved.Error, resolved.Message!);
        if (!SupportsChildFeature(PlatformFeatureFamily.ChildTrapDelivery, PlatformVirtualTrapContract.ContractVersion) ||
            _provider is not IPlatformVirtualTrapProvider provider)
            return KernelResult<PlatformVirtualTrapEvidence>.Fail(KernelError.PlatformUnsupported, "Neutral trap provider is unavailable.");
        ChildBindingRecord record = resolved.Value!;
        var requestValidation = PlatformVirtualTrapContract.ValidateObservationRequest(record.ProviderLease, record.State);
        if (!requestValidation.IsSuccess)
            return FromProviderFailure<PlatformVirtualTrapEvidence>(requestValidation.Status, requestValidation.Message);
        PlatformBackendEpoch backendEpoch;
        lock (_secureDomainLifecycleGate)
        {
            if (record.TransitionInFlight ||
                !PlatformVirtualTrapContract.ValidateObservationRequest(record.ProviderLease, record.State).IsSuccess)
                return KernelResult<PlatformVirtualTrapEvidence>.Fail(KernelError.PlatformBindingActive,
                    "Child lifecycle changed before trap observation.");
            record.PendingChildEffects++;
            backendEpoch = BackendEpoch;
        }
        try
        {
            PlatformAuthorityResult<PlatformVirtualTrapEvidence> result;
            try { result = provider.ObserveVirtualTrap(record.ProviderLease); }
            catch (Exception exception) when (exception is not StackOverflowException)
            {
                lock (_secureDomainLifecycleGate) record.State = PlatformChildDomainState.Faulted;
                return KernelResult<PlatformVirtualTrapEvidence>.Fail(KernelError.PlatformFaulted,
                    $"Trap observation lost its provider result: {exception.Message}");
            }
            lock (_secureDomainLifecycleGate)
            {
                if (BackendEpoch != backendEpoch || record.State == PlatformChildDomainState.Faulted)
                {
                    record.State = PlatformChildDomainState.Faulted;
                    return KernelResult<PlatformVirtualTrapEvidence>.Fail(KernelError.PlatformFaulted,
                        "Backend or child changed during trap observation.");
                }
                if (!result.IsSuccess)
                {
                    record.State = PlatformChildDomainState.Faulted;
                    return FromProviderFailure<PlatformVirtualTrapEvidence>(result.Status, result.Message);
                }
                var evidence = result.Value!;
                var validation = PlatformVirtualTrapContract.ValidateEvidence(record.ProviderLease, evidence);
                if (!validation.IsSuccess || evidence.Sequence <= record.LastTrapSequence)
                {
                    record.State = PlatformChildDomainState.Faulted;
                    return KernelResult<PlatformVirtualTrapEvidence>.Fail(KernelError.PlatformFaulted,
                        validation.IsSuccess ? "Trap evidence sequence is stale or replayed." : validation.Message!);
                }
                record.LastTrapSequence = evidence.Sequence;
                return KernelResult<PlatformVirtualTrapEvidence>.Ok(evidence);
            }
        }
        finally { lock (_secureDomainLifecycleGate) record.PendingChildEffects--; }
    }
}
