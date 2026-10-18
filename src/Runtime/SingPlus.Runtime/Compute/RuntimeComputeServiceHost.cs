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
/// Supports lifecycle-only ModelOnly reference Copy and the separate original
/// neutral software backing profile. Neither path is direct-borrow external
/// execution, zero-copy, coherent shared memory or executable HybridCPU evidence.
/// </summary>
public sealed class RuntimeComputeServiceHost
{
    private sealed record ReceivedCopyRequest(
        ChannelEnvelope Request,
        EndpointSessionInvocationHandle? Invocation,
        bool CancellationRequested);

    private sealed class PendingCopy
    {
        private readonly PendingCopy _original;
        private readonly RuntimeComputeServiceHost _host;
        private readonly BorrowLease<byte> _originalSourceBorrow;
        private readonly OwnedBuffer<byte> _originalDestination;
        private readonly object? _originalDestinationRegionOwner;
        private readonly RegionHandle _originalDestinationRegion;
        public PendingCopy(RuntimeComputeServiceHost host, ulong requestSequence, BorrowLease<byte> sourceBorrow,
            OwnedBuffer<byte> destination, EndpointSessionInvocationHandle? invocation)
        {
            _original=this;_host=host;RequestSequence=requestSequence;SourceBorrow=sourceBorrow;
            Destination=destination;Invocation=invocation;
            _originalSourceBorrow=sourceBorrow;_originalDestination=destination;
            _originalDestinationRegion=destination.Handle;
            _originalDestinationRegionOwner=host._kernel.Regions.CaptureOriginalAccountingOwner(destination.Handle);
        }
        public bool IsOriginalFor(RuntimeComputeServiceHost owner) => ReferenceEquals(_host, owner) && ReferenceEquals(_original, this) &&
            ReferenceEquals(SourceBorrow, _originalSourceBorrow) && ReferenceEquals(Destination, _originalDestination);
        public ulong RequestSequence { get; }
        public BorrowLease<byte> SourceBorrow { get; }
        public OwnedBuffer<byte> Destination { get; }
        public EndpointSessionInvocationHandle? Invocation { get; }

        public OwnedBuffer<byte>? StagingSource { get; set; }
        public CapabilityId? StagingMappingCapability { get; set; }
        public CapabilityId? DestinationMappingCapability { get; set; }
        public PlatformRegionMapping? StagingMapping { get; set; }
        public PlatformRegionMapping? DestinationMapping { get; set; }
        public PlatformDsc1CopySubmission? Submission { get; set; }
        private OwnedBuffer<byte>? _originalStaging;
        private object? _originalStagingRegionOwner;
        private RegionHandle? _originalStagingRegion;
        private CapabilityId? _originalSourceCapability, _originalDestinationCapability;
        private PlatformRegionMapping? _originalSourceMapping, _originalDestinationMapping;
        private PlatformDsc1CopySubmission? _originalSubmission;
        public bool HasOriginalSetup => ReferenceEquals(StagingSource, _originalStaging) &&
            StagingMappingCapability == _originalSourceCapability && DestinationMappingCapability == _originalDestinationCapability &&
            StagingMapping == _originalSourceMapping && DestinationMapping == _originalDestinationMapping && Submission == _originalSubmission;
        public void CaptureStaging(OwnedBuffer<byte> value)
        {
            _originalStaging=value;StagingSource=value;_originalStagingRegion=value.Handle;
            _originalStagingRegionOwner=_host._kernel.Regions.CaptureOriginalAccountingOwner(value.Handle);
        }
        public bool HasOriginalLocalClosures()
        {
            if (DestinationReleased && !HasReleasedDestination()) return false;
            if (StagingCapabilityRevoked && (_originalSourceCapability is not { } source || !_host._kernel.CapabilityAuthority.HasOriginalRevocation(source))) return false;
            if (DestinationCapabilityRevoked && (_originalDestinationCapability is not { } destination || !_host._kernel.CapabilityAuthority.HasOriginalRevocation(destination))) return false;
            if (StagingMappingClosed && !HasClosedMapping(_originalSourceMapping) || DestinationMappingClosed && !HasClosedMapping(_originalDestinationMapping)) return false;
            if (ResponseFinalized && (Invocation is { } invocation
                ? !_host._kernel.HasComputeCopyResponseSettlement(invocation,_host._service,this)
                : _responseReceipt is null)) return false;
            if (SourceBorrowReturned && !_host._kernel.Regions.HasEndedOriginalBorrow(_originalSourceBorrow)) return false;
            if (!StagingReleased) return true;
            if (_originalStaging is null || _originalStagingRegion is not { } handle || _originalStagingRegionOwner is null) return false;
            return _host._kernel.Regions.HasReleasedOriginalRegion(_originalStagingRegionOwner,handle);
        }
        private bool HasReleasedDestination()
        {
            if (_originalDestinationRegionOwner is null) return false;
            if (_host._kernel.Regions.HasReleasedOriginalRegion(_originalDestinationRegionOwner,_originalDestinationRegion)) return true;
            // Failed response transfer owns the next generation and proves its
            // independent cleanup through the original cancelled settlement.
            if (!ResponseFinalized || Invocation is not { } invocation ||
                !_host._kernel.HasComputeCopyResponseSettlement(invocation,_host._service,this,cancelledOnly:true)) return false;
            var ended=_host._kernel.Regions.HasEndedOriginalAccountingLifetime(_originalDestinationRegionOwner,_originalDestinationRegion);
            return ended.IsSuccess && ended.Value;
        }
        private bool HasClosedMapping(PlatformRegionMapping? mapping)
        {
            if (mapping is null) return false;
            var current=_host._kernel.QueryPlatformRegionMappingLifecycle(_host._service,mapping.Value);
            return current.IsSuccess && current.Value.LocalReclaimAllowed;
        }
        private ResponseEnvelope? _responseReceipt;
        public void CaptureResponse(ResponseEnvelope response) { _responseReceipt=response;ResponseFinalized=true; }
        public void CaptureSourceCapability(CapabilityId value) { _originalSourceCapability=value;StagingMappingCapability=value; }
        public void CaptureDestinationCapability(CapabilityId value) { _originalDestinationCapability=value;DestinationMappingCapability=value; }
        public void CaptureSourceMapping(PlatformRegionMapping value) { _originalSourceMapping=value;StagingMapping=value; }
        public void CaptureDestinationMapping(PlatformRegionMapping value) { _originalDestinationMapping=value;DestinationMapping=value; }
        public void CaptureSubmission(PlatformDsc1CopySubmission value)
        {
            if (_originalSubmission is { } original && original != value)
                throw new InvalidOperationException("Original compute submission cannot change.");
            if (_originalSubmission is null) { _originalSubmission=value;Submission=value; }
        }
        public PlatformDsc1CopyOutcome? Outcome { get; set; }
        // Retain the actual kernel consumption receipt, not a host-minted terminal flag.
        private PlatformDsc1CopyReceipt? _consumedReceipt;
        public bool HasOriginalOutcome => Outcome is null ? _consumedReceipt is null :
            _consumedReceipt is { } receipt && receipt.Submission == _originalSubmission && receipt.Outcome == Outcome;
        public void CaptureConsumedReceipt(PlatformDsc1CopyReceipt receipt)
        {
            if (receipt.Submission != _originalSubmission)
                throw new InvalidOperationException("Consumed compute receipt belongs to another original submission.");
            _consumedReceipt=receipt;Outcome=receipt.Outcome;
        }

        public KernelError? AbortError { get; set; }
        public string? AbortMessage { get; set; }
        public KernelResult? AdmissionRefusal { get; set; }
        public bool CancellationRequested { get; set; }
        public bool CancelledBeforeProviderInvocation { get; init; }

        public bool StagingMappingClosed { get; set; }
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
    private int _progressing;

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
    public KernelResult ProcessNextCopy() => Progress(ProcessNextCopyCore);

    private KernelResult ProcessNextCopyCore()
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

        var pending = new PendingCopy(this, request.Sequence, sourceBorrow, destination, invocation);
        _pending = pending;

        if (invocation is { } exactInvocation)
        {
            if (received.Value.CancellationRequested)
            {
                var acceptedCancellation = _kernel.AcceptSessionCancellation(_service, exactInvocation);
                if (!acceptedCancellation.IsSuccess) return RetainRefusal(acceptedCancellation);
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
                        if (!acceptedCancellation.IsSuccess) return RetainRefusal(acceptedCancellation);
                        return SettleDeliveredCancellation(request.Sequence, exactInvocation, sourceBorrow, destination);
                    }
                }
                return RetainRefusal(accepted);
            }

            var postAcceptanceCancellation = _kernel.QuerySessionCancellation(_service, exactInvocation);
            if (!postAcceptanceCancellation.IsSuccess)
                return RetainRefusal(KernelResult.Fail(postAcceptanceCancellation.Error, postAcceptanceCancellation.Message!));
            if (postAcceptanceCancellation.Value)
            {
                var acceptedCancellation = _kernel.AcceptSessionCancellation(_service, exactInvocation);
                if (!acceptedCancellation.IsSuccess) return RetainRefusal(acceptedCancellation);
                return SettleDeliveredCancellation(request.Sequence, exactInvocation, sourceBorrow, destination);
            }
        }

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

        return AdvancePendingCopyCore();

        KernelResult RetainRefusal(KernelResult refusal)
        {
            pending.AdmissionRefusal = refusal;
            return refusal;
        }
    }

    /// <summary>
    /// Advances provider observation or cleanup for the current request. A
    /// PlatformBindingDraining result means the exact request remains pending and
    /// neither the source borrow nor destination response authority has returned.
    /// </summary>
    public KernelResult AdvancePendingCopy() => Progress(AdvancePendingCopyCore);

    private KernelResult ValidatePending(PendingCopy pending)
    {
        if (!ReferenceEquals(_pending, pending) || !pending.IsOriginalFor(this) || !pending.HasOriginalSetup || !pending.HasOriginalLocalClosures() || !pending.HasOriginalOutcome)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original ComputeService pending owner changed; retain custody.");
        return pending.Invocation is { } invocation
            ? _kernel.ValidateComputeCopyInvocationOwner(invocation, _service, pending)
            : KernelResult.Ok();
    }

    private KernelResult AdvancePendingCopyCore()
    {
        var pending = _pending;
        if (pending is null)
        {
            return KernelResult.Fail(
                KernelError.InvalidTransition,
                "The ComputeService host has no pending Copy request.");
        }

        if (pending.AdmissionRefusal is { } refusal) return refusal;
        var custody = ValidatePending(pending);
        if (!custody.IsSuccess) return custody;
        if (pending.AbortError == KernelError.PlatformFaulted && pending.Submission is null)
        {
            return KernelResult.Fail(
                pending.AbortError.Value,
                pending.AbortMessage ??
                "The ComputeService request is fault-pinned by ambiguous platform state.");
        }

        var callerCancellation = ObserveCallerCancellation(pending);
        if (!callerCancellation.IsSuccess) return callerCancellation;

        if (pending.AbortError == KernelError.PlatformFaulted && pending.Outcome is null && pending.Submission is { } original)
        {
            var closed = _kernel.CancelPlatformDsc1Copy(_service, original);
            if (!closed.IsSuccess) return KernelResult.Fail(closed.Error, closed.Message!);
            pending.CaptureConsumedReceipt(closed.Value);
            custody = ValidatePending(pending); if (!custody.IsSuccess) return custody;
        }

        if (!pending.CancelledBeforeProviderInvocation && pending.AbortError is null && pending.Outcome is null)
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

            pending.CaptureConsumedReceipt(terminal.Value!);
            custody = ValidatePending(pending); if (!custody.IsSuccess) return custody;
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
    public KernelResult CancelPendingCopy() => Progress(CancelPendingCopyCore);

    private KernelResult CancelPendingCopyCore()
    {
        var pending = _pending;
        if (pending is null)
        {
            return KernelResult.Fail(
                KernelError.InvalidTransition,
                "The ComputeService host has no pending Copy request to cancel.");
        }

        if (pending.AdmissionRefusal is { } refusal) return refusal;
        if ((pending.AbortError == KernelError.PlatformFaulted && pending.Submission is null) ||
            !pending.CancelledBeforeProviderInvocation && pending.Submission is null && pending.AbortError is null)
        {
            return KernelResult.Fail(
                KernelError.PlatformFaulted,
                "The pending ComputeService request has no trustworthy exact DSC1 identity that can be cancelled.");
        }

        var custody=ValidatePending(pending); if(!custody.IsSuccess)return custody;
        pending.CancellationRequested = true;
        return AdvancePendingCopyCore();
    }

    private KernelResult Progress(Func<KernelResult> advance)
    {
        if (Interlocked.CompareExchange(ref _progressing, 1, 0) != 0)
            return KernelResult.Fail(KernelError.InvalidTransition,
                "The ComputeService host is already progressing its exact request.");
        try { return advance(); }
        finally { Volatile.Write(ref _progressing, 0); }
    }

    private KernelResult ObserveCallerCancellation(PendingCopy pending)
    {
        if (pending.Invocation is not { } invocation)
            return KernelResult.Ok();
        if(pending.CancellationRequested)
        {
            var closed=_kernel.ObserveComputeClosedResponse(invocation,_service,pending);
            return closed.IsSuccess || closed.Error==KernelError.InvalidTransition
                ? KernelResult.Ok() : KernelResult.Fail(closed.Error,closed.Message!);
        }

        var requested = _kernel.QuerySessionCancellation(_service, invocation);
        if(!requested.IsSuccess && requested.Error==KernelError.SessionClosed)
        {
            var closed=_kernel.ObserveComputeClosedResponse(invocation,_service,pending);
            if(!closed.IsSuccess)return KernelResult.Fail(closed.Error,closed.Message!);
            pending.CancellationRequested=true;
            return KernelResult.Ok();
        }
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
        _pending = new PendingCopy(this, requestSequence, sourceBorrow, destination, invocation)
        {
            CancellationRequested = true,
            CancelledBeforeProviderInvocation = true
        };
        if (invocation is { } originalInvocation)
        {
            var bound = _kernel.BindComputeCopyInvocation(originalInvocation, _service, _pending);
            if (!bound.IsSuccess) return bound;
        }
        return AdvancePendingCopyCore();
    }

    private KernelResult<ResponseEnvelope> FinalizeResponse(
        ulong requestSequence,
        EndpointSessionInvocationHandle? invocation,
        bool publish,
        object? payload)
    {
        if (invocation is { } exactInvocation)
        {
            if(!publish && _pending is { } pending)
            {
                var closed=_kernel.ObserveComputeClosedResponse(exactInvocation,_service,pending);
                if(closed.IsSuccess)return closed;
                if(closed.Error!=KernelError.InvalidTransition)return closed;
            }
            return publish
                ? _kernel.PublishSessionResponse(_service, exactInvocation, payload)
                : _kernel.CancelSessionResponse(_service, exactInvocation);
        }

        return publish
            ? _kernel.PublishResponse(_service, _endpoint, requestSequence, payload)
            : _kernel.CancelResponse(_service, _endpoint, requestSequence);
    }

    private KernelResult<PlatformRegionMapping> MapCopyBacking(ProcessHandle owner, PlatformDomainBinding binding,
        CapabilityId capability, RegionHandle region, PlatformMemoryAccess access)
    {
        if (!_kernel.PlatformAuthority.SupportsDsc1Backing)
            return _kernel.MapPlatformOwnedRegion(owner, binding, capability, region, access);
        var descriptor = _kernel.Regions.Validate(region, new(_serviceDomain, owner.Generation));
        if (!descriptor.IsSuccess) return KernelResult<PlatformRegionMapping>.Fail(descriptor.Error, descriptor.Message!);
        var mapped = _kernel.MapPlatformOwnedRegionSlice(owner, binding, capability, region, 0, descriptor.Value!.ByteLength, access);
        return mapped.IsSuccess ? KernelResult<PlatformRegionMapping>.Ok(mapped.Value.Mapping) :
            KernelResult<PlatformRegionMapping>.Fail(mapped.Error, mapped.Message!);
    }

    private KernelResult PrepareAndSubmit(PendingCopy pending)
    {
        // This path has not invoked DSC1 yet. A custody refusal is a local
        // setup failure, not an unknown native effect without a continuation.
        KernelResult FreshSetup()
        {
            var fresh=ValidatePending(pending);
            return fresh.IsSuccess ? fresh : KernelResult.Fail(KernelError.InvalidRegionState, fresh.Message!);
        }
        if (pending.Invocation is { } invocation)
        {
            var bound = _kernel.BindComputeCopyInvocation(invocation, _service, pending);
            if (!bound.IsSuccess) return bound;
        }
        var custody=FreshSetup(); if(!custody.IsSuccess)return custody;
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

        pending.CaptureStaging(staging.Value!);
        custody=FreshSetup(); if(!custody.IsSuccess)return custody;
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
        pending.CaptureSourceCapability(sourceCapability.Value!.CapabilityId);
        custody=FreshSetup(); if(!custody.IsSuccess)return custody;

        var destinationCapability = _kernel.MintCapability(
            _serviceDomain,
            _service,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(
                pending.Destination.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Write);
        if (!destinationCapability.IsSuccess)
            return KernelResult.Fail(destinationCapability.Error, destinationCapability.Message!);
        pending.CaptureDestinationCapability(destinationCapability.Value!.CapabilityId);
        custody=FreshSetup(); if(!custody.IsSuccess)return custody;

        var sourceMapping = MapCopyBacking(
            _service,
            _platformDomain,
            pending.StagingMappingCapability.Value,
            pending.StagingSource.Handle,
            PlatformMemoryAccess.Read);
        if (!sourceMapping.IsSuccess)
            return KernelResult.Fail(sourceMapping.Error, sourceMapping.Message!);
        pending.CaptureSourceMapping(sourceMapping.Value!);
        custody=FreshSetup(); if(!custody.IsSuccess)return custody;

        var destinationMapping = MapCopyBacking(
            _service,
            _platformDomain,
            pending.DestinationMappingCapability.Value,
            pending.Destination.Handle,
            PlatformMemoryAccess.Write);
        if (!destinationMapping.IsSuccess)
            return KernelResult.Fail(destinationMapping.Error, destinationMapping.Message!);
        pending.CaptureDestinationMapping(destinationMapping.Value!);
        custody=FreshSetup(); if(!custody.IsSuccess)return custody;

        var sourceRange = new PlatformDsc1RegionRange(
            pending.StagingMapping.Value,
            0,
            pending.SourceBorrow.Length);
        var destinationRange = new PlatformDsc1RegionRange(
            pending.DestinationMapping.Value,
            0,
            pending.Destination.Length);

        var submission = _kernel.SubmitComputeServiceDsc1Copy(
            _service,
            _platformDomain,
            _platformComputeCapability,
            pending.StagingSource,
            sourceRange,
            pending.Destination,
            destinationRange,
            original => pending.CaptureSubmission(original));
        if (!submission.IsSuccess)
            return KernelResult.Fail(submission.Error, submission.Message!);

        pending.CaptureSubmission(submission.Value!);
        return KernelResult.Ok();
    }

    private KernelResult AdvanceCleanup(PendingCopy pending)
    {
        var custody=ValidatePending(pending); if(!custody.IsSuccess)return custody;
        var sourceMapping = AdvanceMappingClosure(
            pending,
            source: true);
        if (!sourceMapping.IsSuccess)
            return sourceMapping;

        custody=ValidatePending(pending); if(!custody.IsSuccess)return custody;

        var destinationMapping = AdvanceMappingClosure(
            pending,
            source: false);
        if (!destinationMapping.IsSuccess)
            return destinationMapping;

        custody=ValidatePending(pending); if(!custody.IsSuccess)return custody;

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
            custody=ValidatePending(pending); if(!custody.IsSuccess)return custody;
            var revoked = _kernel.RevokeCapability(destinationCapability);
            if (!revoked.IsSuccess) return revoked;
            pending.DestinationCapabilityRevoked = true;
        }

        if (pending.StagingSource is { } staging &&
            !pending.StagingReleased)
        {
            custody=ValidatePending(pending); if(!custody.IsSuccess)return custody;
            var released = _kernel.ReleaseRegion(_service, staging);
            if (!released.IsSuccess) return released;
            pending.StagingReleased = true;
        }

        if (!pending.SourceBorrowReturned)
        {
            custody=ValidatePending(pending); if(!custody.IsSuccess)return custody;
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
            custody=ValidatePending(pending); if(!custody.IsSuccess)return custody;
            var response = FinalizeResponse(
                pending.RequestSequence,
                pending.Invocation,
                publish,
                publish ? pending.Destination : null);
            if (!response.IsSuccess)
            {
                if(!publish || pending.Invocation is not { } failedInvocation)
                    return KernelResult.Fail(response.Error,response.Message!);
                var cleanup=_kernel.CleanupFailedSessionOwnershipResponse(_service,failedInvocation,pending.Destination);
                if(!cleanup.IsSuccess)return KernelResult.Fail(response.Error,response.Message!);
                pending.AbortError=KernelError.PlatformFaulted;
                pending.AbortMessage="Original ownership response closed without publication after failed quantitative composition.";
                pending.DestinationReleased=true;
            }

            if(response.IsSuccess)pending.CaptureResponse(response.Value!);
            else pending.ResponseFinalized = true; // Failed publication cleanup established original session cancellation.
        }

        if (!publish && !pending.DestinationReleased)
        {
            custody=ValidatePending(pending); if(!custody.IsSuccess)return custody;
            var released = _kernel.ReleaseRegion(
                _service,
                pending.Destination);
            if (!released.IsSuccess) return released;
            pending.DestinationReleased = true;
        }

        if (pending.Invocation is { } invocation)
        {
            custody=ValidatePending(pending); if(!custody.IsSuccess)return custody;
            var contained = _kernel.ConsumeComputeCopyInvocationClosure(invocation, _service, pending);
            if (!contained.IsSuccess) return contained;
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

        var current = _kernel.QueryPlatformRegionMappingLifecycle(_service, mapping.Value);
        if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
        var closure = current.Value.PlatformClosure == PlatformExternalClosureState.Active
            ? _kernel.RevokePlatformRegionMapping(_service, mapping.Value)
            : _kernel.ObservePlatformRegionMappingRevocation(_service, mapping.Value);

        if (!closure.IsSuccess) return closure;

        if (source) pending.StagingMappingClosed = true;
        else pending.DestinationMappingClosed = true;
        return KernelResult.Ok();
    }
}
