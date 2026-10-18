using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformOriginalVirtualEventProvider
{
    private sealed class ProviderEventFact(PlatformOriginalVirtualEventRequest request, ChildRecord child)
    {
        public ProviderEventFact? Self;
        public PlatformOriginalVirtualEventRequest Request { get; } = request;
        public ChildRecord Child { get; } = child;
        public DomainRecord? Parent { get; } = child.CreateParent;
        public RootBindRecord? RootFactory { get; } = child.CreateParent?.BindSource;
        public NeutralOriginalVirtualEventRequest Native { get; } = new(new(child.Neutral,
            (NeutralVirtualEventClass)(int)request.Request.EventClass, request.Request.SourceResourceId));
        public bool InFlight;
        public NeutralVirtualizationResult<NeutralVirtualEventReceipt>? Raw;
        public NeutralOriginalVirtualEventClosure? CleanupRaw;
        public PlatformOriginalVirtualEventClosure? Closure;
    }
    private bool EventSources(ProviderEventFact fact) => ReferenceEquals(fact.Self, fact) && IsOriginalChildOwner(fact.Child) &&
        _providerChildren.TryGetValue(fact.Child.Lease.LeaseId, out var current) && ReferenceEquals(current, fact.Child) &&
        fact.Child.Lease == fact.Request.Request.ChildLease && (ulong)fact.Child.OriginalEvents.Count == fact.Child.EventsIssued &&
        fact.Child.OriginalEvents.Contains(fact) && fact.Parent is { } parent && ReferenceEquals(fact.Child.CreateParent, parent) &&
        _domains.TryGetValue(parent.Lease.LeaseId, out var route) && ReferenceEquals(route, parent) &&
        ReferenceEquals(parent.BindSource, fact.RootFactory) && fact.RootFactory is { } factory &&
        _originalRootBinds.TryGetValue(parent.Lease.LeaseId, out var original) && ReferenceEquals(original, factory) && ReferenceEquals(factory.OriginalDomain, parent);
    private bool HasProviderEventPins(ChildRecord child) => (ulong)child.OriginalEvents.Count != child.EventsIssued ||
        child.OriginalEvents.Any(f => !EventSources(f) || f.InFlight || f.Closure is null);

    public PlatformAuthorityResult<PlatformVirtualEventReceipt> InjectOriginalVirtualEvent(PlatformOriginalVirtualEventRequest request)
    {
        if (request is null || _childRuntime is not INeutralOriginalVirtualEventProvider native)
            return ChildContourBlocked<PlatformVirtualEventReceipt>("original external software event");
        var valid = PlatformVirtualEventContract.ValidateRequest(request.Request);
        if (!valid.IsSuccess) return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(valid.Status, valid.Message!);
        var source = ValidateOriginalNativeMappingSource();
        if (!source.IsSuccess) return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(source.Status, source.Message!);
        ProviderEventFact fact;
        lock (_childCreateCommitGate)
        {
            if (!_providerChildren.TryGetValue(request.Request.ChildLease.LeaseId, out var child) || !IsOriginalChildOwner(child) || child.Lease != request.Request.ChildLease ||
                child.CloseInFlight || child.TransitionInFlight || child.FailedClose is not null || child.FailedTransition is not null || child.ClosureReceipt is not null ||
                child.GuestMapInFlight || child.UnclosedGuestMap is not null || HasArtifactBindInterlock(child.Lease) || HasGuestUnmapInterlock(child.Lease) ||
                HasProviderIoPins(child.Lease, true) || HasProviderEventPins(child) || child.EventsIssued == ulong.MaxValue ||
                child.OriginalEvents.Any(f => ReferenceEquals(f.Request, request)))
                return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(PlatformAuthorityStatus.NotAccepted, "Original event owner/admission is unavailable.");
            child.OriginalEvents.EnsureCapacity(child.OriginalEvents.Count + 1);
            fact = new(request, child); fact.Self = fact;
            child.OriginalEvents.Add(fact); child.EventsIssued++;
            if (!EventSources(fact)) return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Event factories/history unavailable.");
            fact.InFlight = true;
        }
        try
        {
            var raw = native.InjectOriginalVirtualEvent(fact.Native);
            lock (_childCreateCommitGate) fact.Raw = raw;
            var closure = native.ResolveOriginalVirtualEventClosure(fact.Native);
            lock (_childCreateCommitGate) fact.CleanupRaw = closure;
            source = ValidateOriginalNativeMappingSource();
            if (!source.IsSuccess || closure is null || !closure.NoFurtherDelivery || !ReferenceEquals(closure.Admission, fact.Native) ||
                !ReferenceEquals(closure, native.ResolveOriginalVirtualEventClosure(fact.Native)))
                return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original delivery lacks exact current settlement.");
            lock (_childCreateCommitGate)
            {
                if (!EventSources(fact) || fact.Child.CloseInFlight || fact.Child.TransitionInFlight || fact.Child.ClosureReceipt is not null)
                    return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Event owner changed before publication.");
                fact.Closure = new(request, true, closure.NoEffect);
                if (!raw.IsSuccess || closure.NoEffect)
                    return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(closure.NoEffect ? PlatformAuthorityStatus.Denied : PlatformAuthorityStatus.Faulted, raw.Reason);
                var receipt = raw.Value;
                if (!NeutralVirtualEventContract.ValidateReceipt(fact.Native.Request, receipt).IsSuccess)
                    return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Malformed original neutral delivery.");
                var result = new PlatformVirtualEventReceipt(fact.Child.Lease.LeaseId, fact.Child.Lease.Generation,
                    fact.Child.Lease.ParentDomainLease.LeaseId, fact.Child.Lease.ParentDomainLease.Generation, receipt.Sequence,
                    request.Request.EventClass, request.Request.SourceResourceId);
                return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Ok(result);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(PlatformAuthorityStatus.Faulted, exception.GetType().Name); }
        finally { lock (_childCreateCommitGate) fact.InFlight = false; }
    }
    public PlatformAuthorityResult<PlatformOriginalVirtualEventClosure> ContainOriginalVirtualEvent(PlatformOriginalVirtualEventRequest request)
    {
        ProviderEventFact? fact;
        lock (_childCreateCommitGate)
        {
            fact = _originalProviderChildren.Values.SelectMany(c => c.OriginalEvents).SingleOrDefault(f => ReferenceEquals(f.Request, request));
            if (fact is null || !EventSources(fact) || fact.InFlight)
                return PlatformAuthorityResult<PlatformOriginalVirtualEventClosure>.Fail(PlatformAuthorityStatus.Faulted, "Original event history/owner unavailable.");
            if (fact.Closure is { } completed) return PlatformAuthorityResult<PlatformOriginalVirtualEventClosure>.Ok(completed);
            fact.InFlight = true;
        }
        try
        {
            if (_childRuntime is not INeutralOriginalVirtualEventProvider native)
                return PlatformAuthorityResult<PlatformOriginalVirtualEventClosure>.Fail(PlatformAuthorityStatus.Unsupported, "Original event cleanup unavailable.");
            var raw = fact.CleanupRaw ?? native.ResolveOriginalVirtualEventClosure(fact.Native);
            if (raw is null)
            {
                var contained = native.ContainOriginalVirtualEvent(fact.Native);
                raw = contained.IsSuccess ? contained.Value : null;
            }
            lock (_childCreateCommitGate) fact.CleanupRaw ??= raw;
            var source = ValidateOriginalNativeMappingSource();
            if (!source.IsSuccess || raw is null || !raw.NoFurtherDelivery || !ReferenceEquals(raw.Admission, fact.Native) ||
                !ReferenceEquals(raw, native.ResolveOriginalVirtualEventClosure(fact.Native)))
                return PlatformAuthorityResult<PlatformOriginalVirtualEventClosure>.Fail(PlatformAuthorityStatus.Faulted, "Original event containment remains pinned.");
            lock (_childCreateCommitGate)
            {
                if (!EventSources(fact)) return PlatformAuthorityResult<PlatformOriginalVirtualEventClosure>.Fail(PlatformAuthorityStatus.Faulted, "Event cleanup source changed.");
                fact.Closure = new(request, true, raw.NoEffect); return PlatformAuthorityResult<PlatformOriginalVirtualEventClosure>.Ok(fact.Closure);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return PlatformAuthorityResult<PlatformOriginalVirtualEventClosure>.Fail(PlatformAuthorityStatus.Faulted, exception.GetType().Name); }
        finally { lock (_childCreateCommitGate) fact.InFlight = false; }
    }
    public PlatformOriginalVirtualEventClosure? ResolveOriginalVirtualEventClosure(PlatformOriginalVirtualEventRequest request)
    {
        lock (_childCreateCommitGate)
        {
            var fact = _originalProviderChildren.Values.SelectMany(c => c.OriginalEvents).SingleOrDefault(f => ReferenceEquals(f.Request, request));
            return fact is not null && EventSources(fact) ? fact.Closure : null;
        }
    }
}
