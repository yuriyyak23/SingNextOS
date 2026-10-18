using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class V6ManagedRemoteLeaseTransportTests
{
    [Fact]
    public void SerializedTwoEndpointLifecyclePreservesSingleOwnerAndEscrow()
    {
        var (pilot, _, transport) = Create();

        Assert.Equal(V6RemoteDelegatedResourcePilotState.Submitted,
            Send(transport, V6RemoteLeaseCommandKind.Submit, rights: RemoteLeaseRightsV1.StagedWrite,
                allocation: 20).State);
        Assert.Equal(V6RemoteDelegatedResourcePilotState.Fenced,
            Send(transport, V6RemoteLeaseCommandKind.Fence).State);
        Assert.Equal(V6RemoteDelegatedResourcePilotState.EffectClosed,
            Send(transport, V6RemoteLeaseCommandKind.EffectClosure, closure: Closure()).State);
        Assert.Equal(V6RemoteDelegatedResourcePilotState.Published,
            Send(transport, V6RemoteLeaseCommandKind.Publish).State);
        var reclaimed = Send(transport, V6RemoteLeaseCommandKind.Reclaim, closure: Closure());

        Assert.Equal(V6RemoteDelegatedResourcePilotState.Reclaimed, reclaimed.State);
        Assert.Equal(1U, reclaimed.SubmitCount);
        Assert.Equal(1U, reclaimed.PublishCount);
        Assert.Equal(reclaimed.Escrow.DelegatedAllocation,
            reclaimed.Escrow.ConsumedAllocation + reclaimed.Escrow.ReturnedAllocation);
        Assert.True(pilot.Query().HasSingleLogicalOwner);
        Assert.False(pilot.Query().TransfersParentAuthority);
        Assert.False(reclaimed.AuthorizesRemoteExecution);
        Assert.False(reclaimed.AuthorizesPublication);
        Assert.False(reclaimed.AuthorizesReclaim);
    }

    [Fact]
    public void DroppedResponseCanRetryExactRequestWithoutDuplicateSubmit()
    {
        var (_, owner, transport) = Create();
        var command = Command(V6RemoteLeaseCommandKind.Submit,
            rights: RemoteLeaseRightsV1.Execute, allocation: 7);
        transport.DropNextResponse();

        var ambiguous = transport.Send(command);
        var retry = transport.Send(command);

        Assert.Equal(KernelError.DependencyUnavailable, ambiguous.Error);
        Assert.True(retry.IsSuccess, retry.Message);
        Assert.Equal(1U, retry.Value!.SubmitCount);
        Assert.Equal(7UL, owner.Query().Escrow.ConsumedAllocation);
    }

    [Fact]
    public void CorruptedResponseAfterOwnerCommitCanRetryWithoutDuplicateSubmit()
    {
        var (_, owner, transport) = Create();
        var command = Command(V6RemoteLeaseCommandKind.Submit,
            rights: RemoteLeaseRightsV1.Execute, allocation: 7);
        transport.CorruptNextResponse();

        var ambiguous = transport.Send(command);
        Assert.Equal(KernelError.DependencyUnavailable, ambiguous.Error);
        Assert.Equal(1U, owner.Query().SubmitCount);

        var retry = transport.Send(command);
        Assert.True(retry.IsSuccess, retry.Message);
        Assert.Equal(1U, retry.Value!.SubmitCount);
        Assert.Equal(7UL, owner.Query().Escrow.ConsumedAllocation);
    }

    [Fact]
    public void JournaledSubmitPersistsPossibleEffectBeforeDeliveryAndExactRetry()
    {
        var (_, owner, transport) = Create(journaled: true);
        var store = new MemoryStore();
        var journal = Journal(store);
        journal.Append(IssuedPayload());
        var command = Command(V6RemoteLeaseCommandKind.Submit,
            rights: RemoteLeaseRightsV1.Execute, allocation: 7);
        Assert.Equal(KernelError.PlatformDenied, transport.Send(command).Error);
        var coordinator = new V6ManagedRemoteLeaseJournaledSubmit(journal, transport, ProviderGeneration);
        Assert.Equal(KernelError.PlatformDenied, transport.Send(command).Error);
        Assert.Equal(0U, owner.Query().SubmitCount);
        transport.DropNextResponse();

        Assert.Equal(KernelError.DependencyUnavailable, coordinator.SendSubmit(command).Error);
        Assert.Equal(V6RemoteLeaseRecoveryTransition.Submitted,
            Assert.Single(journal.Replay().Items).LastPayload.Transition);
        Assert.Equal(1U, owner.Query().SubmitCount);
        Assert.True(coordinator.SendSubmit(command).IsSuccess);
        Assert.Equal(1U, owner.Query().SubmitCount);
        Assert.Equal(KernelError.DuplicateIdentity,
            coordinator.SendSubmit(command with { Allocation = 8 }).Error);
        Assert.Equal(V6RemoteLeaseRestartDisposition.QuarantinedAwaitingClosure,
            Assert.Single(Journal(store).RecordOwnerRestart(Lease.OwnerHostIdentity, 4, 8).Items).Disposition);
    }

    [Fact]
    public void JournalAppendFailurePreventsManagedSubmitDelivery()
    {
        var (_, owner, transport) = Create(journaled: true);
        var store = new MemoryStore();
        var journal = Journal(store);
        journal.Append(IssuedPayload());
        store.FailNextAppend = true;
        var coordinator = new V6ManagedRemoteLeaseJournaledSubmit(journal, transport, ProviderGeneration);
        var command = Command(V6RemoteLeaseCommandKind.Submit,
            rights: RemoteLeaseRightsV1.Execute, allocation: 7);

        Assert.Equal(KernelError.DependencyUnavailable, coordinator.SendSubmit(command).Error);
        Assert.Equal(KernelError.DependencyUnavailable, coordinator.SendSubmit(command).Error);
        Assert.Equal(V6RemoteLeaseRecoveryTransition.Issued,
            Assert.Single(journal.Replay().Items).LastPayload.Transition);
        Assert.Equal(0U, owner.Query().SubmitCount);
    }

    [Fact]
    public void JournaledSubmitWithoutExactIssuedTupleCannotReachOwner()
    {
        var (_, owner, transport) = Create(journaled: true);
        var coordinator = new V6ManagedRemoteLeaseJournaledSubmit(
            Journal(new MemoryStore()), transport, ProviderGeneration);

        var result = coordinator.SendSubmit(Command(V6RemoteLeaseCommandKind.Submit,
            rights: RemoteLeaseRightsV1.Execute, allocation: 7));

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(0U, owner.Query().SubmitCount);
    }

    [Fact]
    public void OwnerJournalRestartDeniesExactRetryOfDeliveredSubmit()
    {
        var (_, owner, transport) = Create(journaled: true);
        var journal = Journal(new MemoryStore());
        journal.Append(IssuedPayload());
        var coordinator = new V6ManagedRemoteLeaseJournaledSubmit(journal, transport, ProviderGeneration);
        var command = Command(V6RemoteLeaseCommandKind.Submit,
            rights: RemoteLeaseRightsV1.Execute, allocation: 7);
        transport.DropNextResponse();
        Assert.Equal(KernelError.DependencyUnavailable, coordinator.SendSubmit(command).Error);
        journal.RecordOwnerRestart(Lease.OwnerHostIdentity, 4, 8);

        Assert.Equal(KernelError.StaleGeneration, coordinator.SendSubmit(command).Error);
        Assert.Equal(1U, owner.Query().SubmitCount);
    }

    [Fact]
    public void FileBackedJournalReplaysSubmittedBeforeRestartAndDeniesStaleRetry()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"singnext-p11-submit-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "lease.journal");
        try
        {
            var (_, owner, transport) = Create(journaled: true);
            var journal = Journal(new FileResourceBudgetJournalStore(path));
            journal.Append(IssuedPayload());
            var coordinator = new V6ManagedRemoteLeaseJournaledSubmit(journal, transport, ProviderGeneration);
            var command = Command(V6RemoteLeaseCommandKind.Submit,
                rights: RemoteLeaseRightsV1.Execute, allocation: 7);
            transport.DropNextResponse();
            Assert.Equal(KernelError.DependencyUnavailable, coordinator.SendSubmit(command).Error);

            var reopened = Journal(new FileResourceBudgetJournalStore(path));
            Assert.Equal(V6RemoteLeaseRecoveryTransition.Submitted,
                Assert.Single(reopened.Replay().Items).LastPayload.Transition);
            Assert.Equal(V6RemoteLeaseRestartDisposition.QuarantinedAwaitingClosure,
                Assert.Single(reopened.RecordOwnerRestart(Lease.OwnerHostIdentity, 4, 8).Items).Disposition);
            Assert.Equal(KernelError.StaleGeneration, coordinator.SendSubmit(command).Error);
            Assert.Equal(1U, owner.Query().SubmitCount);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DuplicateWireDeliveryReturnsCachedOwnerResponse()
    {
        var (_, owner, transport) = Create();
        transport.DuplicateNextDelivery();

        var result = transport.Send(Command(V6RemoteLeaseCommandKind.Submit,
            rights: RemoteLeaseRightsV1.Execute, allocation: 3));

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1U, owner.Query().SubmitCount);
        Assert.Equal(3UL, owner.Query().Escrow.ConsumedAllocation);
    }

    [Fact]
    public void PartitionBeforeDeliveryCannotReachOwnerLinearization()
    {
        var (_, owner, transport) = Create();
        transport.SetPartitioned(true);

        var result = transport.Send(Command(V6RemoteLeaseCommandKind.Submit,
            rights: RemoteLeaseRightsV1.Execute, allocation: 1));

        Assert.Equal(KernelError.DependencyUnavailable, result.Error);
        Assert.Equal(0U, owner.Query().SubmitCount);
        Assert.Equal(V6RemoteDelegatedResourcePilotState.Issued, owner.Query().State);
    }

    [Fact]
    public void RequestIdentityReuseWithDifferentPayloadFailsClosed()
    {
        var (_, owner, transport) = Create();
        var request = Guid.Parse("667ec558-742d-49b8-9c5e-f4cabf8341ae");
        Assert.True(transport.Send(Command(V6RemoteLeaseCommandKind.Submit, request,
            RemoteLeaseRightsV1.Execute, 2)).IsSuccess);

        var conflicting = transport.Send(Command(V6RemoteLeaseCommandKind.Submit, request,
            RemoteLeaseRightsV1.Execute, 3));

        Assert.Equal(KernelError.DuplicateIdentity, conflicting.Error);
        Assert.Equal(1U, owner.Query().SubmitCount);
        Assert.Equal(2UL, owner.Query().Escrow.ConsumedAllocation);
    }

    [Fact]
    public void ProviderLossTransportForbidsPublicationUntilExactClosureAllowsOnlyReclaim()
    {
        var (_, owner, transport) = Create();
        _ = Send(transport, V6RemoteLeaseCommandKind.Submit,
            rights: RemoteLeaseRightsV1.StagedWrite, allocation: 9);
        var lost = Send(transport, V6RemoteLeaseCommandKind.ProviderLoss);
        Assert.True(lost.ProviderLost);
        Assert.Equal(V6RemoteDelegatedResourcePilotState.ProviderLost, lost.State);
        _ = Send(transport, V6RemoteLeaseCommandKind.EffectClosure, closure: Closure());

        var publish = transport.Send(Command(V6RemoteLeaseCommandKind.Publish));
        var reclaimed = Send(transport, V6RemoteLeaseCommandKind.Reclaim, closure: Closure());

        Assert.Equal(KernelError.PlatformUnavailable, publish.Error);
        Assert.Equal(V6RemoteDelegatedResourcePilotState.Reclaimed, reclaimed.State);
        Assert.True(owner.Query().ProviderLost);
        Assert.Equal(0U, owner.Query().PublishCount);
    }

    [Fact]
    public void OwnerRebootMakesTransportedLeaseStaleWithoutResurrection()
    {
        var (pilot, owner, transport) = Create();
        pilot.RebootOwner(4, 8);

        var result = transport.Send(Command(V6RemoteLeaseCommandKind.Submit,
            rights: RemoteLeaseRightsV1.Execute, allocation: 1));

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(0U, owner.Query().SubmitCount);
    }

    [Fact]
    public void CachedSubmitResponseCannotResurrectLeaseAfterOwnerReboot()
    {
        var (pilot, owner, transport) = Create();
        var command = Command(V6RemoteLeaseCommandKind.Submit,
            rights: RemoteLeaseRightsV1.Execute, allocation: 1);
        Assert.True(transport.Send(command).IsSuccess);
        pilot.RebootOwner(4, 8);

        Assert.Equal(KernelError.StaleGeneration, transport.Send(command).Error);
        Assert.Equal(1U, owner.Query().SubmitCount);
    }

    [Fact]
    public void ManagedOwnerRestartFencesBeforeJournalAppendAndStaleRetry()
    {
        var (_, owner, transport) = Create(journaled: true);
        var store = new MemoryStore();
        var journal = Journal(store);
        journal.Append(IssuedPayload());
        var coordinator = new V6ManagedRemoteLeaseJournaledSubmit(journal, transport, ProviderGeneration);
        var command = Command(V6RemoteLeaseCommandKind.Submit,
            rights: RemoteLeaseRightsV1.Execute, allocation: 1);
        transport.DropNextResponse();
        Assert.Equal(KernelError.DependencyUnavailable, coordinator.SendSubmit(command).Error);

        var restart = owner.RestartOwner(journal, 4, 8);

        Assert.True(restart.IsSuccess, restart.Message);
        Assert.Equal(V6RemoteLeaseRestartDisposition.QuarantinedAwaitingClosure,
            Assert.Single(restart.Value!.Items).Disposition);
        Assert.Equal(KernelError.StaleGeneration, coordinator.SendSubmit(command).Error);
        Assert.Equal(1U, owner.Query().SubmitCount);
    }

    [Fact]
    public void FailedRestartJournalAppendLeavesLiveOwnerFenced()
    {
        var (_, owner, transport) = Create();
        var store = new MemoryStore();
        var journal = Journal(store);
        journal.Append(IssuedPayload());
        store.FailNextAppend = true;

        Assert.Equal(KernelError.DependencyUnavailable, owner.RestartOwner(journal, 4, 8).Error);
        Assert.Equal(4UL, owner.Query().Current.OwnerIncarnation);
        Assert.Equal(V6RemoteLeaseRecoveryTransition.Issued,
            Assert.Single(journal.Replay().Items).LastPayload.Transition);
        Assert.Equal(KernelError.StaleGeneration, transport.Send(Command(
            V6RemoteLeaseCommandKind.Submit, rights: RemoteLeaseRightsV1.Execute, allocation: 1)).Error);
    }

    [Fact]
    public async Task ConcurrentManagedSubmitAndRestartNeverAdmitOldLeaseAfterRestart()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var (_, owner, transport) = Create(journaled: true);
            var journal = Journal(new MemoryStore());
            journal.Append(IssuedPayload());
            var coordinator = new V6ManagedRemoteLeaseJournaledSubmit(journal, transport, ProviderGeneration);
            var command = Command(V6RemoteLeaseCommandKind.Submit,
                rights: RemoteLeaseRightsV1.Execute, allocation: 1);
            using var start = new ManualResetEventSlim();
            var submit = Task.Run(() => { start.Wait(); return coordinator.SendSubmit(command); });
            var restart = Task.Run(() => { start.Wait(); return owner.RestartOwner(journal, 4, 8); });
            start.Set();
            await Task.WhenAll(submit, restart);

            Assert.True((await restart).IsSuccess);
            Assert.Contains(coordinator.SendSubmit(command).Error,
                new[] { KernelError.StaleGeneration, KernelError.DependencyUnavailable });
            Assert.True(owner.Query().SubmitCount <= 1);
        }
    }

    [Fact]
    public void WireCodecRejectsTrailingAndOversizedCommandsBeforeOwnerDispatch()
    {
        var command = Command(V6RemoteLeaseCommandKind.Submit,
            rights: RemoteLeaseRightsV1.Execute, allocation: 1);
        var canonical = V6RemoteLeaseWireCodec.Encode(command);
        var trailing = canonical.Append((byte)0xff).ToArray();

        Assert.Throws<ArgumentException>(() =>
            V6RemoteLeaseWireCodec.DecodeCommand(trailing));
        Assert.Throws<ArgumentException>(() =>
            V6RemoteLeaseWireCodec.DecodeCommand(
                new byte[V6RemoteLeaseWireCodec.MaximumCommandBytes + 1]));
    }

    [Fact]
    public void ResponseMustMatchExactRequestKindAndWireDigestEvenOnFailure()
    {
        var (_, owner, _) = Create();
        var command = Command(V6RemoteLeaseCommandKind.Submit,
            rights: RemoteLeaseRightsV1.Execute, allocation: 1);
        byte[] encodedCommand = V6RemoteLeaseWireCodec.Encode(command);
        string commandDigest = V6RemoteLeaseWireCodec.Digest(encodedCommand);
        byte[] response = owner.Handle(encodedCommand).Value!;
        Assert.True(V6RemoteLeaseWireCodec.DecodeResponse(response, command, commandDigest).IsSuccess);

        Assert.Equal(KernelError.InvalidMessage,
            V6RemoteLeaseWireCodec.DecodeResponse(response,
                command with { RequestId = Guid.NewGuid() }, commandDigest).Error);
        Assert.Equal(KernelError.InvalidMessage,
            V6RemoteLeaseWireCodec.DecodeResponse(response,
                command with { Kind = V6RemoteLeaseCommandKind.Fence }, commandDigest).Error);

        byte[] tampered = response.ToArray();
        tampered[^1] = tampered[^1] == (byte)'0' ? (byte)'1' : (byte)'0';
        Assert.Equal(KernelError.InvalidMessage,
            V6RemoteLeaseWireCodec.DecodeResponse(tampered, command, commandDigest).Error);

        byte[] rejected = V6RemoteLeaseWireCodec.EncodeResponse(command,
            KernelResult<V6RemoteDelegatedResourcePilotSnapshot>.Fail(
                KernelError.PlatformDenied, "owner denied"), commandDigest);
        Assert.Equal(KernelError.PlatformDenied,
            V6RemoteLeaseWireCodec.DecodeResponse(rejected, command, commandDigest).Error);
        Assert.Equal(KernelError.InvalidMessage,
            V6RemoteLeaseWireCodec.DecodeResponse(rejected,
                command with { RequestId = Guid.NewGuid() }, commandDigest).Error);
    }

    private static V6RemoteLeaseTransportReceipt Send(V6ManagedRemoteLeaseTransport transport,
        V6RemoteLeaseCommandKind kind, RemoteLeaseRightsV1 rights = RemoteLeaseRightsV1.None,
        ulong allocation = 0, RemoteEffectClosureV1? closure = null) =>
        transport.Send(Command(kind, rights: rights, allocation: allocation, closure: closure)).Value!;

    private static V6RemoteLeaseCommand Command(V6RemoteLeaseCommandKind kind,
        Guid? request = null, RemoteLeaseRightsV1 rights = RemoteLeaseRightsV1.None,
        ulong allocation = 0, RemoteEffectClosureV1? closure = null) =>
        new(1, request ?? Guid.NewGuid(), kind, Lease, rights, allocation, closure);

    private static (V6RemoteDelegatedResourcePilot Pilot, V6RemoteLeaseOwnerEndpoint Owner,
        V6ManagedRemoteLeaseTransport Transport) Create(bool journaled = false)
    {
        var pilot = V6RemoteDelegatedResourcePilot.CreateForQualification(Lease, Current,
            new(1, 100, 60, 0, 0), ProviderGeneration).Value!;
        var owner = new V6RemoteLeaseOwnerEndpoint(pilot);
        return (pilot, owner, new(owner, requireJournaledSubmit: journaled));
    }

    private static RemoteEffectClosureV1 Closure() =>
        new(1, Lease.LeaseId, Lease.OwnerEpoch, Lease.LeaseGeneration,
            ProviderGeneration, 23, true, true);

    private static V6RemoteLeaseRecoveryJournal Journal(IResourceBudgetJournalStore store) =>
        new(store, Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray(),
            Guid.Parse("ae81a933-f210-460c-b883-57008316fa40"));

    private static V6RemoteLeaseRecoveryPayload IssuedPayload() =>
        new(V6RemoteLeaseRecoveryTransition.Issued, Lease.OwnerHostIdentity,
            Lease.OwnerIncarnation, Lease.OwnerEpoch, Lease,
            new(1, 100, 60, 0, 0), ProviderGeneration);

    private sealed class MemoryStore : IResourceBudgetJournalStore
    {
        private readonly List<byte[]> frames = [];
        internal bool FailNextAppend { get; set; }
        public IReadOnlyList<byte[]> ReadFrames() => frames.Select(static value => value.ToArray()).ToArray();
        public void AppendFrame(ReadOnlySpan<byte> frame)
        {
            if (FailNextAppend)
            {
                FailNextAppend = false;
                throw new IOException("injected owner journal failure");
            }
            frames.Add(frame.ToArray());
        }
    }

    private static readonly RemoteAuthorityLeaseV1 Lease = new(1,
        Guid.Parse("690c6048-a963-4ae0-a687-f2ee6adbd44d"),
        "host:owner", 3, "host:remote", 5, new('c', 64),
        RemoteLeaseRightsV1.Read | RemoteLeaseRightsV1.Execute | RemoteLeaseRightsV1.StagedWrite,
        7, 11, 13, 17);
    private static readonly RemoteLeaseCurrentStateV1 Current = new(3, 5, 7, 11, 13, false, false);
    private const ulong ProviderGeneration = 19;
}
