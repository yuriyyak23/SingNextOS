using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    internal KernelResult BindComputeCopyInvocation(EndpointSessionInvocationHandle invocation, ProcessHandle service, object original)
    {
        KernelResult bound =SessionInvocations.BindComputeCopy(invocation,service,original);
        if(!bound.IsSuccess)return bound;
        KernelResult<EndpointSessionRegistry.Record> session =EndpointSessions.ResolveForService(invocation.Session,service);
        if(!session.IsSuccess)return KernelResult.Fail(session.Error,session.Message!);
        KernelResult<ResponseRegistry.OriginalPublication> response;
        lock(_requestResponseCorrelationGate)response=Responses.CapturePublication(service,session.Value!.ServiceEndpoint,invocation.InvocationId.Value);
        return response.IsSuccess ? SessionInvocations.BindOriginalResponse(invocation,service,response.Value!) : KernelResult.Fail(response.Error,response.Message!);
    }

    internal KernelResult<ResponseEnvelope> ObserveComputeClosedResponse(EndpointSessionInvocationHandle invocation, ProcessHandle service, object original)
    {
        KernelResult custody = ValidateComputeCopyInvocationOwner(invocation, service, original);
        if (!custody.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(custody.Error, custody.Message!);
        KernelResult<EndpointSessionRegistry.Record> session =EndpointSessions.ResolveForService(invocation.Session,service,allowClosedCleanup:true);
        if(!session.IsSuccess)return KernelResult<ResponseEnvelope>.Fail(session.Error,session.Message!);
        if(session.Value!.State==EndpointSessionState.Active)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.InvalidTransition,"Active session is not a closed response fact.");
        KernelResult<ResponseRegistry.OriginalPublication> failed = SessionInvocations.ReadFailedOriginalResponse(invocation, service);
        bool retainedOwnership;
        lock (_requestResponseCorrelationGate)
            retainedOwnership = failed.IsSuccess && Responses.HasRetainedOwnershipResponse(failed.Value!);
        if (retainedOwnership)
        {
            KernelResult<object> input;
            KernelResult<ResponseEnvelope> terminal;
            lock (_requestResponseCorrelationGate)
            {
                terminal = Responses.ReadFailedTransferCompletion(failed.Value!);
                input = Responses.ReadFailedOwnershipInput(failed.Value!);
            }
            if (terminal.IsSuccess)
            {
                KernelResult reconciled = SessionInvocations.CompleteFailedOriginalResponse(invocation, service, failed.Value!, terminal.Value!);
                return reconciled.IsSuccess ? terminal : KernelResult<ResponseEnvelope>.Fail(reconciled.Error, reconciled.Message!);
            }
            if (!input.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(input.Error, input.Message!);
            return CleanupFailedSessionOwnershipResponse(service, invocation, input.Value!);
        }
        return SessionInvocations.ObserveComputeChannelCancellation(invocation,service,original);
    }

    internal bool HasComputeFailedResponseCleanup(EndpointSessionInvocationHandle invocation, ProcessHandle service, object original) =>
        SessionInvocations.HasComputeFailedResponseCleanup(invocation, service, original);

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
        KernelResult<EndpointSessionRegistry.Record> session = EndpointSessions.ResolveForService(invocation.Session, service);
        if (!session.IsSuccess) return KernelResult.Fail(session.Error, session.Message!);
        return SessionInvocations.AcceptInvocation(invocation, service, allowInFlightCancellation);
    }

    public KernelResult AcceptSessionCancellation(
        ProcessHandle service,
        EndpointSessionInvocationHandle invocation)
    {
        KernelResult<EndpointSessionRegistry.Record> session = EndpointSessions.ResolveForService(invocation.Session, service);
        if (!session.IsSuccess) return KernelResult.Fail(session.Error, session.Message!);
        return SessionInvocations.AcceptCancellation(invocation, service);
    }
}
