using SingPlus.Contracts;

namespace SingPlus.Sip.Sdk;

// TCB-private invocation identity. Possession is not admission: generated sentry
// targets must revalidate through the existing runtime owners on every call.
public readonly struct TrustedSipInvocationContext
{
    internal TrustedSipInvocationContext(ProcessHandle caller, ProcessHandle service, EndpointSessionHandle session, EndpointSessionInvocationHandle invocation) =>
        (Caller, Service, Session, Invocation) = (caller, service, session, invocation);

    internal ProcessHandle Caller { get; }
    internal ProcessHandle Service { get; }
    internal EndpointSessionHandle Session { get; }
    internal EndpointSessionInvocationHandle Invocation { get; }
}

// A typed carrier between an operation-specific generated sentry and its trusted
// runtime target. ErrorCode is diagnostic/result mapping, never authority evidence.
public readonly struct GeneratedSipSentryResult<T>
{
    private GeneratedSipSentryResult(bool isSuccess, T value, int errorCode, string? message) =>
        (IsSuccess, Value, ErrorCode, Message) = (isSuccess, value, errorCode, message);

    internal bool IsSuccess { get; }
    internal T Value { get; }
    internal int ErrorCode { get; }
    internal string? Message { get; }

    internal static GeneratedSipSentryResult<T> Success(T value) => new(true, value, 0, null);
    internal static GeneratedSipSentryResult<T> Failure(int errorCode, string message) =>
        new(false, default!, errorCode, message);
}

public readonly struct GeneratedSipUnit;
