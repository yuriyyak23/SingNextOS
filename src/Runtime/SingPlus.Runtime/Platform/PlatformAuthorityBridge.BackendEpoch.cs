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
    private bool _backendEpochExhausted;

    internal PlatformBackendEpoch BackendEpoch => new(_backendEpoch);

    // Exact epoch interlock for local composition cleanup, never provider closure evidence.
    internal KernelResult CommitSecureExecutionClosure(PlatformBackendEpoch admissionEpoch,
        Func<KernelResult> commit, out PlatformBackendEpoch currentEpoch)
    {
        lock (_secureDomainLifecycleGate)
        {
            currentEpoch = BackendEpoch;
            if (_backendEpochExhausted || currentEpoch != admissionEpoch)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Backend changed before composition closure commit.");
            return commit();
        }
    }

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
        lock (_secureDomainLifecycleGate)
            return ObserveBackendResetCore();
    }

    private KernelResult<PlatformBackendResetSnapshot> ObserveBackendResetCore()
    {
        if (_provider is null)
        {
            return KernelResult<PlatformBackendResetSnapshot>.Fail(
                KernelError.PlatformUnavailable,
                "No platform backend is configured to invalidate.");
        }

        bool exhausted = _backendEpochExhausted || _backendEpoch == ulong.MaxValue ||
            _domains.Values.Any(record =>
                record.AuthorityState != DomainAuthorityState.Closed &&
                record.Binding.Generation.Value == ulong.MaxValue) ||
            _virtualDomainBindings.Values.Any(record =>
                record.Binding.Generation.Value == ulong.MaxValue);
        // A reset is already an observation of lost continuity. Even if no
        // distinguishable next generation exists, quarantine the old authority.
        _backendEpochExhausted |= exhausted;

        PlatformBackendEpoch previous = BackendEpoch;
        if (!exhausted) _backendEpoch++;
        PlatformBackendEpoch current = BackendEpoch;
        foreach (ModelCreateInvocationFact invocation in _pendingVirtualDomainCreates.Values)
            if (invocation.EffectMayHaveOccurred)
                _unresolvedVirtualDomainCreates.TryAdd(invocation.Owner, invocation);
        foreach (RootBindInvocationFact invocation in _pendingDomainBinds.Values)
            if (invocation.EffectMayHaveOccurred)
                _unresolvedDomainBinds.TryAdd(invocation.Subject, invocation);

        int faultPinnedMappings = 0;
        foreach (MappingRecord record in _mappings.Values)
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
            foreach (PlatformDmaGrantId grantId in _activeDmaSubmissions.Keys)
            {
                if (!_dmaSubmissionFaultPins.Contains(grantId))
                    faultPinnedDmaSubmissions++;
                // An already pinned effect still observes this distinct backend epoch.
                FaultPinDmaSubmissionLocked(grantId, backendEpoch: current);
            }
        }

        int faultPinnedDsc1Operations = 0;
        lock (_dsc1Gate)
        {
            foreach (Dsc1OperationRecord record in OriginalDsc1OperationsLocked())
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
        int faultPinnedChildDomains = 0;
        foreach (ChildBindingRecord record in _childBindings.Values)
        {
            if (record.State == PlatformChildDomainState.Closed)
                continue;

            record.State = PlatformChildDomainState.Faulted;
            faultPinnedChildDomains++;
        }

        foreach (GuestBindingRecord? record in _originalGuestOwners.Values.Concat(_guestBindings.Values).Distinct())
        {
            if (record.Closure != PlatformExternalClosureState.Closed)
                record.Closure = PlatformExternalClosureState.Faulted;
        }

        foreach (VirtualIoBindingRecord record in _virtualIoOwners)
        {
            if (!HasConsumedVirtualIo(record))
                record.Closure = PlatformExternalClosureState.Faulted;
        }

        foreach (SecureDomainRecord? record in SecureDependencyRecordsLocked().Where(record => !record.ClosureConsumed))
            record.TerminalQuarantined = true;

        PlatformDomainBindingId[] domainIds = _domains
            .Where(static pair => pair.Value.AuthorityState != DomainAuthorityState.Closed)
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (PlatformDomainBindingId bindingId in domainIds)
        {
            DomainRecord record = _domains[bindingId];
            PlatformDomainBinding staleBinding = record.Binding with
            {
                Generation = new PlatformDomainBindingGeneration(
                    record.Binding.Generation.Value == ulong.MaxValue
                        ? ulong.MaxValue : record.Binding.Generation.Value + 1),
            };
            _domains[bindingId] = new DomainRecord(staleBinding, record.ProviderLease)
            {
                AuthorityState = DomainAuthorityState.Quarantined,
                SecureCreateMayHaveEffect = record.SecureCreateMayHaveEffect,
                DeviceBindMayHaveEffect = record.DeviceBindMayHaveEffect || record.PendingDeviceBinds != 0,
                DeviceBindSource = record.DeviceBindSource,
                MappingMayHaveEffect = record.MappingMayHaveEffect || record.PendingMappings != 0,
                ChildCreateMayHaveEffect = record.ChildCreateMayHaveEffect || record.PendingChildCreates != 0,
                ChildCreateInvocations = record.ChildCreateInvocations,
                UnpublishedChildCreates = record.UnpublishedChildCreates,
                PendingSecureCreates = 0, // Original owner balances its invocation interlock; facts retain effect lifetime.
                SecureCreateInvocations = record.SecureCreateInvocations,
                MmioSources = record.MmioSources,
                IrqSources = record.IrqSources,
                UnpublishedSecureCreates = record.UnpublishedSecureCreates,
                ProviderIncarnation = record.ProviderIncarnation,
                BackendEpoch = record.BackendEpoch,
                ProviderClosureConfirmed = record.ProviderClosureConfirmed,
                RootRevokeObservation = record.RootRevokeObservation,
                ParentRevokeInFlight = record.ParentRevokeInFlight,
                ParentRevokeMayHaveEffect = record.ParentRevokeMayHaveEffect,
                ExecutionTransitionInFlight = record.ExecutionTransitionInFlight,
                ExecutionPolicy = record.ExecutionPolicy,
            };
        }

        VirtualDomainBindingId[] virtualBindingIds = _virtualDomainBindings.Keys.ToArray();
        foreach (VirtualDomainBindingId bindingId in virtualBindingIds)
        {
            VirtualDomainRecord record = _virtualDomainBindings[bindingId];
            VirtualDomainBinding staleBinding = record.Binding with
            {
                Generation = new VirtualDomainBindingGeneration(
                    record.Binding.Generation.Value == ulong.MaxValue
                        ? ulong.MaxValue : record.Binding.Generation.Value + 1),
            };
            _virtualDomainBindings[bindingId] = record with { Binding = staleBinding, Quarantined = true,ResetOriginalOwner=record.ResetOriginalOwner??record };
        }

        if (exhausted)
            return KernelResult<PlatformBackendResetSnapshot>.Fail(
                KernelError.CapacityExhausted,
                "Platform reset quarantined old authority, but generation space is exhausted.");
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

