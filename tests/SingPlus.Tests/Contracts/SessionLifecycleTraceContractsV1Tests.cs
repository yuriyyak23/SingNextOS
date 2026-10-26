using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class SessionLifecycleTraceContractsV1Tests
{
    private static readonly EndpointSessionHandle Session = new(new(7), new(3));

    [Fact]
    public void ValidLifecycleRejectsMissingOrReorderedTransitions()
    {
        SessionLifecycleTraceEventV1 active = Event(1, EndpointSessionState.Active, SessionLifecycleTraceCauseV1.Opened);
        SessionLifecycleTraceEventV1 draining = Event(2, EndpointSessionState.Draining, SessionLifecycleTraceCauseV1.Expired);
        SessionLifecycleTraceEventV1 closed = Event(3, EndpointSessionState.Closed, SessionLifecycleTraceCauseV1.PinReleased);

        Assert.True(SessionLifecycleTraceValidatorV1.Validate([active, draining, closed]).IsValid);
        Assert.Equal(SessionLifecycleTraceStatusV1.InvalidTransition,
            SessionLifecycleTraceValidatorV1.Validate([active, closed with
            { Sequence = 2, Cause = SessionLifecycleTraceCauseV1.PinReleased }]).Status);
        Assert.Equal(SessionLifecycleTraceStatusV1.NonContiguousSequence,
            SessionLifecycleTraceValidatorV1.Validate([active, closed]).Status);
        Assert.Equal(SessionLifecycleTraceStatusV1.EventAfterClose,
            SessionLifecycleTraceValidatorV1.Validate([active,
                Event(2, EndpointSessionState.Closed, SessionLifecycleTraceCauseV1.ExplicitClose),
                closed]).Status);
        Assert.Equal(SessionLifecycleTraceStatusV1.MixedSession,
            SessionLifecycleTraceValidatorV1.Validate([active, draining with
            { Session = Session with { Generation = new(4) } }]).Status);
    }

    [Fact]
    public void DifferentialCounterexampleIsTupleBoundAndCannotAuthorizeClosure()
    {
        SessionLifecycleTraceEventV1 active = Event(1, EndpointSessionState.Active, SessionLifecycleTraceCauseV1.Opened);
        SessionLifecycleTraceEventV1[] reference = new[] { active,
            Event(2, EndpointSessionState.Closed, SessionLifecycleTraceCauseV1.ExplicitClose) };
        SessionLifecycleTraceEventV1[] candidate = new[] { active,
            Event(2, EndpointSessionState.Draining, SessionLifecycleTraceCauseV1.ProcessTeardown) };
        SessionLifecycleComparisonResultV1 first = SessionLifecycleTraceDifferentialV1.CompareAllowedStates(reference, candidate,
            new string('a', 64), new string('b', 64));
        SessionLifecycleComparisonResultV1 repeated = SessionLifecycleTraceDifferentialV1.CompareAllowedStates(reference, candidate,
            new string('a', 64), new string('b', 64));
        SessionLifecycleComparisonResultV1 rebound = SessionLifecycleTraceDifferentialV1.CompareAllowedStates(reference, candidate,
            new string('a', 64), new string('c', 64));

        Assert.Equal(SessionLifecycleComparisonStatusV1.StateMismatch, first.Status);
        Assert.Equal(first.Difference!.CanonicalId, repeated.Difference!.CanonicalId);
        Assert.NotEqual(first.Difference.CanonicalId, rebound.Difference!.CanonicalId);
        Assert.False(first.Difference.AuthorizesClosure);
        Assert.Throws<ArgumentException>(() => SessionLifecycleTraceDifferentialV1.CompareAllowedStates(
            reference, candidate, new string('A', 64), new string('b', 64)));
    }

    private static SessionLifecycleTraceEventV1 Event(ulong sequence, EndpointSessionState state,
        SessionLifecycleTraceCauseV1 cause) =>
        new(SessionLifecycleTraceEventV1.CurrentVersion, Session, sequence, state, cause);
}
