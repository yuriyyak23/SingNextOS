using SingPlus.Platform;
using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Platform.HybridCpu;
public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformOriginalGuestMapProvider
{
    public bool SupportsOriginalGuestMapResolution => _childRuntime is INeutralOriginalGuestMapProvider { SupportsOriginalGuestMapResolution: true };
    private static bool ProviderGuestMapHistory(ChildRecord child) => (ulong)child.OriginalGuestMaps.Count == child.OriginalGuestMapsIssued &&
        child.OriginalGuestMaps.All(f=>ReferenceEquals(f.OriginalOwner,f) && ReferenceEquals(f.Child,child)) &&
        child.OriginalGuestMaps.Distinct(ReferenceEqualityComparer.Instance).Count() == child.OriginalGuestMaps.Count;
    public PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> MapOriginalGuest(PlatformOriginalGuestMapRequest request) =>
        request is not null && SupportsOriginalGuestMapResolution ? MapGuestRegionCore(request.MappingRequest, request) :
        PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.Unsupported, "Original guest Map capability unavailable.");
    private bool OriginalGuestMapSources(GuestMapFailure fact) =>
        ReferenceEquals(fact.OriginalOwner, fact) && fact.Child is { } child && fact.ParentMapping is { } mapping && fact.Parent is { } parent &&
        ProviderGuestMapHistory(child) && child.OriginalGuestMaps.Contains(fact) && IsOriginalChildOwner(child) &&
        (child.NestedParent is null || ProviderNestedIdentity(child)) &&
        _providerChildren.TryGetValue(child.Lease.LeaseId, out var route) && ReferenceEquals(route, child) &&
        _originalProviderMappings.TryGetValue(mapping.Mapping.Lease.MappingId, out var original) && ReferenceEquals(original, mapping) &&
        _providerMappings.TryGetValue(mapping.Mapping.Lease.MappingId, out var current) && ReferenceEquals(current, mapping) &&
        _domains.TryGetValue(parent.Lease.LeaseId, out var root) && ReferenceEquals(root, parent) &&
        ReferenceEquals(child.CreateParent, parent) && ReferenceEquals(mapping.MapParent, parent) && ValidateDomain(parent.Lease).IsSuccess;
    public PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> ResolveOriginalGuestMap(PlatformOriginalGuestMapRequest original)
    {
        if (original is null || _childRuntime is not INeutralOriginalGuestMapProvider { SupportsOriginalGuestMapResolution: true } resolver)
            return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.Faulted, "Original guest Map resolution unavailable.");
        GuestMapFailure fact;
        ChildRecord[] guestLineage;
        lock (_childCreateCommitGate)
        {
            if (!_originalProviderChildren.TryGetValue(original.MappingRequest.ChildLease.LeaseId, out var child) || !ProviderGuestMapHistory(child) ||
                child.OriginalGuestMaps.SingleOrDefault(f => ReferenceEquals(f.OriginalRequest, original)) is not { } captured ||
                !ReferenceEquals(captured.OriginalOwner, captured) || !ReferenceEquals(captured.Child, child))
                return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.Faulted, "Original guest Map request/source absent or pending.");
            fact = captured;
            if (fact.NoEffectConsumed) return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.NotAccepted, "Exact original Map no-effect consumed.");
            if (!OriginalGuestMapSources(captured) || captured.NativeRequest is null || child.GuestMapInFlight || captured.ParentMapping!.GuestMapInFlight)
                return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.Faulted, "Original guest Map source absent or pending.");
            if (fact.Mapping is { } known)
                return _originalProviderGuestMappings.TryGetValue(known.Lease.LeaseId,out var owner) && ReferenceEquals(owner,known)
                    ? PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Ok(known.Lease)
                    : PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.Faulted,"Original Map acquisition owner changed; equal lease is not continuity.");
            guestLineage = NestedProviderLineage(child).ToArray();
            if (guestLineage.Any(_pendingNestedSources.Contains))
                return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.Faulted, "Original guest ancestor effect pending.");
            child.GuestMapInFlight = fact.ParentMapping!.GuestMapInFlight = true;
            foreach (var source in guestLineage) _pendingNestedSources.Add(source);
        }
        try
        {
            var observation = resolver.ResolveOriginalGuestMap(fact.NativeRequest!);
            lock (_childCreateCommitGate)
            {
                fact.Resolution = observation;
                if (!OriginalGuestMapSources(fact)) return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.Faulted, "Known Map sources changed before consumption.");
                if (observation.Status == NeutralVirtualizationStatus.NotInvoked)
                {
                    fact.NoEffectConsumed = true;
                    if (ReferenceEquals(fact.Child!.UnclosedGuestMap, fact)) fact.Child.UnclosedGuestMap = null;
                    if (ReferenceEquals(fact.ParentMapping!.FailedGuestMap, fact)) fact.ParentMapping.FailedGuestMap = null;
                    return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.NotAccepted, "Exact original guest Map no-effect consumed.");
                }
                var valid = NeutralGuestMemoryContract.ValidateLease(fact.Request, observation.Value);
                if (!observation.IsSuccess || !valid.IsSuccess) return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.Faulted, "Original Map outcome unknown/malformed.");
                var request = original.MappingRequest;
                var lease = new PlatformProviderGuestRegionMappingLease(fact.InvocationId, new(1), request.ChildLease, request.ParentMapping.Lease, request.GuestRange, request.Access);
                var mapped = new GuestRecord(lease, observation.Value) { AdmissionPublished = false, MapChild = fact.Child, MapSource = fact.ParentMapping, MapParent = fact.Parent };
                _originalProviderGuestMappings.Add(lease.LeaseId, mapped); _providerGuestMappings.Add(lease.LeaseId, mapped); fact.Mapping = mapped;
                if (ReferenceEquals(fact.Child!.UnclosedGuestMap, fact)) fact.Child.UnclosedGuestMap = null;
                if (ReferenceEquals(fact.ParentMapping!.FailedGuestMap, fact)) fact.ParentMapping.FailedGuestMap = null;
                return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Ok(lease);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Fail(PlatformAuthorityStatus.Faulted, "Original guest Map resolution unavailable: " + exception.Message); }
        finally { lock (_childCreateCommitGate)
            { fact.Child!.GuestMapInFlight = fact.ParentMapping!.GuestMapInFlight = false; foreach (var source in guestLineage) _pendingNestedSources.Remove(source); } }
    }
}
