using SingPlus.Contracts;
using SingPlus.Sip.FileSystem;
using SingPlus.Sip.Native;
using SingPlus.Sip.Networking;
using SingPlus.Sip.Process;

namespace SingPlus.Runtime;

internal static class NativeServiceDispatch
{
    internal static KernelResult Dispatch(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session, Func<ProcessHandle, ChannelEnvelope, KernelResult<object?>> operation)
    {
        var received = kernel.ReceiveSessionRequest(service, session);
        if (!received.IsSuccess) return KernelResult.Fail(received.Error, received.Message!);
        var invocation = received.Value!.Invocation;
        if (received.Value.CancellationRequested)
        {
            var acceptedCancellation = kernel.AcceptSessionCancellation(service, invocation);
            if (!acceptedCancellation.IsSuccess) return acceptedCancellation;
            var cancelled = kernel.CancelSessionResponse(service, invocation);
            return cancelled.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(cancelled.Error, cancelled.Message!);
        }
        var accepted = kernel.AcceptSessionInvocation(service, invocation, allowInFlightCancellation: false);
        if (!accepted.IsSuccess) return accepted;
        var resolved = kernel.EndpointSessions.ResolveForService(session, service);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var result = operation(resolved.Value!.Caller, received.Value.Request);
        if (!result.IsSuccess)
        {
            _ = kernel.CancelSessionResponse(service, invocation);
            return KernelResult.Fail(result.Error, result.Message!);
        }
        var published = kernel.PublishSessionResponse(service, invocation, result.Value);
        return published.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(published.Error, published.Message!);
    }

    internal static KernelResult ValidateExact(RuntimeKernel kernel, ProcessHandle owner, CapabilityId capability, ResourceKind kind, string resource, CapabilityRights rights)
    {
        var validated = kernel.ValidateCapability(owner, capability, rights);
        if (!validated.IsSuccess) return KernelResult.Fail(validated.Error, validated.Message!);
        return validated.Value!.ResourceKind == kind && string.Equals(validated.Value.ResourceId, resource, StringComparison.Ordinal)
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.WrongCapabilityResource, $"Capability does not authorize exact {kind}/{resource} authority.");
    }
}

public sealed class RuntimeProcessServiceHost
{
    private readonly RuntimeKernel _kernel;
    private readonly ProcessHandle _service;
    private readonly EndpointSessionHandle _session;
    private readonly Dictionary<ProcessHandle, (ProcessHandle Parent, ChildProcessPolicy Policy, CapabilityId ControlCapability, ProcessControlObjectHandle Control)> _children = [];
    private readonly ServiceEndpointDescriptor _serviceDescriptor;

    private RuntimeProcessServiceHost(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session, ServiceEndpointDescriptor serviceDescriptor) { _kernel = kernel; _service = service; _session = session; _serviceDescriptor = serviceDescriptor; }
    public static KernelResult<RuntimeProcessServiceHost> CreateForSession(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session)
    {
        var resolved = kernel.EndpointSessions.ResolveForService(session, service);
        if (!resolved.IsSuccess) return KernelResult<RuntimeProcessServiceHost>.Fail(resolved.Error, resolved.Message!);
        var descriptor = kernel.Services.ResolveForProvider(service, IProcessServiceProtocol.CreateDefinition().ContractName);
        if (!descriptor.IsSuccess) return KernelResult<RuntimeProcessServiceHost>.Fail(descriptor.Error, descriptor.Message!);
        var host = new RuntimeProcessServiceHost(kernel, service, session, descriptor.Value!);
        kernel.RegisterNativeServiceDrain(service, session, host.Drain);
        return KernelResult<RuntimeProcessServiceHost>.Ok(host);
    }
    public KernelResult ProcessNext() => NativeServiceDispatch.Dispatch(_kernel, _service, _session, Execute);
    public KernelResult Drain()
    {
        foreach (var child in _children.Where(static pair => pair.Value.Policy == ChildProcessPolicy.Attached).Select(static pair => pair.Key).ToArray())
        {
            var closed = _kernel.TerminateProcess(child);
            if (!closed.IsSuccess && closed.Error != KernelError.StaleHandle) return closed;
        }
        foreach (var relation in _children.Values)
        {
            _ = _kernel.SealedObjects.Revoke(relation.Control.Seal);
            var revoked = _kernel.RevokeCapability(relation.ControlCapability);
            if (!revoked.IsSuccess && revoked.Error != KernelError.CapabilityRevoked) return revoked;
        }
        _children.Clear();
        return KernelResult.Ok();
    }

    private KernelResult<object?> Execute(ProcessHandle caller, ChannelEnvelope envelope)
    {
        switch (envelope.MessageId)
        {
            case IProcessServiceProtocol.Message_CreateAsync when envelope.Payload is CreateProcessRequest request:
            {
                var createAdmission = _kernel.AdmitSessionCapabilityEffect(caller, _service, _session,
                    request.CreateCapability, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, 1,
                    CapabilityOperation.Execute);
                if (!createAdmission.IsSuccess) return KernelResult<object?>.Fail(createAdmission.Error, createAdmission.Message!);
                using var admittedCreate = createAdmission.Value!;
                if (request.Policy != ChildProcessPolicy.Attached)
                    return KernelResult<object?>.Fail(KernelError.PlatformUnsupported, "The initial ProcessService profile supports only session-attached children; independent-child adoption is not yet defined.");
                if (request.InitialCapabilities.Count > InitialCapabilitySet.MaxCount)
                    return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "Process creation accepts at most sixteen explicit initial capability delegations.");
                foreach (var requested in request.InitialCapabilities)
                {
                    if (requested.Rights == CapabilityRights.None)
                        return KernelResult<object?>.Fail(KernelError.DelegationDenied, "Initial delegated rights must be non-empty.");
                    var source = _kernel.ValidateCapability(caller, requested.SourceCapability, CapabilityRights.Delegate);
                    if (!source.IsSuccess) return KernelResult<object?>.Fail(source.Error, source.Message!);
                    if ((requested.Rights & source.Value!.Rights) != requested.Rights)
                        return KernelResult<object?>.Fail(KernelError.DelegationDenied, "Initial delegated rights must be a subset of source authority.");
                }
                SingProcessManifestV1 manifest;
                try { manifest = request.Manifest.MaterializeForRuntime(); }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                { return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, exception.Message); }
                var created = _kernel.CreateProcess(manifest);
                if (!created.IsSuccess) return KernelResult<object?>.Fail(created.Error, created.Message!);
                var child = new ProcessHandle(created.Value!.ProcessId, created.Value.Generation);
                var control = _kernel.MintCapability(created.Value.DomainId, caller, ResourceKind.Process, CapabilityResourceIds.Process(child), CapabilityRights.Configure | CapabilityRights.Execute | CapabilityRights.Delegate);
                if (!control.IsSuccess) { _ = _kernel.TerminateProcess(child); return KernelResult<object?>.Fail(control.Error, control.Message!); }
                var controlDescriptor = control.Value!;
                var sealedControl = _kernel.SealedObjects.Seal<ProcessControlSeal>(_serviceDescriptor, _service,
                    caller, _session, child.Generation, child.ProcessId.Value, controlDescriptor.CapabilityId);
                if (!sealedControl.IsSuccess) { _ = _kernel.RevokeCapability(controlDescriptor.CapabilityId); _ = _kernel.TerminateProcess(child); return KernelResult<object?>.Fail(sealedControl.Error, sealedControl.Message!); }
                var controlHandle = new ProcessControlObjectHandle(child, child.Generation, sealedControl.Value);
                foreach (var requested in request.InitialCapabilities)
                {
                    var initialDelegation = _kernel.DelegateCapability(caller, child, requested.SourceCapability, requested.Rights);
                    if (!initialDelegation.IsSuccess)
                    {
                        _ = _kernel.RevokeCapability(controlDescriptor.CapabilityId);
                        _ = _kernel.TerminateProcess(child);
                        return KernelResult<object?>.Fail(initialDelegation.Error, initialDelegation.Message!);
                    }
                }
                var admitted = _kernel.AdmitProcess(child);
                if (!admitted.IsSuccess) { _ = _kernel.RevokeCapability(controlDescriptor.CapabilityId); _ = _kernel.TerminateProcess(child); return KernelResult<object?>.Fail(admitted.Error, admitted.Message!); }
                _children[child] = (caller, request.Policy, controlDescriptor.CapabilityId, controlHandle);
                return KernelResult<object?>.Ok(new ProcessAuthorityResponse(new ProcessAuthority(child, controlDescriptor.CapabilityId, controlHandle)));
            }
            case IProcessServiceProtocol.Message_DelegateAsync when envelope.Payload is DelegateProcessCapabilityRequest delegation:
                var delegationAuth = Control(caller, delegation.Process, delegation.ControlCapability, CapabilityRights.Delegate);
                if (!delegationAuth.IsSuccess) return KernelResult<object?>.Fail(delegationAuth.Error, delegationAuth.Message!);
                var delegated = _kernel.DelegateCapability(caller, delegation.Process, delegation.SourceCapability, delegation.Rights);
                return delegated.IsSuccess ? KernelResult<object?>.Ok(null) : KernelResult<object?>.Fail(delegated.Error, delegated.Message!);
            case IProcessServiceProtocol.Message_StartAsync when envelope.Payload is ProcessCommand start: return Transition(caller, start, _kernel.StartProcess);
            case IProcessServiceProtocol.Message_ParkAsync when envelope.Payload is ProcessCommand park: return Transition(caller, park, _kernel.ParkProcess);
            case IProcessServiceProtocol.Message_ResumeAsync when envelope.Payload is ProcessCommand resume: return Transition(caller, resume, _kernel.ResumeProcess);
            case IProcessServiceProtocol.Message_TerminateAsync when envelope.Payload is ProcessCommand terminate: return Transition(caller, terminate, _kernel.TerminateProcess);
            case IProcessServiceProtocol.Message_WaitForExitAsync when envelope.Payload is ProcessCommand wait:
                var waitAuth = Control(caller, wait.Process, wait.ControlCapability, CapabilityRights.Configure);
                if (!waitAuth.IsSuccess) return KernelResult<object?>.Fail(waitAuth.Error, waitAuth.Message!);
                var state = _kernel.Processes.Resolve(wait.Process);
                if (state.IsSuccess) return KernelResult<object?>.Fail(KernelError.InvalidTransition, "Process has not reached terminal closure.");
                var terminal = _kernel.Processes.ResolveTerminalState(wait.Process);
                return terminal.IsSuccess
                    ? KernelResult<object?>.Ok(new ProcessStateResponse(wait.Process, terminal.Value))
                    : KernelResult<object?>.Fail(terminal.Error, terminal.Message!);
            case IProcessServiceProtocol.Message_DelegateV2Async when envelope.Payload is DelegateProcessCapabilityRequestV2 delegationV2:
                return WithControlV2(caller, delegationV2.Control, delegationV2.ControlCapability, CapabilityRights.Delegate, CapabilityOperation.Delegate,
                    () => { var delegated = _kernel.DelegateCapability(caller, delegationV2.Control.Process, delegationV2.SourceCapability, delegationV2.Rights); return delegated.IsSuccess ? KernelResult<object?>.Ok(null) : KernelResult<object?>.Fail(delegated.Error, delegated.Message!); });
            case IProcessServiceProtocol.Message_StartV2Async when envelope.Payload is ProcessCommandV2 startV2: return TransitionV2(caller, startV2, _kernel.StartProcess);
            case IProcessServiceProtocol.Message_ParkV2Async when envelope.Payload is ProcessCommandV2 parkV2: return TransitionV2(caller, parkV2, _kernel.ParkProcess);
            case IProcessServiceProtocol.Message_ResumeV2Async when envelope.Payload is ProcessCommandV2 resumeV2: return TransitionV2(caller, resumeV2, _kernel.ResumeProcess);
            case IProcessServiceProtocol.Message_TerminateV2Async when envelope.Payload is ProcessCommandV2 terminateV2: return TransitionV2(caller, terminateV2, _kernel.TerminateProcess);
            case IProcessServiceProtocol.Message_WaitForExitV2Async when envelope.Payload is ProcessCommandV2 waitV2:
                return WithControlV2(caller, waitV2.Control, waitV2.ControlCapability, CapabilityRights.Configure, CapabilityOperation.Configure,
                    () => { var state = _kernel.Processes.Resolve(waitV2.Control.Process); if (state.IsSuccess) return KernelResult<object?>.Fail(KernelError.InvalidTransition, "Process has not reached terminal closure."); var terminal = _kernel.Processes.ResolveTerminalState(waitV2.Control.Process); return terminal.IsSuccess ? KernelResult<object?>.Ok(new ProcessStateResponse(waitV2.Control.Process, terminal.Value)) : KernelResult<object?>.Fail(terminal.Error, terminal.Message!); });
            default: return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "ProcessService received an unknown message or payload shape.");
        }
    }
    private KernelResult<object?> TransitionV2(ProcessHandle caller, ProcessCommandV2 command, Func<ProcessHandle, KernelResult> transition) =>
        WithControlV2(caller, command.Control, command.ControlCapability, CapabilityRights.Configure, CapabilityOperation.Configure,
            () => { var result = transition(command.Control.Process); return result.IsSuccess ? KernelResult<object?>.Ok(null) : KernelResult<object?>.Fail(result.Error, result.Message!); });
    private KernelResult<object?> WithControlV2(ProcessHandle caller, ProcessControlObjectHandle control, CapabilityId capability,
        CapabilityRights rights, CapabilityOperation operation, Func<KernelResult<object?>> action)
    {
        var pin = _kernel.SealedObjects.AcquirePin(control.Seal, _serviceDescriptor, _service, caller, _session, control.Generation, capability);
        if (!pin.IsSuccess) return KernelResult<object?>.Fail(pin.Error, pin.Message!);
        using var objectPin = pin.Value!;
        if (!_children.TryGetValue(control.Process, out var relation) || relation.Parent != caller || relation.Control != control || objectPin.ObjectKey != control.Process.ProcessId.Value)
            return KernelResult<object?>.Fail(KernelError.StaleHandle, "Process control object is stale or belongs to another caller.");
        var admitted = _kernel.AdmitSessionCapabilityEffect(caller, _service, _session, capability, ResourceKind.Process,
            CapabilityResourceIds.Process(control.Process), 1, operation);
        if (!admitted.IsSuccess) return KernelResult<object?>.Fail(admitted.Error, admitted.Message!);
        using var effect = admitted.Value!;
        var final = _kernel.SealedObjects.Revalidate(objectPin, _serviceDescriptor, _service);
        return final.IsSuccess ? action() : KernelResult<object?>.Fail(final.Error, final.Message!);
    }
    private KernelResult<object?> Transition(ProcessHandle caller, ProcessCommand command, Func<ProcessHandle, KernelResult> transition)
    {
        var auth = Control(caller, command.Process, command.ControlCapability, CapabilityRights.Configure);
        if (!auth.IsSuccess) return KernelResult<object?>.Fail(auth.Error, auth.Message!);
        var result = transition(command.Process);
        return result.IsSuccess ? KernelResult<object?>.Ok(null) : KernelResult<object?>.Fail(result.Error, result.Message!);
    }
    private KernelResult Control(ProcessHandle caller, ProcessHandle child, CapabilityId capability, CapabilityRights rights)
    {
        if (!_children.TryGetValue(child, out var relation) || relation.Parent != caller) return KernelResult.Fail(KernelError.WrongCapabilitySubject, "Process is not an exact child of this session caller.");
        return NativeServiceDispatch.ValidateExact(_kernel, caller, capability, ResourceKind.Process, CapabilityResourceIds.Process(child), rights);
    }
}

public sealed class RuntimeFileServiceHost
{
    private sealed record Entry(ProcessHandle Owner, FileObjectHandle Handle, CapabilityId Capability, string Path) { public byte[] Data { get; set; } = []; }
    private readonly RuntimeKernel _kernel; private readonly ProcessHandle _service; private readonly EndpointSessionHandle _session; private readonly ServiceEndpointDescriptor _serviceDescriptor; private readonly Dictionary<FileObjectId, Entry> _files = []; private ulong _nextId;
    private RuntimeFileServiceHost(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session, ServiceEndpointDescriptor serviceDescriptor) { _kernel = kernel; _service = service; _session = session; _serviceDescriptor = serviceDescriptor; }
    public static KernelResult<RuntimeFileServiceHost> CreateForSession(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session)
    {
        var resolved = kernel.EndpointSessions.ResolveForService(session, service);
        if (!resolved.IsSuccess) return KernelResult<RuntimeFileServiceHost>.Fail(resolved.Error, resolved.Message!);
        var descriptor = kernel.Services.ResolveForProvider(service, IFileServiceProtocol.CreateDefinition().ContractName);
        if (!descriptor.IsSuccess) return KernelResult<RuntimeFileServiceHost>.Fail(descriptor.Error, descriptor.Message!);
        var host = new RuntimeFileServiceHost(kernel, service, session, descriptor.Value!);
        kernel.RegisterNativeServiceDrain(service, session, host.Drain);
        return KernelResult<RuntimeFileServiceHost>.Ok(host);
    }
    public KernelResult ProcessNext() => NativeServiceDispatch.Dispatch(_kernel, _service, _session, Execute);
    public KernelResult Drain()
    {
        foreach (var entry in _files.Values) { _ = _kernel.SealedObjects.Revoke(entry.Handle.Seal); var revoked = _kernel.RevokeCapability(entry.Capability); if (!revoked.IsSuccess && revoked.Error != KernelError.CapabilityRevoked) return revoked; }
        _files.Clear();
        return KernelResult.Ok();
    }
    private KernelResult<object?> Execute(ProcessHandle caller, ChannelEnvelope envelope)
    {
        if (envelope.MessageId == IFileServiceProtocol.Message_OpenAsync && envelope.Payload is OpenFileRequest open)
        {
            if (string.IsNullOrWhiteSpace(open.Path) || open.Path.Length > 200) return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "File path is malformed for service-side namespace policy.");
            var admission = _kernel.AdmitSessionCapabilityEffect(caller, _service, _session,
                open.NamespaceCapability, ResourceKind.File, CapabilityResourceIds.FileNamespace, 1,
                open.Create ? CapabilityOperation.Write : CapabilityOperation.Read);
            if (!admission.IsSuccess) return KernelResult<object?>.Fail(admission.Error, admission.Message!);
            using var admitted = admission.Value!;
            if (_nextId == ulong.MaxValue) return KernelResult<object?>.Fail(KernelError.CapacityExhausted, "File object identity space is exhausted.");
            var objectId = new FileObjectId(++_nextId);
            var capability = _kernel.MintCapability(_kernel.Processes.Resolve(_service).Value!.DomainId, caller, ResourceKind.File, CapabilityResourceIds.FileObject(_session, objectId.Value), CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure);
            if (!capability.IsSuccess) return KernelResult<object?>.Fail(capability.Error, capability.Message!);
            var sealedObject = _kernel.SealedObjects.Seal<FileObjectSeal>(_serviceDescriptor, _service, caller, _session, 1, objectId.Value, capability.Value!.CapabilityId);
            if (!sealedObject.IsSuccess) { _ = _kernel.RevokeCapability(capability.Value.CapabilityId); return KernelResult<object?>.Fail(sealedObject.Error, sealedObject.Message!); }
            var handle = new FileObjectHandle(_session, objectId, new FileObjectGeneration(1), sealedObject.Value);
            _files.Add(handle.ObjectId, new(caller, handle, capability.Value!.CapabilityId, open.Path));
            return KernelResult<object?>.Ok(new FileObjectResponse(new FileObjectAuthority(handle, capability.Value.CapabilityId)));
        }
        if (envelope.MessageId == IFileServiceProtocol.Message_OpenV2Async && envelope.Payload is OpenFileRequestV2 openV2)
        {
            if (string.IsNullOrWhiteSpace(openV2.Path) || openV2.Path.Length > 200) return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "File path is malformed for service-side namespace policy.");
            var admission = _kernel.AdmitSessionCapabilityEffect(caller, _service, _session,
                openV2.NamespaceCapability, ResourceKind.File, CapabilityResourceIds.FileNamespace, 1,
                openV2.Create ? CapabilityOperation.Write : CapabilityOperation.Read);
            if (!admission.IsSuccess) return KernelResult<object?>.Fail(admission.Error, admission.Message!);
            using var admitted = admission.Value!;
            if (_nextId == ulong.MaxValue) return KernelResult<object?>.Fail(KernelError.CapacityExhausted, "File object identity space is exhausted.");
            var objectId = new FileObjectId(++_nextId);
            var capability = _kernel.MintCapabilityV2(_kernel.Processes.Resolve(_service).Value!.DomainId,
                caller, ResourceKind.File, CapabilityResourceIds.FileObject(_session, objectId.Value),
                CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure);
            if (!capability.IsSuccess) return KernelResult<object?>.Fail(capability.Error, capability.Message!);
            var v1 = _kernel.CapabilityAuthority.ResolveCapabilityId(capability.Value);
            if (!v1.IsSuccess) return KernelResult<object?>.Fail(v1.Error, v1.Message!);
            var sealedObject = _kernel.SealedObjects.Seal<FileObjectSeal>(_serviceDescriptor, _service, caller, _session, 1, objectId.Value, v1.Value);
            if (!sealedObject.IsSuccess) { _ = _kernel.RevokeCapability(v1.Value); return KernelResult<object?>.Fail(sealedObject.Error, sealedObject.Message!); }
            var handle = new FileObjectHandle(_session, objectId, new FileObjectGeneration(1), sealedObject.Value);
            _files.Add(handle.ObjectId, new(caller, handle, v1.Value, openV2.Path));
            return KernelResult<object?>.Ok(new FileObjectResponse(new FileObjectAuthority(handle, v1.Value)));
        }
        if (envelope.Payload is FileCommand command) return Command(caller, envelope.MessageId, command, null);
        if (envelope.Payload is FileWriteRequest write) return Command(caller, envelope.MessageId, new(write.File, write.Capability), write.Data);
        return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "FileService received an unknown message or payload shape.");
    }
    private KernelResult<object?> Command(ProcessHandle caller, uint message, FileCommand command, BoundedBytes? bytes)
    {
        var rights = message == IFileServiceProtocol.Message_ReadAsync ? CapabilityRights.Read : message == IFileServiceProtocol.Message_WriteAsync ? CapabilityRights.Write : CapabilityRights.Configure;
        var operation = rights == CapabilityRights.Read ? CapabilityOperation.Read : rights == CapabilityRights.Write ? CapabilityOperation.Write : CapabilityOperation.Configure;
        var pin = _kernel.SealedObjects.AcquirePin(command.File.Seal, _serviceDescriptor, _service, caller, _session, command.File.Generation.Value, command.Capability);
        if (!pin.IsSuccess) return KernelResult<object?>.Fail(pin.Error, pin.Message!);
        using var objectPin = pin.Value!;
        var objectId = new FileObjectId(objectPin.ObjectKey);
        if (!_files.TryGetValue(objectId, out var entry) || entry.Handle != command.File) return KernelResult<object?>.Fail(KernelError.StaleHandle, "File object generation is stale or closed.");
        var admission = _kernel.AdmitSessionCapabilityEffect(caller, _service, _session, command.Capability, ResourceKind.File,
            CapabilityResourceIds.FileObject(_session, objectId.Value), 1, operation);
        if (!admission.IsSuccess) return KernelResult<object?>.Fail(admission.Error, admission.Message!);
        using var admitted = admission.Value!;
        var finalSeal = _kernel.SealedObjects.Revalidate(objectPin, _serviceDescriptor, _service);
        if (!finalSeal.IsSuccess) return KernelResult<object?>.Fail(finalSeal.Error, finalSeal.Message!);
        if (message == IFileServiceProtocol.Message_WriteAsync) { entry.Data = bytes!.Value.ToArray(); return KernelResult<object?>.Ok(null); }
        if (message == IFileServiceProtocol.Message_ReadAsync) return KernelResult<object?>.Ok(new FileReadResponse(new BoundedBytes(entry.Data)));
        if (message == IFileServiceProtocol.Message_CloseAsync) { var closing = _kernel.SealedObjects.BeginClose(objectPin); if (!closing.IsSuccess) return KernelResult<object?>.Fail(closing.Error, closing.Message!); _files.Remove(objectId); _ = _kernel.RevokeCapability(entry.Capability); return KernelResult<object?>.Ok(null); }
        return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "Unknown file operation.");
    }
}

public sealed class RuntimeNetworkServiceHost
{
    private sealed record Entry(ProcessHandle Owner, SocketObjectHandle Handle, CapabilityId Capability, string Label) { public Queue<byte[]> Packets { get; } = new(); }
    private readonly RuntimeKernel _kernel; private readonly ProcessHandle _service; private readonly EndpointSessionHandle _session; private readonly ServiceEndpointDescriptor _serviceDescriptor; private readonly Dictionary<SocketObjectId, Entry> _sockets = []; private readonly object _gate = new(); private ulong _nextId;
    private RuntimeNetworkServiceHost(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session, ServiceEndpointDescriptor serviceDescriptor) { _kernel = kernel; _service = service; _session = session; _serviceDescriptor = serviceDescriptor; }
    public static KernelResult<RuntimeNetworkServiceHost> CreateForSession(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session)
    {
        var resolved = kernel.EndpointSessions.ResolveForService(session, service);
        if (!resolved.IsSuccess) return KernelResult<RuntimeNetworkServiceHost>.Fail(resolved.Error, resolved.Message!);
        var serviceDescriptor = kernel.Services.ResolveForProvider(service, INetworkServiceProtocol.CreateDefinition().ContractName);
        if (!serviceDescriptor.IsSuccess) return KernelResult<RuntimeNetworkServiceHost>.Fail(serviceDescriptor.Error, serviceDescriptor.Message!);
        var host = new RuntimeNetworkServiceHost(kernel, service, session, serviceDescriptor.Value!);
        kernel.RegisterNativeServiceDrain(service, session, host.Drain);
        return KernelResult<RuntimeNetworkServiceHost>.Ok(host);
    }
    public KernelResult ProcessNext() => NativeServiceDispatch.Dispatch(_kernel, _service, _session, Execute);
    public KernelResult Drain()
    {
        lock (_gate)
        {
            foreach (var entry in _sockets.Values)
            {
                _ = _kernel.SealedObjects.Revoke(entry.Handle.Seal);
                var revoked = _kernel.RevokeCapability(entry.Capability);
                if (!revoked.IsSuccess && revoked.Error != KernelError.CapabilityRevoked) return revoked;
            }
            _sockets.Clear();
        }
        return KernelResult.Ok();
    }
    private KernelResult<object?> Execute(ProcessHandle caller, ChannelEnvelope envelope)
    {
        if (envelope.MessageId == INetworkServiceProtocol.Message_OpenAsync && envelope.Payload is OpenSocketRequest open)
        {
            var openAdmission = _kernel.AdmitSessionCapabilityEffect(caller, _service, _session,
                open.EndpointCapability, ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, 1,
                CapabilityOperation.Configure);
            if (!openAdmission.IsSuccess) return KernelResult<object?>.Fail(openAdmission.Error, openAdmission.Message!);
            using var admittedOpen = openAdmission.Value!;
            if (string.IsNullOrWhiteSpace(open.EndpointLabel) || open.EndpointLabel.Length > 200) return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "Endpoint label is malformed for service-side policy.");
            SocketObjectId objectId;
            lock (_gate)
            {
                if (_nextId == ulong.MaxValue)
                    return KernelResult<object?>.Fail(KernelError.CapacityExhausted, "Socket object identity space is exhausted.");
                objectId = new(++_nextId);
            }
            var capability = _kernel.MintCapability(_kernel.Processes.Resolve(_service).Value!.DomainId, caller, ResourceKind.Network, CapabilityResourceIds.SocketObject(_session, objectId.Value), CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure);
            if (!capability.IsSuccess) return KernelResult<object?>.Fail(capability.Error, capability.Message!);
            var sealedObject = _kernel.SealedObjects.Seal<SocketObjectSeal>(_serviceDescriptor, _service,
                caller, _session, 1, objectId.Value, capability.Value!.CapabilityId);
            if (!sealedObject.IsSuccess)
            {
                _ = _kernel.RevokeCapability(capability.Value.CapabilityId);
                return KernelResult<object?>.Fail(sealedObject.Error, sealedObject.Message!);
            }
            var handle = new SocketObjectHandle(_session, objectId, new SocketObjectGeneration(1), sealedObject.Value);
            lock (_gate) _sockets.Add(handle.ObjectId, new(caller, handle, capability.Value.CapabilityId, open.EndpointLabel));
            return KernelResult<object?>.Ok(new SocketObjectResponse(new SocketObjectAuthority(handle, capability.Value.CapabilityId)));
        }
        SocketCommand command; BoundedBytes? packet = null;
        if (envelope.Payload is SocketCommand exact) command = exact;
        else if (envelope.Payload is SendPacketRequest send) { command = new(send.Socket, send.Capability); packet = send.Packet; }
        else return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "NetworkService received an unknown message or payload shape.");
        var rights = envelope.MessageId == INetworkServiceProtocol.Message_ReceiveAsync ? CapabilityRights.Read : envelope.MessageId == INetworkServiceProtocol.Message_SendAsync ? CapabilityRights.Write : CapabilityRights.Configure;
        var operation = rights == CapabilityRights.Read ? CapabilityOperation.Read : rights == CapabilityRights.Write ? CapabilityOperation.Write : CapabilityOperation.Configure;
        var sealedPin = _kernel.SealedObjects.AcquirePin(command.Socket.Seal, _serviceDescriptor,
            _service, caller, _session, command.Socket.Generation.Value, command.Capability);
        if (!sealedPin.IsSuccess) return KernelResult<object?>.Fail(sealedPin.Error, sealedPin.Message!);
        using var objectPin = sealedPin.Value!;
        var objectIdFromSeal = new SocketObjectId(objectPin.ObjectKey);
        Entry entry;
        lock (_gate)
        {
            if (!_sockets.TryGetValue(objectIdFromSeal, out entry!) || entry.Handle.Seal != command.Socket.Seal)
                return KernelResult<object?>.Fail(KernelError.StaleHandle, "Socket object is stale or closed.");
        }
        var admission = _kernel.AdmitSessionCapabilityEffect(caller, _service, _session,
            command.Capability, ResourceKind.Network,
            CapabilityResourceIds.SocketObject(_session, objectIdFromSeal.Value), 1, operation);
        if (!admission.IsSuccess) return KernelResult<object?>.Fail(admission.Error, admission.Message!);
        using var admitted = admission.Value!;
        var finalSeal = _kernel.SealedObjects.Revalidate(objectPin, _serviceDescriptor, _service);
        if (!finalSeal.IsSuccess) return KernelResult<object?>.Fail(finalSeal.Error, finalSeal.Message!);
        if (envelope.MessageId == INetworkServiceProtocol.Message_SendAsync) { lock (_gate) entry.Packets.Enqueue(packet!.Value.ToArray()); return KernelResult<object?>.Ok(null); }
        if (envelope.MessageId == INetworkServiceProtocol.Message_ReceiveAsync) { lock (_gate) return entry.Packets.Count == 0 ? KernelResult<object?>.Fail(KernelError.ResponseNotAvailable, "No committed packet is available.") : KernelResult<object?>.Ok(new ReceivePacketResponse(new BoundedBytes(entry.Packets.Dequeue()))); }
        if (envelope.MessageId == INetworkServiceProtocol.Message_CloseAsync)
        {
            var closing = _kernel.SealedObjects.BeginClose(objectPin);
            if (!closing.IsSuccess) return KernelResult<object?>.Fail(closing.Error, closing.Message!);
            lock (_gate) _sockets.Remove(objectIdFromSeal);
            _ = _kernel.RevokeCapability(entry.Capability);
            return KernelResult<object?>.Ok(null);
        }
        return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "Unknown network operation.");
    }
}
