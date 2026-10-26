using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private bool HasIntactArtifactRelease(ExecutableArtifactRecord artifact) => artifact.ReleaseInvoked && artifact.OriginalRelease is { } original &&
        artifact.ReleaseRequest == original.Request && original.Observation.IsSuccess && original.Observation.Value.Request == original.Request &&
        original.Observation.Value.NoFurtherUse &&
        (artifact.ReleaseObservation == original.Observation || artifact.ReleaseResolutionObservation == original.Observation);
    private bool HasRetainedArtifactRelease(ExecutableArtifactRecord artifact) => artifact.Released && HasIntactArtifactRelease(artifact) &&
        _originalExecutableArtifactOwners.TryGetValue(artifact.Binding.BindingId,out ExecutableArtifactRecord? original) && ReferenceEquals(original,artifact);
    private bool HasDamagedConsumedArtifactRelease(Func<ExecutableArtifactRecord,bool> belongsToOwner) =>
        _originalExecutableArtifactOwners.Values.Any(artifact=>belongsToOwner(artifact) && artifact.Released &&
            (artifact.ReleaseInvoked || artifact.OriginalRelease is not null || artifact.ReleaseRequest is not null) && !HasRetainedArtifactRelease(artifact));
    private bool HasOriginalArtifactReleaseSources(ExecutableArtifactRecord artifact, PlatformProviderIncarnation incarnation) =>
        _originalExecutableArtifactOwners.TryGetValue(artifact.Binding.BindingId, out ExecutableArtifactRecord? original) && ReferenceEquals(original, artifact) &&
        _executableArtifacts.TryGetValue(artifact.Binding.BindingId, out ExecutableArtifactRecord? route) && ReferenceEquals(route, artifact) &&
        artifact.AdmissionSource is { } source && HasCurrentGuestAdmissionSources(source.Guest) &&
        (artifact.BindInvocation is not { } bind || ArtifactReleaseLineageCurrent(bind.Ancestors)) &&
        (artifact.StartInvocation is not { } start || ArtifactReleaseLineageCurrent(start.Ancestors)) &&
        _childBindings.TryGetValue(source.Child.Binding.BindingId, out ChildBindingRecord? child) && ReferenceEquals(child, source.Child) &&
        _guestBindings.TryGetValue(source.Guest.Mapping.MappingId, out GuestBindingRecord? guest) && ReferenceEquals(guest, source.Guest) &&
        _mappings.TryGetValue(source.Parent.Mapping.MappingId, out MappingRecord? parent) && ReferenceEquals(parent, source.Parent) &&
        TryGetCanonicalRootRecord(source.Root.Binding.BindingId, out DomainRecord? root) && ReferenceEquals(root, source.Root) &&
        incarnation.Value != 0 && incarnation == source.Root.ProviderIncarnation && incarnation == source.Parent.ProviderIncarnation &&
        source.Root.BackendEpoch == BackendEpoch && source.Parent.BackendEpoch == BackendEpoch && !_backendEpochExhausted;

    // Cleanup of an already admitted artifact does not deliver an ancestor's event.
    // Only that retained invocation's exact own fault may explain revision drift;
    // its independent mailbox/root pin remains until original containment.
    private bool ArtifactReleaseLineageCurrent(IReadOnlyDictionary<ChildBindingRecord, long> captured) => captured.All(pair =>
        !pair.Key.LifecycleRevisionExhausted && HasCurrentChildCreateSources(pair.Key) && HasOriginalNestedHistory(pair.Key) &&
        (pair.Key.LifecycleRevision == pair.Value ||
         pair.Key.State == PlatformChildDomainState.Faulted &&
         pair.Key.EventsConsumed <= pair.Key.EventsIssued &&
         pair.Key.EventsIssued - pair.Key.EventsConsumed == (ulong)pair.Key.EventInvocations.Count &&
         pair.Key.EventInvocations.All(invocation => ReferenceEquals(invocation.OwnerSelf, invocation)) &&
         pair.Key.EventInvocations.Any(invocation => invocation.EventOriginal is not null &&
             invocation.Request.ChildLease == pair.Key.ProviderLease && ReferenceEquals(invocation.OriginalRoot, pair.Key.CreateRoot) &&
             invocation.AdmittedChildRevision == pair.Value && invocation.OwnFaultRevision == pair.Key.LifecycleRevision)));

    private KernelResult ReleaseArtifactsForGuestMapping(PlatformGuestMapping mapping)
    {
        ExecutableArtifactRecord[] artifacts;
        lock (_secureDomainLifecycleGate)
        {
            if(_originalExecutableArtifactOwners.Values.Any(x=>x.Binding.GuestMapping==mapping && x.Released &&
                (x.ReleaseInvoked || x.OriginalRelease is not null || x.ReleaseRequest is not null) && !HasRetainedArtifactRelease(x)))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Consumed original artifact release evidence unavailable; backing pins retained.");
            foreach (ExecutableArtifactRecord? completed in _originalExecutableArtifactOwners.Values.Where(x => x.Binding.GuestMapping == mapping &&
                x.Released && x.BindInvocation is { NoEffectConsumed: true, DependencyFailureConsumed: false }))
            {
                ConsumeExecutableStartFailureAfterRelease(completed);
                if (!completed.BindInvocation!.DependencyFailureConsumed)
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Independent users still pin completed Bind no-effect cleanup.");
            }
            artifacts = _originalExecutableArtifactOwners.Values.Where(x => x.Binding.GuestMapping == mapping && !x.Released).ToArray();
            if (artifacts.Any(x => HasSoftwareArtifactHistory(x) && (x.SoftwareEntryActive || !HasClosedSoftwareArtifactEntry(x))))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Original software artifact closure remains pinned.");
            if (artifacts.Length != 0 && !artifacts.All(x => HasOwnExecutableStartFailure(x) || HasClosedSoftwareArtifactEntry(x)) && _guestBindings.TryGetValue(mapping.MappingId, out GuestBindingRecord? guestSource) &&
                (guestSource.Closure == PlatformExternalClosureState.Faulted ||
                 _mappings.TryGetValue(mapping.ParentMapping.MappingId, out MappingRecord? backing) &&
                 (backing.ClosureState == PlatformExternalClosureState.Faulted || backing.LocalAuthorizationRevoked && !backing.OriginalPermissionRetiredForCleanup)))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Faulted/revoked backing source remains pinned before artifact release.");
            if (_executableArtifacts.Values.Any(x => x.Binding.GuestMapping == mapping &&
                (!_originalExecutableArtifactOwners.TryGetValue(x.Binding.BindingId, out ExecutableArtifactRecord? original) ||
                 !ReferenceEquals(original, x) && (!original.Released || original.Binding.GuestMapping != mapping))))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Foreign artifact route cannot authorize guest backing release.");
        }
        foreach (ExecutableArtifactRecord artifact in artifacts)
        {
            if (artifact.ProviderReceipt.ArtifactId.Value == 0)
            {
                KernelResult resolved = ResolveArtifactBindForCleanup(artifact);
                if (!resolved.IsSuccess) return resolved;
                lock (_secureDomainLifecycleGate) if (artifact.Released) continue;
            }
            if (_provider is not IPlatformExecutableArtifactReleaseProvider provider)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Artifact release provider is unavailable; guest backing remains pinned.");
            ExecutableArtifactSource admitted;
            PlatformArtifactReleaseRequest request;
            bool resolve;
            ChildBindingRecord[] lineage;
            lock (_secureDomainLifecycleGate)
            {
                if (artifact.AdmissionSource is not { } source || artifact.StartInFlight || artifact.ReleaseInFlight ||
                    source.Child.PendingChildEffects != 0 || source.Guest.PendingExecutableEffects != 0 ||
                    source.Guest.ClosureInFlight || !HasCurrentGuestAdmissionSources(source.Guest))
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "Original artifact release dependencies are in flight or absent.");
                admitted = source;
                lineage = (artifact.BindInvocation?.Ancestors.Keys ?? Enumerable.Empty<ChildBindingRecord>()).Append(admitted.Child).ToArray();
                if (lineage.Any(owner => owner.PendingChildEffects != 0 || owner.CloseInFlight || owner.TransitionInFlight))
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "Original artifact ancestor release is in flight.");
                if (admitted.Guest.PendingExecutableEffects == int.MaxValue || admitted.Child.PendingChildEffects == int.MaxValue)
                    return KernelResult.Fail(KernelError.CapacityExhausted, "Artifact release interlock capacity exhausted before invocation.");
                if (artifact.ReleaseRequest is null && (artifact.ReleaseInvoked || artifact.ReleaseObservation is not null || artifact.ReleaseResolutionObservation is not null))
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original artifact release request history lost; replacement invocation prohibited.");
                if(artifact.OriginalRelease is not null && !HasIntactArtifactRelease(artifact))
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original artifact release custody lost; no replacement lookup/effect.");
                artifact.ReleaseRequest ??= new(Guid.NewGuid(), artifact.ProviderReceipt);
                request = artifact.ReleaseRequest.Value;
                resolve = artifact.ReleaseInvoked;
                artifact.ReleaseInFlight = true;
                admitted.Guest.PendingExecutableEffects++;
                foreach (ChildBindingRecord owner in lineage) owner.PendingChildEffects++;
            }
            try
            {
                PlatformProviderIncarnation incarnation = CurrentProviderIncarnation(); // Outside bridge owner lock.
                lock (_secureDomainLifecycleGate)
                {
                    if (!HasOriginalArtifactReleaseSources(artifact, incarnation))
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original artifact release source changed before invocation/lookup.");
                    if (!resolve) artifact.ReleaseInvoked = true;
                }
                PlatformAuthorityResult<PlatformArtifactReleaseReceipt> result;
                if (artifact.ReleaseObservation is { IsSuccess: true } retained) result = retained;
                else if (artifact.ReleaseResolutionObservation is { IsSuccess: true } retainedQuery) result = retainedQuery;
                else result = resolve ? provider.ResolveExecutableArtifactRelease(request) : provider.ReleaseExecutableArtifact(request);
                lock (_secureDomainLifecycleGate)
                {
                    if (resolve) artifact.ReleaseResolutionObservation = result;
                    else artifact.ReleaseObservation = result;
                    if(result.IsSuccess && result.Value.Request == request && result.Value.NoFurtherUse)artifact.OriginalRelease ??= (request,result);
                    if (result.Status == PlatformAuthorityStatus.NotAccepted) artifact.ReleaseInvoked = false;
                }
                incarnation = CurrentProviderIncarnation();
                lock (_secureDomainLifecycleGate)
                {
                    if (!HasOriginalArtifactReleaseSources(artifact, incarnation) || artifact.ReleaseRequest != request ||
                        result.IsSuccess && (!HasIntactArtifactRelease(artifact) || artifact.OriginalRelease!.Value.Observation != result))
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original artifact release raw outcome retained without current continuity.");
                    if (!result.IsSuccess) return result.Status == PlatformAuthorityStatus.Unsupported
                        ? KernelResult.Fail(KernelError.PlatformBindingActive, result.Message ?? "Artifact release unavailable; backing remains pinned.")
                        : FromProviderFailure(result.Status, result.Message);
                    if (result.Value.Request != request || !result.Value.NoFurtherUse)
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Artifact release lacks exact original no-further-use proof.");
                    artifact.Released = true; // Distinct from guest/mapping closure and quantitative reclaim.
                    ConsumeExecutableStartFailureAfterRelease(artifact);
                }
            }
            catch (Exception exception) when (exception is not StackOverflowException)
            { return KernelResult.Fail(KernelError.PlatformFaulted, $"Artifact release retained/unknown: {exception.GetType().Name}"); }
            finally
            {
                lock (_secureDomainLifecycleGate)
                {
                    artifact.ReleaseInFlight = false;
                    admitted.Guest.PendingExecutableEffects--;
                    foreach (ChildBindingRecord owner in lineage) owner.PendingChildEffects--;
                }
            }
        }
        return KernelResult.Ok();
    }
}

