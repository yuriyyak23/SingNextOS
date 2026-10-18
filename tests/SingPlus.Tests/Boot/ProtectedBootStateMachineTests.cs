using SingNext.Boot.Core;

namespace SingPlus.Tests.Boot;

public sealed class ProtectedBootStateMachineTests
{
    [Fact]
    public void P15_04_CapsuleAndImageDomainsCannotCrossConfirm()
    {
        var capsule = new ProtectedBootStateMachine(BootStateDomain.Capsule);
        var image = new ProtectedBootStateMachine(BootStateDomain.Image);
        var nonce = Guid.NewGuid();
        var trial = capsule.BeginTrial(Initial(BootStateDomain.Capsule), BootSlot.B, 2, nonce, 2).Value!;
        trial = capsule.RecordTrialAttempt(trial).Value!;

        Assert.Equal(BootFailure.SecurityPolicyDenied, image.Confirm(trial, BootStateDomain.Image, 2, nonce).Failure);
        Assert.True(capsule.Confirm(trial, BootStateDomain.Capsule, 2, nonce).IsSuccess);
    }

    [Fact]
    public void P15_04_KernelEntryDoesNotConfirmAndAttemptsOnlyDecrease()
    {
        var machine = new ProtectedBootStateMachine(BootStateDomain.Image);
        var nonce = Guid.NewGuid();
        var trial = machine.BeginTrial(Initial(BootStateDomain.Image), BootSlot.B, 2, nonce, 2).Value!;

        Assert.Equal(BootFailure.SecurityPolicyDenied, machine.Confirm(trial, BootStateDomain.Image, 2, nonce).Failure);
        var attempted = machine.RecordTrialAttempt(trial).Value!;
        Assert.Equal(1, attempted.TrialAttemptsRemaining);
        Assert.Equal(BootFailure.SecurityPolicyDenied, machine.Confirm(attempted, BootStateDomain.Image, 2, Guid.NewGuid()).Failure);
        var confirmed = machine.Confirm(attempted, BootStateDomain.Image, 2, nonce).Value!;
        Assert.Equal(2UL, confirmed.RollbackFloor);
        Assert.Null(confirmed.TrialGeneration);
    }

    [Fact]
    public void P15_04_TornWriteIsIgnoredAndSplitBrainFailsClosed()
    {
        var machine = new ProtectedBootStateMachine(BootStateDomain.Image);
        var committed = Initial(BootStateDomain.Image);
        var torn = committed with { DurableSequence = 2, ConfirmedGeneration = 2, Committed = false };
        Assert.Equal(committed, machine.ResolveReplicas([committed, torn]).Value);

        var conflicting = committed with { ConfirmedSlot = BootSlot.B };
        Assert.Equal(BootFailure.AmbiguousState, machine.ResolveReplicas([committed, conflicting]).Failure);
    }

    [Fact]
    public void P15_04_ExhaustedTrialCannotAttemptOrConfirm()
    {
        var machine = new ProtectedBootStateMachine(BootStateDomain.Image);
        var nonce = Guid.NewGuid();
        var trial = machine.BeginTrial(Initial(BootStateDomain.Image), BootSlot.B, 2, nonce, 1).Value!;
        var exhausted = machine.RecordTrialAttempt(trial).Value!;
        Assert.Equal(0, exhausted.TrialAttemptsRemaining);
        Assert.Equal(BootFailure.SecurityPolicyDenied, machine.RecordTrialAttempt(exhausted).Failure);
    }

    [Fact]
    public void P15_04_UnconfirmedTrialRemainsUnconfirmedAfterDurableReplicaResolution()
    {
        var machine = new ProtectedBootStateMachine(BootStateDomain.Image);
        var nonce = Guid.NewGuid();
        var trial = machine.BeginTrial(Initial(BootStateDomain.Image), BootSlot.B, 2, nonce, 2).Value!;
        var attempted = machine.RecordTrialAttempt(trial).Value! with { Committed = true, IntegrityTag = new byte[] { 7 } };

        var recovered = machine.ResolveReplicas([attempted]);

        Assert.True(recovered.IsSuccess);
        Assert.Equal(BootSlot.A, recovered.Value.ConfirmedSlot);
        Assert.Equal(1UL, recovered.Value.ConfirmedGeneration);
        Assert.Equal(2UL, recovered.Value.TrialGeneration);
        Assert.True(recovered.Value.TrialAttempted);
    }

    [Theory]
    [InlineData(0, BootFailure.Timeout)]
    [InlineData(1, BootFailure.Timeout)]
    [InlineData(2, BootFailure.ReadbackMismatch)]
    [InlineData(3, BootFailure.Timeout)]
    [InlineData(4, BootFailure.SecurityPolicyDenied)]
    public void P15_04_DurableProtocolFailsAtEveryCrashBoundary(int failStage, BootFailure expected)
    {
        var machine = new ProtectedBootStateMachine(BootStateDomain.Image);
        var candidate = machine.BeginTrial(Initial(BootStateDomain.Image), BootSlot.B, 2, Guid.NewGuid(), 2).Value!;

        var result = machine.Persist(new FakeProtectedStore(failStage), candidate, new(1, 1));

        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    public void P15_04_DurableProtocolVerifiesCandidateBeforeAtomicCommitPublication()
    {
        var machine = new ProtectedBootStateMachine(BootStateDomain.Image);
        var candidate = machine.BeginTrial(Initial(BootStateDomain.Image), BootSlot.B, 2, Guid.NewGuid(), 2).Value!;
        var store = new FakeProtectedStore(-1);

        var result = machine.Persist(store, candidate, new(1, 1));

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Committed);
        Assert.Equal(["write", "flush", "verify", "commit", "read"], store.Operations);
    }

    private static ProtectedBootEnvelope Initial(BootStateDomain domain) =>
        new(1, domain, BootSlot.A, 1, 1, null, null, 1, Guid.Empty, 0, false, 1, true, new byte[] { 1 });

    private sealed class FakeProtectedStore(int failStage) : IBootProtectedState
    {
        private ProtectedBootEnvelope _candidate;
        public List<string> Operations { get; } = [];

        public BootFailure WriteCandidate(ProtectedBootEnvelope candidate, BootResetSnapshot reset)
        {
            Operations.Add("write");
            if (failStage == 0) return BootFailure.Timeout;
            _candidate = candidate;
            return BootFailure.None;
        }

        public BootFailure FlushCandidate(BootStateDomain domain, ulong durableSequence, BootResetSnapshot reset)
        {
            Operations.Add("flush");
            return failStage == 1 ? BootFailure.Timeout : BootFailure.None;
        }

        public BootResult<ProtectedBootEnvelope> ReadCandidateAuthenticated(BootStateDomain domain, ulong durableSequence, BootResetSnapshot reset)
        {
            Operations.Add("verify");
            return failStage == 2
                ? BootResult<ProtectedBootEnvelope>.Fail(BootFailure.SecurityPolicyDenied, "invalid MAC")
                : BootResult<ProtectedBootEnvelope>.Success(_candidate);
        }

        public BootFailure PublishCommitMarker(BootStateDomain domain, ulong durableSequence, BootResetSnapshot reset)
        {
            Operations.Add("commit");
            if (failStage == 3) return BootFailure.Timeout;
            _candidate = _candidate with { Committed = true };
            return BootFailure.None;
        }

        public BootResult<ProtectedBootEnvelope> ReadAuthenticated(BootStateDomain domain, BootResetSnapshot reset)
        {
            Operations.Add("read");
            return failStage == 4
                ? BootResult<ProtectedBootEnvelope>.Fail(BootFailure.SecurityPolicyDenied, "invalid MAC")
                : BootResult<ProtectedBootEnvelope>.Success(_candidate);
        }
    }
}
