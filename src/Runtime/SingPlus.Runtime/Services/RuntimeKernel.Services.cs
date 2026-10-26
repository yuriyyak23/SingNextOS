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
        CapabilityRequirementV1[] requirements = requiredCapabilities?.ToArray() ?? [];
        KernelResult<ServiceEndpointDescriptor> registered;
        lock (_platformMemoryUseGate)
        {
            KernelResult<SingProcess> process = Processes.Resolve(provider);
        if (!process.IsSuccess) return KernelResult<ServiceEndpointDescriptor>.Fail(process.Error, process.Message!);
            KernelResult effect = EnsureProcessAcceptsNewEffects(process.Value!);
        if (!effect.IsSuccess) return KernelResult<ServiceEndpointDescriptor>.Fail(effect.Error, effect.Message!);
        registered = Services.Register(provider, name, contract, protocol, responseProtocol, requirements,
            sourceCommit is not null ? commit => CommitRestoreValue(sourceCommit, commit)
            : source is { } controller ? commit =>
            {
                global::SingPlus.Runtime.KernelResult<global::SingPlus.Sip.SingProcess> principal = Processes.Resolve(controller.Principal);
                if (!principal.IsSuccess) return KernelResult<ServiceEndpointDescriptor>.Fail(KernelError.SupervisorDenied, principal.Message!);
                global::SingPlus.Runtime.KernelResult accepts = EnsureProcessAcceptsNewEffects(principal.Value!);
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
        KernelResult<DateTimeOffset?> deadline = EndpointSessions.PrepareDeadline(lifetime);
        if (!deadline.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(deadline.Error, deadline.Message!);
        lock (_platformMemoryUseGate)
        {
        if (lifetime is { } requestedLifetime && requestedLifetime <= TimeSpan.Zero)
            return KernelResult<EndpointSessionHandle>.Fail(KernelError.SessionTimedOut, "Session lifetime must be positive.");
            KernelResult<SingProcess> callerResult = Processes.Resolve(caller);
        if (!callerResult.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(callerResult.Error, callerResult.Message!);
            KernelResult callerEffect = EnsureProcessAcceptsNewEffects(callerResult.Value!);
        if (!callerEffect.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(callerEffect.Error, callerEffect.Message!);
            KernelResult<ServiceRegistry.Record> serviceResult = Services.Resolve(descriptor);
        if (!serviceResult.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(serviceResult.Error, serviceResult.Message!);
        if (sourceCommit is not null)
            return CommitRestoreValue(sourceCommit, () => Services.CommitSessionAdmission(descriptor,
                exact => CreateSession(exact, exact.RequiredCapabilities.ToArray())));
        if (source is { } control)
        {
            if (lifetime is not null)
                return KernelResult<EndpointSessionHandle>.Fail(KernelError.InvalidTransition, "Supervisor rebind requires lifetime-free local admission.");
                KernelResult<SingProcess> principal = Processes.Resolve(control.Principal);
            if (!principal.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(KernelError.SupervisorDenied, principal.Message!);
                KernelResult principalEffect = EnsureProcessAcceptsNewEffects(principal.Value!);
            if (!principalEffect.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(KernelError.SupervisorDenied, principalEffect.Message!);
            return Services.CommitSessionAdmission(descriptor, exact =>
            {
                CapabilityRequirementV1[] requirements = exact.RequiredCapabilities.ToArray();
                return CapabilityAuthority.CommitSupervisorSessionAdmission(control.Capability,
                    principal.Value!.DomainId, control.Principal.Generation, () => CreateSession(exact, requirements));
            });
        }
        return Services.CommitSessionAdmission(descriptor, exact => CreateSession(exact, exact.RequiredCapabilities.ToArray()));

        KernelResult<EndpointSessionHandle> CreateSession(ServiceRegistry.Record service, CapabilityRequirementV1[] requirements)
        {
                KernelResult admission = ValidateSessionPartiesAndRequirements(caller, service, requirements, capabilityIds);
        if (!admission.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(admission.Error, admission.Message!);
        return CapabilityAuthority.CommitSessionPublication(callerResult.Value!.DomainId, caller.Generation,
            capabilityIds, requirements, () => EndpointSessions.CommitOpeningCapacity(AllocateSession));

        KernelResult<EndpointSessionHandle> AllocateSession()
        {
                    KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> channel = service.ResponseProtocol is null
            ? CreateChannel(caller, service.Provider, service.Protocol, capacity)
            : CreateChannel(caller, service.Provider, service.Protocol, service.ResponseProtocol, capacity);
        if (!channel.IsSuccess) return KernelResult<EndpointSessionHandle>.Fail(channel.Error, channel.Message!);
                    KernelResult<EndpointSessionRegistry.Record> session = EndpointSessions.AddPrepared(caller, service.Provider, capabilityIds, requirements, channel.Value!.Left, deadline.Value);
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
            CapabilityId[] ids = capabilities.ToArray();
            KernelResult<bool> checkedAdmission = Services.CommitSessionAdmission(descriptor, exact =>
            {
                KernelResult valid = ValidateSessionPartiesAndRequirements(caller, exact, exact.RequiredCapabilities, ids);
                return valid.IsSuccess ? KernelResult<bool>.Ok(true) : KernelResult<bool>.Fail(valid.Error, valid.Message!);
            });
            return checkedAdmission.IsSuccess ? KernelResult.Ok()
                : KernelResult.Fail(checkedAdmission.Error, checkedAdmission.Message!);
        }
    }

    private KernelResult ValidateSessionPartiesAndRequirements(ProcessHandle caller, ServiceRegistry.Record service,
        IReadOnlyList<CapabilityRequirementV1> requirements, CapabilityId[] capabilityIds)
    {
        KernelResult<SingProcess> callerResult = Processes.Resolve(caller);
        if (!callerResult.IsSuccess) return KernelResult.Fail(callerResult.Error, callerResult.Message!);
        KernelResult callerEffect = EnsureProcessAcceptsNewEffects(callerResult.Value!);
        if (!callerEffect.IsSuccess) return callerEffect;
        KernelResult<SingProcess> providerResult = Processes.Resolve(service.Provider);
        if (!providerResult.IsSuccess) return KernelResult.Fail(providerResult.Error, providerResult.Message!);
        KernelResult providerEffect = EnsureProcessAcceptsNewEffects(providerResult.Value!);
        if (!providerEffect.IsSuccess) return providerEffect;
        foreach (CapabilityRequirementV1 requirement in requirements)
        {
            bool found = capabilityIds.Any(id =>
            {
                KernelResult<CapabilityDescriptorV1> validation = CapabilityAuthority.Validate(id, callerResult.Value!.DomainId, caller.Generation, requirement.Rights);
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
            KernelResult<SingProcess> exact = Processes.Resolve(principal);
            if (!exact.IsSuccess) return KernelResult.Fail(KernelError.SupervisorDenied, exact.Message!);
            KernelResult effect = EnsureProcessAcceptsNewEffects(exact.Value!);
            return effect.IsSuccess ? CapabilityAuthority.CommitSupervisorTelemetryPublication(capability,
                exact.Value!.DomainId, principal.Generation) : KernelResult.Fail(KernelError.SupervisorDenied, effect.Message!);
        }
    }

    public KernelResult CloseSession(ProcessHandle caller, EndpointSessionHandle handle) =>
        CloseSession(caller, handle, allowAlreadyClosed: false);

    private KernelResult CloseSession(ProcessHandle caller, EndpointSessionHandle handle, bool allowAlreadyClosed)
    {
        KernelResult<EndpointSessionRegistry.Record> admission = EndpointSessions.PrepareClose(handle, caller, allowAlreadyClosed);
        if (!admission.IsSuccess) return KernelResult.Fail(admission.Error, admission.Message!);
        KernelResult nativeDrain = DrainNativeServiceSession(handle);
        if (!nativeDrain.IsSuccess) return nativeDrain;
        lock (_requestResponseCorrelationGate)
        {
            if (allowAlreadyClosed && _endpointSessionInvocationRegistry is { } invocations &&
                invocations.UnclosedPossibleEffectCount(handle) != 0)
                return KernelResult.Fail(KernelError.SessionDraining, "Dependency invocation effect has not closed; routing accounting is retained.");
            KernelResult<EndpointSessionRegistry.Record> session = EndpointSessions.Close(handle, caller, allowAlreadyClosed);
            if (!session.IsSuccess) return KernelResult.Fail(session.Error, session.Message!);
            CloseSessionResourceDonations(handle);
            // Cleanup removes unaccepted correlations under the invocation owner
            // gate. Acceptance that won the race, or donation quarantine created
            // by cleanup, must still retain dependency routing accounting.
            if (allowAlreadyClosed && SessionInvocations.UnclosedPossibleEffectCount(handle) != 0)
                return KernelResult.Fail(KernelError.SessionDraining, "Invocation cleanup retained a possible effect; dependency routing cannot be retired.");
            KernelResult close = Channels.Close(session.Value!.Channel, allowAlreadyClosed);
            return close.IsSuccess ? KernelResult.Ok() : close;
        }
    }

    public KernelResult<ChannelEnvelope> ReceiveSession(ProcessHandle service, EndpointSessionHandle handle)
    {
        KernelResult<EndpointSessionRequestEnvelope> request = ReceiveSessionRequest(service, handle);
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
                    KernelResult<EndpointSessionRegistry.Record> session = EndpointSessions.ResolveForService(handle, service);
        if (!session.IsSuccess)
            return KernelResult<EndpointSessionRequestEnvelope>.Fail(session.Error, session.Message!);

        endpoint = session.Value!.ServiceEndpoint;
                    KernelResult<SingProcess> process = Processes.Resolve(service);
        if (!process.IsSuccess)
            return KernelResult<EndpointSessionRequestEnvelope>.Fail(process.Error, process.Message!);
                    KernelResult<ChannelEnvelope> next = Channels.Peek(process.Value!, endpoint);
        if (!next.IsSuccess)
            return KernelResult<EndpointSessionRequestEnvelope>.Fail(next.Error, next.Message!);
                    // Preserve a published invocation, or admit V1 public Send's first invocation
                    // from this actual queued request before the dequeue effect.
                    KernelResult<EndpointSessionInvocationHandle> original = SessionInvocations.PrepareOriginalRequestDelivery(handle, session.Value.Caller,
            service, next.Value!.Sequence, next.Value.MessageId);
        if (!original.IsSuccess)
            return KernelResult<EndpointSessionRequestEnvelope>.Fail(original.Error, original.Message!);
                    KernelResult<ChannelEnvelope> received = ReceiveCommitted(service, endpoint);
        if (!received.IsSuccess)
            return KernelResult<EndpointSessionRequestEnvelope>.Fail(received.Error, received.Message!);
        deliveredFact = received.Value!;

                    EndpointSessionInvocationHandle correlation = original.Value;
                    KernelResult<EndpointSessionInvocationHandle> invocation = SessionInvocations.MarkDelivered(handle, service, received.Value.Sequence,received.Value);
        if (!invocation.IsSuccess)
            return KernelResult<EndpointSessionRequestEnvelope>.Fail(invocation.Error, invocation.Message!);

                    KernelResult<bool> cancellation = SessionInvocations.IsCancellationRequested(correlation, service);
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
            KernelResult<EndpointSessionRegistry.Record> session = EndpointSessions.ResolveForService(invocation.Session, service);
        if (!session.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(session.Error, session.Message!);
        KernelResult<ResponseRegistry.OriginalPublication> original;
        lock(_requestResponseCorrelationGate)original=Responses.CapturePublication(service,session.Value!.ServiceEndpoint,invocation.InvocationId.Value);
        if(!original.IsSuccess)return KernelResult<ResponseEnvelope>.Fail(original.Error,original.Message!);
            KernelResult bound =SessionInvocations.BindOriginalResponse(invocation,service,original.Value!);
        if(!bound.IsSuccess)return KernelResult<ResponseEnvelope>.Fail(bound.Error,bound.Message!);
        return SessionInvocations.Publish(
            invocation,
            service,
            () => PublishResponseCore(service, session.Value!.ServiceEndpoint, invocation.InvocationId.Value, payload, observations),
            () =>
            {
                KernelResult expiry =ObserveSessionExpiry(session.Value!.Caller,invocation.Session);
                if(!expiry.IsSuccess)return expiry;
                KernelResult<EndpointSessionRegistry.Record> current =EndpointSessions.ResolveForService(invocation.Session,service);
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
        KernelResult<EndpointSessionRegistry.Record> session = EndpointSessions.ResolveForService(invocation.Session, service);
        if (!session.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(session.Error, session.Message!);
        if(SessionInvocations.HasBoundComputeCopy(invocation,service))
        {
            KernelResult<ResponseRegistry.OriginalPublication> original;
            lock(_requestResponseCorrelationGate)original=Responses.CapturePublication(service,session.Value!.ServiceEndpoint,invocation.InvocationId.Value);
            if(!original.IsSuccess)return KernelResult<ResponseEnvelope>.Fail(original.Error,original.Message!);
            KernelResult bound =SessionInvocations.BindOriginalResponse(invocation,service,original.Value!);
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
        KernelResult<SingProcess> callerProcess = Processes.Resolve(caller);
        if (!callerProcess.IsSuccess)
            return KernelResult<InlineSipInvocationLease>.Fail(callerProcess.Error, callerProcess.Message!);
        KernelResult<SingProcess> serviceProcess = Processes.Resolve(service);
        if (!serviceProcess.IsSuccess)
            return KernelResult<InlineSipInvocationLease>.Fail(serviceProcess.Error, serviceProcess.Message!);
        KernelResult<EndpointSessionRegistry.Record> session = EndpointSessions.Resolve(sessionHandle, caller);
        if (!session.IsSuccess)
            return KernelResult<InlineSipInvocationLease>.Fail(session.Error, session.Message!);
        if (session.Value!.Service != service)
            return KernelResult<InlineSipInvocationLease>.Fail(KernelError.WrongSessionOwner, "Inline invocation service does not match the session peer.");

        KernelResult<EndpointSessionPin> pinned = EndpointSessions.AcquirePin(sessionHandle, caller, service);
        if (!pinned.IsSuccess)
            return KernelResult<InlineSipInvocationLease>.Fail(pinned.Error, pinned.Message!);

        try
        {
            CapabilityRequirementV1[] requirements = session.Value.Requirements.ToArray();
            CapabilityId[] capabilities = session.Value.Capabilities.ToArray();
            ChannelRegistry.BoundedPayloadSnapshot? boundedSnapshot = Channels.CaptureInlineCopiedPayload(session.Value.Channel, messageId, copiedPayload);
            lock (_platformMemoryUseGate)
            {
            callerProcess = Processes.Resolve(caller);
            if (!callerProcess.IsSuccess)
                return KernelResult<InlineSipInvocationLease>.Fail(callerProcess.Error, callerProcess.Message!);
                KernelResult callerEffect = EnsureProcessAcceptsNewEffects(callerProcess.Value!);
            if (!callerEffect.IsSuccess)
                return KernelResult<InlineSipInvocationLease>.Fail(callerEffect.Error, callerEffect.Message!);
            serviceProcess = Processes.Resolve(service);
            if (!serviceProcess.IsSuccess)
                return KernelResult<InlineSipInvocationLease>.Fail(serviceProcess.Error, serviceProcess.Message!);
                KernelResult serviceEffect = EnsureProcessAcceptsNewEffects(serviceProcess.Value!);
            if (!serviceEffect.IsSuccess)
                return KernelResult<InlineSipInvocationLease>.Fail(serviceEffect.Error, serviceEffect.Message!);
            lock (_requestResponseCorrelationGate)
            {
                    KernelResult currentPin = EndpointSessions.RevalidatePin(pinned.Value!);
                if (!currentPin.IsSuccess)
                    return KernelResult<InlineSipInvocationLease>.Fail(currentPin.Error, currentPin.Message!);
                return CapabilityAuthority.CommitSessionPublication(callerProcess.Value!.DomainId,
                    caller.Generation, capabilities, requirements, PublishCopied);

                KernelResult<InlineSipInvocationLease> PublishCopied()
                {
                        KernelResult<ulong> begun = Channels.BeginInlineCopiedInvocation(
                    callerProcess.Value!, serviceProcess.Value!, session.Value.Channel, messageId, copiedPayload, boundedSnapshot);
                if (!begun.IsSuccess)
                    return KernelResult<InlineSipInvocationLease>.Fail(begun.Error, begun.Message!);
                        EndpointSessionInvocationHandle invocation = SessionInvocations.Register(
                    sessionHandle, caller, service, begun.Value, messageId);
                        KernelResult<EndpointSessionInvocationHandle> delivered = SessionInvocations.MarkDelivered(sessionHandle, service, begun.Value);
                if (!delivered.IsSuccess)
                    return KernelResult<InlineSipInvocationLease>.Fail(delivered.Error, delivered.Message!);
                        KernelResult accepted = SessionInvocations.AcceptInvocation(invocation, service, allowInFlightCancellation: false);
                if (!accepted.IsSuccess)
                    return KernelResult<InlineSipInvocationLease>.Fail(accepted.Error, accepted.Message!);
                        TrustedSipInvocationContext context = new TrustedSipInvocationContext(caller, service, sessionHandle, invocation);
                        InlineSipInvocationLease lease = new InlineSipInvocationLease(context, pinned.Value!, messageId);
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
        KernelResult<SingProcess> callerProcess = Processes.Resolve(caller);
        if (!callerProcess.IsSuccess)
            return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(callerProcess.Error, callerProcess.Message!);
        KernelResult<SingProcess> serviceProcess = Processes.Resolve(service);
        if (!serviceProcess.IsSuccess)
            return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(serviceProcess.Error, serviceProcess.Message!);
        KernelResult<EndpointSessionRegistry.Record> session = EndpointSessions.Resolve(sessionHandle, caller);
        if (!session.IsSuccess)
            return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(session.Error, session.Message!);
        if (session.Value!.Service != service)
            return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(KernelError.WrongSessionOwner, "Inline BORROW service does not match the session peer.");

        KernelResult<EndpointSessionPin> pinned = EndpointSessions.AcquirePin(sessionHandle, caller, service);
        if (!pinned.IsSuccess)
            return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(pinned.Error, pinned.Message!);
        InlineSipInvocationLease? invocationLease = null;
        BorrowLease<T>? borrow = null;
        RegionUseDescriptor? acquiredUse = null;
        try
        {
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.AfterSessionPin);
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.BeforeSessionRevalidation);
            CapabilityRequirementV1[] requirements = session.Value.Requirements.ToArray();
            CapabilityId[] capabilities = session.Value.Capabilities.ToArray();
            KernelResult<InlineSipInvocationLease> admission;
            lock (_platformMemoryUseGate)
            {
            callerProcess = Processes.Resolve(caller);
            if (!callerProcess.IsSuccess)
                return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(callerProcess.Error, callerProcess.Message!);
                KernelResult callerEffect = EnsureProcessAcceptsNewEffects(callerProcess.Value!);
            if (!callerEffect.IsSuccess)
                return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(callerEffect.Error, callerEffect.Message!);
            serviceProcess = Processes.Resolve(service);
            if (!serviceProcess.IsSuccess)
                return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(serviceProcess.Error, serviceProcess.Message!);
                KernelResult serviceEffect = EnsureProcessAcceptsNewEffects(serviceProcess.Value!);
            if (!serviceEffect.IsSuccess)
                return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(serviceEffect.Error, serviceEffect.Message!);
            lock (_requestResponseCorrelationGate)
            {
                    KernelResult currentPin = EndpointSessions.RevalidatePin(pinned.Value!);
                if (!currentPin.IsSuccess)
                    return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(currentPin.Error, currentPin.Message!);
                admission = CapabilityAuthority.CommitSessionPublication(callerProcess.Value!.DomainId,
                    caller.Generation, capabilities, requirements, PublishBorrow);

                KernelResult<InlineSipInvocationLease> PublishBorrow()
                {
                        KernelResult<ChannelRegistry.InlineBorrowProjection<T>> begun = Channels.BeginInlineBorrowInvocation(
                    callerProcess.Value!, serviceProcess.Value!, session.Value.Channel, messageId, payload);
                if (!begun.IsSuccess)
                    return KernelResult<InlineSipInvocationLease>.Fail(begun.Error, begun.Message!);
                borrow = begun.Value.Lease;
                        EndpointSessionInvocationHandle invocation = SessionInvocations.Register(sessionHandle, caller, service, begun.Value.Sequence, messageId);
                        KernelResult<EndpointSessionInvocationHandle> delivered = SessionInvocations.MarkDelivered(sessionHandle, service, begun.Value.Sequence);
                if (!delivered.IsSuccess)
                    return KernelResult<InlineSipInvocationLease>.Fail(delivered.Error, delivered.Message!);
                        KernelResult accepted = SessionInvocations.AcceptInvocation(invocation, service, allowInFlightCancellation: false);
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
            KernelResult<RegionUseDescriptor> use = AcquireBorrowRegionUse(
                caller, service, borrow.Handle, RegionUseMode.ReadOnly,
                new RegionUseRange(0, checked((long)borrow.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<T>())));
            if (!use.IsSuccess)
            {
                return KernelResult<InlineBorrowSipInvocationLease<T>>.Fail(use.Error, use.Message!);
            }
            acquiredUse = use.Value!;
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.AfterRegionUseAcquisition);

            InlineBorrowSipInvocationLease<T> result = new InlineBorrowSipInvocationLease<T>(invocationLease, borrow, use.Value!);
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
        InlineSipInvocationLease invocation = lease.TakeInvocation();
        TrustedSipInvocationContext context = invocation.Context;
        bool releaseAttempted = false;
        bool returnAttempted = false;
        bool settlementAttempted = false;
        try
        {
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.BeforeRegionUseRelease);
            releaseAttempted = true;
            KernelResult released = ReleaseRegionUse(service, lease.Use.Handle);
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.AfterRegionUseRelease);
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.BeforeBorrowReturn);
            returnAttempted = true;
            KernelResult returned = ReturnBorrow(service, lease.Borrow.Handle);
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.AfterBorrowReturn);
            InlineBorrowQualificationHook?.At(InlineBorrowQualificationPoint.BeforeInvocationSettlement);
            bool cleanupSucceeded = released.IsSuccess && returned.IsSuccess;
            settlementAttempted = true;
            KernelResult settled = SessionInvocations.CompleteInline(
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
        KernelResult session = EndpointSessions.RevalidatePin(lease.Invocation.SessionPin);
        if (!session.IsSuccess) return session;
        if (!lease.Borrow.IsValid)
            return KernelResult.Fail(KernelError.InvalidRegionState, "Inline BORROW lease is no longer live.");
        KernelResult<RegionUseDescriptor> use = ValidateRegionUse(lease.Context.Service, lease.Use.Handle);
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
            TrustedSipInvocationContext context = invocation.Context;
        if (context.Service != service)
            return KernelResult<OwnedBuffer<T>>.Fail(KernelError.WrongSessionOwner, "Inline ownership response does not match the live service principal.");
            KernelResult<ResponseEnvelope> publication = SessionInvocations.Publish(context.Invocation, service, CommitOwnership);
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
                        KernelResult current = RevalidateInlineSessionInvocation(invocation);
        if (!current.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(current.Error, current.Message!);
                        KernelResult<SingProcess> serviceProcess = Processes.Resolve(service);
        if (!serviceProcess.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(serviceProcess.Error, serviceProcess.Message!);
                        KernelResult<SingProcess> callerProcess = Processes.Resolve(context.Caller);
        if (!callerProcess.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(callerProcess.Error, callerProcess.Message!);

                        KernelResult<EndpointSessionRegistry.Record> session = EndpointSessions.ResolveForService(context.Session, service);
        if (!session.IsSuccess)
            return KernelResult<ResponseEnvelope>.Fail(session.Error, session.Message!);
                        KernelResult<OwnedBuffer<T>> moved = Responses.TransferInlineOwnership(
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
                KernelResult<SingProcess> callerProcess = Processes.Resolve(caller);
        if (!callerProcess.IsSuccess)
            return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(callerProcess.Error, callerProcess.Message!);
                KernelResult callerEffect = EnsureProcessAcceptsNewEffects(callerProcess.Value!);
        if (!callerEffect.IsSuccess)
            return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(callerEffect.Error, callerEffect.Message!);
                KernelResult<SingProcess> serviceProcess = Processes.Resolve(service);
        if (!serviceProcess.IsSuccess)
            return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(serviceProcess.Error, serviceProcess.Message!);
                KernelResult serviceEffect = EnsureProcessAcceptsNewEffects(serviceProcess.Value!);
        if (!serviceEffect.IsSuccess)
            return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(serviceEffect.Error, serviceEffect.Message!);
                KernelResult<EndpointSessionRegistry.Record> session = EndpointSessions.Resolve(sessionHandle, caller);
        if (!session.IsSuccess)
            return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(session.Error, session.Message!);
        if (session.Value!.Service != service)
            return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(KernelError.WrongSessionOwner, "Inline MOVE service does not match the session peer.");
                KernelResult<EndpointSessionPin> pinned = EndpointSessions.AcquirePin(sessionHandle, caller, service);
        if (!pinned.IsSuccess)
            return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(pinned.Error, pinned.Message!);

        try
        {
            lock (_requestResponseCorrelationGate)
            {
                        KernelResult currentPin = EndpointSessions.RevalidatePin(pinned.Value!);
                if (!currentPin.IsSuccess)
                    return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(currentPin.Error, currentPin.Message!);
                return CapabilityAuthority.CommitSessionPublication(callerProcess.Value!.DomainId,
                    caller.Generation, session.Value.Capabilities.ToArray(), session.Value.Requirements.ToArray(), PublishMove);

                KernelResult<InlineMoveSipInvocationLease<T>> PublishMove()
                {
                            KernelResult<ChannelRegistry.InlineMoveProjection<T>> begun = Channels.BeginInlineMoveInvocation(
                    callerProcess.Value!, serviceProcess.Value!, session.Value.Channel, messageId, payload,
                    (source, target, region, admission, noEffect, prepared) => TransferRegionForIpcCore(source, target, region, admission, observations,noEffect,prepared));
                if (!begun.IsSuccess)
                    return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(begun.Error, begun.Message!);
                            EndpointSessionInvocationHandle invocation = SessionInvocations.Register(
                    sessionHandle, caller, service, begun.Value.Sequence, messageId);
                            KernelResult<EndpointSessionInvocationHandle> delivered = SessionInvocations.MarkDelivered(sessionHandle, service, begun.Value.Sequence);
                if (!delivered.IsSuccess)
                    return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(delivered.Error, delivered.Message!);
                            KernelResult accepted = SessionInvocations.AcceptInvocation(invocation, service, allowInFlightCancellation: false);
                if (!accepted.IsSuccess)
                    return KernelResult<InlineMoveSipInvocationLease<T>>.Fail(accepted.Error, accepted.Message!);
                            InlineSipInvocationLease inline = new InlineSipInvocationLease(
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
        KernelResult session = EndpointSessions.RevalidatePin(lease.Invocation.SessionPin);
        if (!session.IsSuccess) return session;
        if (!lease.Payload.IsValid)
            return KernelResult.Fail(KernelError.InvalidRegionState, "Inline MOVE payload is no longer owned by the consumer stage.");
        KernelResult<SingProcess> service = Processes.Resolve(lease.Context.Service);
        if (!service.IsSuccess) return KernelResult.Fail(service.Error, service.Message!);
        KernelResult<RegionDescriptor> authoritative = Regions.Validate(
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
        InlineSipInvocationLease invocation = lease.TakeInvocation();
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
        TrustedSipInvocationContext context = lease.Context;
        if (context.Service != service)
            return KernelResult.Fail(KernelError.WrongSessionOwner, "Inline invocation context does not match the live session parties.");
        KernelResult settled = SessionInvocations.CompleteInline(context.Invocation, service, succeeded);
        if (!settled.IsSuccess) return settled;
        try { CloseResourceDonationForInvocationTerminal(service, context.Invocation); }
        finally { FinalizeInlineInvocationPin(lease); }
        return settled;
    }

    private void FinalizeInlineInvocationPin(InlineSipInvocationLease invocation)
    {
        EndpointSessionHandle session = invocation.Context.Session;
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
        EndpointSessionRegistry.Record? deferredClose = EndpointSessions.ClaimClosedCleanup(session);
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
        KernelResult<EndpointSessionRegistry.Record> session = ResolveSession(caller, invocation.Session);
        if (!session.IsSuccess) return KernelResult.Fail(session.Error, session.Message!);
        return SessionInvocations.RequestCancellation(invocation, caller);
    }

    public KernelResult BindSessionCancellationScope(
        ProcessHandle caller,
        EndpointSessionInvocationHandle invocation,
        CancellationScopeHandle scope)
    {
        long timestamp = CancellationScopes.CaptureTimestamp(scope);
        KernelResult<EndpointSessionRegistry.Record> session = ResolveSession(caller, invocation.Session);
        if (!session.IsSuccess) return KernelResult.Fail(session.Error, session.Message!);
        return SessionInvocations.BindCancellationScope(invocation, caller, scope, timestamp);
    }

    public KernelResult<CancellationObservation> RequestSessionCancellation(
        ProcessHandle caller,
        EndpointSessionInvocationHandle invocation,
        CancellationScopeHandle scope)
    {
        long timestamp = CancellationScopes.CaptureTimestamp(scope);
        KernelResult<EndpointSessionRegistry.Record> session = ResolveSession(caller, invocation.Session);
        if (!session.IsSuccess) return KernelResult<CancellationObservation>.Fail(session.Error, session.Message!);
        return SessionInvocations.RequestCancellation(invocation, caller, scope, timestamp);
    }

    public KernelResult<bool> QuerySessionCancellation(
        ProcessHandle service,
        EndpointSessionInvocationHandle invocation)
    {
        KernelResult<EndpointSessionRegistry.Record> session = EndpointSessions.ResolveForService(invocation.Session, service);
        if (!session.IsSuccess) return KernelResult<bool>.Fail(session.Error, session.Message!);
        return SessionInvocations.IsCancellationRequested(invocation, service);
    }

    private KernelResult ObserveSessionExpiry(ProcessHandle caller,EndpointSessionHandle handle)
    {
        KernelResult<EndpointSessionRegistry.ExpiryObservation?> observation = EndpointSessions.ReadExpiryObservation(handle, caller);
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
        KernelResult expiry =ObserveSessionExpiry(caller,handle);
        if(!expiry.IsSuccess)return KernelResult<EndpointSessionRegistry.Record>.Fail(expiry.Error,expiry.Message!);
        lock(_requestResponseCorrelationGate)
        {
            KernelResult<EndpointSessionRegistry.Record> session = EndpointSessions.Resolve(handle, caller);
        if (!session.IsSuccess) return session;
            KernelResult<SingProcess> callerProcess = Processes.Resolve(caller);
        if (!callerProcess.IsSuccess) return KernelResult<EndpointSessionRegistry.Record>.Fail(callerProcess.Error, callerProcess.Message!);
        foreach (CapabilityId capability in session.Value!.Capabilities)
        {
                KernelResult<CapabilityDescriptorV1> validation = ValidateCapability(caller, capability, CapabilityRights.None);
            if (!validation.IsSuccess) return KernelResult<EndpointSessionRegistry.Record>.Fail(validation.Error, validation.Message!);
        }
        foreach (CapabilityRequirementV1 requirement in session.Value.Requirements)
        {
                bool found = session.Value.Capabilities.Any(id =>
            {
                KernelResult<CapabilityDescriptorV1> validation = CapabilityAuthority.Validate(id, callerProcess.Value!.DomainId, caller.Generation, requirement.Rights);
                return validation.IsSuccess && validation.Value!.ResourceKind == requirement.ResourceKind && validation.Value.ResourceId == requirement.ResourceId;
            });
            if (!found) return KernelResult<EndpointSessionRegistry.Record>.Fail(KernelError.CapabilityRevoked, "Session admission capability is no longer live.");
        }
        return session;
        }
    }

    internal KernelResult CloseSessionsForProcess(ProcessHandle process)
    {
        foreach (EndpointSessionRegistry.Record session in EndpointSessions.CloseForProcess(process))
        {
            KernelResult nativeDrain = DrainNativeServiceSession(session.Handle);
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
        KernelResult<EndpointSessionRegistry.Record> session = ResolveSession(caller, handle);
        if (!session.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(session.Error, session.Message!);
        EndpointSessionRegistry.Record current = session.Value!;
        KernelResult<EndpointSessionPin> pinned = EndpointSessions.AcquirePin(handle, caller, current.Service);
        if (!pinned.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(pinned.Error, pinned.Message!);
        EndpointSessionPin sessionPin = pinned.Value!;
        try
        {
            KernelResult currentPin = EndpointSessions.RevalidatePin(sessionPin);
        if (!currentPin.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(currentPin.Error, currentPin.Message!);
            KernelResult<ChannelEnvelope> send = SendCore(caller, current.Service, current.Channel, messageId, payload, null,
            current.Capabilities, AdmissionQosHint.None, null, new(handle, sessionPin, current.Requirements.ToArray(),Custody:custody));
        if (!send.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(send.Error, send.Message!);
            EndpointSessionInvocationHandle invocation = SessionInvocations.Register(handle, caller, current.Service, send.Value!.Sequence, messageId);
        FinalizeSessionPin(sessionPin, handle);
        using CancellationTokenRegistration cancellation = cancellationToken.CanBeCanceled
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
        KernelResult<CancellationObservation> temporal = CancellationScopes.Observe(caller, scope);
        if (!temporal.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(temporal.Error, temporal.Message!);
        if (temporal.Value!.Disposition == CancellationDisposition.Stale)
            return KernelResult<ResponseEnvelope>.Fail(KernelError.StaleGeneration, "Cancellation scope generation is stale.");
        if (temporal.Value.CancellationRequested)
        {
            _ = CancellationScopes.RecordDisposition(caller, scope, CancellationDisposition.CancelledBeforeEffect);
            return KernelResult<ResponseEnvelope>.Fail(KernelError.DeadlineExpired, "IPC deadline expired before transfer admission.");
        }

        KernelResult<EndpointSessionRegistry.Record> session = ResolveSession(caller, handle);
        if (!session.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(session.Error, session.Message!);
        KernelResult claim = CancellationScopes.ClaimConsumer(
            caller,
            scope,
            $"ipc:{handle.SessionId.Value}:{handle.Generation.Value}:{scope.ScopeId.Value}:{scope.Generation.Value}");
        if (!claim.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(claim.Error, claim.Message!);

        EndpointSessionRegistry.Record current = session.Value!;
        KernelResult<EndpointSessionPin> pinned = EndpointSessions.AcquirePin(handle, caller, current.Service);
        if (!pinned.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(pinned.Error, pinned.Message!);
        EndpointSessionPin sessionPin = pinned.Value!;
        try
        {
            KernelResult currentPin = EndpointSessions.RevalidatePin(sessionPin);
        if (!currentPin.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(currentPin.Error, currentPin.Message!);
            KernelResult<ChannelEnvelope> send = SendCore(caller, current.Service, current.Channel, messageId, payload, null,
            current.Capabilities, AdmissionQosHint.None, null, new(handle, sessionPin, current.Requirements.ToArray(), scope));
        if (!send.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(send.Error, send.Message!);
            EndpointSessionInvocationHandle invocation = SessionInvocations.Register(
            handle, caller, current.Service, send.Value!.Sequence, messageId, scope);
        FinalizeSessionPin(sessionPin, handle);
            Task<KernelResult<ResponseEnvelope>> response = WaitForResponseAsync(caller, current.Channel, send.Value.Sequence).AsTask();
        if (!stopWaiting.CanBeCanceled)
            return await response.ConfigureAwait(false);

            Task stopped = Task.Delay(Timeout.InfiniteTimeSpan, stopWaiting);
        if (await Task.WhenAny(response, stopped).ConfigureAwait(false) == response)
            return await response.ConfigureAwait(false);

            KernelResult<CancellationObservation> cancellation = SessionInvocations.RequestCancellation(invocation, caller, scope);
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
                EndpointSessionInvocationRegistry.OriginalSend? original =custody.Read(this);
            if(original is null) return KernelResult.Fail(KernelError.WrongSessionOwner,"Original send custody is copied or foreign.");
                KernelResult<(EndpointSessionInvocationHandle Invocation, ProcessHandle Service, ChannelEnvelope Raw, ChannelEndpointHandle Channel)> pending =SessionInvocations.ReadPendingOriginalSend(original,custody.Caller);
            if(!pending.IsSuccess) return KernelResult.Fail(pending.Error,pending.Message!);
                (object Fact, object? Lifetime, ChannelId Channel, ulong Sequence, ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation)[] charges =Budgets.OriginalQueuedCharges().Where(charge=>charge.Channel==pending.Value.Channel.ChannelId && charge.Sequence==pending.Value.Raw.Sequence && charge.Sender==custody.Caller).ToArray();
            if(charges.Length!=1 || charges[0].Lifetime is null)
                return KernelResult.Fail(KernelError.PlatformFaulted,"Exact original queued charge unavailable.");
                (object Fact, object? Lifetime, ChannelId Channel, ulong Sequence, ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation) charge =charges[0];
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
                    EndpointSessionInvocationRegistry.OriginalSend? original =custody.Read(this);
            if(original is null) return KernelResult.Fail(KernelError.WrongSessionOwner,"Original custody is copied or foreign.");
            if(SessionInvocations.OriginalSendCleanupIsCompleted(original))
            {
                KernelResult<EndpointSessionPin> known = SessionInvocations.ReadOriginalSendCleanup(original);
                if (!known.IsSuccess) return KernelResult.Fail(known.Error, known.Message!);
                completedPin = known.Value!;
                EndpointSessions.ConsumeOriginalSendPinCorrelation(completedPin);
                return KernelResult.Ok();
            }
                    KernelResult<(EndpointSessionInvocationHandle Invocation, ProcessHandle Service, ChannelEnvelope Raw, ChannelEndpointHandle Channel)> pending =SessionInvocations.ReadPendingOriginalSend(original,custody.Caller);
                    KernelResult<(ChannelEndpointHandle Channel, ulong Sequence, ProcessHandle Service, PreparedOwnedTransfer Prepared)> transfer =SessionInvocations.ReadOriginalCompletedSendTransfer(original,custody.Caller);
            if(!pending.IsSuccess && !transfer.IsSuccess) return KernelResult.Fail(pending.Error,pending.Message!);
                    ChannelEndpointHandle channel =pending.IsSuccess ? pending.Value.Channel : transfer.Value.Channel;
                    ulong sequence =pending.IsSuccess ? pending.Value.Raw.Sequence : transfer.Value.Sequence;
                    ProcessHandle serviceHandle =pending.IsSuccess ? pending.Value.Service : transfer.Value.Service;
                    (object Fact, object? Lifetime, ChannelId Channel, ulong Sequence, ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation)[] charges =Budgets.OriginalQueuedCharges().Where(item=>item.Channel==channel.ChannelId && item.Sequence==sequence && item.Sender==custody.Caller).ToArray();
            if(charges.Length!=1 || charges[0].Lifetime is null) return KernelResult.Fail(KernelError.PlatformFaulted,"Original queued charge missing.");
                    (object Fact, object? Lifetime, ChannelId Channel, ulong Sequence, ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation) charge =charges[0];
            object?[] payloads;
            if(pending.IsSuccess)
            {
                        KernelResult withdrawn =Channels.WithdrawOriginalQueuedEnvelope(channel,pending.Value.Raw,charge.Lifetime!,charge.Fact);
                if(!withdrawn.IsSuccess) return withdrawn;
                payloads=[pending.Value.Raw.Payload,pending.Value.Raw.SecondaryPayload];
            }
            else
            {
                        KernelResult<object[]> withdrawn =Channels.WithdrawUnpublishedOriginalTransfer(channel,sequence,transfer.Value.Prepared,charge.Lifetime!,charge.Fact);
                if(!withdrawn.IsSuccess) return KernelResult.Fail(withdrawn.Error,withdrawn.Message!);
                payloads=withdrawn.Value!;
            }
                    KernelResult<SingProcess> service =Processes.Resolve(serviceHandle);
                    KernelResult<(RegionHandle Source, DomainId SourceDomain, RegionOwner TargetOwner, object RegionOwner, PreparedOwnedTransfer Prepared, (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation)[] Charges)> transferFacts =SessionInvocations.ReadOriginalTransferCleanupFacts(original);
            if(!transferFacts.IsSuccess) return KernelResult.Fail(transferFacts.Error,transferFacts.Message!);
                    KernelResult<(KernelResult<RegionHandle>? Raw, BudgetReservationHandle? Reservation, object? BudgetOwner)> producer =SessionInvocations.ReadOriginalTransferProducer(original);
            if(!producer.IsSuccess || producer.Value.Raw is not { IsSuccess:true } raw ||
                !transferFacts.Value.Prepared.IsOriginal || transferFacts.Value.Prepared.SourceHandle!=transferFacts.Value.Source ||
                raw.Value.RegionId!=transferFacts.Value.Source.RegionId ||
                raw.Value.Generation.Value!=transferFacts.Value.Source.Generation.Value+1)
                return KernelResult.Fail(KernelError.PlatformFaulted,"Exact original Region effect unavailable.");
                    RegionOwner owner =transferFacts.Value.TargetOwner;
                    KernelResult cleanup =KernelResult.Ok();
            foreach(object? payload in payloads)
            {
                if(payload is SingPlus.Sip.IOriginalBorrowPayload loan)
                {
                            KernelResult closed =Regions.CleanupWithdrawnBorrow(loan,owner);
                    if(!closed.IsSuccess) cleanup=closed;
                }
                else if(payload is SingPlus.Sip.ITransferableOwnedPayload moved)
                {
                    if(moved.Handle!=raw.Value || !ReferenceEquals(moved,transferFacts.Value.Prepared.Projection))
                        return KernelResult.Fail(KernelError.PlatformFaulted,"Withdrawn payload differs from original Region raw.");
                            KernelResult closed =Regions.CleanupWithdrawnPayload(moved,owner,transferFacts.Value.RegionOwner);
                    if(!closed.IsSuccess) cleanup=closed;
                    else if(service.IsSuccess) service.Value!.RemoveRegion(moved.Handle);
                }
                else return KernelResult.Fail(KernelError.UnsupportedPayload,"Original pair resource is not a sealed ownership payload.");
            }
                    KernelResult<bool> ended =Regions.HasEndedOriginalAccountingLifetime(transferFacts.Value.RegionOwner,raw.Value);
            if((!ended.IsSuccess || !ended.Value) && cleanup.IsSuccess)
                cleanup=KernelResult.Fail(KernelError.PlatformFaulted,"Original Region closure remains pinned.");
            if(ended.IsSuccess && ended.Value && producer.Value.Reservation is { } targetReservation)
            {
                if(producer.Value.BudgetOwner is null) return KernelResult.Fail(KernelError.PlatformFaulted,"Original target charge absent.");
                        KernelResult<BudgetReservationSnapshot> priorTarget =Budgets.Query(targetReservation);
                        KernelResult consumedTarget =Budgets.ConsumeClosedTransferReservation(producer.Value.BudgetOwner,serviceHandle,targetReservation,
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
            foreach((object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation) sourceCharge in transferFacts.Value.Charges)
            {
                if(sourceCharge.RegionOwner is null || !ReferenceEquals(sourceCharge.RegionOwner,transferFacts.Value.RegionOwner))
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original source accounting owner changed.");
                        KernelResult<bool> sourceEnded =Regions.HasEndedOriginalAccountingLifetime(sourceCharge.RegionOwner,transferFacts.Value.Source);
                if(!sourceEnded.IsSuccess || !sourceEnded.Value) return KernelResult.Fail(KernelError.PlatformFaulted,"Original source charge lifetime remains pinned.");
                        KernelResult sourceBinding =Budgets.ValidateOriginalRegionChargeBinding(sourceCharge.Fact,sourceCharge.Owner,sourceCharge.Reservation,
                    transferFacts.Value.Source,sourceCharge.RegionOwner,transferFacts.Value.SourceDomain);
                if(!sourceBinding.IsSuccess) return sourceBinding;
                        KernelResult<BudgetReservationSnapshot> priorSource =Budgets.Query(sourceCharge.Reservation);
                        KernelResult<BudgetReservationSnapshot> consumedSource =Budgets.ReleaseOriginalRegionCharge(sourceCharge.Fact,sourceCharge.Owner,sourceCharge.Reservation);
                if(!consumedSource.IsSuccess || consumedSource.Value!.State!=BudgetReservationState.Released)
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original source charge remains pinned.");
                RetireExactRegionBudgetRoute(transferFacts.Value.Source,sourceCharge.Owner,sourceCharge.Reservation);
                if(priorSource.IsSuccess && priorSource.Value!.State!=BudgetReservationState.Released)
                    observations.Add(new(sourceCharge.Owner,TraceEventKind.BudgetReleased,sourceCharge.Reservation));
            }
            if(!cleanup.IsSuccess) return cleanup;
            if(!Channels.HasEndedOriginalQueuedLifetime(charge.Lifetime,charge.Channel,charge.Sequence,charge.Fact))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original withdrawal lifetime changed.");
                    KernelResult<BudgetReservationSnapshot> consumed =Budgets.ConsumeWithdrawnQueuedCharge(charge.Fact,charge.Lifetime!,charge.Sender,charge.Reservation);
            if(!consumed.IsSuccess) return KernelResult.Fail(consumed.Error,consumed.Message!);
            if(!pending.IsSuccess && !Channels.CompleteUnpublishedOriginalTransfer(charge.Lifetime!,transfer.Value.Prepared,charge.Fact))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original unpublished transfer cleanup continuity changed.");
            Channels.ConsumeSendBudgetCorrelation(channel,charge.Sender,charge.Reservation);
                    KernelResult<EndpointSessionPin> completed =SessionInvocations.CompleteWithdrawnOriginalSend(original,custody.Caller);
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
            if(_regionBudgetReservations.TryGetValue(region,out (ProcessHandle Owner, BudgetReservationHandle Reservation) route) && route==(owner,reservation))
                _regionBudgetReservations.Remove(region);
    }

    internal async ValueTask<KernelResult<ResponseEnvelope>> InvokeSessionOwnershipPairAsync(
        ProcessHandle caller,EndpointSessionHandle handle,uint messageId,object first,object second,
        CancellationToken cancellationToken,OriginalSessionSendCustody? custody)
    {
        if(custody is not null && !custody.Matches(this,caller,handle))
            return KernelResult<ResponseEnvelope>.Fail(KernelError.WrongSessionOwner,"Original send custody is copied or foreign.");
        KernelResult<EndpointSessionRegistry.Record> session = ResolveSession(caller, handle);
        if (!session.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(session.Error, session.Message!);
        EndpointSessionRegistry.Record current = session.Value!;
        KernelResult<EndpointSessionPin> pinned = EndpointSessions.AcquirePin(handle, caller, current.Service);
        if (!pinned.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(pinned.Error, pinned.Message!);
        EndpointSessionPin sessionPin = pinned.Value!;
        try
        {
            KernelResult currentPin = EndpointSessions.RevalidatePin(sessionPin);
        if (!currentPin.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(currentPin.Error, currentPin.Message!);
            KernelResult<ChannelEnvelope> send = SendCore(caller, current.Service, current.Channel, messageId, first, second,
            current.Capabilities, AdmissionQosHint.None, null, new(handle, sessionPin, current.Requirements.ToArray(),Custody:custody));
        if (!send.IsSuccess) return KernelResult<ResponseEnvelope>.Fail(send.Error, send.Message!);
            EndpointSessionInvocationHandle invocation = SessionInvocations.Register(handle, caller, current.Service, send.Value!.Sequence, messageId);
        FinalizeSessionPin(sessionPin, handle);
        using CancellationTokenRegistration cancellation = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => _ = RequestSessionCancellation(caller, invocation))
            : default;
        return await WaitForResponseAsync(caller, current.Channel, send.Value.Sequence).ConfigureAwait(false);
        }
        finally { FinalizeSessionPin(sessionPin, handle); }
    }
}






