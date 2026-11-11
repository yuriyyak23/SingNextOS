using SingPlus.Contracts;
using SingPlus.Sip;
using SingPlus.Sip.Gui;

namespace SingPlus.Runtime;

/// <summary>CPU/model compositor. It copies pixels for validation and owns no display or GPU device authority.</summary>
public sealed class RuntimeCompositorServiceHost
{
    private sealed class SurfaceRecord
    {
        public SurfaceRecord? OriginalSelf { get; set; }
        public required SurfaceHandle Handle { get; init; }
        public required ProcessHandle Owner { get; init; }
        public required RegionId Region { get; init; }
        public required RegionGeneration RegisteredRegionGeneration { get; set; }
        public required SurfaceMetadata Metadata { get; init; }
        public SurfaceLifecycleState State { get; set; } = SurfaceLifecycleState.Writable;
    }

    private sealed class AcceptedPresent(
        EndpointSessionInvocationHandle invocation,
        SurfaceRecord surface,
        PresentTransferMode mode,
        object payload,
        RegionGeneration presentedGeneration,
        PresentOperationIdentity operation)
    {
        public EndpointSessionInvocationHandle Invocation { get; }=invocation;
        public SurfaceRecord Surface { get; }=surface;
        public PresentTransferMode Mode { get; }=mode;
        public object Payload { get; }=payload;
        public RegionGeneration PresentedGeneration { get; }=presentedGeneration;
        public PresentOperationIdentity Operation { get; }=operation;
        public AcceptedPresent? OriginalSelf { get; set; }
        // Correlates this exact successful RegionAuthority return, never permission
        // or proof that the independent response publication has completed.
        public bool BorrowReturned { get; set; }
        public EndpointSessionInvocationRegistry.OriginalPublication? OwnershipPublication { get; set; }
    }

    private readonly RuntimeKernel _kernel;
    private readonly object _dispatchDrainGate = new();
    // Host interlocks only; existing kernel owners retain permission and effects.
    private bool _operationInProgress;
    private bool _operationFaultPinned;
    private readonly ProcessHandle _service;
    private readonly EndpointSessionHandle _session;
    private readonly ProcessHandle _caller;
    private readonly Dictionary<RegionId, SurfaceRecord> _surfaces = [];
    private AcceptedPresent? _accepted;
    private (SurfaceRecord Surface, PresentTransferMode Mode, PresentOperationIdentity Operation)? _prepared;
    private ulong _nextSurface = 1;
    private ulong _nextOperation = 1;
    private bool _draining;

    private RuntimeCompositorServiceHost(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session, ProcessHandle caller)
    {
        _kernel = kernel;
        _service = service;
        _session = session;
        _caller = caller;
    }

    public static KernelResult<RuntimeCompositorServiceHost> CreateForSession(RuntimeKernel kernel, ProcessHandle service, EndpointSessionHandle session)
    {
        KernelResult<EndpointSessionRegistry.Record> resolved = kernel.EndpointSessions.ResolveForService(session, service);
        if (!resolved.IsSuccess) return KernelResult<RuntimeCompositorServiceHost>.Fail(resolved.Error, resolved.Message!);
        RuntimeCompositorServiceHost host = new RuntimeCompositorServiceHost(kernel, service, session, resolved.Value!.Caller);
        kernel.RegisterNativeServiceDrain(service, session, host.Drain);
        return KernelResult<RuntimeCompositorServiceHost>.Ok(host);
    }

    public KernelResult ProcessNext() => RunOperation(() =>
    {
        KernelResult accepted = AcceptNextCore();
        return accepted.IsSuccess && _accepted is not null ? CompleteAcceptedCore() : accepted;
    }, acceptNewWork: true);

    public KernelResult AcceptNext() => RunOperation(AcceptNextCore, acceptNewWork: true);

    private KernelResult AcceptNextCore()
    {
        if (_draining) return KernelResult.Fail(KernelError.SessionDraining, "Compositor session is draining.");
        if (_accepted is not null) return KernelResult.Fail(KernelError.PlatformBindingActive, "A present is already accepted.");
        KernelResult<EndpointSessionRequestEnvelope> received = _kernel.ReceiveSessionRequest(_service, _session);
        if (!received.IsSuccess) return KernelResult.Fail(received.Error, received.Message!);
        EndpointSessionInvocationHandle invocation = received.Value!.Invocation;
        if (received.Value.CancellationRequested)
        {
            _ = _kernel.AcceptSessionCancellation(_service, invocation);
            _ = _kernel.CancelSessionResponse(_service, invocation);
            return KernelResult.Fail(KernelError.SessionClosed, "Cancelled before compositor acceptance.");
        }
        // Host operations are serialized. Refuse exhausted allocation before
        // service acceptance so cancellation is an exact pre-effect outcome.
        if (received.Value.Request.MessageId == ICompositorServiceProtocol.Message_RegisterSurfaceAsync && _nextSurface == 0 ||
            received.Value.Request.MessageId == ICompositorServiceProtocol.Message_PreparePresentAsync && _nextOperation == 0)
            return Reject(invocation, KernelError.CapacityExhausted, "Compositor identity space is exhausted.");
        KernelResult serviceAccepted = _kernel.AcceptSessionInvocation(_service, invocation, allowInFlightCancellation: true);
        if (!serviceAccepted.IsSuccess) return serviceAccepted;

        if (received.Value.Request.MessageId == ICompositorServiceProtocol.Message_RegisterSurfaceAsync)
            return Register(invocation, received.Value.Request.Payload);
        if (received.Value.Request.MessageId == ICompositorServiceProtocol.Message_PreparePresentAsync)
            return Prepare(invocation, received.Value.Request.Payload);

        PresentTransferMode mode = received.Value.Request.MessageId switch
        {
            ICompositorServiceProtocol.Message_PresentMoveAsync => PresentTransferMode.Move,
            ICompositorServiceProtocol.Message_PresentReadLeaseAsync => PresentTransferMode.ReadLease,
            _ => (PresentTransferMode)byte.MaxValue,
        };
        if (!Enum.IsDefined(mode)) return Reject(invocation, KernelError.InvalidMessage, "Unknown compositor message.");

        RegionHandle presented;
        object payload;
        if (mode == PresentTransferMode.Move && received.Value.Request.Payload is OwnedBuffer<byte> moved)
        {
            presented = moved.Handle;
            payload = moved;
        }
        else if (mode == PresentTransferMode.ReadLease && received.Value.Request.Payload is BorrowLease<byte> borrowed)
        {
            presented = borrowed.Handle.Region;
            payload = borrowed;
        }
        else return Reject(invocation, KernelError.UnsupportedPayload, "Present payload does not match its declared ownership mode.");

        if (!_surfaces.TryGetValue(presented.RegionId, out SurfaceRecord? surface))
            return Reject(invocation, KernelError.RegionNotFound, "The buffer is not registered as a surface in this session.");
        if (_prepared is not { } prepared || !ReferenceEquals(prepared.Surface, surface) || prepared.Mode != mode)
            return Reject(invocation, KernelError.InvalidProtocolTransition, "Present was not prepared for this exact surface identity, generation, and mode.");
        if (!OwnsOriginalSurface(surface) || surface.State != SurfaceLifecycleState.Writable)
            return Reject(invocation, KernelError.WrongSessionOwner, "Surface owner/session/state binding does not match.");
        ulong expected = mode == PresentTransferMode.Move
            ? surface.RegisteredRegionGeneration.Value + 1
            : surface.RegisteredRegionGeneration.Value;
        if (presented.Generation.Value != expected)
            return Reject(invocation, KernelError.StaleGeneration, "Presented buffer generation does not match the registered surface generation.");

        lock (_dispatchDrainGate)
        {
            surface.State = SurfaceLifecycleState.Presented;
            _prepared = null;
            AcceptedPresent accepted = new AcceptedPresent(invocation, surface, mode, payload, presented.Generation, prepared.Operation);
            accepted.OriginalSelf=accepted;
            _accepted = accepted;
        }
        return KernelResult.Ok();
    }

    public KernelResult CompleteAccepted() => RunOperation(CompleteAcceptedCore, acceptNewWork: false);

    private KernelResult CompleteAcceptedCore()
    {
        if (_accepted is not { } work) return KernelResult.Fail(KernelError.PlatformBindingNotFound, "No present is accepted.");
        lock(_dispatchDrainGate)
            if(!OwnsOriginalAccepted(work))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original accepted presentation/surface continuity changed; cleanup remains pinned.");
        if(work.OwnershipPublication is { } original)
        {
            KernelResult known =_kernel.ReadOriginalSessionPublication(original);
            return known.IsSuccess ? FinishOwnedPresent(work,returned:true) : CleanupFailedOwnedPresent(work);
        }
        // The CPU/model provider consumes a bounded snapshot. This is intentionally a copy, never scanout.
        if (work.Mode != PresentTransferMode.ReadLease || !work.BorrowReturned)
        {
            byte[] bytes = work.Payload switch
            {
                OwnedBuffer<byte> moved => moved.Span.ToArray(),
                BorrowLease<byte> borrowed => borrowed.Span.ToArray(),
                _ => throw new InvalidOperationException(),
            };
            if (bytes.Length != work.Surface.Metadata.Stride * work.Surface.Metadata.Height)
                return Quarantine(work, "Surface byte extent changed after registration.");
        }

        if (work.Mode == PresentTransferMode.ReadLease)
        {
            if (!work.BorrowReturned)
            {
                KernelResult returned = _kernel.ReturnBorrow(_service, ((BorrowLease<byte>)work.Payload).Handle);
                if (!returned.IsSuccess) return Quarantine(work, returned.Message ?? "Borrow release was ambiguous.");
                lock (_dispatchDrainGate) work.BorrowReturned = true;
            }
            ReleaseFence fence = new ReleaseFence(work.Operation, work.Surface.Handle, work.PresentedGeneration, true);
            KernelResult<ResponseEnvelope> published = _kernel.PublishSessionResponse(_service, work.Invocation, new ReleaseFenceResponse(fence));
            if (!published.IsSuccess) return Quarantine(work, published.Message ?? "Release publication failed.");
        }
        else
        {
            KernelResult<EndpointSessionInvocationRegistry.OriginalPublication> captured =_kernel.CaptureOriginalSessionPublication(work.Invocation,_service,work.Payload);
            if(!captured.IsSuccess)return KernelResult.Fail(captured.Error,captured.Message!);
            work.OwnershipPublication=captured.Value; // Original correlation before possible return transfer.
            KernelResult<ResponseEnvelope> published = _kernel.PublishSessionResponse(_service, work.Invocation, work.Payload);
            if (!published.IsSuccess) return CleanupFailedOwnedPresent(work);
            return FinishOwnedPresent(work,returned:true);
        }
        lock (_dispatchDrainGate)
        {
            work.Surface.State = SurfaceLifecycleState.Writable;
            _accepted = null;
        }
        return KernelResult.Ok();
    }

    private bool OwnsOriginalSurface(SurfaceRecord surface) =>
        ReferenceEquals(surface.OriginalSelf,surface) && surface.Owner==_caller && surface.Handle.Session==_session &&
        _surfaces.TryGetValue(surface.Region,out SurfaceRecord? routed) && ReferenceEquals(routed,surface);

    private bool OwnsOriginalAccepted(AcceptedPresent work) =>
        ReferenceEquals(_accepted,work) && ReferenceEquals(work.OriginalSelf,work) &&
        OwnsOriginalSurface(work.Surface) && work.Invocation.Session==_session &&
        work.Surface.RegisteredRegionGeneration.Value + (work.Mode==PresentTransferMode.Move ? 1UL : 0UL)==work.PresentedGeneration.Value;

    private KernelResult CleanupFailedOwnedPresent(AcceptedPresent work)
    {
        // The exact original input reference identifies the producer; its consumed
        // Span/handle projection is never re-read or used for compensation.
        KernelResult<ResponseEnvelope> cleaned =_kernel.CleanupFailedSessionOwnershipResponse(_service,work.Invocation,work.Payload,out RegionHandle region);
        if(!cleaned.IsSuccess)return Quarantine(work,cleaned.Message ?? "Original ownership return remains pinned.");
        return FinishOwnedPresent(work,returned:false,region.Generation);
    }

    private KernelResult FinishOwnedPresent(AcceptedPresent work,bool returned,RegionGeneration? closedGeneration=null)
    {
        lock(_dispatchDrainGate)
        {
            // Kernel completion can emit observations. Revalidate local original
            // identity/generation before changing the surface/publication lifetime.
            if(!OwnsOriginalAccepted(work))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original presentation changed during ownership completion; host remains pinned.");
            work.Surface.RegisteredRegionGeneration=closedGeneration ?? new RegionGeneration(work.PresentedGeneration.Value+1);
            work.Surface.State=returned ? SurfaceLifecycleState.Writable : SurfaceLifecycleState.Closed;
            _accepted=null;
            _operationFaultPinned=false;
        }
        return returned ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformFaulted,
            "Original ownership return closed without publication after failed quantitative composition.");
    }

    public KernelResult Drain()
    {
        lock (_dispatchDrainGate)
        {
            _draining = true;
            if (_operationInProgress)
                return KernelResult.Fail(KernelError.SessionDraining, "Compositor dispatch or completion has not settled.");
            if (_operationFaultPinned)
                return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Compositor operation outcome is unknown; closure remains pinned.");
            if (_accepted is { } accepted)
            {
                if(OwnsOriginalAccepted(accepted))accepted.Surface.State = SurfaceLifecycleState.Quarantined;
                return KernelResult.Fail(KernelError.PlatformFaulted, "Accepted compositor work has ambiguous consumption and remains pinned.");
            }
            if(_surfaces.Values.Any(surface=>!OwnsOriginalSurface(surface)))
                return KernelResult.Fail(KernelError.PlatformFaulted,"Original surface routing changed; drain remains pinned.");
            foreach (SurfaceRecord surface in _surfaces.Values) surface.State = SurfaceLifecycleState.Closed;
            return KernelResult.Ok();
        }
    }

    private KernelResult RunOperation(Func<KernelResult> operation, bool acceptNewWork)
    {
        lock (_dispatchDrainGate)
        {
            if (_operationInProgress)
                return KernelResult.Fail(KernelError.SessionDraining, "Compositor operation is already in progress.");
            if (_operationFaultPinned && (acceptNewWork || _accepted?.OwnershipPublication is null))
                return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Compositor operation outcome is unknown; do not repeat it.");
            if (acceptNewWork && _draining)
                return KernelResult.Fail(KernelError.SessionDraining, "Compositor session is draining.");
            _operationInProgress = true;
        }
        bool returned = false;
        try
        {
            KernelResult result = operation();
            returned = true;
            return result;
        }
        finally
        {
            lock (_dispatchDrainGate)
            {
                _operationInProgress = false;
                if (!returned) { _operationFaultPinned = true; _draining = true; }
            }
        }
    }

    public SurfaceLifecycleState GetSurfaceState(SurfaceHandle handle)
    {
        lock (_dispatchDrainGate)
            return _surfaces.Values.FirstOrDefault(record => record.Handle == handle)?.State ?? SurfaceLifecycleState.Closed;
    }

    private KernelResult Register(EndpointSessionInvocationHandle invocation, object? payload)
    {
        if (payload is not RegisterSurfaceRequest request)
            return Reject(invocation, KernelError.UnsupportedPayload, "Malformed surface registration.");
        KernelResult capability = NativeServiceDispatch.ValidateExact(_kernel, _caller, request.PresentCapability, ResourceKind.Compositor, CapabilityResourceIds.CompositorPresent, CapabilityRights.Read | CapabilityRights.Write);
        if (!capability.IsSuccess) return Reject(invocation, capability.Error, capability.Message!);
        KernelResult<SingProcess> process = _kernel.Processes.Resolve(_caller);
        if (!process.IsSuccess) return Reject(invocation, process.Error, process.Message!);
        KernelResult<RegionDescriptor> region = _kernel.Regions.Validate(request.Buffer, new RegionOwner(process.Value!.DomainId, _caller.Generation));
        if (!region.IsSuccess) return Reject(invocation, region.Error, region.Message!);
        KernelResult<SurfaceMetadata> metadata = ValidateMetadata(request.Metadata, region.Value!.ByteLength);
        if (!metadata.IsSuccess) return Reject(invocation, metadata.Error, metadata.Message!);
        if (_surfaces.ContainsKey(request.Buffer.RegionId))
            return Reject(invocation, KernelError.DuplicateIdentity, "The exact region is already registered in this compositor session.");
        SurfaceHandle handle = new SurfaceHandle(_session, new SurfaceIdentity(_nextSurface), new SurfaceGeneration(1));
        SurfaceRecord record = new SurfaceRecord
        {
            Handle = handle,
            Owner = _caller,
            Region = request.Buffer.RegionId,
            RegisteredRegionGeneration = request.Buffer.Generation,
            Metadata = metadata.Value!,
        };
        record.OriginalSelf=record;
        lock (_dispatchDrainGate)
        {
            _surfaces.EnsureCapacity(_surfaces.Count + 1);
            _nextSurface = _nextSurface == ulong.MaxValue ? 0 : _nextSurface + 1;
            _surfaces.Add(request.Buffer.RegionId, record);
        }
        KernelResult<ResponseEnvelope> published = _kernel.PublishSessionResponse(_service, invocation, new SurfaceAuthorityResponse(new SurfaceAuthority(handle, request.PresentCapability)));
        return published.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(published.Error, published.Message!);
    }

    private KernelResult Prepare(EndpointSessionInvocationHandle invocation, object? payload)
    {
        if (payload is not PreparePresentRequest request || !Enum.IsDefined(request.Mode))
            return Reject(invocation, KernelError.UnsupportedPayload, "Malformed present preparation.");
        if (_prepared is not null)
            return Reject(invocation, KernelError.PlatformBindingActive, "Another exact present is already prepared.");
        SurfaceRecord? surface = _surfaces.Values.FirstOrDefault(record => record.Handle == request.Surface);
        if (surface is null)
            return Reject(invocation, KernelError.StaleHandle, "Surface identity/generation/session is unknown or stale.");
        if (!OwnsOriginalSurface(surface) || surface.State != SurfaceLifecycleState.Writable)
            return Reject(invocation, KernelError.WrongSessionOwner, "Surface is not writable authority of this caller/session.");
        lock (_dispatchDrainGate)
        {
            PresentOperationIdentity operation = new PresentOperationIdentity(_nextOperation);
            _nextOperation = _nextOperation == ulong.MaxValue ? 0 : _nextOperation + 1;
            _prepared = (surface, request.Mode, operation);
        }
        KernelResult<ResponseEnvelope> published = _kernel.PublishSessionResponse(_service, invocation);
        return published.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(published.Error, published.Message!);
    }

    private static KernelResult<SurfaceMetadata> ValidateMetadata(SurfaceMetadata metadata, long bytes)
    {
        if (metadata.ProducerGeneration == 0 || !Enum.IsDefined(metadata.Format) || metadata.Layout != SurfaceLayout.Linear || metadata.Width <= 0 || metadata.Height <= 0 || metadata.Stride <= 0)
            return KernelResult<SurfaceMetadata>.Fail(KernelError.UnsupportedPayload, "Surface semantic metadata is malformed.");
        if (metadata.Width > 8192 || metadata.Height > 8192 || metadata.Stride != checked(metadata.Width * 4) || checked((long)metadata.Stride * metadata.Height) != bytes)
            return KernelResult<SurfaceMetadata>.Fail(KernelError.UnsupportedPayload, "Surface extent/stride does not exactly match its region.");
        if (metadata.Planes.Count != 1 || metadata.Planes[0] != new SurfacePlaneSlice(0, checked((int)bytes), metadata.Stride))
            return KernelResult<SurfaceMetadata>.Fail(KernelError.UnsupportedPayload, "The CPU compositor requires one exact bounded linear plane.");
        return KernelResult<SurfaceMetadata>.Ok(metadata);
    }

    private KernelResult Reject(EndpointSessionInvocationHandle invocation, KernelError error, string message)
    {
        _ = _kernel.CancelSessionResponse(_service, invocation);
        return KernelResult.Fail(error, message);
    }

    private KernelResult Quarantine(AcceptedPresent work, string message)
    {
        lock (_dispatchDrainGate)
            if(OwnsOriginalAccepted(work))work.Surface.State = SurfaceLifecycleState.Quarantined;
        return KernelResult.Fail(KernelError.PlatformFaulted, message);
    }
}
