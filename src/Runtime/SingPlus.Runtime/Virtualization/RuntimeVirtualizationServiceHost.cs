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
    private sealed class PendingResult
    {
        internal readonly PendingResult Original;
        internal readonly EndpointSessionInvocationHandle Invocation;
        internal readonly object? Payload;
        internal readonly bool IsCreation;
        internal PendingResult(EndpointSessionInvocationHandle invocation,object? payload,bool isCreation)
        {Original=this;Invocation=invocation;Payload=payload;IsCreation=isCreation;}
        internal EndpointSessionInvocationRegistry.OriginalPublication? Publication;
        internal bool Attempted;
        internal bool CreationClosed;
        internal bool CreationCleanupStarted;
    }
    private PendingResult? _pendingResult;
    private sealed class PendingOperation
    {
        internal readonly PendingOperation Original;
        internal readonly ChannelEnvelope Request;
        internal readonly EndpointSessionInvocationHandle Invocation;
        internal EndpointSessionInvocationRegistry.OriginalPublication? Lifetime;
        internal readonly bool InputRefused;
        internal bool CancellationAttempted;
        internal PendingOperation(ChannelEnvelope request,EndpointSessionInvocationHandle invocation,bool inputRefused=false)
        {Original=this;Request=request;Invocation=invocation;InputRefused=inputRefused;}
    }
    private PendingOperation? _pendingOperation;
    private int _progressing;
    private int _draining;

    public bool HasPendingResult => _pendingResult is not null || _pendingOperation is not null;

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
        if(!resolved.IsSuccess)return KernelResult<RuntimeVirtualizationServiceHost>.Fail(resolved.Error,resolved.Message!);
        var host=new RuntimeVirtualizationServiceHost(kernel,service,resolved.Value!.Caller,session);
        kernel.RegisterNativeServiceDrain(service,session,host.Drain);
        return KernelResult<RuntimeVirtualizationServiceHost>.Ok(host);
    }

    public KernelResult ProcessNext() => Progress(processNew:true);
    public KernelResult AdvancePendingResult() => Progress(processNew:false);

    public KernelResult Drain()
    {
        Interlocked.Exchange(ref _draining,1); // Stop new dispatch before claiming progress.
        if(Interlocked.CompareExchange(ref _progressing,1,0)!=0)
            return KernelResult.Fail(KernelError.SessionDraining,"Original virtualization dispatch/publication is still progressing.");
        try
        {
            if(_pendingOperation is { } operation)
            {
                var cancelled=ProgressOperationCancellation(operation);
                if(!cancelled.IsSuccess)return cancelled;
                var creations=_kernel.CloseFailedSessionNestedCreations(_caller,_service,_session);
                if(!creations.IsSuccess)return creations;
                // Only host custody retires. The original invocation retains
                // every independent provider/backing/charge cleanup dependency.
                _pendingOperation=null;
            }
            if(_pendingResult is { } pending)
            {
                if(!ReferenceEquals(pending.Original,pending) || pending.Publication is null || !pending.Attempted)
                    return KernelResult.Fail(KernelError.SessionDraining,"Original virtualization result publication remains pending.");
                var known=_kernel.ReadOriginalSessionPublication(pending.Publication);
                if(known.IsSuccess)return RetirePendingResult(pending);
                var channelCancelled=_kernel.ReadOriginalSessionChannelCancellation(pending.Publication);
                if(channelCancelled.IsSuccess)
                {
                    var closed=CloseUnpublishedCreation(pending);
                    return closed.IsSuccess ? RetirePendingResult(pending) : closed;
                }
                var caller=_kernel.Processes.Resolve(_caller);
                var service=_kernel.Processes.Resolve(_service);
                if(known.Error!=KernelError.ResponseNotDelivered ||
                    !(caller.IsSuccess && caller.Value!.State==ProcessState.Exiting || service.IsSuccess && service.Value!.State==ProcessState.Exiting))
                    return KernelResult.Fail(KernelError.SessionDraining,"Original virtualization result publication remains pending.");
                // Exact response never-started is distinct from the already
                // successful provider operation. Cancel publication only; its
                // independent provider/invocation pins still govern teardown.
                var creationClosed=CloseUnpublishedCreation(pending);
                if(!creationClosed.IsSuccess)return creationClosed;
                var cancelled=_kernel.CancelSessionResponse(_service,pending.Invocation);
                if(!cancelled.IsSuccess)return KernelResult.Fail(cancelled.Error,cancelled.Message!);
                return RetirePendingResult(pending);
            }
            return _kernel.CloseFailedSessionNestedCreations(_caller,_service,_session);
        }
        finally { Volatile.Write(ref _progressing,0); }
    }

    private KernelResult Progress(bool processNew)
    {
        if(Interlocked.CompareExchange(ref _progressing,1,0)!=0)
            return KernelResult.Fail(KernelError.InvalidTransition,"Virtualization service is progressing its original invocation.");
        try
        {
            if(_pendingResult is { CreationCleanupStarted:true })return KernelResult.Fail(KernelError.SessionDraining,"Original nested creation cleanup has started; result publication is stopped.");
            if(_pendingResult is not null)return PublishPendingResult();
            if(_pendingOperation is { } operation)
            {
                var handedOff=ProgressOperationCancellation(operation);
                if(!handedOff.IsSuccess)return handedOff;
                // The existing invocation now retains the exact accepted request
                // and all provider/backing/charge pins. This is host handoff only;
                // a separate cleanup request may progress without effect replay.
                _pendingOperation=null;
                if(!processNew)return KernelResult.Ok();
            }
            if(Volatile.Read(ref _draining)!=0)return KernelResult.Fail(KernelError.SessionDraining,"Virtualization host is draining; no new dispatch.");
            return processNew ? ProcessNextCore() : KernelResult.Fail(KernelError.ResponseNotPending,"No original virtualization result pending.");
        }
        finally { Volatile.Write(ref _progressing,0); }
    }

    private KernelResult ProcessNextCore()
    {
        var received = _kernel.ReceiveSessionRequest(_service, _session);
        if (!received.IsSuccess) return KernelResult.Fail(received.Error, received.Message!);
        var invocation = received.Value!.Invocation;
        var request = received.Value.Request;

        if (received.Value.CancellationRequested)
            return SettlePreAcceptanceCancellation(invocation,request);

        var inputAdmission = _kernel.ValidateSessionVirtualIoInput(_caller, request.MessageId, request.Payload);
        if (!inputAdmission.IsSuccess)
        {
            if(SupportsOriginalInputRefusal(request))return RefuseInput(invocation,request,inputAdmission);
            var cancelled = _kernel.CancelSessionResponse(_service, invocation);
            return cancelled.IsSuccess ? inputAdmission : KernelResult.Fail(cancelled.Error, cancelled.Message!);
        }

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
                    return SettlePreAcceptanceCancellation(invocation,request);
            }
            return accepted;
        }

        _pendingOperation=new PendingOperation(request,invocation);
        var lifetime=_kernel.CaptureOriginalVirtualizationRequestLifetime(invocation,_service,request);
        if(!lifetime.IsSuccess)return KernelResult.Fail(lifetime.Error,lifetime.Message!);
        _pendingOperation.Lifetime=lifetime.Value!;
        KernelResult operation;
        object? response = null;

        switch (request.MessageId)
        {
            case IVirtualizationServiceProtocol.Message_CreateAsync when request.Payload is CreateVirtualDomainRequest create:
                var created = _kernel.CreateSessionVirtualDomain(_caller,invocation,create.CreateCapability,create.Profile);
                operation = created.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(created.Error, created.Message!);
                if (created.IsSuccess) response = new VirtualDomainAuthorityResponse(created.Value!);
                break;
            case IVirtualizationServiceProtocol.Message_ConfigureAsync when request.Payload is VirtualDomainCommand configure:
                operation = _kernel.ConfigureSessionVirtualDomain(_caller,invocation, configure.Domain, configure.Capability);
                break;
            case IVirtualizationServiceProtocol.Message_MapGuestRegionAsync when request.Payload is MapGuestRegionRequest map:
                var mapped = _kernel.MapSessionGuestRegion(_caller,invocation,map.Domain,map.MemoryCapability,map.RegionCapability,map.Region,map.GuestRange,map.Access,map.MaximumPrivateExecutableDataBytes);
                operation = mapped.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(mapped.Error, mapped.Message!);
                if (mapped.IsSuccess) response = new GuestRegionMappingResponse(mapped.Value!);
                break;
            case IVirtualizationServiceProtocol.Message_CloseGuestRegionMappingAsync when request.Payload is CloseGuestRegionMappingRequest close:
                operation = _kernel.CloseSessionGuestRegionMapping(_caller, invocation, close.Domain, close.MemoryCapability, close.Mapping);
                break;
            case IVirtualizationServiceProtocol.Message_StartAsync when request.Payload is VirtualDomainCommand start:
                operation = _kernel.StartSessionVirtualDomain(_caller,invocation,start.Domain,start.Capability);
                break;
            case IVirtualizationServiceProtocol.Message_ParkAsync when request.Payload is VirtualDomainCommand park:
                operation = _kernel.ParkSessionVirtualDomain(_caller,invocation, park.Domain, park.Capability);
                break;
            case IVirtualizationServiceProtocol.Message_ResumeAsync when request.Payload is VirtualDomainCommand resume:
                operation = _kernel.ResumeSessionVirtualDomain(_caller,invocation, resume.Domain, resume.Capability);
                break;
            case IVirtualizationServiceProtocol.Message_DestroyAsync when request.Payload is VirtualDomainCommand destroy:
                operation = _kernel.DestroySessionVirtualDomain(_caller, invocation, destroy.Domain, destroy.Capability);
                break;
            case IVirtualizationServiceProtocol.Message_InjectEventAsync when request.Payload is VirtualEventCommand inject:
                operation = _kernel.InjectSessionVirtualEvent(_caller, invocation, inject.Domain, inject.EventCapability, inject.Endpoint);
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
                var nestedDomain = _kernel.CreateSessionNestedVirtualDomain(_caller,invocation, nested.Parent,
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
                operation = _kernel.BindSessionVirtualDomainExecutable(_caller,invocation,artifact.Domain,
                    artifact.ExecuteCapability, artifact.Mapping, artifact.ImmutablePackage.ToArray(),
                    artifact.MaximumExecutionSteps);
                break;
            case IVirtualizationServiceProtocol.Message_BindExecutablePackageAsync when request.Payload is BindVirtualExecutablePackageRequest package:
                operation = _kernel.BindSessionVirtualDomainExecutable(_caller,invocation,package.Domain,
                    package.ExecuteCapability, package.Mapping, package.ImmutablePackage.ToArray(), package.MaximumExecutionSteps);
                break;
            case IVirtualizationServiceProtocol.Message_TransferIoAsync when request.Payload is TransferVirtualIoRequest transfer:
                var transferred=_kernel.TransferSessionVirtualIo(_caller,invocation,transfer);
                operation=transferred.IsSuccess?KernelResult.Ok():KernelResult.Fail(transferred.Error,transferred.Message!);
                if(transferred.IsSuccess)response=transferred.Value;
                break;
            case IVirtualizationServiceProtocol.Message_BindIoAsync when request.Payload is BindVirtualIoRequest bindIo:
                var boundIo=_kernel.BindSessionVirtualIo(_caller,invocation,bindIo);
                operation=boundIo.IsSuccess?KernelResult.Ok():KernelResult.Fail(boundIo.Error,boundIo.Message!);
                if(boundIo.IsSuccess)response=boundIo.Value;
                break;
            case IVirtualizationServiceProtocol.Message_CloseIoAsync when request.Payload is CloseVirtualIoRequest closeIo:
                operation=_kernel.CloseSessionVirtualIo(_caller,invocation,closeIo);
                break;
            default:
                operation = KernelResult.Fail(KernelError.UnsupportedPayload, "VirtualizationService received an unknown message or payload shape.");
                break;
        }

        if (!operation.IsSuccess)
        {
            _pendingOperation!.CancellationAttempted=true;
            _ = _kernel.CancelSessionResponse(_service, invocation);
            // Cancellation is a response consequence, never provider containment.
            // Keep the exact request until drain hands custody to its invocation.
            return operation;
        }

        _pendingResult = new PendingResult(invocation,response,request.MessageId is IVirtualizationServiceProtocol.Message_CreateNestedAsync or IVirtualizationServiceProtocol.Message_CreateAsync);
        _pendingOperation=null; // Successful raw result takes over host custody.
        return PublishPendingResult();
    }

    private KernelResult ReadOperationCancellation(PendingOperation operation)
    {
        if(!ReferenceEquals(operation.Original,operation))
            return KernelResult.Fail(KernelError.PlatformFaulted,"Original virtualization request custody was copied.");
        if(operation.Lifetime is null)
            return KernelResult.Fail(KernelError.ExternalEffectUncontained,"Original accepted request lifetime fact absent.");
        var cancelled=_kernel.ReadOriginalSessionCancelledLifetime(operation.Lifetime,operation.Invocation,_service,operation.Request);
        if(!cancelled.IsSuccess)return cancelled;
        var retired=_kernel.RetireSessionVirtualIoResponse(operation.Lifetime);
        if(!retired.IsSuccess)return retired;
        return _kernel.ProgressSessionReadSnapshotCleanup(operation.Invocation);
    }

    private KernelResult PublishPendingResult()
    {
        var pending=_pendingResult!;
        if(!ReferenceEquals(pending.Original,pending))
            return KernelResult.Fail(KernelError.PlatformFaulted,"Original virtualization result custody was copied.");
        if(pending.Publication is null)
        {
            var captured=_kernel.CaptureOriginalSessionPublication(pending.Invocation,_service,pending.Payload!);
            if(!captured.IsSuccess)return KernelResult.Fail(captured.Error,captured.Message!);
            pending.Publication=captured.Value;
        }
        if(pending.Attempted)
        {
            var known=_kernel.ReadOriginalSessionPublication(pending.Publication);
            if(known.IsSuccess)return RetirePendingResult(pending);
            if(known.Error!=KernelError.ResponseNotDelivered)return known;
            // Exact invocation-owner never-started fact permits response-only retry.
        }
        pending.Attempted=true;
        var published=_kernel.PublishSessionResponse(_service,pending.Invocation,pending.Payload);
        if(!published.IsSuccess)return KernelResult.Fail(published.Error,published.Message!);
        var committed=_kernel.ReadOriginalSessionPublication(pending.Publication);
        if(!committed.IsSuccess)return committed;
        return RetirePendingResult(pending);
    }

    private KernelResult CloseUnpublishedCreation(PendingResult pending)
    {
        if(!pending.IsCreation || pending.CreationClosed)return KernelResult.Ok();
        pending.CreationCleanupStarted=true;
        var closed=_kernel.CloseUnpublishedSessionVirtualCreation(_caller,pending.Publication!);
        if(closed.IsSuccess)closed=_kernel.CompleteSessionVirtualCreationCleanup(pending.Publication!);
        if(closed.IsSuccess)pending.CreationClosed=true;
        return closed;
    }

    private KernelResult RetirePendingResult(PendingResult pending)
    {
        var retired=_kernel.RetireSessionVirtualIoResponse(pending.Publication!);
        if(!retired.IsSuccess)return retired;
        _pendingResult=null;
        return KernelResult.Ok();
    }
    private static bool SupportsOriginalInputRefusal(ChannelEnvelope request) => request.MessageId is
        IVirtualizationServiceProtocol.Message_CreateAsync or IVirtualizationServiceProtocol.Message_BindIoAsync or
        IVirtualizationServiceProtocol.Message_TransferIoAsync or IVirtualizationServiceProtocol.Message_CloseIoAsync;

    private KernelResult RefuseInput(EndpointSessionInvocationHandle invocation,ChannelEnvelope request,KernelResult refusal)
    {
        var operation=new PendingOperation(request,invocation,inputRefused:true);
        _pendingOperation=operation; // Original host custody before any response callback.
        var cancelled=ProgressOperationCancellation(operation);
        if(!cancelled.IsSuccess)return cancelled;
        _pendingOperation=null;
        return refusal;
    }
    private KernelResult ProgressOperationCancellation(PendingOperation operation)
    {
        if(!ReferenceEquals(operation.Original,operation))
            return KernelResult.Fail(KernelError.PlatformFaulted,"Original operation cancellation custody changed.");
        if(operation.Lifetime is null)
        {
            if(!operation.InputRefused)return KernelResult.Fail(KernelError.ExternalEffectUncontained,"Original accepted request lifetime fact absent.");
            var lifetime=_kernel.CaptureOriginalVirtualizationRequestLifetime(operation.Invocation,_service,operation.Request,refusedBeforeAcceptance:true);
            if(!lifetime.IsSuccess)return KernelResult.Fail(lifetime.Error,lifetime.Message!);
            operation.Lifetime=lifetime.Value!;
        }
        if(operation.CancellationAttempted)
        {
            var known=ReadOperationCancellation(operation);
            if(known.IsSuccess)return known;
            // Only the response owner's exact never-started fact permits response
            // retry. Unknown cancellation is not a reason to repeat settlement.
            if(known.Error==KernelError.WrongSessionOwner ||
                _kernel.ReadOriginalSessionPublication(operation.Lifetime).Error!=KernelError.ResponseNotDelivered)return known;
        }
        operation.CancellationAttempted=true;
        var cancelled=_kernel.CancelSessionResponse(_service,operation.Invocation);
        return cancelled.IsSuccess ? ReadOperationCancellation(operation) : KernelResult.Fail(cancelled.Error,cancelled.Message!);
    }

    private KernelResult SettlePreAcceptanceCancellation(EndpointSessionInvocationHandle invocation,ChannelEnvelope request)
    {
        var accepted = _kernel.AcceptSessionCancellation(_service, invocation);
        if (!accepted.IsSuccess) return accepted;
        if(SupportsOriginalInputRefusal(request))return RefuseInput(invocation,request,KernelResult.Ok());
        var cancelled = _kernel.CancelSessionResponse(_service, invocation);
        return cancelled.IsSuccess
            ? KernelResult.Ok()
            : KernelResult.Fail(cancelled.Error, cancelled.Message!);
    }
}


