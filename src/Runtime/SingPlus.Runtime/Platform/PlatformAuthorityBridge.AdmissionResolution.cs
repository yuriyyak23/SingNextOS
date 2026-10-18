using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    internal KernelResult CloseOriginalFailedRootCreation(object original)
    {
        if(original is ModelCreateInvocationFact model)return CloseOriginalFailedModelCreation(model);
        if(original is not RootBindInvocationFact bind)
            return KernelResult.Fail(KernelError.PlatformFaulted,"Original failed root factory unavailable.");
        lock(_secureDomainLifecycleGate)
        {
            if(_domains.Values.Any(r=>ReferenceEquals(r.BindInvocation,bind) && r.AuthorityState==DomainAuthorityState.Closed && r.ProviderClosureConfirmed))
                return KernelResult.Ok();
            if(!_unresolvedDomainBinds.TryGetValue(bind.Subject,out var pending) || !ReferenceEquals(pending,bind))
            {
                if(!_activeSubjects.TryGetValue(bind.Subject,out var active) || !ReferenceEquals(active.BindInvocation,bind))
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Exact failed root owner unavailable.");
            }
        }
        var resolved=ResolveOriginalAdmissionsForCleanup(bind.Subject,out var noEffect,rootOnly:true);
        if(!resolved.IsSuccess || noEffect)return resolved;
        DomainRecord record;
        lock(_secureDomainLifecycleGate)
        {
            if(!_activeSubjects.TryGetValue(bind.Subject,out record!) || !ReferenceEquals(record.BindInvocation,bind))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original failed root resolution changed.");
        }
        return RevokeDomain(record.Binding,bind.Subject);
    }

    private KernelResult CloseOriginalFailedModelCreation(ModelCreateInvocationFact fact)
    {
        if(_provider is not IPlatformOriginalModelCreateProvider provider)
            return KernelResult.Fail(KernelError.PlatformFaulted,"Original model query producer unavailable; effect pinned.");
        lock(_secureDomainLifecycleGate)
        {
            if(!ReferenceEquals(fact.OriginalSelf,fact) || !ReferenceEquals(fact.OriginalRequest,fact.CapturedRequest) || fact.OriginalRequest.Owner!=fact.Owner || fact.OriginalRequest.Profile!=fact.Profile ||
                fact.CleanupInFlight || !_unresolvedVirtualDomainCreates.TryGetValue(fact.Owner,out var owned) || !ReferenceEquals(owned,fact))
                return KernelResult.Fail(KernelError.PlatformBindingActive,"Original model cleanup is pending/foreign.");
            fact.CleanupInFlight=true;
        }
        try
        {
            var before=CurrentProviderIncarnation();
            lock(_secureDomainLifecycleGate)
                if(!_unresolvedVirtualDomainCreates.TryGetValue(fact.Owner,out var owned) || !ReferenceEquals(owned,fact) ||
                    _pendingVirtualDomainCreates.ContainsKey(fact.Owner) || BackendEpoch!=fact.AdmissionEpoch ||
                    before.Value==0 || before!=fact.ProviderIncarnation || _backendEpochExhausted)
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original model cleanup source unavailable.");
            var raw=fact.OriginalObservationValidated ? fact.OriginalObservation : null;
            if(raw is null)
            {
                raw=provider.QueryOriginalModelCreate(fact.OriginalRequest);
                lock(_secureDomainLifecycleGate)fact.OriginalObservation=raw;
            }
            var canonical=provider.QueryOriginalModelCreate(fact.OriginalRequest);
            var after=CurrentProviderIncarnation();
            lock(_secureDomainLifecycleGate)
            {
                if(raw is null || !ReferenceEquals(raw,canonical) || !ReferenceEquals(raw.Request,fact.OriginalRequest) ||
                    after!=before || BackendEpoch!=fact.AdmissionEpoch ||
                    !_unresolvedVirtualDomainCreates.TryGetValue(fact.Owner,out var owned) || !ReferenceEquals(owned,fact))
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original model outcome unknown/foreign; retained.");
                fact.OriginalObservationValidated=true;
                if(raw.Outcome.Status==PlatformAuthorityStatus.NotAccepted && raw.Outcome.Value==default)
                {
                    if(_originalVirtualDomainOwners.ContainsKey(fact.ReservedBinding.BindingId) || _virtualDomainBindings.ContainsKey(fact.ReservedBinding.BindingId))
                        return KernelResult.Fail(KernelError.PlatformFaulted,"Original no-effect model has a conflicting owner.");
                    var consumed=fact.LocalCreationNoEffectConsumer?.Invoke()??KernelResult.Ok();
                    if(consumed.IsSuccess)
                    {
                        _unresolvedVirtualDomainCreates.Remove(fact.Owner);
                        if(_originalModelCreateOwners.TryGetValue(fact.Owner,out var retained) && ReferenceEquals(retained,fact))_originalModelCreateOwners.Remove(fact.Owner);
                    }
                    return consumed;
                }
                if(!raw.Outcome.IsSuccess || raw.Outcome.Value.Owner!=fact.Owner || raw.Outcome.Value.LeaseId.Value==0 || raw.Outcome.Value.Generation.Value==0)
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original model lease malformed/unknown; retained.");
                if(fact.CleanupOwner is null)
                {
                    if(_originalVirtualDomainOwners.ContainsKey(fact.ReservedBinding.BindingId) || _virtualDomainBindings.ContainsKey(fact.ReservedBinding.BindingId))
                        return KernelResult.Fail(KernelError.PlatformFaulted,"Recovered model binding occupied by another owner.");
                    var record=new VirtualDomainRecord(fact.ReservedBinding,raw.Outcome.Value)
                    {CreateInvocation=fact,CreatedInEpoch=fact.AdmissionEpoch,ProviderIncarnation=before,ReceiptValid=true,Quarantined=true};
                    fact.CleanupOwner=record;
                    _originalVirtualDomainOwners.Add(record.Binding.BindingId,record);
                    _virtualDomainBindings.Add(record.Binding.BindingId,record);
                }
                if(!_originalVirtualDomainOwners.TryGetValue(fact.ReservedBinding.BindingId,out var originalOwner) ||
                    !ReferenceEquals(originalOwner,fact.CleanupOwner) ||
                    !_virtualDomainBindings.TryGetValue(fact.ReservedBinding.BindingId,out var routedOwner) || !ReferenceEquals(routedOwner,fact.CleanupOwner) ||
                    !ReferenceEquals(fact.CleanupOwner.CreateInvocation,fact) || fact.CleanupOwner.ProviderLease!=raw.Outcome.Value)
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original recovered model owner route absent/copied/foreign; retained.");
            }
            return RevokeVirtualDomain(fact.ReservedBinding);
        }
        catch(Exception exception) when(exception is not StackOverflowException)
        {return KernelResult.Fail(KernelError.PlatformFaulted,"Original model cleanup query unavailable; retained.");}
        finally {lock(_secureDomainLifecycleGate)fact.CleanupInFlight=false;}
    }

    internal KernelResult CloseUnresolvedModelCreations(PlatformDomainIdentity owner)
    {
        ModelCreateInvocationFact? fact;
        lock(_secureDomainLifecycleGate)
        {
            if(_pendingVirtualDomainCreates.ContainsKey(owner))return KernelResult.Fail(KernelError.PlatformBindingActive,"Original model creation still pending.");
            _unresolvedVirtualDomainCreates.TryGetValue(owner,out fact);
            if(_originalModelCreateOwners.TryGetValue(owner,out var retained) && !ReferenceEquals(retained,fact))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original model creation obligation has no exact cleanup route; retained.");
            if(fact is null)return KernelResult.Ok();
            if(!ReferenceEquals(fact.OriginalSelf,fact) || fact.Owner!=owner)
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original model creation route copied/foreign; retained.");
            // Known local models retain their independent VDA cleanup consumer.
            if(fact.LocalCleanupOwnerDelivered && _originalVirtualDomainOwners.Values.Any(r=>ReferenceEquals(r.CreateInvocation,fact)))return KernelResult.Ok();
        }
        return CloseOriginalFailedModelCreation(fact);
    }

    internal KernelResult ResolveOriginalAdmissionsForCleanup(PlatformDomainIdentity subject)
        =>ResolveOriginalAdmissionsForCleanup(subject,out _);

    internal KernelResult ResolveOriginalAdmissionsForCleanup(PlatformDomainIdentity subject,out bool originalRootNoEffectConsumed,bool rootOnly=false)
    {
        originalRootNoEffectConsumed=false;
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
                            var consumed=bind.LocalCreationNoEffectConsumer?.Invoke()??KernelResult.Ok();
                            if(!consumed.IsSuccess)return consumed;
                            originalRootNoEffectConsumed=true;
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
        if(rootOnly)return KernelResult.Ok();
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
