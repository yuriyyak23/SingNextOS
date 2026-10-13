using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    internal KernelResult BindComputeCopyInvocation(EndpointSessionInvocationHandle invocation, ProcessHandle service, object original) =>
        SessionInvocations.BindComputeCopy(invocation, service, original);

    internal KernelResult ConsumeComputeCopyInvocationClosure(EndpointSessionInvocationHandle invocation, ProcessHandle service, object original) =>
        SessionInvocations.ConsumeComputeCopyClosure(invocation, service, original);

    public KernelResult AcceptSessionInvocation(
        ProcessHandle service,
        EndpointSessionInvocationHandle invocation,
        bool allowInFlightCancellation)
    {
        var session = EndpointSessions.ResolveForService(invocation.Session, service);
        if (!session.IsSuccess) return KernelResult.Fail(session.Error, session.Message!);
        return SessionInvocations.AcceptInvocation(invocation, service, allowInFlightCancellation);
    }

    public KernelResult AcceptSessionCancellation(
        ProcessHandle service,
        EndpointSessionInvocationHandle invocation)
    {
        var session = EndpointSessions.ResolveForService(invocation.Session, service);
        if (!session.IsSuccess) return KernelResult.Fail(session.Error, session.Message!);
        return SessionInvocations.AcceptCancellation(invocation, service);
    }
}
