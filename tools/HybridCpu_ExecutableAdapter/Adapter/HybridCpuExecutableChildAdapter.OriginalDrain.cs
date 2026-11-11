using YAKSys_Hybrid_CPU.Core;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

public sealed partial class HybridCpuExecutableChildAdapter : INeutralOriginalChildDrainProvider
{
    private sealed class OriginalDrain
    {
        public OriginalDrain(NeutralOriginalChildDrainRequest request, Child child)
        { Request = request; Child = child; OriginalOwner = this; }
        public OriginalDrain OriginalOwner { get; }
        public NeutralOriginalChildDrainRequest Request { get; }
        public Child Child { get; }
        public NeutralOriginalChildDrainObservation? Raw;
        public NeutralOriginalChildDrainObservation? OriginalRaw;
        public long Revision;
    }
    private readonly Dictionary<NeutralOriginalChildDrainRequest, OriginalDrain> originalDrains = new(ReferenceEqualityComparer.Instance);
    public bool SupportsOriginalChildDrain => true;
    public NeutralOriginalChildDrainObservation? BeginOriginalChildDrain(NeutralOriginalChildDrainRequest request)
    {
        lock (sync)
        {
            if (request is null || originalDrains.ContainsKey(request) ||
                !originalChildren.TryGetValue(request.Child.Handle, out Child? child) || child.Neutral != request.Child ||
                !children.TryGetValue(request.Child.Handle, out Child? route) || !ReferenceEquals(route, child)) return null;
            bool preceding = originalDrains.Values.Any(f => ReferenceEquals(f.Child, child) &&
                (f.OriginalRaw is null || f.OriginalRaw.Result.Status != NeutralVirtualizationStatus.NotInvoked));
            OriginalDrain fact = new(request, child);
            originalDrains.Add(request, fact); // Original intent before local lifetime effect.
            // BeginDrain is entirely local under sync; unlike Park/Resume it calls no external provider.
            NeutralVirtualizationResult result = preceding || child.StateRevisionExhausted || child.StateRevision == long.MaxValue ||
                !TryChild(request.Child, out _, out _, dependencyRefusalIsPreEffect: true)
                ? new(NeutralVirtualizationStatus.NotInvoked, "Original drain dependency refused before local lifetime effect.")
                : TransitionChildDomainCore(request.Child, NeutralChildDomainTransition.BeginDrain, fact);
            fact.Revision = child!.StateRevision;
            fact.Raw = fact.OriginalRaw = new(request, result);
            return fact.Raw;
        }
    }
    public NeutralOriginalChildDrainObservation? ResolveOriginalChildDrain(NeutralOriginalChildDrainRequest originalRequest)
    {
        lock (sync)
        {
            if (originalRequest is null || !originalDrains.TryGetValue(originalRequest, out OriginalDrain? fact) ||
                !ReferenceEquals(fact.OriginalOwner, fact) || !ReferenceEquals(fact.Request, originalRequest) ||
                fact.Raw is not { } raw || !ReferenceEquals(raw, fact.OriginalRaw) || !ReferenceEquals(raw.OriginalOwner, raw) ||
                !ReferenceEquals(raw.Request, originalRequest) ||
                !originalChildren.TryGetValue(originalRequest.Child.Handle, out Child? original) || !ReferenceEquals(original, fact.Child) ||
                !children.TryGetValue(originalRequest.Child.Handle, out Child? route) || !ReferenceEquals(route, original) ||
                !TryChild(originalRequest.Child, out Child? current, out _, allowCleanup: true) || !ReferenceEquals(current, original) ||
                !NestedIdentity(original) ||
                raw.Result.IsSuccess && original.StateRevisionExhausted ||
                (original.StateRevision != fact.Revision && !(original.State == NeutralChildDomainState.Closed && HasRetainedOriginalChildClosure(original))))
                return null;
            return raw; // Pure same-original lookup, never TransitionChildDomain.
        }
    }
}
