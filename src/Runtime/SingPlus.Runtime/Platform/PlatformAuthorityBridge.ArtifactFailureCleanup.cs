using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    // Correlation on the existing artifact owner, not another lifecycle authority.
    private class ExecutableDependencyFailureFact
    {
        public ExecutableDependencyFailureFact(ExecutableArtifactSource source)
        {
            OriginalOwner = this;
            Source = source;
            ChildBefore = source.Child.State; GuestBefore = source.Guest.Closure; ParentBefore = source.Parent.ClosureState;
            ChildRevision = source.Child.LifecycleRevision; GuestRevision = source.Guest.LifecycleRevision; ParentRevision = source.Parent.LifecycleRevision;
        }
        public ExecutableDependencyFailureFact OriginalOwner { get; }
        public ExecutableArtifactSource Source { get; }
        public PlatformChildDomainState ChildBefore { get; }
        public PlatformExternalClosureState GuestBefore { get; }
        public PlatformExternalClosureState ParentBefore { get; }
        public long ChildRevision { get; set; }
        public long GuestRevision { get; set; }
        public long ParentRevision { get; set; }
        public bool Invoked { get; set; }
        public bool NoEffectConsumed { get; set; }
        public bool OwnFailurePinned { get; set; }
        public bool DependencyFailureConsumed { get; set; }
    }

    private sealed class ExecutableStartInvocationFact(PlatformChildExecutionStartRequest request, ExecutableArtifactSource source)
        : ExecutableDependencyFailureFact(source)
    {
        public Dictionary<ChildBindingRecord, long> Ancestors { get; } = [];
        public PlatformChildExecutionStartRequest Request { get; } = request;
        public long? PublishedChildRevision { get; set; }
        public PlatformAuthorityResult<PlatformChildExecutionReceipt>? Observation { get; set; }
    }

    private sealed class ExecutableBindInvocationFact(PlatformExecutableArtifactRequest request, ExecutableArtifactSource source)
        : ExecutableDependencyFailureFact(source)
    {
        public PlatformExecutableArtifactRequest Request { get; } = request;
        public PlatformAuthorityResult<PlatformExecutableArtifactReceipt>? Observation { get; set; }
        public PlatformAuthorityResult<PlatformExecutableArtifactReceipt>? ResolutionObservation { get; set; }
        public Dictionary<ChildBindingRecord, long> Ancestors { get; } = [];
    }

    private static void AdvanceExecutableSourceRevision(ref long revision, ref int exhausted)
    {
        if (Interlocked.Increment(ref revision) <= 0) Volatile.Write(ref exhausted, 1);
    }

    private static bool SameExecutableSourceRevision(ExecutableDependencyFailureFact fact) =>
        ReferenceEquals(fact.OriginalOwner, fact) &&
        !fact.Source.Child.LifecycleRevisionExhausted && !fact.Source.Guest.LifecycleRevisionExhausted &&
        !fact.Source.Parent.LifecycleRevisionExhausted && fact.ChildRevision == fact.Source.Child.LifecycleRevision &&
        fact.GuestRevision == fact.Source.Guest.LifecycleRevision && fact.ParentRevision == fact.Source.Parent.LifecycleRevision;

    // Caller holds the existing bridge lifecycle gate. No callback or permission lookup.
    private bool HasOwnExecutableStartFailure(ExecutableArtifactRecord artifact) =>
        (artifact.StartInvocation as ExecutableDependencyFailureFact ?? artifact.BindInvocation) is { Invoked: true, OwnFailurePinned: true, DependencyFailureConsumed: false } fact &&
        ReferenceEquals(artifact.AdmissionSource, fact.Source) && SameExecutableSourceRevision(fact) &&
        (!fact.Source.Parent.LocalAuthorizationRevoked || fact.Source.Parent.OriginalPermissionRetiredForCleanup) && !fact.Source.Parent.LocalReservationReleased &&
        fact.Source.Child.State == PlatformChildDomainState.Faulted &&
        fact.Source.Guest.Closure == PlatformExternalClosureState.Faulted &&
        fact.Source.Parent.ClosureState == PlatformExternalClosureState.Faulted;

    private void ConsumeExecutableStartFailureAfterRelease(ExecutableArtifactRecord artifact)
    {
        if (!artifact.Released || !HasOwnExecutableStartFailure(artifact)) return;
        var fact = artifact.StartInvocation as ExecutableDependencyFailureFact ?? artifact.BindInvocation!;
        var source = fact.Source;
        if (source.Parent.LocalAuthorizationRevoked && !source.Parent.OriginalPermissionRetiredForCleanup || source.Parent.LocalReservationReleased ||
            source.Child.TransitionInFlight || source.Child.CloseInFlight || source.Guest.ClosureInFlight ||
            source.Guest.PendingSecureRegionEffects != 0) return;
        // Exact no-further-use discharges only the quarantine written by this Start.
        // Other invocation inventories, Region damage, capabilities and charges stay owned.
        source.Child.State = fact.ChildBefore;
        source.Guest.Closure = fact.GuestBefore;
        source.Parent.ClosureState = fact.ParentBefore;
        fact.DependencyFailureConsumed = true;
    }
}

