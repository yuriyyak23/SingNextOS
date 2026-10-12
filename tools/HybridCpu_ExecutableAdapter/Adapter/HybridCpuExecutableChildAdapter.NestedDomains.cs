using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

public sealed partial class HybridCpuExecutableChildAdapter : INeutralOriginalNestedDomainProvider
{
    private sealed class OriginalNestedFact
    {
        public OriginalNestedFact(NeutralOriginalNestedDomainRequest request, Child parent, Parent root, ulong identity,
            ExternalOriginalNestedChildRequest nativeRequest, Dictionary<Child, long> revisions)
        { OriginalOwner = this; Request = request; Parent = parent; Root = root; Identity = identity; NativeRequest = nativeRequest; Revisions = revisions; }
        public OriginalNestedFact OriginalOwner { get; }
        public NeutralOriginalNestedDomainRequest Request { get; }
        public Child Parent { get; }
        public Parent Root { get; }
        public ulong Identity { get; }
        public ExternalOriginalNestedChildRequest NativeRequest { get; }
        public Dictionary<Child, long> Revisions { get; }
        public ExternalOriginalNestedChildObservation? Raw, Canonical;
        public Child? Child;
        public bool InFlight, Invoked, NoEffectConsumed;
        public bool ContainInvoked;
        public NeutralVirtualizationResult<NeutralChildDomainCloseReceipt>? CleanupRaw;
        public NeutralOriginalNestedDomainClosure? Closure;
    }
    private readonly HashSet<Child> pendingNestedSources = [];
    public bool SupportsOriginalNestedDomains => childRuntime is IHybridCpuOriginalNestedChildRuntimeV1;
    private static bool NestedHistory(Child child) => child.NestedIssued == (ulong)child.OriginalNested.Count &&
        child.OriginalNested.All(f => ReferenceEquals(f.OriginalOwner, f) && ReferenceEquals(f.Parent, child) && f.Request.ParentChild == child.Neutral);
    private bool NestedIdentity(Child child)
    {
        var seen = new HashSet<Child>();
        for (Child? current = child; current is not null; current = current.NestedParent)
        {
            if (!seen.Add(current) || !NestedHistory(current) ||
                !originalChildren.TryGetValue(current.Neutral.Handle, out var original) || !ReferenceEquals(original, current) ||
                !children.TryGetValue(current.Neutral.Handle, out var route) || !ReferenceEquals(route, current)) return false;
            if (current.NestedParent is not { } parent)
                return current.NestedFactory is null && (!current.NestedCreationAllowed || current.ParentSource is { } rootOwner &&
                    rootOwner.OriginalChildCreates.Any(f => ReferenceEquals(f.OriginalOwner, f) && ReferenceEquals(f.Parent, rootOwner) &&
                        ReferenceEquals(f.Child, current) && f.Validated is { NoEffect: false, NestedCreationAllowed: true } admitted &&
                        ReferenceEquals(admitted.Raw.Receipt, current.CreateReceipt) && ReferenceEquals(admitted.Request, f.NativeRequest)));
            if (current.NestedFactory is not { Canonical: { NoEffect: false } raw } fact || !ReferenceEquals(fact.OriginalOwner, fact) ||
                !ReferenceEquals(fact.Child, current) || !ReferenceEquals(fact.Parent, parent) || !parent.OriginalNested.Contains(fact) ||
                !ReferenceEquals(raw, fact.Raw) || !ReferenceEquals(raw.Request, fact.NativeRequest) ||
                !ReferenceEquals(raw.Raw.Receipt, current.CreateReceipt) || !ReferenceEquals(fact.Root, current.ParentSource)) return false;
        }
        return false;
    }
    private bool NestedSources(OriginalNestedFact fact, bool active) =>
        ReferenceEquals(fact.OriginalOwner, fact) && NestedHistory(fact.Parent) && fact.Parent.OriginalNested.Contains(fact) &&
        NestedIdentity(fact.Parent) && ReferenceEquals(fact.Parent.ParentSource, fact.Root) &&
        originalParents.TryGetValue(fact.Root.Neutral, out var originalRoot) && ReferenceEquals(originalRoot, fact.Root) &&
        parents.TryGetValue(fact.Root.Neutral, out var rootRoute) && ReferenceEquals(rootRoute, fact.Root) &&
        ReferenceEquals(fact.Root.BindObservation?.Receipt, fact.Root.Admission) && fact.Root.Admission is not null &&
        ReferenceEquals(fact.Parent.CreateReceipt, fact.NativeRequest.ParentAdmission) &&
        (!active || !fact.Root.Closed && fact.Root.CloseRequest is null && fact.Revisions.All(pair =>
            !pair.Key.StateRevisionExhausted && pair.Key.StateRevision == pair.Value &&
            pair.Key.State is NeutralChildDomainState.Created or NeutralChildDomainState.Running or NeutralChildDomainState.Parked &&
            !pair.Key.CloseMayHaveEffect && pair.Key.OriginalCloseInvocation is not { Invoked: true, NoEffectConsumed: false }));
    private bool HasOriginalNestedPins(Child child) => pendingNestedSources.Contains(child) || !NestedHistory(child) ||
        child.OriginalNested.Any(f => f.InFlight || f.Closure is null && !f.NoEffectConsumed &&
            (f.Child is null || !NestedIdentity(f.Child) || f.Child.State != NeutralChildDomainState.Closed ||
                f.Child.CloseReceipt is not { IsTerminal: true } || f.Child.OriginalCloseInvocation is not { Validated.NoFurtherUse: true }));

    public NeutralVirtualizationResult<NeutralNestedDomainLease> CreateOriginalNestedDomain(NeutralOriginalNestedDomainRequest request)
    {
        OriginalNestedFact fact;
        lock (sync)
        {
            if (!SupportsOriginalNestedDomains || request is null || !TryChild(request.ParentChild, out var parent, out var failure))
                return Fail<NeutralNestedDomainLease>(NeutralVirtualizationStatus.NotInvoked, "Original nested provider/parent unavailable.");
            var valid = NeutralChildDomainContract.ValidateCreate(request.ParentChild.ParentLease, request.Intent);
            if (!valid.IsSuccess) return Fail<NeutralNestedDomainLease>(valid);
            var admitted = request.ParentChild.Intent.Authority.ChildAuthority;
            if ((request.Intent.Authority.ParentAuthority & ~admitted) != 0 || (request.Intent.Authority.ChildAuthority & ~admitted) != 0 ||
                parent!.CreateReceipt is null || parent.ParentSource is null || !parent.NestedCreationAllowed || !NestedIdentity(parent))
                return Fail<NeutralNestedDomainLease>(NeutralVirtualizationStatus.NotInvoked, "Nested original permission/subset refused before effect.");
            if (parent.OriginalNested.Any(f => ReferenceEquals(f.Request, request) || !f.NoEffectConsumed && f.Closure is null && f.Child is null))
                return Fail<NeutralNestedDomainLease>(NeutralVirtualizationStatus.NotInvoked, "Original nested request already invoked or unresolved; no repeat.");
            var revisions = new Dictionary<Child, long>();
            for (Child? cursor = parent; cursor is not null; cursor = cursor.NestedParent)
            {
                if (revisions.Count >= ExternalOriginalNestedChildContract.MaximumDepth || revisions.ContainsKey(cursor) ||
                    pendingNestedSources.Contains(cursor) || pendingChildCloses.Contains(cursor.Neutral.Handle) ||
                    pendingChildTransitions.Contains(cursor.Neutral.Handle) || pendingChildStarts.Contains(cursor.Neutral.Handle) || HasPendingGuestAdmission(cursor.Neutral) ||
                    cursor.StateRevisionExhausted || cursor.State is not (NeutralChildDomainState.Created or NeutralChildDomainState.Running or NeutralChildDomainState.Parked))
                    return Fail<NeutralNestedDomainLease>(NeutralVirtualizationStatus.NotInvoked, "Nested lineage is unavailable or pending.");
                revisions.Add(cursor, cursor.StateRevision);
            }
            if (nextIdentity == 0 || nextIdentity == ulong.MaxValue || nextOperation == 0 || nextOperation == ulong.MaxValue || parent.NestedIssued == ulong.MaxValue)
                return Fail<NeutralNestedDomainLease>(NeutralVirtualizationStatus.NotInvoked, "Nested identity capacity exhausted before effect.");
            var nativeRequest = new ExternalOriginalNestedChildRequest(parent.CreateReceipt,
                new(Guid.NewGuid(), ToExternal(request.Intent.Authority.ChildAuthority), checked((ulong)request.Intent.Profile.MaximumGuestMemoryBytes), Op()),
                request.Intent.Authority.ChildAuthority.HasFlag(NeutralChildAuthorityClass.Lifecycle));
            fact = new(request, parent, parent.ParentSource, Next(), nativeRequest, revisions) { InFlight = true };
            parent.OriginalNested.Add(fact); parent.NestedIssued++;
            foreach (var source in revisions.Keys) pendingNestedSources.Add(source);
        }
        try { return ReconcileNested(fact, invoke: true); }
        finally { lock (sync) { fact.InFlight = false; foreach (var source in fact.Revisions.Keys) pendingNestedSources.Remove(source); } }
    }

    public NeutralVirtualizationResult<NeutralNestedDomainLease> ResolveOriginalNestedDomain(NeutralOriginalNestedDomainRequest request)
    {
        OriginalNestedFact fact;
        lock (sync)
        {
            if (request is null || !SupportsOriginalNestedDomains || !originalChildren.TryGetValue(request.ParentChild.Handle, out var parent) ||
                parent.OriginalNested.SingleOrDefault(f => ReferenceEquals(f.Request, request)) is not { } original ||
                !ReferenceEquals(original.OriginalOwner, original) || !NestedHistory(parent) || original.InFlight)
                return Fail<NeutralNestedDomainLease>(NeutralVirtualizationStatus.Ambiguous, "Original nested invocation unavailable/pending.");
            fact = original;
            if (fact.NoEffectConsumed) return Fail<NeutralNestedDomainLease>(NeutralVirtualizationStatus.NotInvoked, "Exact original nested no-effect already consumed.");
            if (fact.Child is { AdmissionPublished: true } known && NestedIdentity(known)) return Ok(new NeutralNestedDomainLease(known.Neutral, parent.Neutral));
            if (!fact.Invoked || !NestedSources(fact, false) || fact.Revisions.Keys.Any(pendingNestedSources.Contains))
                return Fail<NeutralNestedDomainLease>(NeutralVirtualizationStatus.Ambiguous, "Original nested effect/source remains unknown.");
            fact.InFlight = true;
            foreach (var source in fact.Revisions.Keys) pendingNestedSources.Add(source);
        }
        try { return ReconcileNested(fact, invoke: false); }
        finally { lock (sync) { fact.InFlight = false; foreach (var source in fact.Revisions.Keys) pendingNestedSources.Remove(source); } }
    }

    private NeutralVirtualizationResult<NeutralNestedDomainLease> ReconcileNested(OriginalNestedFact fact, bool invoke, bool cleanupOnly = false)
    {
        var native = (IHybridCpuOriginalNestedChildRuntimeV1)childRuntime;
        try
        {
            if (invoke)
            {
                var before = root.QueryFeatures();
                lock (sync)
                {
                    if (!NestedSources(fact, true) || before.Generation != externalFeatures.Generation || before.ContractVersion != externalFeatures.ContractVersion)
                    { fact.NoEffectConsumed = true; return Fail<NeutralNestedDomainLease>(NeutralVirtualizationStatus.NotInvoked, "Nested sources changed before effect."); }
                    fact.Invoked = true;
                }
                try { var raw = native.CreateOriginalNestedChild(fact.NativeRequest); lock (sync) fact.Raw = raw; }
                catch (Exception exception) when (exception is not StackOverflowException) { /* Only pure reconciliation after lost reply. */ }
            }
            var canonical = native.ResolveOriginalNestedChild(fact.NativeRequest);
            lock (sync)
            {
                fact.Canonical = canonical;
                if (fact.Raw is null) fact.Raw = canonical;
                if (!NestedSources(fact, false) || canonical is null || !ReferenceEquals(canonical, fact.Raw) || !ReferenceEquals(canonical.Request, fact.NativeRequest))
                    return Fail<NeutralNestedDomainLease>(NeutralVirtualizationStatus.Ambiguous, "Original nested raw/source unknown or foreign.");
                if (canonical.NoEffect)
                {
                    if (!canonical.NoFurtherAdmission || canonical.Raw.Receipt is not null || canonical.NestedCreationAllowed)
                        return Fail<NeutralNestedDomainLease>(NeutralVirtualizationStatus.Ambiguous, "Malformed nested no-effect.");
                    fact.NoEffectConsumed = true;
                    return Fail<NeutralNestedDomainLease>(NeutralVirtualizationStatus.NotInvoked, "Exact nested no-effect consumed.");
                }
                var receipt = canonical.Raw.Receipt;
                var request = fact.NativeRequest.CreationRequest;
                if (canonical.NoFurtherAdmission || canonical.Raw.Outcome != ExternalRuntimeOutcome.Bound || receipt is null ||
                    receipt.Lease.Handle.Value == Guid.Empty || receipt.Lease.Epoch.Value == 0 || receipt.Lease.Parent != fact.Root.External ||
                    receipt.Operation != request.Operation || receipt.GrantedAuthority != request.RequestedAuthority || receipt.GuestMemoryLimitBytes != request.GuestMemoryLimitBytes ||
                    receipt.ContractVersion != externalFeatures.ContractVersion || receipt.ManifestGeneration != externalFeatures.Generation ||
                    receipt.State != ExternalChildDomainState.Ready || canonical.NestedCreationAllowed != fact.NativeRequest.AllowNestedCreation)
                    return Fail<NeutralNestedDomainLease>(NeutralVirtualizationStatus.Ambiguous, "Malformed original nested receipt retained.");
                if (fact.Child is null)
                {
                    var lease = new NeutralChildDomainLease(fact.Root.Neutral, new(fact.Identity), new(1), fact.Request.Intent);
                    fact.Child = new(lease, receipt.Lease, receipt.Lease.Parent, NeutralChildDomainState.Created)
                    { CreateReceipt = receipt, ParentSource = fact.Root, NestedParent = fact.Parent, NestedFactory = fact, NestedCreationAllowed = canonical.NestedCreationAllowed, AdmissionPublished = false };
                    originalChildren.Add(lease.Handle, fact.Child); children.Add(lease.Handle, fact.Child);
                }
            }
            var after = root.QueryFeatures();
            lock (sync)
            {
                if (!NestedSources(fact, !cleanupOnly) || after.Generation != externalFeatures.Generation || after.ContractVersion != externalFeatures.ContractVersion ||
                    !NestedIdentity(fact.Child!)) return Fail<NeutralNestedDomainLease>(NeutralVirtualizationStatus.Ambiguous, "Known nested child retained; final source changed.");
                if (!cleanupOnly) fact.Child!.AdmissionPublished = true;
                return Ok(new NeutralNestedDomainLease(fact.Child.Neutral, fact.Parent.Neutral));
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return Fail<NeutralNestedDomainLease>(NeutralVirtualizationStatus.Ambiguous, "Original nested resolution/source unavailable: " + exception.GetType().Name); }
    }

    public NeutralVirtualizationResult<NeutralOriginalNestedDomainClosure> ContainOriginalNestedDomain(NeutralOriginalNestedDomainRequest request) => NestedCleanup(request, false);
    public NeutralVirtualizationResult<NeutralOriginalNestedDomainClosure> ResolveOriginalNestedDomainCleanup(NeutralOriginalNestedDomainRequest request) => NestedCleanup(request, true);
    private NeutralVirtualizationResult<NeutralOriginalNestedDomainClosure> NestedCleanup(NeutralOriginalNestedDomainRequest request, bool pure)
    {
        OriginalNestedFact fact;
        lock (sync)
        {
            if (request is null || !originalChildren.TryGetValue(request.ParentChild.Handle, out var parent) ||
                parent.OriginalNested.SingleOrDefault(f => ReferenceEquals(f.Request, request)) is not { } original ||
                !ReferenceEquals(original.Parent, parent) || !ReferenceEquals(original.OriginalOwner, original) || !NestedHistory(parent) || original.InFlight)
                return Fail<NeutralOriginalNestedDomainClosure>(NeutralVirtualizationStatus.Ambiguous, "Original nested cleanup owner unavailable/pending.");
            fact = original;
            if (fact.Closure is { } completed) return Ok(completed);
            if (fact.NoEffectConsumed)
            { fact.Closure = new(request, true, true, true); return Ok(fact.Closure); }
            if (!fact.Invoked || !NestedSources(fact, false) || fact.Revisions.Keys.Any(pendingNestedSources.Contains))
                return Fail<NeutralOriginalNestedDomainClosure>(NeutralVirtualizationStatus.Ambiguous, "Original nested source/effect unavailable.");
            fact.InFlight = true;
            foreach (var source in fact.Revisions.Keys) pendingNestedSources.Add(source);
        }
        try
        {
            var known = ReconcileNested(fact, false, cleanupOnly: true);
            lock (sync)
            {
                if (fact.NoEffectConsumed) { fact.Closure = new(request, true, true, true); return Ok(fact.Closure); }
                if (!known.IsSuccess || fact.Child is null || !NestedSources(fact, false))
                    return Fail<NeutralOriginalNestedDomainClosure>(NeutralVirtualizationStatus.Ambiguous, "Original nested admission remains unknown.");
                if (pure && !fact.ContainInvoked)
                    return Fail<NeutralOriginalNestedDomainClosure>(NeutralVirtualizationStatus.Ambiguous, "Original nested containment has not run.");
            }
            bool invokeClose;
            lock (sync) { invokeClose = !pure && !fact.ContainInvoked; if (invokeClose) fact.ContainInvoked = true; }
            var closed = invokeClose ? CloseChildDomain(fact.Child!.Neutral) : ResolveOriginalChildClose(fact.Child!.Neutral);
            lock (sync) fact.CleanupRaw = closed; // Exact existing child Close raw before final source observation.
            var after = root.QueryFeatures();
            lock (sync)
            {
                if (!closed.IsSuccess || fact.Child!.OriginalCloseInvocation is not { Validated.NoFurtherUse: true } ||
                    fact.Child.State != NeutralChildDomainState.Closed || !NestedSources(fact, false) ||
                    after.Generation != externalFeatures.Generation || after.ContractVersion != externalFeatures.ContractVersion)
                    return Fail<NeutralOriginalNestedDomainClosure>(NeutralVirtualizationStatus.Ambiguous, "Original nested closure/current source unavailable; no repeat.");
                fact.Closure = new(request, false, true, true);
                return Ok(fact.Closure);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return Fail<NeutralOriginalNestedDomainClosure>(NeutralVirtualizationStatus.Ambiguous, "Original nested containment unknown: " + exception.GetType().Name); }
        finally { lock (sync) { fact.InFlight = false; foreach (var source in fact.Revisions.Keys) pendingNestedSources.Remove(source); } }
    }
}
