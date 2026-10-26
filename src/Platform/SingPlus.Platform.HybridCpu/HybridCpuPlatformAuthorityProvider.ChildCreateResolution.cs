using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Platform.HybridCpu;
public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformOriginalChildCreateProvider
{
    public bool SupportsOriginalChildCreateResolution => _childRuntime is INeutralOriginalChildCreateProvider { SupportsOriginalChildCreateResolution: true };
    public PlatformAuthorityResult<PlatformProviderChildDomainLease> CreateOriginalChild(PlatformOriginalChildCreateRequest request, out bool refusedBeforeEffect)
    {
        refusedBeforeEffect = false;
        return request is not null && SupportsOriginalChildCreateResolution ? CreateChildDomainCore(request.Parent, request.Intent, request, out refusedBeforeEffect) :
            PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(PlatformAuthorityStatus.Unsupported, "Original child Create capability unavailable.");
    }
    private bool HasOriginalChildCreateHistory(DomainRecord parent) =>
        parent.OriginalChildCreatesIssued == (ulong)parent.OriginalChildCreates.Count &&
        parent.OriginalChildCreates.Select(fact => fact.InvocationId).Distinct().Count() == parent.OriginalChildCreates.Count &&
        parent.OriginalChildCreates.Select(fact => fact.OriginalRequest).Distinct(ReferenceEqualityComparer.Instance).Count() == parent.OriginalChildCreates.Count &&
        parent.OriginalChildCreates.Select(fact => fact.NativeRequest).Distinct(ReferenceEqualityComparer.Instance).Count() == parent.OriginalChildCreates.Count &&
        parent.OriginalChildCreates.All(fact => ReferenceEquals(fact.OriginalOwner, fact) && ReferenceEquals(fact.Source, parent) &&
            fact.OriginalRequest is { } request && request.Parent == parent.Lease && fact.Parent == parent.HybridCpuLease &&
            fact.Intent == ToNeutral(request.Intent) && fact.NativeRequest is { } native && native.Parent == fact.Parent && native.Intent == fact.Intent &&
            (fact.NoEffectConsumed ? fact.Child is null &&
                (fact.Observation is { Status: NeutralVirtualizationStatus.NotInvoked } || fact.Resolution is { Status: NeutralVirtualizationStatus.NotInvoked }) :
                fact.Child is not { } child || ReferenceEquals(child.CreateParent, parent) && child.OriginalCreateRequired &&
                    child.Lease.LeaseId == fact.InvocationId && child.Lease.ParentDomainLease == parent.Lease && child.Lease.Intent == request.Intent &&
                    _originalProviderChildren.TryGetValue(child.Lease.LeaseId, out ChildRecord? original) && ReferenceEquals(original, child) &&
                    _providerChildren.TryGetValue(child.Lease.LeaseId, out ChildRecord? routed) && ReferenceEquals(routed, child) &&
                    (HasChildCreateObservation(fact.Observation, fact, child) || HasChildCreateObservation(fact.Resolution, fact, child))));

    private static bool HasChildCreateObservation(NeutralVirtualizationResult<NeutralChildDomainLease>? raw, ChildCreateFailure fact, ChildRecord child) =>
        raw is { IsSuccess: true } observation && observation.Value == child.Neutral &&
        NeutralChildDomainContract.ValidateLease(fact.Parent, fact.Intent, observation.Value).IsSuccess;

    private bool OriginalChildCreateSources(ChildCreateFailure fact) =>
        fact.Source is { } source && HasOriginalChildCreateHistory(source) &&
        ReferenceEquals(fact.OriginalOwner, fact) && fact.Source is { } parent && parent.OriginalChildCreates.Contains(fact) &&
        fact.OriginalRequest is { } request && request.Parent == parent.Lease && fact.Parent == parent.HybridCpuLease &&
        _domains.TryGetValue(parent.Lease.LeaseId, out DomainRecord? route) && ReferenceEquals(route, parent) && ValidateDomain(parent.Lease).IsSuccess &&
        !parent.Revoked && !parent.RootCloseInFlight && !parent.RootCloseUncertain && !parent.ExternalRevocationObserved;
    public PlatformAuthorityResult<PlatformProviderChildDomainLease> ResolveOriginalChildCreate(PlatformOriginalChildCreateRequest request)
    {
        ChildCreateFailure fact;
        lock (_childCreateCommitGate)
        {
            if (request is null || _childRuntime is not INeutralOriginalChildCreateProvider { SupportsOriginalChildCreateResolution: true } ||
                !_domains.TryGetValue(request.Parent.LeaseId, out DomainRecord? parent) ||
                parent.OriginalChildCreates.SingleOrDefault(f => ReferenceEquals(f.OriginalRequest, request)) is not { } saved ||
                !OriginalChildCreateSources(saved) || parent.ChildCreateInFlight || saved.NativeRequest is null)
                return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(PlatformAuthorityStatus.Faulted, "Original child Create request/source unavailable.");
            fact = saved;
            if (fact.NoEffectConsumed) return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(PlatformAuthorityStatus.NotAccepted, "Exact original child Create no-effect consumed.");
            if (fact.Child is { } known) return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Ok(known.Lease);
            parent.ChildCreateInFlight = true;
        }
        try
        {
            NeutralVirtualizationResult<NeutralChildDomainLease> observation = ((INeutralOriginalChildCreateProvider)_childRuntime!).ResolveOriginalChildCreate(fact.NativeRequest!);
            bool parentNoEffect = observation.Status == NeutralVirtualizationStatus.NotInvoked &&
                _childRuntime is INeutralOriginalExternalParentBindNoEffectProvider noEffect && noEffect.HasOriginalExternalParentBindNoEffect(fact.NativeRequest!);
            lock (_childCreateCommitGate)
            {
                fact.Resolution = observation;
                if (!OriginalChildCreateSources(fact)) return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(PlatformAuthorityStatus.Faulted, "Known child Create source changed.");
                if (observation.Status == NeutralVirtualizationStatus.NotInvoked)
                {
                    fact.NoEffectConsumed = true;
                    if (parentNoEffect && !fact.PreviousExternalParentEffect) fact.Source!.ExternalChildCreateInvoked = false;
                    if (ReferenceEquals(fact.Source!.FailedChildCreate, fact)) fact.Source.FailedChildCreate = null;
                    return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(PlatformAuthorityStatus.NotAccepted, "Exact original child Create no-effect consumed.");
                }
                NeutralVirtualizationResult valid = NeutralChildDomainContract.ValidateLease(fact.Parent, fact.Intent, observation.Value);
                if (!observation.IsSuccess || !valid.IsSuccess) return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(PlatformAuthorityStatus.Faulted, "Original child Create outcome unknown/malformed.");
                PlatformProviderChildDomainLease lease = new PlatformProviderChildDomainLease(fact.InvocationId, new(1), request.Parent, request.Intent);
                ChildRecord child = new ChildRecord(lease, observation.Value) { CreateParent = fact.Source, OriginalCreateRequired = true };
                _originalProviderChildren.Add(lease.LeaseId, child); _providerChildren.Add(lease.LeaseId, child); fact.Child = child;
                if (ReferenceEquals(fact.Source!.FailedChildCreate, fact)) fact.Source.FailedChildCreate = null;
                return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Ok(lease);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(PlatformAuthorityStatus.Faulted, "Original child Create lookup unavailable: " + exception.Message); }
        finally { lock (_childCreateCommitGate) fact.Source!.ChildCreateInFlight = false; }
    }
}
