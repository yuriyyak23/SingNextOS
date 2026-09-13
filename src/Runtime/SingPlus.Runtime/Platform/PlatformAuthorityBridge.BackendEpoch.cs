using SingPlus.Platform;

namespace SingPlus.Runtime;

/// <summary>
/// Sing-local generation for the currently observed platform backend instance.
/// This value is never derived from, nor aliased to, a provider or hardware token.
/// </summary>
internal readonly record struct PlatformBackendEpoch(ulong Value);

/// <summary>
/// Diagnostic result of a trusted backend-reset observation. Counts describe
/// local records that were conservatively invalidated; they confer no authority.
/// </summary>
internal readonly record struct PlatformBackendResetSnapshot(
    PlatformBackendEpoch PreviousEpoch,
    PlatformBackendEpoch CurrentEpoch,
    int QuarantinedDomains,
    int FaultPinnedMappings,
    int FaultPinnedDmaSubmissions,
    int FaultPinnedDsc1Operations,
    int InvalidatedVirtualDomains);

public sealed partial class PlatformAuthorityBridge
{
    private ulong _backendEpoch = 1;

    internal PlatformBackendEpoch BackendEpoch => new(_backendEpoch);

    /// <summary>
    /// Records that the configured platform backend reset or otherwise lost the
    /// continuity needed to validate previously issued provider authority.
    /// Existing local authority is never reclaimed optimistically. Instead the
    /// bridge advances only Sing-local generations, quarantines domain roots and
    /// fault-pins incomplete effects so all previously handed-out local bindings
    /// become stale or fault-pinned without importing a provider reset token into
    /// Sing authority.
    /// </summary>
    internal KernelResult<PlatformBackendResetSnapshot> ObserveBackendReset()
    {
        if (_provider is null)
        {
            return KernelResult<PlatformBackendResetSnapshot>.Fail(
                KernelError.PlatformUnavailable,
                "No platform backend is configured to invalidate.");
        }

        if (_backendEpoch == ulong.MaxValue ||
            _domains.Values.Any(record =>
                record.AuthorityState != DomainAuthorityState.Closed &&
                record.Binding.Generation.Value == ulong.MaxValue) ||
            _virtualDomainBindings.Values.Any(record =>
                record.Binding.Generation.Value == ulong.MaxValue))
        {
            return KernelResult<PlatformBackendResetSnapshot>.Fail(
                KernelError.CapacityExhausted,
                "Platform reset generation space is exhausted; old authority cannot be made distinguishably stale.");
        }

        var previous = BackendEpoch;
        _backendEpoch++;
        var current = BackendEpoch;

        var faultPinnedMappings = 0;
        foreach (var record in _mappings.Values)
        {
            if (record.LocalReservationReleased ||
                record.ClosureState == PlatformExternalClosureState.Closed)
            {
                continue;
            }

            record.ClosureState = PlatformExternalClosureState.Faulted;
            faultPinnedMappings++;
        }

        int faultPinnedDmaSubmissions;
        lock (_dmaCompletionGate)
        {
            faultPinnedDmaSubmissions = 0;
            foreach (var grantId in _activeDmaSubmissions.Keys)
            {
                if (_dmaSubmissionFaultPins.Add(grantId))
                    faultPinnedDmaSubmissions++;
            }
        }

        var faultPinnedDsc1Operations = 0;
        lock (_dsc1Gate)
        {
            foreach (var record in _dsc1Operations.Values)
            {
                if (record.LocalReservationsReleased ||
                    record.State is Dsc1OperationState.ClosedCompleted or
                        Dsc1OperationState.ClosedCancelled or
                        Dsc1OperationState.Faulted)
                {
                    continue;
                }

                record.State = Dsc1OperationState.Faulted;
                faultPinnedDsc1Operations++;
            }
        }

        // Child-domain, guest-mapping and virtual-I/O ledgers are independent
        // bridge-owned authorities. A backend reset must invalidate them as well;
        // quarantining only the RuntimeKernel VM record is insufficient because a
        // stale ledger binding could otherwise still issue a provider call directly.
        var faultPinnedChildDomains = 0;
        foreach (var record in _childBindings.Values)
        {
            if (record.State == PlatformChildDomainState.Closed)
                continue;

            record.State = PlatformChildDomainState.Faulted;
            faultPinnedChildDomains++;
        }

        foreach (var record in _guestBindings.Values)
        {
            if (record.Closure != PlatformExternalClosureState.Closed)
                record.Closure = PlatformExternalClosureState.Faulted;
        }

        foreach (var record in _virtualIoBindings.Values)
        {
            if (record.Closure != PlatformExternalClosureState.Closed)
                record.Closure = PlatformExternalClosureState.Faulted;
        }

        foreach (var record in _secureDomains.Values)
            record.Quarantined = true;

        var domainIds = _domains
            .Where(static pair => pair.Value.AuthorityState != DomainAuthorityState.Closed)
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (var bindingId in domainIds)
        {
            var record = _domains[bindingId];
            var staleBinding = record.Binding with
            {
                Generation = new PlatformDomainBindingGeneration(
                    record.Binding.Generation.Value + 1),
            };
            _domains[bindingId] = new DomainRecord(staleBinding, record.ProviderLease)
            {
                AuthorityState = DomainAuthorityState.Quarantined,
                ExecutionPolicy = record.ExecutionPolicy,
            };
        }

        var virtualBindingIds = _virtualDomainBindings.Keys.ToArray();
        foreach (var bindingId in virtualBindingIds)
        {
            var record = _virtualDomainBindings[bindingId];
            var staleBinding = record.Binding with
            {
                Generation = new VirtualDomainBindingGeneration(
                    record.Binding.Generation.Value + 1),
            };
            _virtualDomainBindings[bindingId] = record with { Binding = staleBinding };
        }

        return KernelResult<PlatformBackendResetSnapshot>.Ok(new(
            previous,
            current,
            domainIds.Length,
            faultPinnedMappings,
            faultPinnedDmaSubmissions,
            faultPinnedDsc1Operations,
            virtualBindingIds.Length + faultPinnedChildDomains));
    }
}
