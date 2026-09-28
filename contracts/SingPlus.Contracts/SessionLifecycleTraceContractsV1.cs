using System.Security.Cryptography;
using System.Text;

namespace SingPlus.Contracts;

public enum SessionLifecycleTraceCauseV1 : byte
{
    Opened = 1,
    ExplicitClose = 2,
    Expired = 3,
    ProcessTeardown = 4,
    PinReleased = 5,
}

/// <summary>Non-authoritative observation of an EndpointSessionRegistry transition.</summary>
public readonly record struct SessionLifecycleTraceEventV1(
    ushort Version,
    EndpointSessionHandle Session,
    ulong Sequence,
    EndpointSessionState State,
    SessionLifecycleTraceCauseV1 Cause)
{
    public const ushort CurrentVersion = 1;
    public bool AuthorizesExecution => false;
    public bool AuthorizesClosure => false;

    public SessionLifecycleTraceEventV1 Validate()
    {
        if (Version != CurrentVersion || Session.SessionId.Value == 0 ||
            Session.Generation.Value == 0 || Sequence == 0 ||
            State is not (EndpointSessionState.Active or EndpointSessionState.Draining or EndpointSessionState.Closed) ||
            !Enum.IsDefined(Cause) ||
            State == EndpointSessionState.Active && Cause != SessionLifecycleTraceCauseV1.Opened ||
            State == EndpointSessionState.Draining &&
            Cause is SessionLifecycleTraceCauseV1.Opened or SessionLifecycleTraceCauseV1.PinReleased ||
            State == EndpointSessionState.Closed && Cause == SessionLifecycleTraceCauseV1.Opened)
            throw new NotSupportedException("Session lifecycle observation is malformed or unsupported.");
        return this;
    }
}

public enum SessionLifecycleTraceStatusV1 : byte
{
    Valid = 1,
    InvalidEvent = 2,
    MixedSession = 3,
    NonContiguousSequence = 4,
    InvalidTransition = 5,
    EventAfterClose = 6,
}

public sealed record SessionLifecycleTraceCounterexampleV1(
    SessionLifecycleTraceStatusV1 Status, int EventIndex, string CanonicalId)
{
    public bool AuthorizesClosure => false;
}

public sealed record SessionLifecycleTraceValidationResultV1(
    SessionLifecycleTraceStatusV1 Status, SessionLifecycleTraceCounterexampleV1? Counterexample)
{
    public bool IsValid => Status == SessionLifecycleTraceStatusV1.Valid;
}

public static class SessionLifecycleTraceValidatorV1
{
    public static SessionLifecycleTraceValidationResultV1 Validate(
        IEnumerable<SessionLifecycleTraceEventV1> trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var events = trace.ToArray();
        if (events.Length == 0) return Fail(SessionLifecycleTraceStatusV1.InvalidTransition, 0, default);
        EndpointSessionHandle? session = null;
        ulong sequence = 0;
        EndpointSessionState? state = null;
        for (var index = 0; index < events.Length; index++)
        {
            var item = events[index];
            try { item.Validate(); }
            catch (NotSupportedException)
            { return Fail(SessionLifecycleTraceStatusV1.InvalidEvent, index, item); }
            if (session is { } previousSession && previousSession != item.Session)
                return Fail(SessionLifecycleTraceStatusV1.MixedSession, index, item);
            if (sequence == ulong.MaxValue || item.Sequence != sequence + 1)
                return Fail(SessionLifecycleTraceStatusV1.NonContiguousSequence, index, item);
            if (state == EndpointSessionState.Closed)
                return Fail(SessionLifecycleTraceStatusV1.EventAfterClose, index, item);
            var allowed = state switch
            {
                null => item.State == EndpointSessionState.Active,
                EndpointSessionState.Active => item.State is EndpointSessionState.Draining or EndpointSessionState.Closed &&
                    item.Cause != SessionLifecycleTraceCauseV1.PinReleased,
                EndpointSessionState.Draining => item.State == EndpointSessionState.Closed,
                _ => false,
            };
            if (!allowed) return Fail(SessionLifecycleTraceStatusV1.InvalidTransition, index, item);
            session = item.Session;
            sequence = item.Sequence;
            state = item.State;
        }
        return new(SessionLifecycleTraceStatusV1.Valid, null);
    }

    private static SessionLifecycleTraceValidationResultV1 Fail(
        SessionLifecycleTraceStatusV1 status, int index, SessionLifecycleTraceEventV1 item)
    {
        var payload = FormattableString.Invariant(
            $"session-trace/v1|{(byte)status}|{index}|{item.Session.SessionId.Value}|{item.Session.Generation.Value}|{item.Sequence}|{(int)item.State}|{(byte)item.Cause}");
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        return new(status, new(status, index, id));
    }
}

public enum SessionLifecycleComparisonStatusV1 : byte
{
    Equivalent = 1,
    InvalidReference = 2,
    InvalidCandidate = 3,
    LengthMismatch = 4,
    StateMismatch = 5,
}

public sealed record SessionLifecycleDifferenceV1(
    SessionLifecycleComparisonStatusV1 Status,
    int EventIndex,
    string ReferenceTupleDigest,
    string CandidateTupleDigest,
    string CanonicalId)
{
    public bool AuthorizesClosure => false;
}

public sealed record SessionLifecycleComparisonResultV1(
    SessionLifecycleComparisonStatusV1 Status, SessionLifecycleDifferenceV1? Difference)
{
    public bool IsEquivalent => Status == SessionLifecycleComparisonStatusV1.Equivalent;
}

public static class SessionLifecycleTraceDifferentialV1
{
    public static SessionLifecycleComparisonResultV1 CompareAllowedStates(
        IEnumerable<SessionLifecycleTraceEventV1> reference,
        IEnumerable<SessionLifecycleTraceEventV1> candidate,
        string referenceTupleDigest,
        string candidateTupleDigest)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateDigest(referenceTupleDigest);
        ValidateDigest(candidateTupleDigest);
        var left = reference.ToArray();
        var right = candidate.ToArray();
        var leftResult = SessionLifecycleTraceValidatorV1.Validate(left);
        if (!leftResult.IsValid)
            return Fail(SessionLifecycleComparisonStatusV1.InvalidReference,
                leftResult.Counterexample!.EventIndex, leftResult.Counterexample.CanonicalId);
        var rightResult = SessionLifecycleTraceValidatorV1.Validate(right);
        if (!rightResult.IsValid)
            return Fail(SessionLifecycleComparisonStatusV1.InvalidCandidate,
                rightResult.Counterexample!.EventIndex, rightResult.Counterexample.CanonicalId);
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            if (left[index].State != right[index].State)
                return Fail(SessionLifecycleComparisonStatusV1.StateMismatch, index,
                    FormattableString.Invariant($"{(int)left[index].State}|{(int)right[index].State}"));
        if (left.Length != right.Length)
            return Fail(SessionLifecycleComparisonStatusV1.LengthMismatch,
                Math.Min(left.Length, right.Length), FormattableString.Invariant($"{left.Length}|{right.Length}"));
        return new(SessionLifecycleComparisonStatusV1.Equivalent, null);

        SessionLifecycleComparisonResultV1 Fail(
            SessionLifecycleComparisonStatusV1 status, int index, string detail)
        {
            var payload = FormattableString.Invariant(
                $"session-differential/v1|{(byte)status}|{index}|{referenceTupleDigest}|{candidateTupleDigest}|{detail}");
            var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            return new(status, new(status, index, referenceTupleDigest, candidateTupleDigest, id));
        }
    }

    private static void ValidateDigest(string? value)
    {
        if (value is null || value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Source tuple digest must be canonical SHA-256 hex.");
    }
}
