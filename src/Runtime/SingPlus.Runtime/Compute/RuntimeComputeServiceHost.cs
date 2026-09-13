using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;
using SingPlus.Sip.Compute;

namespace SingPlus.Runtime;

/// <summary>
/// Privileged runtime composition for the bounded ComputeService DSC1 Copy contour.
///
/// The public SIP source remains a caller-owned read borrow while destination
/// ownership moves to the service. Current platform mappings are owner-bound and
/// the DSC1 model contract admits both ranges under one platform-domain subject,
/// so this host snapshots the bounded source borrow into a service-owned staging
/// buffer before platform admission. The original borrow remains live until all
/// accepted platform use has reached exact terminal closure and the temporary
/// mappings are closed.
///
/// This is a ModelOnly composition. It is not direct-borrow external execution,
/// zero-copy, coherent shared memory or executable HybridCPU evidence.
/// </summary>
public sealed class RuntimeComputeServiceHost
{
    private sealed record ReceivedCopyRequest(
        ChannelEnvelope Request,
        EndpointSessionInvocationHandle? Invocation,
        bool CancellationRequested);

    private sealed class PendingCopy(
        ulong requestSequence,
        BorrowLease<byte> sourceBorrow,
        OwnedBuffer<byte> destination,
        EndpointSessionInvocationHandle? invocation)
    {
        public ulong RequestSequence { get; } = requestSequence;
        public BorrowLease<byte> SourceBorrow { get; } = sourceBorrow;
        public OwnedBuffer<byte> Destination { get; } = destination;
        public EndpointSessionInvocationHandle? Invocation { get; } = invocation;

        public OwnedBuffer<byte>? StagingSource { get; set; }
        public CapabilityId? StagingMappingCapability { get; set; }
        public CapabilityId? DestinationMappingCapability { get; set; }
        public PlatformRegionMapping? StagingMapping { get; set; }
        public PlatformRegionMapping? DestinationMapping { get; set; }
        public PlatformDsc1CopySubmission? Submission { get; set; }
        public PlatformDsc1CopyOutcome? Outcome { get; set; }

        public KernelError? AbortError { get; set; }
        public string? AbortMessage { get; set; }
        public bool CancellationRequested { get; set; }

        public bool StagingMappingCloseStarted { get; set; }
        public bool StagingMappingClosed { get; set; }
        public bool DestinationMappingCloseStarted { get; set; }
        public bool DestinationMappingClosed { get; set; }
        public bool StagingCapabilityRevoked { get; set; }
        public bool DestinationCapabilityRevoked { get; set; }
        public bool StagingReleased { get; set; }
        public bool SourceBorrowReturned { get; set; }
        public bool ResponseFinalized { get; set; }
        public bool DestinationReleased { get; set; }
    }

    private readonly RuntimeKernel _kernel;
    private readonly ProcessHandle _service;
    private readonly ChannelEndpointHandle _endpoint;
    private readonly EndpointSessionHandle? _session;
    private readonly PlatformDomainBinding _platformDomain;
    private readonly Dsc1ComputeCapability _platformComputeCapability;
    private readonly DomainId _serviceDomain;
    private PendingCopy? _pending;

    private RuntimeComputeServiceHost(
        RuntimeKernel kernel,
        ProcessHandle service,
        ChannelEndpointHandle endpoint,
        EndpointSessionHandle? session,
        PlatformDomainBinding platformDomain,
        Dsc1ComputeCapability platformComputeCapability,
        DomainId serviceDomain)
    {
        _kernel = kernel;
        _service = service;
        _endpoint = endpoint;
        _session = session;
        _platformDomain = platformDomain;
        _platformComputeCapability = platformComputeCapability;
        _serviceDomain = serviceDomain;
    }

    public bool HasPendingCopy => _pending is not null;
    public ulong? PendingRequestSequence => _pending?.RequestSequence;

    public KernelResult CancelNextRequestBeforeAcceptance()
    {
        if (_pending is not null)
            return KernelResult.Fail(KernelError.InvalidTransition, "A ComputeService request has already reached service acceptance.");

        var received = ReceiveRequest();
        if (!received.IsSuccess) return KernelResult.Fail(received.Error, received.Message!);
        var request = received.Value!.Request;
        if (request.Payload is not BorrowLease<byte> sourceBorrow || request.SecondaryPayload is not OwnedBuffer<byte> destination)
            return KernelResult.Fail(KernelError.UnsupportedPayload, "Cancellation requires the exact ComputeService ownership pair.");

        if (received.Value.CancellationRequested && received.Value.Invocation is { } requestedInvocation)
        {
            var accepted = _kernel.AcceptSessionCancellation(_service, requestedInvocation);
            if (!accepted.IsSuccess) return accepted;
        }

        return SettleDeliveredCancellation(request.Sequence, received.Value.Invocation, sourceBorrow, destination);
    }

    public static KernelResult<RuntimeComputeServiceHost> Create(
        RuntimeKernel kernel,
        ProcessHandle service,
        ChannelEndpointHandle endpoint,
        PlatformDomainBinding platformDomain,
        Dsc1ComputeCapability platformComputeCapability) =>
        CreateCore(kernel, service, endpoint, session: null, platformDomain, platformComputeCapability);

    private static KernelResult<RuntimeComputeServiceHost> CreateCore(
        RuntimeKernel kernel,
        ProcessHandle service,
        ChannelEndpointHandle endpoint,
        EndpointSessionHandle? session,
        PlatformDomainBinding platformDomain,
        Dsc1ComputeCapability platformComputeCapability)
    {
        ArgumentNullException.ThrowIfNull(kernel);

        var process = kernel.Processes.Resolve(service);
        if (!process.IsSuccess)
        {
            return KernelResult<RuntimeComputeServiceHost>.Fail(
                process.Error,
                process.Message!);
        }

        var capability = kernel.ValidateCapability(
            service,
            platformComputeCapability.CapabilityId,
            CapabilityRights.Execute);
        if (!capability.IsSuccess)
        {
            return KernelResult<RuntimeComputeServiceHost>.Fail(
                capability.Error,
                capability.Message!);
        }

        if (capability.Value!.ResourceKind != ResourceKind.Compute ||
            !string.Equals(
                capability.Value.ResourceId,
                CapabilityResourceIds.Dsc1Copy,
                StringComparison.Ordinal))
        {
            return KernelResult<RuntimeComputeServiceHost>.Fail(
                KernelError.WrongCapabilityResource,
                "The service platform capability does not authorize DSC1 Copy v1.");
        }

        var expectedSubject = new PlatformDomainIdentity(
            process.Value!.DomainId,
            service);
        var binding = kernel.PlatformAuthority.ValidateDomain(
            platformDomain,
            expectedSubject);
        if (!binding.IsSuccess)
        {
            return KernelResult<RuntimeComputeServiceHost>.Fail(
                binding.Error,
                binding.Message!);
        }

        return KernelResult<RuntimeComputeServiceHost>.Ok(
            new RuntimeComputeServiceHost(
                kernel,
                service,
                endpoint,
                session,
                platformDomain,
                platformComputeCapability,
                process.Value.DomainId));
    }

    public static KernelResult<RuntimeComputeServiceHost> CreateForComponent(
        RuntimeKernel kernel,
        ComponentIdentity component,
        EndpointSessionHandle session)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        var authority = kernel.ResolveComputeComponentAuthority(component);
        if (!authority.IsSuccess) return KernelResult<RuntimeComputeServiceHost>.Fail(authority.Error, authority.Message!);
        var resolvedSession = kernel.EndpointSessions.ResolveForService(session, authority.Value.Process);
        if (!resolvedSession.IsSuccess) return KernelResult<RuntimeComputeServiceHost>.Fail(resolvedSession.Error, resolvedSession.Message!);
        return CreateCore(
            kernel,
            authority.Value.Process,
            resolvedSession.Value!.ServiceEndpoint,
            session,
            authority.Value.Binding,
            new Dsc1ComputeCapability(authority.Value.ComputeCapability));
    }

    public static KernelResult<RuntimeComputeServiceHost> CreateForSession(
        RuntimeKernel kernel,
        ProcessHandle service,
        EndpointSessionHandle session,
        PlatformDomainBinding platformDomain,
        Dsc1ComputeCapability platformComputeCapability)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        var resolvedSession = kernel.EndpointSessions.ResolveForService(session, service);
        if (!resolvedSession.IsSuccess) return KernelResult<RuntimeComputeServiceHost>.Fail(resolvedSession.Error, resolvedSession.Message!);
        return CreateCore(
            kernel,
            service,
            resolvedSession.Value!.ServiceEndpoint,
            session,
            platformDomain,
            platformComputeCapability);
    }

    /// <summary>
    /// Receives exactly one ComputeService Copy request and advances it as far as
    /// the current provider permits. Only one request is admitted at a time so the
    /// service adds no queue or scheduler authority beyond the existing channel.
    /// </summary>
    public KernelResult ProcessNextCopy()
    {
        if (_pending is not null)
        {
            return KernelResult.Fail(
                KernelError.InvalidTransition,
                "The ComputeService host already has a pending Copy request.");
        }

        var received = ReceiveRequest();
        if (!received.IsSuccess)
            return KernelResult.Fail(received.Error, received.Message!);

        var request = received.Value!.Request;
        var invocation = received.Value.Invocation;
        if (request.MessageId != IComputeServiceProtocol.Message_CopyAsync ||
            request.Payload is not BorrowLease<byte> sourceBorrow ||
            request.SecondaryPayload is not OwnedBuffer<byte> destination)
        {
            _ = FinalizeResponse(request.Sequence, invocation, publish: false, payload: null);
            return KernelResult.Fail(
                KernelError.UnsupportedPayload,
                "The ComputeService runtime received a request that is not the exact generated Copy ownership pair.");
        }

        if (invocation is { } exactInvocation)
        {
            if (received.Value.CancellationRequested)
            {
                var acceptedCancellation = _kernel.AcceptSessionCancellation(_service, exactInvocation);
                if (!acceptedCancellation.IsSuccess) return acceptedCancellation;
                return SettleDeliveredCancellation(request.Sequence, exactInvocation, sourceBorrow, destination);
            }

            var accepted = _kernel.AcceptSessionInvocation(
                _service,
                exactInvocation,
                allowInFlightCancellation: true);
            if (!accepted.IsSuccess)
            {
                if (accepted.Error == KernelError.InvalidTransition)
                {
                    var cancellation = _kernel.QuerySessionCancellation(_service, exactInvocation);
                    if (cancellation.IsSuccess && cancellation.Value)
                    {
                        var acceptedCancellation = _kernel.AcceptSessionCancellation(_service, exactInvocation);
                        if (!acceptedCancellation.IsSuccess) return acceptedCancellation;
                        return SettleDeliveredCancellation(request.Sequence, exactInvocation, sourceBorrow, destination);
                    }
                }
                return accepted;
            }

            var postAcceptanceCancellation = _kernel.QuerySessionCancellation(_service, exactInvocation);
            if (!postAcceptanceCancellation.IsSuccess)
                return KernelResult.Fail(postAcceptanceCancellation.Error, postAcceptanceCancellation.Message!);
            if (postAcceptanceCancellation.Value)
            {
                var acceptedCancellation = _kernel.AcceptSessionCancellation(_service, exactInvocation);
                if (!acceptedCancellation.IsSuccess) return acceptedCancellation;
                return SettleDeliveredCancellation(request.Sequence, exactInvocation, sourceBorrow, destination);
            }
        }

        var pending = new PendingCopy(
            request.Sequence,
            sourceBorrow,
            destination,
            invocation);
        _pending = pending;

        var setup = PrepareAndSubmit(pending);
        if (!setup.IsSuccess)
        {
            pending.AbortError = setup.Error;
            pending.AbortMessage = setup.Message;

            // PlatformFaulted may mean the provider accepted an effect whose exact
            // identity cannot be reconciled. Never close mappings, return the borrow,
            // cancel the response or release destination through that ambiguity.
            if (setup.Error == KernelError.PlatformFaulted)
                return setup;
        }

        return AdvancePendingCopy();
    }

    /// <summary>
    /// Advances provider observation or cleanup for the current request. A
    /// PlatformBindingDraining result means the exact request remains pending and
    /// neither the source borrow nor destination response authority has returned.
    /// </summary>
    public KernelResult AdvancePendingCopy()
    {
        var pending = _pending;
        if (pending is null)
        {
            return KernelResult.Fail(
                KernelError.InvalidTransition,
                "The ComputeService host has no pending Copy request.");
        }

        if (pending.AbortError == KernelError.PlatformFaulted)
        {
            return KernelResult.Fail(
                pending.AbortError.Value,
                pending.AbortMessage ??
                "The ComputeService request is fault-pinned by ambiguous platform state.");
        }

        var callerCancellation = ObserveCallerCancellation(pending);
        if (!callerCancellation.IsSuccess) return callerCancellation;

        if (pending.AbortError is null && pending.Outcome is null)
        {
            if (pending.Submission is not { } submission)
            {
                return KernelResult.Fail(
                    KernelError.PlatformFaulted,
                    "A non-aborted ComputeService request lost its exact DSC1 submission identity.");
            }

            var terminal = pending.CancellationRequested
                ? _kernel.CancelPlatformDsc1Copy(_service, submission)
                : _kernel.ObservePlatformDsc1Copy(_service, submission);
            if (!terminal.IsSuccess)
                return KernelResult.Fail(terminal.Error, terminal.Message!);

            pending.Outcome = terminal.Value!.Outcome;
        }

        var cleanup = AdvanceCleanup(pending);
        if (!cleanup.IsSuccess)
            return cleanup;

        _pending = null;

        if (pending.AbortError is { } abortError)
        {
            return KernelResult.Fail(
                abortError,
                pending.AbortMessage ?? "The ComputeService request was rejected before accepted DSC1 execution.");
        }

        return KernelResult.Ok();
    }

    /// <summary>
    /// Requests exact cancellation/drain for an accepted request. Cancellation is
    /// not reported as a successful Copy response: after closure the source borrow
    /// returns, the correlated response is cancelled and service-owned destination
    /// authority is released locally.
    /// </summary>
    public KernelResult CancelPendingCopy()
    {
        var pending = _pending;
        if (pending is null)
        {
            return KernelResult.Fail(
                KernelError.InvalidTransition,
                "The ComputeService host has no pending Copy request to cancel.");
        }

        if (pending.AbortError == KernelError.PlatformFaulted ||
            pending.Submission is null && pending.AbortError is null)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The pending ComputeService request has no trustworthy exact DSC1 identity that can be cancelled.");
        }

        pending.CancellationRequested = true;
        return AdvancePendingCopy();
    }

    private KernelResult ObserveCallerCancellation(PendingCopy pending)
    {
        if (pending.CancellationRequested || pending.Invocation is not { } invocation)
            return KernelResult.Ok();

        var requested = _kernel.QuerySessionCancellation(_service, invocation);
        if (!requested.IsSuccess) return KernelResult.Fail(requested.Error, requested.Message!);
        if (!requested.Value) return KernelResult.Ok();

        var accepted = _kernel.AcceptSessionCancellation(_service, invocation);
        if (!accepted.IsSuccess) return accepted;
        pending.CancellationRequested = true;
        return KernelResult.Ok();
    }

    private KernelResult<ReceivedCopyRequest> ReceiveRequest()
    {
        if (_session is { } session)
        {
            var received = _kernel.ReceiveSessionRequest(_service, session);
            return received.IsSuccess
                ? KernelResult<ReceivedCopyRequest>.Ok(new ReceivedCopyRequest(
                    received.Value!.Request,
                    received.Value.Invocation,
                    received.Value.CancellationRequested))
                : KernelResult<ReceivedCopyRequest>.Fail(received.Error, received.Message!);
        }

        var raw = _kernel.Receive(_service, _endpoint);
        return raw.IsSuccess
            ? KernelResult<ReceivedCopyRequest>.Ok(new ReceivedCopyRequest(raw.Value!, null, false))
            : KernelResult<ReceivedCopyRequest>.Fail(raw.Error, raw.Message!);
    }

    private KernelResult SettleDeliveredCancellation(
        ulong requestSequence,
        EndpointSessionInvocationHandle? invocation,
        BorrowLease<byte> sourceBorrow,
        OwnedBuffer<byte> destination)
    {
        var returned = _kernel.ReturnBorrow(_service, sourceBorrow.Handle);
        if (!returned.IsSuccess) return returned;
        var cancelled = FinalizeResponse(requestSequence, invocation, publish: false, payload: null);
        if (!cancelled.IsSuccess) return KernelResult.Fail(cancelled.Error, cancelled.Message!);
        return _kernel.ReleaseRegion(_service, destination);
    }

    private KernelResult<ResponseEnvelope> FinalizeResponse(
        ulong requestSequence,
        EndpointSessionInvocationHandle? invocation,
        bool publish,
        object? payload)
    {
        if (invocation is { } exactInvocation)
        {
            return publish
                ? _kernel.PublishSessionResponse(_service, exactInvocation, payload)
                : _kernel.CancelSessionResponse(_service, exactInvocation);
        }

        return publish
            ? _kernel.PublishResponse(_service, _endpoint, requestSequence, payload)
            : _kernel.CancelResponse(_service, _endpoint, requestSequence);
    }

    private KernelResult PrepareAndSubmit(PendingCopy pending)
    {
        if (pending.SourceBorrow.Length != pending.Destination.Length)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "ComputeService Copy requires equal source and destination byte lengths.");
        }

        if (pending.SourceBorrow.Length <= 0 ||
            pending.SourceBorrow.Length > PlatformDsc1ComputeContract.MaximumByteLength)
        {
            return KernelResult.Fail(
                KernelError.PlatformDenied,
                "ComputeService Copy exceeds the bounded DSC1 Copy v1 byte length.");
        }

        var staging = _kernel.AllocateBuffer<byte>(
            _service,
            pending.SourceBorrow.Length);
        if (!staging.IsSuccess)
            return KernelResult.Fail(staging.Error, staging.Message!);

        pending.StagingSource = staging.Value!;
        pending.SourceBorrow.Span.CopyTo(pending.StagingSource.Span);

        var sourceCapability = _kernel.MintCapability(
            _serviceDomain,
            _service,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(
                pending.StagingSource.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read);
        if (!sourceCapability.IsSuccess)
            return KernelResult.Fail(sourceCapability.Error, sourceCapability.Message!);
        pending.StagingMappingCapability = sourceCapability.Value!.CapabilityId;

        var destinationCapability = _kernel.MintCapability(
            _serviceDomain,
            _service,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(
                pending.Destination.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Write);
        if (!destinationCapability.IsSuccess)
            return KernelResult.Fail(destinationCapability.Error, destinationCapability.Message!);
        pending.DestinationMappingCapability = destinationCapability.Value!.CapabilityId;

        var sourceMapping = _kernel.MapPlatformOwnedRegion(
            _service,
            _platformDomain,
            pending.StagingMappingCapability.Value,
            pending.StagingSource.Handle,
            PlatformMemoryAccess.Read);
        if (!sourceMapping.IsSuccess)
            return KernelResult.Fail(sourceMapping.Error, sourceMapping.Message!);
        pending.StagingMapping = sourceMapping.Value!;

        var destinationMapping = _kernel.MapPlatformOwnedRegion(
            _service,
            _platformDomain,
            pending.DestinationMappingCapability.Value,
            pending.Destination.Handle,
            PlatformMemoryAccess.Write);
        if (!destinationMapping.IsSuccess)
            return KernelResult.Fail(destinationMapping.Error, destinationMapping.Message!);
        pending.DestinationMapping = destinationMapping.Value!;

        var sourceRange = new PlatformDsc1RegionRange(
            pending.StagingMapping.Value,
            0,
            pending.SourceBorrow.Length);
        var destinationRange = new PlatformDsc1RegionRange(
            pending.DestinationMapping.Value,
            0,
            pending.Destination.Length);

        var submission = _kernel.SubmitPlatformDsc1Copy(
            _service,
            _platformDomain,
            _platformComputeCapability,
            pending.StagingSource,
            sourceRange,
            pending.Destination,
            destinationRange);
        if (!submission.IsSuccess)
            return KernelResult.Fail(submission.Error, submission.Message!);

        pending.Submission = submission.Value!;
        return KernelResult.Ok();
    }

    private KernelResult AdvanceCleanup(PendingCopy pending)
    {
        var sourceMapping = AdvanceMappingClosure(
            pending,
            source: true);
        if (!sourceMapping.IsSuccess)
            return sourceMapping;

        var destinationMapping = AdvanceMappingClosure(
            pending,
            source: false);
        if (!destinationMapping.IsSuccess)
            return destinationMapping;

        if (pending.StagingMappingCapability is { } sourceCapability &&
            !pending.StagingCapabilityRevoked)
        {
            var revoked = _kernel.RevokeCapability(sourceCapability);
            if (!revoked.IsSuccess) return revoked;
            pending.StagingCapabilityRevoked = true;
        }

        if (pending.DestinationMappingCapability is { } destinationCapability &&
            !pending.DestinationCapabilityRevoked)
        {
            var revoked = _kernel.RevokeCapability(destinationCapability);
            if (!revoked.IsSuccess) return revoked;
            pending.DestinationCapabilityRevoked = true;
        }

        if (pending.StagingSource is { } staging &&
            !pending.StagingReleased)
        {
            var released = _kernel.ReleaseRegion(_service, staging);
            if (!released.IsSuccess) return released;
            pending.StagingReleased = true;
        }

        if (!pending.SourceBorrowReturned)
        {
            var returned = _kernel.ReturnBorrow(
                _service,
                pending.SourceBorrow.Handle);
            if (!returned.IsSuccess) return returned;
            pending.SourceBorrowReturned = true;
        }

        var publish = pending.AbortError is null &&
                      pending.Outcome == PlatformDsc1CopyOutcome.Completed &&
                      !pending.CancellationRequested;

        if (!pending.ResponseFinalized)
        {
            var response = FinalizeResponse(
                pending.RequestSequence,
                pending.Invocation,
                publish,
                publish ? pending.Destination : null);
            if (!response.IsSuccess)
                return KernelResult.Fail(response.Error, response.Message!);

            pending.ResponseFinalized = true;
        }

        if (!publish && !pending.DestinationReleased)
        {
            var released = _kernel.ReleaseRegion(
                _service,
                pending.Destination);
            if (!released.IsSuccess) return released;
            pending.DestinationReleased = true;
        }

        return KernelResult.Ok();
    }

    private KernelResult AdvanceMappingClosure(
        PendingCopy pending,
        bool source)
    {
        var mapping = source
            ? pending.StagingMapping
            : pending.DestinationMapping;
        if (mapping is null)
            return KernelResult.Ok();

        var closed = source
            ? pending.StagingMappingClosed
            : pending.DestinationMappingClosed;
        if (closed) return KernelResult.Ok();

        var started = source
            ? pending.StagingMappingCloseStarted
            : pending.DestinationMappingCloseStarted;

        KernelResult closure;
        if (!started)
        {
            if (source) pending.StagingMappingCloseStarted = true;
            else pending.DestinationMappingCloseStarted = true;
            closure = _kernel.RevokePlatformRegionMapping(
                _service,
                mapping.Value);
        }
        else
        {
            closure = _kernel.ObservePlatformRegionMappingRevocation(
                _service,
                mapping.Value);
        }

        if (!closure.IsSuccess) return closure;

        if (source) pending.StagingMappingClosed = true;
        else pending.DestinationMappingClosed = true;
        return KernelResult.Ok();
    }
}
