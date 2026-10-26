using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformOriginalVirtualEventProvider
{
    private enum ProviderEventAdmissionState { Prepared, Invoked, RefusedBeforeInvocation }
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
        public ProviderEventAdmissionState AdmissionState = ProviderEventAdmissionState.Prepared;
        public NeutralVirtualizationResult<NeutralVirtualEventReceipt>? Raw;
        public NeutralOriginalVirtualEventClosure? CleanupRaw;
        public PlatformOriginalVirtualEventClosure? Closure;
        public bool ClosureProduced;
        public (PlatformOriginalVirtualEventClosure Closure, NeutralOriginalVirtualEventClosure? Raw)? OriginalClosure;
    }
    private bool EventSources(ProviderEventFact fact) => ReferenceEquals(fact.Self, fact) && IsOriginalChildOwner(fact.Child) &&
        _providerChildren.TryGetValue(fact.Child.Lease.LeaseId, out ChildRecord? current) && ReferenceEquals(current, fact.Child) &&
        fact.Child.Lease == fact.Request.Request.ChildLease && (ulong)fact.Child.OriginalEvents.Count == fact.Child.EventsIssued &&
        fact.Child.OriginalEvents.Contains(fact) && fact.Parent is { } parent && ReferenceEquals(fact.Child.CreateParent, parent) &&
        _domains.TryGetValue(parent.Lease.LeaseId, out DomainRecord? route) && ReferenceEquals(route, parent) &&
        ReferenceEquals(parent.BindSource, fact.RootFactory) && fact.RootFactory is { } factory &&
        _originalRootBinds.TryGetValue(parent.Lease.LeaseId, out RootBindRecord? original) && ReferenceEquals(original, factory) && ReferenceEquals(factory.OriginalDomain, parent);
    private bool HasProviderEventPins(ChildRecord child) => (ulong)child.OriginalEvents.Count != child.EventsIssued ||
        child.OriginalEvents.Any(f => !EventSources(f) || f.InFlight || !HasRetainedEventClosure(f));
    private static bool HasRetainedEventClosure(ProviderEventFact fact) => fact.ClosureProduced && fact.OriginalClosure is { } original &&
        ReferenceEquals(fact.Closure, original.Closure) && ReferenceEquals(fact.CleanupRaw, original.Raw) &&
        ReferenceEquals(original.Closure.Admission, fact.Request) && original.Closure.NoFurtherDelivery &&
        (original.Raw is { } raw
            ? fact.AdmissionState == ProviderEventAdmissionState.Invoked && ReferenceEquals(raw.Admission, fact.Native) && raw.NoFurtherDelivery && original.Closure.NoEffect == raw.NoEffect
            : original.Closure.NoEffect && HasNeverInvokedEventDisposition(fact));
    private static bool HasNeverInvokedEventDisposition(ProviderEventFact fact) =>
        fact.AdmissionState == ProviderEventAdmissionState.RefusedBeforeInvocation && fact.Raw is null && fact.CleanupRaw is null;
    private static bool CaptureEventClosure(ProviderEventFact fact, NeutralOriginalVirtualEventClosure? raw)
    {
        if (fact.ClosureProduced || fact.OriginalClosure is not null || fact.Closure is not null ||
            !ReferenceEquals(fact.CleanupRaw, raw) || (raw is null ? !HasNeverInvokedEventDisposition(fact) :
                fact.AdmissionState != ProviderEventAdmissionState.Invoked || !ReferenceEquals(raw.Admission, fact.Native) || !raw.NoFurtherDelivery)) return false;
        fact.ClosureProduced = true;
        PlatformOriginalVirtualEventClosure closure = new(fact.Request, true, raw?.NoEffect ?? true);
        fact.OriginalClosure = (closure, raw);
        fact.Closure = closure;
        return true;
    }

    public PlatformAuthorityResult<PlatformVirtualEventReceipt> InjectOriginalVirtualEvent(PlatformOriginalVirtualEventRequest request)
    {
        if (request is null || _childRuntime is not INeutralOriginalVirtualEventProvider native)
            return ChildContourBlocked<PlatformVirtualEventReceipt>("original external software event");
        PlatformAuthorityResult valid = PlatformVirtualEventContract.ValidateRequest(request.Request);
        if (!valid.IsSuccess) return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(valid.Status, valid.Message!);
        ProviderEventFact fact;
        lock (_childCreateCommitGate)
        {
            if (!_providerChildren.TryGetValue(request.Request.ChildLease.LeaseId, out ChildRecord? child) || !IsOriginalChildOwner(child) || child.Lease != request.Request.ChildLease ||
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
            PlatformAuthorityResult source = ValidateOriginalNativeMappingSource(); // Original custody precedes this external callback.
            if (!source.IsSuccess) return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(source.Status, source.Message!);
            lock (_childCreateCommitGate)
            {
                if (!EventSources(fact) || fact.AdmissionState != ProviderEventAdmissionState.Prepared ||
                    fact.Child.CloseInFlight || fact.Child.TransitionInFlight || fact.Child.FailedClose is not null ||
                    fact.Child.FailedTransition is not null || fact.Child.ClosureReceipt is not null)
                    return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original event source changed before SDK invocation.");
                fact.AdmissionState = ProviderEventAdmissionState.Invoked; // Before the effect-capable SDK call; never reverted on exception.
            }
            NeutralVirtualizationResult<NeutralVirtualEventReceipt> raw = native.InjectOriginalVirtualEvent(fact.Native);
            lock (_childCreateCommitGate) fact.Raw = raw;
            NeutralOriginalVirtualEventClosure? closure = native.ResolveOriginalVirtualEventClosure(fact.Native);
            lock (_childCreateCommitGate) fact.CleanupRaw = closure;
            bool canonical = closure is not null && closure.NoFurtherDelivery && ReferenceEquals(closure.Admission, fact.Native) &&
                ReferenceEquals(closure, native.ResolveOriginalVirtualEventClosure(fact.Native));
            source = ValidateOriginalNativeMappingSource(); // Fresh after the last external query, outside the commit gate.
            if (!source.IsSuccess || closure is null || !canonical)
                return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original delivery lacks exact current settlement.");
            lock (_childCreateCommitGate)
            {
                if (!EventSources(fact) || !ReferenceEquals(fact.CleanupRaw, closure) || fact.Raw != raw ||
                    fact.Child.CloseInFlight || fact.Child.TransitionInFlight || fact.Child.ClosureReceipt is not null)
                    return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Event owner changed before publication.");
                if (!CaptureEventClosure(fact, closure))
                    return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Original event delivery closure custody unavailable.");
                if (!raw.IsSuccess || closure.NoEffect)
                    return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(closure.NoEffect ? PlatformAuthorityStatus.Denied : PlatformAuthorityStatus.Faulted, raw.Reason);
                NeutralVirtualEventReceipt receipt = raw.Value;
                if (!NeutralVirtualEventContract.ValidateReceipt(fact.Native.Request, receipt).IsSuccess)
                    return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Fail(PlatformAuthorityStatus.Faulted, "Malformed original neutral delivery.");
                PlatformVirtualEventReceipt result = new PlatformVirtualEventReceipt(fact.Child.Lease.LeaseId, fact.Child.Lease.Generation,
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
            if (fact.ClosureProduced || fact.OriginalClosure is not null || fact.Closure is not null)
                return HasRetainedEventClosure(fact) ? PlatformAuthorityResult<PlatformOriginalVirtualEventClosure>.Ok(fact.Closure!) :
                    PlatformAuthorityResult<PlatformOriginalVirtualEventClosure>.Fail(PlatformAuthorityStatus.Faulted, "Produced original event closure/raw custody lost; retained without replay.");
            fact.InFlight = true;
        }
        try
        {
            if (fact.AdmissionState != ProviderEventAdmissionState.Invoked)
            {
                PlatformAuthorityResult fresh = ValidateOriginalNativeMappingSource();
                if (!fresh.IsSuccess) return PlatformAuthorityResult<PlatformOriginalVirtualEventClosure>.Fail(fresh.Status, fresh.Message!);
                lock (_childCreateCommitGate)
                {
                    if (!EventSources(fact))
                        return PlatformAuthorityResult<PlatformOriginalVirtualEventClosure>.Fail(PlatformAuthorityStatus.Faulted, "Original never-invoked event owner changed.");
                    if (fact.AdmissionState == ProviderEventAdmissionState.Prepared)
                        fact.AdmissionState = ProviderEventAdmissionState.RefusedBeforeInvocation;
                    if (!CaptureEventClosure(fact, null))
                        return PlatformAuthorityResult<PlatformOriginalVirtualEventClosure>.Fail(PlatformAuthorityStatus.Faulted, "Original never-invoked event disposition unavailable.");
                    return PlatformAuthorityResult<PlatformOriginalVirtualEventClosure>.Ok(fact.Closure!);
                }
            }
            if (_childRuntime is not INeutralOriginalVirtualEventProvider native)
                return PlatformAuthorityResult<PlatformOriginalVirtualEventClosure>.Fail(PlatformAuthorityStatus.Unsupported, "Original event cleanup unavailable.");
            NeutralOriginalVirtualEventClosure? raw = fact.CleanupRaw ?? native.ResolveOriginalVirtualEventClosure(fact.Native);
            if (raw is null)
            {
                NeutralVirtualizationResult<NeutralOriginalVirtualEventClosure> contained = native.ContainOriginalVirtualEvent(fact.Native);
                raw = contained.IsSuccess ? contained.Value : null;
            }
            lock (_childCreateCommitGate) fact.CleanupRaw ??= raw;
            bool canonical = raw is not null && raw.NoFurtherDelivery && ReferenceEquals(raw.Admission, fact.Native) &&
                ReferenceEquals(raw, native.ResolveOriginalVirtualEventClosure(fact.Native));
            PlatformAuthorityResult source = ValidateOriginalNativeMappingSource(); // Query callbacks may substitute the native source.
            if (!source.IsSuccess || raw is null || !canonical)
                return PlatformAuthorityResult<PlatformOriginalVirtualEventClosure>.Fail(PlatformAuthorityStatus.Faulted, "Original event containment remains pinned.");
            lock (_childCreateCommitGate)
            {
                if (!EventSources(fact) || !ReferenceEquals(fact.CleanupRaw, raw))
                    return PlatformAuthorityResult<PlatformOriginalVirtualEventClosure>.Fail(PlatformAuthorityStatus.Faulted, "Event cleanup source/raw changed.");
                if (!CaptureEventClosure(fact, raw))
                    return PlatformAuthorityResult<PlatformOriginalVirtualEventClosure>.Fail(PlatformAuthorityStatus.Faulted, "Original event containment closure custody unavailable.");
                return PlatformAuthorityResult<PlatformOriginalVirtualEventClosure>.Ok(fact.Closure!);
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
            ProviderEventFact? fact = _originalProviderChildren.Values.SelectMany(c => c.OriginalEvents).SingleOrDefault(f => ReferenceEquals(f.Request, request));
            return fact is not null && EventSources(fact) && HasRetainedEventClosure(fact) ? fact.Closure : null;
        }
    }
}
