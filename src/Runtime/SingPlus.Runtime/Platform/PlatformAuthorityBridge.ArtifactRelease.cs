using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private bool HasOriginalArtifactReleaseSources(ExecutableArtifactRecord artifact, PlatformProviderIncarnation incarnation) =>
        _originalExecutableArtifactOwners.TryGetValue(artifact.Binding.BindingId, out var original) && ReferenceEquals(original, artifact) &&
        _executableArtifacts.TryGetValue(artifact.Binding.BindingId, out var route) && ReferenceEquals(route, artifact) &&
        artifact.AdmissionSource is { } source && HasCurrentGuestAdmissionSources(source.Guest) &&
        (artifact.BindInvocation is not { } bind || GuestEffectLineageCurrent(bind.Ancestors)) &&
        (artifact.StartInvocation is not { } start || GuestEffectLineageCurrent(start.Ancestors)) &&
        _childBindings.TryGetValue(source.Child.Binding.BindingId, out var child) && ReferenceEquals(child, source.Child) &&
        _guestBindings.TryGetValue(source.Guest.Mapping.MappingId, out var guest) && ReferenceEquals(guest, source.Guest) &&
        _mappings.TryGetValue(source.Parent.Mapping.MappingId, out var parent) && ReferenceEquals(parent, source.Parent) &&
        TryGetCanonicalRootRecord(source.Root.Binding.BindingId, out var root) && ReferenceEquals(root, source.Root) &&
        incarnation.Value != 0 && incarnation == source.Root.ProviderIncarnation && incarnation == source.Parent.ProviderIncarnation &&
        source.Root.BackendEpoch == BackendEpoch && source.Parent.BackendEpoch == BackendEpoch && !_backendEpochExhausted;

    private KernelResult ReleaseArtifactsForGuestMapping(PlatformGuestMapping mapping)
    {
        ExecutableArtifactRecord[] artifacts;
        lock (_secureDomainLifecycleGate)
        {
            foreach (var completed in _originalExecutableArtifactOwners.Values.Where(x => x.Binding.GuestMapping == mapping &&
                x.Released && x.BindInvocation is { NoEffectConsumed: true, DependencyFailureConsumed: false }))
            {
                ConsumeExecutableStartFailureAfterRelease(completed);
                if (!completed.BindInvocation!.DependencyFailureConsumed)
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Independent users still pin completed Bind no-effect cleanup.");
            }
            artifacts = _originalExecutableArtifactOwners.Values.Where(x => x.Binding.GuestMapping == mapping && !x.Released).ToArray();
            if (artifacts.Length != 0 && !artifacts.All(HasOwnExecutableStartFailure) && _guestBindings.TryGetValue(mapping.MappingId, out var guestSource) &&
                (guestSource.Closure == PlatformExternalClosureState.Faulted ||
                 _mappings.TryGetValue(mapping.ParentMapping.MappingId, out var backing) &&
                 (backing.ClosureState == PlatformExternalClosureState.Faulted || backing.LocalAuthorizationRevoked && !backing.OriginalPermissionRetiredForCleanup)))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Faulted/revoked backing source remains pinned before artifact release.");
            if (_executableArtifacts.Values.Any(x => x.Binding.GuestMapping == mapping &&
                (!_originalExecutableArtifactOwners.TryGetValue(x.Binding.BindingId, out var original) ||
                 !ReferenceEquals(original, x) && (!original.Released || original.Binding.GuestMapping != mapping))))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Foreign artifact route cannot authorize guest backing release.");
        }
        foreach (var artifact in artifacts)
        {
            if (artifact.ProviderReceipt.ArtifactId.Value == 0)
            {
                var resolved = ResolveArtifactBindForCleanup(artifact);
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
                artifact.ReleaseRequest ??= new(Guid.NewGuid(), artifact.ProviderReceipt);
                request = artifact.ReleaseRequest.Value;
                resolve = artifact.ReleaseInvoked;
                artifact.ReleaseInFlight = true;
                admitted.Guest.PendingExecutableEffects++;
                foreach (var owner in lineage) owner.PendingChildEffects++;
            }
            try
            {
                var incarnation = CurrentProviderIncarnation(); // Outside bridge owner lock.
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
                    if (result.Status == PlatformAuthorityStatus.NotAccepted) artifact.ReleaseInvoked = false;
                }
                incarnation = CurrentProviderIncarnation();
                lock (_secureDomainLifecycleGate)
                {
                    if (!HasOriginalArtifactReleaseSources(artifact, incarnation) || artifact.ReleaseRequest != request)
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
                    foreach (var owner in lineage) owner.PendingChildEffects--;
                }
            }
        }
        return KernelResult.Ok();
    }
}

