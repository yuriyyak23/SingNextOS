using System.Security.Cryptography;

namespace SingNext.Boot.Core;

public sealed class ProtectedBootStateMachine
{
    public const uint CurrentVersion = 1;

    public ProtectedBootStateMachine(BootStateDomain domain)
    {
        if (!Enum.IsDefined(domain)) throw new ArgumentOutOfRangeException(nameof(domain));
        Domain = domain;
    }

    public BootStateDomain Domain { get; }

    public BootResult<ProtectedBootEnvelope> ResolveReplicas(IReadOnlyList<ProtectedBootEnvelope> replicas)
    {
        ArgumentNullException.ThrowIfNull(replicas);
        ProtectedBootEnvelope? winner = null;
        for (var i = 0; i < replicas.Count; i++)
        {
            var candidate = replicas[i];
            if (!candidate.Committed) continue;
            if (!IsValid(candidate))
                return BootResult<ProtectedBootEnvelope>.Fail(BootFailure.Malformed, "Protected-state replica is incompatible or violates monotonic invariants.");
            if (winner is null || candidate.DurableSequence > winner.Value.DurableSequence)
            {
                winner = candidate;
                continue;
            }
            if (candidate.DurableSequence == winner.Value.DurableSequence && !SemanticEquals(candidate, winner.Value))
                return BootResult<ProtectedBootEnvelope>.Fail(BootFailure.AmbiguousState, "Highest committed protected-state replicas disagree.");
        }
        return winner is { } value
            ? BootResult<ProtectedBootEnvelope>.Success(value)
            : BootResult<ProtectedBootEnvelope>.Fail(BootFailure.NotFound, "No valid committed protected state exists.");
    }

    public BootResult<ProtectedBootEnvelope> BeginTrial(
        ProtectedBootEnvelope current, BootSlot trialSlot, ulong generation, Guid nonce, byte attempts)
    {
        if (!IsValid(current) || current.TrialGeneration is not null || nonce == Guid.Empty ||
            !Enum.IsDefined(trialSlot) || trialSlot == current.ConfirmedSlot ||
            generation < current.RollbackFloor || attempts is 0 or > BootLimits.MaxTrialAttempts)
            return BootResult<ProtectedBootEnvelope>.Fail(BootFailure.SecurityPolicyDenied, "Trial transition violates protected-state policy.");
        return BootResult<ProtectedBootEnvelope>.Success(current with
        {
            TrialSlot = trialSlot,
            TrialGeneration = generation,
            PreviousConfirmedGeneration = current.ConfirmedGeneration,
            TrialNonce = nonce,
            TrialAttemptsRemaining = attempts,
            TrialAttempted = false,
            DurableSequence = checked(current.DurableSequence + 1),
            Committed = false,
            IntegrityTag = ReadOnlyMemory<byte>.Empty,
        });
    }

    public BootResult<ProtectedBootEnvelope> RecordTrialAttempt(ProtectedBootEnvelope current)
    {
        if (!IsValid(current) || current.TrialGeneration is null || current.TrialAttemptsRemaining == 0)
            return BootResult<ProtectedBootEnvelope>.Fail(BootFailure.SecurityPolicyDenied, "No trial attempt is available.");
        return BootResult<ProtectedBootEnvelope>.Success(current with
        {
            TrialAttemptsRemaining = checked((byte)(current.TrialAttemptsRemaining - 1)),
            TrialAttempted = true,
            DurableSequence = checked(current.DurableSequence + 1),
            Committed = false,
            IntegrityTag = ReadOnlyMemory<byte>.Empty,
        });
    }

    public BootResult<ProtectedBootEnvelope> Confirm(
        ProtectedBootEnvelope current, BootStateDomain domain, ulong generation, Guid nonce)
    {
        if (!IsValid(current) || domain != Domain || current.Domain != Domain || !current.TrialAttempted ||
            current.TrialGeneration != generation || current.TrialNonce != nonce || current.TrialSlot is null)
            return BootResult<ProtectedBootEnvelope>.Fail(BootFailure.SecurityPolicyDenied, "Trial confirmation does not match an attempted trial.");
        return BootResult<ProtectedBootEnvelope>.Success(current with
        {
            ConfirmedSlot = current.TrialSlot.Value,
            ConfirmedGeneration = generation,
            RollbackFloor = Math.Max(current.RollbackFloor, generation),
            TrialSlot = null,
            TrialGeneration = null,
            PreviousConfirmedGeneration = generation,
            TrialNonce = Guid.Empty,
            TrialAttemptsRemaining = 0,
            TrialAttempted = false,
            DurableSequence = checked(current.DurableSequence + 1),
            Committed = false,
            IntegrityTag = ReadOnlyMemory<byte>.Empty,
        });
    }

    public BootResult<ProtectedBootEnvelope> Persist(IBootProtectedState store, ProtectedBootEnvelope candidate, BootResetSnapshot reset)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (!IsValid(candidate with { Committed = true }))
            return BootResult<ProtectedBootEnvelope>.Fail(BootFailure.Malformed, "Protected-state candidate is invalid.");
        var write = store.WriteCandidate(candidate with { Committed = false }, reset);
        if (write != BootFailure.None) return BootResult<ProtectedBootEnvelope>.Fail(write, "Candidate write failed.");
        var flush = store.FlushCandidate(Domain, candidate.DurableSequence, reset);
        if (flush != BootFailure.None) return BootResult<ProtectedBootEnvelope>.Fail(flush, "Candidate was not durably flushed.");
        var candidateReadback = store.ReadCandidateAuthenticated(Domain, candidate.DurableSequence, reset);
        if (!candidateReadback.IsSuccess || candidateReadback.Value.Committed ||
            !SemanticEquals(candidate with { Committed = false }, candidateReadback.Value))
            return BootResult<ProtectedBootEnvelope>.Fail(BootFailure.ReadbackMismatch, "Durable authenticated candidate did not read back exactly.");
        var commit = store.PublishCommitMarker(Domain, candidate.DurableSequence, reset);
        if (commit != BootFailure.None) return BootResult<ProtectedBootEnvelope>.Fail(commit, "Commit marker was not atomically published.");
        var readback = store.ReadAuthenticated(Domain, reset);
        if (!readback.IsSuccess)
            return BootResult<ProtectedBootEnvelope>.Fail(readback.Failure, "Committed state did not authenticate on readback.");
        var actual = readback.Value;
        if (!actual.Committed || !SemanticEquals(candidate with { Committed = true }, actual))
            return BootResult<ProtectedBootEnvelope>.Fail(BootFailure.ReadbackMismatch, "Committed state did not read back exactly.");
        return BootResult<ProtectedBootEnvelope>.Success(actual);
    }

    private bool IsValid(ProtectedBootEnvelope value) =>
        value.Version == CurrentVersion && value.Domain == Domain && Enum.IsDefined(value.ConfirmedSlot) &&
        value.ConfirmedGeneration >= value.RollbackFloor && value.TrialAttemptsRemaining <= BootLimits.MaxTrialAttempts &&
        ((value.TrialGeneration is null && value.TrialSlot is null && value.TrialNonce == Guid.Empty && value.TrialAttemptsRemaining == 0 && !value.TrialAttempted) ||
         (value.TrialGeneration is not null && value.TrialSlot is not null && value.TrialNonce != Guid.Empty &&
          value.TrialGeneration >= value.RollbackFloor && value.PreviousConfirmedGeneration == value.ConfirmedGeneration));

    private static bool SemanticEquals(ProtectedBootEnvelope left, ProtectedBootEnvelope right) =>
        left.Version == right.Version && left.Domain == right.Domain && left.ConfirmedSlot == right.ConfirmedSlot &&
        left.ConfirmedGeneration == right.ConfirmedGeneration && left.RollbackFloor == right.RollbackFloor &&
        left.TrialSlot == right.TrialSlot && left.TrialGeneration == right.TrialGeneration &&
        left.PreviousConfirmedGeneration == right.PreviousConfirmedGeneration && left.TrialNonce == right.TrialNonce &&
        left.TrialAttemptsRemaining == right.TrialAttemptsRemaining && left.TrialAttempted == right.TrialAttempted &&
        left.DurableSequence == right.DurableSequence && left.Committed == right.Committed &&
        CryptographicOperations.FixedTimeEquals(left.IntegrityTag.Span, right.IntegrityTag.Span);
}
