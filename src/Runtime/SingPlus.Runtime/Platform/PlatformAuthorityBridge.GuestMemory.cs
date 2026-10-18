using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    // Original effect correlation on the existing child owner, never permission.
    private sealed class GuestMapInvocationFact
    {
        public GuestMapInvocationFact(GuestAdmissionSource source, PlatformGuestRegionMappingRequest request, PlatformBackendEpoch epoch)
        { OriginalOwner = this; Source = source; Request = request; Epoch = epoch; OriginalRequest = new(request); ChildState = source.Child.State; ParentState = source.Parent.ClosureState; ChildRevision = source.Child.LifecycleRevision; ParentRevision = source.Parent.LifecycleRevision; }
        public GuestMapInvocationFact OriginalOwner { get; }
        public GuestAdmissionSource Source { get; }
        public PlatformGuestRegionMappingRequest Request { get; }
        public PlatformOriginalGuestMapRequest OriginalRequest { get; }
        public PlatformBackendEpoch Epoch { get; }
        public PlatformGuestMappingId ReservedIdentity;
        public PlatformChildDomainState ChildState { get; }
        public PlatformExternalClosureState ParentState { get; }
        public long ChildRevision, ParentRevision, ChildFaultRevision, ParentFaultRevision;
        public bool OwnChildFault, OwnParentFault, ResolutionInFlight;
        public bool OwnGuestFault;
        public long GuestFaultRevision;
        public GuestBindingRecord? KnownMapping;
        public PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>? Observation { get; set; }
        public PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>? Resolution { get; set; }
        public bool Unresolved { get; set; } = true;
        public Dictionary<ChildBindingRecord, long> Lineage { get; init; } = [];
    }

    // Original invocation cleanup fact, never guest admission or original closure evidence.
    private sealed record UnpublishedGuestMapReceipt(PlatformChildBinding Child,
        PlatformRegionMapping ParentMapping, PlatformGuestRegionMappingRequest Request,
        PlatformBackendEpoch AdmissionEpoch, PlatformProviderIncarnation ProviderIncarnation,
        PlatformProviderGuestRegionMappingLease Receipt)
    {
        public PlatformGuestRegionMappingClosureReceipt? ReturnedClosureReceipt { get; set; }
    }

    internal KernelResult<PlatformGuestMapping> MapChildGuestRegion(
        PlatformChildBinding child, PlatformRegionMapping parentMapping,
        PlatformGuestAddressRange guestRange, PlatformGuestMemoryAccess access)
        => MapChildGuestRegion(child, parentMapping, guestRange, access, out _);

    internal KernelResult<PlatformGuestMapping> MapChildGuestRegion(
        PlatformChildBinding child, PlatformRegionMapping parentMapping,
        PlatformGuestAddressRange guestRange, PlatformGuestMemoryAccess access,
        out PlatformGuestMapping? knownMapping,
        Func<Func<KernelResult>, KernelResult>? admissionConsumer = null,
        Func<Func<KernelResult>, KernelResult>? sourceCommitConsumer = null,
        Func<PlatformGuestMapping, Func<Func<KernelResult>, KernelResult>, KernelResult>? completionConsumer = null,
        VirtualDomainAuthority.Record? localMappingOwner = null, GuestRegionMappingHandle? localMapping = null)
    {
        knownMapping = null;
        if ((localMappingOwner is null) != (localMapping is null) ||
            localMapping is { } local && (local.MappingId.Value == 0 || local.Generation.Value == 0))
            return KernelResult<PlatformGuestMapping>.Fail(KernelError.StaleGeneration, "Local guest mapping correlation is incomplete.");
        var childResult = ResolveChild(child);
        if (!childResult.IsSuccess) return KernelResult<PlatformGuestMapping>.Fail(childResult.Error, childResult.Message!);
        if (!SupportsChildFeature(PlatformFeatureFamily.ChildGuestMemory, PlatformGuestMemoryContract.ContractVersion) ||
            _provider is not IPlatformGuestMemoryProvider provider)
            return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformUnsupported, "Guest-memory provider is unavailable.");
        if (!_mappings.TryGetValue(parentMapping.MappingId, out MappingRecord? parent) || parent.Mapping != parentMapping)
            return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformBindingNotFound, "Exact parent mapping was not found.");
        if (parent.LocalAuthorizationRevoked || parent.ClosureState != PlatformExternalClosureState.Active)
            return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformBindingRevoked, "Parent mapping is not active.");
        ChildBindingRecord childRecord = childResult.Value!;
        if (parentMapping.DomainBinding != child.ParentBinding)
            return KernelResult<PlatformGuestMapping>.Fail(KernelError.WrongPlatformDomain, "Parent mapping belongs to another domain.");
        // Preserve the originally admitted bounded slice. A lease identifies the
        // Region, but does not widen a subrange mapping to that entire Region.
        PlatformRegionSlice parentSlice;
        lock(_secureDomainLifecycleGate)
        {
            if(!_exactMappingSlices.TryGetValue(parentMapping.MappingId,out parentSlice))
            {
                if(parent.AdmissionRequest is not null)
                    return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformFaulted,"Original bounded parent slice is unavailable.");
                parentSlice=new(parent.ProviderLease.Region,0,parent.ProviderLease.Region.ByteLength,parent.ProviderLease.Access);
            }
            if(parent.AdmissionRequest is { } admitted && admitted.Slice!=parentSlice)
                return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformFaulted,"Original bounded parent slice changed.");
        }
        var request = new PlatformGuestRegionMappingRequest(childRecord.ProviderLease,
            new PlatformProviderOwnedRegionMapping(parent.ProviderLease,parentSlice), guestRange, access);
        var validation = PlatformGuestMemoryContract.ValidateRequest(request, childRecord.State);
        if (!validation.IsSuccess) return FromProviderFailure<PlatformGuestMapping>(validation.Status, validation.Message);
        DomainRecord originalRoot = null!;
        PlatformBackendEpoch backendEpoch = default;
        PlatformGuestMappingId reservedMappingId = default;
        GuestMapInvocationFact invocation = null!;
        Dictionary<ChildBindingRecord, long> sourceRevisions = [];
        long rootRevision = 0;
        KernelResult Begin()
        {
          lock (_secureDomainLifecycleGate)
          {
            if (_nextGuestBindingId == 0 || _nextGuestBindingId == ulong.MaxValue)
                return KernelResult.Fail(KernelError.CapacityExhausted, "Guest mapping identity space is exhausted.");
            if (HasUnresolvedGuestMapInvocation(parent) || childRecord.GuestMapInvocations.Any(x => x.Unresolved))
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    "Original guest-map invocation remains unresolved; repeated effect is forbidden.");
            var lineage = ValidateActiveChildLineage(childRecord);
            if (!lineage.IsSuccess)
                return lineage;
            var captured = CaptureGuestEffectLineage(childRecord);
            if (!captured.IsSuccess) return KernelResult.Fail(captured.Error, captured.Message!);
            sourceRevisions = captured.Value!;
            if (!_childBindings.TryGetValue(child.BindingId, out var currentChild) || !ReferenceEquals(currentChild, childRecord) ||
                !_mappings.TryGetValue(parentMapping.MappingId, out var currentParent) || !ReferenceEquals(currentParent, parent) ||
                !_domains.TryGetValue(child.ParentBinding.BindingId, out originalRoot!) || originalRoot.Binding != child.ParentBinding ||
                originalRoot.ProviderLease != childRecord.ProviderLease.ParentDomainLease)
                return KernelResult.Fail(KernelError.StaleGeneration, "Original guest admission sources changed before reservation.");
            if (originalRoot.AuthorityRevisionExhausted)
                return KernelResult.Fail(KernelError.CapacityExhausted, "Guest root source revision exhausted.");
            rootRevision = originalRoot.AuthorityRevision;
            if (parent.LocalAuthorizationRevoked || parent.ClosureState != PlatformExternalClosureState.Active ||
                childRecord.State == PlatformChildDomainState.Faulted || childRecord.TransitionInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingRevoked,
                    "Parent mapping or child changed before guest admission.");
            KernelResult CommitAdmission()
            {
                if (parent.PendingGuestMaps == int.MaxValue || sourceRevisions.Keys.Any(x => x.PendingChildEffects == int.MaxValue))
                    return KernelResult.Fail(KernelError.CapacityExhausted, "Guest admission interlock capacity is exhausted.");
                parent.PendingGuestMaps++;
                foreach (var owner in sourceRevisions.Keys) owner.PendingChildEffects++;
                backendEpoch = BackendEpoch;
                return KernelResult.Ok();
            }
            return sourceCommitConsumer is null ? CommitAdmission() : sourceCommitConsumer(CommitAdmission);
          }
        }
        var sourceAdmission = admissionConsumer is null ? Begin() : admissionConsumer(Begin);
        if (!sourceAdmission.IsSuccess)
            return KernelResult<PlatformGuestMapping>.Fail(sourceAdmission.Error, sourceAdmission.Message!);
        try
        {
        KernelResult ValidateSource(PlatformProviderIncarnation source)
        {
            if (source.Value == 0 || source != parent.ProviderIncarnation ||
                BackendEpoch != backendEpoch || parent.BackendEpoch != backendEpoch || _backendEpochExhausted ||
                !_mappings.TryGetValue(parentMapping.MappingId, out var exactParent) || !ReferenceEquals(exactParent, parent) ||
                !_childBindings.TryGetValue(child.BindingId, out var exactChild) || !ReferenceEquals(exactChild, childRecord) ||
                parent.Mapping != parentMapping || childRecord.Binding != child ||
                (_exactMappingSlices.TryGetValue(parentMapping.MappingId,out var liveSlice) ? liveSlice!=parentSlice : parent.AdmissionRequest is not null) ||
                parent.ProviderLease != request.ParentMapping.Lease || childRecord.ProviderLease != request.ChildLease ||
                !_domains.TryGetValue(child.ParentBinding.BindingId, out var root) || !ReferenceEquals(root, originalRoot) || root.Binding != child.ParentBinding ||
                root.ProviderIncarnation != source || root.BackendEpoch != backendEpoch ||
                root.AuthorityRevisionExhausted || root.AuthorityRevision != rootRevision ||
                root.ProviderLease != childRecord.ProviderLease.ParentDomainLease)
            {
                if (invocation is not null) PinOriginalGuestMap(invocation);
                else { childRecord.State = PlatformChildDomainState.Faulted; parent.ClosureState = PlatformExternalClosureState.Faulted; }
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original guest-map source changed; backing remains pinned.");
            }
            var lineage = ValidateActiveChildLineage(childRecord);
            if (!lineage.IsSuccess) return lineage;
            if (!GuestEffectLineageCurrent(sourceRevisions))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original guest ancestor revision/history changed; backing remains pinned.");
            if (parent.LocalAuthorizationRevoked || parent.LocalReservationReleased || parent.ClosureTransitionInFlight ||
                parent.ClosureState != PlatformExternalClosureState.Active || childRecord.TransitionInFlight ||
                !PlatformGuestMemoryContract.ValidateRequest(request, childRecord.State).IsSuccess)
                return KernelResult.Fail(KernelError.PlatformBindingRevoked, "Original guest admission changed after source observation.");
            return KernelResult.Ok();
        }
        PlatformProviderIncarnation source;
        try { source = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformFaulted, "Guest-map source unavailable before invocation."); }
        KernelResult FinalAdmission()
        {
            lock (_secureDomainLifecycleGate)
            {
                var exact = ValidateSource(source);
                if (!exact.IsSuccess) return exact;
                if (_nextGuestBindingId == 0 || _nextGuestBindingId == ulong.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "Guest mapping identity space is exhausted.");
                KernelResult Commit()
                {
                    if (HasUnresolvedGuestMapInvocation(parent) || childRecord.GuestMapInvocations.Any(x => x.Unresolved))
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original guest-map effect is unresolved.");
                    reservedMappingId = new(_nextGuestBindingId++);
                    invocation = new(new(childRecord, parent, originalRoot)
                        { LocalMappingOwner = localMappingOwner, LocalMapping = localMapping }, request, backendEpoch)
                        { Lineage = new(sourceRevisions) };
                    invocation.ReservedIdentity = reservedMappingId;
                    childRecord.GuestMapInvocations.Add(invocation);
                    return KernelResult.Ok();
                }
                return sourceCommitConsumer is null ? Commit() : sourceCommitConsumer(Commit);
            }
        }
        var final = admissionConsumer is null ? FinalAdmission() : admissionConsumer(FinalAdmission);
        if (!final.IsSuccess) return KernelResult<PlatformGuestMapping>.Fail(final.Error, final.Message!);
        PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> result;
        try { result = _provider is IPlatformOriginalGuestMapProvider { SupportsOriginalGuestMapResolution: true } originalGuest
            ? originalGuest.MapOriginalGuest(invocation.OriginalRequest) : provider.MapGuestRegion(request); }
        catch (Exception exception) when (exception is not StackOverflowException)
        {
            lock (_secureDomainLifecycleGate)
            {
                PinOriginalGuestMap(invocation);
            }
            return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformFaulted,
                $"Guest mapping may have taken effect without a receipt: {exception.Message}");
        }
        lock (_secureDomainLifecycleGate)
        {
            invocation.Observation = result;
            if (result.Status == PlatformAuthorityStatus.NotAccepted) invocation.Unresolved = false;
        }
        if (!result.IsSuccess)
        {
            if (result.Status != PlatformAuthorityStatus.NotAccepted)
            {
                lock (_secureDomainLifecycleGate)
                {
                    PinOriginalGuestMap(invocation);
                }
            }
            return FromProviderFailure<PlatformGuestMapping>(result.Status, result.Message);
        }
        var leaseValidation = PlatformGuestMemoryContract.ValidateLease(request, result.Value!);
        if (!leaseValidation.IsSuccess)
        {
            var fact = new UnpublishedGuestMapReceipt(child, parentMapping, request,
                backendEpoch, parent.ProviderIncarnation, result.Value!);
            lock (_secureDomainLifecycleGate)
            {
                parent.UnpublishedGuestMaps.Add(fact);
                PinOriginalGuestMap(invocation);
            }
            // A malformed returned tuple is not continuity of this original request.
            // Retain it; only original request resolution may identify the cleanup target.
            return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformFaulted, leaseValidation.Message!);
        }
        PlatformGuestMapping mapping;
        GuestBindingRecord knownRecord;
        lock (_secureDomainLifecycleGate)
        {
            mapping = new PlatformGuestMapping(reservedMappingId, new(1), child, parentMapping);
            knownRecord = new GuestBindingRecord(mapping, result.Value!)
                { AdmissionSource = invocation.Source };
            _guestBindings.Add(mapping.MappingId, knownRecord);
            _originalGuestOwners.Add(mapping.MappingId, knownRecord);
            invocation.KnownMapping = knownRecord;
            invocation.Unresolved = false; // Exact known owner now retains this effect's lifetime.
            knownMapping = mapping;
        }
        try { source = CurrentProviderIncarnation(); }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformFaulted, "Known guest source unavailable; exact cleanup retained."); }
        lock (_secureDomainLifecycleGate)
        {
            if (!HasCurrentOriginalGuestOwner(knownRecord))
                return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformFaulted,
                    "Original guest owner changed after source observation; exact receipt remains retained.");
            var sourceValid = ValidateSource(source);
            if (BackendEpoch != backendEpoch || childRecord.State == PlatformChildDomainState.Faulted || parent.ClosureState == PlatformExternalClosureState.Faulted)
            {
                PinOriginalGuestMap(invocation);
                return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformFaulted,
                    "Guest mapping changed before local publication; parent remains pinned.");
            }
            if (!sourceValid.IsSuccess && !parent.LocalAuthorizationRevoked)
                return KernelResult<PlatformGuestMapping>.Fail(sourceValid.Error, sourceValid.Message!);
            if (parent.LocalAuthorizationRevoked || parent.ClosureState != PlatformExternalClosureState.Active)
            {
                if (sourceCommitConsumer is null)
                {
                    PinOriginalGuestMap(invocation);
                    return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformFaulted,
                        "Parent permission changed during guest admission; exact known lease remains pinned.");
                }
                return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformBindingRevoked,
                    "Parent permission changed; exact known guest lease retained for cleanup.");
            }
            // The known lease belongs to the existing guest owner even when late
            // ancestor permission is gone; it must still pin the parent for cleanup.
            var lineage = ValidateActiveChildLineage(childRecord);
            if (!lineage.IsSuccess)
                return KernelResult<PlatformGuestMapping>.Fail(lineage.Error, lineage.Message!);
        }
        KernelResult CommitCurrent(Func<KernelResult> commit)
        {
            lock (_secureDomainLifecycleGate)
            {
                if (!HasCurrentOriginalGuestOwner(knownRecord))
                    return KernelResult.Fail(KernelError.PlatformFaulted,
                        "Original guest owner changed before local publication; exact receipt remains retained.");
                var exactSource = ValidateSource(source);
                if (!exactSource.IsSuccess) return exactSource;
                var lineage = ValidateActiveChildLineage(childRecord);
                if (!lineage.IsSuccess) return lineage;
                if (BackendEpoch != backendEpoch || parent.LocalAuthorizationRevoked ||
                    parent.ClosureState != PlatformExternalClosureState.Active || childRecord.TransitionInFlight)
                    return KernelResult.Fail(KernelError.PlatformBindingRevoked, "Guest owner changed before local publication.");
                var committed = commit();
                if (committed.IsSuccess) knownRecord.AdmissionPublished = true;
                return committed;
            }
        }
        var publication = completionConsumer is null ? CommitCurrent(() => KernelResult.Ok()) : completionConsumer(mapping, CommitCurrent);
        if (publication.IsSuccess && !knownRecord.AdmissionPublished)
            return KernelResult<PlatformGuestMapping>.Fail(KernelError.PlatformFaulted, "Exact guest receipt was retained without admission publication.");
        return publication.IsSuccess ? KernelResult<PlatformGuestMapping>.Ok(mapping) :
            KernelResult<PlatformGuestMapping>.Fail(publication.Error, publication.Message!);
        }
        finally
        {
            lock (_secureDomainLifecycleGate)
            {
                parent.PendingGuestMaps--;
                foreach (var owner in sourceRevisions.Keys) owner.PendingChildEffects--;
            }
        }
    }

    private KernelResult<Dictionary<ChildBindingRecord, long>> CaptureGuestEffectLineage(ChildBindingRecord child)
    {
        var captured = new Dictionary<ChildBindingRecord, long>();
        for (ChildBindingRecord? current = child; current is not null; current = current.CreateParentChild)
        {
            if (captured.ContainsKey(current) || current.LifecycleRevisionExhausted || !HasCurrentChildCreateSources(current) ||
                !HasOriginalNestedHistory(current) || current.TransitionInFlight || current.CloseInFlight)
                return KernelResult<Dictionary<ChildBindingRecord, long>>.Fail(KernelError.PlatformFaulted, "Original guest lineage unavailable or pending.");
            captured.Add(current, current.LifecycleRevision);
        }
        return KernelResult<Dictionary<ChildBindingRecord, long>>.Ok(captured);
    }
    private bool GuestEffectLineageCurrent(IReadOnlyDictionary<ChildBindingRecord, long> captured) => captured.All(pair =>
        !pair.Key.LifecycleRevisionExhausted && pair.Key.LifecycleRevision == pair.Value &&
        HasCurrentChildCreateSources(pair.Key) && HasOriginalNestedHistory(pair.Key));

    internal KernelResult CommitGuestLocalMappingSource(PlatformGuestMapping mapping,
        VirtualDomainAuthority.Record localOwner, GuestRegionMappingHandle localMapping, Func<KernelResult> commit)
    {
        lock (_secureDomainLifecycleGate)
        {
            if (!_guestBindings.TryGetValue(mapping.MappingId, out var guest) || guest.Mapping != mapping ||
                !HasCurrentGuestAdmissionSources(guest) || guest.AdmissionSource is not { } original ||
                !ReferenceEquals(original.LocalMappingOwner, localOwner) || original.LocalMapping != localMapping)
                return KernelResult.Fail(KernelError.StaleGeneration, "Original guest/local mapping owner association changed.");
            // Identity correlation only: active admission and exact closure are independently checked.
            return commit(); // Trusted pure local source/finalization mutation, no provider callback.
        }
    }

    // Trusted pure source admission/publication; pending is lifetime, never permission.
    internal KernelResult CommitSecureGuestMapping(PlatformGuestMapping mapping, PlatformRegionMapping parent,
        bool begin, Func<KernelResult> commit, out Action? endPending,
        out Func<Func<KernelResult>, KernelResult>? publishPending)
    {
        endPending = null;
        publishPending = null;
        lock (_secureDomainLifecycleGate)
        {
            var child = ResolveChild(mapping.Child);
            if (!child.IsSuccess) return KernelResult.Fail(child.Error, child.Message!);
            var lineage = ValidateActiveChildLineage(child.Value!);
            if (!lineage.IsSuccess) return lineage;
            if (!_guestBindings.TryGetValue(mapping.MappingId, out var guest) || guest.Mapping != mapping ||
                guest.Closure != PlatformExternalClosureState.Active || !guest.AdmissionPublished || !HasCurrentGuestAdmissionSources(guest) || mapping.ParentMapping != parent ||
                !_mappings.TryGetValue(parent.MappingId, out var backing) || backing.Mapping != parent ||
                backing.LocalAuthorizationRevoked || backing.ClosureState != PlatformExternalClosureState.Active ||
                child.Value!.TransitionInFlight || child.Value.CloseInFlight)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact secure guest mapping/parent is not active.");
            if (begin && (guest.PendingSecureRegionEffects != 0 || guest.PendingExecutableEffects != 0) ||
                !begin && guest.PendingSecureRegionEffects != 1)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Exact secure guest mapping admission changed.");
            if (begin && child.Value!.PendingChildEffects == int.MaxValue)
                return KernelResult.Fail(KernelError.CapacityExhausted, "Secure guest child interlock capacity is exhausted.");
            // Capture the admitted owners before the external effect. This completes only
            // the local admission interlock, independently of current routes or provider closure.
            var originalChild = child.Value!;
            bool ended = false;
            Action finish = () =>
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (ended) return;
                    ended = true;
                    guest.PendingSecureRegionEffects--;
                    originalChild.PendingChildEffects--;
                }
            };
            KernelResult Publish(Func<KernelResult> publication)
            {
                lock (_secureDomainLifecycleGate)
                {
                    if (ended || !_guestBindings.TryGetValue(mapping.MappingId, out var currentGuest) ||
                        !ReferenceEquals(currentGuest, guest) ||
                        !_childBindings.TryGetValue(mapping.Child.BindingId, out var currentChild) ||
                        !ReferenceEquals(currentChild, originalChild))
                        return KernelResult.Fail(KernelError.StaleGeneration, "Original secure guest pending owners changed before publication.");
                    return CommitSecureGuestMapping(mapping, parent, false, publication, out _, out _);
                }
            }
            var result = commit();
            if (result.IsSuccess && begin)
            {
                guest.PendingSecureRegionEffects++;
                originalChild.PendingChildEffects++;
                endPending = finish;
                publishPending = Publish;
            }
            return result;
        }
    }

    // Completed original lifetime only; this fact grants no mapping-use admission.
    private static bool HasValidatedGuestClosureFact(GuestBindingRecord record) =>
        record.LastClosureReceipt is { } fact && fact.AdmittedLease == record.ProviderLease &&
        record.AdmissionSource is { } source && fact.AdmissionEpoch == source.Parent.BackendEpoch &&
        source.Parent.ProviderLease == record.ProviderLease.ParentMappingLease &&
        source.Child.ProviderLease == record.ProviderLease.ChildLease &&
        PlatformGuestMemoryContract.ValidateClosureReceipt(record.ProviderLease, fact.Receipt).IsSuccess;

    // Caller holds the existing lifecycle gate; completed dependency facts must
    // belong to this original parent, independently of equal public tuples.
    private bool HasUnclosedGuestDependency(MappingRecord parent) =>
        parent.PendingGuestMaps != 0 || HasUnresolvedGuestMapInvocation(parent) || _originalGuestOwners.Values.Concat(_guestBindings.Values).Any(guest =>
            guest.Mapping.ParentMapping.MappingId == parent.Mapping.MappingId &&
            (!HasCurrentOriginalGuestOwner(guest) || guest.Closure != PlatformExternalClosureState.Closed || !HasValidatedGuestClosureFact(guest) ||
             guest.AdmissionSource is not { } source || !ReferenceEquals(source.Parent, parent)));

    private bool HasUnclosedGuestDependency(ChildBindingRecord child) =>
        child.GuestMapInvocations.Any(x => x.Unresolved) ||
        _originalGuestOwners.Values.Concat(_guestBindings.Values).Any(guest => guest.Mapping.Child == child.Binding &&
            (!HasCurrentOriginalGuestOwner(guest) || guest.Closure != PlatformExternalClosureState.Closed || !HasValidatedGuestClosureFact(guest) ||
             guest.AdmissionSource is not { } source || !ReferenceEquals(source.Child, child)));

    private bool HasUnresolvedGuestMapInvocation(MappingRecord parent) =>
        _originalChildOwners.Values.Concat(_childBindings.Values).Any(child =>
            child.GuestMapInvocations.Any(fact => (fact.Unresolved || fact.ResolutionInFlight) &&
                fact.Source.Parent.Mapping.MappingId == parent.Mapping.MappingId));

    internal KernelResult UnmapChildGuestRegion(PlatformGuestMapping mapping, bool allowCommittedClosure = false)
    {
        var releasedArtifacts = ReleaseArtifactsForGuestMapping(mapping);
        if (!releasedArtifacts.IsSuccess) return releasedArtifacts;
        PlatformChildBinding? cleanupChild = null;
        lock (_secureDomainLifecycleGate)
        {
            if (allowCommittedClosure && _guestBindings.TryGetValue(mapping.MappingId, out var guest) &&
                guest.Mapping == mapping && guest.Closure == PlatformExternalClosureState.Active &&
                HasCurrentGuestAdmissionSources(guest) && guest.AdmissionSource is { } admitted &&
                admitted.Child.State == PlatformChildDomainState.Faulted)
            {
                if (guest.ClosureInFlight || guest.PendingExecutableEffects != 0 || guest.PendingSecureRegionEffects != 0 ||
                    _originalExecutableArtifactOwners.Values.Any(artifact => !artifact.Released && artifact.Binding.GuestMapping == mapping))
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "Guest cleanup dependencies remain pending.");
                cleanupChild = admitted.Child.Binding;
            }
        }
        if (cleanupChild is { } capturedChild)
        {
            // Existing cleanup admission consumes only exact retained transition facts.
            // Provider drain/source callbacks stay outside the guest owner lock; the
            // ordinary unmap admission below rechecks every source after this callback.
            var drain = TransitionChildDomain(capturedChild, PlatformChildDomainTransition.BeginDrain, allowCommittedDrain: true);
            if (!drain.IsSuccess) return drain;
        }
        GuestBindingRecord record;
        ChildBindingRecord child;
        MappingRecord parent;
        PlatformBackendEpoch epoch;
        PlatformExternalClosureState originalClosure;
        GuestUnmapRefusalFact? refusal;
        bool supportsOriginalUnmap;
        try { supportsOriginalUnmap = _provider is IPlatformOriginalGuestUnmapProvider { SupportsOriginalGuestUnmapResolution: true }; }
        catch { return KernelResult.Fail(KernelError.PlatformFaulted, "Guest resolution source unavailable."); }
        lock (_secureDomainLifecycleGate)
        {
            if (!_guestBindings.TryGetValue(mapping.MappingId, out record!))
                return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Guest mapping was not found.");
            if (record.Mapping.Generation != mapping.Generation)
                return KernelResult.Fail(KernelError.StaleGeneration, "Guest mapping generation is stale.");
            if (record.Mapping != mapping)
                return KernelResult.Fail(KernelError.WrongPlatformDomain, "Guest mapping belongs to another child or parent mapping.");
            if (record.Closure == PlatformExternalClosureState.Closed)
            {
                if (record.ClosureInFlight || record.PendingExecutableEffects != 0 || record.PendingSecureRegionEffects != 0 ||
                    !HasValidatedGuestClosureFact(record) || !HasCurrentGuestAdmissionSources(record))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Closed guest projection lacks exact original closure or has pending dependents.");
                return allowCommittedClosure ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformBindingRevoked, "Guest mapping is already closed.");
            }
            if (record.Closure == PlatformExternalClosureState.Faulted && !HasOwnGuestUnmapFault(record))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Guest mapping remains quarantined.");
            if (record.ClosureInFlight || record.Closure == PlatformExternalClosureState.Draining && record.OriginalUnmap is not { Supports: true, Invoked: true } &&
                record.LastClosureReceipt is null && record.LastUnmapRefusal is null)
                return KernelResult.Fail(KernelError.PlatformBindingRevoked, "Guest closure is already in progress.");
            if (!_childBindings.TryGetValue(mapping.Child.BindingId, out child!) || child.Binding != mapping.Child ||
                !_mappings.TryGetValue(mapping.ParentMapping.MappingId, out parent!) || parent.Mapping != mapping.ParentMapping)
                return KernelResult.Fail(KernelError.StaleGeneration, "Exact guest child/parent owner is unavailable.");
            if (!HasCurrentGuestAdmissionSources(record))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original guest admission owners changed; unmap refused before reservation or effect.");
            if (record.PendingExecutableEffects != 0 || record.PendingSecureRegionEffects != 0 ||
                _originalExecutableArtifactOwners.Values.Any(artifact => !artifact.Released && artifact.Binding.GuestMapping == mapping))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Guest dependent effects or artifact release remain pending.");
            if (_provider is not IPlatformGuestMemoryProvider)
                return KernelResult.Fail(KernelError.PlatformUnsupported, "Guest-memory provider is unavailable.");
            epoch = parent.BackendEpoch;
            originalClosure = record.Closure;
            refusal = record.LastUnmapRefusal;
            record.ClosureInFlight = true;
        }
        try
        {
            PlatformProviderIncarnation source;
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, "Guest unmap source is unavailable; original state and cleanup facts are retained."); }
            lock (_secureDomainLifecycleGate)
            {
                var exact = ValidateGuestUnmapSourceLocked(record, child, parent, epoch, source);
                if (!exact.IsSuccess) return exact;
                if (refusal is not null)
                    return RestoreRefusedGuestUnmapLocked(record, child, parent, epoch, refusal);
                // The external source query can admit a dependent consumer while
                // this mapping is still Active. Recheck before staging/provider unmap.
                if (record.Closure != originalClosure || record.PendingExecutableEffects != 0 ||
                    record.PendingSecureRegionEffects != 0 ||
                    _originalExecutableArtifactOwners.Values.Any(artifact => !artifact.Released && artifact.Binding.GuestMapping == mapping))
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "Guest dependent owner changed before unmap invocation.");
                record.Closure = PlatformExternalClosureState.Draining;
                record.OriginalUnmap ??= new(record, child, parent, supportsOriginalUnmap, originalClosure);
                record.OriginalUnmap.Revision = record.LifecycleRevision;
                record.OriginalUnmap.OwnFault = false;
                record.LastUnmapRefusal = null;
            }

            if (record.LastClosureReceipt is null)
            {
                PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt> result;
                try
                {
                    var invocation = record.OriginalUnmap!;
                    bool pure = invocation.Invoked;
                    if (pure)
                        result = invocation.Supports && _provider is IPlatformOriginalGuestUnmapProvider resolver
                            ? resolver.ResolveOriginalGuestUnmap(record.ProviderLease)
                            : PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original guest effect cannot be repeated.");
                    else
                    {
                        lock (_secureDomainLifecycleGate) invocation.Invoked = true;
                        result = ((IPlatformGuestMemoryProvider)_provider!).UnmapGuestRegion(record.ProviderLease);
                    }
                    lock (_secureDomainLifecycleGate)
                    {
                        if (pure) invocation.Resolution = result;
                        else invocation.Raw = result;
                    }
                }
                catch (Exception exception) when (exception is not StackOverflowException)
                {
                    lock (_secureDomainLifecycleGate) PinOriginalGuestUnmap(record);
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Guest unmap outcome is unknown; reclaim remains pinned.");
                }
                if (result.Status == PlatformAuthorityStatus.NotAccepted)
                {
                    lock (_secureDomainLifecycleGate)
                    {
                        if (originalClosure != PlatformExternalClosureState.Active && record.OriginalUnmap is not { Supports: true, OriginalState: PlatformExternalClosureState.Active })
                        {
                            record.Closure = PlatformExternalClosureState.Faulted;
                            return KernelResult.Fail(KernelError.PlatformFaulted, "Refused unmap has no original Active owner.");
                        }
                        record.LastUnmapRefusal = refusal = new(record.ProviderLease, epoch, child, parent);
                    }
                    // Roll back only this local staged drain, never a provider
                    // effect or a known closure fact. Source queries may reenter.
                    try { source = CurrentProviderIncarnation(); }
                    catch (Exception exception) when (exception is not StackOverflowException)
                    { return KernelResult.Fail(KernelError.PlatformFaulted, "Refused guest unmap source unavailable; staged owner remains pinned."); }
                    lock (_secureDomainLifecycleGate)
                    {
                        var exact = ValidateGuestUnmapSourceLocked(record, child, parent, epoch, source);
                        if (!exact.IsSuccess) return exact;
                        return RestoreRefusedGuestUnmapLocked(record, child, parent, epoch, refusal!);
                    }
                }
                lock (_secureDomainLifecycleGate)
                {
                    // Preserve the original returned fact before any late source
                    // query, receipt validation or reset/publication refusal.
                    if (result.IsSuccess) record.LastClosureReceipt = new(record.ProviderLease, epoch, result.Value!);
                    else
                    {
                        PinOriginalGuestUnmap(record);
                        return FromProviderFailure(result.Status, result.Message);
                    }
                }
            }
            var fact = record.LastClosureReceipt!;
            var validation = PlatformGuestMemoryContract.ValidateClosureReceipt(record.ProviderLease, fact.Receipt);
            if (fact.AdmittedLease != record.ProviderLease || fact.AdmissionEpoch != epoch || !validation.IsSuccess)
            {
                lock (_secureDomainLifecycleGate) record.Closure = PlatformExternalClosureState.Faulted;
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original guest closure receipt is malformed or belongs to another source.");
            }
            try { source = CurrentProviderIncarnation(); }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, "Known guest closure source is unavailable; exact receipt is retained for retry."); }
            lock (_secureDomainLifecycleGate)
            {
                var exact = ValidateGuestUnmapSourceLocked(record, child, parent, epoch, source);
                if (!exact.IsSuccess) return exact;
                if (record.Closure != PlatformExternalClosureState.Draining)
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Guest closure state changed before local commit.");
                record.Closure = PlatformExternalClosureState.Closed;
                return KernelResult.Ok();
            }
        }
        finally { lock (_secureDomainLifecycleGate) record.ClosureInFlight = false; }
    }

    private KernelResult RestoreRefusedGuestUnmapLocked(GuestBindingRecord record,
        ChildBindingRecord child, MappingRecord parent, PlatformBackendEpoch epoch, GuestUnmapRefusalFact refusal)
    {
        if (!ReferenceEquals(record.LastUnmapRefusal, refusal) || refusal.AdmittedLease != record.ProviderLease ||
            refusal.AdmissionEpoch != epoch || !ReferenceEquals(refusal.Child, child) || !ReferenceEquals(refusal.Parent, parent) ||
            record.Closure != PlatformExternalClosureState.Draining || record.LastClosureReceipt is not null ||
            record.PendingExecutableEffects != 0 || record.PendingSecureRegionEffects != 0 ||
            _originalExecutableArtifactOwners.Values.Any(artifact => !artifact.Released && artifact.Binding.GuestMapping == record.Mapping))
        {
            record.Closure = PlatformExternalClosureState.Faulted;
            return KernelResult.Fail(KernelError.PlatformFaulted, "Refused guest unmap owner changed; staged state cannot be restored.");
        }
        record.Closure = PlatformExternalClosureState.Active;
        record.LastUnmapRefusal = null;
        record.OriginalUnmap = null;
        // Recovery performs no provider call and cannot advance the reclaim consumer.
        return FromProviderFailure(PlatformAuthorityStatus.NotAccepted, "Guest unmap was not invoked; fresh unmap admission is required.");
    }

    // Identity continuity for cleanup; this does not renew mapping use or root permission.
    private bool HasCurrentOriginalGuestOwner(GuestBindingRecord record) =>
        _originalGuestOwners.TryGetValue(record.Mapping.MappingId, out var original) && ReferenceEquals(original, record) &&
        _guestBindings.TryGetValue(record.Mapping.MappingId, out var route) && ReferenceEquals(route, record);

    private bool HasCurrentGuestAdmissionSources(GuestBindingRecord record) =>
        HasCurrentOriginalGuestOwner(record) && record.AdmissionSource is { } admitted &&
        _childBindings.TryGetValue(record.Mapping.Child.BindingId, out var child) && ReferenceEquals(child, admitted.Child) &&
        _mappings.TryGetValue(record.Mapping.ParentMapping.MappingId, out var parent) && ReferenceEquals(parent, admitted.Parent) &&
        _domains.TryGetValue(record.Mapping.Child.ParentBinding.BindingId, out var root) && ReferenceEquals(root, admitted.Root) &&
        HasCurrentChildCreateSources(child) && child.Binding == record.Mapping.Child && parent.Mapping == record.Mapping.ParentMapping &&
        root.Binding == record.Mapping.Child.ParentBinding && root.ProviderLease == record.ProviderLease.ChildLease.ParentDomainLease;

    private KernelResult ValidateGuestUnmapSourceLocked(GuestBindingRecord record,
        ChildBindingRecord child, MappingRecord parent, PlatformBackendEpoch epoch, PlatformProviderIncarnation source)
    {
        var mapping = record.Mapping;
        if (!HasCurrentGuestAdmissionSources(record) || _backendEpochExhausted || BackendEpoch != epoch || parent.BackendEpoch != epoch ||
            source.Value == 0 || source != parent.ProviderIncarnation ||
            !_guestBindings.TryGetValue(mapping.MappingId, out var current) || !ReferenceEquals(current, record) ||
            !_childBindings.TryGetValue(mapping.Child.BindingId, out var currentChild) || !ReferenceEquals(currentChild, child) ||
            !_mappings.TryGetValue(mapping.ParentMapping.MappingId, out var currentParent) || !ReferenceEquals(currentParent, parent) ||
            record.ProviderLease.ChildLease != child.ProviderLease || record.ProviderLease.ParentMappingLease != parent.ProviderLease ||
            child.State is PlatformChildDomainState.Faulted or PlatformChildDomainState.Closed ||
            parent.ClosureState == PlatformExternalClosureState.Faulted || parent.LocalReservationReleased ||
            record.Closure == PlatformExternalClosureState.Faulted && !HasOwnGuestUnmapFault(record) ||
            record.OriginalUnmap is { Supports: true } original &&
            (!ReferenceEquals(original.OriginalOwner, original) || !ReferenceEquals(original.Guest, record) ||
             !ReferenceEquals(original.Child, child) || !ReferenceEquals(original.Parent, parent) ||
             child.LifecycleRevisionExhausted || parent.LifecycleRevisionExhausted ||
            child.LifecycleRevision != original.ChildRevision || parent.LifecycleRevision != original.ParentRevision ||
             record.LifecycleRevisionExhausted || record.Closure == PlatformExternalClosureState.Draining && record.LifecycleRevision != original.Revision))
        {
            if (record.OriginalUnmap is { Supports: true }) PinOriginalGuestUnmap(record);
            else record.Closure = PlatformExternalClosureState.Faulted;
            return KernelResult.Fail(KernelError.PlatformFaulted, "Exact guest source/owner continuity changed; original receipt and parent pin are retained.");
        }
        // Exact original Unmap is cleanup of this admitted guest/parent lifetime.
        // Root quarantine withdraws new permission, not this original closure path.
        return ValidateMappingIdentity(mapping.ParentMapping, mapping.Child.ParentBinding.Subject,requireActiveDomain:false);
    }
}
