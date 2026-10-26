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
        MemoryStore store = new MemoryStore();
        ResourceBudgetRecoveryJournal journal = new ResourceBudgetRecoveryJournal(store, Key, Guid.Parse("8ac040b0-22c8-4f85-b9a7-21c4767377ec"));
        journal.Append(Payload(ResourceBudgetRecoveryTransition.Prepared));
        journal.Append(Payload(ResourceBudgetRecoveryTransition.PossibleSubmit));

        ResourceBudgetRecoveryJournal restarted = new ResourceBudgetRecoveryJournal(store, Key);
        ResourceBudgetRecoverySnapshot snapshot = restarted.Replay();

        ResourceBudgetRecoveryItem item = Assert.Single(snapshot.ReconciliationBacklog);
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
        MemoryStore store = new MemoryStore();
        ResourceBudgetRecoveryJournal journal = new ResourceBudgetRecoveryJournal(store, Key, Guid.NewGuid());
        journal.Append(Payload(ResourceBudgetRecoveryTransition.Prepared));
        journal.Append(Payload(ResourceBudgetRecoveryTransition.PossibleSubmit));
        journal.Append(Payload(ResourceBudgetRecoveryTransition.Quarantined));
        journal.Append(Payload(ResourceBudgetRecoveryTransition.SettledConservative, [Amount(1000)]));

        ResourceBudgetRecoveryItem item = Assert.Single(journal.Replay().Items);
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
        MemoryStore store = new MemoryStore();
        ResourceBudgetRecoveryJournal journal = new ResourceBudgetRecoveryJournal(store, Key, Guid.NewGuid());
        journal.Append(Payload(ResourceBudgetRecoveryTransition.Prepared));
        journal.Append(Payload(ResourceBudgetRecoveryTransition.PossibleSubmit));

        if (mutation == "wrong-key")
        {
            byte[] wrong = Key.ToArray();
            wrong[0] ^= 0xff;
            Assert.Throws<InvalidDataException>(() => new ResourceBudgetRecoveryJournal(store, wrong));
            return;
        }

        byte[] target = mutation == "chain" ? store.Frames[1] : store.Frames[0];
        if (mutation == "torn") store.Frames[1] = store.Frames[1][..^1];
        else if (mutation == "payload") target[target.Length / 2] ^= 0x40;
        else target[^1] ^= 0x40;

        Assert.Throws<InvalidDataException>(() => new ResourceBudgetRecoveryJournal(store, Key));
    }

    [Fact]
    public void InvalidTransitionsWideningAndAutomaticRefundShapesAreRejected()
    {
        MemoryStore store = new MemoryStore();
        ResourceBudgetRecoveryJournal journal = new ResourceBudgetRecoveryJournal(store, Key, Guid.NewGuid());
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
        string root = Path.Combine(Path.GetTempPath(), $"singnext-p16-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "budget.recovery");
        try
        {
            ResourceBudgetRecoveryJournal journal = new ResourceBudgetRecoveryJournal(new FileResourceBudgetJournalStore(path), Key, Guid.NewGuid());
            journal.Append(Payload(ResourceBudgetRecoveryTransition.Prepared));
            journal.Append(Payload(ResourceBudgetRecoveryTransition.CancelledPreSubmit));
            ResourceBudgetRecoverySnapshot replay = new ResourceBudgetRecoveryJournal(new FileResourceBudgetJournalStore(path), Key).Replay();
            Assert.True(Assert.Single(replay.Items).IsTerminal);

            using (FileStream stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
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
        string root = Path.Combine(Path.GetTempPath(), $"singnext-p16-ambiguous-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "budget.recovery");
            CompleteFrameThenThrowStore store = new CompleteFrameThenThrowStore(new FileResourceBudgetJournalStore(path));
            ResourceBudgetRecoveryJournal journal = new ResourceBudgetRecoveryJournal(store, Key, Guid.NewGuid());
            journal.Append(Payload(ResourceBudgetRecoveryTransition.Prepared));
            journal.Append(Payload(ResourceBudgetRecoveryTransition.PossibleSubmit));
            store.ThrowAfterNextWrite = true;

            Assert.Throws<IOException>(() => journal.Append(
                Payload(ResourceBudgetRecoveryTransition.SettledExact, [Amount(4)])));
            Assert.Equal(3UL, journal.Replay().LastSequence);
            ResourceBudgetRecoveryPayload nextLease = Payload(ResourceBudgetRecoveryTransition.Prepared) with
            {
                Lease = new BudgetReservationHandle(new BudgetReservationId(32),
                    new BudgetGeneration(4))
            };
            ResourceBudgetRecoveryRecord next = journal.Append(nextLease);

            Assert.Equal(4UL, next.Sequence);
            ResourceBudgetRecoverySnapshot replay = new ResourceBudgetRecoveryJournal(
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
        MemoryStore store = new MemoryStore();
        ResourceBudgetRecoveryJournal journal = new ResourceBudgetRecoveryJournal(store, Key, Guid.NewGuid());
        journal.Append(Payload(ResourceBudgetRecoveryTransition.Prepared));
        journal.Append(Payload(ResourceBudgetRecoveryTransition.PossibleSubmit));
        Assert.Equal([Amount(1000)], journal.Replay().ConservativeRecoveryCharge);

        store.Frames.Clear();
        if (replaceEpoch)
        {
            ResourceBudgetRecoveryJournal replacement = new ResourceBudgetRecoveryJournal(store, Key, Guid.NewGuid());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveReplayRejectsAuthenticatedForkWithoutChangingObservedPrefix(bool longer)
    {
        MemoryStore store = new MemoryStore();
        ResourceBudgetRecoveryJournal journal = new ResourceBudgetRecoveryJournal(store, Key, Guid.NewGuid());
        journal.Append(Payload(ResourceBudgetRecoveryTransition.Prepared));
        journal.Append(Payload(ResourceBudgetRecoveryTransition.PossibleSubmit));
        IReadOnlyList<byte[]> original = store.ReadFrames();
        MemoryStore branchStore = new MemoryStore();
        branchStore.Frames.Add(original[0].ToArray());
        ResourceBudgetRecoveryJournal branch = new ResourceBudgetRecoveryJournal(branchStore, Key);
        branch.Append(Payload(ResourceBudgetRecoveryTransition.CancelledPreSubmit));
        if (longer) branch.Append(Payload(ResourceBudgetRecoveryTransition.Prepared) with
        {
            Lease = new BudgetReservationHandle(new BudgetReservationId(32), new BudgetGeneration(4))
        });
        store.Frames.Clear();
        store.Frames.AddRange(branchStore.ReadFrames());
        Assert.Throws<InvalidDataException>(() => journal.Replay());
        int frameCount = store.Frames.Count;
        Assert.Throws<InvalidDataException>(() => journal.Append(Payload(ResourceBudgetRecoveryTransition.Prepared) with
        {
            Lease = new BudgetReservationHandle(new BudgetReservationId(33), new BudgetGeneration(4))
        }));
        Assert.Equal(frameCount, store.Frames.Count);
        store.Frames.Clear();
        store.Frames.AddRange(original);
        Assert.Equal(2UL, journal.Replay().LastSequence);
        Assert.Equal([Amount(1000)], journal.Replay().ConservativeRecoveryCharge);
    }

    [Fact]
    public void LiveReplayAcceptsAuthenticatedExtensionOfObservedPrefix()
    {
        MemoryStore store = new MemoryStore();
        ResourceBudgetRecoveryJournal journal = new ResourceBudgetRecoveryJournal(store, Key, Guid.NewGuid());
        journal.Append(Payload(ResourceBudgetRecoveryTransition.Prepared));
        journal.Append(Payload(ResourceBudgetRecoveryTransition.PossibleSubmit));
        ResourceBudgetRecoveryJournal extension = new ResourceBudgetRecoveryJournal(store, Key);
        extension.Append(Payload(ResourceBudgetRecoveryTransition.Quarantined));
        Assert.Equal(3UL, journal.Replay().LastSequence);
        Assert.Equal([Amount(1000)], journal.Replay().ConservativeRecoveryCharge);
    }

    [Fact]
    public void KernelRecoveryRejectsAuthenticatedForkAfterFirstSnapshot()
    {
        MemoryStore original = new MemoryStore();
        ResourceBudgetRecoveryJournal journal = new ResourceBudgetRecoveryJournal(original, Key, Guid.NewGuid());
        journal.Append(Payload(ResourceBudgetRecoveryTransition.Prepared));
        journal.Append(Payload(ResourceBudgetRecoveryTransition.PossibleSubmit));
        MemoryStore fork = new MemoryStore();
        fork.Frames.Add(original.Frames[0].ToArray());
        new ResourceBudgetRecoveryJournal(fork, Key).Append(
            Payload(ResourceBudgetRecoveryTransition.CancelledPreSubmit));
        SnapshotChangeStore changing = new SnapshotChangeStore(original.ReadFrames(), fork.ReadFrames());

        Assert.Throws<InvalidDataException>(() => new RuntimeKernel(null, null,
            new RuntimeKernelRecoveryOptions("unused-budget-recovery", Key), changing));
    }

    [Fact]
    public void KernelRecoveryAcceptsExtensionAndImportsConservativeCharge()
    {
        MemoryStore original = new MemoryStore();
        ResourceBudgetRecoveryJournal journal = new ResourceBudgetRecoveryJournal(original, Key, Guid.NewGuid());
        journal.Append(Payload(ResourceBudgetRecoveryTransition.Prepared));
        IReadOnlyList<byte[]> first = original.ReadFrames();
        journal.Append(Payload(ResourceBudgetRecoveryTransition.PossibleSubmit));
        SnapshotChangeStore changing = new SnapshotChangeStore(first, original.ReadFrames());

        RuntimeKernel kernel = new RuntimeKernel(null, null,
            new RuntimeKernelRecoveryOptions("unused-budget-recovery", Key), changing);

        Assert.Equal(ResourceBudgetRecoveryTransition.PossibleSubmit,
            Assert.Single(kernel.ResourceBudgetRecovery!.ReconciliationBacklog).LastPayload.Transition);
        Assert.Equal(KernelError.BudgetReservationNotFound, kernel.Budgets.Query(
            Payload(ResourceBudgetRecoveryTransition.Prepared).Lease).Error);
        Assert.Equal(KernelError.BudgetExceeded, kernel.Budgets.ConfigureSystem([Amount(999)]).Error);
        Assert.False(kernel.Budgets.RecoveryImportComplete);
        Assert.True(kernel.Budgets.ConfigureSystem([Amount(1000)]).IsSuccess);
        Assert.Equal(1000UL, kernel.Budgets.Query(kernel.Budgets.SystemBudget).Value!.Usage
            .Single(usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used);
    }

    private sealed class SnapshotChangeStore(IReadOnlyList<byte[]> first, IReadOnlyList<byte[]> next)
        : IResourceBudgetJournalStore
    {
        private int _reads;
        public IReadOnlyList<byte[]> ReadFrames() =>
            (++_reads == 1 ? first : next).Select(static frame => frame.ToArray()).ToArray();
        public void AppendFrame(ReadOnlySpan<byte> frame) =>
            throw new InvalidOperationException("Recovery must not append or repeat submission.");
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
