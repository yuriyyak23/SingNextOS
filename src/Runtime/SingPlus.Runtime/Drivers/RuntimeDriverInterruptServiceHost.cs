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

        var observed = _kernel.PollPlatformInterrupt(_service, _irq.Binding);
        if (!observed.IsSuccess)
            return SettleUnavailable(request.Invocation, observed.Error, observed.Message!);

        var delivery = observed.Value!;
        var response = delivery.DeliveryAvailable && delivery.Event is { } @event
            ? new DriverInterruptPollResponse(DriverInterruptPollStatus.Delivered, @event)
            : new DriverInterruptPollResponse(DriverInterruptPollStatus.NoDelivery, default);
        var published = _kernel.PublishSessionResponse(_service, request.Invocation, response);
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
