using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformOriginalArtifactBindNoEffectProvider
{
    public PlatformAuthorityResult<PlatformExecutableArtifactReceipt> ResolveExecutableArtifactBind(PlatformExecutableArtifactRequest originalRequest)
    {
        GuestRecord mapping;
        ArtifactBindFailure invocation;
        ChildRecord[] lineage;
        lock (_childCreateCommitGate)
        {
            if (!_originalProviderGuestMappings.TryGetValue(originalRequest.GuestMapping.LeaseId, out mapping!) ||
                mapping.OriginalArtifactBindRequest != originalRequest || !HasCurrentGuestMapSources(mapping) ||
                !_providerGuestMappings.TryGetValue(mapping.Lease.LeaseId, out var route) || !ReferenceEquals(route, mapping) ||
                mapping.ArtifactBindInFlight || mapping.UnmapInFlight || mapping.MapChild!.CloseInFlight || mapping.MapChild.TransitionInFlight)
                return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original Bind request/source unavailable or in flight.");
            var existing = _originalProviderArtifacts.Values.SingleOrDefault(x => ReferenceEquals(x.BindMapping, mapping));
            if (existing is not null)
            {
                if (!_providerArtifacts.TryGetValue(existing.Receipt.ArtifactId, out var artifactRoute) || !ReferenceEquals(artifactRoute, existing))
                    return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original Bind artifact route changed.");
                return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Ok(existing.Receipt);
            }
            if (mapping.OriginalArtifactBindInvocation is { NoEffectConsumed: true } consumed && ReferenceEquals(consumed.OwnerSelf, consumed))
                return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.NotAccepted, "Original Bind no-effect already consumed.");
            if (mapping.FailedArtifactBind is not { } failed || !ReferenceEquals(failed.OwnerSelf, failed) || !ReferenceEquals(mapping.OriginalArtifactBindInvocation, failed) || _childRuntime is not INeutralOriginalArtifactBindProvider)
                return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original Bind outcome unknown; backing remains pinned.");
            invocation = failed;
            lineage = NestedProviderLineage(mapping.MapChild!).ToArray();
            if (lineage.Any(source => _pendingNestedSources.Contains(source) || source.CloseInFlight || source.TransitionInFlight))
                return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original Bind ancestor lookup is in flight.");
            mapping.ArtifactBindInFlight = true;
            foreach (var source in lineage) _pendingNestedSources.Add(source);
        }
        try
        {
            var source = ValidateOriginalNativeMappingSource();
            lock (_childCreateCommitGate) if (!source.IsSuccess || !HasCurrentGuestMapSources(mapping) || !ReferenceEquals(mapping.FailedArtifactBind, invocation))
                return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original Bind source changed before pure lookup.");
            var result = invocation.Observation is { IsSuccess: true } known ? known :
                mapping.ArtifactBindResolutionObservation is { IsSuccess: true } retained ? retained :
                ((INeutralOriginalArtifactBindProvider)_childRuntime).ResolveExecutableArtifactBind(invocation.Request);
            lock (_childCreateCommitGate) mapping.ArtifactBindResolutionObservation = result;
            source = ValidateOriginalNativeMappingSource();
            lock (_childCreateCommitGate)
            {
                if (source.IsSuccess && HasCurrentGuestMapSources(mapping) && ReferenceEquals(mapping.FailedArtifactBind, invocation) &&
                    _providerGuestMappings.TryGetValue(mapping.Lease.LeaseId, out var originalRoute) && ReferenceEquals(originalRoute, mapping) &&
                    _childRuntime is INeutralOriginalArtifactBindNoEffectProvider && result.Status == NeutralVirtualizationStatus.NotInvoked)
                {
                    invocation.NoEffectConsumed = true;
                    mapping.FailedArtifactBind = null;
                    return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.NotAccepted, "Exact original Bind no-effect consumed.");
                }
                if (!source.IsSuccess || !HasCurrentGuestMapSources(mapping) || !ReferenceEquals(mapping.FailedArtifactBind, invocation) ||
                    !_providerGuestMappings.TryGetValue(mapping.Lease.LeaseId, out var route) || !ReferenceEquals(route, mapping) ||
                    !result.IsSuccess || !NeutralChildExecutionContract.ValidateAdmissionReceipt(invocation.Request, result.Value).IsSuccess ||
                    result.Value.ContentDigest != invocation.ContentDigest)
                    return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original Bind outcome unknown/malformed; backing remains pinned.");
                var receipt = new PlatformExecutableArtifactReceipt(invocation.InvocationId, new(1), originalRequest.ChildLease.LeaseId,
                    originalRequest.ChildLease.Generation, originalRequest.GuestMapping.LeaseId, originalRequest.GuestMapping.Generation,
                    originalRequest.ChildLease.ParentDomainLease.LeaseId, originalRequest.ChildLease.ParentDomainLease.Generation,
                    invocation.ContentDigest, originalRequest.MaximumExecutionSteps) { MaximumPrivateDataBytes = originalRequest.MaximumPrivateDataBytes };
                var artifact = new ArtifactRecord(receipt, result.Value) { AdmissionPublished = false, BindMapping = mapping };
                _originalProviderArtifacts.Add(receipt.ArtifactId, artifact);
                _providerArtifacts.Add(receipt.ArtifactId, artifact);
                mapping.FailedArtifactBind = null; // Exact admitted backing identity adopted for release only.
                return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Ok(receipt);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(PlatformAuthorityStatus.Faulted, $"Original Bind lookup retained/unknown: {exception.GetType().Name}"); }
        finally
        {
            lock (_childCreateCommitGate)
            {
                mapping.ArtifactBindInFlight = false;
                foreach (var source in lineage) _pendingNestedSources.Remove(source);
            }
        }
    }
}
