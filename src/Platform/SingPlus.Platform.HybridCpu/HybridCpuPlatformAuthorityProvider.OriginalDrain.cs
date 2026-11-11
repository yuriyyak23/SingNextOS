using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Platform.HybridCpu;

public sealed partial class HybridCpuPlatformAuthorityProvider : IPlatformOriginalChildDrainProvider
{
    private sealed class OriginalDrainFact
    {
        public OriginalDrainFact(PlatformOriginalChildDrainRequest request, ChildRecord child)
        { OriginalOwner = this; Request = request; Child = child; NativeRequest = new(child.Neutral); }
        public OriginalDrainFact OriginalOwner { get; }
        public PlatformOriginalChildDrainRequest Request { get; }
        public ChildRecord Child { get; }
        public NeutralOriginalChildDrainRequest NativeRequest { get; }
        public NeutralOriginalChildDrainObservation? Raw, OriginalRaw, Reply;
        public ChildTransitionFailure? Failure;
        public PlatformAuthorityResult? Result;
        public PlatformOriginalChildDrainObservation? Observation, OriginalObservation;
        public bool ObservationProduced;
        public bool InFlight, Invoked, CanonicalObserved;
    }
    private readonly Dictionary<PlatformOriginalChildDrainRequest, OriginalDrainFact> _originalChildDrains = new(ReferenceEqualityComparer.Instance);
    public bool SupportsOriginalChildDrain => _childRuntime is INeutralOriginalChildDrainProvider { SupportsOriginalChildDrain: true };
    private bool HasDrainSources(OriginalDrainFact fact) => ReferenceEquals(fact.OriginalOwner, fact) &&
        _originalChildDrains.TryGetValue(fact.Request, out OriginalDrainFact? original) && ReferenceEquals(original, fact) &&
        IsOriginalChildOwner(fact.Child) && fact.Request.Child == fact.Child.Lease &&
        (!fact.Child.OriginalNestedRequired || fact.Child.NestedParent is not null && ProviderNestedIdentity(fact.Child));
    private static bool ValidDrainRaw(OriginalDrainFact fact, NeutralOriginalChildDrainObservation? raw) =>
        raw is not null && ReferenceEquals(raw.OriginalOwner, raw) && ReferenceEquals(raw.Request, fact.NativeRequest);
    public PlatformAuthorityResult BeginOriginalChildDrain(PlatformOriginalChildDrainRequest request)
    {
        bool supported = SupportsOriginalChildDrain; // Capability observation outside commit lock.
        OriginalDrainFact fact;
        lock (_childCreateCommitGate)
        {
            if (request is null || !supported || _originalChildDrains.ContainsKey(request) ||
                !_providerChildren.TryGetValue(request.Child.LeaseId, out ChildRecord? child) || child.Lease != request.Child || !IsOriginalChildOwner(child))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original drain owner unavailable; no repeat.");
            fact = new(request, child) { InFlight = true };
            bool preceding = child.TransitionInFlight || child.CloseInFlight || _originalChildDrains.Values.Any(f => ReferenceEquals(f.Child, child) &&
                (f.InFlight || f.Invoked && f.Result is not { Status: PlatformAuthorityStatus.NotAccepted }));
            _originalChildDrains.Add(request, fact); // Platform and neutral original intent before callbacks/effect.
            if (preceding)
            {
                fact.InFlight = false;
                return (fact.Result = PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted,
                    "Original drain is already invoked or pending; this attempt never invoked.")).Value;
            }
            child.TransitionInFlight = true; // Same owner gate spans prepared intent through raw handoff.
        }
        try
        {
            PlatformAuthorityResult result = TransitionChildDomainCore(request.Child, PlatformChildDomainTransition.BeginDrain, fact);
            lock (_childCreateCommitGate) fact.Result = result;
            return result;
        }
        finally
        { lock (_childCreateCommitGate) { fact.Failure = fact.Child.FailedTransition; fact.InFlight = false; fact.Child.TransitionInFlight = false; } }
    }
    public PlatformAuthorityResult ResolveOriginalChildDrain(PlatformOriginalChildDrainRequest originalRequest)
    {
        OriginalDrainFact fact;
        lock (_childCreateCommitGate)
        {
            if (originalRequest is null || !_originalChildDrains.TryGetValue(originalRequest, out fact!) ||
                !HasDrainSources(fact) || fact.InFlight || fact.Child.TransitionInFlight || fact.Child.CloseInFlight ||
                fact.CanonicalObserved && (fact.OriginalRaw is null || !ReferenceEquals(fact.Raw, fact.OriginalRaw)))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original drain custody/source unavailable.");
            if (!fact.Invoked)
            {
                if (fact.Result is { IsSuccess: true } closed && HasPublishedChildClosure(fact.Child)) return closed;
                return (fact.Result = PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted, "Exact original drain never invoked.")).Value;
            }
            fact.InFlight = true; fact.Child.TransitionInFlight = true;
        }
        try
        {
            PlatformProviderIncarnation before = CurrentIncarnation;
            NeutralOriginalChildDrainObservation? raw = ((INeutralOriginalChildDrainProvider)_childRuntime!).ResolveOriginalChildDrain(fact.NativeRequest);
            lock (_childCreateCommitGate)
                if (ValidDrainRaw(fact, raw) && !fact.CanonicalObserved)
                { fact.Raw = fact.OriginalRaw = raw; fact.CanonicalObserved = true; }
            PlatformProviderIncarnation after = CurrentIncarnation;
            NeutralOriginalChildDrainObservation? fresh = ((INeutralOriginalChildDrainProvider)_childRuntime!).ResolveOriginalChildDrain(fact.NativeRequest);
            lock (_childCreateCommitGate)
            {
                if (!HasDrainSources(fact) || before.Value == 0 || after != before || !ValidDrainRaw(fact, raw) || !ReferenceEquals(raw, fresh) ||
                    fact.OriginalRaw is not null && !ReferenceEquals(fact.OriginalRaw, raw) ||
                    !ReferenceEquals(fact.Child.FailedTransition, fact.Failure))
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Original drain outcome remains unknown/foreign; no effect repeated.");
                fact.Raw = fact.OriginalRaw ??= raw;
                if (!raw!.Result.IsSuccess && raw.Result.Status != NeutralVirtualizationStatus.NotInvoked)
                    return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Retained original drain does not prove success or no-effect.");
                fact.Child.FailedTransition = null; fact.Failure = null;
                return (fact.Result = raw.Result.IsSuccess ? PlatformAuthorityResult.Ok() :
                    PlatformAuthorityResult.Fail(PlatformAuthorityStatus.NotAccepted, "Canonical original drain not invoked.")).Value;
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException)
        { return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, $"Original drain query unavailable: {exception.GetType().Name}"); }
        finally { lock (_childCreateCommitGate) { fact.InFlight = false; fact.Child.TransitionInFlight = false; } }
    }
    public PlatformOriginalChildDrainObservation? ObserveOriginalChildDrain(PlatformOriginalChildDrainRequest originalRequest)
    {
        PlatformAuthorityResult result = ResolveOriginalChildDrain(originalRequest);
        lock (_childCreateCommitGate)
        {
            if (!result.IsSuccess && result.Status != PlatformAuthorityStatus.NotAccepted ||
                !_originalChildDrains.TryGetValue(originalRequest, out OriginalDrainFact? fact) || !HasDrainSources(fact) ||
                fact.Result != result || fact.InFlight || fact.Child.TransitionInFlight || fact.Child.CloseInFlight)
                return null;
            if (fact.ObservationProduced)
            {
                if (fact.Observation is not { } retained || !ReferenceEquals(retained, fact.OriginalObservation)) return null;
                return ReferenceEquals(retained.OriginalOwner, retained) && ReferenceEquals(retained.Request, originalRequest) && retained.Result == result
                    ? retained : null;
            }
            fact.ObservationProduced = true;
            return fact.Observation = fact.OriginalObservation = new(originalRequest, result);
        }
    }
}
