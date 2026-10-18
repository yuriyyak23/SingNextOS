using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    internal KernelResult BindComputeCopyInvocation(EndpointSessionInvocationHandle invocation, ProcessHandle service, object original)
    {
        var bound=SessionInvocations.BindComputeCopy(invocation,service,original);
        if(!bound.IsSuccess)return bound;
        var session=EndpointSessions.ResolveForService(invocation.Session,service);
        if(!session.IsSuccess)return KernelResult.Fail(session.Error,session.Message!);
        KernelResult<ResponseRegistry.OriginalPublication> response;
        lock(_requestResponseCorrelationGate)response=Responses.CapturePublication(service,session.Value!.ServiceEndpoint,invocation.InvocationId.Value);
        return response.IsSuccess ? SessionInvocations.BindOriginalResponse(invocation,service,response.Value!) : KernelResult.Fail(response.Error,response.Message!);
    }

    internal KernelResult<ResponseEnvelope> ObserveComputeClosedResponse(EndpointSessionInvocationHandle invocation, ProcessHandle service, object original)
    {
        var session=EndpointSessions.ResolveForService(invocation.Session,service,allowClosedCleanup:true);
        if(!session.IsSuccess)return KernelResult<ResponseEnvelope>.Fail(session.Error,session.Message!);
        if(session.Value!.State==EndpointSessionState.Active)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.InvalidTransition,"Active session is not a closed response fact.");
        return SessionInvocations.ObserveComputeChannelCancellation(invocation,service,original);
    }

    internal KernelResult ConsumeComputeCopyInvocationClosure(EndpointSessionInvocationHandle invocation, ProcessHandle service, object original) =>
        SessionInvocations.ConsumeComputeCopyClosure(invocation, service, original);

    internal KernelResult ValidateComputeCopyInvocationOwner(EndpointSessionInvocationHandle invocation, ProcessHandle service, object original) =>
        SessionInvocations.ValidateComputeCopyOwner(invocation, service, original);

    internal bool HasComputeCopyResponseSettlement(EndpointSessionInvocationHandle invocation, ProcessHandle service, object original, bool cancelledOnly = false) =>
        SessionInvocations.HasComputeResponseSettlement(invocation, service, original, cancelledOnly);

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
