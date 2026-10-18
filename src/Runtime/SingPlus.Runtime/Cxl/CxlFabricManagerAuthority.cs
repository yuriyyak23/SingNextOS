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
    private sealed class BindingRecord
    {
        public BindingRecord(CxlFabricBinding binding) { OwnerSelf = this; Binding = binding; State = CxlFabricResourceState.Bound; }
        public BindingRecord OwnerSelf { get; }
        private CxlFabricBinding _binding = null!;
        private CxlFabricResourceState _state;
        public CxlFabricBinding Binding { get => _binding; set { Advance(); _binding = value; } }
        public CxlFabricResourceState State { get => _state; set { Advance(); _state = value; } }
        public ulong SourceRevision { get; private set; }
        public bool RevisionExhausted { get; private set; }
        private void Advance() { if (SourceRevision == ulong.MaxValue) RevisionExhausted = true; else SourceRevision++; }
        private CxlFabricReconfigurationTicket? _ticket;
        public CxlFabricReconfigurationTicket? Ticket { get => _ticket; set { Advance(); _ticket = value; } }
        public int PendingAdmissions { get; set; }
        public bool CompletionInFlight { get; set; }
        public bool BeginInFlight { get; set; }
        public BeginInvocation? Begin { get; set; }
        public CompletionInvocation? Completion { get; set; }
        public List<BeginInvocation> BeginHistory { get; } = [];
        public List<CompletionInvocation> CompletionHistory { get; } = [];
        public CxlFabricBinding? LastCompletionReceipt { get; set; }
        public List<(ProcessHandle Principal, ExternalOperationHandle Operation, Func<KernelResult>? ProviderClosure)> Operations { get; } = [];
    }
    private sealed class BeginInvocation
    {
        public BeginInvocation(BindingRecord source, CxlFabricBinding binding, ulong revision)
        { OwnerSelf = this; Source = source; Binding = binding; Revision = revision; }
        public BeginInvocation OwnerSelf { get; }
        public BindingRecord Source { get; }
        public CxlFabricBinding Binding { get; }
        public ulong Revision { get; }
        public bool Invoked { get; set; }
        public bool NoEffectConsumed { get; set; }
        public bool Completed { get; set; }
        public ulong? OwnFaultRevision { get; set; }
        public PlatformAuthorityResult<CxlFabricReconfigurationTicket>? RawObservation { get; set; }
    }
    private sealed class CompletionInvocation
    {
        public CompletionInvocation(BindingRecord source, BeginInvocation begin, CxlFabricReconfigurationTicket ticket, ulong revision)
        { OwnerSelf = this; Source = source; Begin = begin; Ticket = ticket; Revision = revision; }
        public CompletionInvocation OwnerSelf { get; }
        public BindingRecord Source { get; }
        public BeginInvocation Begin { get; }
        public CxlFabricReconfigurationTicket Ticket { get; }
        public ulong Revision { get; }
        public bool Invoked { get; set; }
        public bool NoEffectConsumed { get; set; }
        public bool Consumed { get; set; }
        public ulong? OwnFaultRevision { get; set; }
        public PlatformAuthorityResult<CxlFabricBinding>? RawObservation { get; set; }
    }
    private readonly List<BindingRecord> _bindingOwners = [];
    private bool IsOriginalBinding(BindingRecord record) => ReferenceEquals(record.OwnerSelf, record) &&
        _bindingOwners.Any(owner => ReferenceEquals(owner, record)) &&
        _bindings.TryGetValue(record.Binding.BindingId, out var routed) && ReferenceEquals(routed, record);
    private sealed class PoolRecord(CxlOwnedPoolAllocation allocation)
    {
        public CxlOwnedPoolAllocation Allocation { get; } = allocation;
        public bool ReleaseInFlight { get; set; }
        public bool ReceiptValidated { get; set; }
        public bool Published { get; set; }
        public CxlPoolAssignment? ReleasedReceipt { get; set; }
    }
    internal Action? BeforePoolUseRelease { get; set; }
    private readonly RuntimeKernel _kernel;
    private readonly RegionAuthority _regions;
    private readonly ICxlFabricManagementProvider _provider;
    private readonly Dictionary<CxlFabricBindingId, BindingRecord> _bindings = [];
    private readonly Dictionary<RegionHandle, PoolRecord> _poolAllocations = [];
    private readonly HashSet<RegionOwner> _uncontainedPoolAssignments = [];
    private readonly Dictionary<RegionHandle, RegionOwner> _pendingPoolAssignments = [];
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
            _bindingOwners.EnsureCapacity(_bindingOwners.Count + 1);
            _bindingOwners.Add(record);
            _bindings.Add(binding.BindingId, record);
            return KernelResult<CxlFabricManagedBinding>.Ok(Snapshot(record));
        }
    }

    private KernelResult<(BindingRecord Source, ulong Revision)> PrepareAdmission(CxlFabricBinding binding)
    {
        BindingRecord record; ulong revision;
        lock (_gate)
        {
            var local = ValidateAdmissionCore(binding);
            if (!local.IsSuccess) return KernelResult<(BindingRecord, ulong)>.Fail(local.Error, local.Message!);
            record = _bindings[binding.BindingId]; revision = record.SourceRevision;
        }
        var current = _provider.QueryResource(binding.BindingId);
        if (!current.IsSuccess || current.Value!.Binding != binding || current.Value.State != CxlFabricResourceState.Bound)
            return KernelResult<(BindingRecord, ulong)>.Fail(current.Status == PlatformAuthorityStatus.Stale ? KernelError.StaleGeneration : Map(current.Status), current.Message ?? "Fabric provider state changed.");
        lock (_gate)
        {
            var local = ValidateAdmissionCore(binding);
            if (!local.IsSuccess) return KernelResult<(BindingRecord, ulong)>.Fail(local.Error, local.Message!);
            if (!IsOriginalBinding(record) || record.SourceRevision != revision)
                return KernelResult<(BindingRecord, ulong)>.Fail(KernelError.StaleGeneration, "Original fabric owner changed across admission query.");
            return KernelResult<(BindingRecord, ulong)>.Ok((record, revision));
        }
    }
    internal readonly record struct EffectSource(object OriginalOwner, ulong Revision, CxlFabricBinding Binding);
    internal KernelResult<EffectSource> CaptureEffectSource(CxlFabricBinding binding)
    {
        lock (_gate)
        {
            var admitted = ValidateAdmissionCore(binding);
            if (!admitted.IsSuccess) return KernelResult<EffectSource>.Fail(admitted.Error, admitted.Message!);
            var record = _bindings[binding.BindingId];
            return KernelResult<EffectSource>.Ok(new(record, record.SourceRevision, record.Binding));
        }
    }
    internal KernelResult CommitEffectSource(EffectSource source, Func<KernelResult> commit)
    {
        lock (_gate)
        {
            if (source.OriginalOwner is not BindingRecord record || !IsOriginalBinding(record) ||
                record.RevisionExhausted || record.SourceRevision != source.Revision || record.Binding != source.Binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "Original FM source changed before consumer commit.");
            var admitted = ValidateAdmissionCore(source.Binding);
            return admitted.IsSuccess ? commit() : admitted; // trusted local writer; no callback/provider queries
        }
    }
    public KernelResult ValidateAdmission(CxlFabricBinding binding)
    {
        var source = PrepareAdmission(binding);
        return source.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(source.Error, source.Message!);
    }

    private KernelResult ValidateAdmissionCore(CxlFabricBinding binding)
    {
        if (!_bindings.TryGetValue(binding.BindingId, out var record))
            return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Fabric binding is not managed.");
        if (!IsOriginalBinding(record) || record.RevisionExhausted)
            return KernelResult.Fail(KernelError.StaleGeneration, "Original fabric owner is unavailable or exhausted.");
        if (record.Begin is { Invoked: true, NoEffectConsumed: false, Completed: false } || record.BeginInFlight || record.CompletionInFlight)
            return KernelResult.Fail(KernelError.PlatformBindingDraining, "Original reconfiguration invocation remains pending.");
        if (record.State != CxlFabricResourceState.Bound)
            return KernelResult.Fail(KernelError.PlatformBindingDraining, "Fabric binding is draining or faulted; new admission is closed.");
        if (record.Binding != binding)
            return KernelResult.Fail(KernelError.StaleGeneration, "Fabric binding generation is stale.");
        return KernelResult.Ok();
    }

    public KernelResult TrackOperation(CxlFabricBinding binding, ProcessHandle principal, ExternalOperationHandle operation,
        Func<KernelResult>? providerClosure = null)
    {
        var fresh = PrepareAdmission(binding);
        if (!fresh.IsSuccess) return KernelResult.Fail(fresh.Error, fresh.Message!);
        var snapshot = _kernel.QueryExternalOperation(principal, operation);
        if (!snapshot.IsSuccess) return KernelResult.Fail(snapshot.Error, snapshot.Message!);
        lock (_gate)
        {
            var admission = ValidateAdmissionCore(binding);
            if (!admission.IsSuccess) return admission;
            if (!IsOriginalBinding(fresh.Value.Source) || fresh.Value.Source.SourceRevision != fresh.Value.Revision)
                return KernelResult.Fail(KernelError.StaleGeneration, "Original tracking source changed.");
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
        // Released is a terminal local operation lifetime, independently owned by
        // the existing operation authority. It is not a provider closure receipt.
        var lifetime = _kernel.QueryExternalOperation(principal, operation);
        if (!lifetime.IsSuccess) return KernelResult.Fail(lifetime.Error, lifetime.Message!);
        if (lifetime.Value!.State != ExternalOperationState.Released)
            return KernelResult.Fail(KernelError.ExternalEffectUncontained,
                "A live or unresolved operation must retain its fabric dependency.");
        lock (_gate)
        {
            if (!_bindings.TryGetValue(binding.BindingId, out var record))
                return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Fabric binding is not managed.");
            if (!IsOriginalBinding(record) || record.Binding != binding)
                return KernelResult.Fail(KernelError.StaleGeneration, "Original fabric binding generation is stale.");
            record.Operations.RemoveAll(item => item.Principal == principal && item.Operation == operation);
            return KernelResult.Ok();
        }
    }

    public KernelResult<T> ExecuteAdmission<T>(CxlFabricBinding binding, Func<KernelResult<T>> providerEffect)
    {
        ArgumentNullException.ThrowIfNull(providerEffect);
        var fresh = PrepareAdmission(binding);
        if (!fresh.IsSuccess) return KernelResult<T>.Fail(fresh.Error, fresh.Message!);
        BindingRecord record;
        lock (_gate)
        {
            var admission = ValidateAdmissionCore(binding);
            if (!admission.IsSuccess) return KernelResult<T>.Fail(admission.Error, admission.Message!);
            record = fresh.Value.Source;
            if (!IsOriginalBinding(record) || record.SourceRevision != fresh.Value.Revision)
                return KernelResult<T>.Fail(KernelError.StaleGeneration, "Original fabric admission source changed.");
            if (record.PendingAdmissions == int.MaxValue)
                return KernelResult<T>.Fail(KernelError.CapacityExhausted, "Fabric admission interlock is saturated.");
            record.PendingAdmissions++;
        }
        try
        {
            var result = providerEffect(); // actual consumer owns raw outcome before this boundary
            lock (_gate)
            {
                if (!IsOriginalBinding(record) || record.SourceRevision != fresh.Value.Revision || record.RevisionExhausted)
                    return KernelResult<T>.Fail(KernelError.ExternalEffectUncontained, "Actual fabric effect finished after original local source changed; cleanup facts remain at its consumer.");
            }
            return result;
        }
        finally { lock (_gate) record.PendingAdmissions--; }
    }

    private void RetainOriginalBeginFault(BindingRecord record, BeginInvocation fact)
    {
        lock (_gate)
        {
            var own = ReferenceEquals(record.OwnerSelf, record) && _bindingOwners.Any(value => ReferenceEquals(value, record)) &&
                ReferenceEquals(record.Begin, fact) && ReferenceEquals(fact.OwnerSelf, fact) && ReferenceEquals(fact.Source, record) &&
                record.Binding == fact.Binding && (record.SourceRevision == fact.Revision || record.SourceRevision == fact.OwnFaultRevision) &&
                fact.RawObservation is { } raw && (raw.IsSuccess || raw.Status == PlatformAuthorityStatus.NotAccepted);
            record.State = CxlFabricResourceState.Faulted;
            if (own && !record.RevisionExhausted) fact.OwnFaultRevision = record.SourceRevision;
        }
    }
    private KernelResult RestoreOriginalBegin(CxlFabricBindingId bindingId)
    {
        BindingRecord record; BeginInvocation fact; ulong revision;
        (ProcessHandle Principal, ExternalOperationHandle Operation, Func<KernelResult>? ProviderClosure)[] dependencies;
        lock (_gate)
        {
            if (!_bindings.TryGetValue(bindingId, out record!)) return KernelResult.Ok();
            if (!IsOriginalBinding(record)) return KernelResult.Fail(KernelError.StaleGeneration, "Original FM route was substituted.");
            if (record.Begin is not { } pending || pending.NoEffectConsumed || pending.Completed || record.Ticket is not null) return KernelResult.Ok();
            fact = pending;
            if (record.BeginInFlight || record.CompletionInFlight || record.PendingAdmissions != 0)
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Original Begin observation is in flight.");
            if (!ReferenceEquals(fact.OwnerSelf, fact) || !ReferenceEquals(fact.Source, record) || record.Binding != fact.Binding || record.RevisionExhausted ||
                !(record.State == CxlFabricResourceState.Draining && record.SourceRevision == fact.Revision ||
                  record.State == CxlFabricResourceState.Faulted && record.SourceRevision == fact.OwnFaultRevision))
                return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Independent Begin source loss remains pinned.");
            if (fact.Invoked && fact.RawObservation is not { IsSuccess: true } && fact.RawObservation is not { Status: PlatformAuthorityStatus.NotAccepted })
                return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Original Begin outcome is unknown; no repeat.");
            revision = record.SourceRevision;
            dependencies = fact.Invoked ? [] : record.Operations.ToArray();
            record.BeginInFlight = true;
        }
        try
        {
            var observed = _provider.QueryResource(bindingId);
            // Caller never-invoked does not erase an unresolved dependent operation.
            foreach (var operation in dependencies)
            {
                var state = _kernel.QueryExternalOperation(operation.Principal, operation.Operation);
                if (!state.IsSuccess || state.Value!.State != ExternalOperationState.Released)
                    return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Original dependent operation has not closed.");
            }
            lock (_gate)
            {
                if (!IsOriginalBinding(record) || !ReferenceEquals(record.Begin, fact) || !ReferenceEquals(fact.OwnerSelf, fact) ||
                    record.SourceRevision != revision || record.RevisionExhausted || !observed.IsSuccess || observed.Value!.Binding != fact.Binding)
                    return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Original Begin source cannot be reconciled.");
                if (!fact.Invoked || fact.RawObservation is { Status: PlatformAuthorityStatus.NotAccepted })
                {
                    if (observed.Value.State != CxlFabricResourceState.Bound)
                        return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Original no-effect lacks current Bound source.");
                    fact.NoEffectConsumed = true; record.State = CxlFabricResourceState.Bound;
                    return KernelResult.Ok();
                }
                var ticket = fact.RawObservation!.Value.Value;
                if (ticket is null || ticket.PreviousBinding != fact.Binding || ticket.ReconfigurationId.Value == 0 ||
                    ticket.ReplacementGeneration.Value <= fact.Binding.Generation.Value || observed.Value.State != CxlFabricResourceState.Draining)
                    return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Original Begin ticket/source is malformed or unavailable.");
                record.Ticket = ticket; record.State = CxlFabricResourceState.Draining;
                return KernelResult.Ok(); // cleanup-only ticket handoff; admission remains closed
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { return KernelResult.Fail(KernelError.ExternalEffectUncontained, $"Original Begin observation unavailable: {exception.Message}"); }
        finally { lock (_gate) record.BeginInFlight = false; }
    }
    public KernelResult<CxlFabricManagedBinding> BeginReconfiguration(CxlFabricBinding binding)
    {
        var restored = RestoreOriginalBegin(binding.BindingId);
        if (!restored.IsSuccess) return KernelResult<CxlFabricManagedBinding>.Fail(restored.Error, restored.Message!);
        var fresh = PrepareAdmission(binding);
        if (!fresh.IsSuccess) return KernelResult<CxlFabricManagedBinding>.Fail(fresh.Error, fresh.Message!);
        BindingRecord record;
        BeginInvocation fact;
        (ProcessHandle Principal, ExternalOperationHandle Operation, Func<KernelResult>? ProviderClosure)[] operations;
        lock (_gate)
        {
            var admission = ValidateAdmissionCore(binding);
            if (!admission.IsSuccess) return KernelResult<CxlFabricManagedBinding>.Fail(admission.Error, admission.Message!);
            record = fresh.Value.Source;
            if (!IsOriginalBinding(record) || record.SourceRevision != fresh.Value.Revision)
                return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.StaleGeneration, "Original fabric Begin source changed.");
            if (record.PendingAdmissions != 0)
                return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.PlatformBindingDraining, "Fabric creation callbacks must settle before reconfiguration.");
            operations = record.Operations.ToArray();
        }
        foreach (var tracked in operations)
        {
            var pending = _kernel.ExternalOperations.ValidateFabricDrainPreflight(tracked.Operation);
            if (!pending.IsSuccess && pending.Error == KernelError.PlatformBindingDraining)
                return KernelResult<CxlFabricManagedBinding>.Fail(pending.Error, pending.Message!);
            // Other missing/foreign dependencies are diagnosed after admission closes.
            // An unavailable tracked owner cannot leave this fabric open to new work.
        }
        lock (_gate)
        {
            if (!IsOriginalBinding(record) || record.SourceRevision != fresh.Value.Revision || record.PendingAdmissions != 0 ||
                !record.Operations.SequenceEqual(operations))
                return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.StaleGeneration, "Original drain inventory changed during preflight.");
            record.State = CxlFabricResourceState.Draining; // closes admission before provider generation changes
            fact = new(record, record.Binding, record.SourceRevision);
            record.BeginHistory.Add(fact); record.Begin = fact;
            record.BeginInFlight = true;
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
                        RetainOriginalBeginFault(record, fact);
                        return KernelResult<CxlFabricManagedBinding>.Fail(closure.Error, closure.Message!);
                    }
                    providerClosed = true;
                }
                var drained = DrainOperation(tracked.Principal, tracked.Operation, providerClosed);
                if (!drained.IsSuccess)
                {
                    RetainOriginalBeginFault(record, fact);
                    return KernelResult<CxlFabricManagedBinding>.Fail(drained.Error, drained.Message!);
                }
            }
            PlatformAuthorityResult<CxlFabricReconfigurationTicket> begun;
            try
            {
                var preSource = _provider.QueryResource(binding.BindingId);
                if (!preSource.IsSuccess || preSource.Value!.Binding != fact.Binding || preSource.Value.State != CxlFabricResourceState.Bound)
                    return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.ExternalEffectUncontained, "Original Begin source stopped before effect.");
                lock (_gate)
                {
                    if (!IsOriginalBinding(record) || record.RevisionExhausted || !ReferenceEquals(fact.OwnerSelf, fact) ||
                        !ReferenceEquals(record.Begin, fact) || record.SourceRevision != fact.Revision ||
                        record.State != CxlFabricResourceState.Draining || record.Binding != fact.Binding || record.Ticket is not null || fact.Invoked)
                        return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.StaleGeneration, "Original fabric Begin stopped before effect.");
                    fact.Invoked = true;
                }
                begun = _provider.BeginReconfiguration(fact.Binding);
                lock (_gate) fact.RawObservation = begun;
            }
            catch (Exception exception)
            {
                RetainOriginalBeginFault(record, fact);
                return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.ExternalEffectUncontained,
                    $"Fabric reconfiguration threw after its effect boundary: {exception.Message}");
            }
            if (!begun.IsSuccess)
            {
                var rejectedSourceCurrent = false;
                if (begun.Status == PlatformAuthorityStatus.NotAccepted)
                {
                    var source = _provider.QueryResource(binding.BindingId); // outside owner gate
                    rejectedSourceCurrent = source.IsSuccess && source.Value!.Binding == binding &&
                        source.Value.State == CxlFabricResourceState.Bound;
                }
                lock (_gate)
                {
                    if (begun.Status == PlatformAuthorityStatus.NotAccepted &&
                        (!rejectedSourceCurrent || !_bindings.TryGetValue(binding.BindingId, out var current) ||
                         !IsOriginalBinding(record) || !ReferenceEquals(current, record) || record.SourceRevision != fact.Revision || record.Binding != binding ||
                         record.State != CxlFabricResourceState.Draining || record.Ticket is not null))
                    {
                        RetainOriginalBeginFault(record, fact);
                        return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.ExternalEffectUncontained,
                            "Rejected reconfiguration did not preserve its exact provider/local source.");
                    }
                    if (rejectedSourceCurrent) fact.NoEffectConsumed = true;
                    record.State = rejectedSourceCurrent ? CxlFabricResourceState.Bound : CxlFabricResourceState.Faulted;
                }
                return KernelResult<CxlFabricManagedBinding>.Fail(Map(begun.Status), begun.Message ?? "Fabric reconfiguration failed to begin.");
            }
            lock (_gate)
            {
                if (!IsOriginalBinding(record) || !ReferenceEquals(record.Begin, fact) || !ReferenceEquals(fact.OwnerSelf, fact) ||
                    record.SourceRevision != fact.Revision || record.State != CxlFabricResourceState.Draining || record.Binding != fact.Binding)
                {
                    RetainOriginalBeginFault(record, fact);
                    return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.ExternalEffectUncontained, "Original Begin outcome retained after local source loss.");
                }
                record.Ticket = begun.Value!; // raw already retained; this is local publication
                if (begun.Value!.ReconfigurationId.Value == 0 || begun.Value.PreviousBinding != binding ||
                    begun.Value.ReplacementGeneration.Value <= binding.Generation.Value)
                {
                    RetainOriginalBeginFault(record, fact);
                    return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.ExternalEffectUncontained,
                        "Fabric reconfiguration ticket does not preserve the exact source and forward generation.");
                }
                return KernelResult<CxlFabricManagedBinding>.Ok(Snapshot(record));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            RetainOriginalBeginFault(record, fact);
            return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.ExternalEffectUncontained,
                $"Fabric drain did not prove closure: {exception.Message}");
        }
        finally { lock (_gate) record.BeginInFlight = false; }
    }

    public KernelResult<CxlFabricManagedBinding> CompleteReconfiguration(CxlFabricBindingId bindingId)
    {
        var restored = RestoreOriginalBegin(bindingId);
        if (!restored.IsSuccess) return KernelResult<CxlFabricManagedBinding>.Fail(restored.Error, restored.Message!);
        BindingRecord record;
        CompletionInvocation fact;
        bool OriginalFact() => IsOriginalBinding(record) && ReferenceEquals(fact.OwnerSelf, fact) &&
            ReferenceEquals(fact.Source, record) && ReferenceEquals(record.Completion, fact) &&
            record.CompletionHistory.Any(value => ReferenceEquals(value, fact)) &&
            ReferenceEquals(record.Begin, fact.Begin) && ReferenceEquals(fact.Begin.OwnerSelf, fact.Begin) &&
            ReferenceEquals(fact.Begin.Source, record) && fact.Begin.Invoked &&
            fact.Begin.RawObservation is { IsSuccess: true } begun && ReferenceEquals(begun.Value, fact.Ticket) &&
            ReferenceEquals(record.Ticket, fact.Ticket) && record.Binding == fact.Ticket.PreviousBinding;
        bool CurrentSource() => !record.RevisionExhausted &&
            (record.State == CxlFabricResourceState.Draining && record.SourceRevision == fact.Revision ||
             record.State == CxlFabricResourceState.Faulted && fact.OwnFaultRevision == record.SourceRevision);
        void RetainOwnFault()
        {
            lock (_gate)
            {
                var own = OriginalFact() && CurrentSource() && fact.RawObservation is { } raw &&
                    (raw.IsSuccess || raw.Status == PlatformAuthorityStatus.NotAccepted);
                record.State = CxlFabricResourceState.Faulted;
                if (own && !record.RevisionExhausted) fact.OwnFaultRevision = record.SourceRevision;
            }
        }
        lock (_gate)
        {
            if (!_bindings.TryGetValue(bindingId, out record!) || !IsOriginalBinding(record) || record.Begin is not { } begin ||
                !ReferenceEquals(begin.OwnerSelf, begin) || !ReferenceEquals(begin.Source, record) || !begin.Invoked ||
                begin.RawObservation is not { IsSuccess: true } begun || begun.Value is not { } ticket || !ReferenceEquals(record.Ticket, ticket))
                return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.InvalidTransition, "Original fabric binding has no admitted reconfiguration ticket.");
            if (record.CompletionInFlight || record.BeginInFlight)
                return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.PlatformBindingDraining, "Fabric callback is already in flight.");
            if (record.Completion is null || record.Completion.NoEffectConsumed)
            {
                if (record.State != CxlFabricResourceState.Draining || record.RevisionExhausted)
                    return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.InvalidTransition, "Fabric completion requires the original draining source.");
                record.Completion = new(record, begin, ticket, record.SourceRevision);
                record.CompletionHistory.Add(record.Completion);
            }
            fact = record.Completion;
            if (!OriginalFact()) return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.InvalidTransition, "Original completion fact was substituted.");
            if (fact.Invoked && fact.RawObservation is not { IsSuccess: true } &&
                fact.RawObservation is not { Status: PlatformAuthorityStatus.NotAccepted })
                return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.ExternalEffectUncontained, "Original Complete outcome is unknown; no repeat.");
            if (!CurrentSource()) return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.InvalidTransition, "Independent fabric source loss prevents completion consumption.");
            record.CompletionInFlight = true;
        }
        try
        {
            if (!fact.Invoked)
            {
                lock (_gate)
                {
                    if (!OriginalFact() || !CurrentSource()) return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.StaleGeneration, "Completion stopped before effect.");
                    fact.Invoked = true;
                }
                var completed = _provider.CompleteReconfiguration(fact.Ticket);
                lock (_gate)
                {
                    fact.RawObservation = completed;
                    if (completed.IsSuccess) record.LastCompletionReceipt = completed.Value;
                }
            }
            var raw = fact.RawObservation;
            if (raw is null) { RetainOwnFault(); return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.ExternalEffectUncontained, "Original completion response is absent."); }
            if (!raw.Value.IsSuccess && raw.Value.Status != PlatformAuthorityStatus.NotAccepted)
            { RetainOwnFault(); return KernelResult<CxlFabricManagedBinding>.Fail(Map(raw.Value.Status), raw.Value.Message ?? "Original completion remains pinned."); }
            var observed = _provider.QueryResource(bindingId); // pure fresh source observation, never another Complete
            lock (_gate)
            {
                if (!OriginalFact() || !CurrentSource())
                { RetainOwnFault(); return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.ExternalEffectUncontained, "Original completion lost its local source."); }
                if (!raw.Value.IsSuccess)
                {
                    if (!observed.IsSuccess || observed.Value!.Binding != fact.Ticket.PreviousBinding || observed.Value.State != CxlFabricResourceState.Draining)
                    { RetainOwnFault(); return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.ExternalEffectUncontained, "Exact no-effect completion lacks current draining source."); }
                    fact.NoEffectConsumed = true; record.State = CxlFabricResourceState.Draining;
                    return KernelResult<CxlFabricManagedBinding>.Fail(Map(raw.Value.Status), raw.Value.Message ?? "Exact no-effect consumed; a fresh completion may be admitted.");
                }
                var binding = raw.Value.Value;
                if (binding is null || binding.BindingId != bindingId || binding.Generation != fact.Ticket.ReplacementGeneration ||
                    binding.EndpointId != fact.Ticket.PreviousBinding.EndpointId || binding.DeviceGeneration != fact.Ticket.PreviousBinding.DeviceGeneration)
                { RetainOwnFault(); return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.PlatformFaulted, "Malformed original replacement binding."); }
                if (!observed.IsSuccess || observed.Value!.Binding != binding || observed.Value.State != CxlFabricResourceState.Bound)
                { RetainOwnFault(); return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.ExternalEffectUncontained, "Known completion lost provider source continuity."); }
                fact.Consumed = true; fact.Begin.Completed = true;
                record.Binding = binding; record.State = CxlFabricResourceState.Bound; record.Ticket = null;
                record.Operations.Clear();
                return KernelResult<CxlFabricManagedBinding>.Ok(Snapshot(record));
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            RetainOwnFault();
            return KernelResult<CxlFabricManagedBinding>.Fail(KernelError.ExternalEffectUncontained, $"Original completion remains pinned: {exception.Message}");
        }
        finally { lock (_gate) record.CompletionInFlight = false; }
    }

    public KernelResult<CxlOwnedPoolAllocation> AssignPool(
        CxlFabricPoolId poolId, CxlEndpointId endpointId, RegionHandle region, RegionOwner owner)
    {
        var descriptor = _regions.Validate(region, owner);
        if (!descriptor.IsSuccess) return KernelResult<CxlOwnedPoolAllocation>.Fail(descriptor.Error, descriptor.Message!);
        lock (_gate)
        {
            if (_poolAllocations.Values.Any(allocation => allocation.Allocation.Region.RegionId == region.RegionId) ||
                _pendingPoolAssignments.Keys.Any(pending => pending.RegionId == region.RegionId))
                return KernelResult<CxlOwnedPoolAllocation>.Fail(KernelError.PlatformBindingActive, "Region already has a live or pending pool assignment.");
            _pendingPoolAssignments.Add(region, owner);
        }
        try
        {
        var use = _regions.AcquireUse(region, owner, RegionUseMode.DevicePrivate, new(0, descriptor.Value!.ByteLength));
        if (!use.IsSuccess) return KernelResult<CxlOwnedPoolAllocation>.Fail(use.Error, use.Message!);
        PlatformAuthorityResult<CxlPoolAssignment> assigned;
        try
        {
            assigned = _provider.AssignPoolCapacity(poolId, endpointId, descriptor.Value!.ByteLength);
        }
        catch (Exception exception)
        {
            lock (_gate) _uncontainedPoolAssignments.Add(owner);
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
            lock (_gate) _uncontainedPoolAssignments.Add(owner);
            return KernelResult<CxlOwnedPoolAllocation>.Fail(KernelError.ExternalEffectUncontained,
                assigned.Message ?? "Pool assignment acceptance is ambiguous and remains quarantined.");
        }
        if (assigned.Value is not { } receipt)
        {
            lock (_gate) _uncontainedPoolAssignments.Add(owner);
            return KernelResult<CxlOwnedPoolAllocation>.Fail(KernelError.ExternalEffectUncontained, "Pool success returned no receipt.");
        }
        var allocation = new CxlOwnedPoolAllocation(receipt, region, owner, use.Value!.Handle);
        lock (_gate)
        {
            var collision = _poolAllocations.Values.Any(item => item.Allocation.Assignment.AssignmentId == receipt.AssignmentId);
            var record = new PoolRecord(allocation);
            _poolAllocations.Add(region, record); // retained exact returned cleanup fact before publication
            if (receipt.AssignmentId.Value == 0 || receipt.Generation == 0 || receipt.PoolId != poolId ||
                receipt.EndpointId != endpointId || receipt.CapacityBytes != descriptor.Value!.ByteLength || collision)
            {
                _uncontainedPoolAssignments.Add(owner);
                return KernelResult<CxlOwnedPoolAllocation>.Fail(KernelError.ExternalEffectUncontained,
                    "Pool assignment receipt does not preserve the exact request or collides with an existing owner.");
            }
            record.ReceiptValidated = true;
        }
        var published = _regions.CommitPoolUsePublication(allocation.RegionUse, owner, () =>
        {
            lock (_gate)
            {
                if (!_poolAllocations.TryGetValue(region, out var current) || current.Allocation != allocation ||
                    !current.ReceiptValidated || current.ReleaseInFlight || current.ReleasedReceipt is not null)
                    return KernelResult.Fail(KernelError.StaleGeneration, "Pool publication lost its exact tracked receipt/lifetime.");
                current.Published = true;
                return KernelResult.Ok();
            }
        });
        return published.IsSuccess ? KernelResult<CxlOwnedPoolAllocation>.Ok(allocation)
            : KernelResult<CxlOwnedPoolAllocation>.Fail(published.Error, published.Message!);
        }
        finally { lock (_gate) _pendingPoolAssignments.Remove(region); }
    }

    public KernelResult ReleasePool(CxlOwnedPoolAllocation allocation)
    {
        PoolRecord record;
        bool providerClosed;
        lock (_gate)
        {
            if (!_poolAllocations.TryGetValue(allocation.Region, out record!) || record.Allocation != allocation)
                return KernelResult.Fail(KernelError.StaleGeneration, "Pool assignment is stale.");
            if (!record.ReceiptValidated)
                return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Malformed pool receipt requires exact reconciliation before release.");
            if (record.ReleaseInFlight)
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Exact pool release is already in flight.");
            record.ReleaseInFlight = true;
            providerClosed = record.ReleasedReceipt == allocation.Assignment;
        }
        try
        {
            var region = _regions.Validate(allocation.Region, allocation.Owner);
            if (!region.IsSuccess) return KernelResult.Fail(region.Error, region.Message!);
            if (!providerClosed)
            {
                var released = _provider.ReleasePoolCapacity(allocation.Assignment);
                if (!released.IsSuccess) return KernelResult.Fail(Map(released.Status), released.Message ?? "Pool release failed.");
                lock (_gate) record.ReleasedReceipt = allocation.Assignment; // exact successful closure fact only
            }
            BeforePoolUseRelease?.Invoke(); // outside manager/Region gates
            var use = _regions.ReleaseUse(allocation.RegionUse, allocation.Owner);
            if (!use.IsSuccess) return use;
            lock (_gate)
            {
                if (!_poolAllocations.TryGetValue(allocation.Region, out var current) ||
                    !ReferenceEquals(current, record))
                    return KernelResult.Fail(KernelError.StaleGeneration, "Pool cleanup lost its exact owner record.");
                _poolAllocations.Remove(allocation.Region);
            }
            return KernelResult.Ok();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return KernelResult.Fail(KernelError.ExternalEffectUncontained,
                $"Pool release did not complete local cleanup: {exception.Message}");
        }
        finally { lock (_gate) record.ReleaseInFlight = false; }
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
        CxlOwnedPoolAllocation[] allocations;
        lock (_gate)
        {
            if (_pendingPoolAssignments.Values.Contains(owner))
                return KernelResult.Fail(KernelError.PlatformBindingDraining,
                    "Pool assignment callback and receipt tracking must finish before teardown.");
            if (_uncontainedPoolAssignments.Contains(owner))
                return KernelResult.Fail(KernelError.ExternalEffectUncontained,
                    "A pool assignment has ambiguous acceptance and reclaim remains quarantined.");
            allocations = _poolAllocations.Values.Where(item => item.Allocation.Owner == owner).Select(item => item.Allocation).ToArray();
        }
        foreach (var allocation in allocations)
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
