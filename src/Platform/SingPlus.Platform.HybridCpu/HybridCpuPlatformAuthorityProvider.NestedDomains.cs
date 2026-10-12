using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformNestedDomainProvider, IPlatformOriginalNestedDomainProvider
{
    private sealed class ProviderNestedFact
    {
        public ProviderNestedFact(PlatformOriginalNestedDomainRequest request, ChildRecord parent, DomainRecord root,
            PlatformProviderChildDomainLeaseId identity, NeutralOriginalNestedDomainRequest nativeRequest)
        { OriginalOwner = this; Request = request; Parent = parent; Root = root; Factory = root.BindSource; Identity = identity; NativeRequest = nativeRequest; }
        public ProviderNestedFact OriginalOwner { get; }
        public PlatformOriginalNestedDomainRequest Request { get; }
        public ChildRecord Parent { get; }
        public DomainRecord Root { get; }
        public RootBindRecord? Factory { get; }
        public PlatformProviderChildDomainLeaseId Identity { get; }
        public NeutralOriginalNestedDomainRequest NativeRequest { get; }
        public NeutralVirtualizationResult<NeutralNestedDomainLease>? Raw;
        public ChildRecord? Child;
        public bool Invoked, InFlight, NoEffectConsumed;
        public bool ContainInvoked;
        public NeutralOriginalNestedDomainClosure? CleanupRaw;
        public PlatformOriginalNestedDomainClosure? Closure;
    }
    private readonly HashSet<ChildRecord> _pendingNestedSources = new(ReferenceEqualityComparer.Instance);
    public bool SupportsOriginalNestedDomains => _childRuntime is INeutralOriginalNestedDomainProvider { SupportsOriginalNestedDomains: true };
    private static bool ProviderNestedHistory(ChildRecord child) => child.NestedIssued == (ulong)child.OriginalNested.Count &&
        child.OriginalNested.All(f => ReferenceEquals(f.OriginalOwner, f) && ReferenceEquals(f.Parent, child) && f.Request.Request.ParentChildLease == child.Lease);
    private bool ProviderNestedIdentity(ChildRecord child)
    {
        var seen = new HashSet<ChildRecord>(ReferenceEqualityComparer.Instance);
        for (ChildRecord? current = child; current is not null; current = current.NestedParent)
        {
            if (!seen.Add(current) || !ProviderNestedHistory(current) || !IsOriginalChildOwner(current) ||
                !_providerChildren.TryGetValue(current.Lease.LeaseId, out var route) || !ReferenceEquals(route, current)) return false;
            if (current.NestedParent is not { } parent)
                return current.NestedFactory is null && current.CreateParent is { } rootOwner &&
                    rootOwner.OriginalChildCreates.Any(f => ReferenceEquals(f.OriginalOwner, f) && ReferenceEquals(f.Source, rootOwner) &&
                        ReferenceEquals(f.Child, current) && f.NativeRequest is not null && f.NativeRequest.Parent == rootOwner.HybridCpuLease);
            if (current.NestedFactory is not { Raw: { IsSuccess: true } raw } fact || !ReferenceEquals(fact.OriginalOwner, fact) ||
                !ReferenceEquals(fact.Parent, parent) || !ReferenceEquals(fact.Child, current) || !parent.OriginalNested.Contains(fact) ||
                raw.Value.Child != current.Neutral || raw.Value.ImmediateParent != parent.Neutral || !ReferenceEquals(fact.Root, current.CreateParent)) return false;
        }
        return false;
    }
    private bool ProviderNestedSources(ProviderNestedFact fact, bool active) =>
        ReferenceEquals(fact.OriginalOwner, fact) && fact.Parent.OriginalNested.Contains(fact) && ProviderNestedIdentity(fact.Parent) &&
        ReferenceEquals(fact.Parent.CreateParent, fact.Root) && ReferenceEquals(fact.Root.BindSource, fact.Factory) && fact.Factory is not null &&
        _activeSubjects.TryGetValue(fact.Root.Lease.Subject, out var activeRoot) && ReferenceEquals(activeRoot, fact.Root) &&
        _originalRootBinds.TryGetValue(fact.Root.Lease.LeaseId, out var originalFactory) && ReferenceEquals(originalFactory, fact.Factory) &&
        ReferenceEquals(fact.Factory.PublishedDomain, fact.Root) &&
        _domains.TryGetValue(fact.Root.Lease.LeaseId, out var root) && ReferenceEquals(root, fact.Root) &&
        fact.NativeRequest.ParentChild == fact.Parent.Neutral &&
        (!active || ValidateDomain(fact.Root.Lease).IsSuccess && !fact.Root.RootCloseInFlight && !fact.Root.RootCloseUncertain &&
            !fact.Root.ChildCreateInFlight && NestedProviderLineage(fact.Parent).All(x => !x.CloseInFlight && !x.TransitionInFlight &&
                x.FailedClose is null && x.FailedTransition is null && !x.ClosurePublished && x.ClosureReceipt is null && !x.GuestMapInFlight));
    private static IEnumerable<ChildRecord> NestedProviderLineage(ChildRecord child)
    { for (ChildRecord? current = child; current is not null; current = current.NestedParent) yield return current; }
    private bool HasProviderNestedPins(ChildRecord child) => _pendingNestedSources.Contains(child) || !ProviderNestedHistory(child) ||
        child.OriginalNested.Any(f => f.InFlight || f.Closure is null && !f.NoEffectConsumed && (f.Child is null || !ProviderNestedIdentity(f.Child) || !HasPublishedChildClosure(f.Child)));

    public PlatformAuthorityResult<PlatformProviderNestedDomainLease> CreateNestedChildDomain(PlatformNestedDomainRequest request) => CreateOriginalNestedDomain(new(request));
    public PlatformAuthorityResult<PlatformProviderNestedDomainLease> CreateOriginalNestedDomain(PlatformOriginalNestedDomainRequest request)
    {
        ProviderNestedFact fact;
        lock (_childCreateCommitGate)
        {
            if (request is null || !SupportsOriginalNestedDomains || !_providerChildren.TryGetValue(request.Request.ParentChildLease.LeaseId, out var parent) ||
                parent.Lease != request.Request.ParentChildLease || !ProviderNestedIdentity(parent) || parent.CreateParent is not { } root)
                return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Fail(PlatformAuthorityStatus.NotAccepted, "Original nested parent/provider unavailable before effect.");
            var valid = PlatformNestedDomainContract.ValidateRequest(request.Request);
            if (!valid.IsSuccess) return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Fail(valid.Status, valid.Message!);
            if (parent.OriginalNested.Any(f => ReferenceEquals(f.Request, request) || !f.NoEffectConsumed && f.Closure is null && f.Child is null))
                return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Fail(PlatformAuthorityStatus.NotAccepted, "Original nested request already invoked or unresolved; no repeat.");
            if (NestedProviderLineage(parent).Any(x => _pendingNestedSources.Contains(x) || x.CloseInFlight || x.TransitionInFlight || x.FailedClose is not null ||
                x.FailedTransition is not null || x.ClosureReceipt is not null || x.ClosurePublished || x.GuestMapInFlight) ||
                root.ChildCreateInFlight || !ValidateDomain(root.Lease).IsSuccess || _nextProviderChildId == 0 || _nextProviderChildId == ulong.MaxValue || parent.NestedIssued == ulong.MaxValue)
                return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Fail(PlatformAuthorityStatus.NotAccepted, "Original nested lineage/capacity refuses before effect.");
            fact = new(request, parent, root, new(_nextProviderChildId++), new(parent.Neutral, ToNeutral(request.Request.ChildIntent))) { InFlight = true };
            parent.OriginalNested.Add(fact); parent.NestedIssued++;
            foreach (var source in NestedProviderLineage(parent)) _pendingNestedSources.Add(source);
        }
        try { return ReconcileProviderNested(fact, true); }
        finally { lock (_childCreateCommitGate) { fact.InFlight = false; foreach (var source in NestedProviderLineage(fact.Parent)) _pendingNestedSources.Remove(source); } }
    }
    public PlatformAuthorityResult<PlatformProviderNestedDomainLease> ResolveOriginalNestedDomain(PlatformOriginalNestedDomainRequest request)
    {
        ProviderNestedFact fact;
        lock (_childCreateCommitGate)
        {
            if (request is null || !SupportsOriginalNestedDomains || !_originalProviderChildren.TryGetValue(request.Request.ParentChildLease.LeaseId, out var parent) ||
                parent.OriginalNested.SingleOrDefault(f => ReferenceEquals(f.Request, request)) is not { } original || !ProviderNestedSources(original, false) || original.InFlight)
                return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Fail(PlatformAuthorityStatus.Faulted, "Original nested request/source unavailable.");
            fact = original;
            if (fact.NoEffectConsumed) return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Fail(PlatformAuthorityStatus.NotAccepted, "Exact original no-effect consumed.");
            if (fact.Child is { } child && ProviderNestedIdentity(child)) return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Ok(new(child.Lease, parent.Lease.LeaseId, parent.Lease.Generation));
            if (!fact.Invoked || NestedProviderLineage(parent).Any(_pendingNestedSources.Contains))
                return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Fail(PlatformAuthorityStatus.Faulted, "Original nested effect unknown/pending.");
            fact.InFlight = true;
            foreach (var source in NestedProviderLineage(parent)) _pendingNestedSources.Add(source);
        }
        try { return ReconcileProviderNested(fact, false); }
        finally { lock (_childCreateCommitGate) { fact.InFlight = false; foreach (var source in NestedProviderLineage(fact.Parent)) _pendingNestedSources.Remove(source); } }
    }
    private PlatformAuthorityResult<PlatformProviderNestedDomainLease> ReconcileProviderNested(ProviderNestedFact fact, bool invoke)
    {
        try
        {
            var source = CurrentIncarnation;
            lock (_childCreateCommitGate)
            {
                if (!ProviderNestedSources(fact, true) || source.Value == 0)
                { if (!fact.Invoked) fact.NoEffectConsumed = true; return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Fail(fact.Invoked ? PlatformAuthorityStatus.Faulted : PlatformAuthorityStatus.NotAccepted, "Nested source changed before effect/lookup."); }
                if (invoke) fact.Invoked = true;
            }
            var native = (INeutralOriginalNestedDomainProvider)_childRuntime!;
            NeutralVirtualizationResult<NeutralNestedDomainLease> result;
            if (invoke)
            {
                try { result = native.CreateOriginalNestedDomain(fact.NativeRequest); lock (_childCreateCommitGate) fact.Raw = result; }
                catch (Exception exception) when (exception is not StackOverflowException)
                { result = native.ResolveOriginalNestedDomain(fact.NativeRequest); lock (_childCreateCommitGate) fact.Raw = result; }
            }
            else { result = native.ResolveOriginalNestedDomain(fact.NativeRequest); lock (_childCreateCommitGate) fact.Raw = result; }
            var after = CurrentIncarnation;
            lock (_childCreateCommitGate)
            {
                if (!ProviderNestedSources(fact, true) || after != source)
                    return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Fail(PlatformAuthorityStatus.Faulted, "Nested original raw retained; final source changed.");
                if (!result.IsSuccess)
                {
                    if (result.Status == NeutralVirtualizationStatus.NotInvoked) fact.NoEffectConsumed = true;
                    return FromNeutral<PlatformProviderNestedDomainLease>(result.Status, result.Reason);
                }
                var exact = NeutralChildDomainContract.ValidateLease(fact.Parent.Neutral.ParentLease, fact.NativeRequest.Intent, result.Value.Child);
                if (!exact.IsSuccess || result.Value.ImmediateParent != fact.Parent.Neutral)
                    return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Fail(PlatformAuthorityStatus.Faulted, "Malformed original nested outcome retained.");
                if (fact.Child is null)
                {
                    var lease = new PlatformProviderChildDomainLease(fact.Identity, new(1), fact.Root.Lease, fact.Request.Request.ChildIntent);
                    fact.Child = new(lease, result.Value.Child) { CreateParent = fact.Root, NestedParent = fact.Parent, NestedFactory = fact };
                    _originalProviderChildren.Add(lease.LeaseId, fact.Child); _providerChildren.Add(lease.LeaseId, fact.Child);
                }
                return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Ok(new(fact.Child.Lease, fact.Parent.Lease.LeaseId, fact.Parent.Lease.Generation));
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Fail(PlatformAuthorityStatus.Faulted, "Original nested response/source remains unknown: " + exception.GetType().Name); }
    }

    public PlatformAuthorityResult<PlatformOriginalNestedDomainClosure> ContainOriginalNestedDomain(PlatformOriginalNestedDomainRequest request) => ProviderNestedCleanup(request, false);
    public PlatformAuthorityResult<PlatformOriginalNestedDomainClosure> ResolveOriginalNestedDomainCleanup(PlatformOriginalNestedDomainRequest request) => ProviderNestedCleanup(request, true);
    private PlatformAuthorityResult<PlatformOriginalNestedDomainClosure> ProviderNestedCleanup(PlatformOriginalNestedDomainRequest request, bool pure)
    {
        ProviderNestedFact fact;
        lock (_childCreateCommitGate)
        {
            if (request is null || !_originalProviderChildren.TryGetValue(request.Request.ParentChildLease.LeaseId, out var parent) ||
                parent.OriginalNested.SingleOrDefault(f => ReferenceEquals(f.Request, request)) is not { } original ||
                !ReferenceEquals(original.Parent, parent) || !ReferenceEquals(original.OriginalOwner, original) || !ProviderNestedHistory(parent) || original.InFlight)
                return PlatformAuthorityResult<PlatformOriginalNestedDomainClosure>.Fail(PlatformAuthorityStatus.Faulted, "Original nested cleanup owner unavailable/pending.");
            fact = original;
            if (fact.Closure is { } completed) return PlatformAuthorityResult<PlatformOriginalNestedDomainClosure>.Ok(completed);
            if (fact.NoEffectConsumed)
            { fact.Closure = new(request, true, true, true); return PlatformAuthorityResult<PlatformOriginalNestedDomainClosure>.Ok(fact.Closure); }
            if (!SupportsOriginalNestedDomains || !ProviderNestedSources(fact, false) || NestedProviderLineage(parent).Any(_pendingNestedSources.Contains))
                return PlatformAuthorityResult<PlatformOriginalNestedDomainClosure>.Fail(PlatformAuthorityStatus.Faulted, "Original nested cleanup source unavailable.");
            fact.InFlight = true;
            foreach (var source in NestedProviderLineage(parent)) _pendingNestedSources.Add(source);
        }
        try
        {
            var native = (INeutralOriginalNestedDomainProvider)_childRuntime!;
            var before = CurrentIncarnation;
            var result = native.ResolveOriginalNestedDomainCleanup(fact.NativeRequest);
            if (!result.IsSuccess && !pure)
            {
                bool invoke;
                lock (_childCreateCommitGate) { invoke = !fact.ContainInvoked && fact.CleanupRaw is null; if (invoke) fact.ContainInvoked = true; }
                if (invoke) result = native.ContainOriginalNestedDomain(fact.NativeRequest);
            }
            lock (_childCreateCommitGate) if (result.IsSuccess) fact.CleanupRaw ??= result.Value;
            var canonical = native.ResolveOriginalNestedDomainCleanup(fact.NativeRequest);
            var after = CurrentIncarnation;
            lock (_childCreateCommitGate)
            {
                if (!ProviderNestedSources(fact, false) || before.Value == 0 || after != before || fact.CleanupRaw is not { } raw ||
                    !canonical.IsSuccess || !ReferenceEquals(canonical.Value, raw) || !ReferenceEquals(raw.Request, fact.NativeRequest) ||
                    !raw.NoFurtherAdmission || !raw.NoFurtherChildUse)
                    return PlatformAuthorityResult<PlatformOriginalNestedDomainClosure>.Fail(PlatformAuthorityStatus.Faulted, "Original nested closure/source unknown or foreign; no repeat.");
                fact.Closure = new(request, raw.NoEffect, true, true);
                return PlatformAuthorityResult<PlatformOriginalNestedDomainClosure>.Ok(fact.Closure);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return PlatformAuthorityResult<PlatformOriginalNestedDomainClosure>.Fail(PlatformAuthorityStatus.Faulted, "Original nested cleanup unknown: " + exception.GetType().Name); }
        finally { lock (_childCreateCommitGate) { fact.InFlight = false; foreach (var source in NestedProviderLineage(fact.Parent)) _pendingNestedSources.Remove(source); } }
    }
}
