using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed record CxlFabricManagedBinding(
    CxlFabricBinding Binding,
    CxlFabricResourceState State,
    CxlFabricReconfigurationId? ActiveReconfiguration);

public sealed record CxlOwnedPoolAllocation(
    CxlPoolAssignment Assignment,
    RegionHandle Region,
    RegionOwner Owner,
    RegionUseHandle RegionUse);

/// <summary>Serializes FM observations into generation-safe local admission and reclaim.</summary>
public sealed class CxlFabricManagerAuthority : ICxlTeardownParticipant
{
    private sealed class BindingRecord(CxlFabricBinding binding)
    {
        public CxlFabricBinding Binding { get; set; } = binding;
        public CxlFabricResourceState State { get; set; } = CxlFabricResourceState.Bound;
        public CxlFabricReconfigurationTicket? Ticket { get; set; }
        public int PendingAdmissions { get; set; }
        public bool CompletionInFlight { get; set; }
        public List<(ProcessHandle Principal, ExternalOperationHandle Operation, Func<KernelResult>? ProviderClosure)> Operations { get; } = [];
    }

    private readonly RuntimeKernel _kernel;
    private readonly RegionAuthority _regions;
    private readonly ICxlFabricManagementProvider _provider;
    private readonly Dictionary<CxlFabricBindingId, BindingRecord> _bindings = [];
    private readonly Dictionary<CxlPoolAssignmentId, CxlOwnedPoolAllocation> _poolAllocations = [];
    private readonly HashSet<RegionOwner> _uncontainedPoolAssignments = [];
    private readonly object _gate = new();

    public CxlFabricManagerAuthority(RuntimeKernel kernel, ICxlFabricManagementProvider provider)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _regions = kernel.Regions;
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        kernel.RegisterCxlTeardownParticipant(this);
    }

    public KernelResult<CxlFabricManagedBinding> Register(CxlFabricBinding binding)
    {
        if (binding.BindingId.Value == 0 || binding.Generation.Value == 0)
            return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.PlatformDenied, "Fabric binding is invalid or already registered.");
        lock (_gate)
            if (_bindings.ContainsKey(binding.BindingId))
                return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.PlatformDenied, "Fabric binding is already registered.");
        var provider = _provider.QueryResource(binding.BindingId);
        if (!provider.IsSuccess || provider.Value!.Binding != binding || provider.Value.State != CxlFabricResourceState.Bound)
            return KernelResult<CxlFabricManagedBinding>.Fail(Map(provider.Status), provider.Message ?? "Provider fabric binding is not current.");
        lock (_gate)
        {
            if (_bindings.ContainsKey(binding.BindingId))
                return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.PlatformDenied, "Fabric registration lost to an existing exact binding owner.");
            var record = new BindingRecord(binding);
            _bindings.Add(binding.BindingId, record);
            return KernelResult<CxlFabricManagedBinding>.Ok(Snapshot(record));
        }
    }

    public KernelResult ValidateAdmission(CxlFabricBinding binding)
    {
        lock (_gate)
        {
            var local = ValidateAdmissionCore(binding);
            if (!local.IsSuccess) return local;
        }
        var current = _provider.QueryResource(binding.BindingId);
        if (!current.IsSuccess || current.Value!.Binding != binding || current.Value.State != CxlFabricResourceState.Bound)
            return KernelResult.Fail(current.Status == PlatformAuthorityStatus.Stale ? KernelError.StaleGeneration : Map(current.Status), current.Message ?? "Fabric provider state changed.");
        lock (_gate) return ValidateAdmissionCore(binding);
    }

    private KernelResult ValidateAdmissionCore(CxlFabricBinding binding)
    {
        if (!_bindings.TryGetValue(binding.BindingId, out var record))
            return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Fabric binding is not managed.");
        if (record.State != CxlFabricResourceState.Bound)
            return KernelResult.Fail(KernelError.PlatformBindingDraining, "Fabric binding is draining or faulted; new admission is closed.");
        if (record.Binding != binding)
            return KernelResult.Fail(KernelError.StaleGeneration, "Fabric binding generation is stale.");
        return KernelResult.Ok();
    }

    public KernelResult TrackOperation(CxlFabricBinding binding, ProcessHandle principal, ExternalOperationHandle operation,
        Func<KernelResult>? providerClosure = null)
    {
        var fresh = ValidateAdmission(binding);
        if (!fresh.IsSuccess) return fresh;
        var snapshot = _kernel.QueryExternalOperation(principal, operation);
        if (!snapshot.IsSuccess) return KernelResult.Fail(snapshot.Error, snapshot.Message!);
        lock (_gate)
        {
            var admission = ValidateAdmissionCore(binding);
            if (!admission.IsSuccess) return admission;
            if (snapshot.Value!.State is ExternalOperationState.Submitted or ExternalOperationState.DeviceComplete or ExternalOperationState.Visible or ExternalOperationState.Published &&
                providerClosure is null)
                return KernelResult.Fail(KernelError.ExternalEffectUncontained,
                    "Post-submit fabric operations require an explicit provider closure/containment callback.");
            _bindings[binding.BindingId].Operations.Add((principal, operation, providerClosure));
            return KernelResult.Ok();
        }
    }

    public KernelResult UntrackOperation(CxlFabricBinding binding, ProcessHandle principal, ExternalOperationHandle operation)
    {
        lock (_gate)
        {
            if (!_bindings.TryGetValue(binding.BindingId, out var record))
                return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Fabric binding is not managed.");
            if (record.Binding != binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "Fabric binding generation is stale.");
            record.Operations.RemoveAll(item => item.Principal == principal && item.Operation == operation);
            return KernelResult.Ok();
        }
    }

    public KernelResult<T> ExecuteAdmission<T>(CxlFabricBinding binding, Func<KernelResult<T>> providerEffect)
    {
        ArgumentNullException.ThrowIfNull(providerEffect);
        var fresh = ValidateAdmission(binding);
        if (!fresh.IsSuccess) return KernelResult<T>.Fail(fresh.Error, fresh.Message!);
        BindingRecord record;
        lock (_gate)
        {
            var admission = ValidateAdmissionCore(binding);
            if (!admission.IsSuccess) return KernelResult<T>.Fail(admission.Error, admission.Message!);
            record = _bindings[binding.BindingId];
            if (record.PendingAdmissions == int.MaxValue)
                return KernelResult<T>.Fail(KernelError.CapacityExhausted, "Fabric admission interlock is saturated.");
            record.PendingAdmissions++;
        }
        try { return providerEffect(); }
        finally { lock (_gate) record.PendingAdmissions--; }
    }

    public KernelResult<CxlFabricManagedBinding> BeginReconfiguration(CxlFabricBinding binding)
    {
        var fresh = ValidateAdmission(binding);
        if (!fresh.IsSuccess) return KernelResult<CxlFabricManagedBinding>.Fail(fresh.Error, fresh.Message!);
        BindingRecord record;
        (ProcessHandle Principal, ExternalOperationHandle Operation, Func<KernelResult>? ProviderClosure)[] operations;
        lock (_gate)
        {
            var admission = ValidateAdmissionCore(binding);
            if (!admission.IsSuccess) return KernelResult<CxlFabricManagedBinding>.Fail(admission.Error, admission.Message!);
            record = _bindings[binding.BindingId];
            if (record.PendingAdmissions != 0)
                return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.PlatformBindingDraining, "Fabric creation callbacks must settle before reconfiguration.");
            record.State = CxlFabricResourceState.Draining; // closes admission before provider generation changes
            operations = record.Operations.ToArray();
        }
        try
        {
            foreach (var tracked in operations)
            {
                var providerClosed = false;
                if (tracked.ProviderClosure is not null)
                {
                    var closure = tracked.ProviderClosure();
                    if (!closure.IsSuccess)
                    {
                        lock (_gate) record.State = CxlFabricResourceState.Faulted;
                        return KernelResult<CxlFabricManagedBinding>.Fail(closure.Error, closure.Message!);
                    }
                    providerClosed = true;
                }
                var drained = DrainOperation(tracked.Principal, tracked.Operation, providerClosed);
                if (!drained.IsSuccess)
                {
                    lock (_gate) record.State = CxlFabricResourceState.Faulted;
                    return KernelResult<CxlFabricManagedBinding>.Fail(drained.Error, drained.Message!);
                }
            }
            PlatformAuthorityResult<CxlFabricReconfigurationTicket> begun;
            try
            {
                begun = _provider.BeginReconfiguration(binding);
            }
            catch (Exception exception)
            {
                lock (_gate) record.State = CxlFabricResourceState.Faulted;
                return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.ExternalEffectUncontained,
                    $"Fabric reconfiguration threw after its effect boundary: {exception.Message}");
            }
            if (!begun.IsSuccess)
            {
                lock (_gate) record.State = begun.Status == PlatformAuthorityStatus.NotAccepted
                    ? CxlFabricResourceState.Bound : CxlFabricResourceState.Faulted;
                return KernelResult<CxlFabricManagedBinding>.Fail(Map(begun.Status), begun.Message ?? "Fabric reconfiguration failed to begin.");
            }
            lock (_gate)
            {
                record.Ticket = begun.Value!;
                return KernelResult<CxlFabricManagedBinding>.Ok(Snapshot(record));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            lock (_gate) record.State = CxlFabricResourceState.Faulted;
            return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.ExternalEffectUncontained,
                $"Fabric drain did not prove closure: {exception.Message}");
        }
    }

    public KernelResult<CxlFabricManagedBinding> CompleteReconfiguration(CxlFabricBindingId bindingId)
    {
        BindingRecord record;
        CxlFabricReconfigurationTicket ticket;
        lock (_gate)
        {
            if (!_bindings.TryGetValue(bindingId, out record!) || record.State != CxlFabricResourceState.Draining || record.Ticket is null)
                return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.InvalidTransition, "Fabric binding has no active reconfiguration.");
            if (record.CompletionInFlight)
                return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.PlatformBindingDraining, "Fabric completion callback is already in flight.");
            ticket = record.Ticket;
            record.CompletionInFlight = true;
        }
        try
        {
            PlatformAuthorityResult<CxlFabricBinding> completed;
            PlatformAuthorityResult<CxlFabricResourceSnapshot> observed = default;
            try
            {
                completed = _provider.CompleteReconfiguration(ticket);
                if (completed.IsSuccess) observed = _provider.QueryResource(bindingId);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                lock (_gate) record.State = CxlFabricResourceState.Faulted;
                return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.ExternalEffectUncontained,
                    $"Fabric reconfiguration completion threw after its effect boundary: {exception.Message}");
            }
            lock (_gate)
            {
                if (!_bindings.TryGetValue(bindingId, out var current) || !ReferenceEquals(current, record) ||
                    record.State != CxlFabricResourceState.Draining || record.Ticket != ticket || record.Binding != ticket.PreviousBinding)
                {
                    record.State = CxlFabricResourceState.Faulted;
                    return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.ExternalEffectUncontained, "Fabric completion lost its exact admitted ticket/state.");
                }
                if (!completed.IsSuccess)
                {
                    record.State = CxlFabricResourceState.Faulted;
                    return KernelResult<CxlFabricManagedBinding>.Fail(Map(completed.Status), completed.Message ?? "Fabric reconfiguration completion failed.");
                }
                if (completed.Value!.BindingId != bindingId || completed.Value.Generation != ticket.ReplacementGeneration ||
                    completed.Value.EndpointId != ticket.PreviousBinding.EndpointId ||
                    completed.Value.DeviceGeneration != ticket.PreviousBinding.DeviceGeneration)
                {
                    record.State = CxlFabricResourceState.Faulted;
                    return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.PlatformFaulted, "Provider returned a malformed replacement binding.");
                }
                if (!observed.IsSuccess || observed.Value!.Binding != completed.Value || observed.Value.State != CxlFabricResourceState.Bound)
                {
                    record.State = CxlFabricResourceState.Faulted;
                    return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.ExternalEffectUncontained, "Fabric completion receipt lost provider source continuity.");
                }
                record.Binding = completed.Value;
                record.State = CxlFabricResourceState.Bound;
                record.Ticket = null;
                record.Operations.Clear();
                return KernelResult<CxlFabricManagedBinding>.Ok(Snapshot(record));
            }
        }
        finally { lock (_gate) record.CompletionInFlight = false; }
    }

    public KernelResult<CxlOwnedPoolAllocation> AssignPool(
        CxlFabricPoolId poolId, CxlEndpointId endpointId, RegionHandle region, RegionOwner owner)
    {
        var descriptor = _regions.Validate(region, owner);
        if (!descriptor.IsSuccess) return KernelResult<CxlOwnedPoolAllocation>.Fail(descriptor.Error, descriptor.Message!);
        if (_poolAllocations.Values.Any(allocation => allocation.Region.RegionId == region.RegionId))
            return KernelResult<CxlOwnedPoolAllocation>.Fail(KernelError.PlatformBindingActive, "Region already has a live pool assignment.");
        var use = _regions.AcquireUse(region, owner, RegionUseMode.DevicePrivate, new(0, descriptor.Value!.ByteLength));
        if (!use.IsSuccess) return KernelResult<CxlOwnedPoolAllocation>.Fail(use.Error, use.Message!);
        PlatformAuthorityResult<CxlPoolAssignment> assigned;
        try
        {
            assigned = _provider.AssignPoolCapacity(poolId, endpointId, descriptor.Value!.ByteLength);
        }
        catch (Exception exception)
        {
            _uncontainedPoolAssignments.Add(owner);
            return KernelResult<CxlOwnedPoolAllocation>.Fail(KernelError.ExternalEffectUncontained,
                $"Pool assignment threw after its effect boundary and remains quarantined: {exception.Message}");
        }
        if (!assigned.IsSuccess)
        {
            if (assigned.Status == PlatformAuthorityStatus.NotAccepted)
            {
                _ = _regions.ReleaseUse(use.Value!.Handle, owner);
                return KernelResult<CxlOwnedPoolAllocation>.Fail(Map(assigned.Status), assigned.Message ?? "Pool assignment was rejected before effect.");
            }
            _uncontainedPoolAssignments.Add(owner);
            return KernelResult<CxlOwnedPoolAllocation>.Fail(KernelError.ExternalEffectUncontained,
                assigned.Message ?? "Pool assignment acceptance is ambiguous and remains quarantined.");
        }
        var allocation = new CxlOwnedPoolAllocation(assigned.Value!, region, owner, use.Value!.Handle);
        _poolAllocations.Add(allocation.Assignment.AssignmentId, allocation);
        return KernelResult<CxlOwnedPoolAllocation>.Ok(allocation);
    }

    public KernelResult ReleasePool(CxlOwnedPoolAllocation allocation)
    {
        if (!_poolAllocations.TryGetValue(allocation.Assignment.AssignmentId, out var exact) || exact != allocation)
            return KernelResult.Fail(KernelError.StaleGeneration, "Pool assignment is stale.");
        var region = _regions.Validate(allocation.Region, allocation.Owner);
        if (!region.IsSuccess) return KernelResult.Fail(region.Error, region.Message!);
        var released = _provider.ReleasePoolCapacity(allocation.Assignment);
        if (!released.IsSuccess) return KernelResult.Fail(Map(released.Status), released.Message ?? "Pool release failed.");
        var use = _regions.ReleaseUse(allocation.RegionUse, allocation.Owner);
        if (!use.IsSuccess) return use;
        _poolAllocations.Remove(allocation.Assignment.AssignmentId);
        return KernelResult.Ok();
    }

    public KernelResult<CxlPeerAccessEvidence> ValidatePeerAccess(
        CxlPeerAccessRequest request,
        CxlFabricBinding? initiatorAuthority = null,
        CxlFabricBinding? targetAuthority = null)
    {
        if (initiatorAuthority is null || targetAuthority is null ||
            initiatorAuthority.EndpointId != request.Initiator || targetAuthority.EndpointId != request.Target)
            return KernelResult<CxlPeerAccessEvidence>.Fail(KernelError.PlatformDenied, "Peer access requires exact authority for both fabric bindings.");
        var initiator = ValidateAdmission(initiatorAuthority);
        if (!initiator.IsSuccess) return KernelResult<CxlPeerAccessEvidence>.Fail(initiator.Error, initiator.Message!);
        var target = ValidateAdmission(targetAuthority);
        if (!target.IsSuccess) return KernelResult<CxlPeerAccessEvidence>.Fail(target.Error, target.Message!);
        if (!request.PlatformIsolationMaterialized)
            return KernelResult<CxlPeerAccessEvidence>.Fail(KernelError.PlatformDenied, "Peer access requires materialized platform isolation.");
        var evidence = _provider.QueryPeerAccess(request);
        if (!evidence.IsSuccess) return KernelResult<CxlPeerAccessEvidence>.Fail(Map(evidence.Status), evidence.Message ?? "Peer route query failed.");
        if (!evidence.Value!.RouteSupported || evidence.Value.Initiator != request.Initiator || evidence.Value.Target != request.Target)
            return KernelResult<CxlPeerAccessEvidence>.Fail(KernelError.PlatformUnsupported, "Peer route is unsupported or malformed.");
        return KernelResult<CxlPeerAccessEvidence>.Ok(evidence.Value);
    }

    private KernelResult DrainOperation(ProcessHandle principal, ExternalOperationHandle operation, bool providerClosed)
    {
        var snapshot = _kernel.QueryExternalOperation(principal, operation);
        if (!snapshot.IsSuccess) return KernelResult.Fail(snapshot.Error, snapshot.Message!);
        if (snapshot.Value!.State == ExternalOperationState.Released) return KernelResult.Ok();
        if (snapshot.Value.State is ExternalOperationState.Prepared or ExternalOperationState.Admitted)
        {
            var cancelled = _kernel.CancelExternalOperation(principal, operation, false);
            if (!cancelled.IsSuccess) return KernelResult.Fail(cancelled.Error, cancelled.Message!);
            // The operation owner decides whether submission occurred; this snapshot is no closure proof.
            var releasedPreSubmit = _kernel.ReleaseExternalOperation(principal, operation, new(false, false));
            return releasedPreSubmit.IsSuccess ? KernelResult.Ok()
                : KernelResult.Fail(releasedPreSubmit.Error, releasedPreSubmit.Message!);
        }
        if (!providerClosed)
            return KernelResult.Fail(KernelError.ExternalEffectUncontained,
                "Post-submit fabric drain requires verified provider closure or effect containment.");
        if (snapshot.Value.State == ExternalOperationState.Published)
        {
            var releasedPublished = _kernel.ReleaseExternalOperation(principal, operation, new(true, false));
            return releasedPublished.IsSuccess
                ? KernelResult.Ok()
                : KernelResult.Fail(releasedPublished.Error, releasedPublished.Message!);
        }
        var lost = _kernel.RecordExternalOperationProviderLoss(principal, operation);
        if (!lost.IsSuccess) return KernelResult.Fail(lost.Error, lost.Message!);
        if (snapshot.Value.PublicationPolicy != ExternalPublicationPolicy.Staged) return KernelResult.Ok();
        var released = _kernel.ReleaseExternalOperation(principal, operation, new(true, false));
        return released.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(released.Error, released.Message!);
    }

    private static CxlFabricManagedBinding Snapshot(BindingRecord record) =>
        new(record.Binding, record.State, record.Ticket?.ReconfigurationId);

    KernelResult ICxlTeardownParticipant.CloseForProcess(ProcessHandle process, RegionOwner owner)
    {
        if (_uncontainedPoolAssignments.Contains(owner))
            return KernelResult.Fail(KernelError.ExternalEffectUncontained,
                "A pool assignment has ambiguous acceptance and reclaim remains quarantined.");
        foreach (var allocation in _poolAllocations.Values.Where(item => item.Owner == owner).ToArray())
        {
            var released = ReleasePool(allocation);
            if (!released.IsSuccess) return released;
        }
        return KernelResult.Ok();
    }
    private static KernelError Map(PlatformAuthorityStatus status) => status switch
    {
        PlatformAuthorityStatus.NotAccepted => KernelError.PlatformUnavailable,
        PlatformAuthorityStatus.Unavailable => KernelError.PlatformUnavailable,
        PlatformAuthorityStatus.Unsupported => KernelError.PlatformUnsupported,
        PlatformAuthorityStatus.Stale => KernelError.StaleGeneration,
        PlatformAuthorityStatus.Revoked => KernelError.PlatformBindingRevoked,
        PlatformAuthorityStatus.Denied => KernelError.PlatformDenied,
        _ => KernelError.PlatformFaulted
    };
}
