using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider
{
    public PlatformAuthorityResult<PlatformDomainBindResolution> ResolveDomainBind(PlatformDomainBindRequest request)
    {
        RootBindRecord? owner;
        DomainRecord? output;
        lock (_childCreateCommitGate)
        {
            owner = request.InvocationId == Guid.Empty ? null : _originalRootBinds.Values.FirstOrDefault(x => x.Request == request);
            output = owner?.OriginalDomain;
        }
        if (owner is null) return PlatformAuthorityResult<PlatformDomainBindResolution>.Fail(PlatformAuthorityStatus.Faulted, "Original Bind request is unavailable.");
        PlatformAuthorityResult source = ValidateOriginalNativeMappingSource(); // Callback outside owner lock.
        if (!source.IsSuccess) return PlatformAuthorityResult<PlatformDomainBindResolution>.Fail(source.Status, source.Message!);
        if(owner is {NativeObservation:null,NativeRequest:{ } pendingNativeBind} && _runtime is INeutralOriginalBindOutcomeProvider nativeLookup)
        {
            NeutralOriginalDomainBindObservation? queried =owner.NativeResolutionObservation??nativeLookup.ResolveOriginalDomainBind(pendingNativeBind);
            lock(_childCreateCommitGate)
            {
                if(queried is not null)owner.NativeResolutionObservation??=queried;
                queried=owner.NativeResolutionObservation;
                if(!owner.InFlight && owner.Request==request && owner.NativeObservation is null &&
                    _originalRootBinds.TryGetValue(owner.Lease.LeaseId,out RootBindRecord? originalBind) && ReferenceEquals(originalBind,owner) &&
                    queried is {SourceFenceMatched:true} raw && raw.Request==pendingNativeBind &&
                    (raw.Observation is {IsBound:true,Decision:NeutralDomainBindDecision.Bound} bound &&
                    bound.Lease.Handle.Value!=0 && bound.Lease.Epoch.Value!=0 ||
                    raw.ProvenNoEffect && !raw.Observation.IsBound && raw.Observation.Lease==default &&
                    raw.Observation.Decision is NeutralDomainBindDecision.Faulted or NeutralDomainBindDecision.UnsupportedProfile))
                {
                    owner.NativeObservation=raw;owner.Observation=raw.Observation;
                    if(raw.Observation.IsBound)
                    {
                        output=new(owner.Lease,raw.Observation.Lease){BindSource=owner,CleanupOnly=true};
                        owner.OriginalDomain=output;owner.PublishedDomain=output;
                    }
                }
            }
            source=ValidateOriginalNativeMappingSource();
            if(!source.IsSuccess)return PlatformAuthorityResult<PlatformDomainBindResolution>.Fail(source.Status,source.Message!);
        }
        lock (_childCreateCommitGate)
        {
            if (!owner.InFlight && owner.Request == request && owner.NativeRequest is { } rejectedRequest &&
                owner.NativeObservation is { } rejected && (!rejected.SourceFenceMatched || rejected.ProvenNoEffect) && rejected.Request == rejectedRequest &&
                rejectedRequest.InvocationId == request.InvocationId && rejectedRequest.RuntimeIncarnation == _originalNativeMappingIncarnation &&
                rejected.Observation is { IsBound: false, Lease: var rejectedLease } && rejectedLease == default &&
                rejected.Observation.Decision is NeutralDomainBindDecision.Faulted or NeutralDomainBindDecision.UnsupportedProfile &&
                owner.OriginalDomain is null && owner.PublishedDomain is null &&
                _originalRootBinds.TryGetValue(owner.Lease.LeaseId, out RootBindRecord? noEffectOwner) && ReferenceEquals(noEffectOwner, owner) &&
                !_activeSubjects.ContainsKey(request.Subject) && !_domains.ContainsKey(owner.Lease.LeaseId) &&
                (owner.NoEffectConsumed || (_rootBinds.TryGetValue(request.Subject, out RootBindRecord? pendingNoEffect) && ReferenceEquals(pendingNoEffect, owner))))
            {
                owner.NoEffectConsumed = true;
                if (_rootBinds.TryGetValue(request.Subject, out RootBindRecord? pending) && ReferenceEquals(pending, owner)) _rootBinds.Remove(request.Subject);
                return PlatformAuthorityResult<PlatformDomainBindResolution>.Ok(new(request, default) { ProvenNoEffect = true });
            }
            if (owner.InFlight || !_originalRootBinds.TryGetValue(owner.Lease.LeaseId, out RootBindRecord? original) || !ReferenceEquals(original, owner) ||
                owner.Request != request || owner.NativeRequest is not { } nativeRequest ||
                owner.NativeObservation is not { SourceFenceMatched: true } native || native.Request != nativeRequest ||
                nativeRequest.InvocationId != request.InvocationId || nativeRequest.RuntimeIncarnation != _originalNativeMappingIncarnation ||
                native.Observation != owner.Observation || owner.Observation is not { IsBound: true, Decision: NeutralDomainBindDecision.Bound } raw ||
                owner.PublishedDomain is not { } record || !ReferenceEquals(record.BindSource, owner) || record.HybridCpuLease != raw.Lease)
                return PlatformAuthorityResult<PlatformDomainBindResolution>.Fail(PlatformAuthorityStatus.Faulted, "Original Bind outcome/owner is unresolved.");
            if (!ReferenceEquals(owner.OriginalDomain, record) || !ReferenceEquals(record, output))
                return PlatformAuthorityResult<PlatformDomainBindResolution>.Fail(PlatformAuthorityStatus.Faulted, "Original Bind output reference is foreign.");
            if (_domains.TryGetValue(owner.Lease.LeaseId, out DomainRecord? current))
            {
                if (!ReferenceEquals(current, record) || !ValidateDomainIdentity(owner.Lease).IsSuccess)
                    return PlatformAuthorityResult<PlatformDomainBindResolution>.Fail(PlatformAuthorityStatus.Faulted, "Original Bind cleanup routing is foreign.");
            }
            else
            {
                if (!_rootBinds.TryGetValue(request.Subject, out RootBindRecord? pending) || !ReferenceEquals(pending, owner) || _activeSubjects.ContainsKey(request.Subject))
                    return PlatformAuthorityResult<PlatformDomainBindResolution>.Fail(PlatformAuthorityStatus.Faulted, "Original pending Bind owner is absent/foreign.");
                _domains.Add(owner.Lease.LeaseId, record);
                _activeSubjects.Add(request.Subject, record);
                _rootBinds.Remove(request.Subject);
            }
            return PlatformAuthorityResult<PlatformDomainBindResolution>.Ok(new(request, owner.Lease));
        }
    }

    public PlatformAuthorityResult<PlatformOwnedRegionMapResolution> ResolveOwnedRegionMap(PlatformOwnedRegionMapRequest request)
    {
        DomainRecord? parent;
        OwnedMapFailure? owner;
        ProviderMappingRecord? mapping;
        lock (_childCreateCommitGate)
        {
            _domains.TryGetValue(request.Domain.LeaseId, out parent);
            owner = parent?.OwnedMapAdmissionOwner;
            mapping = owner?.Request == request ? owner.OriginalMapping :
                _originalProviderMappings.Values.FirstOrDefault(x => x.AdmissionRequest == request);
        }
        if (request.InvocationId == Guid.Empty || parent is null)
            return PlatformAuthorityResult<PlatformOwnedRegionMapResolution>.Fail(PlatformAuthorityStatus.Faulted, "Original Map outcome is unavailable.");
        PlatformAuthorityResult source = ValidateOriginalNativeMappingSource();
        if (!source.IsSuccess) return PlatformAuthorityResult<PlatformOwnedRegionMapResolution>.Fail(source.Status, source.Message!);
        if(owner is { NativeObservation:null,NativeRequest:{ } pending } && owner.Request==request &&
            _runtime is INeutralOriginalMapOutcomeProvider nativeLookup)
        {
            NeutralOriginalOwnedRegionMapObservation? queried =owner.NativeResolutionObservation??nativeLookup.ResolveOriginalOwnedRegionMap(pending); // Pure callback outside provider gate.
            lock(_childCreateCommitGate)
            {
                if(queried is not null)owner.NativeResolutionObservation??=queried; // Raw before validation; malformed cannot be replaced.
                queried=owner.NativeResolutionObservation;
                if(!parent.OwnedMapInFlight && ReferenceEquals(parent.OwnedMapAdmissionOwner,owner) &&
                    ReferenceEquals(parent.FailedOwnedMap,owner) && owner.NativeObservation is null &&
                    queried is { SourceFenceMatched:true } raw && raw.Request==pending &&
                    raw.Observation is { IsMapped:true,Decision:NeutralOwnedRegionMapDecision.Mapped } observed &&
                    observed.Lease.DomainLease==owner.NeutralDomain && observed.Lease.Slice==owner.NeutralSlice &&
                    observed.Lease.Handle.Value!=0 && observed.Lease.Epoch.Value!=0 && observed.Lease.Coherence==NeutralMemoryCoherenceModel.NonCoherent)
                {
                    owner.NativeObservation=raw;owner.Observation=observed;
                    PlatformProviderRegionMappingLease lease =new PlatformProviderRegionMappingLease(owner.InvocationId,new(1),request.Domain,request.Slice.Region,request.Slice.Access);
                    mapping=new(new(lease,request.Slice),observed.Lease){MapParent=parent,CleanupOnly=true,AdmissionRequest=request,MapAdmissionSource=owner};
                    owner.OriginalMapping=mapping;owner.CleanupMapping=mapping;
                }
                else if(!parent.OwnedMapInFlight && ReferenceEquals(parent.OwnedMapAdmissionOwner,owner) &&
                    ReferenceEquals(parent.FailedOwnedMap,owner) && owner.NativeObservation is null &&
                    queried is {SourceFenceMatched:true,ProvenNoEffect:true} rejected && rejected.Request==pending &&
                    rejected.Observation is {IsMapped:false,Decision:NeutralOwnedRegionMapDecision.Faulted} && rejected.Observation.Lease==default)
                {owner.NativeObservation=rejected;owner.Observation=rejected.Observation;}
            }
            source=ValidateOriginalNativeMappingSource();
            if(!source.IsSuccess)return PlatformAuthorityResult<PlatformOwnedRegionMapResolution>.Fail(source.Status,source.Message!);
        }
        lock (_childCreateCommitGate)
        {
            if (!parent.OwnedMapInFlight && owner is not null && owner.Request == request &&
                owner.NativeRequest is { } rejectedRequest && owner.NativeObservation is { } rejected &&
                (!rejected.SourceFenceMatched || rejected.ProvenNoEffect && rejected.Observation.Decision==NeutralOwnedRegionMapDecision.Faulted) &&
                rejected.Request == rejectedRequest && rejectedRequest.InvocationId == request.InvocationId &&
                rejectedRequest.RuntimeIncarnation == _originalNativeMappingIncarnation &&
                rejected.Observation is { IsMapped: false, Lease: var rejectedLease } && rejectedLease == default &&
                owner.OriginalMapping is null && owner.CleanupMapping is null &&
                ReferenceEquals(parent.OwnedMapAdmissionOwner, owner) &&
                (parent.FailedOwnedMap is null || ReferenceEquals(parent.FailedOwnedMap, owner)) &&
                _domains.TryGetValue(request.Domain.LeaseId, out DomainRecord? rejectedParent) && ReferenceEquals(rejectedParent, parent) &&
                ValidateDomainIdentity(request.Domain).IsSuccess)
            {
                parent.FailedOwnedMap = null; // Original no-effect fact remains in the admission owner.
                return PlatformAuthorityResult<PlatformOwnedRegionMapResolution>.Ok(new(request, default) { ProvenNoEffect = true });
            }
            if (mapping is null) return PlatformAuthorityResult<PlatformOwnedRegionMapResolution>.Fail(PlatformAuthorityStatus.Faulted, "Original Map outcome is unavailable.");
            if (parent.OwnedMapInFlight || !_domains.TryGetValue(request.Domain.LeaseId, out DomainRecord? currentParent) || !ReferenceEquals(currentParent, parent) ||
                !ValidateDomainIdentity(request.Domain).IsSuccess || mapping.AdmissionRequest != request || !ReferenceEquals(mapping.MapParent, parent) ||
                mapping.MapAdmissionSource is not { NativeRequest: { } nativeRequest, NativeObservation: { SourceFenceMatched: true } native } admission ||
                native.Request != nativeRequest || nativeRequest.InvocationId != request.InvocationId ||
                nativeRequest.RuntimeIncarnation != _originalNativeMappingIncarnation || native.Observation != admission.Observation)
                return PlatformAuthorityResult<PlatformOwnedRegionMapResolution>.Fail(PlatformAuthorityStatus.Faulted, "Original Map parent/request is absent/foreign.");
            if (_originalProviderMappings.TryGetValue(mapping.Mapping.Lease.MappingId, out ProviderMappingRecord? original))
            {
                if (!ReferenceEquals(original, mapping) || !_providerMappings.TryGetValue(mapping.Mapping.Lease.MappingId, out ProviderMappingRecord? routed) || !ReferenceEquals(routed, mapping))
                    return PlatformAuthorityResult<PlatformOwnedRegionMapResolution>.Fail(PlatformAuthorityStatus.Faulted, "Original Map cleanup owner is foreign.");
            }
            else
            {
                if (owner is null || !ReferenceEquals(parent.OwnedMapAdmissionOwner, owner) || !ReferenceEquals(parent.FailedOwnedMap, owner) ||
                    owner.Request != request || !ReferenceEquals(owner.CleanupMapping, mapping) || !ReferenceEquals(mapping.MapAdmissionSource, owner) ||
                    owner.Observation is not { IsMapped: true, Decision: NeutralOwnedRegionMapDecision.Mapped } raw || raw.Lease != mapping.HybridCpuLease ||
                    _providerMappings.ContainsKey(mapping.Mapping.Lease.MappingId))
                    return PlatformAuthorityResult<PlatformOwnedRegionMapResolution>.Fail(PlatformAuthorityStatus.Faulted, "Original Map invocation remains unresolved.");
                _originalProviderMappings.Add(mapping.Mapping.Lease.MappingId, mapping);
                _providerMappings.Add(mapping.Mapping.Lease.MappingId, mapping);
                parent.FailedOwnedMap = null;
                parent.OwnedMapAdmissionOwner = null;
            }
            return PlatformAuthorityResult<PlatformOwnedRegionMapResolution>.Ok(new(request, mapping.Mapping));
        }
    }
}
