using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase16ResourceBudgetRecoveryJournalTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();

    [Fact]
    public void ColdReplayNeverMaterializesLeaseAndPreservesPossibleSubmitAsBacklog()
    {
        var store = new MemoryStore();
        var journal = new ResourceBudgetRecoveryJournal(store, Key, Guid.Parse("8ac040b0-22c8-4f85-b9a7-21c4767377ec"));
        journal.Append(Payload(ResourceBudgetRecoveryTransition.Prepared));
        journal.Append(Payload(ResourceBudgetRecoveryTransition.PossibleSubmit));

        var restarted = new ResourceBudgetRecoveryJournal(store, Key);
        var snapshot = restarted.Replay();

        var item = Assert.Single(snapshot.ReconciliationBacklog);
        Assert.Equal(ResourceBudgetRecoveryTransition.PossibleSubmit, item.LastPayload.Transition);
        Assert.False(item.IsTerminal);
        Assert.Equal(2UL, snapshot.LastSequence);
        Assert.DoesNotContain(typeof(ResourceBudgetAuthority).GetFields(
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic),
            field => field.FieldType == typeof(ResourceBudgetRecoveryJournal));
    }

    [Fact]
    public void ExactContainmentAndWorstCaseAreTerminalButPendingIsNotRefundEvidence()
    {
        var store = new MemoryStore();
        var journal = new ResourceBudgetRecoveryJournal(store, Key, Guid.NewGuid());
        journal.Append(Payload(ResourceBudgetRecoveryTransition.Prepared));
        journal.Append(Payload(ResourceBudgetRecoveryTransition.PossibleSubmit));
        journal.Append(Payload(ResourceBudgetRecoveryTransition.Quarantined));
        journal.Append(Payload(ResourceBudgetRecoveryTransition.SettledConservative, [Amount(1000)]));

        var item = Assert.Single(journal.Replay().Items);
        Assert.True(item.IsTerminal);
        Assert.False(item.RequiresReconciliation);
        Assert.Equal([Amount(1000)], item.LastPayload.ChargedAmounts);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("authenticator")]
    [InlineData("chain")]
    [InlineData("torn")]
    [InlineData("wrong-key")]
    public void TamperWrongKeyAndTornFramesFailClosed(string mutation)
    {
        var store = new MemoryStore();
        var journal = new ResourceBudgetRecoveryJournal(store, Key, Guid.NewGuid());
        journal.Append(Payload(ResourceBudgetRecoveryTransition.Prepared));
        journal.Append(Payload(ResourceBudgetRecoveryTransition.PossibleSubmit));

        if (mutation == "wrong-key")
        {
            var wrong = Key.ToArray();
            wrong[0] ^= 0xff;
            Assert.Throws<InvalidDataException>(() => new ResourceBudgetRecoveryJournal(store, wrong));
            return;
        }

        var target = mutation == "chain" ? store.Frames[1] : store.Frames[0];
        if (mutation == "torn") store.Frames[1] = store.Frames[1][..^1];
        else if (mutation == "payload") target[target.Length / 2] ^= 0x40;
        else target[^1] ^= 0x40;

        Assert.Throws<InvalidDataException>(() => new ResourceBudgetRecoveryJournal(store, Key));
    }

    [Fact]
    public void InvalidTransitionsWideningAndAutomaticRefundShapesAreRejected()
    {
        var store = new MemoryStore();
        var journal = new ResourceBudgetRecoveryJournal(store, Key, Guid.NewGuid());
        journal.Append(Payload(ResourceBudgetRecoveryTransition.Prepared));

        Assert.Throws<InvalidDataException>(() => journal.Append(
            Payload(ResourceBudgetRecoveryTransition.SettledContained)));
        Assert.Throws<InvalidDataException>(() => journal.Append(
            Payload(ResourceBudgetRecoveryTransition.SettledExact, [Amount(1001)])));
        Assert.Throws<InvalidDataException>(() => journal.Append(
            Payload(ResourceBudgetRecoveryTransition.SettledConservative, [Amount(999)])));

        journal.Append(Payload(ResourceBudgetRecoveryTransition.PossibleSubmit));
        Assert.Throws<InvalidDataException>(() => journal.Append(
            Payload(ResourceBudgetRecoveryTransition.CancelledPreSubmit)));
    }

    [Fact]
    public void FileStoreRoundTripsFsyncedFramesAndRejectsTornTail()
    {
        var root = Path.Combine(Path.GetTempPath(), $"singnext-p16-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "budget.recovery");
        try
        {
            var journal = new ResourceBudgetRecoveryJournal(new FileResourceBudgetJournalStore(path), Key, Guid.NewGuid());
            journal.Append(Payload(ResourceBudgetRecoveryTransition.Prepared));
            journal.Append(Payload(ResourceBudgetRecoveryTransition.CancelledPreSubmit));
            var replay = new ResourceBudgetRecoveryJournal(new FileResourceBudgetJournalStore(path), Key).Replay();
            Assert.True(Assert.Single(replay.Items).IsTerminal);

            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
                stream.Write([8, 0, 0, 0, 1, 2]);
            Assert.Throws<InvalidDataException>(() =>
                new ResourceBudgetRecoveryJournal(new FileResourceBudgetJournalStore(path), Key));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CompleteFrameThenAppendErrorAdvancesNextSequenceFromVerifiedReplay()
    {
        var root = Path.Combine(Path.GetTempPath(), $"singnext-p16-ambiguous-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "budget.recovery");
            var store = new CompleteFrameThenThrowStore(new FileResourceBudgetJournalStore(path));
            var journal = new ResourceBudgetRecoveryJournal(store, Key, Guid.NewGuid());
            journal.Append(Payload(ResourceBudgetRecoveryTransition.Prepared));
            journal.Append(Payload(ResourceBudgetRecoveryTransition.PossibleSubmit));
            store.ThrowAfterNextWrite = true;

            Assert.Throws<IOException>(() => journal.Append(
                Payload(ResourceBudgetRecoveryTransition.SettledExact, [Amount(4)])));
            Assert.Equal(3UL, journal.Replay().LastSequence);
            var nextLease = Payload(ResourceBudgetRecoveryTransition.Prepared) with
            {
                Lease = new BudgetReservationHandle(new BudgetReservationId(32),
                    new BudgetGeneration(4))
            };
            var next = journal.Append(nextLease);

            Assert.Equal(4UL, next.Sequence);
            var replay = new ResourceBudgetRecoveryJournal(
                new FileResourceBudgetJournalStore(path), Key).Replay();
            Assert.Equal(4UL, replay.LastSequence);
            Assert.Equal(2, replay.Items.Count);
            Assert.Equal(ResourceBudgetRecoveryTransition.SettledExact,
                replay.Items.Single(item => item.LastPayload.Lease.ReservationId.Value == 31)
                    .LastPayload.Transition);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveReplayRejectsRegressedTailAndChangedEpoch(bool replaceEpoch)
    {
        var store = new MemoryStore();
        var journal = new ResourceBudgetRecoveryJournal(store, Key, Guid.NewGuid());
        journal.Append(Payload(ResourceBudgetRecoveryTransition.Prepared));
        journal.Append(Payload(ResourceBudgetRecoveryTransition.PossibleSubmit));
        Assert.Equal([Amount(1000)], journal.Replay().ConservativeRecoveryCharge);

        store.Frames.Clear();
        if (replaceEpoch)
        {
            var replacement = new ResourceBudgetRecoveryJournal(store, Key, Guid.NewGuid());
            replacement.Append(Payload(ResourceBudgetRecoveryTransition.Prepared));
            replacement.Append(Payload(ResourceBudgetRecoveryTransition.CancelledPreSubmit));
            Assert.Empty(replacement.Replay().ConservativeRecoveryCharge);
        }

        Assert.Throws<InvalidDataException>(() => journal.Replay());
        Assert.Throws<InvalidDataException>(() => journal.Append(Payload(
            ResourceBudgetRecoveryTransition.Prepared) with
        {
            Lease = new BudgetReservationHandle(new BudgetReservationId(32),
                new BudgetGeneration(4))
        }));
    }

    private static ResourceBudgetRecoveryPayload Payload(
        ResourceBudgetRecoveryTransition transition,
        IReadOnlyList<BudgetAmount>? charged = null) =>
        new(new BudgetReservationHandle(new BudgetReservationId(31), new BudgetGeneration(4)),
            new ProcessHandle(new ProcessId(41), 5), [Amount(1000)],
            new PlatformResourceCorrelation(new PlatformResourceCorrelationId(51),
                new PlatformResourceCorrelationGeneration(6)), transition, charged ?? []);

    private static BudgetAmount Amount(ulong value) =>
        new(ServiceBudgetDimension.ComputeTimeNanoseconds, value);

    private sealed class MemoryStore : IResourceBudgetJournalStore
    {
        internal List<byte[]> Frames { get; } = [];
        public IReadOnlyList<byte[]> ReadFrames() => Frames.Select(static frame => frame.ToArray()).ToArray();
        public void AppendFrame(ReadOnlySpan<byte> frame) => Frames.Add(frame.ToArray());
    }

    private sealed class CompleteFrameThenThrowStore(IResourceBudgetJournalStore inner)
        : IResourceBudgetJournalStore
    {
        internal bool ThrowAfterNextWrite { get; set; }
        public IReadOnlyList<byte[]> ReadFrames() => inner.ReadFrames();
        public void AppendFrame(ReadOnlySpan<byte> frame)
        {
            inner.AppendFrame(frame);
            if (!ThrowAfterNextWrite) return;
            ThrowAfterNextWrite = false;
            throw new IOException("Simulated lost append acknowledgement after full file frame.");
        }
    }
}
