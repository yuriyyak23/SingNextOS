using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private static bool HasConsumedOriginalCopyLifetime(OriginalCopyInvocation fact) => fact.LifetimeConsumed && fact.Invoked &&
        ReferenceEquals(fact.OwnerSelf,fact) && fact.LifetimeRaw is { IsSuccess: true } raw &&
        ReferenceEquals(raw.Value.Request,fact.Request) && raw.Value.NoFurtherByteUseGuaranteed;
    internal KernelResult<PlatformDmaGrant[]> OriginalCopyRetirementGrants(PlatformDmaGrant grant,PlatformDomainIdentity subject)
    {
        var resolved = ResolveOriginalCopyLifetimeForCleanup(grant,subject);
        if (!resolved.IsSuccess) return KernelResult<PlatformDmaGrant[]>.Fail(resolved.Error,resolved.Message!);
        lock (_dmaCompletionGate)
        {
            if (!_dmaGrants.TryGetValue(grant.GrantId,out var owner) || !IsOriginalDmaGrant(owner) || owner.Grant != grant || grant.DeviceLease.DomainBinding.Subject != subject)
                return KernelResult<PlatformDmaGrant[]>.Fail(KernelError.PlatformFaulted,"Original retirement owner changed.");
            if (owner.OriginalCopy is not { LifetimeConsumed: true } fact) return KernelResult<PlatformDmaGrant[]>.Ok([grant]);
            if (!HasConsumedOriginalCopyLifetime(fact) || fact.Uncertain || fact.CleanupInFlight || !Leg(fact.Source) || !Leg(fact.Destination))
                return KernelResult<PlatformDmaGrant[]>.Fail(KernelError.PlatformFaulted,"Original consumed pair inventory changed; peer retirement denied.");
            var peer = ReferenceEquals(owner,fact.Source) ? fact.Destination : fact.Source;
            return KernelResult<PlatformDmaGrant[]>.Ok([grant,peer.Grant]); // Historical cleanup correlation, never new peer permission/publication.
            bool Leg(DmaGrantRecord record) => IsOriginalDmaGrant(record) && ReferenceEquals(record.OriginalCopy,fact) &&
                (ulong)record.CopyHistory.Count == record.CopiesIssued && record.CopyHistory.Count(copy => ReferenceEquals(copy,fact)) == 1 &&
                !record.PreparationInFlight && !record.SubmissionInFlight && !record.AcquisitionInFlight && !record.ClosureInFlight &&
                !HasFaultPinnedDmaSubmission(record.Grant.GrantId) && !HasActiveDmaSubmission(record.Grant.GrantId);
        }
    }

    private KernelResult ResolveOriginalCopyLifetimeForCleanup(PlatformDmaGrant grant,PlatformDomainIdentity subject)
    {
        KernelResult Fail() => KernelResult.Fail(KernelError.PlatformFaulted,"Original copy byte lifetime unresolved; pins retained, no repeat.");
        OriginalCopyInvocation fact;
        DmaTraceObserver? retiredSourceTrace = null,retiredDestinationTrace = null;
        lock (_dmaCompletionGate)
        {
            var identity = ValidateDmaGrantIdentity(grant,subject);
            if (!identity.IsSuccess) return identity;
            var owner = _dmaGrants[grant.GrantId];
            if ((ulong)owner.CopyHistory.Count != owner.CopiesIssued || owner.CopyHistory.Any(copy => !ReferenceEquals(copy.OwnerSelf,copy))) return Fail();
            if (owner.OriginalCopy is not { } original) return owner.CopiesIssued == 0 ? KernelResult.Ok() : Fail();
            fact = original;
            if (fact.LifetimeConsumed) return HasConsumedOriginalCopyLifetime(fact) ? KernelResult.Ok() : Fail();
            if (!fact.Uncertain && !HasCompletedRecords()) return KernelResult.Ok();
            if (fact.CleanupInFlight || _provider is not IPlatformOriginalDmaCopyLifetimeProvider || !Exact()) return Fail();
            fact.CleanupInFlight = true;
        }
        try
        {
            var source = CurrentProviderIncarnation();
            lock (_dmaCompletionGate) if (!Current(source)) return Fail();
            var raw = ((IPlatformOriginalDmaCopyLifetimeProvider)_provider).ResolveDmaCopyLifetime(fact.Request);
            lock (_dmaCompletionGate) fact.LifetimeRaw = raw;
            var fresh = CurrentProviderIncarnation();
            lock (_dmaCompletionGate)
            {
                if (!Current(fresh) || fact.LifetimeRaw is not { } retained || retained != raw ||
                    !raw.IsSuccess || !ReferenceEquals(raw.Value.Request,fact.Request) || !raw.Value.NoFurtherByteUseGuaranteed) return Fail();
                Retire(fact.Source,fact.AcceptedSource); Retire(fact.Destination,fact.AcceptedDestination);
                fact.LifetimeConsumed = true;
                fact.Uncertain = false; // Consume only this invocation's pin, never the independent fault set.
                retiredSourceTrace = fact.AcceptedSource?.Trace; retiredDestinationTrace = fact.AcceptedDestination?.Trace;
                return KernelResult.Ok();
            }
        }
        catch (Exception exception) when (exception is not StackOverflowException) { return Fail(); }
        finally
        {
            lock (_dmaCompletionGate) fact.CleanupInFlight = false;
            FlushDmaTrace(retiredSourceTrace); FlushDmaTrace(retiredDestinationTrace); // External observers outside owner locks, after authoritative consumption.
        }

        bool Exact() => ReferenceEquals(fact.OwnerSelf,fact) && fact.Invoked &&
            (!HasCompletedRecords() || fact.AcceptedSource is { } a && fact.AcceptedDestination is { } b && Completed(fact.Source,a) && Completed(fact.Destination,b)) &&
            Leg(fact.Source,fact.SourceRevision,fact.SourceCycle) && Leg(fact.Destination,fact.DestinationRevision,fact.DestinationCycle);
        bool Leg(DmaGrantRecord owner,ulong revision,DmaVisibilityState cycle) => IsOriginalDmaGrant(owner) &&
            ReferenceEquals(owner.OriginalCopy,fact) && (ulong)owner.CopyHistory.Count == owner.CopiesIssued && owner.CopyHistory.Count(copy => ReferenceEquals(copy,fact)) == 1 &&
            !owner.SourceExhausted && OriginalRevision(owner,revision) &&
            !owner.SubmissionInFlight && !owner.PreparationInFlight && !owner.AcquisitionInFlight && !owner.ClosureInFlight &&
            !_dmaSubmissionFaultPins.Contains(owner.Grant.GrantId) && Accepted(owner) &&
            (owner.PlatformClosed && HasConsumedOriginalDmaClosure(owner) ||
                _dmaVisibilityStates.TryGetValue(owner.Grant.GrantId,out var current) && ReferenceEquals(current,cycle)) &&
            _deviceLeases.TryGetValue(owner.Grant.DeviceLease.LeaseId,out var device) && ReferenceEquals(device,owner.OriginalDeviceSource) &&
            _mappings.TryGetValue(owner.Grant.Mapping.Mapping.MappingId,out var mapping) && ReferenceEquals(mapping,owner.OriginalMappingSource);
        bool Current(PlatformProviderIncarnation source) => fact.CleanupInFlight && (fact.Uncertain || HasCompletedRecords()) && Exact() && !_backendEpochExhausted && BackendEpoch == fact.Epoch && source.Value != 0 &&
            source == fact.Source.ProviderIncarnation && source == fact.Destination.ProviderIncarnation;
        bool Accepted(DmaGrantRecord owner)
        {
            var expected = ReferenceEquals(owner,fact.Source) ? fact.AcceptedSource : fact.AcceptedDestination;
            if (expected is null) return owner.CompletedCopySubmission is null && !HasActiveDmaSubmission(owner.Grant.GrantId);
            return ReferenceEquals(expected.OwnerSelf,expected) &&
                (_activeDmaSubmissions.TryGetValue(owner.Grant.GrantId,out var current) ? ReferenceEquals(current,expected) : Completed(owner,expected)) &&
                !expected.CompletionObservationInFlight && expected.PageFaultsInFlight.Count == 0 && expected.ResolvedPageFaults.Count == 0 && expected.KnownPageFaultEvidence.Count == 0 &&
                (!owner.OwnVisibilityUncertain || CanContainPendingOriginalDmaVisibility(owner));
        }
        bool HasCompletedRecords() => fact.Source.CompletedCopySubmission is not null || fact.Destination.CompletedCopySubmission is not null;
        bool Completed(DmaGrantRecord owner,DmaSubmissionRecord expected) => ReferenceEquals(owner.CompletedCopySubmission,expected) &&
            expected.CompletionProven && expected.CompletionEvidence is { IsSatisfied: true } &&
            expected.KnownCompletionEvidence is { State: PlatformProviderDmaCompletionState.Completed } && expected.KnownCompletionEpoch == fact.Epoch &&
            expected.CopyVisibilityEvidence is { IsSatisfied: true } &&
            owner.CopyPeerGrantId == (ReferenceEquals(owner,fact.Source) ? fact.Destination : fact.Source).Grant.GrantId;
        bool OriginalRevision(DmaGrantRecord owner,ulong revision) => !owner.PlatformClosed ? owner.SourceRevision == revision :
            HasConsumedOriginalDmaClosure(owner) && owner.OriginalClosure is { Consumed: true } closed &&
            closed.Revision == revision && closed.Epoch == fact.Epoch && revision != ulong.MaxValue && owner.SourceRevision == revision + 1;
        void Retire(DmaGrantRecord owner,DmaSubmissionRecord? expected)
        {
            if (expected is null) return;
            if (!_activeDmaSubmissions.Remove(owner.Grant.GrantId)) return; // Completed records retain their already published evidence.
            try { if (expected.Trace is { QuarantineQueued: false }) QueueDmaTraceLocked(expected,SemanticTraceEventKindV1.Quarantined); }
            catch (Exception exception) when (exception is not StackOverflowException) { } // Diagnostic failure cannot strand an already proven byte lifetime.
        }
    }

    private void PinOriginalCopyCompletionLookupLocked(DmaGrantRecord grant,DmaSubmissionRecord record)
    {
        if (_provider is IPlatformOriginalDmaCopyCompletionProvider && record.OriginalCompletionLookupInvoked &&
            grant.OriginalCopy is { Invoked: true, LifetimeConsumed: false } fact && ReferenceEquals(fact.OwnerSelf,fact) &&
            (ReferenceEquals(fact.AcceptedSource,record) || ReferenceEquals(fact.AcceptedDestination,record)) &&
            ReferenceEquals(record.OwnerSelf,record) && IsOriginalDmaGrant(grant) && BackendEpoch == fact.Epoch)
            fact.Uncertain = true;
        else FaultPinDmaSubmissionLocked(grant.Grant.GrantId);
    }
}
