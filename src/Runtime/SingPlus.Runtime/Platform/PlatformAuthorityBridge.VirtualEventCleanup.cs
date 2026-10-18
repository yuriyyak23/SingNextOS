using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private static void MarkOriginalEventFault(ChildBindingRecord record,
        ChildDeliveryInvocation<PlatformVirtualEventRequest, PlatformVirtualEventReceipt> invocation)
    {
        bool exact = !record.LifecycleRevisionExhausted && record.LifecycleRevision == invocation.AdmittedChildRevision && record.State == invocation.AdmittedChildState;
        record.State = PlatformChildDomainState.Faulted;
        if (exact && !record.LifecycleRevisionExhausted) invocation.OwnFaultRevision = record.LifecycleRevision;
    }
    private KernelResult ContainOriginalChildEventsForDrain(PlatformChildBinding binding)
    {
        ChildBindingRecord record;
        lock (_secureDomainLifecycleGate)
        {
            if (!_originalChildOwners.TryGetValue(binding.BindingId, out record!) || record.Binding != binding)
                return KernelResult.Ok(); // Existing drain owner resolution decides absence; no event fact is inferred.
            if (record.EventInvocations.Count != 0 &&
                (!_childBindings.TryGetValue(binding.BindingId, out var route) || !ReferenceEquals(route, record)))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original child delivery cleanup route unavailable.");
        }
        return ContainOriginalChildEvents(record);
    }
    private KernelResult ContainOriginalChildEvents(ChildBindingRecord record)
    {
        ChildDeliveryInvocation<PlatformVirtualEventRequest, PlatformVirtualEventReceipt>[] pending;
        lock (_secureDomainLifecycleGate)
        {
            if (record.EventsConsumed > record.EventsIssued || record.EventsIssued - record.EventsConsumed != (ulong)record.EventInvocations.Count ||
                record.EventInvocations.Any(x => !ReferenceEquals(x.OwnerSelf, x)))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original delivery history lost; child remains pinned.");
            if (record.EventInvocations.Count == 0) return KernelResult.Ok();
            if (record.PendingChildEffects != 0 || record.CloseInFlight || record.TransitionInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Original delivery is still in flight.");
            // Preserve the established V1 known-delivery teardown. Its retained observation is not
            // reinterpreted as optional containment; unknown V1 delivery still cannot close.
            if (record.EventInvocations.Any(x => x.EventOriginal is null &&
                (x.Observation is not { IsSuccess: true } known || !PlatformVirtualEventContract.ValidateReceipt(x.Request, known.Value).IsSuccess)))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Legacy delivery outcome remains unknown/malformed.");
            pending = record.EventInvocations.Where(x => x.EventOriginal is not null).ToArray();
            if (pending.Length == 0) return KernelResult.Ok();
            if (record.PendingChildEffects != 0 || record.CloseInFlight || record.TransitionInFlight ||
                !_originalChildOwners.TryGetValue(record.Binding.BindingId, out var original) || !ReferenceEquals(original, record) || !HasCurrentChildCreateSources(record))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Original delivery cleanup owner unavailable or in flight.");
            record.PendingChildEffects++;
        }
        try
        {
            if (_provider is not IPlatformOriginalVirtualEventProvider provider)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Provider cannot contain original delivery; uncertainty remains pinned.");
            foreach (var invocation in pending)
            {
                if (invocation.EventOriginal is not { } request)
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Legacy delivery has no original containment contract.");
                var raw = invocation.EventCleanupRaw ?? provider.ResolveOriginalVirtualEventClosure(request);
                if (raw is null)
                {
                    var containment = provider.ContainOriginalVirtualEvent(request);
                    raw = containment.IsSuccess ? containment.Value : null;
                }
                lock (_secureDomainLifecycleGate) invocation.EventCleanupRaw ??= raw; // Original raw before further callbacks.
                if (raw is null || !raw.NoFurtherDelivery || !ReferenceEquals(raw.Admission, request) ||
                    !ReferenceEquals(raw, provider.ResolveOriginalVirtualEventClosure(request)))
                    return KernelResult.Fail(KernelError.PlatformFaulted, "Original delivery closure is unknown/malformed; no repeat.");
                var source = CurrentProviderIncarnation();
                lock (_secureDomainLifecycleGate)
                {
                    var root = invocation.OriginalRoot;
                    if (!_originalChildOwners.TryGetValue(record.Binding.BindingId, out var original) || !ReferenceEquals(original, record) ||
                        !_childBindings.TryGetValue(record.Binding.BindingId, out var route) || !ReferenceEquals(route, record) ||
                        !HasCurrentChildCreateSources(record) || _backendEpochExhausted || BackendEpoch != invocation.AdmissionEpoch ||
                        root.BackendEpoch != invocation.AdmissionEpoch || source.Value == 0 || source != root.ProviderIncarnation ||
                        !TryGetCanonicalRootRecord(record.Binding.ParentBinding.BindingId, out var currentRoot) || !ReferenceEquals(currentRoot, root) ||
                        request.Request.ChildLease != record.ProviderLease || request.Request != invocation.Request ||
                        record.EventsConsumed >= record.EventsIssued || record.EventsIssued - record.EventsConsumed != (ulong)record.EventInvocations.Count ||
                        !record.EventInvocations.Remove(invocation))
                        return KernelResult.Fail(KernelError.PlatformFaulted, "Original delivery source changed before exact consumption.");
                    record.EventsConsumed++;
                    if (record.State == PlatformChildDomainState.Faulted && invocation.OwnFaultRevision is { } faultRevision &&
                        !record.LifecycleRevisionExhausted && record.LifecycleRevision == faultRevision)
                    {
                        // Continue only the local state quarantined by this exact invocation.
                        // This never restores native lifecycle or effect permission; BeginDrain/Close still revalidate their own owners.
                        record.EventCleanupOnly = true;
                        record.State = invocation.AdmittedChildState;
                    }
                    // Only this bridge invocation pin is consumed. KernelEventRegistry owns local endpoint/signal users.
                }
            }
            return KernelResult.Ok();
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return KernelResult.Fail(KernelError.PlatformFaulted, $"Original delivery cleanup retained: {exception.GetType().Name}."); }
        finally { lock (_secureDomainLifecycleGate) record.PendingChildEffects--; }
    }
}
