using SingPlus.Contracts;
using SingPlus.Sip;
using SingPlus.Sip.Gui;

namespace SingPlus.Runtime;

/// <summary>CPU/model compositor. It copies pixels for validation and owns no display or GPU device authority.</summary>
public sealed class RuntimeCompositorServiceHost
{
    private sealed class SurfaceRecord
    {
        public required SurfaceHandle Handle { get; init; }
        public required ProcessHandle Owner { get; init; }
        public required RegionId Region { get; init; }
        public required RegionGeneration RegisteredRegionGeneration { get; set; }
        public required SurfaceMetadata Metadata { get; init; }
        public SurfaceLifecycleState State { get; set; } = SurfaceLifecycleState.Writable;
    }

    private sealed record AcceptedPresent(
        EndpointSessionInvocationHandle Invocation,
        SurfaceRecord Surface,
        PresentTransferMode Mode,
        object Payload,
        RegionGeneration PresentedGeneration,
        PresentOperationIdentity Operation);

    private readonly RuntimeKernel _kernel;
    private readonly ProcessHandle _service;
    private readonly EndpointSessionHandle _session;
    private readonly ProcessHandle _caller;
    private readonly Dictionary<RegionId, SurfaceRecord> _surfaces = [];
    private AcceptedPresent? _accepted;
    private (SurfaceRecord Surface, PresentTransferMode Mode)? _prepared;
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
        var resolved = kernel.EndpointSessions.ResolveForService(session, service);
        if (!resolved.IsSuccess) return KernelResult<RuntimeCompositorServiceHost>.Fail(resolved.Error, resolved.Message!);
        var host = new RuntimeCompositorServiceHost(kernel, service, session, resolved.Value!.Caller);
        kernel.RegisterNativeServiceDrain(service, session, host.Drain);
        return KernelResult<RuntimeCompositorServiceHost>.Ok(host);
    }

    public KernelResult ProcessNext()
    {
        var accepted = AcceptNext();
        return accepted.IsSuccess && _accepted is not null ? CompleteAccepted() : accepted;
    }

    public KernelResult AcceptNext()
    {
        if (_draining) return KernelResult.Fail(KernelError.SessionDraining, "Compositor session is draining.");
        if (_accepted is not null) return KernelResult.Fail(KernelError.PlatformBindingActive, "A present is already accepted.");
        var received = _kernel.ReceiveSessionRequest(_service, _session);
        if (!received.IsSuccess) return KernelResult.Fail(received.Error, received.Message!);
        var invocation = received.Value!.Invocation;
        if (received.Value.CancellationRequested)
        {
            _ = _kernel.AcceptSessionCancellation(_service, invocation);
            _ = _kernel.CancelSessionResponse(_service, invocation);
            return KernelResult.Fail(KernelError.SessionClosed, "Cancelled before compositor acceptance.");
        }
        var serviceAccepted = _kernel.AcceptSessionInvocation(_service, invocation, allowInFlightCancellation: true);
        if (!serviceAccepted.IsSuccess) return serviceAccepted;

        if (received.Value.Request.MessageId == ICompositorServiceProtocol.Message_RegisterSurfaceAsync)
            return Register(invocation, received.Value.Request.Payload);
        if (received.Value.Request.MessageId == ICompositorServiceProtocol.Message_PreparePresentAsync)
            return Prepare(invocation, received.Value.Request.Payload);

        var mode = received.Value.Request.MessageId switch
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

        if (!_surfaces.TryGetValue(presented.RegionId, out var surface))
            return Reject(invocation, KernelError.RegionNotFound, "The buffer is not registered as a surface in this session.");
        if (_prepared is not { } prepared || !ReferenceEquals(prepared.Surface, surface) || prepared.Mode != mode)
            return Reject(invocation, KernelError.InvalidProtocolTransition, "Present was not prepared for this exact surface identity, generation, and mode.");
        if (surface.Owner != _caller || surface.Handle.Session != _session || surface.State != SurfaceLifecycleState.Writable)
            return Reject(invocation, KernelError.WrongSessionOwner, "Surface owner/session/state binding does not match.");
        var expected = mode == PresentTransferMode.Move
            ? surface.RegisteredRegionGeneration.Value + 1
            : surface.RegisteredRegionGeneration.Value;
        if (presented.Generation.Value != expected)
            return Reject(invocation, KernelError.StaleGeneration, "Presented buffer generation does not match the registered surface generation.");

        surface.State = SurfaceLifecycleState.Presented;
        _prepared = null;
        _accepted = new AcceptedPresent(invocation, surface, mode, payload, presented.Generation, new PresentOperationIdentity(_nextOperation++));
        return KernelResult.Ok();
    }

    public KernelResult CompleteAccepted()
    {
        if (_accepted is not { } work) return KernelResult.Fail(KernelError.PlatformBindingNotFound, "No present is accepted.");
        // The CPU/model provider consumes a bounded snapshot. This is intentionally a copy, never scanout.
        var bytes = work.Payload switch
        {
            OwnedBuffer<byte> moved => moved.Span.ToArray(),
            BorrowLease<byte> borrowed => borrowed.Span.ToArray(),
            _ => throw new InvalidOperationException(),
        };
        if (bytes.Length != work.Surface.Metadata.Stride * work.Surface.Metadata.Height)
            return Quarantine(work, "Surface byte extent changed after registration.");

        if (work.Mode == PresentTransferMode.ReadLease)
        {
            var returned = _kernel.ReturnBorrow(_service, ((BorrowLease<byte>)work.Payload).Handle);
            if (!returned.IsSuccess) return Quarantine(work, returned.Message ?? "Borrow release was ambiguous.");
            var fence = new ReleaseFence(work.Operation, work.Surface.Handle, work.PresentedGeneration, true);
            var published = _kernel.PublishSessionResponse(_service, work.Invocation, new ReleaseFenceResponse(fence));
            if (!published.IsSuccess) return Quarantine(work, published.Message ?? "Release publication failed.");
        }
        else
        {
            var published = _kernel.PublishSessionResponse(_service, work.Invocation, work.Payload);
            if (!published.IsSuccess) return Quarantine(work, published.Message ?? "Ownership return failed.");
            work.Surface.RegisteredRegionGeneration = new RegionGeneration(work.PresentedGeneration.Value + 1);
        }
        work.Surface.State = SurfaceLifecycleState.Writable;
        _accepted = null;
        return KernelResult.Ok();
    }

    public KernelResult Drain()
    {
        _draining = true;
        if (_accepted is { } accepted)
        {
            accepted.Surface.State = SurfaceLifecycleState.Quarantined;
            return KernelResult.Fail(KernelError.PlatformFaulted, "Accepted compositor work has ambiguous consumption and remains pinned.");
        }
        foreach (var surface in _surfaces.Values) surface.State = SurfaceLifecycleState.Closed;
        return KernelResult.Ok();
    }

    public SurfaceLifecycleState GetSurfaceState(SurfaceHandle handle) =>
        _surfaces.Values.FirstOrDefault(record => record.Handle == handle)?.State ?? SurfaceLifecycleState.Closed;

    private KernelResult Register(EndpointSessionInvocationHandle invocation, object? payload)
    {
        if (payload is not RegisterSurfaceRequest request)
            return Reject(invocation, KernelError.UnsupportedPayload, "Malformed surface registration.");
        var capability = NativeServiceDispatch.ValidateExact(_kernel, _caller, request.PresentCapability, ResourceKind.Compositor, CapabilityResourceIds.CompositorPresent, CapabilityRights.Read | CapabilityRights.Write);
        if (!capability.IsSuccess) return Reject(invocation, capability.Error, capability.Message!);
        var process = _kernel.Processes.Resolve(_caller);
        if (!process.IsSuccess) return Reject(invocation, process.Error, process.Message!);
        var region = _kernel.Regions.Validate(request.Buffer, new RegionOwner(process.Value!.DomainId, _caller.Generation));
        if (!region.IsSuccess) return Reject(invocation, region.Error, region.Message!);
        var metadata = ValidateMetadata(request.Metadata, region.Value!.ByteLength);
        if (!metadata.IsSuccess) return Reject(invocation, metadata.Error, metadata.Message!);
        if (_surfaces.ContainsKey(request.Buffer.RegionId))
            return Reject(invocation, KernelError.DuplicateIdentity, "The exact region is already registered in this compositor session.");
        var handle = new SurfaceHandle(_session, new SurfaceIdentity(_nextSurface++), new SurfaceGeneration(1));
        _surfaces.Add(request.Buffer.RegionId, new SurfaceRecord
        {
            Handle = handle,
            Owner = _caller,
            Region = request.Buffer.RegionId,
            RegisteredRegionGeneration = request.Buffer.Generation,
            Metadata = metadata.Value!,
        });
        var published = _kernel.PublishSessionResponse(_service, invocation, new SurfaceAuthorityResponse(new SurfaceAuthority(handle, request.PresentCapability)));
        return published.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(published.Error, published.Message!);
    }

    private KernelResult Prepare(EndpointSessionInvocationHandle invocation, object? payload)
    {
        if (payload is not PreparePresentRequest request || !Enum.IsDefined(request.Mode))
            return Reject(invocation, KernelError.UnsupportedPayload, "Malformed present preparation.");
        if (_prepared is not null)
            return Reject(invocation, KernelError.PlatformBindingActive, "Another exact present is already prepared.");
        var surface = _surfaces.Values.FirstOrDefault(record => record.Handle == request.Surface);
        if (surface is null)
            return Reject(invocation, KernelError.StaleHandle, "Surface identity/generation/session is unknown or stale.");
        if (surface.Owner != _caller || surface.Handle.Session != _session || surface.State != SurfaceLifecycleState.Writable)
            return Reject(invocation, KernelError.WrongSessionOwner, "Surface is not writable authority of this caller/session.");
        _prepared = (surface, request.Mode);
        var published = _kernel.PublishSessionResponse(_service, invocation);
        return published.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(published.Error, published.Message!);
    }

    private static KernelResult<SurfaceMetadata> ValidateMetadata(SurfaceMetadata? metadata, long bytes)
    {
        if (metadata is null || metadata.ProducerGeneration == 0 || !Enum.IsDefined(metadata.Format) || metadata.Layout != SurfaceLayout.Linear || metadata.Width <= 0 || metadata.Height <= 0 || metadata.Stride <= 0)
            return KernelResult<SurfaceMetadata>.Fail(KernelError.UnsupportedPayload, "Surface semantic metadata is malformed.");
        if (metadata.Width > 8192 || metadata.Height > 8192 || metadata.Stride != checked(metadata.Width * 4) || checked((long)metadata.Stride * metadata.Height) != bytes)
            return KernelResult<SurfaceMetadata>.Fail(KernelError.UnsupportedPayload, "Surface extent/stride does not exactly match its region.");
        if (metadata.Planes is null || metadata.Planes.Count != 1 || metadata.Planes[0] != new SurfacePlaneSlice(0, checked((int)bytes), metadata.Stride))
            return KernelResult<SurfaceMetadata>.Fail(KernelError.UnsupportedPayload, "The CPU compositor requires one exact bounded linear plane.");
        return KernelResult<SurfaceMetadata>.Ok(metadata with { Planes = metadata.Planes.ToArray() });
    }

    private KernelResult Reject(EndpointSessionInvocationHandle invocation, KernelError error, string message)
    {
        _ = _kernel.CancelSessionResponse(_service, invocation);
        return KernelResult.Fail(error, message);
    }

    private KernelResult Quarantine(AcceptedPresent work, string message)
    {
        work.Surface.State = SurfaceLifecycleState.Quarantined;
        return KernelResult.Fail(KernelError.PlatformFaulted, message);
    }
}
