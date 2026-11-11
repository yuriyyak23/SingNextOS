using SingPlus.Platform;
namespace SingPlus.Runtime;
public sealed partial class PlatformAuthorityBridge
{
    private sealed class OriginalGuestUnmapInvocation
    {
        public OriginalGuestUnmapInvocation(GuestBindingRecord guest, ChildBindingRecord child, MappingRecord parent, bool supports, PlatformExternalClosureState originalState)
        { OriginalOwner = this; Guest = guest; Child = child; Parent = parent; Supports = supports; OriginalState = originalState; ChildRevision = child.LifecycleRevision; ParentRevision = parent.LifecycleRevision; }
        public PlatformExternalClosureState OriginalState { get; }
        public OriginalGuestUnmapInvocation OriginalOwner { get; }
        public GuestBindingRecord Guest { get; }
        public ChildBindingRecord Child { get; }
        public MappingRecord Parent { get; }
        public bool Supports { get; }
        public long ChildRevision { get; }
        public long ParentRevision { get; }
        public long Revision, FaultRevision;
        public bool Invoked, OwnFault;
        public PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>? Raw;
        public PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>? Resolution;
    }
    private bool HasOwnGuestUnmapFault(GuestBindingRecord guest) =>
        guest.OriginalUnmap is { Supports: true, OwnFault: true } fact && ReferenceEquals(fact.OriginalOwner, fact) &&
        ReferenceEquals(fact.Guest, guest) && !guest.LifecycleRevisionExhausted &&
        guest.Closure == PlatformExternalClosureState.Faulted && guest.LifecycleRevision == fact.FaultRevision &&
        !fact.Child.LifecycleRevisionExhausted && fact.Child.LifecycleRevision == fact.ChildRevision &&
        !fact.Parent.LifecycleRevisionExhausted && fact.Parent.LifecycleRevision == fact.ParentRevision;
    private void PinOriginalGuestUnmap(GuestBindingRecord guest)
    {
        if (guest.OriginalUnmap is { Invoked: true } fact && ReferenceEquals(fact.OriginalOwner, fact) &&
            !guest.LifecycleRevisionExhausted && guest.Closure == PlatformExternalClosureState.Draining && guest.LifecycleRevision == fact.Revision)
        { guest.Closure = PlatformExternalClosureState.Faulted; fact.OwnFault = true; fact.FaultRevision = guest.LifecycleRevision; }
    }
}
