using SingPlus.Contracts;
using SingPlus.Sip.Virtualization;

namespace SingPlus.Runtime;

/// <summary>Dispatches one exact endpoint session without acquiring caller authority.</summary>
public sealed class RuntimeVirtualizationServiceHost
{
    private readonly RuntimeKernel _kernel;
    private readonly ProcessHandle _service;
    private readonly ProcessHandle _caller;
    private readonly EndpointSessionHandle _session;

    private RuntimeVirtualizationServiceHost(RuntimeKernel kernel, ProcessHandle service, ProcessHandle caller, EndpointSessionHandle session)
    {
        _kernel = kernel;
        _service = service;
        _caller = caller;
        _session = session;
    }

    public static KernelResult<RuntimeVirtualizationServiceHost> CreateForSession(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        var resolved = kernel.EndpointSessions.ResolveForService(session, service);
        return resolved.IsSuccess
            ? KernelResult<RuntimeVirtualizationServiceHost>.Ok(new RuntimeVirtualizationServiceHost(kernel, service, resolved.Value!.Caller, session))
            : KernelResult<RuntimeVirtualizationServiceHost>.Fail(resolved.Error, resolved.Message!);
    }

    public KernelResult ProcessNext()
    {
        var received = _kernel.ReceiveSessionRequest(_service, _session);
        if (!received.IsSuccess) return KernelResult.Fail(received.Error, received.Message!);
        var invocation = received.Value!.Invocation;
        var request = received.Value.Request;

        if (received.Value.CancellationRequested)
            return SettlePreAcceptanceCancellation(invocation);

        var accepted = _kernel.AcceptSessionInvocation(
            _service,
            invocation,
            allowInFlightCancellation: false);
        if (!accepted.IsSuccess)
        {
            if (accepted.Error == KernelError.InvalidTransition)
            {
                var cancellation = _kernel.QuerySessionCancellation(_service, invocation);
                if (cancellation.IsSuccess && cancellation.Value)
                    return SettlePreAcceptanceCancellation(invocation);
            }
            return accepted;
        }

        KernelResult operation;
        object? response = null;

        switch (request.MessageId)
        {
            case IVirtualizationServiceProtocol.Message_CreateAsync when request.Payload is CreateVirtualDomainRequest create:
                var created = _kernel.CreateVirtualDomain(_caller, create.CreateCapability, create.Profile);
                operation = created.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(created.Error, created.Message!);
                if (created.IsSuccess) response = new VirtualDomainAuthorityResponse(created.Value!);
                break;
            case IVirtualizationServiceProtocol.Message_ConfigureAsync when request.Payload is VirtualDomainCommand configure:
                operation = _kernel.ConfigureVirtualDomain(_caller, configure.Domain, configure.Capability);
                break;
            case IVirtualizationServiceProtocol.Message_MapGuestRegionAsync when request.Payload is MapGuestRegionRequest map:
                var mapped = _kernel.MapGuestRegion(_caller, map.Domain, map.MemoryCapability, map.RegionCapability, map.Region, map.GuestRange, map.Access);
                operation = mapped.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(mapped.Error, mapped.Message!);
                if (mapped.IsSuccess) response = new GuestRegionMappingResponse(mapped.Value!);
                break;
            case IVirtualizationServiceProtocol.Message_CloseGuestRegionMappingAsync when request.Payload is CloseGuestRegionMappingRequest close:
                operation = _kernel.CloseGuestRegionMapping(_caller, close.Domain, close.MemoryCapability, close.Mapping);
                break;
            case IVirtualizationServiceProtocol.Message_StartAsync when request.Payload is VirtualDomainCommand start:
                operation = _kernel.StartVirtualDomain(_caller, start.Domain, start.Capability);
                break;
            case IVirtualizationServiceProtocol.Message_ParkAsync when request.Payload is VirtualDomainCommand park:
                operation = _kernel.ParkVirtualDomain(_caller, park.Domain, park.Capability);
                break;
            case IVirtualizationServiceProtocol.Message_ResumeAsync when request.Payload is VirtualDomainCommand resume:
                operation = _kernel.ResumeVirtualDomain(_caller, resume.Domain, resume.Capability);
                break;
            case IVirtualizationServiceProtocol.Message_DestroyAsync when request.Payload is VirtualDomainCommand destroy:
                operation = _kernel.DestroyVirtualDomain(_caller, destroy.Domain, destroy.Capability);
                break;
            case IVirtualizationServiceProtocol.Message_InjectEventAsync when request.Payload is VirtualEventCommand inject:
                operation = _kernel.InjectVirtualEvent(_caller, inject.Domain, inject.EventCapability, inject.Endpoint);
                break;
            case IVirtualizationServiceProtocol.Message_WaitEventAsync when request.Payload is WaitVirtualEventRequest wait:
                var state = _kernel.QueryVirtualDomain(_caller, wait.Domain);
                if (!state.IsSuccess)
                {
                    operation = KernelResult.Fail(state.Error, state.Message!);
                    break;
                }
                var waited = _kernel.WaitForKernelEventAsync(_caller, wait.Endpoint).AsTask().GetAwaiter().GetResult();
                operation = waited.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(waited.Error, waited.Message!);
                if (waited.IsSuccess) response = new VirtualEventResponse(waited.Value!);
                break;
            case IVirtualizationServiceProtocol.Message_CreateNestedAsync when request.Payload is CreateNestedVirtualDomainRequest nested:
                var nestedDomain = _kernel.CreateNestedVirtualDomain(_caller, nested.Parent,
                    nested.ParentCapability, nested.Profile);
                operation = nestedDomain.IsSuccess
                    ? KernelResult.Ok()
                    : KernelResult.Fail(nestedDomain.Error, nestedDomain.Message!);
                if (nestedDomain.IsSuccess)
                    response = new VirtualDomainAuthorityResponse(nestedDomain.Value!);
                break;
            case IVirtualizationServiceProtocol.Message_ObserveTrapAsync when request.Payload is ObserveVirtualTrapRequest trap:
                var observed = _kernel.ObserveVirtualTrap(_caller, trap.Domain, trap.TrapCapability);
                operation = observed.IsSuccess
                    ? KernelResult.Ok()
                    : KernelResult.Fail(observed.Error, observed.Message!);
                if (observed.IsSuccess) response = new VirtualTrapResponse(observed.Value!);
                break;
            case IVirtualizationServiceProtocol.Message_BindExecutableArtifactAsync when request.Payload is BindVirtualExecutableArtifactRequest artifact:
                operation = _kernel.BindVirtualDomainExecutable(_caller, artifact.Domain,
                    artifact.ExecuteCapability, artifact.Mapping, artifact.ImmutablePackage.ToArray(),
                    artifact.MaximumExecutionSteps);
                break;
            default:
                operation = KernelResult.Fail(KernelError.UnsupportedPayload, "VirtualizationService received an unknown message or payload shape.");
                break;
        }

        if (!operation.IsSuccess)
        {
            _ = _kernel.CancelSessionResponse(_service, invocation);
            return operation;
        }

        var published = _kernel.PublishSessionResponse(_service, invocation, response);
        return published.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(published.Error, published.Message!);
    }

    private KernelResult SettlePreAcceptanceCancellation(EndpointSessionInvocationHandle invocation)
    {
        var accepted = _kernel.AcceptSessionCancellation(_service, invocation);
        if (!accepted.IsSuccess) return accepted;
        var cancelled = _kernel.CancelSessionResponse(_service, invocation);
        return cancelled.IsSuccess
            ? KernelResult.Ok()
            : KernelResult.Fail(cancelled.Error, cancelled.Message!);
    }
}
