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
    private readonly Dictionary<ProcessHandle, (ProcessHandle Parent, ChildProcessPolicy Policy, CapabilityId ControlCapability)> _children = [];

    private RuntimeProcessServiceHost(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session) { _kernel = kernel; _service = service; _session = session; }
    public static KernelResult<RuntimeProcessServiceHost> CreateForSession(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session)
    {
        var resolved = kernel.EndpointSessions.ResolveForService(session, service);
        if (!resolved.IsSuccess) return KernelResult<RuntimeProcessServiceHost>.Fail(resolved.Error, resolved.Message!);
        var host = new RuntimeProcessServiceHost(kernel, service, session);
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
                var createAuth = NativeServiceDispatch.ValidateExact(_kernel, caller, request.CreateCapability, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute);
                if (!createAuth.IsSuccess) return KernelResult<object?>.Fail(createAuth.Error, createAuth.Message!);
                if (request.Policy != ChildProcessPolicy.Attached)
                    return KernelResult<object?>.Fail(KernelError.PlatformUnsupported, "The initial ProcessService profile supports only session-attached children; independent-child adoption is not yet defined.");
                if (request.InitialCapabilities is null || request.InitialCapabilities.Count > 16)
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
                var created = _kernel.CreateProcess(request.Manifest);
                if (!created.IsSuccess) return KernelResult<object?>.Fail(created.Error, created.Message!);
                var child = new ProcessHandle(created.Value!.ProcessId, created.Value.Generation);
                var control = _kernel.MintCapability(created.Value.DomainId, caller, ResourceKind.Process, CapabilityResourceIds.Process(child), CapabilityRights.Configure | CapabilityRights.Execute | CapabilityRights.Delegate);
                if (!control.IsSuccess) { _ = _kernel.TerminateProcess(child); return KernelResult<object?>.Fail(control.Error, control.Message!); }
                var controlDescriptor = control.Value!;
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
                _children[child] = (caller, request.Policy, controlDescriptor.CapabilityId);
                return KernelResult<object?>.Ok(new ProcessAuthorityResponse(new ProcessAuthority(child, controlDescriptor.CapabilityId)));
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
            default: return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "ProcessService received an unknown message or payload shape.");
        }
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
    private readonly RuntimeKernel _kernel; private readonly ProcessHandle _service; private readonly EndpointSessionHandle _session; private readonly Dictionary<FileObjectId, Entry> _files = []; private ulong _nextId;
    private RuntimeFileServiceHost(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session) { _kernel = kernel; _service = service; _session = session; }
    public static KernelResult<RuntimeFileServiceHost> CreateForSession(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session)
    {
        var resolved = kernel.EndpointSessions.ResolveForService(session, service);
        if (!resolved.IsSuccess) return KernelResult<RuntimeFileServiceHost>.Fail(resolved.Error, resolved.Message!);
        var host = new RuntimeFileServiceHost(kernel, service, session);
        kernel.RegisterNativeServiceDrain(service, session, host.Drain);
        return KernelResult<RuntimeFileServiceHost>.Ok(host);
    }
    public KernelResult ProcessNext() => NativeServiceDispatch.Dispatch(_kernel, _service, _session, Execute);
    public KernelResult Drain()
    {
        foreach (var entry in _files.Values) { var revoked = _kernel.RevokeCapability(entry.Capability); if (!revoked.IsSuccess && revoked.Error != KernelError.CapabilityRevoked) return revoked; }
        _files.Clear();
        return KernelResult.Ok();
    }
    private KernelResult<object?> Execute(ProcessHandle caller, ChannelEnvelope envelope)
    {
        if (envelope.MessageId == IFileServiceProtocol.Message_OpenAsync && envelope.Payload is OpenFileRequest open)
        {
            var authority = NativeServiceDispatch.ValidateExact(_kernel, caller, open.NamespaceCapability, ResourceKind.File, CapabilityResourceIds.FileNamespace, open.Create ? CapabilityRights.Write : CapabilityRights.Read);
            if (!authority.IsSuccess) return KernelResult<object?>.Fail(authority.Error, authority.Message!);
            if (string.IsNullOrWhiteSpace(open.Path) || open.Path.Length > 200) return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "File path is malformed for service-side namespace policy.");
            var handle = new FileObjectHandle(_session, new FileObjectId(++_nextId), new FileObjectGeneration(1));
            var capability = _kernel.MintCapability(_kernel.Processes.Resolve(_service).Value!.DomainId, caller, ResourceKind.File, CapabilityResourceIds.FileObject(_session, handle.ObjectId.Value), CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure);
            if (!capability.IsSuccess) return KernelResult<object?>.Fail(capability.Error, capability.Message!);
            _files.Add(handle.ObjectId, new(caller, handle, capability.Value!.CapabilityId, open.Path));
            return KernelResult<object?>.Ok(new FileObjectResponse(new FileObjectAuthority(handle, capability.Value.CapabilityId)));
        }
        if (envelope.Payload is FileCommand command) return Command(caller, envelope.MessageId, command, null);
        if (envelope.Payload is FileWriteRequest write) return Command(caller, envelope.MessageId, new(write.File, write.Capability), write.Data);
        return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "FileService received an unknown message or payload shape.");
    }
    private KernelResult<object?> Command(ProcessHandle caller, uint message, FileCommand command, BoundedBytes? bytes)
    {
        if (command.File.Session != _session) return KernelResult<object?>.Fail(KernelError.WrongSessionOwner, "File object is bound to another endpoint session.");
        if (!_files.TryGetValue(command.File.ObjectId, out var entry) || entry.Handle != command.File) return KernelResult<object?>.Fail(KernelError.StaleHandle, "File object generation is stale or closed.");
        if (entry.Owner != caller) return KernelResult<object?>.Fail(KernelError.WrongCapabilitySubject, "File object belongs to another session caller.");
        var rights = message == IFileServiceProtocol.Message_ReadAsync ? CapabilityRights.Read : message == IFileServiceProtocol.Message_WriteAsync ? CapabilityRights.Write : CapabilityRights.Configure;
        var auth = NativeServiceDispatch.ValidateExact(_kernel, caller, command.Capability, ResourceKind.File, CapabilityResourceIds.FileObject(_session, command.File.ObjectId.Value), rights);
        if (!auth.IsSuccess) return KernelResult<object?>.Fail(auth.Error, auth.Message!);
        if (message == IFileServiceProtocol.Message_WriteAsync) { entry.Data = bytes!.Value.ToArray(); return KernelResult<object?>.Ok(null); }
        if (message == IFileServiceProtocol.Message_ReadAsync) return KernelResult<object?>.Ok(new FileReadResponse(new BoundedBytes(entry.Data)));
        if (message == IFileServiceProtocol.Message_CloseAsync) { _files.Remove(command.File.ObjectId); _ = _kernel.RevokeCapability(entry.Capability); return KernelResult<object?>.Ok(null); }
        return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "Unknown file operation.");
    }
}

public sealed class RuntimeNetworkServiceHost
{
    private sealed record Entry(ProcessHandle Owner, SocketObjectHandle Handle, CapabilityId Capability, string Label) { public Queue<byte[]> Packets { get; } = new(); }
    private readonly RuntimeKernel _kernel; private readonly ProcessHandle _service; private readonly EndpointSessionHandle _session; private readonly Dictionary<SocketObjectId, Entry> _sockets = []; private ulong _nextId;
    private RuntimeNetworkServiceHost(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session) { _kernel = kernel; _service = service; _session = session; }
    public static KernelResult<RuntimeNetworkServiceHost> CreateForSession(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session)
    {
        var resolved = kernel.EndpointSessions.ResolveForService(session, service);
        if (!resolved.IsSuccess) return KernelResult<RuntimeNetworkServiceHost>.Fail(resolved.Error, resolved.Message!);
        var host = new RuntimeNetworkServiceHost(kernel, service, session);
        kernel.RegisterNativeServiceDrain(service, session, host.Drain);
        return KernelResult<RuntimeNetworkServiceHost>.Ok(host);
    }
    public KernelResult ProcessNext() => NativeServiceDispatch.Dispatch(_kernel, _service, _session, Execute);
    public KernelResult Drain()
    {
        foreach (var entry in _sockets.Values) { var revoked = _kernel.RevokeCapability(entry.Capability); if (!revoked.IsSuccess && revoked.Error != KernelError.CapabilityRevoked) return revoked; }
        _sockets.Clear();
        return KernelResult.Ok();
    }
    private KernelResult<object?> Execute(ProcessHandle caller, ChannelEnvelope envelope)
    {
        if (envelope.MessageId == INetworkServiceProtocol.Message_OpenAsync && envelope.Payload is OpenSocketRequest open)
        {
            var authority = NativeServiceDispatch.ValidateExact(_kernel, caller, open.EndpointCapability, ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure);
            if (!authority.IsSuccess) return KernelResult<object?>.Fail(authority.Error, authority.Message!);
            if (string.IsNullOrWhiteSpace(open.EndpointLabel) || open.EndpointLabel.Length > 200) return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "Endpoint label is malformed for service-side policy.");
            var handle = new SocketObjectHandle(_session, new SocketObjectId(++_nextId), new SocketObjectGeneration(1));
            var capability = _kernel.MintCapability(_kernel.Processes.Resolve(_service).Value!.DomainId, caller, ResourceKind.Network, CapabilityResourceIds.SocketObject(_session, handle.ObjectId.Value), CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure);
            if (!capability.IsSuccess) return KernelResult<object?>.Fail(capability.Error, capability.Message!);
            _sockets.Add(handle.ObjectId, new(caller, handle, capability.Value!.CapabilityId, open.EndpointLabel));
            return KernelResult<object?>.Ok(new SocketObjectResponse(new SocketObjectAuthority(handle, capability.Value.CapabilityId)));
        }
        SocketCommand command; BoundedBytes? packet = null;
        if (envelope.Payload is SocketCommand exact) command = exact;
        else if (envelope.Payload is SendPacketRequest send) { command = new(send.Socket, send.Capability); packet = send.Packet; }
        else return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "NetworkService received an unknown message or payload shape.");
        if (command.Socket.Session != _session) return KernelResult<object?>.Fail(KernelError.WrongSessionOwner, "Socket object is bound to another endpoint session.");
        if (!_sockets.TryGetValue(command.Socket.ObjectId, out var entry) || entry.Handle != command.Socket) return KernelResult<object?>.Fail(KernelError.StaleHandle, "Socket object generation is stale or closed.");
        if (entry.Owner != caller) return KernelResult<object?>.Fail(KernelError.WrongCapabilitySubject, "Socket belongs to another session caller.");
        var rights = envelope.MessageId == INetworkServiceProtocol.Message_ReceiveAsync ? CapabilityRights.Read : envelope.MessageId == INetworkServiceProtocol.Message_SendAsync ? CapabilityRights.Write : CapabilityRights.Configure;
        var auth = NativeServiceDispatch.ValidateExact(_kernel, caller, command.Capability, ResourceKind.Network, CapabilityResourceIds.SocketObject(_session, command.Socket.ObjectId.Value), rights);
        if (!auth.IsSuccess) return KernelResult<object?>.Fail(auth.Error, auth.Message!);
        if (envelope.MessageId == INetworkServiceProtocol.Message_SendAsync) { entry.Packets.Enqueue(packet!.Value.ToArray()); return KernelResult<object?>.Ok(null); }
        if (envelope.MessageId == INetworkServiceProtocol.Message_ReceiveAsync) return entry.Packets.Count == 0 ? KernelResult<object?>.Fail(KernelError.ResponseNotAvailable, "No committed packet is available.") : KernelResult<object?>.Ok(new ReceivePacketResponse(new BoundedBytes(entry.Packets.Dequeue())));
        if (envelope.MessageId == INetworkServiceProtocol.Message_CloseAsync) { _sockets.Remove(command.Socket.ObjectId); _ = _kernel.RevokeCapability(entry.Capability); return KernelResult<object?>.Ok(null); }
        return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "Unknown network operation.");
    }
}
