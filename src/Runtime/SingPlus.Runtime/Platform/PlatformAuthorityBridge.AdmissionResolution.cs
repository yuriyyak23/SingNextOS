using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    internal KernelResult ResolveOriginalAdmissionsForCleanup(PlatformDomainIdentity subject)
    {
        if (_provider is not IPlatformOriginalAdmissionResolutionProvider provider) return KernelResult.Ok();
        RootBindInvocationFact? bind;
        lock (_secureDomainLifecycleGate)
        {
            _unresolvedDomainBinds.TryGetValue(subject, out bind);
            if (bind is not null)
            {
                if (bind.ResolutionInFlight || _pendingDomainBinds.ContainsKey(subject))
                    return KernelResult.Fail(KernelError.PlatformBindingDraining, "Original Bind admission/resolution is in flight.");
                if (bind.Request is null) return KernelResult.Fail(KernelError.PlatformFaulted, "Original Bind has no provider request key.");
                bind.ResolutionInFlight = true;
            }
        }
        if (bind is not null)
        {
            try
            {
                var request = bind.Request!.Value;
                PlatformAuthorityResult<PlatformDomainBindResolution> result;
                try { result = provider.ResolveDomainBind(request); }
                catch (Exception exception) when (exception is not StackOverflowException)
                { return KernelResult.Fail(KernelError.PlatformFaulted, "Original Bind resolution response unavailable; effect remains pinned."); }
                lock (_secureDomainLifecycleGate) bind.ResolutionObservation = result;
                if (!result.IsSuccess) return FromProviderFailure(result.Status, result.Message);
                var resolved = result.Value;
                if (resolved.Request != request || (resolved.ProvenNoEffect ? resolved.Lease != default : !PlatformDomainContract.ValidateLease(subject, resolved.Lease).IsSuccess))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original Bind resolution is malformed/foreign.");
                PlatformProviderIncarnation source;
                try { source = CurrentProviderIncarnation(); }
                catch (Exception exception) when (exception is not StackOverflowException)
                { return KernelResult.Fail(KernelError.PlatformFaulted, "Known Bind resolution retained while source is unavailable."); }
                lock (_secureDomainLifecycleGate)
                {
                    if (_backendEpochExhausted || BackendEpoch != bind.AdmissionEpoch || source != bind.ProviderIncarnation || source.Value == 0 ||
                        !_unresolvedDomainBinds.TryGetValue(subject, out var original) || !ReferenceEquals(original, bind) || bind.Request != request)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original Bind/source owner changed before cleanup adoption.");
                    if (bind.CleanupOwner is null)
                    {
                        if (resolved.ProvenNoEffect)
                        {
                            if (_activeSubjects.ContainsKey(subject) || _pendingDomainBinds.ContainsKey(subject))
                                return KernelResult.Fail(KernelError.PlatformFaulted, "Original no-effect Bind cannot consume a foreign root/admission.");
                            _unresolvedDomainBinds.Remove(subject);
                            return KernelResult.Ok(); // Exact no-effect consumption, no provider Close or synthetic lease.
                        }
                        if (_activeSubjects.ContainsKey(subject) || _nextDomainBindingId is 0 or ulong.MaxValue)
                            return KernelResult.Fail(KernelError.PlatformFaulted, "Original Bind cleanup cannot replace another root.");
                        var binding = AddDomainRecord(subject, resolved.Lease, DomainAuthorityState.Quarantined, source, bind.AdmissionEpoch, bind);
                        bind.CleanupOwner = _domains[binding.BindingId];
                    }
                    var owner = bind.CleanupOwner;
                    if (owner.ProviderLease != resolved.Lease || !ReferenceEquals(owner.BindInvocation, bind) ||
                        !ValidateRootCleanupSourceLocked(owner, owner.Binding, subject, bind.AdmissionEpoch, source).IsSuccess)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original Bind cleanup record is absent/foreign.");
                    _unresolvedDomainBinds.Remove(subject); // Correlation consumed, not closure or permission.
                }
            }
            finally { lock (_secureDomainLifecycleGate) bind.ResolutionInFlight = false; }
        }
        MappingRecord[] mappings;
        lock (_secureDomainLifecycleGate)
            mappings = OriginalMappingRecordsLocked().Where(x => x.Mapping.DomainBinding.Subject == subject && x.ProviderLease == default && x.AdmissionRequest is not null && !HasValidatedAdmissionNoEffect(x)).ToArray();
        foreach (var mapping in mappings)
        {
            var result = RunMappingClosureTransition(mapping.Mapping, subject, owner => ResolveOriginalMapForCleanup(provider, owner, subject));
            if (!result.IsSuccess) return KernelResult.Fail(result.Error, result.Message!);
        }
        return KernelResult.Ok();
    }

    private KernelResult<PlatformRegionMappingLifecycle> ResolveOriginalMapForCleanup(
        IPlatformOriginalAdmissionResolutionProvider provider, MappingRecord owner, PlatformDomainIdentity subject)
    {
        if (owner.ProviderLease != default) return KernelResult<PlatformRegionMappingLifecycle>.Ok(owner.Lifecycle);
        var request = owner.AdmissionRequest!.Value;
        var generation = ValidateMappingClosureGeneration(owner);
        if (!generation.IsSuccess) return KernelResult<PlatformRegionMappingLifecycle>.Fail(generation.Error, generation.Message!);
        PlatformAuthorityResult<PlatformOwnedRegionMapResolution> result;
        try { result = provider.ResolveOwnedRegionMap(request); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformFaulted, "Original Map resolution response unavailable."); }
        lock (_secureDomainLifecycleGate) owner.AdmissionResolutionObservation = result;
        if (!result.IsSuccess) return FromProviderFailure<PlatformRegionMappingLifecycle>(result.Status, result.Message);
        var resolved = result.Value;
        if (resolved.Request != request || (resolved.ProvenNoEffect ? resolved.Mapping != default : !PlatformOwnedRegionMappingContract.ValidateResult(request.Domain, request.Slice, resolved.Mapping).IsSuccess))
            return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformFaulted, "Original Map resolution is malformed/foreign.");
        generation = ValidateMappingClosureGeneration(owner);
        if (!generation.IsSuccess) return KernelResult<PlatformRegionMappingLifecycle>.Fail(generation.Error, generation.Message!);
        PlatformProviderIncarnation source;
        try { source = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformFaulted, "Original Map resolution source is unavailable."); }
        lock (_secureDomainLifecycleGate)
        {
            if (_backendEpochExhausted || BackendEpoch != owner.BackendEpoch || source.Value == 0 || source != owner.ProviderIncarnation)
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformFaulted, "Original Map resolution source changed.");
            var exact = ValidateMappingCleanupOwnerLocked(owner, owner.Mapping, subject);
            if (!exact.IsSuccess) return KernelResult<PlatformRegionMappingLifecycle>.Fail(exact.Error, exact.Message!);
            if (owner.ProviderLease != default || owner.LocalReservationReleased || owner.AdmissionRequest != request ||
                owner.ClosureState != PlatformExternalClosureState.Faulted || owner.OriginalDomain?.ProviderLease != request.Domain ||
                !_exactMappingSlices.TryGetValue(owner.Mapping.MappingId, out var slice) || slice != request.Slice)
                return KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformFaulted, "Original Map cleanup owner changed before adoption.");
            owner.ProviderLease = resolved.Mapping.Lease;
            owner.AdmissionNoEffectProven = resolved.ProvenNoEffect;
            owner.LocalAuthorizationRevoked = true;
            owner.ClosureState = resolved.ProvenNoEffect ? PlatformExternalClosureState.Faulted : PlatformExternalClosureState.Active;
            if (owner.OriginalDomain is { } root && root.PendingMappings == 0 && root.MappingRecords.All(x => x.ProviderLease != default || HasValidatedAdmissionNoEffect(x)))
                root.MappingMayHaveEffect = false;
            return KernelResult<PlatformRegionMappingLifecycle>.Ok(owner.Lifecycle);
        }
    }

    private static bool HasValidatedAdmissionNoEffect(MappingRecord owner) => owner.AdmissionNoEffectProven &&
        owner.ProviderLease == default && owner.AdmissionRequest is { } request &&
        owner.AdmissionResolutionObservation is { IsSuccess: true } result &&
        result.Value.ProvenNoEffect && result.Value.Request == request && result.Value.Mapping == default;
}
