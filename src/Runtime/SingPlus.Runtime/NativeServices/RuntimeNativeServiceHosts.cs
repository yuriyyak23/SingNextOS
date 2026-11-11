using SingPlus.Contracts;
using SingPlus.Sip;
using SingPlus.Sip.FileSystem;
using SingPlus.Sip.Native;
using SingPlus.Sip.Networking;
using SingPlus.Sip.Process;
using SingPlus.Sip.Sdk;

namespace SingPlus.Runtime;

internal enum NativeSipCompletedEvent
{
    RequestReceived = 0,
    InvocationAccepted,
    SessionResolved,
    CapabilityAdmitted,
    ImplementationEntered,
    ImplementationExited,
    ResponsePublished,
    ResponseCancelled,
    CancellationCommitted,
}

internal interface INativeSipCompletedEventSink
{
    void Record(NativeSipCompletedEvent completedEvent);
}

internal enum NativeSipDeterministicPoint
{
    AfterRequestReceived = 0,
    AfterInvocationAccepted,
    AfterSessionResolved,
    BeforeCapabilityCommit,
    AfterCapabilityCommit,
    BeforeImplementationEntry,
    AfterImplementationExit,
    BeforePublication,
    AfterPublication,
}

internal interface INativeSipDeterministicHook
{
    void At(NativeSipDeterministicPoint point);
}

internal static class NativeServiceDispatch
{
    internal static KernelResult Dispatch(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session, Func<TrustedSipInvocationContext, ChannelEnvelope, KernelResult<object?>> operation, INativeSipCompletedEventSink? trace = null, INativeSipDeterministicHook? hook = null, Func<KernelResult>? beforePublication = null, Func<ChannelEnvelope, bool>? objectCapacityExhausted = null)
    {
        KernelResult<EndpointSessionRequestEnvelope> received = kernel.ReceiveSessionRequest(service, session);
        if (!received.IsSuccess) return KernelResult.Fail(received.Error, received.Message!);
        trace?.Record(NativeSipCompletedEvent.RequestReceived);
        hook?.At(NativeSipDeterministicPoint.AfterRequestReceived);
        EndpointSessionInvocationHandle invocation = received.Value!.Invocation;
        if (received.Value.CancellationRequested)
        {
            KernelResult acceptedCancellation = kernel.AcceptSessionCancellation(service, invocation);
            if (!acceptedCancellation.IsSuccess) return acceptedCancellation;
            trace?.Record(NativeSipCompletedEvent.CancellationCommitted);
            KernelResult<ResponseEnvelope> cancelled = kernel.CancelSessionResponse(service, invocation);
            if (cancelled.IsSuccess) trace?.Record(NativeSipCompletedEvent.ResponseCancelled);
            return cancelled.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(cancelled.Error, cancelled.Message!);
        }
        // Pure local host capacity refusal before implementation acceptance.
        // Later callback failures still carry the existing possible-effect pins.
        if (objectCapacityExhausted?.Invoke(received.Value.Request) == true)
        {
            KernelResult<ResponseEnvelope> cancelled = kernel.CancelSessionResponse(service, invocation);
            if (cancelled.IsSuccess) trace?.Record(NativeSipCompletedEvent.ResponseCancelled);
            return cancelled.IsSuccess
                ? KernelResult.Fail(KernelError.CapacityExhausted, "Native object identity space is exhausted before acceptance.")
                : KernelResult.Fail(cancelled.Error, cancelled.Message!);
        }
        KernelResult accepted = kernel.AcceptSessionInvocation(service, invocation, allowInFlightCancellation: false);
        if (!accepted.IsSuccess) return accepted;
        trace?.Record(NativeSipCompletedEvent.InvocationAccepted);
        hook?.At(NativeSipDeterministicPoint.AfterInvocationAccepted);
        KernelResult<EndpointSessionRegistry.Record> resolved = kernel.EndpointSessions.ResolveForService(session, service);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        trace?.Record(NativeSipCompletedEvent.SessionResolved);
        hook?.At(NativeSipDeterministicPoint.AfterSessionResolved);
        TrustedSipInvocationContext context = new TrustedSipInvocationContext(resolved.Value!.Caller, service, session, invocation);
        KernelResult<object?> result = operation(context, received.Value.Request);
        if (!result.IsSuccess)
        {
            KernelResult<ResponseEnvelope> cancelled = kernel.CancelSessionResponse(service, invocation);
            if (cancelled.IsSuccess) trace?.Record(NativeSipCompletedEvent.ResponseCancelled);
            return KernelResult.Fail(result.Error, result.Message!);
        }
        hook?.At(NativeSipDeterministicPoint.BeforePublication);
        if (beforePublication is not null)
        {
            KernelResult publication = beforePublication();
            if (!publication.IsSuccess)
            {
                KernelResult<ResponseEnvelope> cancelled = kernel.CancelSessionResponse(service, invocation);
                if (cancelled.IsSuccess) trace?.Record(NativeSipCompletedEvent.ResponseCancelled);
                return publication;
            }
        }
        KernelResult<ResponseEnvelope> published = kernel.PublishSessionResponse(service, invocation, result.Value);
        if (published.IsSuccess)
        {
            trace?.Record(NativeSipCompletedEvent.ResponsePublished);
            hook?.At(NativeSipDeterministicPoint.AfterPublication);
        }
        return published.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(published.Error, published.Message!);
    }

    internal static KernelResult ValidateExact(RuntimeKernel kernel, ProcessHandle owner, CapabilityId capability, ResourceKind kind, string resource, CapabilityRights rights)
    {
        KernelResult<CapabilityDescriptorV1> validated = kernel.ValidateCapability(owner, capability, rights);
        if (!validated.IsSuccess) return KernelResult.Fail(validated.Error, validated.Message!);
        return validated.Value!.ResourceKind == kind && string.Equals(validated.Value.ResourceId, resource, StringComparison.Ordinal)
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.WrongCapabilityResource, $"Capability does not authorize exact {kind}/{resource} authority.");
    }
}

public sealed class RuntimeProcessServiceHost
{
    private sealed class ChildRelation(ProcessHandle parent, ChildProcessPolicy policy)
    {
        public ProcessHandle Parent { get; } = parent;
        public ChildProcessPolicy Policy { get; } = policy;
        public CapabilityId? ControlCapability { get; set; }
        public ProcessControlObjectHandle? Control { get; set; }
    }
    private readonly object _dispatchDrainGate = new();
    // Host admission/closure interlocks only. Permission and invocation effects
    // remain owned by their existing kernel authorities.
    private bool _dispatchInProgress;
    private bool _drainRequested;
    private bool _drainInProgress;
    private bool _dispatchFaultPinned;
    private readonly INativeSipDeterministicHook? _hook;
    private readonly RuntimeKernel _kernel;
    private readonly ProcessHandle _service;
    private readonly EndpointSessionHandle _session;
    private readonly Dictionary<ProcessHandle, ChildRelation> _children = [];
    private readonly ServiceEndpointDescriptor _serviceDescriptor;

    private RuntimeProcessServiceHost(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session, ServiceEndpointDescriptor serviceDescriptor, INativeSipDeterministicHook? hook) { _kernel = kernel; _service = service; _session = session; _serviceDescriptor = serviceDescriptor; _hook = hook; }
    public static KernelResult<RuntimeProcessServiceHost> CreateForSession(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session)
        => CreateForSession(kernel, service, session, hook: null);
    internal static KernelResult<RuntimeProcessServiceHost> CreateForSession(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session, INativeSipDeterministicHook? hook)
    {
        KernelResult<EndpointSessionRegistry.Record> resolved = kernel.EndpointSessions.ResolveForService(session, service);
        if (!resolved.IsSuccess) return KernelResult<RuntimeProcessServiceHost>.Fail(resolved.Error, resolved.Message!);
        KernelResult<ServiceEndpointDescriptor> descriptor = kernel.Services.ResolveForProvider(service, IProcessServiceProtocol.CreateDefinition().ContractName);
        if (!descriptor.IsSuccess) return KernelResult<RuntimeProcessServiceHost>.Fail(descriptor.Error, descriptor.Message!);
        RuntimeProcessServiceHost host = new RuntimeProcessServiceHost(kernel, service, session, descriptor.Value!, hook);
        kernel.RegisterNativeServiceDrain(service, session, host.Drain);
        return KernelResult<RuntimeProcessServiceHost>.Ok(host);
    }
    public KernelResult ProcessNext()
    {
        lock (_dispatchDrainGate)
        {
            if (_drainRequested || _dispatchInProgress)
                return KernelResult.Fail(KernelError.SessionDraining, "Native process host dispatch is draining or already in progress.");
            _dispatchInProgress = true;
        }
        bool returned = false;
        try
        {
            KernelResult result = NativeServiceDispatch.Dispatch(_kernel, _service, _session, Execute, hook: _hook);
            returned = true;
            return result;
        }
        finally
        {
            lock (_dispatchDrainGate)
            {
                _dispatchInProgress = false;
                if (!returned)
                {
                    _dispatchFaultPinned = true;
                    _drainRequested = true;
                }
            }
        }
    }
    public KernelResult Drain()
    {
        lock (_dispatchDrainGate)
        {
            _drainRequested = true;
            if (_dispatchFaultPinned)
                return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Native process dispatch outcome is unknown; closure remains pinned.");
            if (_dispatchInProgress || _drainInProgress)
                return KernelResult.Fail(KernelError.SessionDraining, "Native process dispatch or drain callback has not settled.");
            _drainInProgress = true;
        }
        try { return DrainChildren(); }
        finally { lock (_dispatchDrainGate) _drainInProgress = false; }
    }

    private KernelResult DrainChildren()
    {
        foreach (KeyValuePair<ProcessHandle, ChildRelation> pair in _children.ToArray())
        {
            KernelResult closed = CleanupChildRelation(pair.Key, pair.Value);
            if (!closed.IsSuccess) return closed;
        }
        return KernelResult.Ok();
    }

    private KernelResult CleanupChildRelation(ProcessHandle child, ChildRelation relation)
    {
        // Revoke issued local controls before entering child closure callbacks.
        if (relation.Control is { } control)
        {
            KernelResult sealRevoked = _kernel.SealedObjects.Revoke(control.Seal);
            if (!sealRevoked.IsSuccess) return sealRevoked;
        }
        if (relation.ControlCapability is { } capability)
        {
            KernelResult revoked = _kernel.RevokeCapability(capability);
            if (!revoked.IsSuccess && revoked.Error != KernelError.CapabilityRevoked) return revoked;
        }
        if (relation.Policy == ChildProcessPolicy.Attached)
        {
            // Only the existing exact owner retirement receipt permits
            // skipping termination. Absence/stale identity proves nothing.
            KernelResult<ProcessState> retired = _kernel.Processes.ResolveTerminalState(child);
            if (!retired.IsSuccess)
            {
                KernelResult closed = _kernel.TerminateProcess(child);
                if (!closed.IsSuccess) return closed;
            }
        }
        // Consume this successful relation independently. A later child
        // refusal cannot replay its termination/control cleanup.
        _children.Remove(child);
        return KernelResult.Ok();
    }

    private KernelResult<object?> RefuseCreatedChild(ProcessHandle child, ChildRelation relation,
        KernelError error, string? message)
    {
        KernelResult cleanup = CleanupChildRelation(child, relation);
        if (!cleanup.IsSuccess)
        {
            lock (_dispatchDrainGate) _drainRequested = true;
            return KernelResult<object?>.Fail(cleanup.Error,
                $"Child creation refused ({error}); exact child cleanup remains pending: {cleanup.Message}");
        }
        return KernelResult<object?>.Fail(error, message!);
    }

    private KernelResult<object?> Execute(TrustedSipInvocationContext context, ChannelEnvelope envelope)
    {
        ProcessHandle caller = context.Caller;
        switch (envelope.MessageId)
        {
            case IProcessServiceProtocol.Message_CreateAsync when envelope.Payload is CreateProcessRequest request:
            {
                    KernelResult<EffectAdmissionLease> createAdmission = _kernel.AdmitSessionCapabilityEffect(caller, _service, _session,
                    request.CreateCapability, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, 1,
                    CapabilityOperation.Execute);
                if (!createAdmission.IsSuccess) return KernelResult<object?>.Fail(createAdmission.Error, createAdmission.Message!);
                using EffectAdmissionLease admittedCreate = createAdmission.Value!;
                if (request.Policy != ChildProcessPolicy.Attached)
                    return KernelResult<object?>.Fail(KernelError.PlatformUnsupported, "The initial ProcessService profile supports only session-attached children; independent-child adoption is not yet defined.");
                if (request.InitialCapabilities.Count > InitialCapabilitySet.MaxCount)
                    return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "Process creation accepts at most sixteen explicit initial capability delegations.");
                foreach (ProcessCapabilityDelegation requested in request.InitialCapabilities)
                {
                    if (requested.Rights == CapabilityRights.None)
                        return KernelResult<object?>.Fail(KernelError.DelegationDenied, "Initial delegated rights must be non-empty.");
                        KernelResult<CapabilityDescriptorV1> source = _kernel.ValidateCapability(caller, requested.SourceCapability, CapabilityRights.Delegate);
                    if (!source.IsSuccess) return KernelResult<object?>.Fail(source.Error, source.Message!);
                    if ((requested.Rights & source.Value!.Rights) != requested.Rights)
                        return KernelResult<object?>.Fail(KernelError.DelegationDenied, "Initial delegated rights must be a subset of source authority.");
                }
                SingProcessManifestV1 manifest;
                try { manifest = request.Manifest.MaterializeForRuntime(); }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                { return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, exception.Message); }
                    KernelResult<SingProcess> created = _kernel.CreateProcess(manifest);
                if (!created.IsSuccess) return KernelResult<object?>.Fail(created.Error, created.Message!);
                    ProcessHandle child = new ProcessHandle(created.Value!.ProcessId, created.Value.Generation);
                    // Track the real created child before any later refusal/callback.
                    // Existing host dispatch interlock prevents drain from passing
                    // this in-flight creation; no new permission owner is introduced.
                    ChildRelation relation = new ChildRelation(caller, request.Policy);
                _children.Add(child, relation);
                    KernelResult<CapabilityDescriptorV1> control = _kernel.MintCapability(created.Value.DomainId, caller, ResourceKind.Process, CapabilityResourceIds.Process(child), CapabilityRights.Configure | CapabilityRights.Execute | CapabilityRights.Delegate);
                if (!control.IsSuccess) return RefuseCreatedChild(child, relation, control.Error, control.Message);
                    CapabilityDescriptorV1 controlDescriptor = control.Value!;
                relation.ControlCapability = controlDescriptor.CapabilityId;
                    KernelResult<SealedHandle<ProcessControlSeal>> sealedControl = _kernel.SealedObjects.Seal<ProcessControlSeal>(_serviceDescriptor, _service,
                    caller, _session, child.Generation, child.ProcessId.Value, controlDescriptor.CapabilityId);
                if (!sealedControl.IsSuccess) return RefuseCreatedChild(child, relation, sealedControl.Error, sealedControl.Message);
                    ProcessControlObjectHandle controlHandle = new ProcessControlObjectHandle(child, child.Generation, sealedControl.Value);
                relation.Control = controlHandle;
                foreach (ProcessCapabilityDelegation requested in request.InitialCapabilities)
                {
                        KernelResult<CapabilityDescriptorV1> initialDelegation = _kernel.DelegateCapability(caller, child, requested.SourceCapability, requested.Rights);
                    if (!initialDelegation.IsSuccess)
                    {
                        return RefuseCreatedChild(child, relation, initialDelegation.Error, initialDelegation.Message);
                    }
                }
                    KernelResult admitted = _kernel.AdmitProcess(child);
                if (!admitted.IsSuccess) return RefuseCreatedChild(child, relation, admitted.Error, admitted.Message);
                return KernelResult<object?>.Ok(new ProcessAuthorityResponse(new ProcessAuthority(child, controlDescriptor.CapabilityId, controlHandle)));
            }
            case IProcessServiceProtocol.Message_DelegateAsync when envelope.Payload is DelegateProcessCapabilityRequest delegation:
                KernelResult delegationAuth = Control(caller, delegation.Process, delegation.ControlCapability, CapabilityRights.Delegate);
                if (!delegationAuth.IsSuccess) return KernelResult<object?>.Fail(delegationAuth.Error, delegationAuth.Message!);
                KernelResult<CapabilityDescriptorV1> delegated = _kernel.DelegateCapability(caller, delegation.Process, delegation.SourceCapability, delegation.Rights);
                return delegated.IsSuccess ? KernelResult<object?>.Ok(null) : KernelResult<object?>.Fail(delegated.Error, delegated.Message!);
            case IProcessServiceProtocol.Message_StartAsync when envelope.Payload is ProcessCommand start: return Transition(caller, start, _kernel.StartProcess);
            case IProcessServiceProtocol.Message_ParkAsync when envelope.Payload is ProcessCommand park: return Transition(caller, park, _kernel.ParkProcess);
            case IProcessServiceProtocol.Message_ResumeAsync when envelope.Payload is ProcessCommand resume: return Transition(caller, resume, _kernel.ResumeProcess);
            case IProcessServiceProtocol.Message_TerminateAsync when envelope.Payload is ProcessCommand terminate: return Transition(caller, terminate, _kernel.TerminateProcess);
            case IProcessServiceProtocol.Message_WaitForExitAsync when envelope.Payload is ProcessCommand wait:
                KernelResult waitAuth = Control(caller, wait.Process, wait.ControlCapability, CapabilityRights.Configure);
                if (!waitAuth.IsSuccess) return KernelResult<object?>.Fail(waitAuth.Error, waitAuth.Message!);
                KernelResult<SingProcess> state = _kernel.Processes.Resolve(wait.Process);
                if (state.IsSuccess) return KernelResult<object?>.Fail(KernelError.InvalidTransition, "Process has not reached terminal closure.");
                KernelResult<ProcessState> terminal = _kernel.Processes.ResolveTerminalState(wait.Process);
                return terminal.IsSuccess
                    ? KernelResult<object?>.Ok(new ProcessStateResponse(wait.Process, terminal.Value))
                    : KernelResult<object?>.Fail(terminal.Error, terminal.Message!);
            case IProcessServiceProtocol.Message_DelegateV2Async when envelope.Payload is DelegateProcessCapabilityRequestV2 delegationV2:
                return WithControlV2(caller, delegationV2.Control, delegationV2.ControlCapability, CapabilityRights.Delegate, CapabilityOperation.Delegate,
                    () => { KernelResult<CapabilityDescriptorV1> delegated = _kernel.DelegateCapability(caller, delegationV2.Control.Process, delegationV2.SourceCapability, delegationV2.Rights); return delegated.IsSuccess ? KernelResult<object?>.Ok(null) : KernelResult<object?>.Fail(delegated.Error, delegated.Message!); });
            case IProcessServiceProtocol.Message_StartV2Async when envelope.Payload is ProcessCommandV2 startV2: return TransitionV2(caller, startV2, _kernel.StartProcess);
            case IProcessServiceProtocol.Message_ParkV2Async when envelope.Payload is ProcessCommandV2 parkV2: return TransitionV2(caller, parkV2, _kernel.ParkProcess);
            case IProcessServiceProtocol.Message_ResumeV2Async when envelope.Payload is ProcessCommandV2 resumeV2: return TransitionV2(caller, resumeV2, _kernel.ResumeProcess);
            case IProcessServiceProtocol.Message_TerminateV2Async when envelope.Payload is ProcessCommandV2 terminateV2: return TransitionV2(caller, terminateV2, _kernel.TerminateProcess);
            case IProcessServiceProtocol.Message_WaitForExitV2Async when envelope.Payload is ProcessCommandV2 waitV2:
                return WithControlV2(caller, waitV2.Control, waitV2.ControlCapability, CapabilityRights.Configure, CapabilityOperation.Configure,
                    () => { KernelResult<SingProcess> state = _kernel.Processes.Resolve(waitV2.Control.Process); if (state.IsSuccess) return KernelResult<object?>.Fail(KernelError.InvalidTransition, "Process has not reached terminal closure."); KernelResult<ProcessState> terminal = _kernel.Processes.ResolveTerminalState(waitV2.Control.Process); return terminal.IsSuccess ? KernelResult<object?>.Ok(new ProcessStateResponse(waitV2.Control.Process, terminal.Value)) : KernelResult<object?>.Fail(terminal.Error, terminal.Message!); });
            default: return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "ProcessService received an unknown message or payload shape.");
        }
    }
    private KernelResult<object?> TransitionV2(ProcessHandle caller, ProcessCommandV2 command, Func<ProcessHandle, KernelResult> transition) =>
        WithControlV2(caller, command.Control, command.ControlCapability, CapabilityRights.Configure, CapabilityOperation.Configure,
            () => { KernelResult result = transition(command.Control.Process); return result.IsSuccess ? KernelResult<object?>.Ok(null) : KernelResult<object?>.Fail(result.Error, result.Message!); });
    private KernelResult<object?> WithControlV2(ProcessHandle caller, ProcessControlObjectHandle control, CapabilityId capability,
        CapabilityRights rights, CapabilityOperation operation, Func<KernelResult<object?>> action)
    {
        KernelResult<SealedObjectPin<ProcessControlSeal>> pin = _kernel.SealedObjects.AcquirePin(control.Seal, _serviceDescriptor, _service, caller, _session, control.Generation, capability);
        if (!pin.IsSuccess) return KernelResult<object?>.Fail(pin.Error, pin.Message!);
        using SealedObjectPin<ProcessControlSeal> objectPin = pin.Value!;
        if (!_children.TryGetValue(control.Process, out ChildRelation? relation) || relation.Parent != caller || relation.Control != control || objectPin.ObjectKey != control.Process.ProcessId.Value)
            return KernelResult<object?>.Fail(KernelError.StaleHandle, "Process control object is stale or belongs to another caller.");
        KernelResult<EffectAdmissionLease> admitted = _kernel.AdmitSessionCapabilityEffect(caller, _service, _session, capability, ResourceKind.Process,
            CapabilityResourceIds.Process(control.Process), 1, operation);
        if (!admitted.IsSuccess) return KernelResult<object?>.Fail(admitted.Error, admitted.Message!);
        using EffectAdmissionLease effect = admitted.Value!;
        KernelResult final = _kernel.SealedObjects.Revalidate(objectPin, _serviceDescriptor, _service);
        return final.IsSuccess ? action() : KernelResult<object?>.Fail(final.Error, final.Message!);
    }
    private KernelResult<object?> Transition(ProcessHandle caller, ProcessCommand command, Func<ProcessHandle, KernelResult> transition)
    {
        KernelResult auth = Control(caller, command.Process, command.ControlCapability, CapabilityRights.Configure);
        if (!auth.IsSuccess) return KernelResult<object?>.Fail(auth.Error, auth.Message!);
        KernelResult result = transition(command.Process);
        return result.IsSuccess ? KernelResult<object?>.Ok(null) : KernelResult<object?>.Fail(result.Error, result.Message!);
    }
    private KernelResult Control(ProcessHandle caller, ProcessHandle child, CapabilityId capability, CapabilityRights rights)
    {
        if (!_children.TryGetValue(child, out ChildRelation? relation) || relation.Parent != caller) return KernelResult.Fail(KernelError.WrongCapabilitySubject, "Process is not an exact child of this session caller.");
        return NativeServiceDispatch.ValidateExact(_kernel, caller, capability, ResourceKind.Process, CapabilityResourceIds.Process(child), rights);
    }
}

public sealed class RuntimeFileServiceHost : IIFileServiceGeneratedSentryTarget_OpenAsync
{
    private readonly object _dispatchDrainGate = new();
    // Admission interlocks only; kernel permission/effect owners remain authoritative.
    private bool _dispatchInProgress;
    private bool _implementationInProgress;
    private bool _drainRequested;
    private bool _drainInProgress;
    private bool _dispatchFaultPinned;
    private sealed record Entry(ProcessHandle Owner, FileObjectId ObjectId, string Path)
    {
        public CapabilityId? Capability { get; set; }
        public FileObjectHandle? Handle { get; set; }
        // Set only after the actual local seal owner returns. Exception is unknown.
        public bool SealOutcomeKnown { get; set; }
        public byte[] Data { get; set; } = [];
    }
    private readonly RuntimeKernel _kernel; private readonly ProcessHandle _service; private readonly EndpointSessionHandle _session; private readonly ServiceEndpointDescriptor _serviceDescriptor; private readonly INativeSipCompletedEventSink? _trace; private readonly INativeSipDeterministicHook? _hook; private readonly Dictionary<FileObjectId, Entry> _files = []; private ulong _nextId;
    private RuntimeFileServiceHost(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session, ServiceEndpointDescriptor serviceDescriptor, INativeSipCompletedEventSink? trace, INativeSipDeterministicHook? hook) { _kernel = kernel; _service = service; _session = session; _serviceDescriptor = serviceDescriptor; _trace = trace; _hook = hook; }
    public static KernelResult<RuntimeFileServiceHost> CreateForSession(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session)
        => CreateForSession(kernel, service, session, trace: null, hook: null);
    internal static KernelResult<RuntimeFileServiceHost> CreateForSession(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session, INativeSipCompletedEventSink? trace)
        => CreateForSession(kernel, service, session, trace, hook: null);
    internal static KernelResult<RuntimeFileServiceHost> CreateForSession(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session, INativeSipCompletedEventSink? trace, INativeSipDeterministicHook? hook)
    {
        KernelResult<EndpointSessionRegistry.Record> resolved = kernel.EndpointSessions.ResolveForService(session, service);
        if (!resolved.IsSuccess) return KernelResult<RuntimeFileServiceHost>.Fail(resolved.Error, resolved.Message!);
        KernelResult<ServiceEndpointDescriptor> descriptor = kernel.Services.ResolveForProvider(service, IFileServiceProtocol.CreateDefinition().ContractName);
        if (!descriptor.IsSuccess) return KernelResult<RuntimeFileServiceHost>.Fail(descriptor.Error, descriptor.Message!);
        RuntimeFileServiceHost host = new RuntimeFileServiceHost(kernel, service, session, descriptor.Value!, trace, hook);
        kernel.RegisterNativeServiceDrain(service, session, host.Drain);
        return KernelResult<RuntimeFileServiceHost>.Ok(host);
    }
    public KernelResult ProcessNext()
    {
        lock (_dispatchDrainGate)
        {
            if (_drainRequested || _dispatchInProgress || _implementationInProgress)
                return KernelResult.Fail(KernelError.SessionDraining, "Native file host dispatch is draining or already in progress.");
            _dispatchInProgress = true;
        }
        bool returned = false;
        try
        {
            KernelResult result = NativeServiceDispatch.Dispatch(_kernel, _service, _session, Execute, _trace, _hook,
                beforePublication: RefuseDrainingDispatch,
                objectCapacityExhausted: request => _nextId == ulong.MaxValue &&
                    (request.MessageId == IFileServiceProtocol.Message_OpenAsync || request.MessageId == IFileServiceProtocol.Message_OpenV2Async));
            returned = true;
            return result;
        }
        finally
        {
            lock (_dispatchDrainGate)
            {
                _dispatchInProgress = false;
                if (!returned) { _dispatchFaultPinned = true; _drainRequested = true; }
            }
        }
    }

    private KernelResult RefuseDrainingDispatch()
    {
        lock (_dispatchDrainGate)
            return _drainRequested
                ? KernelResult.Fail(KernelError.SessionDraining, "Native file host drain was requested; dispatch publication/admission is refused.")
                : KernelResult.Ok();
    }

    public KernelResult Drain()
    {
        lock (_dispatchDrainGate)
        {
            _drainRequested = true;
            if (_dispatchFaultPinned)
                return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Native file dispatch outcome is unknown; closure remains pinned.");
            if (_dispatchInProgress || _implementationInProgress || _drainInProgress)
                return KernelResult.Fail(KernelError.SessionDraining, "Native file dispatch or drain callback has not settled.");
            _drainInProgress = true;
        }
        try
        {
            Entry[] entries = _files.Values.ToArray();
            // Local source revocation precedes uncertain seal cleanup for every
            // known entry. A failed owner lookup is not closure evidence.
            KernelResult? revokeFailure = null;
            foreach (Entry? entry in entries)
            {
                KernelResult revoked = RevokeFileControl(entry);
                if (!revoked.IsSuccess && revoked.Error != KernelError.CapabilityRevoked)
                    revokeFailure ??= revoked;
            }
            if (revokeFailure is { } failed) return failed;
            foreach (Entry? entry in entries)
            {
                KernelResult released = ReleaseFileEntry(entry);
                if (!released.IsSuccess) return released;
            }
            return KernelResult.Ok();
        }
        finally { lock (_dispatchDrainGate) _drainInProgress = false; }
    }

    private KernelResult RevokeFileControl(Entry entry) => entry.Capability is { } capability
        ? _kernel.RevokeCapability(capability)
        : KernelResult.Fail(KernelError.ExternalEffectUncontained, "File control issuance outcome is unknown.");

    private KernelResult ReleaseFileEntry(Entry entry)
    {
        if (!entry.SealOutcomeKnown)
            return KernelResult.Fail(KernelError.ExternalEffectUncontained, "File seal outcome is unknown; accounting remains pinned.");
        if (entry.Handle is { } handle)
        {
            KernelResult sealedControl = _kernel.SealedObjects.Revoke(handle.Seal);
            if (!sealedControl.IsSuccess) return sealedControl;
        }
        _files.Remove(entry.ObjectId);
        return KernelResult.Ok();
    }

    private KernelResult<T> RefuseCreatedFile<T>(Entry entry, KernelError error, string message)
    {
        KernelResult revoked = RevokeFileControl(entry);
        KernelResult cleanup = !revoked.IsSuccess && revoked.Error != KernelError.CapabilityRevoked
            ? revoked : ReleaseFileEntry(entry);
        if (!cleanup.IsSuccess)
        {
            lock (_dispatchDrainGate) _drainRequested = true;
            return KernelResult<T>.Fail(cleanup.Error, $"{message} File control compensation remains pending: {cleanup.Message}");
        }
        return KernelResult<T>.Fail(error, message);
    }
    private KernelResult<object?> Execute(TrustedSipInvocationContext context, ChannelEnvelope envelope)
    {
        if (envelope.MessageId == IFileServiceProtocol.Message_OpenAsync && envelope.Payload is OpenFileRequest open)
            return FromGenerated(IFileServiceGeneratedOperationSentries.InvokeRuntime_OpenAsync(this, in context, open));
        KernelResult admitted = BeginImplementation();
        if (!admitted.IsSuccess) return KernelResult<object?>.Fail(admitted.Error, admitted.Message!);
        bool returned = false;
        try
        {
            KernelResult<object?> result = ExecuteOrdinary(context, envelope);
            returned = true;
            return result;
        }
        finally { EndImplementation(returned); }
    }

    private KernelResult BeginImplementation()
    {
        lock (_dispatchDrainGate)
        {
            if (_drainRequested || _implementationInProgress)
                return KernelResult.Fail(KernelError.SessionDraining, "Native file implementation is draining or already in progress.");
            _implementationInProgress = true;
            return KernelResult.Ok();
        }
    }

    private void EndImplementation(bool returned)
    {
        lock (_dispatchDrainGate)
        {
            _implementationInProgress = false;
            if (!returned) { _dispatchFaultPinned = true; _drainRequested = true; }
        }
    }

    private KernelResult<object?> ExecuteOrdinary(TrustedSipInvocationContext context, ChannelEnvelope envelope)
    {
        ProcessHandle caller = context.Caller;
        if (envelope.MessageId == IFileServiceProtocol.Message_OpenV2Async && envelope.Payload is OpenFileRequestV2 openV2)
        {
            if (string.IsNullOrWhiteSpace(openV2.Path) || openV2.Path.Length > 200) return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "File path is malformed for service-side namespace policy.");
            KernelResult localAdmission = RefuseDrainingDispatch();
            if (!localAdmission.IsSuccess) return KernelResult<object?>.Fail(localAdmission.Error, localAdmission.Message!);
            KernelResult<EffectAdmissionLease> admission = _kernel.AdmitSessionCapabilityEffect(caller, _service, _session,
                openV2.NamespaceCapability, ResourceKind.File, CapabilityResourceIds.FileNamespace, 1,
                openV2.Create ? CapabilityOperation.Write : CapabilityOperation.Read);
            if (!admission.IsSuccess) return KernelResult<object?>.Fail(admission.Error, admission.Message!);
            using EffectAdmissionLease admitted = admission.Value!;
            _trace?.Record(NativeSipCompletedEvent.CapabilityAdmitted);
            _trace?.Record(NativeSipCompletedEvent.ImplementationEntered);
            if (_nextId == ulong.MaxValue) return KernelResult<object?>.Fail(KernelError.CapacityExhausted, "File object identity space is exhausted.");
            FileObjectId objectId = new FileObjectId(++_nextId);
            Entry entry = new Entry(caller, objectId, openV2.Path);
            _files.Add(objectId, entry);
            // Namespace admission remains V2. The response carries a V1 object
            // control, so record its actual issuance without a lossy V2 projection.
            KernelResult<CapabilityDescriptorV1> capability = _kernel.MintCapability(_kernel.Processes.Resolve(_service).Value!.DomainId,
                caller, ResourceKind.File, CapabilityResourceIds.FileObject(_session, objectId.Value),
                CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure);
            if (!capability.IsSuccess)
            {
                // This inspected local V1 mint returns failure before issuance.
                // Exceptions keep the candidate and existing host fault pin.
                _files.Remove(objectId);
                return KernelResult<object?>.Fail(capability.Error, capability.Message!);
            }
            entry.Capability = capability.Value!.CapabilityId;
            KernelResult<SealedHandle<FileObjectSeal>> sealedObject = _kernel.SealedObjects.Seal<FileObjectSeal>(_serviceDescriptor, _service, caller, _session, 1, objectId.Value, capability.Value.CapabilityId);
            entry.SealOutcomeKnown = true;
            if (!sealedObject.IsSuccess) return RefuseCreatedFile<object?>(entry, sealedObject.Error, sealedObject.Message!);
            FileObjectHandle handle = new FileObjectHandle(_session, objectId, new FileObjectGeneration(1), sealedObject.Value);
            entry.Handle = handle;
            _trace?.Record(NativeSipCompletedEvent.ImplementationExited);
            return KernelResult<object?>.Ok(new FileObjectResponse(new FileObjectAuthority(handle, capability.Value.CapabilityId)));
        }
        if (envelope.Payload is FileCommand command) return Command(caller, envelope.MessageId, command, null);
        if (envelope.Payload is FileWriteRequest write) return Command(caller, envelope.MessageId, new(write.File, write.Capability), write.Data);
        return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "FileService received an unknown message or payload shape.");
    }

    GeneratedSipSentryResult<FileObjectResponse> IIFileServiceGeneratedSentryTarget_OpenAsync.Sentry_OpenAsync(
        in TrustedSipInvocationContext context,
        OpenFileRequest request)
    {
        KernelResult admission = BeginImplementation();
        if (!admission.IsSuccess)
            return GeneratedSipSentryResult<FileObjectResponse>.Failure((int)admission.Error, admission.Message!);
        bool returned = false;
        try
        {
            KernelResult<FileObjectResponse> result = Open(context, request);
            returned = true;
            return result.IsSuccess
                ? GeneratedSipSentryResult<FileObjectResponse>.Success(result.Value!)
                : GeneratedSipSentryResult<FileObjectResponse>.Failure((int)result.Error, result.Message!);
        }
        finally { EndImplementation(returned); }
    }

    private KernelResult<FileObjectResponse> Open(TrustedSipInvocationContext context, OpenFileRequest open)
    {
        if (string.IsNullOrWhiteSpace(open.Path) || open.Path.Length > 200)
            return KernelResult<FileObjectResponse>.Fail(KernelError.UnsupportedPayload, "File path is malformed for service-side namespace policy.");
        _hook?.At(NativeSipDeterministicPoint.BeforeCapabilityCommit);
        KernelResult localAdmission = RefuseDrainingDispatch();
        if (!localAdmission.IsSuccess) return KernelResult<FileObjectResponse>.Fail(localAdmission.Error, localAdmission.Message!);
        KernelResult<EffectAdmissionLease> admission = _kernel.AdmitSessionCapabilityEffect(context.Caller, context.Service, context.Session,
            open.NamespaceCapability, ResourceKind.File, CapabilityResourceIds.FileNamespace, 1,
            open.Create ? CapabilityOperation.Write : CapabilityOperation.Read);
        if (!admission.IsSuccess) return KernelResult<FileObjectResponse>.Fail(admission.Error, admission.Message!);
        using EffectAdmissionLease admitted = admission.Value!;
        _trace?.Record(NativeSipCompletedEvent.CapabilityAdmitted);
        _hook?.At(NativeSipDeterministicPoint.AfterCapabilityCommit);
        _hook?.At(NativeSipDeterministicPoint.BeforeImplementationEntry);
        _trace?.Record(NativeSipCompletedEvent.ImplementationEntered);
        if (_nextId == ulong.MaxValue)
            return KernelResult<FileObjectResponse>.Fail(KernelError.CapacityExhausted, "File object identity space is exhausted.");
        FileObjectId objectId = new FileObjectId(++_nextId);
        Entry entry = new Entry(context.Caller, objectId, open.Path);
        _files.Add(objectId, entry);
        KernelResult<CapabilityDescriptorV1> capability = _kernel.MintCapability(_kernel.Processes.Resolve(context.Service).Value!.DomainId, context.Caller,
            ResourceKind.File, CapabilityResourceIds.FileObject(context.Session, objectId.Value),
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure);
        if (!capability.IsSuccess)
        {
            _files.Remove(objectId);
            return KernelResult<FileObjectResponse>.Fail(capability.Error, capability.Message!);
        }
        entry.Capability = capability.Value!.CapabilityId;
        KernelResult<SealedHandle<FileObjectSeal>> sealedObject = _kernel.SealedObjects.Seal<FileObjectSeal>(_serviceDescriptor, context.Service, context.Caller,
            context.Session, 1, objectId.Value, capability.Value!.CapabilityId);
        entry.SealOutcomeKnown = true;
        if (!sealedObject.IsSuccess)
            return RefuseCreatedFile<FileObjectResponse>(entry, sealedObject.Error, sealedObject.Message!);
        FileObjectHandle handle = new FileObjectHandle(context.Session, objectId, new FileObjectGeneration(1), sealedObject.Value);
        entry.Handle = handle;
        _trace?.Record(NativeSipCompletedEvent.ImplementationExited);
        _hook?.At(NativeSipDeterministicPoint.AfterImplementationExit);
        return KernelResult<FileObjectResponse>.Ok(new(new(handle, capability.Value.CapabilityId)));
    }

    private static KernelResult<object?> FromGenerated<T>(GeneratedSipSentryResult<T> result) =>
        result.IsSuccess
            ? KernelResult<object?>.Ok(result.Value)
            : KernelResult<object?>.Fail((KernelError)result.ErrorCode, result.Message!);
    private KernelResult<object?> Command(ProcessHandle caller, uint message, FileCommand command, BoundedBytes? bytes)
    {
        CapabilityRights rights = message == IFileServiceProtocol.Message_ReadAsync ? CapabilityRights.Read : message == IFileServiceProtocol.Message_WriteAsync ? CapabilityRights.Write : CapabilityRights.Configure;
        CapabilityOperation operation = rights == CapabilityRights.Read ? CapabilityOperation.Read : rights == CapabilityRights.Write ? CapabilityOperation.Write : CapabilityOperation.Configure;
        KernelResult<SealedObjectPin<FileObjectSeal>> pin = _kernel.SealedObjects.AcquirePin(command.File.Seal, _serviceDescriptor, _service, caller, _session, command.File.Generation.Value, command.Capability);
        if (!pin.IsSuccess) return KernelResult<object?>.Fail(pin.Error, pin.Message!);
        using SealedObjectPin<FileObjectSeal> objectPin = pin.Value!;
        FileObjectId objectId = new FileObjectId(objectPin.ObjectKey);
        if (!_files.TryGetValue(objectId, out Entry? entry) || entry.Handle != command.File) return KernelResult<object?>.Fail(KernelError.StaleHandle, "File object generation is stale or closed.");
        KernelResult localAdmission = RefuseDrainingDispatch();
        if (!localAdmission.IsSuccess) return KernelResult<object?>.Fail(localAdmission.Error, localAdmission.Message!);
        KernelResult<EffectAdmissionLease> admission = _kernel.AdmitSessionCapabilityEffect(caller, _service, _session, command.Capability, ResourceKind.File,
            CapabilityResourceIds.FileObject(_session, objectId.Value), 1, operation);
        if (!admission.IsSuccess) return KernelResult<object?>.Fail(admission.Error, admission.Message!);
        using EffectAdmissionLease admitted = admission.Value!;
        _trace?.Record(NativeSipCompletedEvent.CapabilityAdmitted);
        KernelResult finalSeal = _kernel.SealedObjects.Revalidate(objectPin, _serviceDescriptor, _service);
        if (!finalSeal.IsSuccess) return KernelResult<object?>.Fail(finalSeal.Error, finalSeal.Message!);
        _trace?.Record(NativeSipCompletedEvent.ImplementationEntered);
        if (message == IFileServiceProtocol.Message_WriteAsync || message == IFileServiceProtocol.Message_ReadAsync)
        {
            // Trace callbacks can revoke or redirect the original seal source.
            // The admitted capability lease remains independent permission.
            KernelResult afterTraceSeal = _kernel.SealedObjects.Revalidate(objectPin, _serviceDescriptor, _service);
            if (!afterTraceSeal.IsSuccess) return KernelResult<object?>.Fail(afterTraceSeal.Error, afterTraceSeal.Message!);
            KernelResult afterTraceAdmission = RefuseDrainingDispatch();
            if (!afterTraceAdmission.IsSuccess) return KernelResult<object?>.Fail(afterTraceAdmission.Error, afterTraceAdmission.Message!);
        }
        if (message == IFileServiceProtocol.Message_WriteAsync) { entry.Data = bytes!.Value.ToArray(); _trace?.Record(NativeSipCompletedEvent.ImplementationExited); return KernelResult<object?>.Ok(null); }
        if (message == IFileServiceProtocol.Message_ReadAsync) { FileReadResponse response = new FileReadResponse(new BoundedBytes(entry.Data)); _trace?.Record(NativeSipCompletedEvent.ImplementationExited); return KernelResult<object?>.Ok(response); }
        if (message == IFileServiceProtocol.Message_CloseAsync)
        {
            KernelResult revoked = RevokeFileControl(entry);
            if (!revoked.IsSuccess && revoked.Error != KernelError.CapabilityRevoked) return RefuseFileClose(revoked);
            KernelResult closing = _kernel.SealedObjects.BeginClose(objectPin);
            if (!closing.IsSuccess) return RefuseFileClose(closing);
            _files.Remove(objectId);
            _trace?.Record(NativeSipCompletedEvent.ImplementationExited);
            return KernelResult<object?>.Ok(null);
        }
        return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "Unknown file operation.");
    }

    private KernelResult<object?> RefuseFileClose(KernelResult failure)
    {
        lock (_dispatchDrainGate) _drainRequested = true;
        return KernelResult<object?>.Fail(failure.Error, failure.Message!);
    }
}

public sealed class RuntimeNetworkServiceHost
{
    private readonly object _dispatchDrainGate = new();
    // Admission interlocks only; capability/session owners grant permission.
    private bool _dispatchInProgress;
    private bool _drainRequested;
    private bool _drainInProgress;
    private bool _dispatchFaultPinned;
    private readonly INativeSipCompletedEventSink? _trace;
    private readonly INativeSipDeterministicHook? _hook;
    private sealed record Entry(ProcessHandle Owner, SocketObjectId ObjectId, string Label)
    {
        public CapabilityId? Capability { get; set; }
        public SocketObjectHandle? Handle { get; set; }
        public bool SealOutcomeKnown { get; set; }
        public Queue<byte[]> Packets { get; } = new();
    }
    private readonly RuntimeKernel _kernel; private readonly ProcessHandle _service; private readonly EndpointSessionHandle _session; private readonly ServiceEndpointDescriptor _serviceDescriptor; private readonly Dictionary<SocketObjectId, Entry> _sockets = []; private readonly object _gate = new(); private ulong _nextId;
    private RuntimeNetworkServiceHost(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session, ServiceEndpointDescriptor serviceDescriptor, INativeSipCompletedEventSink? trace, INativeSipDeterministicHook? hook) { _kernel = kernel; _service = service; _session = session; _serviceDescriptor = serviceDescriptor; _trace = trace; _hook = hook; }
    public static KernelResult<RuntimeNetworkServiceHost> CreateForSession(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session)
        => CreateForSession(kernel, service, session, trace: null, hook: null);
    internal static KernelResult<RuntimeNetworkServiceHost> CreateForSession(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session, INativeSipCompletedEventSink? trace, INativeSipDeterministicHook? hook)
    {
        KernelResult<EndpointSessionRegistry.Record> resolved = kernel.EndpointSessions.ResolveForService(session, service);
        if (!resolved.IsSuccess) return KernelResult<RuntimeNetworkServiceHost>.Fail(resolved.Error, resolved.Message!);
        KernelResult<ServiceEndpointDescriptor> serviceDescriptor = kernel.Services.ResolveForProvider(service, INetworkServiceProtocol.CreateDefinition().ContractName);
        if (!serviceDescriptor.IsSuccess) return KernelResult<RuntimeNetworkServiceHost>.Fail(serviceDescriptor.Error, serviceDescriptor.Message!);
        RuntimeNetworkServiceHost host = new RuntimeNetworkServiceHost(kernel, service, session, serviceDescriptor.Value!, trace, hook);
        kernel.RegisterNativeServiceDrain(service, session, host.Drain);
        return KernelResult<RuntimeNetworkServiceHost>.Ok(host);
    }
    public KernelResult ProcessNext()
    {
        lock (_dispatchDrainGate)
        {
            if (_drainRequested || _dispatchInProgress)
                return KernelResult.Fail(KernelError.SessionDraining, "Native network host dispatch is draining or already in progress.");
            _dispatchInProgress = true;
        }
        bool returned = false;
        try
        {
            KernelResult result = NativeServiceDispatch.Dispatch(_kernel, _service, _session, Execute, _trace, _hook,
                beforePublication: RefuseDrainingDispatch,
                objectCapacityExhausted: request =>
                {
                    lock (_gate) return _nextId == ulong.MaxValue && request.MessageId == INetworkServiceProtocol.Message_OpenAsync;
                });
            returned = true;
            return result;
        }
        finally
        {
            lock (_dispatchDrainGate)
            {
                _dispatchInProgress = false;
                if (!returned) { _dispatchFaultPinned = true; _drainRequested = true; }
            }
        }
    }

    private KernelResult RefuseDrainingDispatch()
    {
        lock (_dispatchDrainGate)
            return _drainRequested
                ? KernelResult.Fail(KernelError.SessionDraining, "Native network drain was requested; dispatch admission/publication is refused.")
                : KernelResult.Ok();
    }

    public KernelResult Drain()
    {
        lock (_dispatchDrainGate)
        {
            _drainRequested = true;
            if (_dispatchFaultPinned)
                return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Native network dispatch outcome is unknown; closure remains pinned.");
            if (_dispatchInProgress || _drainInProgress)
                return KernelResult.Fail(KernelError.SessionDraining, "Native network dispatch or drain callback has not settled.");
            _drainInProgress = true;
        }
        try
        {
            Entry[] entries;
            lock (_gate) entries = _sockets.Values.ToArray();
            // Stop every known local source before closure cleanup. A lost seal
            // record cannot keep a later socket capability active.
            KernelResult? revokeFailure = null;
            foreach (Entry entry in entries)
            {
                KernelResult revoked = RevokeSocketControl(entry);
                if (!revoked.IsSuccess && revoked.Error != KernelError.CapabilityRevoked)
                    revokeFailure ??= revoked;
            }
            if (revokeFailure is { } failed) return failed;
            foreach (Entry entry in entries)
            {
                KernelResult released = ReleaseSocketEntry(entry);
                if (!released.IsSuccess) return released;
            }
            return KernelResult.Ok();
        }
        finally { lock (_dispatchDrainGate) _drainInProgress = false; }
    }

    private KernelResult ReleaseSocketEntry(Entry entry)
    {
        if (!entry.SealOutcomeKnown)
            return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Socket seal outcome is unknown; accounting remains pinned.");
        if (entry.Handle is { } handle)
        {
            KernelResult sealedControl = _kernel.SealedObjects.Revoke(handle.Seal);
            if (!sealedControl.IsSuccess) return sealedControl;
        }
        lock (_gate) _sockets.Remove(entry.ObjectId);
        return KernelResult.Ok();
    }

    private KernelResult RevokeSocketControl(Entry entry) => entry.Capability is { } capability
        ? _kernel.RevokeCapability(capability)
        : KernelResult.Fail(KernelError.ExternalEffectUncontained, "Socket control issuance outcome is unknown.");

    private KernelResult<object?> RefuseCreatedSocket(Entry entry, KernelError error, string message)
    {
        KernelResult revoked = RevokeSocketControl(entry);
        KernelResult cleanup = !revoked.IsSuccess && revoked.Error != KernelError.CapabilityRevoked
            ? revoked : ReleaseSocketEntry(entry);
        if (!cleanup.IsSuccess)
        {
            lock (_dispatchDrainGate) _drainRequested = true;
            return KernelResult<object?>.Fail(cleanup.Error, $"{message} Socket control compensation remains pending: {cleanup.Message}");
        }
        return KernelResult<object?>.Fail(error, message);
    }
    private KernelResult<object?> Execute(TrustedSipInvocationContext context, ChannelEnvelope envelope)
    {
        ProcessHandle caller = context.Caller;
        if (envelope.MessageId == INetworkServiceProtocol.Message_OpenAsync && envelope.Payload is OpenSocketRequest open)
        {
            _hook?.At(NativeSipDeterministicPoint.BeforeCapabilityCommit);
            KernelResult localAdmission = RefuseDrainingDispatch();
            if (!localAdmission.IsSuccess) return KernelResult<object?>.Fail(localAdmission.Error, localAdmission.Message!);
            KernelResult<EffectAdmissionLease> openAdmission = _kernel.AdmitSessionCapabilityEffect(caller, _service, _session,
                open.EndpointCapability, ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, 1,
                CapabilityOperation.Configure);
            if (!openAdmission.IsSuccess) return KernelResult<object?>.Fail(openAdmission.Error, openAdmission.Message!);
            using EffectAdmissionLease admittedOpen = openAdmission.Value!;
            _trace?.Record(NativeSipCompletedEvent.CapabilityAdmitted);
            _hook?.At(NativeSipDeterministicPoint.AfterCapabilityCommit);
            if (string.IsNullOrWhiteSpace(open.EndpointLabel) || open.EndpointLabel.Length > 200) return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "Endpoint label is malformed for service-side policy.");
            _hook?.At(NativeSipDeterministicPoint.BeforeImplementationEntry);
            _trace?.Record(NativeSipCompletedEvent.ImplementationEntered);
            SocketObjectId objectId;
            lock (_gate)
            {
                if (_nextId == ulong.MaxValue)
                    return KernelResult<object?>.Fail(KernelError.CapacityExhausted, "Socket object identity space is exhausted.");
                objectId = new(++_nextId);
            }
            Entry created = new Entry(caller, objectId, open.EndpointLabel);
            lock (_gate) _sockets.Add(objectId, created);
            KernelResult<CapabilityDescriptorV1> capability = _kernel.MintCapability(_kernel.Processes.Resolve(_service).Value!.DomainId, caller, ResourceKind.Network, CapabilityResourceIds.SocketObject(_session, objectId.Value), CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure);
            if (!capability.IsSuccess)
            {
                lock (_gate) _sockets.Remove(objectId);
                return KernelResult<object?>.Fail(capability.Error, capability.Message!);
            }
            created.Capability = capability.Value!.CapabilityId;
            KernelResult<SealedHandle<SocketObjectSeal>> sealedObject = _kernel.SealedObjects.Seal<SocketObjectSeal>(_serviceDescriptor, _service,
                caller, _session, 1, objectId.Value, capability.Value!.CapabilityId);
            created.SealOutcomeKnown = true;
            if (!sealedObject.IsSuccess)
                return RefuseCreatedSocket(created, sealedObject.Error, sealedObject.Message!);
            SocketObjectHandle handle = new SocketObjectHandle(_session, objectId, new SocketObjectGeneration(1), sealedObject.Value);
            created.Handle = handle;
            _trace?.Record(NativeSipCompletedEvent.ImplementationExited);
            _hook?.At(NativeSipDeterministicPoint.AfterImplementationExit);
            return KernelResult<object?>.Ok(new SocketObjectResponse(new SocketObjectAuthority(handle, capability.Value.CapabilityId)));
        }
        SocketCommand command; BoundedBytes? packet = null;
        if (envelope.Payload is SocketCommand exact) command = exact;
        else if (envelope.Payload is SendPacketRequest send) { command = new(send.Socket, send.Capability); packet = send.Packet; }
        else return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "NetworkService received an unknown message or payload shape.");
        CapabilityRights rights = envelope.MessageId == INetworkServiceProtocol.Message_ReceiveAsync ? CapabilityRights.Read : envelope.MessageId == INetworkServiceProtocol.Message_SendAsync ? CapabilityRights.Write : CapabilityRights.Configure;
        CapabilityOperation operation = rights == CapabilityRights.Read ? CapabilityOperation.Read : rights == CapabilityRights.Write ? CapabilityOperation.Write : CapabilityOperation.Configure;
        KernelResult<SealedObjectPin<SocketObjectSeal>> sealedPin = _kernel.SealedObjects.AcquirePin(command.Socket.Seal, _serviceDescriptor,
            _service, caller, _session, command.Socket.Generation.Value, command.Capability);
        if (!sealedPin.IsSuccess) return KernelResult<object?>.Fail(sealedPin.Error, sealedPin.Message!);
        using SealedObjectPin<SocketObjectSeal> objectPin = sealedPin.Value!;
        SocketObjectId objectIdFromSeal = new SocketObjectId(objectPin.ObjectKey);
        Entry entry;
        lock (_gate)
        {
            if (!_sockets.TryGetValue(objectIdFromSeal, out entry!) || entry.Handle is not { } currentHandle || currentHandle.Seal != command.Socket.Seal)
                return KernelResult<object?>.Fail(KernelError.StaleHandle, "Socket object is stale or closed.");
        }
        _hook?.At(NativeSipDeterministicPoint.BeforeCapabilityCommit);
        KernelResult localCommandAdmission = RefuseDrainingDispatch();
        if (!localCommandAdmission.IsSuccess) return KernelResult<object?>.Fail(localCommandAdmission.Error, localCommandAdmission.Message!);
        KernelResult<EffectAdmissionLease> admission = _kernel.AdmitSessionCapabilityEffect(caller, _service, _session,
            command.Capability, ResourceKind.Network,
            CapabilityResourceIds.SocketObject(_session, objectIdFromSeal.Value), 1, operation);
        if (!admission.IsSuccess) return KernelResult<object?>.Fail(admission.Error, admission.Message!);
        using EffectAdmissionLease admitted = admission.Value!;
        _trace?.Record(NativeSipCompletedEvent.CapabilityAdmitted);
        _hook?.At(NativeSipDeterministicPoint.AfterCapabilityCommit);
        KernelResult finalSeal = _kernel.SealedObjects.Revalidate(objectPin, _serviceDescriptor, _service);
        if (!finalSeal.IsSuccess) return KernelResult<object?>.Fail(finalSeal.Error, finalSeal.Message!);
        if (envelope.MessageId == INetworkServiceProtocol.Message_SendAsync) { lock (_gate) entry.Packets.Enqueue(packet!.Value.ToArray()); return KernelResult<object?>.Ok(null); }
        if (envelope.MessageId == INetworkServiceProtocol.Message_ReceiveAsync) { lock (_gate) return entry.Packets.Count == 0 ? KernelResult<object?>.Fail(KernelError.ResponseNotAvailable, "No committed packet is available.") : KernelResult<object?>.Ok(new ReceivePacketResponse(new BoundedBytes(entry.Packets.Dequeue()))); }
        if (envelope.MessageId == INetworkServiceProtocol.Message_CloseAsync)
        {
            _trace?.Record(NativeSipCompletedEvent.ImplementationEntered);
            KernelResult revoked = RevokeSocketControl(entry);
            if (!revoked.IsSuccess && revoked.Error != KernelError.CapabilityRevoked) return RefuseSocketClose(revoked);
            KernelResult closing = _kernel.SealedObjects.BeginClose(objectPin);
            if (!closing.IsSuccess) return RefuseSocketClose(closing);
            lock (_gate) _sockets.Remove(objectIdFromSeal);
            return KernelResult<object?>.Ok(null);
        }
        return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "Unknown network operation.");
    }

    private KernelResult<object?> RefuseSocketClose(KernelResult failure)
    {
        lock (_dispatchDrainGate) _drainRequested = true;
        return KernelResult<object?>.Fail(failure.Error, failure.Message!);
    }
}
