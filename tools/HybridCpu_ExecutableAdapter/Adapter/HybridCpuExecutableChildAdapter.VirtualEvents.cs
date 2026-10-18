using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

public sealed partial class HybridCpuExecutableChildAdapter : INeutralOriginalVirtualEventProvider
{
    private sealed class OriginalEventFact(NeutralOriginalVirtualEventRequest request, Child child,
        ExternalOriginalChildEventRequest native)
    {
        public OriginalEventFact? Self;
        public NeutralOriginalVirtualEventRequest Request { get; } = request;
        public Child Child { get; } = child;
        public Parent Parent { get; } = child.ParentSource!;
        public ExternalOriginalChildEventRequest Native { get; } = native;
        public long Revision { get; } = child.StateRevision;
        public bool InFlight, SourceFaulted, ConsumeInvoked, ContainInvoked;
        public ExternalOriginalChildEventObservation? AdmissionRaw, TerminalRaw;
        public NeutralOriginalVirtualEventClosure? Closure;
        public NeutralVirtualEventReceipt? Receipt;
    }
    private bool EventSources(OriginalEventFact fact) => !fact.SourceFaulted && ReferenceEquals(fact.Self, fact) &&
        (ulong)fact.Child.OriginalEvents.Count == fact.Child.OriginalEventsIssued && fact.Child.OriginalEvents.Contains(fact) &&
        originalChildren.TryGetValue(fact.Child.Neutral.Handle, out var child) && ReferenceEquals(child, fact.Child) &&
        children.TryGetValue(fact.Child.Neutral.Handle, out var route) && ReferenceEquals(route, child) &&
        ReferenceEquals(child.CreateReceipt, fact.Native.Admission) && ReferenceEquals(child.ParentSource, fact.Parent) &&
        ReferenceEquals(fact.Parent.OriginalOwner, fact.Parent) && originalParents.TryGetValue(fact.Parent.Neutral, out var parent) && ReferenceEquals(parent, fact.Parent) &&
        parents.TryGetValue(fact.Parent.Neutral, out var parentRoute) && ReferenceEquals(parentRoute, parent) &&
        fact.Parent.Admission is { } admission && ReferenceEquals(parent.BindObservation?.Receipt, admission);
    private bool HasOriginalEventPins(Child child) => (ulong)child.OriginalEvents.Count != child.OriginalEventsIssued ||
        child.OriginalEvents.Any(f => !ReferenceEquals(f.Self, f) || !ReferenceEquals(f.Child, child) || f.InFlight || f.Closure is null);
    private bool CurrentEventSource(OriginalEventFact fact, bool publication)
    {
        var current = root.QueryFeatures(); // External source callback is outside the SDK owner gate.
        lock (sync)
        {
            if (current.ContractVersion != externalFeatures.ContractVersion || current.Generation != externalFeatures.Generation) fact.SourceFaulted = true;
            if (publication && (fact.Child.StateRevisionExhausted || fact.Child.StateRevision != fact.Revision ||
                fact.Parent.CloseInvoked || fact.Parent.Closed || !NeutralVirtualEventContract.ValidateRequest(fact.Request.Request, fact.Child.State).IsSuccess)) return false;
            return EventSources(fact);
        }
    }
    private bool ExactEventRaw(OriginalEventFact fact, ExternalOriginalChildEventObservation raw, bool terminal) =>
        ReferenceEquals(raw.Request, fact.Native) && raw.ContractVersion == externalFeatures.ContractVersion && raw.ManifestGeneration == externalFeatures.Generation &&
        (!terminal || raw.NoFurtherDelivery) && (!raw.NoEffect || raw.NoFurtherDelivery && raw.Receipt is null) &&
        (raw.Receipt is null ? !terminal || raw.NoEffect || fact.ContainInvoked : raw.Receipt.Lease == fact.Child.External &&
            raw.Receipt.Operation == fact.Native.Operation && raw.Receipt.Sequence == fact.Native.Sequence && raw.Receipt.Kind == ExternalChildEventKind.External &&
            raw.Receipt.ContractVersion == externalFeatures.ContractVersion && raw.Receipt.ManifestGeneration == externalFeatures.Generation);

    public NeutralVirtualizationResult<NeutralVirtualEventReceipt> InjectOriginalVirtualEvent(NeutralOriginalVirtualEventRequest request)
    {
        if (request is null || childRuntime is not IHybridCpuOriginalChildEventRuntimeV1 native)
            return Fail<NeutralVirtualEventReceipt>(NeutralVirtualizationStatus.Unsupported, "Original software event producer unavailable.");
        OriginalEventFact fact;
        lock (sync)
        {
            if (!TryChild(request.Request.ChildLease, out var child, out var failure)) return Fail<NeutralVirtualEventReceipt>(failure.Status, failure.Reason);
            var valid = NeutralVirtualEventContract.ValidateRequest(request.Request, child!.State);
            if (!valid.IsSuccess) return Fail<NeutralVirtualEventReceipt>(valid.Status, valid.Reason);
            if (request.Request.EventClass != NeutralVirtualEventClass.ExternalSignal)
                return Fail<NeutralVirtualEventReceipt>(NeutralVirtualizationStatus.Unsupported, "Timer/preemption require independent executable semantics.");
            if (child.CreateReceipt is null || child.ParentSource?.Admission is null || child.StateRevisionExhausted ||
                pendingChildTransitions.Contains(child.Neutral.Handle) || pendingChildStarts.Contains(child.Neutral.Handle) ||
                child.OriginalEventsIssued == ulong.MaxValue || nextOperation is 0 or ulong.MaxValue || child.OriginalEvents.Any(f => ReferenceEquals(f.Request, request)))
                return Fail<NeutralVirtualEventReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original event source/admission capacity unavailable.");
            child.OriginalEvents.EnsureCapacity(child.OriginalEvents.Count + 1);
            var sequence = child.OriginalEventsIssued + 1;
            fact = new(request, child, new(child.CreateReceipt, request.Request.SourceResourceId, sequence, Op())); fact.Self = fact; fact.InFlight = true;
            child.OriginalEvents.Add(fact); child.OriginalEventsIssued++;
        }
        try
        {
            if (!CurrentEventSource(fact, true)) return Fail<NeutralVirtualEventReceipt>(NeutralVirtualizationStatus.Ambiguous, "Fresh original event admission unavailable.");
            var raw = native.InjectOriginalChildEvent(fact.Native);
            lock (sync) fact.AdmissionRaw = raw; // Retain exact returned raw before query/callback.
            if (raw is null || !ExactEventRaw(fact, raw, false) || !ReferenceEquals(raw, native.ResolveOriginalChildEvent(fact.Native)) || !CurrentEventSource(fact, true))
                return Fail<NeutralVirtualEventReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original event admission lacks exact current observation.");
            if (raw.NoEffect)
            {
                lock (sync) { fact.TerminalRaw = raw; fact.Closure = new(request, true, true); }
                return Fail<NeutralVirtualEventReceipt>(NeutralVirtualizationStatus.Denied, "Original software event was explicitly refused before effect.");
            }
            lock (sync) fact.ConsumeInvoked = true;
            var terminal = native.ConsumeOriginalChildEvent(fact.Native);
            lock (sync) fact.TerminalRaw = terminal;
            if (terminal is null || !ExactEventRaw(fact, terminal, true) || terminal.NoEffect || terminal.Receipt is null ||
                !ReferenceEquals(terminal, native.ResolveOriginalChildEvent(fact.Native)) || !CurrentEventSource(fact, true))
                return Fail<NeutralVirtualEventReceipt>(NeutralVirtualizationStatus.Ambiguous, "Original mailbox consumption lacks exact fresh delivery.");
            lock (sync)
            {
                if (!EventSources(fact) || fact.Child.StateRevision != fact.Revision || fact.Child.StateRevisionExhausted)
                    return Fail<NeutralVirtualEventReceipt>(NeutralVirtualizationStatus.Ambiguous, "Event source changed before publication.");
                fact.Closure = new(request, true, false);
                fact.Receipt = new(fact.Child.Neutral.Handle, fact.Child.Neutral.Epoch, fact.Parent.Neutral.Handle,
                    fact.Parent.Neutral.Epoch, fact.Native.Sequence, request.Request.EventClass, request.Request.SourceResourceId);
                return Ok(fact.Receipt.Value);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return Fail<NeutralVirtualEventReceipt>(NeutralVirtualizationStatus.Ambiguous, $"Original event outcome retained for cleanup: {exception.GetType().Name}."); }
        finally { lock (sync) fact.InFlight = false; }
    }

    public NeutralVirtualizationResult<NeutralOriginalVirtualEventClosure> ContainOriginalVirtualEvent(NeutralOriginalVirtualEventRequest request)
    {
        OriginalEventFact? fact;
        lock (sync)
        {
            fact = originalChildren.Values.SelectMany(c => c.OriginalEvents).SingleOrDefault(f => ReferenceEquals(f.Request, request));
            if (fact is null || !EventSources(fact) || fact.InFlight) return Fail<NeutralOriginalVirtualEventClosure>(NeutralVirtualizationStatus.Ambiguous, "Original event route/history unavailable.");
            if (fact.Closure is { } completed) return Ok(completed);
            fact.InFlight = true;
        }
        try
        {
            if (childRuntime is not IHybridCpuOriginalChildEventRuntimeV1 native || !CurrentEventSource(fact, false))
                return Fail<NeutralOriginalVirtualEventClosure>(NeutralVirtualizationStatus.Ambiguous, "Original event cleanup source unavailable.");
            var raw = fact.TerminalRaw ?? native.ResolveOriginalChildEvent(fact.Native);
            if (raw is not null && raw.NoFurtherDelivery)
            { lock (sync) fact.TerminalRaw ??= raw; }
            else if (!fact.ContainInvoked && fact.TerminalRaw is null)
            {
                // Native admission-based containment can fence allocated intent without an admission reply.
                lock (sync) fact.ContainInvoked = true;
                raw = native.ContainOriginalChildEvent(fact.Native);
                lock (sync) fact.TerminalRaw = raw;
            }
            if (raw is null || !ExactEventRaw(fact, raw, true) || !ReferenceEquals(raw, native.ResolveOriginalChildEvent(fact.Native)) || !CurrentEventSource(fact, false))
                return Fail<NeutralOriginalVirtualEventClosure>(NeutralVirtualizationStatus.Ambiguous, "Unknown/malformed event cleanup remains pinned; effects are not repeated.");
            lock (sync)
            {
                if (!EventSources(fact)) return Fail<NeutralOriginalVirtualEventClosure>(NeutralVirtualizationStatus.Ambiguous, "Event cleanup history changed.");
                fact.Closure = new(request, true, raw.NoEffect); return Ok(fact.Closure);
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return Fail<NeutralOriginalVirtualEventClosure>(NeutralVirtualizationStatus.Ambiguous, exception.GetType().Name); }
        finally { lock (sync) fact.InFlight = false; }
    }
    public NeutralOriginalVirtualEventClosure? ResolveOriginalVirtualEventClosure(NeutralOriginalVirtualEventRequest request)
    {
        lock (sync)
        {
            var fact = originalChildren.Values.SelectMany(c => c.OriginalEvents).SingleOrDefault(f => ReferenceEquals(f.Request, request));
            return fact is not null && EventSources(fact) ? fact.Closure : null;
        }
    }
}
