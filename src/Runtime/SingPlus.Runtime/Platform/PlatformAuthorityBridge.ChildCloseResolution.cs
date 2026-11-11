using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private sealed class OriginalChildCloseInvocation
    {
        public OriginalChildCloseInvocation(ChildBindingRecord child, DomainRecord root, IReadOnlyList<ChildBindingRecord> ancestors, PlatformBackendEpoch epoch, bool supportsResolution)
        {
            OriginalOwner = this; Child = child; Root = root; Ancestors = ancestors.ToArray(); Epoch = epoch;
            Lease = child.ProviderLease; Incarnation = root.ProviderIncarnation; Revision = child.LifecycleRevision;
            SupportsResolution = supportsResolution;
        }
        public OriginalChildCloseInvocation OriginalOwner { get; }
        public ChildBindingRecord Child { get; }
        public DomainRecord Root { get; }
        public ChildBindingRecord[] Ancestors { get; }
        public PlatformBackendEpoch Epoch { get; }
        public PlatformProviderChildDomainLease Lease { get; }
        public PlatformProviderIncarnation Incarnation { get; }
        public long Revision { get; }
        public bool SupportsResolution { get; }
        public bool Invoked, OwnFaultPinned, NoEffectConsumed;
        public long FaultRevision;
        public PlatformAuthorityResult<PlatformChildDomainClosureReceipt>? Observation, ResolutionObservation;
    }

    private static bool HasOriginalChildCloseInvocation(ChildBindingRecord child, OriginalChildCloseInvocation invocation) =>
        ReferenceEquals(invocation.OriginalOwner, invocation) && ReferenceEquals(invocation.Child, child) &&
        ReferenceEquals(child.OriginalCloseInvocation, invocation) && ReferenceEquals(child.CreateRoot, invocation.Root) &&
        child.ProviderLease == invocation.Lease && invocation.Root.BackendEpoch == invocation.Epoch &&
        invocation.Root.ProviderIncarnation == invocation.Incarnation && !child.LifecycleRevisionExhausted;

    private static bool HasOwnChildCloseFailure(ChildBindingRecord child) =>
        child.OriginalCloseInvocation is { Invoked: true, OwnFaultPinned: true, NoEffectConsumed: false, SupportsResolution: true } invocation &&
        HasOriginalChildCloseInvocation(child, invocation) && child.State == PlatformChildDomainState.Faulted &&
        child.LifecycleRevision == invocation.FaultRevision;

    private static bool HasChildCloseStateContinuity(ChildBindingRecord child)
    {
        if (child.OriginalCloseInvocation is not { Invoked: true, NoEffectConsumed: false } invocation)
            return child.State == PlatformChildDomainState.Draining;
        if (!invocation.SupportsResolution && child.State == PlatformChildDomainState.Draining && HasValidatedChildClosureFact(child))
            return true; // Existing explicit BeginDrain consumed fresh sources for a known legacy receipt.
        return HasOriginalChildCloseInvocation(child, invocation) &&
            (child.State == PlatformChildDomainState.Draining && child.LifecycleRevision == invocation.Revision || HasOwnChildCloseFailure(child));
    }

    private static void PinOwnChildCloseFailure(ChildBindingRecord child)
    {
        if (child.OriginalCloseInvocation is { } invocation && HasOriginalChildCloseInvocation(child, invocation) &&
            child.State == PlatformChildDomainState.Draining && child.LifecycleRevision == invocation.Revision)
        {
            child.State = PlatformChildDomainState.Faulted;
            invocation.OwnFaultPinned = true; invocation.FaultRevision = child.LifecycleRevision;
        }
    }
}
