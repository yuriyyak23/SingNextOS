using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class PlatformAuthorityBridge
{
    private sealed class DmaVisibilityOwnerInvocation
    {
        public DmaVisibilityOwnerInvocation(DmaGrantRecord source, bool acquire, PlatformBackendEpoch epoch, bool canContain)
        { OwnerSelf = this; Source = source; Grant = source.ProviderGrant; Acquire = acquire; Epoch = epoch; Revision = source.SourceRevision; CanContain = canContain; }
        public DmaVisibilityOwnerInvocation OwnerSelf { get; }
        public DmaGrantRecord Source { get; }
        public PlatformProviderDmaGrant Grant { get; }
        public bool Acquire { get; }
        public PlatformBackendEpoch Epoch { get; }
        public ulong Revision { get; }
        public bool CanContain { get; }
        public bool Invoked { get; set; }
        public bool Uncertain { get; set; }
        public PlatformAuthorityResult<PlatformProviderDmaPrepareEvidence>? RawPrepare { get; set; }
        public PlatformAuthorityResult<PlatformProviderDmaAcquireEvidence>? RawAcquire { get; set; }
        public DmaGrantClosureInvocation? ClosedByOriginalGrant { get; set; }
    }

    private static bool HasPendingOriginalDmaVisibility(DmaGrantRecord record) => record.VisibilityHistory.Any(fact => fact.Uncertain && fact.ClosedByOriginalGrant is not { Consumed: true });
    private static bool OriginalVisibilityHistoryIntact(DmaGrantRecord record) => (ulong)record.VisibilityHistory.Count == record.VisibilityIssued &&
        record.VisibilityHistory.All(fact => ReferenceEquals(fact.OwnerSelf,fact) && ReferenceEquals(fact.Source,record));
    private bool CanContainPendingOriginalDmaVisibility(DmaGrantRecord record) => OriginalVisibilityHistoryIntact(record) && record.VisibilityHistory.Where(fact => fact.Uncertain).All(fact =>
        ReferenceEquals(fact.OwnerSelf,fact) && ReferenceEquals(fact.Source,record) && fact.Invoked && fact.CanContain &&
        fact.Grant == record.ProviderGrant && record.SourceRevision == fact.Revision && BackendEpoch == fact.Epoch);

    private void PinOriginalDmaVisibilityLocked(DmaGrantRecord record, DmaVisibilityOwnerInvocation? fact)
    {
        if (fact is not null) fact.Uncertain = true;
        if (fact is { Invoked: true, CanContain: true } && ReferenceEquals(fact.OwnerSelf,fact) && ReferenceEquals(fact.Source,record) &&
            record.VisibilityHistory.Any(old => ReferenceEquals(old,fact)) && fact.Grant == record.ProviderGrant &&
            record.SourceRevision == fact.Revision && BackendEpoch == fact.Epoch && IsOriginalDmaGrant(record))
            record.OwnVisibilityUncertain = true; // Exact grant Close may contain only this visibility use.
        else _dmaSubmissionFaultPins.Add(record.Grant.GrantId); // Independent/incapable continuity remains pinned.
    }
}
