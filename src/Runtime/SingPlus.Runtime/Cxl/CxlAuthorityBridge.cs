using System.Collections.Concurrent;
using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

/// <summary>
/// Joins existing Sing authority to narrow CXL provider bindings. Provider evidence never
/// substitutes for a live RegionUse or DeviceResourceSet-derived device lease.
/// </summary>
public sealed class CxlAuthorityBridge : ICxlTeardownParticipant
{
    internal sealed class OperationPin(CxlFabricBinding fabric, RegionOwner owner, object originalParent)
    {
        internal CxlFabricBinding Fabric { get; } = fabric;
        internal RegionOwner Owner { get; } = owner;
        internal object OriginalParent { get; } = originalParent;
    }
    private sealed record TrackedFabric(CxlFabricBinding Binding, RegionOwner Owner, ProcessHandle Process,
        CxlDeviceReservation DeviceReservation, RegionBackingLeaseHandle? BackingLease = null)
    {
        public int PendingChildren { get; set; }
        public bool ClosureInFlight { get; set; }
        public bool CreationInFlight { get; set; } = true;
        public bool Admitted { get; set; }
        // Successful provider reply, retained independently of late source/local cleanup.
        public CxlFabricBinding? ReleasedBinding { get; set; }
        public HashSet<OperationPin> Operations { get; } = [];
        public List<MemoryInvocation> MemoryInvocations { get; } = [];
    }
    // Original parent/backing and raw outcome correlations in the existing fabric owner.
    private sealed class MemoryInvocation(TrackedFabric parent, RegionBackingLeaseDescriptor backing)
    {
        public TrackedFabric Parent { get; } = parent;
        public RegionBackingLeaseDescriptor Backing { get; } = backing;
        public PlatformAuthorityResult<CxlMemoryBinding>? Outcome { get; set; }
        public PlatformAuthorityResult? ReturnedObjectCompensation { get; set; }
        public bool ReceiptValidated { get; set; }
        public TrackedMemory? OriginalMemorySource { get; set; }
    }
    private sealed record TrackedMemory(CxlMemoryBinding Binding, MemoryInvocation Invocation)
    {
        public bool ReleaseInFlight { get; set; }
        public CxlMemoryBinding? ReleasedBinding { get; set; }
    }
    private readonly object _gate = new();
    private readonly RegionAuthority _regions;
    private readonly RuntimeKernel _kernel;
    private readonly PlatformAuthorityBridge _platform;
    private readonly ICxlDiscoveryProvider _discovery;
    private readonly ICxlIoProvider _io;
    private readonly ICxlFabricProvider _fabric;
    private readonly ICxlMemoryProvider _memory;
    private readonly ICxlCoherentAccessProvider _coherent;
    private readonly ICxlSecurityEvidenceProvider _security;
    private readonly ConcurrentDictionary<CxlFabricBindingId, TrackedFabric> _trackedFabric = new();
    private readonly ConcurrentDictionary<CxlMemoryBindingId, TrackedMemory> _trackedMemory = new();
    private readonly ConcurrentDictionary<CxlCoherentBindingId, CxlCoherentBinding> _trackedCoherent = new();
    private readonly ConcurrentDictionary<(ProcessHandle Process, RegionOwner Owner), byte> _uncontainedEffects = new();

    public CxlAuthorityBridge(
        RuntimeKernel kernel,
        ICxlDiscoveryProvider discovery,
        ICxlIoProvider io,
        ICxlFabricProvider fabric,
        ICxlMemoryProvider memory,
        ICxlCoherentAccessProvider coherent,
        ICxlSecurityEvidenceProvider security)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        _kernel = kernel;
        _regions = kernel.Regions;
        _platform = kernel.PlatformAuthority;
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _io = io ?? throw new ArgumentNullException(nameof(io));
        _fabric = fabric ?? throw new ArgumentNullException(nameof(fabric));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _coherent = coherent ?? throw new ArgumentNullException(nameof(coherent));
        _security = security ?? throw new ArgumentNullException(nameof(security));
        kernel.RegisterCxlTeardownParticipant(this);
    }

    public KernelResult<PlatformDeviceIdentity> ResolveIoDevice(CxlEndpointSnapshot expected)
    {
        var endpoint = ValidateEndpoint(expected, CxlEndpointFeatures.Io);
        if (!endpoint.IsSuccess) return KernelResult<PlatformDeviceIdentity>.Fail(endpoint.Error, endpoint.Message!);
        return FromProvider(_io.ResolveDevice(expected.EndpointId, expected.DeviceGeneration));
    }

    /// <summary>
    /// Revalidates boot-admission evidence against the current provider generation. This
    /// creates no fabric binding, backing lease, RegionUse, OwnedRegion, or capability.
    /// </summary>
    public KernelResult<CxlEndpointSnapshot> RevalidateFreshBootEndpoint(CxlEndpointSnapshot expected) =>
        ValidateEndpoint(expected, CxlEndpointFeatures.Io | CxlEndpointFeatures.Memory);

    public KernelResult ValidateDeviceAuthority(
        PlatformDomainIdentity subject,
        PlatformDeviceLease deviceLease,
        CxlEndpointSnapshot endpoint)
    {
        var lease = _platform.ValidateDeviceLease(deviceLease, subject);
        if (!lease.IsSuccess) return lease;
        var current = ValidateEndpoint(endpoint, CxlEndpointFeatures.Io);
        if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
        var device = FromProvider(_io.ResolveDevice(endpoint.EndpointId, endpoint.DeviceGeneration));
        if (!device.IsSuccess) return KernelResult.Fail(device.Error, device.Message!);
        return device.Value == deviceLease.Device
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.InsufficientRights, "The device lease does not authorize this CXL endpoint.");
    }

    public KernelResult<CxlFabricBinding> BindFabric(
        RegionOwner principal,
        RegionUseHandle regionUse,
        PlatformDomainIdentity subject,
        PlatformDeviceLease deviceLease,
        CxlFabricBindingRequest request)
        => CreateFabricWithParent(principal, subject, deviceLease,
            reservation => BindFabricCore(principal, regionUse, subject, deviceLease, request, reservation));

    private KernelResult<CxlFabricBinding> BindFabricCore(
        RegionOwner principal, RegionUseHandle regionUse, PlatformDomainIdentity subject,
        PlatformDeviceLease deviceLease, CxlFabricBindingRequest request, CxlDeviceReservation reservation)
    {
        var authority = ValidateAuthority(principal, regionUse, subject, deviceLease, request.EndpointId, request.DeviceGeneration);
        if (!authority.IsSuccess) return KernelResult<CxlFabricBinding>.Fail(authority.Error, authority.Message!);
        if (request.CapacityBytes <= 0 || !Enum.IsDefined(request.Persistence) || !Enum.IsDefined(request.Sharing))
            return KernelResult<CxlFabricBinding>.Fail(KernelError.PlatformDenied, "CXL fabric request has invalid semantic placement properties.");

        var providerResult = InvokeCreation(() => InvokeFabricBind(request, reservation), subject.Process, principal, "CXL fabric bind",
            () => ValidateCreationTuple(request.EndpointId, request.DeviceGeneration, CxlEndpointFeatures.Io),
            () => _kernel.CommitCxlDeviceEffect(deviceLease, principal, KernelResult.Ok));
        if (!providerResult.IsSuccess) return providerResult;
        var result = KernelResult<CxlFabricBinding>.Ok(providerResult.Value!);
        var binding = result.Value!;
        if (!TrackFabric(binding, principal, subject.Process, reservation))
            return KernelResult<CxlFabricBinding>.Fail(KernelError.ExternalEffectUncontained, "CXL fabric receipt identity collided; parent remains pinned.");
        var currentBinding = FromProvider(_fabric.Query(binding.BindingId));
        if (binding.BindingId.Value == 0 || binding.Generation.Value == 0 ||
            binding.EndpointId != request.EndpointId || binding.DeviceGeneration != request.DeviceGeneration ||
            binding.CapacityBytes < request.CapacityBytes || !currentBinding.IsSuccess || currentBinding.Value != binding)
        {
            return CompensateMalformedFabric(reservation, binding, subject.Process, principal);
        }
        lock (_gate) reservation.OriginalFabricReceiptValidated = true;
        var finalDevice = ValidateDeviceAuthority(subject, deviceLease,
            new(request.EndpointId, request.DeviceGeneration, CxlEndpointFeatures.Io, true));
        var final = _kernel.CommitCxlDeviceEffect(deviceLease, principal,
            () => finalDevice.IsSuccess ? CommitFabricPublication(reservation) : finalDevice);
        return final.IsSuccess && finalDevice.IsSuccess ? result : KernelResult<CxlFabricBinding>.Fail(KernelError.ExternalEffectUncontained,
            "CXL fabric receipt arrived after authorization changed; parent remains pinned for closure.");
    }

    public KernelResult<CxlFabricBinding> BindFabricForBacking(
        RegionOwner principal, RegionBackingLeaseHandle backingLease,
        PlatformDomainIdentity subject, PlatformDeviceLease deviceLease,
        CxlFabricBindingRequest request)
    {
        var admission = BeginBackingCreation(subject.Process, principal, backingLease);
        if (!admission.IsSuccess) return KernelResult<CxlFabricBinding>.Fail(admission.Error, admission.Message!);
        try { return CreateFabricWithParent(principal, subject, deviceLease,
            reservation => BindFabricForBackingCore(principal, backingLease, subject, deviceLease, request, reservation)); }
        finally { _regions.EndBackingCreation(admission.Value!); }
    }

    private KernelResult<CxlFabricBinding> BindFabricForBackingCore(
        RegionOwner principal, RegionBackingLeaseHandle backingLease,
        PlatformDomainIdentity subject, PlatformDeviceLease deviceLease, CxlFabricBindingRequest request, CxlDeviceReservation reservation)
    {
        var backing = _regions.ValidateBacking(backingLease, principal);
        if (!backing.IsSuccess) return KernelResult<CxlFabricBinding>.Fail(backing.Error, backing.Message!);
        var device = ValidateDeviceAuthority(subject, deviceLease,
            new(request.EndpointId, request.DeviceGeneration, CxlEndpointFeatures.Io | CxlEndpointFeatures.Memory, true));
        if (!device.IsSuccess) return KernelResult<CxlFabricBinding>.Fail(device.Error, device.Message!);
        if (request.CapacityBytes < backing.Value!.ByteLength || !Enum.IsDefined(request.Persistence) || !Enum.IsDefined(request.Sharing))
            return KernelResult<CxlFabricBinding>.Fail(KernelError.PlatformDenied, "CXL backing request is invalid or too small.");
        var providerResult = InvokeCreation(() => InvokeFabricBind(request, reservation), subject.Process, principal, "CXL backing fabric bind",
            () => ValidateCreationTuple(request.EndpointId, request.DeviceGeneration, CxlEndpointFeatures.Io | CxlEndpointFeatures.Memory),
            () => _kernel.CommitCxlDeviceEffect(deviceLease, principal, KernelResult.Ok));
        if (!providerResult.IsSuccess) return providerResult;
        var result = KernelResult<CxlFabricBinding>.Ok(providerResult.Value!);
        var binding = result.Value!;
        if (!TrackFabric(binding, principal, subject.Process, reservation, backingLease))
            return KernelResult<CxlFabricBinding>.Fail(KernelError.ExternalEffectUncontained, "CXL fabric receipt identity collided; parent remains pinned.");
        var currentBinding = FromProvider(_fabric.Query(binding.BindingId));
        if (binding.BindingId.Value == 0 || binding.Generation.Value == 0 || binding.EndpointId != request.EndpointId ||
            binding.DeviceGeneration != request.DeviceGeneration || binding.CapacityBytes < request.CapacityBytes ||
            !currentBinding.IsSuccess || currentBinding.Value != binding)
        {
            return CompensateMalformedFabric(reservation, binding, subject.Process, principal);
        }
        lock (_gate) reservation.OriginalFabricReceiptValidated = true;
        var finalDevice = ValidateDeviceAuthority(subject, deviceLease,
            new(request.EndpointId, request.DeviceGeneration, CxlEndpointFeatures.Io | CxlEndpointFeatures.Memory, true));
        var finalPrincipal = _kernel.CommitCxlDeviceEffect(deviceLease, principal,
            () => finalDevice.IsSuccess ? CommitFabricPublication(reservation) : finalDevice);
        if (!finalDevice.IsSuccess || !finalPrincipal.IsSuccess)
            return KernelResult<CxlFabricBinding>.Fail(KernelError.ExternalEffectUncontained,
                "CXL fabric creation completed after local authorization changed; exact tracked receipt remains pinned for closure.");
        return result;
    }

    public KernelResult<CxlMemoryBinding> BindMemory(
        RegionOwner principal,
        RegionBackingLeaseHandle backingLease,
        CxlFabricBinding expectedFabric)
    {
        TrackedFabric tracked;
        lock (_gate)
        {
            if (!_trackedFabric.TryGetValue(expectedFabric.BindingId, out tracked!) || tracked.Owner != principal || tracked.Binding != expectedFabric)
                return KernelResult<CxlMemoryBinding>.Fail(KernelError.PlatformBindingNotFound,
                    "Exact owner fabric is required for backing creation admission.");
            if (!IsOriginalFabricCurrentLocked(tracked))
                return KernelResult<CxlMemoryBinding>.Fail(KernelError.StaleGeneration, "Original CXL memory parent is not current.");
        }
        var admission = BeginBackingCreation(tracked.Process, principal, backingLease);
        if (!admission.IsSuccess) return KernelResult<CxlMemoryBinding>.Fail(admission.Error, admission.Message!);
        try { return WithFabricChild(principal, expectedFabric,
            () => BindMemoryCore(principal, backingLease, expectedFabric, tracked), tracked); }
        finally { _regions.EndBackingCreation(admission.Value!); }
    }

    private KernelResult<CxlMemoryBinding> BindMemoryCore(
        RegionOwner principal, RegionBackingLeaseHandle backingLease,
        CxlFabricBinding expectedFabric, TrackedFabric trackedFabric)
    {
        var process = trackedFabric.Process;
        var backing = _regions.ValidateBacking(backingLease, principal);
        if (!backing.IsSuccess) return KernelResult<CxlMemoryBinding>.Fail(backing.Error, backing.Message!);
        var fabric = ValidateFabric(expectedFabric);
        if (!fabric.IsSuccess) return KernelResult<CxlMemoryBinding>.Fail(fabric.Error, fabric.Message!);
        MemoryInvocation? invocation = null;
        var providerResult = InvokeCreation(() =>
        {
            invocation = new(trackedFabric, backing.Value!);
            lock (_gate) trackedFabric.MemoryInvocations.Add(invocation);
            var response = _memory.BindMemory(expectedFabric, backing.Value!);
            lock (_gate) invocation.Outcome = response;
            return response;
        },
            trackedFabric.Process, principal, "CXL memory bind",
            () => ValidateCreationTuple(expectedFabric.EndpointId, expectedFabric.DeviceGeneration,
                CxlEndpointFeatures.Io | CxlEndpointFeatures.Memory, expectedFabric),
            () => _kernel.CommitCxlDeviceEffect(trackedFabric.DeviceReservation.Lease, principal,
                () => ValidateFabricChildCommit(trackedFabric)));
        if (!providerResult.IsSuccess)
        {
            // Fresh exact NotAccepted or local pre-effect refusal only; ambiguity retains the attempt.
            if (providerResult.Error != KernelError.ExternalEffectUncontained && invocation is not null)
                lock (_gate) trackedFabric.MemoryInvocations.Remove(invocation);
            return providerResult;
        }
        var binding = providerResult.Value!;
        var originalMemory = new TrackedMemory(binding, invocation!);
        lock (_gate)
        {
            invocation!.OriginalMemorySource = originalMemory;
            if (!_trackedMemory.TryAdd(binding.BindingId, originalMemory))
            {
                _uncontainedEffects.TryAdd((process, principal), 0);
                return KernelResult<CxlMemoryBinding>.Fail(KernelError.ExternalEffectUncontained, "CXL memory receipt identity collided; fabric remains pinned.");
            }
        }
        KernelResult<CxlMemoryBinding> validated;
        try { validated = ValidateMemoryResult(binding, invocation!); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            _uncontainedEffects.TryAdd((process, principal), 0);
            return KernelResult<CxlMemoryBinding>.Fail(KernelError.ExternalEffectUncontained,
                $"Original CXL memory outcome remains quarantined after observation/compensation failure: {exception.Message}");
        }
        if (validated.IsSuccess)
        {
            var finalFabric = ValidateFabric(expectedFabric);
            var finalPrincipal = _kernel.CommitCxlDeviceEffect(trackedFabric.DeviceReservation.Lease, principal,
                () => finalFabric.IsSuccess ? ValidateFabricChildCommit(trackedFabric) : finalFabric);
            if (!finalFabric.IsSuccess || !finalPrincipal.IsSuccess)
                return KernelResult<CxlMemoryBinding>.Fail(KernelError.ExternalEffectUncontained,
                    "CXL memory creation completed after a dependency changed; tracked receipt remains pinned for closure.");
        }
        return validated;
    }

    private KernelResult<RegionBackingLeaseDescriptor> BeginBackingCreation(
        ProcessHandle process, RegionOwner owner, RegionBackingLeaseHandle backing)
    {
        KernelResult<RegionBackingLeaseDescriptor> admission = default;
        var committed = _kernel.CommitCxlBackingPublication(process, owner, () =>
        {
            admission = _regions.BeginBackingCreation(backing, owner);
            return admission.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(admission.Error, admission.Message!);
        });
        return committed.IsSuccess ? admission : KernelResult<RegionBackingLeaseDescriptor>.Fail(committed.Error, committed.Message!);
    }

    private bool TrackFabric(CxlFabricBinding binding, RegionOwner owner, ProcessHandle process,
        CxlDeviceReservation reservation, RegionBackingLeaseHandle? backing = null)
    {
        var tracked = new TrackedFabric(binding, owner, process, reservation, backing);
        lock (_gate)
            return reservation.CaptureFabricSource(tracked) && _trackedFabric.TryAdd(binding.BindingId, tracked);
    }

    private KernelResult CommitFabricPublication(CxlDeviceReservation reservation)
    {
        lock (_gate)
        {
            if (reservation.OriginalFabricSource is not TrackedFabric tracked || !IsOriginalFabricCurrentLocked(tracked) ||
                !reservation.OriginalFabricReceiptValidated || !tracked.CreationInFlight || tracked.ClosureInFlight)
                return KernelResult.Fail(KernelError.StaleGeneration, "CXL fabric publication lost its original admitted source.");
            tracked.Admitted = true;
            return KernelResult.Ok();
        }
    }

    private bool IsOriginalFabricCurrentLocked(TrackedFabric tracked) =>
        ReferenceEquals(tracked.DeviceReservation.OriginalFabricSource, tracked) &&
        _trackedFabric.TryGetValue(tracked.Binding.BindingId, out var current) && ReferenceEquals(current, tracked);

    private KernelResult ValidateFabricClosureCommitLocked(TrackedFabric tracked)
    {
        if (!IsOriginalFabricCurrentLocked(tracked))
            return KernelResult.Fail(KernelError.StaleGeneration, "Original CXL fabric owner is not current.");
        if (!tracked.ClosureInFlight || tracked.CreationInFlight || tracked.PendingChildren != 0 ||
            tracked.Operations.Count != 0 || tracked.MemoryInvocations.Count != 0 ||
            _trackedMemory.Values.Any(item => item.Binding.FabricBinding.BindingId == tracked.Binding.BindingId) ||
            _trackedCoherent.Values.Any(item => item.FabricBinding.BindingId == tracked.Binding.BindingId))
            return KernelResult.Fail(KernelError.PlatformBindingActive, "Original CXL fabric dependencies are not closed.");
        return _uncontainedEffects.ContainsKey((tracked.Process, tracked.Owner))
            ? KernelResult.Fail(KernelError.ExternalEffectUncontained, "Unknown original CXL effects remain pinned.") : KernelResult.Ok();
    }

    private PlatformAuthorityResult<CxlFabricBinding> InvokeFabricBind(
        CxlFabricBindingRequest request, CxlDeviceReservation reservation)
    {
        lock (_gate) reservation.OriginalFabricRequest = request;
        var result = _fabric.Bind(request); // callback outside owner/commit locks
        lock (_gate) reservation.OriginalFabricOutcome = result;
        return result;
    }

    private KernelResult<CxlFabricBinding> CompensateMalformedFabric(CxlDeviceReservation reservation,
        CxlFabricBinding binding, ProcessHandle process, RegionOwner owner)
    {
        // Set ambiguity before compensation: reentry/throw cannot release original pins.
        _uncontainedEffects.TryAdd((process, owner), 0);
        var compensation = _fabric.Unbind(binding);
        lock (_gate) reservation.ReturnedObjectCompensation = compensation;
        return KernelResult<CxlFabricBinding>.Fail(KernelError.ExternalEffectUncontained,
            "Malformed CXL fabric outcome remains quarantined; returned-object cleanup does not close the original invocation.");
    }

    private KernelResult<CxlFabricBinding> CreateFabricWithParent(RegionOwner owner, PlatformDomainIdentity subject,
        PlatformDeviceLease lease, Func<CxlDeviceReservation, KernelResult<CxlFabricBinding>> create)
    {
        if (subject != lease.DomainBinding.Subject)
            return KernelResult<CxlFabricBinding>.Fail(KernelError.WrongPlatformDomain, "CXL parent subject is not exact.");
        var reservation = new CxlDeviceReservation(lease);
        var admitted = _kernel.CommitCxlDeviceEffect(lease, owner, () => _platform.AddCxlDeviceReservation(reservation));
        if (!admitted.IsSuccess) return KernelResult<CxlFabricBinding>.Fail(admitted.Error, admitted.Message!);
        var publish = false;
        try
        {
            var result = create(reservation);
            if (!result.IsSuccess && result.Error != KernelError.ExternalEffectUncontained &&
                !_trackedFabric.Values.Any(item => ReferenceEquals(item.DeviceReservation, reservation)))
            {
                var released = _platform.ReleaseCxlDeviceReservation(reservation);
                if (!released.IsSuccess)
                    return KernelResult<CxlFabricBinding>.Fail(KernelError.ExternalEffectUncontained,
                        "CXL rejection/compensation lost parent continuity; reservation remains pinned.");
            }
            publish = result.IsSuccess;
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            if (!_trackedFabric.Values.Any(item => ReferenceEquals(item.DeviceReservation, reservation)))
                _uncontainedEffects.TryAdd((subject.Process, owner), 0);
            return KernelResult<CxlFabricBinding>.Fail(KernelError.ExternalEffectUncontained,
                $"CXL fabric creation did not prove closure; parent reservation remains pinned: {exception.Message}");
        }
        finally
        {
            lock (_gate)
                if (reservation.OriginalFabricSource is TrackedFabric original)
                {
                    if (!publish) original.Admitted = false;
                    original.CreationInFlight = false;
                }
        }
    }

    private KernelResult ValidateFabricChildCommit(TrackedFabric tracked)
    {
        lock (_gate)
        {
            if (!IsOriginalFabricCurrentLocked(tracked))
                return KernelResult.Fail(KernelError.StaleGeneration, "Original CXL child parent changed before commit.");
            return tracked.Admitted && !tracked.CreationInFlight && !tracked.ClosureInFlight && tracked.PendingChildren > 0
                ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformBindingActive, "Original CXL child admission is no longer live.");
        }
    }

    private KernelResult<T> WithFabricChild<T>(RegionOwner owner, CxlFabricBinding fabric, Func<KernelResult<T>> create,
        TrackedFabric? captured = null)
    {
        TrackedFabric tracked;
        lock (_gate)
        {
            if (captured is not null) tracked = captured;
            else if (!_trackedFabric.TryGetValue(fabric.BindingId, out tracked!))
                return KernelResult<T>.Fail(KernelError.PlatformBindingNotFound, "Exact owner fabric is required for child admission.");
            if (tracked.Owner != owner || tracked.Binding != fabric)
                return KernelResult<T>.Fail(KernelError.PlatformBindingNotFound, "Exact owner fabric is required for child admission.");
            if (!IsOriginalFabricCurrentLocked(tracked))
                return KernelResult<T>.Fail(KernelError.StaleGeneration, "Original CXL child parent is not current.");
        }
        var admission = _kernel.CommitCxlDeviceEffect(tracked.DeviceReservation.Lease, owner, () =>
        {
            lock (_gate)
            {
                if (!IsOriginalFabricCurrentLocked(tracked))
                    return KernelResult.Fail(KernelError.StaleGeneration, "CXL fabric changed before child admission.");
                if (tracked.CreationInFlight || !tracked.Admitted || tracked.ClosureInFlight || tracked.PendingChildren == int.MaxValue)
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "CXL child admission cannot overlap fabric closure.");
                tracked.PendingChildren++;
                return KernelResult.Ok();
            }
        });
        if (!admission.IsSuccess) return KernelResult<T>.Fail(admission.Error, admission.Message!);
        try { return create(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return KernelResult<T>.Fail(KernelError.ExternalEffectUncontained,
                $"CXL child creation did not prove closure; parent remains pinned: {exception.Message}");
        }
        finally { lock (_gate) tracked.PendingChildren--; }
    }

    internal KernelResult<OperationPin> BeginOperation(RegionOwner owner, CxlFabricBinding fabric, PlatformDeviceLease device)
    {
        TrackedFabric tracked;
        lock (_gate)
        {
            if (!_trackedFabric.TryGetValue(fabric.BindingId, out tracked!) || tracked.Owner != owner ||
                tracked.Binding != fabric || tracked.DeviceReservation.Lease != device ||
                !IsOriginalFabricCurrentLocked(tracked))
                return KernelResult<OperationPin>.Fail(KernelError.StaleGeneration, "Original Type-2 fabric source is not current.");
        }
        var exact = ValidateFabric(fabric);
        if (!exact.IsSuccess) return KernelResult<OperationPin>.Fail(exact.Error, exact.Message!);
        var source = ValidateClosureEndpoint(fabric);
        if (!source.IsSuccess) return KernelResult<OperationPin>.Fail(source.Error, source.Message!);
        var pin = new OperationPin(fabric, owner, tracked);
        var admitted = _kernel.CommitCxlDeviceEffect(tracked.DeviceReservation.Lease, owner, () =>
        {
            lock (_gate)
            {
                if (!IsOriginalFabricCurrentLocked(tracked) ||
                    !tracked.Admitted || tracked.CreationInFlight || tracked.ClosureInFlight)
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "Type-2 operation cannot overlap fabric creation/closure.");
                tracked.Operations.Add(pin);
                return KernelResult.Ok();
            }
        });
        return admitted.IsSuccess ? KernelResult<OperationPin>.Ok(pin)
            : KernelResult<OperationPin>.Fail(admitted.Error, admitted.Message!);
    }

    // Called only by the real Type-2 consumer after zero-effect admission failure
    // or exact provider operation closure. This pin carries no permission.
    internal KernelResult EndOperation(OperationPin pin)
    {
        lock (_gate)
        {
            if (pin.OriginalParent is not TrackedFabric tracked || !IsOriginalFabricCurrentLocked(tracked) ||
                tracked.Binding != pin.Fabric || tracked.Owner != pin.Owner || !tracked.Operations.Remove(pin))
                return KernelResult.Fail(KernelError.StaleGeneration, "Type-2 parent operation pin is not exact.");
            return KernelResult.Ok();
        }
    }

    // Pure local consumer commit; callbacks supplied here are existing local writers only.
    internal KernelResult CommitOperationSource(OperationPin pin, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            if (pin.OriginalParent is not TrackedFabric tracked || !IsOriginalFabricCurrentLocked(tracked) ||
                tracked.Binding != pin.Fabric || tracked.Owner != pin.Owner || !tracked.Operations.Contains(pin))
                return KernelResult.Fail(KernelError.StaleGeneration, "Original Type-2 operation parent is not current.");
            if (!tracked.Admitted || tracked.CreationInFlight || tracked.ClosureInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingActive, "Original Type-2 parent is not usable.");
            return commit();
        }
    }

    internal KernelResult ValidateOperationClosureSource(OperationPin pin)
    {
        var original = CommitOperationSource(pin, KernelResult.Ok);
        if (!original.IsSuccess) return original;
        var source = ValidateClosureEndpoint(pin.Fabric);
        if (!source.IsSuccess) return source;
        var fabric = ValidateFabric(pin.Fabric);
        return fabric.IsSuccess ? CommitOperationSource(pin, KernelResult.Ok) : fabric;
    }

    public KernelResult<CxlCoherentBinding> BindCoherentAccess(
        RegionOwner principal,
        RegionUseHandle regionUse,
        CxlFabricBinding expectedFabric)
        => WithFabricChild(principal, expectedFabric, () => BindCoherentAccessCore(principal, regionUse, expectedFabric));

    private KernelResult<CxlCoherentBinding> BindCoherentAccessCore(
        RegionOwner principal, RegionUseHandle regionUse, CxlFabricBinding expectedFabric)
    {
        var endpoint = ValidateEndpoint(
            new CxlEndpointSnapshot(expectedFabric.EndpointId, expectedFabric.DeviceGeneration, CxlEndpointFeatures.CoherentAccess, true),
            CxlEndpointFeatures.CoherentAccess);
        if (!endpoint.IsSuccess) return KernelResult<CxlCoherentBinding>.Fail(endpoint.Error, endpoint.Message!);
        var use = _regions.ValidateUse(regionUse, principal);
        if (!use.IsSuccess) return KernelResult<CxlCoherentBinding>.Fail(use.Error, use.Message!);
        if (use.Value!.Mode != RegionUseMode.DirectCoherentWrite && use.Value.Mode != RegionUseMode.SharedReadMostly)
            return KernelResult<CxlCoherentBinding>.Fail(KernelError.InsufficientRights, "Coherent access requires an explicitly coherent RegionUse mode.");
        var fabric = ValidateFabric(expectedFabric);
        if (!fabric.IsSuccess) return KernelResult<CxlCoherentBinding>.Fail(fabric.Error, fabric.Message!);
        if (!_trackedFabric.TryGetValue(expectedFabric.BindingId, out var trackedFabric) || trackedFabric.Binding != expectedFabric)
            return KernelResult<CxlCoherentBinding>.Fail(KernelError.PlatformBindingNotFound,
                "CXL fabric binding is not tracked for coherent-access creation.");

        var providerResult = InvokeCreation(() => _coherent.BindCoherentAccess(expectedFabric, use.Value),
            trackedFabric.Process, principal, "CXL coherent-access bind",
            () => ValidateCreationTuple(expectedFabric.EndpointId, expectedFabric.DeviceGeneration,
                CxlEndpointFeatures.CoherentAccess, expectedFabric),
            () => _kernel.CommitCxlDeviceEffect(trackedFabric.DeviceReservation.Lease, principal, KernelResult.Ok));
        if (!providerResult.IsSuccess) return providerResult;
        var result = KernelResult<CxlCoherentBinding>.Ok(providerResult.Value!);
        var binding = result.Value!;
        if (!_trackedCoherent.TryAdd(binding.BindingId, binding))
        {
            _uncontainedEffects.TryAdd((trackedFabric.Process, principal), 0);
            return KernelResult<CxlCoherentBinding>.Fail(KernelError.ExternalEffectUncontained, "CXL coherent receipt identity collided; fabric remains pinned.");
        }
        var current = FromProvider(_coherent.QueryCoherentAccess(binding.BindingId));
        if (binding.BindingId.Value == 0 || binding.Generation.Value == 0 ||
            binding.FabricBinding != expectedFabric || binding.RegionUse != regionUse ||
            !current.IsSuccess || current.Value != binding)
        {
            var compensation = _coherent.ReleaseCoherentAccess(binding);
            if (compensation.IsSuccess) _trackedCoherent.TryRemove(binding.BindingId, out _);
            else
            {
                _uncontainedEffects.TryAdd((trackedFabric.Process, principal), 0);
                return KernelResult<CxlCoherentBinding>.Fail(KernelError.ExternalEffectUncontained,
                    "Malformed CXL coherent binding could not be closed and remains quarantined.");
            }
            return KernelResult<CxlCoherentBinding>.Fail(KernelError.PlatformFaulted, "CXL coherent provider returned a malformed binding.");
        }
        var finalFabric = ValidateFabric(expectedFabric);
        var finalPrincipal = _kernel.CommitCxlDeviceEffect(trackedFabric.DeviceReservation.Lease, principal, KernelResult.Ok);
        return finalFabric.IsSuccess && finalPrincipal.IsSuccess ? result
            : KernelResult<CxlCoherentBinding>.Fail(KernelError.ExternalEffectUncontained,
                "CXL coherent receipt arrived after authorization changed; parent remains pinned for closure.");
    }

    public KernelResult RevalidateBeforeEffect(
        RegionOwner principal,
        RegionUseHandle regionUse,
        CxlEndpointSnapshot endpoint,
        CxlFabricBinding fabric,
        CxlCoherentBinding? coherent = null)
    {
        var use = _regions.ValidateUse(regionUse, principal);
        if (!use.IsSuccess) return KernelResult.Fail(use.Error, use.Message!);
        TrackedFabric tracked;
        lock (_gate)
        {
            if (!_trackedFabric.TryGetValue(fabric.BindingId, out tracked!) || tracked.Owner != principal ||
                tracked.Binding != fabric || !IsOriginalFabricCurrentLocked(tracked) ||
                !tracked.Admitted || tracked.CreationInFlight || tracked.ClosureInFlight)
                return KernelResult.Fail(KernelError.StaleGeneration, "Original CXL effect source is not current.");
        }
        var currentEndpoint = ValidateEndpoint(endpoint, CxlEndpointFeatures.None);
        if (!currentEndpoint.IsSuccess) return KernelResult.Fail(currentEndpoint.Error, currentEndpoint.Message!);
        var currentFabric = ValidateFabric(fabric);
        if (!currentFabric.IsSuccess) return currentFabric;
        if (fabric.EndpointId != endpoint.EndpointId || fabric.DeviceGeneration != endpoint.DeviceGeneration)
            return KernelResult.Fail(KernelError.StaleGeneration, "CXL endpoint and fabric binding generations no longer describe the same binding.");

        if (coherent is not null)
        {
            var current = FromProvider(_coherent.QueryCoherentAccess(coherent.BindingId));
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            if (current.Value != coherent || coherent.RegionUse != regionUse || coherent.FabricBinding != fabric)
                return KernelResult.Fail(KernelError.StaleGeneration, "CXL coherent binding generation changed.");
            if ((currentEndpoint.Value!.Features & CxlEndpointFeatures.CoherentAccess) == 0)
                return KernelResult.Fail(KernelError.StaleGeneration, "CXL coherent capability changed after binding.");
        }
        return _kernel.CommitCxlDeviceEffect(tracked.DeviceReservation.Lease, principal, () =>
        {
            lock (_gate)
            {
                if (!IsOriginalFabricCurrentLocked(tracked) || tracked.Binding != fabric || tracked.Owner != principal ||
                    !tracked.Admitted || tracked.CreationInFlight || tracked.ClosureInFlight)
                    return KernelResult.Fail(KernelError.StaleGeneration, "Original CXL effect source changed during revalidation.");
                var finalUse = _regions.ValidateUse(regionUse, principal);
                return finalUse.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(finalUse.Error, finalUse.Message!);
            }
        });
    }

    public KernelResult RevalidateBacking(RegionOwner principal, RegionBackingLeaseHandle backingLease,
        CxlEndpointSnapshot endpoint, CxlFabricBinding fabric, CxlMemoryBinding memory)
    {
        var backing = _regions.ValidateBacking(backingLease, principal);
        if (!backing.IsSuccess) return KernelResult.Fail(backing.Error, backing.Message!);
        var currentEndpoint = ValidateEndpoint(endpoint, CxlEndpointFeatures.Memory);
        if (!currentEndpoint.IsSuccess) return KernelResult.Fail(currentEndpoint.Error, currentEndpoint.Message!);
        var currentFabric = ValidateFabric(fabric);
        if (!currentFabric.IsSuccess) return currentFabric;
        var current = FromProvider(_memory.QueryMemory(memory.BindingId));
        if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
        return current.Value == memory && memory.BackingLease == backingLease && memory.FabricBinding == fabric
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.StaleGeneration, "CXL backing generation changed.");
    }

    // Existing device and tracked binding owners guard pure secure guest decisions only.
    internal KernelResult<Func<Func<KernelResult>, KernelResult>> PrepareSecureGuestBackingSource(RegionOwner owner,
        RegionBackingLeaseHandle backing, CxlFabricBinding fabric, CxlMemoryBinding memory)
        => PrepareBackingConsequenceSource(owner, backing, fabric, memory);

    // Existing exact source owners also guard Region health consequences; no provider callbacks.
    internal KernelResult<Func<Func<KernelResult>, KernelResult>> PrepareBackingConsequenceSource(RegionOwner owner,
        RegionBackingLeaseHandle backing, CxlFabricBinding fabric, CxlMemoryBinding memory)
    {
        TrackedFabric captured;
        TrackedMemory capturedMemory;
        lock (_gate)
        {
            if (!_trackedFabric.TryGetValue(fabric.BindingId, out captured!) ||
                !_trackedMemory.TryGetValue(memory.BindingId, out capturedMemory!) ||
                !IsBackingConsequenceSourceCurrentLocked(captured, capturedMemory, owner, backing, fabric, memory))
                return KernelResult<Func<Func<KernelResult>, KernelResult>>.Fail(KernelError.StaleGeneration,
                    "Original CXL backing consequence source is absent or changed.");
        }
        var device = _kernel.PrepareCxlDeviceSource(captured.DeviceReservation.Lease, owner);
        if (!device.IsSuccess) return device;
        KernelResult Commit(Func<KernelResult> commit) => device.Value!(() =>
        {
            lock (_gate)
            {
                if (!IsBackingConsequenceSourceCurrentLocked(captured, capturedMemory, owner, backing, fabric, memory))
                    return KernelResult.Fail(KernelError.StaleGeneration, "Original CXL backing consequence source stopped or changed.");
                return commit();
            }
        });
        return KernelResult<Func<Func<KernelResult>, KernelResult>>.Ok(Commit);
    }

    private bool IsOriginalMemoryCurrentLocked(TrackedMemory source) =>
        ReferenceEquals(source.Invocation.OriginalMemorySource, source) &&
        _trackedMemory.TryGetValue(source.Binding.BindingId, out var current) && ReferenceEquals(current, source);

    private bool IsBackingConsequenceSourceCurrentLocked(TrackedFabric parent, TrackedMemory source,
        RegionOwner owner, RegionBackingLeaseHandle backing, CxlFabricBinding fabric, CxlMemoryBinding memory) =>
        IsOriginalFabricCurrentLocked(parent) && parent.Binding == fabric && parent.Owner == owner &&
        parent.BackingLease == backing && parent.Admitted && !parent.CreationInFlight && !parent.ClosureInFlight &&
        IsOriginalMemoryCurrentLocked(source) && ReferenceEquals(source.Invocation.Parent, parent) &&
        parent.MemoryInvocations.Contains(source.Invocation) && source.Invocation.ReceiptValidated &&
        source.Invocation.Backing.Handle == backing && source.Binding == memory && !source.ReleaseInFlight &&
        source.ReleasedBinding is null && memory.BackingLease == backing && memory.FabricBinding == fabric;

    internal KernelResult<CxlFabricBinding> QueryFabric(CxlFabricBindingId id) => FromProvider(_fabric.Query(id));
    internal KernelResult<CxlMemoryBinding> QueryMemory(CxlMemoryBindingId id) => FromProvider(_memory.QueryMemory(id));

    // Tuple selection for the real cleanup consumer only. These results are not
    // closure proof; ReleaseMemory/ReleaseFabric still require fresh source continuity.
    internal KernelResult<CxlMemoryBinding> QueryMemoryForCleanup(CxlMemoryBinding admitted)
    {
        lock (_gate)
            if (_trackedMemory.TryGetValue(admitted.BindingId, out var tracked) && tracked.Binding == admitted &&
                tracked.ReleasedBinding is { } reply)
                return KernelResult<CxlMemoryBinding>.Ok(reply);
        return QueryMemory(admitted.BindingId);
    }

    internal KernelResult<CxlFabricBinding> QueryFabricForCleanup(CxlFabricBinding admitted)
    {
        lock (_gate)
            if (_trackedFabric.TryGetValue(admitted.BindingId, out var tracked) && tracked.Binding == admitted &&
                tracked.ReleasedBinding is { } reply)
                return KernelResult<CxlFabricBinding>.Ok(reply);
        return QueryFabric(admitted.BindingId);
    }

    public KernelResult<EvidenceRecord> QuerySecurityEvidence(CxlEndpointSnapshot expected)
    {
        var endpoint = ValidateEndpoint(expected, CxlEndpointFeatures.None);
        if (!endpoint.IsSuccess) return KernelResult<EvidenceRecord>.Fail(endpoint.Error, endpoint.Message!);
        return FromProvider(_security.QuerySecurityEvidence(expected.EndpointId, expected.DeviceGeneration));
    }

    public KernelResult ReleaseMemory(CxlMemoryBinding binding)
    {
        TrackedMemory record;
        lock (_gate)
        {
            if (!_trackedMemory.TryGetValue(binding.BindingId, out record!) || record.Binding.BackingLease != binding.BackingLease ||
                record.Binding.FabricBinding.BindingId != binding.FabricBinding.BindingId ||
                record.Binding.FabricBinding.EndpointId != binding.FabricBinding.EndpointId ||
                record.Binding.FabricBinding.DeviceGeneration != binding.FabricBinding.DeviceGeneration ||
                record.ReleasedBinding is { } reply && reply != binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "CXL memory closure cannot substitute its exact source/release tuple.");
            if (!IsOriginalMemoryCurrentLocked(record))
                return KernelResult.Fail(KernelError.StaleGeneration, "Original CXL memory owner is not current.");
            if (!record.Invocation.ReceiptValidated)
                return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Original CXL memory receipt is unvalidated; returned-object cleanup cannot release it.");
            if (!IsOriginalFabricCurrentLocked(record.Invocation.Parent))
                return KernelResult.Fail(KernelError.StaleGeneration, "Original CXL memory parent is not current.");
            if (record.ReleaseInFlight || _trackedFabric.TryGetValue(binding.FabricBinding.BindingId, out var parent) &&
                (parent.CreationInFlight || parent.PendingChildren != 0 || parent.ClosureInFlight))
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "CXL memory creation/closure is already in flight.");
            record.ReleaseInFlight = true;
        }
        try { return ReleaseMemoryCore(binding, record); }
        finally { lock (_gate) record.ReleaseInFlight = false; }
    }

    private KernelResult ReleaseMemoryCore(CxlMemoryBinding binding, TrackedMemory record)
    {
        var tracked = record.Binding;
        var source = ValidateClosureEndpoint(tracked.FabricBinding);
        if (!source.IsSuccess) return source;
        if (record.ReleasedBinding is null)
        {
            var current = FromProvider(_memory.QueryMemory(binding.BindingId));
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            if (current.Value != binding) return KernelResult.Fail(KernelError.StaleGeneration, "CXL memory release requires the exact current binding generation.");
            var finalSource = ValidateClosureEndpoint(tracked.FabricBinding);
            if (!finalSource.IsSuccess) return finalSource;
            lock (_gate)
                if (!IsOriginalMemoryCurrentLocked(record) || !IsOriginalFabricCurrentLocked(record.Invocation.Parent))
                    return KernelResult.Fail(KernelError.StaleGeneration, "Original memory source changed before provider Release.");
            var released = _memory.ReleaseMemory(binding);
            if (!released.IsSuccess) return KernelResult.Fail(Map(released.Status), released.Message ?? "CXL memory release failed.");
            lock (_gate) record.ReleasedBinding = binding;
        }
        if (!ValidateClosureEndpoint(tracked.FabricBinding).IsSuccess)
            return KernelResult.Fail(KernelError.ExternalEffectUncontained, "CXL memory closure lost endpoint continuity; receipt and parent remain pinned.");
        lock (_gate)
        {
            if (!IsOriginalMemoryCurrentLocked(record))
                return KernelResult.Fail(KernelError.StaleGeneration, "CXL memory cleanup lost its original tracked lifetime.");
            if (!IsOriginalFabricCurrentLocked(record.Invocation.Parent))
                return KernelResult.Fail(KernelError.StaleGeneration, "Known CXL memory closure lost its original parent; cleanup facts remain pinned.");
            record.Invocation.Parent.MemoryInvocations.Remove(record.Invocation);
            _trackedMemory.TryRemove(binding.BindingId, out _);
        }
        return KernelResult.Ok();
    }

    public KernelResult ReleaseFabric(CxlFabricBinding binding)
    {
        TrackedFabric tracked;
        lock (_gate)
        {
            if (!_trackedFabric.TryGetValue(binding.BindingId, out tracked!))
                return KernelResult.Fail(KernelError.PlatformBindingNotFound, "CXL fabric receipt is not tracked.");
            if (!IsOriginalFabricCurrentLocked(tracked))
                return KernelResult.Fail(KernelError.StaleGeneration, "CXL fabric closure requires its original owner.");
            if (!tracked.DeviceReservation.OriginalFabricReceiptValidated ||
                _uncontainedEffects.ContainsKey((tracked.Process, tracked.Owner)))
                return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Unknown CXL invocation outcome cannot prove fabric closure.");
            if (tracked.CreationInFlight || tracked.ClosureInFlight || tracked.PendingChildren != 0 || tracked.Operations.Count != 0 || tracked.MemoryInvocations.Count != 0 ||
                _trackedMemory.Values.Any(item => item.Binding.FabricBinding.BindingId == binding.BindingId) ||
                _trackedCoherent.Values.Any(item => item.FabricBinding.BindingId == binding.BindingId))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "CXL fabric closure requires completed child admission and exact child closure.");
            tracked.ClosureInFlight = true;
        }
        try { return ReleaseFabricCore(binding, tracked); }
        finally { lock (_gate) tracked.ClosureInFlight = false; }
    }

    private KernelResult ReleaseFabricCore(CxlFabricBinding binding, TrackedFabric tracked)
    {
        if (binding.EndpointId != tracked.Binding.EndpointId || binding.DeviceGeneration != tracked.Binding.DeviceGeneration ||
            tracked.ReleasedBinding is { } reply && reply != binding)
            return KernelResult.Fail(KernelError.StaleGeneration, "CXL fabric closure cannot substitute a rebound source tuple.");
        var source = ValidateClosureEndpoint(tracked.Binding);
        if (!source.IsSuccess) return source;
        if (tracked.ReleasedBinding is null)
        {
            var current = FromProvider(_fabric.Query(binding.BindingId));
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            if (current.Value != binding) return KernelResult.Fail(KernelError.StaleGeneration, "CXL fabric release requires the exact current binding generation.");
            var finalSource = ValidateClosureEndpoint(tracked.Binding);
            if (!finalSource.IsSuccess) return finalSource;
            lock (_gate)
            {
                var local = ValidateFabricClosureCommitLocked(tracked);
                if (!local.IsSuccess) return local;
            }
            var released = _fabric.Unbind(binding);
            if (!released.IsSuccess) return KernelResult.Fail(Map(released.Status), released.Message ?? "CXL fabric release failed.");
            lock (_gate)
            {
                tracked.ReleasedBinding = binding;
                tracked.Admitted = false;
            }
        }
        if (!ValidateClosureEndpoint(tracked.Binding).IsSuccess)
            return KernelResult.Fail(KernelError.ExternalEffectUncontained, "CXL fabric closure lost endpoint continuity; parent remains pinned.");
        return _platform.ReleaseCxlDeviceReservation(tracked.DeviceReservation, release =>
        {
            lock (_gate)
            {
                var local = ValidateFabricClosureCommitLocked(tracked);
                if (!local.IsSuccess) return local;
                if (tracked.ReleasedBinding != binding)
                    return KernelResult.Fail(KernelError.StaleGeneration, "Original CXL fabric closure receipt is not exact.");
                var parent = release(); // trusted platform mutation; no callback or provider query
                if (!parent.IsSuccess) return parent;
                _trackedFabric.TryRemove(binding.BindingId, out _);
                return KernelResult.Ok();
            }
        });
    }

    public KernelResult ReleaseCoherent(CxlCoherentBinding binding)
    {
        if (!_trackedCoherent.TryGetValue(binding.BindingId, out var tracked) ||
            tracked.FabricBinding.EndpointId != binding.FabricBinding.EndpointId ||
            tracked.FabricBinding.DeviceGeneration != binding.FabricBinding.DeviceGeneration)
            return KernelResult.Fail(KernelError.StaleGeneration, "CXL coherent closure cannot substitute a rebound source tuple.");
        var source = ValidateClosureEndpoint(tracked.FabricBinding);
        if (!source.IsSuccess) return source;
        var current = FromProvider(_coherent.QueryCoherentAccess(binding.BindingId));
        if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
        if (current.Value != binding) return KernelResult.Fail(KernelError.StaleGeneration, "CXL coherent release requires the exact current binding generation.");
        var released = _coherent.ReleaseCoherentAccess(binding);
        if (!released.IsSuccess) return KernelResult.Fail(Map(released.Status), released.Message ?? "CXL coherent release failed.");
        if (!ValidateClosureEndpoint(tracked.FabricBinding).IsSuccess)
            return KernelResult.Fail(KernelError.ExternalEffectUncontained, "CXL coherent closure lost endpoint continuity; parent remains pinned.");
        _trackedCoherent.TryRemove(binding.BindingId, out _);
        return KernelResult.Ok();
    }

    KernelResult ICxlTeardownParticipant.CloseForProcess(ProcessHandle process, RegionOwner owner)
    {
        if (_regions.HasPendingBackingCreations(owner))
            return KernelResult.Fail(KernelError.PlatformBindingDraining,
                "CXL backing creation is pending receipt publication.");
        lock (_gate)
            if (_trackedFabric.Values.Any(item => item.Process == process && item.Owner == owner &&
                (item.CreationInFlight || item.PendingChildren != 0 || item.ClosureInFlight || item.Operations.Count != 0)))
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "CXL child admission or closure remains in flight.");
        if (_uncontainedEffects.ContainsKey((process, owner)))
            return KernelResult.Fail(KernelError.ExternalEffectUncontained,
                "A CXL creation effect has ambiguous acceptance and reclaim remains quarantined.");
        var fabrics = _trackedFabric.Values.Where(item => item.Process == process && item.Owner == owner).ToArray();
        foreach (var fabric in fabrics)
        {
            if (fabric.BackingLease is not { } lease) continue;
            var admission = _regions.BeginBackingClosure(lease, owner);
            if (!admission.IsSuccess) return admission;
        }
        foreach (var coherent in _trackedCoherent.Values.Where(item => fabrics.Any(f => f.Binding.BindingId == item.FabricBinding.BindingId)).ToArray())
        {
            var closed = ReleaseCoherent(coherent);
            if (!closed.IsSuccess) return closed;
        }
        foreach (var memory in _trackedMemory.Values.Where(item => fabrics.Any(f => f.Binding.BindingId == item.Binding.FabricBinding.BindingId)).ToArray())
        {
            var closed = ReleaseMemory(QueryMemoryForCleanup(memory.Binding).Value ?? memory.Binding);
            if (!closed.IsSuccess) return closed;
        }
        foreach (var fabric in fabrics)
        {
            var selected = QueryFabricForCleanup(fabric.Binding);
            var closed = ReleaseFabric(selected.IsSuccess ? selected.Value! : fabric.Binding);
            if (!closed.IsSuccess) return closed;
            if (fabric.BackingLease is { } backingLease)
            {
                var releasedBacking = _regions.ReleaseBacking(backingLease, owner);
                if (!releasedBacking.IsSuccess && releasedBacking.Error != KernelError.PlatformBindingNotFound)
                    return releasedBacking;
            }
        }
        return KernelResult.Ok();
    }

    private KernelResult ValidateAuthority(RegionOwner principal, RegionUseHandle regionUse, PlatformDomainIdentity subject,
        PlatformDeviceLease deviceLease, CxlEndpointId endpointId, CxlDeviceGeneration generation)
    {
        var use = _regions.ValidateUse(regionUse, principal);
        if (!use.IsSuccess) return KernelResult.Fail(use.Error, use.Message!);
        var lease = _platform.ValidateDeviceLease(deviceLease, subject);
        if (!lease.IsSuccess) return lease;
        var endpoint = ValidateEndpoint(new(endpointId, generation, CxlEndpointFeatures.Io, true), CxlEndpointFeatures.Io);
        if (!endpoint.IsSuccess) return KernelResult.Fail(endpoint.Error, endpoint.Message!);
        var io = FromProvider(_io.ResolveDevice(endpointId, generation));
        if (!io.IsSuccess) return KernelResult.Fail(io.Error, io.Message!);
        return io.Value == deviceLease.Device
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.InsufficientRights, "The device lease does not authorize this CXL endpoint.");
    }

    private KernelResult<CxlEndpointSnapshot> ValidateEndpoint(CxlEndpointSnapshot expected, CxlEndpointFeatures required)
    {
        var current = FromProvider(_discovery.QueryEndpoint(expected.EndpointId));
        if (!current.IsSuccess) return current;
        if (!current.Value!.Available) return KernelResult<CxlEndpointSnapshot>.Fail(KernelError.PlatformUnavailable, "CXL endpoint is unavailable.");
        if (current.Value.DeviceGeneration != expected.DeviceGeneration)
            return KernelResult<CxlEndpointSnapshot>.Fail(KernelError.StaleGeneration, "CXL device generation changed.");
        if ((current.Value.Features & required) != required)
            return KernelResult<CxlEndpointSnapshot>.Fail(KernelError.PlatformUnsupported, "CXL endpoint no longer supports the required semantic feature.");
        return current;
    }

    private KernelResult ValidateFabric(CxlFabricBinding expected)
    {
        var current = FromProvider(_fabric.Query(expected.BindingId));
        if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
        return current.Value == expected
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.StaleGeneration, "CXL fabric binding generation changed.");
    }

    private KernelResult<CxlMemoryBinding> ValidateMemoryResult(CxlMemoryBinding binding, MemoryInvocation invocation)
    {
        var current = FromProvider(_memory.QueryMemory(binding.BindingId));
        if (binding.BindingId.Value != 0 && binding.Generation.Value != 0 && binding.FabricBinding == invocation.Parent.Binding &&
            binding.BackingLease == invocation.Backing.Handle && current.IsSuccess && current.Value == binding)
        {
            lock (_gate) invocation.ReceiptValidated = true;
            return KernelResult<CxlMemoryBinding>.Ok(binding);
        }
        // Returned-object closure cannot discharge an unknown original invocation.
        _uncontainedEffects.TryAdd((invocation.Parent.Process, invocation.Parent.Owner), 0);
        var compensation = _memory.ReleaseMemory(binding);
        lock (_gate) invocation.ReturnedObjectCompensation = compensation;
        return KernelResult<CxlMemoryBinding>.Fail(KernelError.ExternalEffectUncontained,
            "Malformed CXL memory outcome remains quarantined independently of returned-object cleanup.");
    }

    private static KernelResult<T> FromProvider<T>(PlatformAuthorityResult<T> result) => result.IsSuccess
        ? KernelResult<T>.Ok(result.Value!)
        : KernelResult<T>.Fail(Map(result.Status), result.Message ?? "CXL provider rejected the request.");

    private KernelResult ValidateCreationTuple(CxlEndpointId endpoint, CxlDeviceGeneration generation,
        CxlEndpointFeatures features, CxlFabricBinding? fabric = null)
    {
        var current = ValidateEndpoint(new(endpoint, generation, features, true), features);
        if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
        return fabric is null ? KernelResult.Ok() : ValidateFabric(fabric);
    }

    private KernelResult ValidateClosureEndpoint(CxlFabricBinding binding)
    {
        var current = ValidateEndpoint(new(binding.EndpointId, binding.DeviceGeneration, CxlEndpointFeatures.Io, true), CxlEndpointFeatures.Io);
        return current.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(current.Error, current.Message!);
    }

    private KernelResult<T> InvokeCreation<T>(Func<PlatformAuthorityResult<T>> effect,
        ProcessHandle process, RegionOwner owner, string operation, Func<KernelResult> validateNoEffectTuple,
        Func<KernelResult> admitEffect)
    {
        var permission = admitEffect();
        if (!permission.IsSuccess) return KernelResult<T>.Fail(permission.Error, permission.Message!);
        PlatformAuthorityResult<T> result;
        try
        {
            result = effect();
        }
        catch (Exception exception)
        {
            _uncontainedEffects.TryAdd((process, owner), 0);
            return KernelResult<T>.Fail(KernelError.ExternalEffectUncontained,
                $"{operation} threw without closure evidence and remains quarantined: {exception.Message}");
        }
        if (result.IsSuccess) return KernelResult<T>.Ok(result.Value!);
        if (result.Status == PlatformAuthorityStatus.NotAccepted)
        {
            // The provider contract supplies zero-effect evidence. A fresh tuple
            // only scopes that evidence; it never supplies closure on its own.
            try
            {
                if (validateNoEffectTuple().IsSuccess)
                    return KernelResult<T>.Fail(Map(result.Status), result.Message ?? $"{operation} was rejected before effect.");
            }
            catch (Exception exception)
            {
                _uncontainedEffects.TryAdd((process, owner), 0);
                return KernelResult<T>.Fail(KernelError.ExternalEffectUncontained,
                    $"{operation} zero-effect tuple observation failed; backing remains quarantined: {exception.Message}");
            }
            _uncontainedEffects.TryAdd((process, owner), 0);
            return KernelResult<T>.Fail(KernelError.ExternalEffectUncontained,
                $"{operation} rejection is outside the admitted generation tuple; backing remains quarantined.");
        }
        _uncontainedEffects.TryAdd((process, owner), 0);
        return KernelResult<T>.Fail(KernelError.ExternalEffectUncontained,
            result.Message ?? $"{operation} acceptance is ambiguous and remains quarantined.");
    }

    private static KernelError Map(PlatformAuthorityStatus status) => status switch
    {
        PlatformAuthorityStatus.NotAccepted => KernelError.PlatformUnavailable,
        PlatformAuthorityStatus.Unavailable => KernelError.PlatformUnavailable,
        PlatformAuthorityStatus.Unsupported => KernelError.PlatformUnsupported,
        PlatformAuthorityStatus.Stale => KernelError.StaleGeneration,
        PlatformAuthorityStatus.Revoked => KernelError.PlatformBindingRevoked,
        PlatformAuthorityStatus.WrongDomain => KernelError.WrongPlatformDomain,
        PlatformAuthorityStatus.Denied => KernelError.PlatformDenied,
        _ => KernelError.PlatformFaulted
    };
}
