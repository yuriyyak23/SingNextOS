using SingPlus.Contracts;
using SingPlus.Sip;
using SingPlus.Sip.Sdk;

namespace SingPlus.Runtime;

internal sealed class InlineSipInvocationLease : IDisposable
{
    private EndpointSessionPin? _sessionPin;

    internal InlineSipInvocationLease(TrustedSipInvocationContext context, EndpointSessionPin sessionPin, uint messageId) =>
        (Context, _sessionPin, MessageId) = (context, sessionPin, messageId);

    internal TrustedSipInvocationContext Context { get; }
    internal uint MessageId { get; }
    internal EndpointSessionPin SessionPin => _sessionPin ?? throw new ObjectDisposedException(nameof(InlineSipInvocationLease));
    public void Dispose() => Interlocked.Exchange(ref _sessionPin, null)?.Dispose();
}

internal sealed class InlineBorrowSipInvocationLease<T> where T : unmanaged
{
    private InlineSipInvocationLease? _invocation;

    internal InlineBorrowSipInvocationLease(
        InlineSipInvocationLease invocation,
        BorrowLease<T> borrow,
        RegionUseDescriptor use) =>
        (_invocation, Borrow, Use) = (invocation, borrow, use);

    internal BorrowLease<T> Borrow { get; }
    internal RegionUseDescriptor Use { get; }
    internal TrustedSipInvocationContext Context => Invocation.Context;
    internal InlineSipInvocationLease Invocation => _invocation ?? throw new ObjectDisposedException(nameof(InlineBorrowSipInvocationLease<T>));
    internal InlineSipInvocationLease TakeInvocation() =>
        Interlocked.Exchange(ref _invocation, null) ?? throw new ObjectDisposedException(nameof(InlineBorrowSipInvocationLease<T>));
}

internal sealed class InlineMoveSipInvocationLease<T> where T : unmanaged
{
    private InlineSipInvocationLease? _invocation;

    internal InlineMoveSipInvocationLease(InlineSipInvocationLease invocation, OwnedBuffer<T> payload) =>
        (_invocation, Payload) = (invocation, payload);

    internal OwnedBuffer<T> Payload { get; }
    internal TrustedSipInvocationContext Context => Invocation.Context;
    internal InlineSipInvocationLease Invocation => _invocation ?? throw new ObjectDisposedException(nameof(InlineMoveSipInvocationLease<T>));
    internal InlineSipInvocationLease TakeInvocation() =>
        Interlocked.Exchange(ref _invocation, null) ?? throw new ObjectDisposedException(nameof(InlineMoveSipInvocationLease<T>));
}

internal enum InlineBorrowQualificationPoint
{
    AfterSessionPin = 0,
    BeforeSessionRevalidation,
    AfterInvocationAccepted,
    BeforeRegionUseAcquisition,
    AfterRegionUseAcquisition,
    BeforeFinalRevalidation,
    AfterFinalRevalidation,
    BeforeRegionUseRelease,
    AfterRegionUseRelease,
    BeforeBorrowReturn,
    AfterBorrowReturn,
    BeforeInvocationSettlement,
    AfterInvocationSettlement,
}

internal interface IInlineBorrowQualificationHook
{
    void At(InlineBorrowQualificationPoint point);
}

public sealed partial class RuntimeKernel
{
    private EndpointSessionInvocationRegistry? _endpointSessionInvocationRegistry;
    private EndpointSessionInvocationRegistry SessionInvocations =>
        _endpointSessionInvocationRegistry ??= new EndpointSessionInvocationRegistry(CancellationScopes);

    internal Action? SessionInvocationSettlementReservedHook
    {
        set => SessionInvocations.SettlementReservedHook = value;
    }

    internal IInlineBorrowQualificationHook? InlineBorrowQualificationHook { private get; set; }

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
        var registered = Services.Register(provider, name, contract, protocol, responseProtocol, requiredCapabilities ?? []);
        if (registered.IsSuccess)
            RecordTrace(provider, TraceEventKind.ServiceLifecycle, null, "service",
                registered.Value!.Service.Id.Value.ToString(), registered.Value.Availability.ToString(), "registered");
        return registered;
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
        lock (_requestResponseCorrelationGate)
        {
            SessionInvocations.CloseSession(handle);
            var close = Channels.Close(session.Value!.Channel);
            return close.IsSuccess ? KernelResult.Ok() : close;
        }
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

    internal KernelResult<InlineSipInvocationLease> BeginInlineSessionInvocation(
        ProcessHandle caller,
        ProcessHandle service,
        EndpointSessionHandle sessionHandle,
        uint messageId,
        object? copiedPayload)
    {
        var callerProcess = Processes.Resolve(caller);
        if (!callerProcess.IsSuccess)
            return KernelResult<InlineSipInvocationLease>.Fail(callerProcess.Error, callerProcess.Message!);
        var serviceProcess = Processes.Resolve(service);
        if (!serviceProcess.IsSuccess)
            return KernelResult<InlineSipInvocationLease>.Fail(serviceProcess.Error, serviceProcess.Message!);
        var session = EndpointSessions.Resolve(sessionHandle, caller);
        if (!session.IsSuccess)
            return KernelResult<InlineSipInvocationLease>.Fail(session.Error, session.Message!);
        if (session.Value!.Service != service)
            return KernelResult<InlineSipInvocationLease>.Fail(KernelError.WrongSessionOwner, "Inline invocation service does not match the session peer.");

        var pinned = EndpointSessions.AcquirePin(sessionHandle, caller, service);
        if (!pinned.IsSuccess)
            return KernelResult<InlineSipInvocationLease>.Fail(pinned.Error, pinned.Message!);

        try
        {
            lock (_requestResponseCorrelationGate)
            {
                var currentPin = EndpointSessions.RevalidatePin(pinned.Value!);
                if (!currentPin.IsSuccess)
                    return KernelResult<InlineSipInvocationLease>.Fail(currentPin.Error, currentPin.Message!);
                var begun = Channels.BeginInlineCopiedInvocation(
                    callerProcess.Value!, serviceProcess.Value!, session.Value.Channel, messageId, copiedPayload);
                if (!begun.IsSuccess)
                    return KernelResult<InlineSipInvocationLease>.Fail(begun.Error, begun.Message!);
                var invocation = SessionInvocations.Register(
                    sessionHandle, caller, service, begun.Value, messageId);
                var delivered = SessionInvocations.MarkDelivered(sessionHandle, service, begun.Value);
                if (!delivered.IsSuccess)
                    return KernelResult<InlineSipInvocationLease>.Fail(delivered.Error, delivered.Message!);
                var accepted = SessionInvocations.AcceptInvocation(invocation, service, allowInFlightCancellation: false);
                if (!accepted.IsSuccess)
                    return KernelResult<InlineSipInvocationLease>.Fail(accepted.Error, accepted.Message!);
                var context = new TrustedSipInvocationContext(caller, service, sessionHandle, invocation);
                var lease = new InlineSipInvocationLease(context, pinned.Value!, messageId);
                pinned = default;
                return KernelResult<InlineSipInvocationLease>.Ok(lease);
            }
        }
        finally
        {
            if (pinned.Value is { } remainingPin)
                FinalizeSessionPin(remainingPin, sessionHandle);
        }
    }

    internal KernelResult RevalidateInlineSessionInvocation(InlineSipInvocationLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return EndpointSessions.RevalidatePin(lease.SessionPin);
    }

    internal KernelResult<InlineBorrowSipInvocationLease<T>> BeginInlineBorrowSessionInvocation<T>(
        ProcessHandle caller,
        ProcessHandle service,
        EndpointSessionHandle sessionHandle,
        uint messageId,
        OwnedBuffer<T> payload) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(payload);
        var callerProcess = Processes.Resolve(caller);
        if (!callerProcess.IsSuccess)
            return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(callerProcess.Error, callerProcess.Message!);
        var serviceProcess = Processes.Resolve(service);
        if (!serviceProcess.IsSuccess)
            return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(serviceProcess.Error, serviceProcess.Message!);
        var session = EndpointSessions.Resolve(sessionHandle, caller);
        if (!session.IsSuccess)
            return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(session.Error, session.Message!);
        if (session.Value!.Service != service)
            return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(KernelError.WrongSessionOwner, "Inline BORROW service does not match the session peer.");

        var pinned = EndpointSessions.AcquirePin(sessionHandle, caller, service);
        if (!pinned.IsSuccess)
            return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(pinned.Error, pinned.Message!);
        InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.AfterSessionPin);
        InlineSipInvocationLease? invocationLease = null;
        BorrowLease<T>? borrow = null;
        try
        {
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.BeforeSessionRevalidation);
            lock (_requestResponseCorrelationGate)
            {
                var currentPin = EndpointSessions.RevalidatePin(pinned.Value!);
                if (!currentPin.IsSuccess)
                    return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(currentPin.Error, currentPin.Message!);
                var begun = Channels.BeginInlineBorrowInvocation(
                    callerProcess.Value!, serviceProcess.Value!, session.Value.Channel, messageId, payload);
                if (!begun.IsSuccess)
                    return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(begun.Error, begun.Message!);
                borrow = begun.Value.Lease;
                var invocation = SessionInvocations.Register(sessionHandle, caller, service, begun.Value.Sequence, messageId);
                var delivered = SessionInvocations.MarkDelivered(sessionHandle, service, begun.Value.Sequence);
                if (!delivered.IsSuccess)
                    return FailAfterInlineBorrow(delivered.Error, delivered.Message!, service, borrow, invocationLease);
                var accepted = SessionInvocations.AcceptInvocation(invocation, service, allowInFlightCancellation: false);
                if (!accepted.IsSuccess)
                    return FailAfterInlineBorrow(accepted.Error, accepted.Message!, service, borrow, invocationLease);
                invocationLease = new InlineSipInvocationLease(
                    new TrustedSipInvocationContext(caller, service, sessionHandle, invocation), pinned.Value!, messageId);
                pinned = default;
            }
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.AfterInvocationAccepted);

            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.BeforeRegionUseAcquisition);
            var use = AcquireBorrowRegionUse(
                caller, service, borrow.Handle, RegionUseMode.ReadOnly,
                new RegionUseRange(0, checked((long)borrow.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<T>())));
            if (!use.IsSuccess)
            {
                _ = SessionInvocations.CompleteInline(invocationLease.Context.Invocation, service, succeeded: false);
                _ = ReturnBorrow(service, borrow.Handle);
                FinalizeInlineInvocationPin(invocationLease);
                invocationLease = null;
                return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(use.Error, use.Message!);
            }
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.AfterRegionUseAcquisition);

            var result = new InlineBorrowSipInvocationLease<T>(invocationLease, borrow, use.Value!);
            invocationLease = null;
            return KernelResult<InlineBorrowSipInvocationLease<T>>.Ok(result);
        }
        finally
        {
            invocationLease?.Dispose();
            if (pinned.Value is { } remainingPin)
                FinalizeSessionPin(remainingPin, sessionHandle);
        }
    }

    private KernelResult<InlineBorrowSipInvocationLease<T>> FailAfterInlineBorrow<T>(
        KernelError error,
        string message,
        ProcessHandle service,
        BorrowLease<T> borrow,
        InlineSipInvocationLease? invocation) where T : unmanaged
    {
        _ = ReturnBorrow(service, borrow.Handle);
        invocation?.Dispose();
        return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(error, message);
    }

    internal KernelResult SettleInlineBorrowSessionInvocation<T>(
        ProcessHandle service,
        InlineBorrowSipInvocationLease<T> lease,
        bool succeeded) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Context.Service != service)
            return KernelResult.Fail(KernelError.WrongSessionOwner, "Inline BORROW context does not match the live session parties.");
        var invocation = lease.TakeInvocation();
        var context = invocation.Context;
        try
        {
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.BeforeRegionUseRelease);
            var released = ReleaseRegionUse(service, lease.Use.Handle);
            if (!released.IsSuccess) return released;
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.AfterRegionUseRelease);
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.BeforeBorrowReturn);
            var returned = ReturnBorrow(service, lease.Borrow.Handle);
            if (!returned.IsSuccess) return returned;
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.AfterBorrowReturn);
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.BeforeInvocationSettlement);
            var settled = SessionInvocations.CompleteInline(context.Invocation, service, succeeded);
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.AfterInvocationSettlement);
            return settled;
        }
        finally
        {
            FinalizeInlineInvocationPin(invocation);
        }
    }

    internal KernelResult RevalidateInlineBorrowSessionInvocation<T>(
        InlineBorrowSipInvocationLease<T> lease) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(lease);
        InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.BeforeFinalRevalidation);
        var session = EndpointSessions.RevalidatePin(lease.Invocation.SessionPin);
        if (!session.IsSuccess) return session;
        if (!lease.Borrow.IsValid)
            return KernelResult.Fail(KernelError.InvalidRegionState, "Inline BORROW lease is no longer live.");
        var use = ValidateRegionUse(lease.Context.Service, lease.Use.Handle);
        if (!use.IsSuccess) return KernelResult.Fail(use.Error, use.Message!);
        InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.AfterFinalRevalidation);
        return KernelResult.Ok();
    }

    internal KernelResult<OwnedBuffer<T>> TransferInlineOwnershipResponse<T>(
        ProcessHandle service,
        InlineSipInvocationLease invocation,
        OwnedBuffer<T> payload) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(payload);
        var context = invocation.Context;
        if (context.Service != service)
            return KernelResult<OwnedBuffer<T>>.Fail(KernelError.WrongSessionOwner, "Inline ownership response does not match the live service principal.");
        var current = RevalidateInlineSessionInvocation(invocation);
        if (!current.IsSuccess)
            return KernelResult<OwnedBuffer<T>>.Fail(current.Error, current.Message!);
        var serviceProcess = Processes.Resolve(service);
        if (!serviceProcess.IsSuccess)
            return KernelResult<OwnedBuffer<T>>.Fail(serviceProcess.Error, serviceProcess.Message!);
        var callerProcess = Processes.Resolve(context.Caller);
        if (!callerProcess.IsSuccess)
            return KernelResult<OwnedBuffer<T>>.Fail(callerProcess.Error, callerProcess.Message!);

        var session = EndpointSessions.ResolveForService(context.Session, service);
        if (!session.IsSuccess)
            return KernelResult<OwnedBuffer<T>>.Fail(session.Error, session.Message!);
        var moved = Responses.TransferInlineOwnership(
            serviceProcess.Value!, callerProcess.Value!, session.Value!.ServiceEndpoint, invocation.MessageId, payload);
        if (!moved.IsSuccess)
            return KernelResult<OwnedBuffer<T>>.Fail(moved.Error, moved.Message!);
        var settled = SettleInlineSessionInvocation(service, invocation, succeeded: true);
        return settled.IsSuccess
            ? moved
            : KernelResult<OwnedBuffer<T>>.Fail(settled.Error, settled.Message!);
    }

    internal KernelResult<InlineMoveSipInvocationLease<T>> BeginInlineMoveSessionInvocation<T>(
        ProcessHandle caller,
        ProcessHandle service,
        EndpointSessionHandle sessionHandle,
        uint messageId,
        OwnedBuffer<T> payload) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(payload);
        var callerProcess = Processes.Resolve(caller);
        if (!callerProcess.IsSuccess)
            return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(callerProcess.Error, callerProcess.Message!);
        var serviceProcess = Processes.Resolve(service);
        if (!serviceProcess.IsSuccess)
            return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(serviceProcess.Error, serviceProcess.Message!);
        var session = EndpointSessions.Resolve(sessionHandle, caller);
        if (!session.IsSuccess)
            return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(session.Error, session.Message!);
        if (session.Value!.Service != service)
            return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(KernelError.WrongSessionOwner, "Inline MOVE service does not match the session peer.");
        var pinned = EndpointSessions.AcquirePin(sessionHandle, caller, service);
        if (!pinned.IsSuccess)
            return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(pinned.Error, pinned.Message!);

        try
        {
            lock (_requestResponseCorrelationGate)
            {
                var currentPin = EndpointSessions.RevalidatePin(pinned.Value!);
                if (!currentPin.IsSuccess)
                    return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(currentPin.Error, currentPin.Message!);
                var begun = Channels.BeginInlineMoveInvocation(
                    callerProcess.Value!, serviceProcess.Value!, session.Value.Channel, messageId, payload);
                if (!begun.IsSuccess)
                    return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(begun.Error, begun.Message!);
                var invocation = SessionInvocations.Register(
                    sessionHandle, caller, service, begun.Value.Sequence, messageId);
                var delivered = SessionInvocations.MarkDelivered(sessionHandle, service, begun.Value.Sequence);
                if (!delivered.IsSuccess)
                    return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(delivered.Error, delivered.Message!);
                var accepted = SessionInvocations.AcceptInvocation(invocation, service, allowInFlightCancellation: false);
                if (!accepted.IsSuccess)
                    return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(accepted.Error, accepted.Message!);
                var inline = new InlineSipInvocationLease(
                    new TrustedSipInvocationContext(caller, service, sessionHandle, invocation), pinned.Value!, messageId);
                pinned = default;
                return KernelResult<InlineMoveSipInvocationLease<T>>.Ok(new(inline, begun.Value.Payload));
            }
        }
        finally
        {
            if (pinned.Value is { } remainingPin)
                FinalizeSessionPin(remainingPin, sessionHandle);
        }
    }

    internal KernelResult RevalidateInlineMoveSessionInvocation<T>(
        InlineMoveSipInvocationLease<T> lease) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(lease);
        var session = EndpointSessions.RevalidatePin(lease.Invocation.SessionPin);
        if (!session.IsSuccess) return session;
        if (!lease.Payload.IsValid)
            return KernelResult.Fail(KernelError.InvalidRegionState, "Inline MOVE payload is no longer owned by the consumer stage.");
        var service = Processes.Resolve(lease.Context.Service);
        if (!service.IsSuccess) return KernelResult.Fail(service.Error, service.Message!);
        var authoritative = Regions.Validate(
            lease.Payload.Handle,
            new RegionOwner(service.Value!.DomainId, lease.Context.Service.Generation));
        return authoritative.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(authoritative.Error, authoritative.Message!);
    }

    internal KernelResult SettleInlineMoveSessionInvocation<T>(
        ProcessHandle service,
        InlineMoveSipInvocationLease<T> lease,
        bool succeeded) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Context.Service != service)
            return KernelResult.Fail(KernelError.WrongSessionOwner, "Inline MOVE context does not match the live service principal.");
        var invocation = lease.TakeInvocation();
        try
        {
            // MOVE is already committed. Settlement never invents an inverse transfer.
            return SessionInvocations.CompleteInline(invocation.Context.Invocation, service, succeeded);
        }
        finally
        {
            FinalizeInlineInvocationPin(invocation);
        }
    }

    internal KernelResult SettleInlineSessionInvocation(
        ProcessHandle service,
        InlineSipInvocationLease lease,
        bool succeeded)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var context = lease.Context;
        try
        {
            if (context.Service != service)
                return KernelResult.Fail(KernelError.WrongSessionOwner, "Inline invocation context does not match the live session parties.");
            return SessionInvocations.CompleteInline(context.Invocation, service, succeeded);
        }
        finally
        {
            FinalizeInlineInvocationPin(lease);
        }
    }

    private void FinalizeInlineInvocationPin(InlineSipInvocationLease invocation)
    {
        var session = invocation.Context.Session;
        invocation.Dispose();
        CompleteDeferredSessionCleanup(session);
    }

    private void FinalizeSessionPin(EndpointSessionPin pin, EndpointSessionHandle session)
    {
        pin.Dispose();
        CompleteDeferredSessionCleanup(session);
    }

    private void CompleteDeferredSessionCleanup(EndpointSessionHandle session)
    {
        var deferredClose = EndpointSessions.ClaimClosedCleanup(session);
        if (deferredClose is null) return;
        lock (_requestResponseCorrelationGate)
        {
            SessionInvocations.CloseSession(session);
            _ = Channels.Close(deferredClose.Channel);
        }
    }

    public KernelResult RequestSessionCancellation(
        ProcessHandle caller,
        EndpointSessionInvocationHandle invocation)
    {
        var session = ResolveSession(caller, invocation.Session);
        if (!session.IsSuccess) return KernelResult.Fail(session.Error, session.Message!);
        return SessionInvocations.RequestCancellation(invocation, caller);
    }

    public KernelResult BindSessionCancellationScope(
        ProcessHandle caller,
        EndpointSessionInvocationHandle invocation,
        CancellationScopeHandle scope)
    {
        var session = ResolveSession(caller, invocation.Session);
        if (!session.IsSuccess) return KernelResult.Fail(session.Error, session.Message!);
        return SessionInvocations.BindCancellationScope(invocation, caller, scope);
    }

    public KernelResult<CancellationObservation> RequestSessionCancellation(
        ProcessHandle caller,
        EndpointSessionInvocationHandle invocation,
        CancellationScopeHandle scope)
    {
        var session = ResolveSession(caller, invocation.Session);
        if (!session.IsSuccess) return KernelResult<CancellationObservation>.Fail(session.Error, session.Message!);
        return SessionInvocations.RequestCancellation(invocation, caller, scope);
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
        var pinned = EndpointSessions.AcquirePin(handle, caller, current.Service);
        if (!pinned.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(pinned.Error, pinned.Message!);
        using var sessionPin = pinned.Value!;
        var currentPin = EndpointSessions.RevalidatePin(sessionPin);
        if (!currentPin.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(currentPin.Error, currentPin.Message!);
        var send = Send(caller, current.Service, current.Channel, messageId, payload, current.Capabilities);
        if (!send.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(send.Error, send.Message!);
        var invocation = SessionInvocations.Register(handle, caller, current.Service, send.Value!.Sequence, messageId);
        sessionPin.Dispose();
        using var cancellation = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => _ = RequestSessionCancellation(caller, invocation))
            : default;
        return await WaitForResponseAsync(caller, current.Channel, send.Value.Sequence).ConfigureAwait(false);
    }

    public async ValueTask<KernelResult<ResponseEnvelope>> InvokeSessionUntilCancellationAsync(
        ProcessHandle caller,
        EndpointSessionHandle handle,
        uint messageId,
        object? payload,
        CancellationScopeHandle scope,
        CancellationToken stopWaiting)
    {
        var temporal = CancellationScopes.Observe(caller, scope);
        if (!temporal.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(temporal.Error, temporal.Message!);
        if (temporal.Value!.Disposition == CancellationDisposition.Stale)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.StaleGeneration, "Cancellation scope generation is stale.");
        if (temporal.Value.CancellationRequested)
        {
            _ = CancellationScopes.RecordDisposition(caller, scope, CancellationDisposition.CancelledBeforeEffect);
            return KernelResult<ResponseEnvelope>.Fail(KernelError.DeadlineExpired, "IPC deadline expired before transfer admission.");
        }

        var session = ResolveSession(caller, handle);
        if (!session.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(session.Error, session.Message!);
        var claim = CancellationScopes.ClaimConsumer(
            caller,
            scope,
            $"ipc:{handle.SessionId.Value}:{handle.Generation.Value}:{scope.ScopeId.Value}:{scope.Generation.Value}");
        if (!claim.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(claim.Error, claim.Message!);

        var current = session.Value!;
        var pinned = EndpointSessions.AcquirePin(handle, caller, current.Service);
        if (!pinned.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(pinned.Error, pinned.Message!);
        using var sessionPin = pinned.Value!;
        var currentPin = EndpointSessions.RevalidatePin(sessionPin);
        if (!currentPin.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(currentPin.Error, currentPin.Message!);
        var send = Send(caller, current.Service, current.Channel, messageId, payload, current.Capabilities);
        if (!send.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(send.Error, send.Message!);
        var invocation = SessionInvocations.Register(
            handle, caller, current.Service, send.Value!.Sequence, messageId, scope);
        sessionPin.Dispose();
        var response = WaitForResponseAsync(caller, current.Channel, send.Value.Sequence).AsTask();
        if (!stopWaiting.CanBeCanceled)
            return await response.ConfigureAwait(false);

        var stopped = Task.Delay(Timeout.InfiniteTimeSpan, stopWaiting);
        if (await Task.WhenAny(response, stopped).ConfigureAwait(false) == response)
            return await response.ConfigureAwait(false);

        var cancellation = SessionInvocations.RequestCancellation(invocation, caller, scope);
        if (!cancellation.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(cancellation.Error, cancellation.Message!);
        return KernelResult<ResponseEnvelope>.Fail(
            KernelError.CancellationPending,
            $"Caller wait stopped with {cancellation.Value!.Disposition}; callee or provider work may still require exact settlement.");
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
        var pinned = EndpointSessions.AcquirePin(handle, caller, current.Service);
        if (!pinned.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(pinned.Error, pinned.Message!);
        using var sessionPin = pinned.Value!;
        var currentPin = EndpointSessions.RevalidatePin(sessionPin);
        if (!currentPin.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(currentPin.Error, currentPin.Message!);
        var send = SendOwnershipPair(caller, current.Service, current.Channel, messageId, first, second, current.Capabilities);
        if (!send.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(send.Error, send.Message!);
        var invocation = SessionInvocations.Register(handle, caller, current.Service, send.Value!.Sequence, messageId);
        sessionPin.Dispose();
        using var cancellation = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => _ = RequestSessionCancellation(caller, invocation))
            : default;
        return await WaitForResponseAsync(caller, current.Channel, send.Value.Sequence).ConfigureAwait(false);
    }
}
