using SingPlus.Contracts;

namespace SingPlus.Sip.Sdk;

// TCB-private invocation identity. Possession is not admission: generated sentry
// targets must revalidate through the existing runtime owners on every call.
public readonly struct TrustedSipInvocationContext
{
    private readonly Func<SipResourceRequirementV1, GeneratedSipResourceAdmission>? _resourceAdmission;

    internal TrustedSipInvocationContext(ProcessHandle caller, ProcessHandle service, EndpointSessionHandle session,
        EndpointSessionInvocationHandle invocation,
        Func<SipResourceRequirementV1, GeneratedSipResourceAdmission>? resourceAdmission = null)
    {
        Caller = caller;
        Service = service;
        Session = session;
        Invocation = invocation;
        _resourceAdmission = resourceAdmission;
    }

    internal ProcessHandle Caller { get; }
    internal ProcessHandle Service { get; }
    internal EndpointSessionHandle Session { get; }
    internal EndpointSessionInvocationHandle Invocation { get; }
    internal GeneratedSipResourceAdmission EnterResourceAdmission(SipResourceRequirementV1 requirement) =>
        _resourceAdmission is null
            ? GeneratedSipResourceAdmission.Failure(1, "No live resource admission resolver is bound to this invocation.")
            : _resourceAdmission(requirement);
}

// Publicly observable status is diagnostic only. The cleanup action can only be
// installed by the trusted runtime through TrustedSipInvocationContext.
public sealed class GeneratedSipResourceAdmission : IDisposable
{
    private IDisposable? _cleanup;
    private Func<Func<GeneratedSipSubmitResult>, GeneratedSipSubmitResult>? _submit;

    private GeneratedSipResourceAdmission(bool isSuccess, int errorCode, string? message, IDisposable? cleanup,
        Func<Func<GeneratedSipSubmitResult>, GeneratedSipSubmitResult>? submit) =>
        (IsSuccess, ErrorCode, Message, _cleanup, _submit) = (isSuccess, errorCode, message, cleanup, submit);

    public bool IsSuccess { get; }
    public int ErrorCode { get; }
    public string? Message { get; }

    internal static GeneratedSipResourceAdmission Success(IDisposable cleanup,
        Func<Func<GeneratedSipSubmitResult>, GeneratedSipSubmitResult> submit) => new(true, 0, null, cleanup, submit);
    internal static GeneratedSipResourceAdmission Failure(int errorCode, string message) => new(false, errorCode, message, null, null);
    public GeneratedSipSubmitResult Submit(Func<GeneratedSipSubmitResult> providerSubmit)
    {
        ArgumentNullException.ThrowIfNull(providerSubmit);
        var submit = Interlocked.Exchange(ref _submit, null);
        return submit is null
            ? GeneratedSipSubmitResult.Failure(1, "Generated SIP resource admission was not live or was already submitted.")
            : submit(providerSubmit);
    }
    public void Dispose() => Interlocked.Exchange(ref _cleanup, null)?.Dispose();
}

public readonly record struct GeneratedSipSubmitResult(bool IsSuccess, int ErrorCode, string? Message)
{
    public static GeneratedSipSubmitResult Ok() => new(true, 0, null);
    public static GeneratedSipSubmitResult Failure(int errorCode, string message) => new(false, errorCode, message);
}

public static class GeneratedSipResourceSentry
{
    public static GeneratedSipResourceAdmission Enter(
        in TrustedSipInvocationContext context,
        SipResourceRequirementV1 requirement) => context.EnterResourceAdmission(requirement);
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
    public static GeneratedSipSentryResult<T> Failure(int errorCode, string message) =>
        new(false, default!, errorCode, message);
}

public readonly struct GeneratedSipUnit;
