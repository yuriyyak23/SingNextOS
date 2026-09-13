using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private EndpointSessionInvocationRegistry? _endpointSessionInvocationRegistry;
    private EndpointSessionInvocationRegistry SessionInvocations =>
        _endpointSessionInvocationRegistry ??= new EndpointSessionInvocationRegistry();

    public KernelResult<ServiceEndpointDescriptor> RegisterService(
        ProcessHandle provider,
        string name,
        ServiceContractIdentity contract,
        ProtocolDefinitionV1 protocol,
        ResponseProtocolDefinitionV1? responseProtocol = null,
        IReadOnlyList<CapabilityRequirementV1>? requiredCapabilities = null)
    {
        var process = Processes.Resolve(provider);
        if (!process.IsSuccess) return KernelResult<ServiceEndpointDescriptor>.Fail(process.Error, process.Message!);
        var effect = EnsureProcessAcceptsNewEffects(process.Value!);
        if (!effect.IsSuccess) return KernelResult<ServiceEndpointDescriptor>.Fail(effect.Error, effect.Message!);
        return Services.Register(provider, name, contract, protocol, responseProtocol, requiredCapabilities ?? []);
    }

    public KernelResult<ServiceEndpointDescriptor[]> ResolveByContract(ServiceContractIdentity contract) =>
        Services.ResolveByContract(contract);

    public KernelResult<ServiceEndpointDescriptor> ResolveByServiceName(string name) =>
        Services.ResolveByServiceName(name);

    public KernelResult SetServiceAvailability(ServiceIdentity service, ServiceAvailability availability) =>
        Services.SetAvailability(service, availability);

    public KernelResult<EndpointSessionHandle> OpenSession(
        ProcessHandle caller,
        ServiceEndpointDescriptor descriptor,
        IReadOnlyCollection<CapabilityId>? admissionCapabilities = null,
        int capacity = 8,
        TimeSpan? lifetime = null)
    {
        if (lifetime is { } requestedLifetime && requestedLifetime <= TimeSpan.Zero)
            return KernelResult<EndpointSessionHandle>.Fail(KernelError.SessionTimedOut, "Session lifetime must be positive.");
        var callerResult = Processes.Resolve(caller);
        if (!callerResult.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(callerResult.Error, callerResult.Message!);
        var callerEffect = EnsureProcessAcceptsNewEffects(callerResult.Value!);
        if (!callerEffect.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(callerEffect.Error, callerEffect.Message!);
        var serviceResult = Services.Resolve(descriptor);
        if (!serviceResult.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(serviceResult.Error, serviceResult.Message!);
        var service = serviceResult.Value!;
        var providerResult = Processes.Resolve(service.Provider);
        if (!providerResult.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(providerResult.Error, providerResult.Message!);
        var providerEffect = EnsureProcessAcceptsNewEffects(providerResult.Value!);
        if (!providerEffect.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(providerEffect.Error, providerEffect.Message!);

        var capabilityIds = admissionCapabilities?.ToArray() ?? [];
        foreach (var requirement in service.RequiredCapabilities)
        {
            var found = capabilityIds.Any(id =>
            {
                var validation = CapabilityAuthority.Validate(id, callerResult.Value!.DomainId, caller.Generation, requirement.Rights);
                return validation.IsSuccess && validation.Value!.ResourceKind == requirement.ResourceKind && validation.Value.ResourceId == requirement.ResourceId;
            });
            if (!found) return KernelResult<EndpointSessionHandle>.Fail(KernelError.MissingCapability, $"Session requires {requirement.ResourceKind}:{requirement.ResourceId} ({requirement.Rights}).");
        }

        var channel = service.ResponseProtocol is null
            ? CreateChannel(caller, service.Provider, service.Protocol, capacity)
            : CreateChannel(caller, service.Provider, service.Protocol, service.ResponseProtocol, capacity);
        if (!channel.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(channel.Error, channel.Message!);
        var session = EndpointSessions.Add(caller, service.Provider, capabilityIds, service.RequiredCapabilities, channel.Value!.Left, lifetime);
        return session.IsSuccess
            ? KernelResult<EndpointSessionHandle>.Ok(session.Value!.Handle)
            : KernelResult<EndpointSessionHandle>.Fail(session.Error, session.Message!);
    }

    public KernelResult CloseSession(ProcessHandle caller, EndpointSessionHandle handle)
    {
        var nativeDrain = DrainNativeServiceSession(handle);
        if (!nativeDrain.IsSuccess) return nativeDrain;
        var session = EndpointSessions.Close(handle, caller);
        if (!session.IsSuccess) return KernelResult.Fail(session.Error, session.Message!);
        SessionInvocations.CloseSession(handle);
        var close = Channels.Close(session.Value!.Channel);
        return close.IsSuccess ? KernelResult.Ok() : close;
    }

    public KernelResult<ChannelEnvelope> ReceiveSession(ProcessHandle service, EndpointSessionHandle handle)
    {
        var request = ReceiveSessionRequest(service, handle);
        return request.IsSuccess
            ? KernelResult<ChannelEnvelope>.Ok(request.Value!.Request)
            : KernelResult<ChannelEnvelope>.Fail(request.Error, request.Message!);
    }

    public KernelResult<EndpointSessionRequestEnvelope> ReceiveSessionRequest(
        ProcessHandle service,
        EndpointSessionHandle handle)
    {
        var session = EndpointSessions.ResolveForService(handle, service);
        if (!session.IsSuccess)
            return KernelResult<EndpointSessionRequestEnvelope>.Fail(session.Error, session.Message!);

        var received = Receive(service, session.Value!.ServiceEndpoint);
        if (!received.IsSuccess)
            return KernelResult<EndpointSessionRequestEnvelope>.Fail(received.Error, received.Message!);

        var correlation = SessionInvocations.Register(
            handle,
            session.Value.Caller,
            service,
            received.Value!.Sequence,
            received.Value.MessageId);
        var invocation = SessionInvocations.MarkDelivered(handle, service, received.Value.Sequence);
        if (!invocation.IsSuccess)
            return KernelResult<EndpointSessionRequestEnvelope>.Fail(invocation.Error, invocation.Message!);

        var cancellation = SessionInvocations.IsCancellationRequested(correlation, service);
        if (!cancellation.IsSuccess)
            return KernelResult<EndpointSessionRequestEnvelope>.Fail(cancellation.Error, cancellation.Message!);

        return KernelResult<EndpointSessionRequestEnvelope>.Ok(
            new EndpointSessionRequestEnvelope(correlation, received.Value, cancellation.Value));
    }

    public KernelResult<ResponseEnvelope> PublishSessionResponse(
        ProcessHandle service,
        EndpointSessionHandle handle,
        ulong requestSequence,
        object? payload = null) =>
        PublishSessionResponse(
            service,
            new EndpointSessionInvocationHandle(
                handle,
                new EndpointSessionInvocationId(requestSequence),
                new EndpointSessionInvocationGeneration(1)),
            payload);

    public KernelResult<ResponseEnvelope> PublishSessionResponse(
        ProcessHandle service,
        EndpointSessionInvocationHandle invocation,
        object? payload = null)
    {
        var session = EndpointSessions.ResolveForService(invocation.Session, service);
        if (!session.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(session.Error, session.Message!);
        return SessionInvocations.Publish(
            invocation,
            service,
            () => PublishResponse(service, session.Value!.ServiceEndpoint, invocation.InvocationId.Value, payload));
    }

    public KernelResult<ResponseEnvelope> CancelSessionResponse(
        ProcessHandle service,
        EndpointSessionInvocationHandle invocation)
    {
        var session = EndpointSessions.ResolveForService(invocation.Session, service);
        if (!session.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(session.Error, session.Message!);
        return SessionInvocations.Cancel(
            invocation,
            service,
            () => CancelResponse(service, session.Value!.ServiceEndpoint, invocation.InvocationId.Value));
    }

    public KernelResult RequestSessionCancellation(
        ProcessHandle caller,
        EndpointSessionInvocationHandle invocation)
    {
        var session = ResolveSession(caller, invocation.Session);
        if (!session.IsSuccess) return KernelResult.Fail(session.Error, session.Message!);
        return SessionInvocations.RequestCancellation(invocation, caller);
    }

    public KernelResult<bool> QuerySessionCancellation(
        ProcessHandle service,
        EndpointSessionInvocationHandle invocation)
    {
        var session = EndpointSessions.ResolveForService(invocation.Session, service);
        if (!session.IsSuccess) return KernelResult<bool>.Fail(session.Error, session.Message!);
        return SessionInvocations.IsCancellationRequested(invocation, service);
    }

    internal KernelResult<EndpointSessionRegistry.Record> ResolveSession(ProcessHandle caller, EndpointSessionHandle handle)
    {
        if (EndpointSessions.ExpireIfDue(handle, caller) is { } expired)
        {
            SessionInvocations.CloseSession(handle);
            _ = Channels.Close(expired.Channel);
            return KernelResult<EndpointSessionRegistry.Record>.Fail(KernelError.SessionTimedOut, "Endpoint session lifetime expired.");
        }
        var session = EndpointSessions.Resolve(handle, caller);
        if (!session.IsSuccess) return session;
        var callerProcess = Processes.Resolve(caller);
        if (!callerProcess.IsSuccess) return KernelResult<EndpointSessionRegistry.Record>.Fail(callerProcess.Error, callerProcess.Message!);
        foreach (var capability in session.Value!.Capabilities)
        {
            var validation = ValidateCapability(caller, capability, CapabilityRights.None);
            if (!validation.IsSuccess) return KernelResult<EndpointSessionRegistry.Record>.Fail(validation.Error, validation.Message!);
        }
        foreach (var requirement in session.Value.Requirements)
        {
            var found = session.Value.Capabilities.Any(id =>
            {
                var validation = CapabilityAuthority.Validate(id, callerProcess.Value!.DomainId, caller.Generation, requirement.Rights);
                return validation.IsSuccess && validation.Value!.ResourceKind == requirement.ResourceKind && validation.Value.ResourceId == requirement.ResourceId;
            });
            if (!found) return KernelResult<EndpointSessionRegistry.Record>.Fail(KernelError.CapabilityRevoked, "Session admission capability is no longer live.");
        }
        return session;
    }

    internal KernelResult CloseSessionsForProcess(ProcessHandle process)
    {
        foreach (var session in EndpointSessions.CloseForProcess(process))
        {
            var nativeDrain = DrainNativeServiceSession(session.Handle);
            if (!nativeDrain.IsSuccess) return nativeDrain;
            SessionInvocations.CloseSession(session.Handle);
            _ = Channels.Close(session.Channel);
        }
        Services.RetireForProvider(process);
        return KernelResult.Ok();
    }

    internal ValueTask<KernelResult<ResponseEnvelope>> InvokeSessionAsync(
        ProcessHandle caller,
        EndpointSessionHandle handle,
        uint messageId,
        object? payload) =>
        InvokeSessionAsync(caller, handle, messageId, payload, CancellationToken.None);

    internal async ValueTask<KernelResult<ResponseEnvelope>> InvokeSessionAsync(
        ProcessHandle caller,
        EndpointSessionHandle handle,
        uint messageId,
        object? payload,
        CancellationToken cancellationToken)
    {
        var session = ResolveSession(caller, handle);
        if (!session.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(session.Error, session.Message!);
        var current = session.Value!;
        var send = Send(caller, current.Service, current.Channel, messageId, payload, current.Capabilities);
        if (!send.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(send.Error, send.Message!);
        var invocation = SessionInvocations.Register(handle, caller, current.Service, send.Value!.Sequence, messageId);
        using var cancellation = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => _ = RequestSessionCancellation(caller, invocation))
            : default;
        return await WaitForResponseAsync(caller, current.Channel, send.Value.Sequence).ConfigureAwait(false);
    }

    internal ValueTask<KernelResult<ResponseEnvelope>> InvokeSessionOwnershipPairAsync(
        ProcessHandle caller,
        EndpointSessionHandle handle,
        uint messageId,
        object first,
        object second) =>
        InvokeSessionOwnershipPairAsync(caller, handle, messageId, first, second, CancellationToken.None);

    internal async ValueTask<KernelResult<ResponseEnvelope>> InvokeSessionOwnershipPairAsync(
        ProcessHandle caller,
        EndpointSessionHandle handle,
        uint messageId,
        object first,
        object second,
        CancellationToken cancellationToken)
    {
        var session = ResolveSession(caller, handle);
        if (!session.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(session.Error, session.Message!);
        var current = session.Value!;
        var send = SendOwnershipPair(caller, current.Service, current.Channel, messageId, first, second, current.Capabilities);
        if (!send.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(send.Error, send.Message!);
        var invocation = SessionInvocations.Register(handle, caller, current.Service, send.Value!.Sequence, messageId);
        using var cancellation = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => _ = RequestSessionCancellation(caller, invocation))
            : default;
        return await WaitForResponseAsync(caller, current.Channel, send.Value.Sequence).ConfigureAwait(false);
    }
}
