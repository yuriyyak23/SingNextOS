using SingPlus.Contracts;
using SingPlus.Sip.Drivers;

namespace SingPlus.Runtime;

/// <summary>
/// Privileged composition for one exact IRQ route from a component DeviceResourceSet.
/// The EndpointSession transports only committed Sing event evidence and never carries
/// the underlying device, IRQ-binding, MMIO, DMA, or provider authority.
/// </summary>
public sealed class RuntimeDriverInterruptServiceHost
{
    private readonly RuntimeKernel _kernel;
    private readonly ProcessHandle _service;
    private readonly EndpointSessionHandle _session;
    private readonly IrqResourceGrant _irq;
    private int _processing;
    private EndpointSessionInvocationHandle? _pending;
    private object? _pendingResponse;
    private EndpointSessionInvocationRegistry.OriginalPublication? _publication;

    public KernelResult ReconcileLastPublication()
        => _publication is { } original ? _kernel.ReadOriginalSessionPublication(original) :
            KernelResult.Fail(KernelError.ResponseNotPending,"No captured original publication; absence is not settlement.");

    private RuntimeDriverInterruptServiceHost(
        RuntimeKernel kernel,
        ProcessHandle service,
        EndpointSessionHandle session,
        IrqResourceGrant irq)
    {
        _kernel = kernel;
        _service = service;
        _session = session;
        _irq = irq;
    }

    public static KernelResult<RuntimeDriverInterruptServiceHost> CreateForComponent(
        RuntimeKernel kernel,
        ComponentIdentity component,
        EndpointSessionHandle session,
        string irqCapabilityResourceId)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        if (string.IsNullOrWhiteSpace(irqCapabilityResourceId))
            return KernelResult<RuntimeDriverInterruptServiceHost>.Fail(KernelError.InvalidManifest, "Exact driver IRQ resource identity is required.");

        var authority = kernel.ResolveDriverComponentAuthority(component);
        if (!authority.IsSuccess)
            return KernelResult<RuntimeDriverInterruptServiceHost>.Fail(authority.Error, authority.Message!);
        var resolvedSession = kernel.EndpointSessions.ResolveForService(session, authority.Value.Process);
        if (!resolvedSession.IsSuccess)
            return KernelResult<RuntimeDriverInterruptServiceHost>.Fail(resolvedSession.Error, resolvedSession.Message!);

        var irq = authority.Value.Resources.Irqs.SingleOrDefault(candidate =>
            string.Equals(candidate.CapabilityResourceId, irqCapabilityResourceId, StringComparison.Ordinal));
        if (irq is null)
            return KernelResult<RuntimeDriverInterruptServiceHost>.Fail(KernelError.MissingCapability, "The component DeviceResourceSet does not contain the requested IRQ route.");

        return KernelResult<RuntimeDriverInterruptServiceHost>.Ok(
            new RuntimeDriverInterruptServiceHost(kernel, authority.Value.Process, session, irq));
    }

    public KernelResult ProcessNext()
    {
        if(Interlocked.CompareExchange(ref _processing,1,0)!=0)
            return KernelResult.Fail(KernelError.InvalidTransition,"Original IRQ caller already processing.");
        try
        {
            if(_pending is { } original)
            {
                if(_pendingResponse is null)return KernelResult.Fail(KernelError.PlatformFaulted,"Original Poll outcome unresolved; no repeat.");
                var settled=_publication is { } captured?_kernel.ReadOriginalSessionPublication(captured):
                    _kernel.ReadOriginalSessionPublication(original,_service,_pendingResponse);
                if(settled.Error==KernelError.ResponseNotDelivered)
                {
                    var replay=_kernel.PublishSessionResponse(_service,original,_pendingResponse);
                    settled=replay.IsSuccess?KernelResult.Ok():KernelResult.Fail(replay.Error,replay.Message!);
                }
                if(settled.IsSuccess){_pending=null;_pendingResponse=null;}
                return settled;
            }
            return ProcessNew();
        }
        finally {Volatile.Write(ref _processing,0);}
    }

    private KernelResult ProcessNew()
    {
        var received = _kernel.ReceiveSessionRequest(_service, _session);
        if (!received.IsSuccess) return KernelResult.Fail(received.Error, received.Message!);
        var request = received.Value!;
        if (request.Request.MessageId != IDriverInterruptServiceProtocol.Message_PollAsync)
            return SettleUnavailable(request.Invocation, KernelError.InvalidMessage, "Driver interrupt service received an unexpected message.");

        if (request.CancellationRequested)
        {
            var accepted = _kernel.AcceptSessionCancellation(_service, request.Invocation);
            if (!accepted.IsSuccess) return accepted;
            var cancelled = _kernel.CancelSessionResponse(_service, request.Invocation);
            return cancelled.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(cancelled.Error, cancelled.Message!);
        }

        var serviceAccepted = _kernel.AcceptSessionInvocation(
            _service,
            request.Invocation,
            allowInFlightCancellation: false);
        if (!serviceAccepted.IsSuccess)
        {
            if (serviceAccepted.Error == KernelError.InvalidTransition)
            {
                var cancellation = _kernel.QuerySessionCancellation(_service, request.Invocation);
                if (cancellation.IsSuccess && cancellation.Value)
                {
                    var accepted = _kernel.AcceptSessionCancellation(_service, request.Invocation);
                    if (!accepted.IsSuccess) return accepted;
                    var cancelled = _kernel.CancelSessionResponse(_service, request.Invocation);
                    return cancelled.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(cancelled.Error, cancelled.Message!);
                }
            }
            return serviceAccepted;
        }

        _pending=request.Invocation; // Caller original before possible Poll effect.
        var observed = _kernel.PollPlatformInterrupt(_service, _irq.Binding);
        if (!observed.IsSuccess)
            return SettleUnavailable(request.Invocation, observed.Error, observed.Message!);

        var delivery = observed.Value!;
        var response = delivery.DeliveryAvailable && delivery.Event is { } @event
            ? new DriverInterruptPollResponse(DriverInterruptPollStatus.Delivered, @event)
            : new DriverInterruptPollResponse(DriverInterruptPollStatus.NoDelivery, default);
        _pendingResponse=response; // Retain exact boxed payload before publication callbacks.
        var captured=_kernel.CaptureOriginalSessionPublication(request.Invocation,_service,_pendingResponse);
        if(!captured.IsSuccess)return KernelResult.Fail(captured.Error,captured.Message!);
        _publication=captured.Value;
        var published = _kernel.PublishSessionResponse(_service, request.Invocation, _pendingResponse);
        if(published.IsSuccess){_pending=null;_pendingResponse=null;}
        return published.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(published.Error, published.Message!);
    }

    private KernelResult SettleUnavailable(
        EndpointSessionInvocationHandle invocation,
        KernelError error,
        string message)
    {
        var published = _kernel.PublishSessionResponse(
            _service,
            invocation,
            new DriverInterruptPollResponse(DriverInterruptPollStatus.ResourceUnavailable, default));
        return published.IsSuccess
            ? KernelResult.Fail(error, message)
            : KernelResult.Fail(published.Error, published.Message!);
    }
}

public sealed partial class RuntimeKernel
{
    internal KernelResult<EndpointSessionInvocationRegistry.OriginalPublication> CaptureOriginalSessionPublication(EndpointSessionInvocationHandle invocation,ProcessHandle service,object payload)
        => SessionInvocations.CapturePublication(invocation,service,payload);
    internal KernelResult ReadOriginalSessionPublication(EndpointSessionInvocationRegistry.OriginalPublication original)
    {
        var known=SessionInvocations.ReadPublication(original);
        if(known.IsSuccess)return known;
        var response=SessionInvocations.OriginalResponse(original);
        if(response is null)return known;
        KernelResult<ResponseEnvelope> committed;
        bool payloadMatches;
        lock(_requestResponseCorrelationGate)
        {
            committed=Responses.ReadPublication(response);
            payloadMatches=committed.IsSuccess && Responses.MatchesOriginalPublicationPayload(response,original.Payload,committed.Value!);
        }
        return committed.IsSuccess && payloadMatches?SessionInvocations.ReconcileResponse(original,committed.Value!,true):known;
    }
    internal KernelResult ReadOriginalSessionPublication(EndpointSessionInvocationHandle invocation,ProcessHandle service,object originalPayload)
        => SessionInvocations.ReadPublishedOutcome(invocation,service,originalPayload);

    internal KernelResult ReadOriginalSessionChannelCancellation(EndpointSessionInvocationRegistry.OriginalPublication original)
    {
        lock(_requestResponseCorrelationGate)
        {
            var response=SessionInvocations.OriginalResponse(original);
            if(response is null)return KernelResult.Fail(KernelError.ResponseNotPending,"Original response cancellation fact absent.");
            var cancelled=Responses.ReadOriginalChannelCancellation(response);
            return cancelled.IsSuccess ? SessionInvocations.ReconcileOriginalChannelCancellation(original,cancelled.Value!) :
                KernelResult.Fail(cancelled.Error,cancelled.Message!);
        }
    }
}
