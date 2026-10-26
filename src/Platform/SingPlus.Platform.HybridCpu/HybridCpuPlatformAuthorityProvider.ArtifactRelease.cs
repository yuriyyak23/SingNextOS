using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider
{
    public PlatformAuthorityResult<PlatformArtifactReleaseReceipt> ReleaseExecutableArtifact(PlatformArtifactReleaseRequest request)
        => ReleaseArtifactCore(request, false);
    public PlatformAuthorityResult<PlatformArtifactReleaseReceipt> ResolveExecutableArtifactRelease(PlatformArtifactReleaseRequest request)
        => ReleaseArtifactCore(request, true);

    private bool HasIntactArtifactRelease(ArtifactRecord artifact) => artifact.ReleaseInvoked && artifact.OriginalRelease is { } original &&
        artifact.ReleaseRequest == original.Request && original.Observation.IsSuccess && original.Observation.Value.NoFurtherUse &&
        artifact.NativeReleaseRequest == original.Observation.Value.Request &&
        (artifact.ReleaseObservation == original.Observation || artifact.ReleaseResolutionObservation == original.Observation);
    private bool HasRetainedArtifactRelease(ArtifactRecord artifact) => artifact.Released && HasIntactArtifactRelease(artifact) &&
        _originalProviderArtifacts.TryGetValue(artifact.Receipt.ArtifactId,out ArtifactRecord? original) && ReferenceEquals(original,artifact);

    private bool HasOriginalArtifactReleaseSources(ArtifactRecord artifact) =>
        OriginalExecutionHistoryIntact(artifact) &&
        _originalProviderArtifacts.TryGetValue(artifact.Receipt.ArtifactId, out ArtifactRecord? original) && ReferenceEquals(original, artifact) &&
        _providerArtifacts.TryGetValue(artifact.Receipt.ArtifactId, out ArtifactRecord? route) && ReferenceEquals(route, artifact) &&
        artifact.BindMapping is { } mapping && _providerGuestMappings.TryGetValue(mapping.Lease.LeaseId, out GuestRecord? guest) &&
        ReferenceEquals(guest, mapping) && HasCurrentGuestMapSources(mapping) &&
        ValidateDomain(mapping.Lease.ChildLease.ParentDomainLease).IsSuccess;

    private PlatformAuthorityResult<PlatformArtifactReleaseReceipt> ReleaseArtifactCore(PlatformArtifactReleaseRequest request, bool resolutionOnly)
    {
        ArtifactRecord artifact;
        bool resolve; bool consumed;
        NeutralArtifactReleaseRequest nativeRequest;
        ChildRecord[] lineage;
        lock (_childCreateCommitGate)
        {
            if (request.InvocationId == Guid.Empty || !_originalProviderArtifacts.TryGetValue(request.Artifact.ArtifactId, out artifact!) ||
                artifact.Receipt != request.Artifact ||
                artifact.ReleaseRequest is { } previous && previous != request)
                return PlatformAuthorityResult<PlatformArtifactReleaseReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original artifact release owner/request unavailable.");
            if (!_providerArtifacts.TryGetValue(request.Artifact.ArtifactId, out ArtifactRecord? route) || !ReferenceEquals(route, artifact))
                return PlatformAuthorityResult<PlatformArtifactReleaseReceipt>.Fail(artifact.ReleaseInvoked || artifact.ReleaseInFlight ? PlatformAuthorityStatus.Faulted : PlatformAuthorityStatus.NotAccepted,
                    "Original artifact route changed before this release invocation/lookup.");
            if(artifact.ReleaseInFlight)return PlatformAuthorityResult<PlatformArtifactReleaseReceipt>.Fail(PlatformAuthorityStatus.Faulted,"Original release lookup in flight.");
            if (artifact.BindMapping?.MapChild is { } secureChild && HasSoftwareSecurePins(secureChild))
                return PlatformAuthorityResult<PlatformArtifactReleaseReceipt>.Fail(PlatformAuthorityStatus.NotAccepted, "Original software entry cleanup remains pinned.");
            consumed=artifact.Released;
            if(consumed)
            {
                if(!HasRetainedArtifactRelease(artifact))return PlatformAuthorityResult<PlatformArtifactReleaseReceipt>.Fail(PlatformAuthorityStatus.Faulted,"Consumed original release evidence unavailable.");
                nativeRequest=artifact.NativeReleaseRequest!.Value;resolve=true;lineage=[];
            }
            else
            {
            if(artifact.OriginalRelease is not null && !HasIntactArtifactRelease(artifact))
                return PlatformAuthorityResult<PlatformArtifactReleaseReceipt>.Fail(PlatformAuthorityStatus.Faulted,"Original release custody lost; pins retained.");
            if (_childRuntime is not INeutralExecutableArtifactReleaseProvider)
                return PlatformAuthorityResult<PlatformArtifactReleaseReceipt>.Fail(PlatformAuthorityStatus.Unsupported, "Artifact release provider is unavailable; backing remains pinned.");
            if (!HasOriginalArtifactReleaseSources(artifact) ||
                artifact.ReleaseInFlight || artifact.ExecutionInFlight || artifact.BindMapping!.ArtifactBindInFlight ||
                artifact.BindMapping.UnmapInFlight || artifact.BindMapping.MapChild!.TransitionInFlight || artifact.BindMapping.MapChild.CloseInFlight)
                return PlatformAuthorityResult<PlatformArtifactReleaseReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original release source/dependencies remain pinned.");
            if (resolutionOnly && artifact.ReleaseRequest is null)
                return PlatformAuthorityResult<PlatformArtifactReleaseReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original release request is absent.");
            artifact.ReleaseRequest ??= request;
            artifact.NativeReleaseRequest ??= new(request.InvocationId, artifact.Neutral);
            nativeRequest = artifact.NativeReleaseRequest.Value;
            resolve = artifact.ReleaseInvoked;
            lineage = NestedProviderLineage(artifact.BindMapping!.MapChild!).ToArray();
            if (lineage.Any(source => _pendingNestedSources.Contains(source) || source.CloseInFlight || source.TransitionInFlight))
                return PlatformAuthorityResult<PlatformArtifactReleaseReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original artifact ancestor release is in flight.");
            }
            artifact.ReleaseInFlight = true;
            foreach (ChildRecord source in lineage) _pendingNestedSources.Add(source);
        }
        try
        {
            PlatformAuthorityResult source = ValidateOriginalNativeMappingSource(); // Outside provider owner lock.
            if(consumed)
            {
                lock(_childCreateCommitGate)
                    return source.IsSuccess && HasRetainedArtifactRelease(artifact) && artifact.ReleaseRequest == request &&
                        _providerArtifacts.TryGetValue(artifact.Receipt.ArtifactId,out ArtifactRecord? current) && ReferenceEquals(current,artifact) ?
                        PlatformAuthorityResult<PlatformArtifactReleaseReceipt>.Ok(new(request,true)) :
                        PlatformAuthorityResult<PlatformArtifactReleaseReceipt>.Fail(PlatformAuthorityStatus.Faulted,"Consumed release source/evidence changed.");
            }
            lock (_childCreateCommitGate)
            {
                if (!source.IsSuccess || !HasOriginalArtifactReleaseSources(artifact))
                    return PlatformAuthorityResult<PlatformArtifactReleaseReceipt>.Fail(artifact.ReleaseInvoked ? PlatformAuthorityStatus.Faulted : PlatformAuthorityStatus.NotAccepted,
                        "Original artifact source changed before release/lookup.");
                if (!resolutionOnly && !resolve) artifact.ReleaseInvoked = true;
            }
            NeutralVirtualizationResult<NeutralArtifactReleaseReceipt> result;
            if (artifact.ReleaseObservation is { IsSuccess: true } retained) result = retained;
            else if (artifact.ReleaseResolutionObservation is { IsSuccess: true } retainedQuery) result = retainedQuery;
            else result = resolve || resolutionOnly
                ? ((INeutralExecutableArtifactReleaseProvider)_childRuntime!).ResolveExecutableArtifactRelease(nativeRequest)
                : ((INeutralExecutableArtifactReleaseProvider)_childRuntime!).ReleaseExecutableArtifact(nativeRequest);
            lock (_childCreateCommitGate)
            {
                if (resolve || resolutionOnly) artifact.ReleaseResolutionObservation = result;
                else artifact.ReleaseObservation = result; // Raw before callbacks.
                if(result.IsSuccess && result.Value.Request == nativeRequest && result.Value.NoFurtherUse)artifact.OriginalRelease ??= (request,result);
                if (result.Status == NeutralVirtualizationStatus.NotInvoked) artifact.ReleaseInvoked = false;
            }
            source = ValidateOriginalNativeMappingSource();
            lock (_childCreateCommitGate)
            {
                if (!source.IsSuccess || !HasOriginalArtifactReleaseSources(artifact) ||
                    result.IsSuccess && (!HasIntactArtifactRelease(artifact) || artifact.OriginalRelease!.Value.Observation != result))
                    return PlatformAuthorityResult<PlatformArtifactReleaseReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original release outcome retained without current source continuity.");
                if (!result.IsSuccess) return FromNeutral<PlatformArtifactReleaseReceipt>(result.Status, result.Reason);
                if (result.Value.Request != nativeRequest || !result.Value.NoFurtherUse)
                    return PlatformAuthorityResult<PlatformArtifactReleaseReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Malformed original artifact release proof.");
                artifact.Released = true;
                return PlatformAuthorityResult<PlatformArtifactReleaseReceipt>.Ok(new(request, true));
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return PlatformAuthorityResult<PlatformArtifactReleaseReceipt>.Fail(artifact.ReleaseInvoked ? PlatformAuthorityStatus.Faulted : PlatformAuthorityStatus.NotAccepted,
            $"Original release retained/unknown: {exception.GetType().Name}"); }
        finally
        {
            lock (_childCreateCommitGate)
            {
                artifact.ReleaseInFlight = false;
                foreach (ChildRecord source in lineage) _pendingNestedSources.Remove(source);
            }
        }
    }
}
