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
    private bool OriginalChildCreateSources(ChildCreateFailure fact) =>
        ReferenceEquals(fact.OriginalOwner, fact) && fact.Source is { } parent && parent.OriginalChildCreates.Contains(fact) &&
        fact.OriginalRequest is { } request && request.Parent == parent.Lease && fact.Parent == parent.HybridCpuLease &&
        _domains.TryGetValue(parent.Lease.LeaseId, out var route) && ReferenceEquals(route, parent) && ValidateDomain(parent.Lease).IsSuccess &&
        !parent.Revoked && !parent.RootCloseInFlight && !parent.RootCloseUncertain && !parent.ExternalRevocationObserved;
    public PlatformAuthorityResult<PlatformProviderChildDomainLease> ResolveOriginalChildCreate(PlatformOriginalChildCreateRequest request)
    {
        ChildCreateFailure fact;
        lock (_childCreateCommitGate)
        {
            if (request is null || _childRuntime is not INeutralOriginalChildCreateProvider { SupportsOriginalChildCreateResolution: true } ||
                !_domains.TryGetValue(request.Parent.LeaseId, out var parent) ||
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
            var observation = ((INeutralOriginalChildCreateProvider)_childRuntime!).ResolveOriginalChildCreate(fact.NativeRequest!);
            var parentNoEffect = observation.Status == NeutralVirtualizationStatus.NotInvoked &&
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
                var valid = NeutralChildDomainContract.ValidateLease(fact.Parent, fact.Intent, observation.Value);
                if (!observation.IsSuccess || !valid.IsSuccess) return PlatformAuthorityResult<PlatformProviderChildDomainLease>.Fail(PlatformAuthorityStatus.Faulted, "Original child Create outcome unknown/malformed.");
                var lease = new PlatformProviderChildDomainLease(fact.InvocationId, new(1), request.Parent, request.Intent);
                var child = new ChildRecord(lease, observation.Value) { CreateParent = fact.Source };
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
