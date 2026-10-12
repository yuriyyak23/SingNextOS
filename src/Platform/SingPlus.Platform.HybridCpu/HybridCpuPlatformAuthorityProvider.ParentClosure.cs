using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider
{
    private PlatformAuthorityResult CloseExternalParentLifetime(DomainRecord record, bool allowRemainingEffect)
    {
        if (!record.ExternalChildCreateInvoked || _childRuntime is not INeutralExternalParentClosureProvider producer)
            return PlatformAuthorityResult.Ok();
        NeutralExternalParentCloseRequest request;
        bool resolve;
        lock (_childCreateCommitGate)
        {
            if (record.ExternalParentClosureConsumed) return PlatformAuthorityResult.Ok();
            if (!ValidateDomainIdentity(record.Lease).IsSuccess || !_domains.TryGetValue(record.Lease.LeaseId, out var route) || !ReferenceEquals(route, record) ||
                record.RootCloseObservation is not { Decision: NeutralDomainCloseDecision.Closed or NeutralDomainCloseDecision.Revoked } ||
                _originalProviderChildren.Values.Any(child => child.Lease.ParentDomainLease == record.Lease && !HasPublishedChildClosure(child)) ||
                _originalProviderGuestMappings.Values.Any(mapping => mapping.Lease.ChildLease.ParentDomainLease == record.Lease && !mapping.ClosurePublished) ||
                _originalProviderArtifacts.Values.Any(artifact => artifact.Receipt.ParentLeaseId == record.Lease.LeaseId && !artifact.Released) ||
                HasActiveProviderDeviceLeases(record.Lease) || HasActiveProviderMappings(record.Lease))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original composed root dependencies/source remain pinned.");
            record.ExternalParentCloseRequest ??= new(Guid.NewGuid(), record.HybridCpuLease);
            request = record.ExternalParentCloseRequest.Value;
            resolve = record.ExternalParentCloseInvoked;
            if (!resolve && !allowRemainingEffect)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Pure root lookup cannot invoke the remaining staged external parent Close.");
            if (!resolve) record.ExternalParentCloseInvoked = true;
        }
        try
        {
            var result = resolve ? producer.ResolveExternalParentClose(request) : producer.CloseExternalParent(request);
            lock (_childCreateCommitGate) record.ExternalParentCloseObservation = result; // Raw before source callback.
            var source = ValidateOriginalNativeMappingSource();
            lock (_childCreateCommitGate)
            {
                if (!source.IsSuccess || !ValidateDomainIdentity(record.Lease).IsSuccess ||
                    !_domains.TryGetValue(record.Lease.LeaseId, out var route) || !ReferenceEquals(route, record) || record.ExternalParentCloseRequest != request)
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original parent closure outcome retained without current source continuity.");
                if (result.Status == NeutralVirtualizationStatus.NotInvoked)
                {
                    record.ExternalParentCloseNoEffectConsumed = result;
                    record.ExternalParentCloseInvoked = false; record.ExternalParentCloseRequest = null;
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Exact external component no-effect consumed; neutral root closure is still partial, not overall no-effect.");
                }
                if (!result.IsSuccess || result.Value.Request != request || !result.Value.NoFurtherUse)
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original external parent closure remains unknown/malformed; root pins retained.");
                record.ExternalParentClosureConsumed = true;
                return PlatformAuthorityResult.Ok();
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, $"Original external parent Close retained/unknown: {exception.GetType().Name}"); }
    }
}
