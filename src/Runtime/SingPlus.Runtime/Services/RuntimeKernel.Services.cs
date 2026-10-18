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
        _endpointSessionInvocationRegistry ??= new EndpointSessionInvocationRegistry(
            CancellationScopes, _requestResponseCorrelationGate);

    internal EndpointSessionInvocationRegistry.InvocationConsequenceSnapshot?
        SnapshotSessionInvocationConsequence(EndpointSessionInvocationHandle invocation) =>
        SessionInvocations.SnapshotConsequence(invocation);

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
        => RegisterServiceCore(provider, name, contract, protocol, responseProtocol, requiredCapabilities, null);

    private KernelResult<ServiceEndpointDescriptor> RegisterServiceCore(ProcessHandle provider, string name,
        ServiceContractIdentity contract, ProtocolDefinitionV1 protocol, ResponseProtocolDefinitionV1? responseProtocol,
        IReadOnlyList<CapabilityRequirementV1>? requiredCapabilities,
        (ProcessHandle Principal, CapabilityId Capability)? source,
        Func<Func<KernelResult>, KernelResult>? sourceCommit = null)
    {
        var requirements = requiredCapabilities?.ToArray() ?? [];
        KernelResult<ServiceEndpointDescriptor> registered;
        lock (_platformMemoryUseGate)
        {
        var process = Processes.Resolve(provider);
        if (!process.IsSuccess) return KernelResult<ServiceEndpointDescriptor>.Fail(process.Error, process.Message!);
        var effect = EnsureProcessAcceptsNewEffects(process.Value!);
        if (!effect.IsSuccess) return KernelResult<ServiceEndpointDescriptor>.Fail(effect.Error, effect.Message!);
        registered = Services.Register(provider, name, contract, protocol, responseProtocol, requirements,
            sourceCommit is not null ? commit => CommitRestoreValue(sourceCommit, commit)
            : source is { } controller ? commit =>
            {
                var principal = Processes.Resolve(controller.Principal);
                if (!principal.IsSuccess) return KernelResult<ServiceEndpointDescriptor>.Fail(KernelError.SupervisorDenied, principal.Message!);
                var accepts = EnsureProcessAcceptsNewEffects(principal.Value!);
                if (!accepts.IsSuccess) return KernelResult<ServiceEndpointDescriptor>.Fail(KernelError.SupervisorDenied, accepts.Message!);
                return CapabilityAuthority.CommitSupervisorServiceRegistration(controller.Capability,
                    principal.Value!.DomainId, controller.Principal.Generation, commit);
            } : null);
        }
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
        TimeSpan? lifetime = null) =>
        OpenSessionCore(caller, descriptor, admissionCapabilities, capacity, lifetime, source: null);

    private KernelResult<EndpointSessionHandle> OpenSessionCore(ProcessHandle caller,
        ServiceEndpointDescriptor descriptor, IReadOnlyCollection<CapabilityId>? admissionCapabilities,
        int capacity, TimeSpan? lifetime, (ProcessHandle Principal, CapabilityId Capability)? source,
        Func<Func<KernelResult>, KernelResult>? sourceCommit = null)
    {
        if (sourceCommit is not null && lifetime is not null)
            return KernelResult<EndpointSessionHandle>.Fail(KernelError.InvalidTransition, "Restore dependency admission requires a lifetime-free local commit.");
        if (source is not null && lifetime is not null)
            return KernelResult<EndpointSessionHandle>.Fail(KernelError.InvalidTransition, "Supervisor rebind requires lifetime-free local admission.");
        CapabilityId[] capabilityIds;
        try { capabilityIds = admissionCapabilities?.ToArray() ?? []; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { return KernelResult<EndpointSessionHandle>.Fail(KernelError.PlatformFaulted, $"Session admission capture failed: {exception.Message}"); }
        var deadline = EndpointSessions.PrepareDeadline(lifetime);
        if (!deadline.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(deadline.Error, deadline.Message!);
        lock (_platformMemoryUseGate)
        {
        if (lifetime is { } requestedLifetime && requestedLifetime <= TimeSpan.Zero)
            return KernelResult<EndpointSessionHandle>.Fail(KernelError.SessionTimedOut, "Session lifetime must be positive.");
        var callerResult = Processes.Resolve(caller);
        if (!callerResult.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(callerResult.Error, callerResult.Message!);
        var callerEffect = EnsureProcessAcceptsNewEffects(callerResult.Value!);
        if (!callerEffect.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(callerEffect.Error, callerEffect.Message!);
        var serviceResult = Services.Resolve(descriptor);
        if (!serviceResult.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(serviceResult.Error, serviceResult.Message!);
        if (sourceCommit is not null)
            return CommitRestoreValue(sourceCommit, () => Services.CommitSessionAdmission(descriptor,
                exact => CreateSession(exact, exact.RequiredCapabilities.ToArray())));
        if (source is { } control)
        {
            if (lifetime is not null)
                return KernelResult<EndpointSessionHandle>.Fail(KernelError.InvalidTransition, "Supervisor rebind requires lifetime-free local admission.");
            var principal = Processes.Resolve(control.Principal);
            if (!principal.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(KernelError.SupervisorDenied, principal.Message!);
            var principalEffect = EnsureProcessAcceptsNewEffects(principal.Value!);
            if (!principalEffect.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(KernelError.SupervisorDenied, principalEffect.Message!);
            return Services.CommitSessionAdmission(descriptor, exact =>
            {
                var requirements = exact.RequiredCapabilities.ToArray();
                return CapabilityAuthority.CommitSupervisorSessionAdmission(control.Capability,
                    principal.Value!.DomainId, control.Principal.Generation, () => CreateSession(exact, requirements));
            });
        }
        return Services.CommitSessionAdmission(descriptor, exact => CreateSession(exact, exact.RequiredCapabilities.ToArray()));

        KernelResult<EndpointSessionHandle> CreateSession(ServiceRegistry.Record service, CapabilityRequirementV1[] requirements)
        {
        var admission = ValidateSessionPartiesAndRequirements(caller, service, requirements, capabilityIds);
        if (!admission.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(admission.Error, admission.Message!);
        return CapabilityAuthority.CommitSessionPublication(callerResult.Value!.DomainId, caller.Generation,
            capabilityIds, requirements, () => EndpointSessions.CommitOpeningCapacity(AllocateSession));

        KernelResult<EndpointSessionHandle> AllocateSession()
        {
        var channel = service.ResponseProtocol is null
            ? CreateChannel(caller, service.Provider, service.Protocol, capacity)
            : CreateChannel(caller, service.Provider, service.Protocol, service.ResponseProtocol, capacity);
        if (!channel.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(channel.Error, channel.Message!);
        var session = EndpointSessions.AddPrepared(caller, service.Provider, capabilityIds, requirements, channel.Value!.Left, deadline.Value);
        return session.IsSuccess
            ? KernelResult<EndpointSessionHandle>.Ok(session.Value!.Handle)
            : KernelResult<EndpointSessionHandle>.Fail(session.Error, session.Message!);
        }
        }
        }
    }

    private KernelResult PreflightDependencySession(ProcessHandle caller, ServiceEndpointDescriptor descriptor,
        IReadOnlyCollection<CapabilityId> capabilities)
    {
        lock (_platformMemoryUseGate)
        {
            var ids = capabilities.ToArray();
            var checkedAdmission = Services.CommitSessionAdmission(descriptor, exact =>
            {
                var valid = ValidateSessionPartiesAndRequirements(caller, exact, exact.RequiredCapabilities, ids);
                return valid.IsSuccess ? KernelResult<bool>.Ok(true) : KernelResult<bool>.Fail(valid.Error, valid.Message!);
            });
            return checkedAdmission.IsSuccess ? KernelResult.Ok()
                : KernelResult.Fail(checkedAdmission.Error, checkedAdmission.Message!);
        }
    }

    private KernelResult ValidateSessionPartiesAndRequirements(ProcessHandle caller, ServiceRegistry.Record service,
        IReadOnlyList<CapabilityRequirementV1> requirements, CapabilityId[] capabilityIds)
    {
        var callerResult = Processes.Resolve(caller);
        if (!callerResult.IsSuccess) return KernelResult.Fail(callerResult.Error, callerResult.Message!);
        var callerEffect = EnsureProcessAcceptsNewEffects(callerResult.Value!);
        if (!callerEffect.IsSuccess) return callerEffect;
        var providerResult = Processes.Resolve(service.Provider);
        if (!providerResult.IsSuccess) return KernelResult.Fail(providerResult.Error, providerResult.Message!);
        var providerEffect = EnsureProcessAcceptsNewEffects(providerResult.Value!);
        if (!providerEffect.IsSuccess) return providerEffect;
        foreach (var requirement in requirements)
        {
            var found = capabilityIds.Any(id =>
            {
                var validation = CapabilityAuthority.Validate(id, callerResult.Value!.DomainId, caller.Generation, requirement.Rights);
                return validation.IsSuccess && validation.Value!.ResourceKind == requirement.ResourceKind && validation.Value.ResourceId == requirement.ResourceId;
            });
            if (!found) return KernelResult.Fail(KernelError.MissingCapability,
                $"Session requires {requirement.ResourceKind}:{requirement.ResourceId} ({requirement.Rights}).");
        }
        return KernelResult.Ok();
    }

    private KernelResult ValidateSupervisorSessionSource(ProcessHandle principal, CapabilityId capability)
    {
        lock (_platformMemoryUseGate)
        {
            var exact = Processes.Resolve(principal);
            if (!exact.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, exact.Message!);
            var effect = EnsureProcessAcceptsNewEffects(exact.Value!);
            return effect.IsSuccess ? CapabilityAuthority.CommitSupervisorTelemetryPublication(capability,
                exact.Value!.DomainId, principal.Generation) : KernelResult.Fail(KernelError.SupervisorDenied, effect.Message!);
        }
    }

    public KernelResult CloseSession(ProcessHandle caller, EndpointSessionHandle handle) =>
        CloseSession(caller, handle, allowAlreadyClosed: false);

    private KernelResult CloseSession(ProcessHandle caller, EndpointSessionHandle handle, bool allowAlreadyClosed)
    {
        var admission = EndpointSessions.PrepareClose(handle, caller, allowAlreadyClosed);
        if (!admission.IsSuccess) return KernelResult.Fail(admission.Error, admission.Message!);
        var nativeDrain = DrainNativeServiceSession(handle);
        if (!nativeDrain.IsSuccess) return nativeDrain;
        lock (_requestResponseCorrelationGate)
        {
            if (allowAlreadyClosed && _endpointSessionInvocationRegistry is { } invocations &&
                invocations.UnclosedPossibleEffectCount(handle) != 0)
                return KernelResult.Fail(KernelError.SessionDraining, "Dependency invocation effect has not closed; routing accounting is retained.");
            var session = EndpointSessions.Close(handle, caller, allowAlreadyClosed);
            if (!session.IsSuccess) return KernelResult.Fail(session.Error, session.Message!);
            CloseSessionResourceDonations(handle);
            // Cleanup removes unaccepted correlations under the invocation owner
            // gate. Acceptance that won the race, or donation quarantine created
            // by cleanup, must still retain dependency routing accounting.
            if (allowAlreadyClosed && SessionInvocations.UnclosedPossibleEffectCount(handle) != 0)
                return KernelResult.Fail(KernelError.SessionDraining, "Invocation cleanup retained a possible effect; dependency routing cannot be retired.");
            var close = Channels.Close(session.Value!.Channel, allowAlreadyClosed);
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
        ChannelEnvelope? deliveredFact = null;
        ChannelEndpointHandle endpoint = default;
        try { return Commit(); }
        finally
        {
            if (deliveredFact is { } exact) ObserveReceived(service, endpoint, exact);
        }

        KernelResult<EndpointSessionRequestEnvelope> Commit()
        {
        lock (_platformMemoryUseGate)
        {
        lock (_requestResponseCorrelationGate)
        {
        var session = EndpointSessions.ResolveForService(handle, service);
        if (!session.IsSuccess)
            return KernelResult<EndpointSessionRequestEnvelope>.Fail(session.Error, session.Message!);

        endpoint = session.Value!.ServiceEndpoint;
        var process = Processes.Resolve(service);
        if (!process.IsSuccess)
            return KernelResult<EndpointSessionRequestEnvelope>.Fail(process.Error, process.Message!);
        var next = Channels.Peek(process.Value!, endpoint);
        if (!next.IsSuccess)
            return KernelResult<EndpointSessionRequestEnvelope>.Fail(next.Error, next.Message!);
        // Preserve a published invocation, or admit V1 public Send's first invocation
        // from this actual queued request before the dequeue effect.
        var original = SessionInvocations.PrepareOriginalRequestDelivery(handle, session.Value.Caller,
            service, next.Value!.Sequence, next.Value.MessageId);
        if (!original.IsSuccess)
            return KernelResult<EndpointSessionRequestEnvelope>.Fail(original.Error, original.Message!);
        var received = ReceiveCommitted(service, endpoint);
        if (!received.IsSuccess)
            return KernelResult<EndpointSessionRequestEnvelope>.Fail(received.Error, received.Message!);
        deliveredFact = received.Value!;

        var correlation = original.Value;
        var invocation = SessionInvocations.MarkDelivered(handle, service, received.Value.Sequence,received.Value);
        if (!invocation.IsSuccess)
            return KernelResult<EndpointSessionRequestEnvelope>.Fail(invocation.Error, invocation.Message!);

        var cancellation = SessionInvocations.IsCancellationRequested(correlation, service);
        if (!cancellation.IsSuccess)
            return KernelResult<EndpointSessionRequestEnvelope>.Fail(cancellation.Error, cancellation.Message!);

        return KernelResult<EndpointSessionRequestEnvelope>.Ok(
            new EndpointSessionRequestEnvelope(correlation, received.Value, cancellation.Value));
        }
        }
        }
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
        List<BudgetTraceObservation> observations = [];
        try
        {
        var session = EndpointSessions.ResolveForService(invocation.Session, service);
        if (!session.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(session.Error, session.Message!);
        KernelResult<ResponseRegistry.OriginalPublication> original;
        lock(_requestResponseCorrelationGate)original=Responses.CapturePublication(service,session.Value!.ServiceEndpoint,invocation.InvocationId.Value);
        if(!original.IsSuccess)return KernelResult<ResponseEnvelope>.Fail(original.Error,original.Message!);
        var bound=SessionInvocations.BindOriginalResponse(invocation,service,original.Value!);
        if(!bound.IsSuccess)return KernelResult<ResponseEnvelope>.Fail(bound.Error,bound.Message!);
        return SessionInvocations.Publish(
            invocation,
            service,
            () => PublishResponseCore(service, session.Value!.ServiceEndpoint, invocation.InvocationId.Value, payload, observations),
            () =>
            {
                var expiry=ObserveSessionExpiry(session.Value!.Caller,invocation.Session);
                if(!expiry.IsSuccess)return expiry;
                var current=EndpointSessions.ResolveForService(invocation.Session,service);
                return current.IsSuccess && ReferenceEquals(current.Value,session.Value)
                    ? KernelResult.Ok()
                    : KernelResult.Fail(current.IsSuccess?KernelError.PlatformFaulted:current.Error,
                        current.IsSuccess?"Original response session changed during expiry observation.":current.Message!);
            });
        }
        finally { EmitBudgetObservations(observations); }
    }

    public KernelResult<ResponseEnvelope> CancelSessionResponse(
        ProcessHandle service,
        EndpointSessionInvocationHandle invocation)
    {
        var session = EndpointSessions.ResolveForService(invocation.Session, service);
        if (!session.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(session.Error, session.Message!);
        if(SessionInvocations.HasBoundComputeCopy(invocation,service))
        {
            KernelResult<ResponseRegistry.OriginalPublication> original;
            lock(_requestResponseCorrelationGate)original=Responses.CapturePublication(service,session.Value!.ServiceEndpoint,invocation.InvocationId.Value);
            if(!original.IsSuccess)return KernelResult<ResponseEnvelope>.Fail(original.Error,original.Message!);
            var bound=SessionInvocations.BindOriginalResponse(invocation,service,original.Value!);
            if(!bound.IsSuccess)return KernelResult<ResponseEnvelope>.Fail(bound.Error,bound.Message!);
        }
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
            var requirements = session.Value.Requirements.ToArray();
            var capabilities = session.Value.Capabilities.ToArray();
            var boundedSnapshot = Channels.CaptureInlineCopiedPayload(session.Value.Channel, messageId, copiedPayload);
            lock (_platformMemoryUseGate)
            {
            callerProcess = Processes.Resolve(caller);
            if (!callerProcess.IsSuccess)
                return KernelResult<InlineSipInvocationLease>.Fail(callerProcess.Error, callerProcess.Message!);
            var callerEffect = EnsureProcessAcceptsNewEffects(callerProcess.Value!);
            if (!callerEffect.IsSuccess)
                return KernelResult<InlineSipInvocationLease>.Fail(callerEffect.Error, callerEffect.Message!);
            serviceProcess = Processes.Resolve(service);
            if (!serviceProcess.IsSuccess)
                return KernelResult<InlineSipInvocationLease>.Fail(serviceProcess.Error, serviceProcess.Message!);
            var serviceEffect = EnsureProcessAcceptsNewEffects(serviceProcess.Value!);
            if (!serviceEffect.IsSuccess)
                return KernelResult<InlineSipInvocationLease>.Fail(serviceEffect.Error, serviceEffect.Message!);
            lock (_requestResponseCorrelationGate)
            {
                var currentPin = EndpointSessions.RevalidatePin(pinned.Value!);
                if (!currentPin.IsSuccess)
                    return KernelResult<InlineSipInvocationLease>.Fail(currentPin.Error, currentPin.Message!);
                return CapabilityAuthority.CommitSessionPublication(callerProcess.Value!.DomainId,
                    caller.Generation, capabilities, requirements, PublishCopied);

                KernelResult<InlineSipInvocationLease> PublishCopied()
                {
                var begun = Channels.BeginInlineCopiedInvocation(
                    callerProcess.Value!, serviceProcess.Value!, session.Value.Channel, messageId, copiedPayload, boundedSnapshot);
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
        InlineSipInvocationLease? invocationLease = null;
        BorrowLease<T>? borrow = null;
        RegionUseDescriptor? acquiredUse = null;
        try
        {
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.AfterSessionPin);
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.BeforeSessionRevalidation);
            var requirements = session.Value.Requirements.ToArray();
            var capabilities = session.Value.Capabilities.ToArray();
            KernelResult<InlineSipInvocationLease> admission;
            lock (_platformMemoryUseGate)
            {
            callerProcess = Processes.Resolve(caller);
            if (!callerProcess.IsSuccess)
                return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(callerProcess.Error, callerProcess.Message!);
            var callerEffect = EnsureProcessAcceptsNewEffects(callerProcess.Value!);
            if (!callerEffect.IsSuccess)
                return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(callerEffect.Error, callerEffect.Message!);
            serviceProcess = Processes.Resolve(service);
            if (!serviceProcess.IsSuccess)
                return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(serviceProcess.Error, serviceProcess.Message!);
            var serviceEffect = EnsureProcessAcceptsNewEffects(serviceProcess.Value!);
            if (!serviceEffect.IsSuccess)
                return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(serviceEffect.Error, serviceEffect.Message!);
            lock (_requestResponseCorrelationGate)
            {
                var currentPin = EndpointSessions.RevalidatePin(pinned.Value!);
                if (!currentPin.IsSuccess)
                    return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(currentPin.Error, currentPin.Message!);
                admission = CapabilityAuthority.CommitSessionPublication(callerProcess.Value!.DomainId,
                    caller.Generation, capabilities, requirements, PublishBorrow);

                KernelResult<InlineSipInvocationLease> PublishBorrow()
                {
                var begun = Channels.BeginInlineBorrowInvocation(
                    callerProcess.Value!, serviceProcess.Value!, session.Value.Channel, messageId, payload);
                if (!begun.IsSuccess)
                    return KernelResult<InlineSipInvocationLease>.Fail(begun.Error, begun.Message!);
                borrow = begun.Value.Lease;
                var invocation = SessionInvocations.Register(sessionHandle, caller, service, begun.Value.Sequence, messageId);
                var delivered = SessionInvocations.MarkDelivered(sessionHandle, service, begun.Value.Sequence);
                if (!delivered.IsSuccess)
                    return KernelResult<InlineSipInvocationLease>.Fail(delivered.Error, delivered.Message!);
                var accepted = SessionInvocations.AcceptInvocation(invocation, service, allowInFlightCancellation: false);
                if (!accepted.IsSuccess)
                    return KernelResult<InlineSipInvocationLease>.Fail(accepted.Error, accepted.Message!);
                return KernelResult<InlineSipInvocationLease>.Ok(new InlineSipInvocationLease(
                    new TrustedSipInvocationContext(caller, service, sessionHandle, invocation), pinned.Value!, messageId));
                }
            }
            }
            if (!admission.IsSuccess)
            {
                if (borrow is not null) _ = ReturnBorrow(service, borrow.Handle);
                return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(admission.Error, admission.Message!);
            }
            invocationLease = admission.Value!;
            pinned = default;
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.AfterInvocationAccepted);

            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.BeforeRegionUseAcquisition);
            var use = AcquireBorrowRegionUse(
                caller, service, borrow.Handle, RegionUseMode.ReadOnly,
                new RegionUseRange(0, checked((long)borrow.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<T>())));
            if (!use.IsSuccess)
            {
                return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(use.Error, use.Message!);
            }
            acquiredUse = use.Value!;
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.AfterRegionUseAcquisition);

            var result = new InlineBorrowSipInvocationLease<T>(invocationLease, borrow, use.Value!);
            invocationLease = null;
            return KernelResult<InlineBorrowSipInvocationLease<T>>.Ok(result);
        }
        finally
        {
            if (invocationLease is not null)
            {
                try
                {
                    // Preparation never handed the accepted invocation to its consumer.
                    // Cleanup uses the exact local facts; a refusal leaves owner facts intact.
                    if (acquiredUse is not null) _ = ReleaseRegionUse(service, acquiredUse.Handle);
                    if (borrow is not null) _ = ReturnBorrow(service, borrow.Handle);
                    _ = SessionInvocations.CompleteInline(invocationLease.Context.Invocation, service, succeeded: false);
                }
                finally
                {
                    CloseResourceDonationForInvocationTerminal(service, invocationLease.Context.Invocation);
                    FinalizeInlineInvocationPin(invocationLease);
                }
            }
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
        var releaseAttempted = false;
        var returnAttempted = false;
        var settlementAttempted = false;
        try
        {
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.BeforeRegionUseRelease);
            releaseAttempted = true;
            var released = ReleaseRegionUse(service, lease.Use.Handle);
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.AfterRegionUseRelease);
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.BeforeBorrowReturn);
            returnAttempted = true;
            var returned = ReturnBorrow(service, lease.Borrow.Handle);
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.AfterBorrowReturn);
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.BeforeInvocationSettlement);
            var cleanupSucceeded = released.IsSuccess && returned.IsSuccess;
            settlementAttempted = true;
            var settled = SessionInvocations.CompleteInline(
                context.Invocation,
                service,
                succeeded && cleanupSucceeded);
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.AfterInvocationSettlement);
            if (!released.IsSuccess) return released;
            if (!returned.IsSuccess) return returned;
            return settled;
        }
        finally
        {
            try
            {
                // Only remaining exact steps are attempted; completed or refused steps
                // are never replayed after a callback fault.
                if (!releaseAttempted) _ = ReleaseRegionUse(service, lease.Use.Handle);
                if (!returnAttempted) _ = ReturnBorrow(service, lease.Borrow.Handle);
                if (!settlementAttempted) _ = SessionInvocations.CompleteInline(context.Invocation, service, succeeded: false);
            }
            finally
            {
                CloseResourceDonationForInvocationTerminal(service, context.Invocation);
                FinalizeInlineInvocationPin(invocation);
            }
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
        session = EndpointSessions.RevalidatePin(lease.Invocation.SessionPin);
        if (!session.IsSuccess) return session;
        if (!lease.Borrow.IsValid)
            return KernelResult.Fail(KernelError.InvalidRegionState, "Inline BORROW lease is no longer live.");
        use = ValidateRegionUse(lease.Context.Service, lease.Use.Handle);
        return use.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(use.Error, use.Message!);
    }

    internal KernelResult<OwnedBuffer<T>> TransferInlineOwnershipResponse<T>(
        ProcessHandle service,
        InlineSipInvocationLease invocation,
        OwnedBuffer<T> payload) where T : unmanaged
    {
        List<BudgetTraceObservation> observations = [];
        try
        {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(payload);
        var context = invocation.Context;
        if (context.Service != service)
            return KernelResult<OwnedBuffer<T>>.Fail(KernelError.WrongSessionOwner, "Inline ownership response does not match the live service principal.");
        var publication = SessionInvocations.Publish(context.Invocation, service, CommitOwnership);
        if (!publication.IsSuccess)
            return KernelResult<OwnedBuffer<T>>.Fail(publication.Error, publication.Message!);
        CloseResourceDonationForInvocationTerminal(service, context.Invocation);
        FinalizeInlineInvocationPin(invocation);
        return KernelResult<OwnedBuffer<T>>.Ok((OwnedBuffer<T>)publication.Value!.Payload!);

        KernelResult<ResponseEnvelope> CommitOwnership()
        {
        lock (_platformMemoryUseGate)
        lock (_requestResponseCorrelationGate)
        {
        var current = RevalidateInlineSessionInvocation(invocation);
        if (!current.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(current.Error, current.Message!);
        var serviceProcess = Processes.Resolve(service);
        if (!serviceProcess.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(serviceProcess.Error, serviceProcess.Message!);
        var callerProcess = Processes.Resolve(context.Caller);
        if (!callerProcess.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(callerProcess.Error, callerProcess.Message!);

        var session = EndpointSessions.ResolveForService(context.Session, service);
        if (!session.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(session.Error, session.Message!);
        var moved = Responses.TransferInlineOwnership(
            serviceProcess.Value!, callerProcess.Value!, session.Value!.ServiceEndpoint, invocation.MessageId, payload,
            () => RevalidateInlineSessionInvocation(invocation),
            (source, target, region, admission, noEffect, prepared) => TransferRegionForIpcCore(source, target, region, admission, observations,noEffect,prepared));
        if (!moved.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(moved.Error, moved.Message!);
        return KernelResult<ResponseEnvelope>.Ok(new(context.Invocation.InvocationId.Value,
            invocation.MessageId, ResponsePublicationStatus.Published, moved.Value));
        }
        }
        }
        finally { EmitBudgetObservations(observations); }
    }

    internal KernelResult<InlineMoveSipInvocationLease<T>> BeginInlineMoveSessionInvocation<T>(
        ProcessHandle caller,
        ProcessHandle service,
        EndpointSessionHandle sessionHandle,
        uint messageId,
        OwnedBuffer<T> payload) where T : unmanaged
    {
        List<BudgetTraceObservation> observations = [];
        try
        {
        lock (_platformMemoryUseGate)
        {
        ArgumentNullException.ThrowIfNull(payload);
        var callerProcess = Processes.Resolve(caller);
        if (!callerProcess.IsSuccess)
            return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(callerProcess.Error, callerProcess.Message!);
        var callerEffect = EnsureProcessAcceptsNewEffects(callerProcess.Value!);
        if (!callerEffect.IsSuccess)
            return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(callerEffect.Error, callerEffect.Message!);
        var serviceProcess = Processes.Resolve(service);
        if (!serviceProcess.IsSuccess)
            return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(serviceProcess.Error, serviceProcess.Message!);
        var serviceEffect = EnsureProcessAcceptsNewEffects(serviceProcess.Value!);
        if (!serviceEffect.IsSuccess)
            return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(serviceEffect.Error, serviceEffect.Message!);
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
                return CapabilityAuthority.CommitSessionPublication(callerProcess.Value!.DomainId,
                    caller.Generation, session.Value.Capabilities.ToArray(), session.Value.Requirements.ToArray(), PublishMove);

                KernelResult<InlineMoveSipInvocationLease<T>> PublishMove()
                {
                var begun = Channels.BeginInlineMoveInvocation(
                    callerProcess.Value!, serviceProcess.Value!, session.Value.Channel, messageId, payload,
                    (source, target, region, admission, noEffect, prepared) => TransferRegionForIpcCore(source, target, region, admission, observations,noEffect,prepared));
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
        }
        finally
        {
            if (pinned.Value is { } remainingPin)
                FinalizeSessionPin(remainingPin, sessionHandle);
        }
        }
        }
        finally { EmitBudgetObservations(observations); }
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
        if (context.Service != service)
            return KernelResult.Fail(KernelError.WrongSessionOwner, "Inline invocation context does not match the live session parties.");
        var settled = SessionInvocations.CompleteInline(context.Invocation, service, succeeded);
        if (!settled.IsSuccess) return settled;
        try { CloseResourceDonationForInvocationTerminal(service, context.Invocation); }
        finally { FinalizeInlineInvocationPin(lease); }
        return settled;
    }

    private void FinalizeInlineInvocationPin(InlineSipInvocationLease invocation)
    {
        var session = invocation.Context.Session;
        invocation.Dispose();
        CompleteDeferredSessionCleanup(session);
    }

    private void FinalizeSessionPin(EndpointSessionPin pin, EndpointSessionHandle session)
    {
        if (EndpointSessions.OriginalSendPinIsPending(pin)) return;
        pin.Dispose();
        CompleteDeferredSessionCleanup(session);
    }

    private void CompleteDeferredSessionCleanup(EndpointSessionHandle session)
    {
        var deferredClose = EndpointSessions.ClaimClosedCleanup(session);
        if (deferredClose is null) return;
        lock (_requestResponseCorrelationGate)
        {
            CloseSessionResourceDonations(session);
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
        var timestamp = CancellationScopes.CaptureTimestamp(scope);
        var session = ResolveSession(caller, invocation.Session);
        if (!session.IsSuccess) return KernelResult.Fail(session.Error, session.Message!);
        return SessionInvocations.BindCancellationScope(invocation, caller, scope, timestamp);
    }

    public KernelResult<CancellationObservation> RequestSessionCancellation(
        ProcessHandle caller,
        EndpointSessionInvocationHandle invocation,
        CancellationScopeHandle scope)
    {
        var timestamp = CancellationScopes.CaptureTimestamp(scope);
        var session = ResolveSession(caller, invocation.Session);
        if (!session.IsSuccess) return KernelResult<CancellationObservation>.Fail(session.Error, session.Message!);
        return SessionInvocations.RequestCancellation(invocation, caller, scope, timestamp);
    }

    public KernelResult<bool> QuerySessionCancellation(
        ProcessHandle service,
        EndpointSessionInvocationHandle invocation)
    {
        var session = EndpointSessions.ResolveForService(invocation.Session, service);
        if (!session.IsSuccess) return KernelResult<bool>.Fail(session.Error, session.Message!);
        return SessionInvocations.IsCancellationRequested(invocation, service);
    }

    private KernelResult ObserveSessionExpiry(ProcessHandle caller,EndpointSessionHandle handle)
    {
        var observation = EndpointSessions.ReadExpiryObservation(handle, caller);
        if (!observation.IsSuccess)
            return KernelResult.Fail(observation.Error, observation.Message!);
        lock (_requestResponseCorrelationGate)
        {
        if (EndpointSessions.ExpireIfDue(observation.Value) is { } expired)
        {
            if (expired.State == EndpointSessionState.Closed)
            {
                lock (_requestResponseCorrelationGate)
                {
                    CloseSessionResourceDonations(handle);
                    _ = Channels.Close(expired.Channel);
                }
            }
            return KernelResult.Fail(KernelError.SessionTimedOut, "Endpoint session lifetime expired.");
        }
        return KernelResult.Ok();
        }
    }

    internal KernelResult<EndpointSessionRegistry.Record> ResolveSession(ProcessHandle caller, EndpointSessionHandle handle)
    {
        var expiry=ObserveSessionExpiry(caller,handle);
        if(!expiry.IsSuccess)return KernelResult<EndpointSessionRegistry.Record>.Fail(expiry.Error,expiry.Message!);
        lock(_requestResponseCorrelationGate)
        {
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
    }

    internal KernelResult CloseSessionsForProcess(ProcessHandle process)
    {
        foreach (var session in EndpointSessions.CloseForProcess(process))
        {
            var nativeDrain = DrainNativeServiceSession(session.Handle);
            if (!nativeDrain.IsSuccess) return nativeDrain;
            lock (_requestResponseCorrelationGate)
            {
                CloseSessionResourceDonations(session.Handle);
                _ = Channels.Close(session.Channel);
            }
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

    internal ValueTask<KernelResult<ResponseEnvelope>> InvokeSessionAsync(
        ProcessHandle caller,
        EndpointSessionHandle handle,
        uint messageId,
        object? payload,
        CancellationToken cancellationToken) => InvokeSessionAsync(caller,handle,messageId,payload,cancellationToken,null);

    internal async ValueTask<KernelResult<ResponseEnvelope>> InvokeSessionAsync(
        ProcessHandle caller, EndpointSessionHandle handle, uint messageId, object? payload,
        CancellationToken cancellationToken, OriginalSessionSendCustody? custody)
    {
        if(custody is not null && !custody.Matches(this,caller,handle))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.WrongSessionOwner,"Original send custody is copied or foreign.");
        var session = ResolveSession(caller, handle);
        if (!session.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(session.Error, session.Message!);
        var current = session.Value!;
        var pinned = EndpointSessions.AcquirePin(handle, caller, current.Service);
        if (!pinned.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(pinned.Error, pinned.Message!);
        var sessionPin = pinned.Value!;
        try
        {
        var currentPin = EndpointSessions.RevalidatePin(sessionPin);
        if (!currentPin.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(currentPin.Error, currentPin.Message!);
        var send = SendCore(caller, current.Service, current.Channel, messageId, payload, null,
            current.Capabilities, AdmissionQosHint.None, null, new(handle, sessionPin, current.Requirements.ToArray(),Custody:custody));
        if (!send.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(send.Error, send.Message!);
        var invocation = SessionInvocations.Register(handle, caller, current.Service, send.Value!.Sequence, messageId);
        FinalizeSessionPin(sessionPin, handle);
        using var cancellation = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => _ = RequestSessionCancellation(caller, invocation))
            : default;
        return await WaitForResponseAsync(caller, current.Channel, send.Value.Sequence).ConfigureAwait(false);
        }
        finally { FinalizeSessionPin(sessionPin, handle); }
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
        var sessionPin = pinned.Value!;
        try
        {
        var currentPin = EndpointSessions.RevalidatePin(sessionPin);
        if (!currentPin.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(currentPin.Error, currentPin.Message!);
        var send = SendCore(caller, current.Service, current.Channel, messageId, payload, null,
            current.Capabilities, AdmissionQosHint.None, null, new(handle, sessionPin, current.Requirements.ToArray(), scope));
        if (!send.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(send.Error, send.Message!);
        var invocation = SessionInvocations.Register(
            handle, caller, current.Service, send.Value!.Sequence, messageId, scope);
        FinalizeSessionPin(sessionPin, handle);
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
        finally { FinalizeSessionPin(sessionPin, handle); }
    }

    internal ValueTask<KernelResult<ResponseEnvelope>> InvokeSessionOwnershipPairAsync(
        ProcessHandle caller,
        EndpointSessionHandle handle,
        uint messageId,
        object first,
        object second) =>
        InvokeSessionOwnershipPairAsync(caller, handle, messageId, first, second, CancellationToken.None);

    internal ValueTask<KernelResult<ResponseEnvelope>> InvokeSessionOwnershipPairAsync(
        ProcessHandle caller,
        EndpointSessionHandle handle,
        uint messageId,
        object first,
        object second,
        CancellationToken cancellationToken)
        => InvokeSessionOwnershipPairAsync(caller,handle,messageId,first,second,cancellationToken,null);

    internal bool OriginalSessionSendIsPending(OriginalSessionSendCustody custody) =>
        custody.Read(this) is { } original && SessionInvocations.OriginalSendIsPending(original);

    internal KernelResult WithdrawOriginalSessionSend(OriginalSessionSendCustody custody)
    {
        lock(_platformMemoryUseGate)
        lock(_requestResponseCorrelationGate)
        {
            var original=custody.Read(this);
            if(original is null) return KernelResult.Fail(KernelError.WrongSessionOwner,"Original send custody is copied or foreign.");
            var pending=SessionInvocations.ReadPendingOriginalSend(original,custody.Caller);
            if(!pending.IsSuccess) return KernelResult.Fail(pending.Error,pending.Message!);
            var charges=Budgets.OriginalQueuedCharges().Where(charge=>charge.Channel==pending.Value.Channel.ChannelId && charge.Sequence==pending.Value.Raw.Sequence && charge.Sender==custody.Caller).ToArray();
            if(charges.Length!=1 || charges[0].Lifetime is null)
                return KernelResult.Fail(KernelError.PlatformFaulted,"Exact original queued charge unavailable.");
            var charge=charges[0];
            return Channels.WithdrawOriginalQueuedEnvelope(pending.Value.Channel,pending.Value.Raw,charge.Lifetime!,charge.Fact);
        }
    }

    internal KernelResult CleanupOriginalSessionSend(OriginalSessionSendCustody custody)
    {
        EndpointSessionPin? completedPin=null;
        List<BudgetTraceObservation> observations=[];
        try
        {
        lock(_platformMemoryUseGate)
        lock(_requestResponseCorrelationGate)
        {
            var original=custody.Read(this);
            if(original is null) return KernelResult.Fail(KernelError.WrongSessionOwner,"Original custody is copied or foreign.");
            if(SessionInvocations.OriginalSendCleanupIsCompleted(original)) return KernelResult.Ok();
            var pending=SessionInvocations.ReadPendingOriginalSend(original,custody.Caller);
            var transfer=SessionInvocations.ReadOriginalCompletedSendTransfer(original,custody.Caller);
            if(!pending.IsSuccess && !transfer.IsSuccess) return KernelResult.Fail(pending.Error,pending.Message!);
            var channel=pending.IsSuccess ? pending.Value.Channel : transfer.Value.Channel;
            var sequence=pending.IsSuccess ? pending.Value.Raw.Sequence : transfer.Value.Sequence;
            var serviceHandle=pending.IsSuccess ? pending.Value.Service : transfer.Value.Service;
            var charges=Budgets.OriginalQueuedCharges().Where(item=>item.Channel==channel.ChannelId && item.Sequence==sequence && item.Sender==custody.Caller).ToArray();
            if(charges.Length!=1 || charges[0].Lifetime is null) return KernelResult.Fail(KernelError.PlatformFaulted,"Original queued charge missing.");
            var charge=charges[0];
            object?[] payloads;
            if(pending.IsSuccess)
            {
                var withdrawn=Channels.WithdrawOriginalQueuedEnvelope(channel,pending.Value.Raw,charge.Lifetime!,charge.Fact);
                if(!withdrawn.IsSuccess) return withdrawn;
                payloads=[pending.Value.Raw.Payload,pending.Value.Raw.SecondaryPayload];
            }
            else
            {
                var withdrawn=Channels.WithdrawUnpublishedOriginalTransfer(channel,sequence,transfer.Value.Prepared,charge.Lifetime!,charge.Fact);
                if(!withdrawn.IsSuccess) return KernelResult.Fail(withdrawn.Error,withdrawn.Message!);
                payloads=withdrawn.Value!;
            }
            var service=Processes.Resolve(serviceHandle);
            var transferFacts=SessionInvocations.ReadOriginalTransferCleanupFacts(original);
            if(!transferFacts.IsSuccess) return KernelResult.Fail(transferFacts.Error,transferFacts.Message!);
            var producer=SessionInvocations.ReadOriginalTransferProducer(original);
            if(!producer.IsSuccess || producer.Value.Raw is not { IsSuccess:true } raw ||
                !transferFacts.Value.Prepared.IsOriginal || transferFacts.Value.Prepared.SourceHandle!=transferFacts.Value.Source ||
                raw.Value.RegionId!=transferFacts.Value.Source.RegionId ||
                raw.Value.Generation.Value!=transferFacts.Value.Source.Generation.Value+1)
                return KernelResult.Fail(KernelError.PlatformFaulted,"Exact original Region effect unavailable.");
            var owner=transferFacts.Value.TargetOwner;
            var cleanup=KernelResult.Ok();
            foreach(var payload in payloads)
            {
                if(payload is SingPlus.Sip.IOriginalBorrowPayload loan)
                {
                    var closed=Regions.CleanupWithdrawnBorrow(loan,owner);
                    if(!closed.IsSuccess) cleanup=closed;
                }
                else if(payload is SingPlus.Sip.ITransferableOwnedPayload moved)
                {
                    if(moved.Handle!=raw.Value || !ReferenceEquals(moved,transferFacts.Value.Prepared.Projection))
                        return KernelResult.Fail(KernelError.PlatformFaulted,"Withdrawn payload differs from original Region raw.");
                    var closed=Regions.CleanupWithdrawnPayload(moved,owner,transferFacts.Value.RegionOwner);
                    if(!closed.IsSuccess) cleanup=closed;
                    else if(service.IsSuccess) service.Value!.RemoveRegion(moved.Handle);
                }
                else return KernelResult.Fail(KernelError.UnsupportedPayload,"Original pair resource is not a sealed ownership payload.");
            }
            var ended=Regions.HasEndedOriginalAccountingLifetime(transferFacts.Value.RegionOwner,raw.Value);
            if((!ended.IsSuccess || !ended.Value) && cleanup.IsSuccess)
                cleanup=KernelResult.Fail(KernelError.PlatformFaulted,"Original Region closure remains pinned.");
            if(ended.IsSuccess && ended.Value && producer.Value.Reservation is { } targetReservation)
            {
                if(producer.Value.BudgetOwner is null) return KernelResult.Fail(KernelError.PlatformFaulted,"Original target charge absent.");
                var priorTarget=Budgets.Query(targetReservation);
                var consumedTarget=Budgets.ConsumeClosedTransferReservation(producer.Value.BudgetOwner,serviceHandle,targetReservation,
                    transferFacts.Value.Prepared,
                    raw.Value,transferFacts.Value.RegionOwner,owner.DomainId);
                if(!consumedTarget.IsSuccess) cleanup=consumedTarget;
                else
                {
                    RetireExactRegionBudgetRoute(raw.Value,serviceHandle,targetReservation);
                    if(priorTarget.IsSuccess && priorTarget.Value!.State!=BudgetReservationState.Released)
                        observations.Add(new(serviceHandle,TraceEventKind.BudgetReleased,targetReservation));
                }
            }
            foreach(var sourceCharge in transferFacts.Value.Charges)
            {
                if(sourceCharge.RegionOwner is null || !ReferenceEquals(sourceCharge.RegionOwner,transferFacts.Value.RegionOwner))
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original source accounting owner changed.");
                var sourceEnded=Regions.HasEndedOriginalAccountingLifetime(sourceCharge.RegionOwner,transferFacts.Value.Source);
                if(!sourceEnded.IsSuccess || !sourceEnded.Value) return KernelResult.Fail(KernelError.PlatformFaulted,"Original source charge lifetime remains pinned.");
                var sourceBinding=Budgets.ValidateOriginalRegionChargeBinding(sourceCharge.Fact,sourceCharge.Owner,sourceCharge.Reservation,
                    transferFacts.Value.Source,sourceCharge.RegionOwner,transferFacts.Value.SourceDomain);
                if(!sourceBinding.IsSuccess) return sourceBinding;
                var priorSource=Budgets.Query(sourceCharge.Reservation);
                var consumedSource=Budgets.ReleaseOriginalRegionCharge(sourceCharge.Fact,sourceCharge.Owner,sourceCharge.Reservation);
                if(!consumedSource.IsSuccess || consumedSource.Value!.State!=BudgetReservationState.Released)
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original source charge remains pinned.");
                RetireExactRegionBudgetRoute(transferFacts.Value.Source,sourceCharge.Owner,sourceCharge.Reservation);
                if(priorSource.IsSuccess && priorSource.Value!.State!=BudgetReservationState.Released)
                    observations.Add(new(sourceCharge.Owner,TraceEventKind.BudgetReleased,sourceCharge.Reservation));
            }
            if(!cleanup.IsSuccess) return cleanup;
            if(!Channels.HasEndedOriginalQueuedLifetime(charge.Lifetime,charge.Channel,charge.Sequence,charge.Fact))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original withdrawal lifetime changed.");
            var consumed=Budgets.ConsumeWithdrawnQueuedCharge(charge.Fact,charge.Lifetime!,charge.Sender,charge.Reservation);
            if(!consumed.IsSuccess) return KernelResult.Fail(consumed.Error,consumed.Message!);
            if(!pending.IsSuccess && !Channels.CompleteUnpublishedOriginalTransfer(charge.Lifetime!,transfer.Value.Prepared,charge.Fact))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original unpublished transfer cleanup continuity changed.");
            Channels.ConsumeSendBudgetCorrelation(channel,charge.Sender,charge.Reservation);
            var completed=SessionInvocations.CompleteWithdrawnOriginalSend(original,custody.Caller);
            if(!completed.IsSuccess) return KernelResult.Fail(completed.Error,completed.Message!);
            completedPin=completed.Value!;
            EndpointSessions.ConsumeOriginalSendPinCorrelation(completedPin);
            return KernelResult.Ok();
        }
        }
        finally
        {
            if(completedPin is not null) FinalizeSessionPin(completedPin,custody.Session);
            EmitBudgetObservations(observations);
        }
    }

    private void RetireExactRegionBudgetRoute(RegionHandle region,ProcessHandle owner,BudgetReservationHandle reservation)
    {
        lock(_regionBudgetReservationsGate)
            if(_regionBudgetReservations.TryGetValue(region,out var route) && route==(owner,reservation))
                _regionBudgetReservations.Remove(region);
    }

    internal async ValueTask<KernelResult<ResponseEnvelope>> InvokeSessionOwnershipPairAsync(
        ProcessHandle caller,EndpointSessionHandle handle,uint messageId,object first,object second,
        CancellationToken cancellationToken,OriginalSessionSendCustody? custody)
    {
        if(custody is not null && !custody.Matches(this,caller,handle))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.WrongSessionOwner,"Original send custody is copied or foreign.");
        var session = ResolveSession(caller, handle);
        if (!session.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(session.Error, session.Message!);
        var current = session.Value!;
        var pinned = EndpointSessions.AcquirePin(handle, caller, current.Service);
        if (!pinned.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(pinned.Error, pinned.Message!);
        var sessionPin = pinned.Value!;
        try
        {
        var currentPin = EndpointSessions.RevalidatePin(sessionPin);
        if (!currentPin.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(currentPin.Error, currentPin.Message!);
        var send = SendCore(caller, current.Service, current.Channel, messageId, first, second,
            current.Capabilities, AdmissionQosHint.None, null, new(handle, sessionPin, current.Requirements.ToArray(),Custody:custody));
        if (!send.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(send.Error, send.Message!);
        var invocation = SessionInvocations.Register(handle, caller, current.Service, send.Value!.Sequence, messageId);
        FinalizeSessionPin(sessionPin, handle);
        using var cancellation = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => _ = RequestSessionCancellation(caller, invocation))
            : default;
        return await WaitForResponseAsync(caller, current.Channel, send.Value.Sequence).ConfigureAwait(false);
        }
        finally { FinalizeSessionPin(sessionPin, handle); }
    }
}






