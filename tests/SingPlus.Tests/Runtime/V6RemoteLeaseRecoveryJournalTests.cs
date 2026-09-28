using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class V6RemoteLeaseRecoveryJournalTests
{
    [Fact]
    public void RestartPersistsFreshOwnerGenerationAndQuarantinesPossibleEffect()
    {
        var store = new MemoryStore();
        var journal = Journal(store);
        journal.Append(Payload(V6RemoteLeaseRecoveryTransition.Issued, IssuedEscrow));
        journal.Append(Payload(V6RemoteLeaseRecoveryTransition.Submitted, ConsumedEscrow));

        var restarted = Journal(store).RecordOwnerRestart(Lease.OwnerHostIdentity, 4, 8);

        var item = Assert.Single(restarted.Items);
        Assert.Equal(V6RemoteLeaseRestartDisposition.QuarantinedAwaitingClosure, item.Disposition);
        Assert.True(item.RequiresExactClosure);
        Assert.False(item.AuthorizesExecution);
        Assert.False(item.AuthorizesPublication);
        Assert.False(item.AuthorizesReclaim);
        Assert.False(restarted.RestoresLeaseAuthority);
        Assert.False(restarted.AllowsOldLeasePublication);
        Assert.Equal(RemoteLeaseAdmissionCodeV1.StaleOwnerIncarnation,
            RemoteAuthorityLeaseEvaluatorV1.Evaluate(Lease,
                Current with { OwnerIncarnation = 4, OwnerEpoch = 8 }));

        var staleNewIdentity = Lease with { LeaseId = Guid.NewGuid() };
        Assert.Throws<InvalidDataException>(() =>
            Journal(store).Append(Payload(V6RemoteLeaseRecoveryTransition.Issued,
                IssuedEscrow, staleNewIdentity)));
        var fresh = Lease with
        {
            LeaseId = Guid.NewGuid(), OwnerIncarnation = 4, OwnerEpoch = 8,
            LeaseGeneration = 1, IssuedAtOwnerSequence = 1, NotAfterOwnerSequence = 5
        };
        Assert.Equal(2, Journal(store).Append(Payload(V6RemoteLeaseRecoveryTransition.Issued,
            IssuedEscrow, fresh)).Items.Count);
    }

    [Fact]
    public void ExactPostRestartClosureAllowsReclaimOnlyAndTerminatesBacklog()
    {
        var store = new MemoryStore();
        var journal = Journal(store);
        journal.Append(Payload(V6RemoteLeaseRecoveryTransition.Issued, IssuedEscrow));
        journal.Append(Payload(V6RemoteLeaseRecoveryTransition.Submitted, ConsumedEscrow));
        journal.Append(Payload(V6RemoteLeaseRecoveryTransition.ProviderLost, ConsumedEscrow));
        journal.RecordOwnerRestart(Lease.OwnerHostIdentity, 4, 8);

        journal.Append(Payload(V6RemoteLeaseRecoveryTransition.EffectClosed,
            ConsumedEscrow, closure: Closure));
        var replay = journal.Append(Payload(V6RemoteLeaseRecoveryTransition.Reclaimed,
            ReclaimedEscrow, closure: Closure));

        var terminal = Assert.Single(replay.Items);
        Assert.Equal(V6RemoteLeaseRecoveryTransition.Reclaimed,
            terminal.LastPayload.Transition);
        var nextRestart = journal.RecordOwnerRestart(Lease.OwnerHostIdentity, 5, 9);
        Assert.Equal(V6RemoteLeaseRestartDisposition.TerminalReclaimed,
            Assert.Single(nextRestart.Items).Disposition);
    }

    [Fact]
    public void IssuedLeaseAfterRestartCannotBeReclaimedWithoutExactClosure()
    {
        var store = new MemoryStore();
        var journal = Journal(store);
        journal.Append(Payload(V6RemoteLeaseRecoveryTransition.Issued, IssuedEscrow));
        var plan = journal.RecordOwnerRestart(Lease.OwnerHostIdentity, 4, 8);

        Assert.Equal(V6RemoteLeaseRestartDisposition.QuarantinedAwaitingClosure,
            Assert.Single(plan.Items).Disposition);
        Assert.Throws<InvalidDataException>(() => journal.Append(Payload(
            V6RemoteLeaseRecoveryTransition.Reclaimed,
            IssuedEscrow with { ReturnedAllocation = IssuedEscrow.DelegatedAllocation })));
        Assert.Throws<InvalidDataException>(() => journal.Append(Payload(
            V6RemoteLeaseRecoveryTransition.EffectClosed, ConsumedEscrow,
            closure: Closure with { ProviderGeneration = ProviderGeneration + 1 })));

        journal.Append(Payload(V6RemoteLeaseRecoveryTransition.EffectClosed,
            ConsumedEscrow, closure: Closure));
        var replay = journal.Append(Payload(V6RemoteLeaseRecoveryTransition.Reclaimed,
            ReclaimedEscrow, closure: Closure));
        Assert.Equal(V6RemoteLeaseRecoveryTransition.Reclaimed,
            Assert.Single(replay.Items).LastPayload.Transition);
    }

    [Fact]
    public void ClosedOrPublishedLeaseRestartsAsReclaimOnlyNeverRepublish()
    {
        var store = new MemoryStore();
        var journal = Journal(store);
        journal.Append(Payload(V6RemoteLeaseRecoveryTransition.Issued, IssuedEscrow));
        journal.Append(Payload(V6RemoteLeaseRecoveryTransition.Submitted, ConsumedEscrow));
        journal.Append(Payload(V6RemoteLeaseRecoveryTransition.Fenced, ConsumedEscrow));
        journal.Append(Payload(V6RemoteLeaseRecoveryTransition.EffectClosed,
            ConsumedEscrow, closure: Closure));
        journal.Append(Payload(V6RemoteLeaseRecoveryTransition.Published,
            ConsumedEscrow, closure: Closure));

        var plan = journal.RecordOwnerRestart(Lease.OwnerHostIdentity, 4, 8);

        var item = Assert.Single(plan.Items);
        Assert.Equal(V6RemoteLeaseRestartDisposition.ClosureVerifiedReclaimOnly,
            item.Disposition);
        Assert.False(item.AuthorizesPublication);
        Assert.False(plan.AllowsOldLeasePublication);
    }

    [Fact]
    public void WrongKeyAndTamperingFailAuthenticationBeforeRecovery()
    {
        var store = new MemoryStore();
        Journal(store).Append(Payload(V6RemoteLeaseRecoveryTransition.Issued, IssuedEscrow));

        Assert.Throws<InvalidDataException>(() =>
            new V6RemoteLeaseRecoveryJournal(store, new byte[32]));
        store.Frames[0][^1] ^= 0x5a;
        Assert.Throws<InvalidDataException>(() => Journal(store));
    }

    [Fact]
    public void RestartHighWatermarksMustAdvanceAcrossRepeatedProcessRestarts()
    {
        var store = new MemoryStore();
        var journal = Journal(store);
        journal.Append(Payload(V6RemoteLeaseRecoveryTransition.Issued, IssuedEscrow));
        journal.RecordOwnerRestart(Lease.OwnerHostIdentity, 4, 8);

        Assert.Throws<InvalidDataException>(() =>
            Journal(store).RecordOwnerRestart(Lease.OwnerHostIdentity, 4, 9));
        Assert.Throws<InvalidDataException>(() =>
            Journal(store).RecordOwnerRestart(Lease.OwnerHostIdentity, 5, 8));
        var second = Journal(store).RecordOwnerRestart(Lease.OwnerHostIdentity, 5, 9);
        Assert.Equal(5UL, second.FreshOwnerIncarnation);
        Assert.Equal(9UL, second.FreshOwnerEpoch);
    }

    [Fact]
    public void FileStoreFlushesAuthenticatedRestartPlanAcrossInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"singnext-p11-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "remote-lease.journal");
        try
        {
            var journal = new V6RemoteLeaseRecoveryJournal(
                new FileResourceBudgetJournalStore(path), Key, JournalEpoch);
            journal.Append(Payload(V6RemoteLeaseRecoveryTransition.Issued, IssuedEscrow));
            journal.Append(Payload(V6RemoteLeaseRecoveryTransition.Submitted, ConsumedEscrow));

            var recovered = new V6RemoteLeaseRecoveryJournal(
                new FileResourceBudgetJournalStore(path), Key)
                .RecordOwnerRestart(Lease.OwnerHostIdentity, 4, 8);

            Assert.Equal(V6RemoteLeaseRestartDisposition.QuarantinedAwaitingClosure,
                Assert.Single(recovered.Items).Disposition);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static V6RemoteLeaseRecoveryJournal Journal(MemoryStore store) =>
        new(store, Key, JournalEpoch);

    private static V6RemoteLeaseRecoveryPayload Payload(
        V6RemoteLeaseRecoveryTransition transition, RemoteResourceEscrowV1 escrow,
        RemoteAuthorityLeaseV1? lease = null, RemoteEffectClosureV1? closure = null)
    {
        var exact = lease ?? Lease;
        return new(transition, exact.OwnerHostIdentity, exact.OwnerIncarnation,
            exact.OwnerEpoch, exact, escrow, ProviderGeneration, closure);
    }

    private sealed class MemoryStore : IResourceBudgetJournalStore
    {
        internal List<byte[]> Frames { get; } = [];
        public IReadOnlyList<byte[]> ReadFrames() => Frames.Select(static frame => frame.ToArray()).ToArray();
        public void AppendFrame(ReadOnlySpan<byte> frame) => Frames.Add(frame.ToArray());
    }

    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
    private static readonly Guid JournalEpoch = Guid.Parse("ae81a933-f210-460c-b883-57008316fa40");
    private const ulong ProviderGeneration = 19;
    private static readonly RemoteAuthorityLeaseV1 Lease = new(1,
        Guid.Parse("690c6048-a963-4ae0-a687-f2ee6adbd44d"),
        "host:owner", 3, "host:remote", 5, new('c', 64),
        RemoteLeaseRightsV1.Read | RemoteLeaseRightsV1.Execute | RemoteLeaseRightsV1.StagedWrite,
        7, 11, 13, 17);
    private static readonly RemoteLeaseCurrentStateV1 Current = new(3, 5, 7, 11, 13, false, false);
    private static readonly RemoteEffectClosureV1 Closure = new(1, Lease.LeaseId,
        Lease.OwnerEpoch, Lease.LeaseGeneration, ProviderGeneration, 23, true, true);
    private static readonly RemoteResourceEscrowV1 IssuedEscrow = new(1, 100, 60, 0, 0);
    private static readonly RemoteResourceEscrowV1 ConsumedEscrow = new(1, 100, 60, 20, 0);
    private static readonly RemoteResourceEscrowV1 ReclaimedEscrow = new(1, 100, 60, 20, 40);
}
