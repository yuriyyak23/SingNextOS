using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal readonly record struct V6TemporalProviderReservationHandle(Guid Id, ulong Generation);

internal enum V6TemporalProviderReservationState { Reserved = 1, InUse, Released, Quarantined }

internal sealed record V6TemporalProviderReservation(
    V6TemporalProviderReservationHandle Handle,
    string ProviderIdentity,
    string OperationCorrelation,
    ResourceEnvelopeV1 Envelope,
    ulong ProviderGeneration,
    ulong ReservationGeneration,
    V6TemporalProviderReservationState State)
{
    internal bool AuthorizesExecution => false;
    internal bool GuaranteesDeadline => false;
    internal bool GuaranteesMinimumService => false;
}

/// <summary>Named deterministic protected-capacity provider for the managed P03 contour.</summary>
internal sealed class V6ManagedTemporalCapacityProvider
{
    internal const string Identity = "singnext.managed-model-temporal-capacity";
    private sealed class Record(V6TemporalProviderReservation reservation)
    {
        internal V6TemporalProviderReservation Reservation { get; set; } = reservation;
    }

    private readonly object _sync = new();
    private readonly Dictionary<Guid, Record> _records = [];
    private readonly ulong _capacityNanoseconds;
    private ulong _reservedNanoseconds;
    private ulong _providerGeneration = 1;
    private bool _providerGenerationExhausted;
    private ulong _nextReservationGeneration = 1;
    private bool _resetBeforeNextRelease;

    internal V6ManagedTemporalCapacityProvider(ulong capacityNanoseconds)
    {
        if (capacityNanoseconds == 0 || capacityNanoseconds == ulong.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(capacityNanoseconds));
        _capacityNanoseconds = capacityNanoseconds;
    }

    internal ulong ProviderGeneration { get { lock (_sync) return _providerGeneration; } }
    internal ulong ReservedNanoseconds { get { lock (_sync) return _reservedNanoseconds; } }

    internal KernelResult<V6TemporalProviderReservation> Reserve(
        string operationCorrelation, ResourceEnvelopeV1 envelope)
    {
        ResourceEnvelopeV1 exact;
        try { exact = envelope.Canonicalize(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or OverflowException)
        { return KernelResult<V6TemporalProviderReservation>.Fail(KernelError.InvalidMessage, exception.Message); }
        if (exact.Family != ResourceDimensionFamilyV1.Time || exact.ResourceClass != ResourceClassV1.ComputeTime ||
            exact.Unit != ResourceUnitV1.Nanoseconds || exact.WindowNanoseconds != 0 ||
            !Canonical(operationCorrelation))
            return KernelResult<V6TemporalProviderReservation>.Fail(KernelError.PlatformUnsupported,
                "Managed temporal capacity supports only canonical ComputeTime/Nanoseconds reservations.");
        lock (_sync)
        {
            if (_providerGenerationExhausted)
                return KernelResult<V6TemporalProviderReservation>.Fail(KernelError.CapacityExhausted,
                    "Temporal provider generation is exhausted; fresh admission is unavailable.");
            if (_records.Values.Any(record => record.Reservation.OperationCorrelation == operationCorrelation &&
                record.Reservation.State is V6TemporalProviderReservationState.Reserved or
                    V6TemporalProviderReservationState.InUse or V6TemporalProviderReservationState.Quarantined))
                return KernelResult<V6TemporalProviderReservation>.Fail(KernelError.DuplicateIdentity,
                    "Operation correlation already owns live temporal capacity.");
            if (exact.Amount > _capacityNanoseconds - _reservedNanoseconds)
                return KernelResult<V6TemporalProviderReservation>.Fail(KernelError.BudgetExceeded,
                    "Managed provider protected capacity is exhausted.");
            if (_nextReservationGeneration == 0)
                return KernelResult<V6TemporalProviderReservation>.Fail(KernelError.CapacityExhausted,
                    "Temporal provider reservation generation is exhausted.");
            var reservation = new V6TemporalProviderReservation(new(Guid.NewGuid(), 1), Identity,
                operationCorrelation, exact, _providerGeneration, _nextReservationGeneration++,
                V6TemporalProviderReservationState.Reserved);
            _records.Add(reservation.Handle.Id, new(reservation));
            _reservedNanoseconds += exact.Amount;
            return KernelResult<V6TemporalProviderReservation>.Ok(reservation);
        }
    }

    internal KernelResult<V6TemporalProviderReservation> Query(V6TemporalProviderReservationHandle handle)
    {
        lock (_sync) return Resolve(handle);
    }

    internal KernelResult<V6TemporalProviderReservation> BeginUse(V6TemporalProviderReservationHandle handle) =>
        Transition(handle, V6TemporalProviderReservationState.Reserved, V6TemporalProviderReservationState.InUse);

    internal KernelResult<V6TemporalProviderReservation> Quarantine(V6TemporalProviderReservationHandle handle)
    {
        lock (_sync)
        {
            var resolved = Resolve(handle);
            if (!resolved.IsSuccess) return resolved;
            var record = _records[handle.Id];
            if (record.Reservation.State == V6TemporalProviderReservationState.Quarantined) return resolved;
            if (record.Reservation.State is not (V6TemporalProviderReservationState.Reserved or V6TemporalProviderReservationState.InUse))
                return KernelResult<V6TemporalProviderReservation>.Fail(KernelError.InvalidTransition,
                    "Only live temporal capacity can be quarantined.");
            record.Reservation = record.Reservation with { State = V6TemporalProviderReservationState.Quarantined };
            return KernelResult<V6TemporalProviderReservation>.Ok(record.Reservation);
        }
    }

    internal KernelResult<V6TemporalProviderReservation> Release(V6TemporalProviderReservationHandle handle, bool unusedOnly = false)
    {
        lock (_sync)
        {
            var resolved = Resolve(handle);
            if (!resolved.IsSuccess) return resolved;
            if (resolved.Value!.State == V6TemporalProviderReservationState.Released) return resolved;
            if (unusedOnly && resolved.Value.State != V6TemporalProviderReservationState.Reserved)
                return KernelResult<V6TemporalProviderReservation>.Fail(KernelError.InvalidTransition,
                    "Unused compensation cannot release capacity that may have begun use.");
            if (resolved.Value.State is not (V6TemporalProviderReservationState.Reserved or V6TemporalProviderReservationState.InUse))
                return KernelResult<V6TemporalProviderReservation>.Fail(KernelError.InvalidTransition,
                    "Quarantined temporal capacity requires explicit reconciliation.");
            if (_resetBeforeNextRelease)
            {
                _resetBeforeNextRelease = false;
                var reset = ResetCore();
                if (!reset.IsSuccess)
                    return KernelResult<V6TemporalProviderReservation>.Fail(reset.Error, reset.Message!);
                resolved = Resolve(handle);
                if (!resolved.IsSuccess) return resolved;
            }
            var record = _records[handle.Id];
            if (record.Reservation.State is not (V6TemporalProviderReservationState.Reserved or V6TemporalProviderReservationState.InUse))
                return KernelResult<V6TemporalProviderReservation>.Fail(KernelError.InvalidTransition,
                    "Quarantined temporal capacity requires explicit reconciliation.");
            _reservedNanoseconds -= record.Reservation.Envelope.Amount;
            record.Reservation = record.Reservation with { State = V6TemporalProviderReservationState.Released };
            return KernelResult<V6TemporalProviderReservation>.Ok(record.Reservation);
        }
    }

    internal KernelResult<V6TemporalProviderReservation> ReconcileAndRelease(V6TemporalProviderReservationHandle handle)
    {
        lock (_sync)
        {
            var resolved = Resolve(handle);
            if (!resolved.IsSuccess) return resolved;
            var record = _records[handle.Id];
            // Retry can reuse exact owner-recorded closure without releasing twice.
            // Resolve still rejects a missing record or stale handle generation.
            if (record.Reservation.State == V6TemporalProviderReservationState.Released) return resolved;
            if (record.Reservation.State != V6TemporalProviderReservationState.Quarantined)
                return KernelResult<V6TemporalProviderReservation>.Fail(KernelError.InvalidTransition,
                    "Only quarantined temporal capacity requires reconciliation.");
            _reservedNanoseconds -= record.Reservation.Envelope.Amount;
            record.Reservation = record.Reservation with { State = V6TemporalProviderReservationState.Released };
            return KernelResult<V6TemporalProviderReservation>.Ok(record.Reservation);
        }
    }

    internal KernelResult Reset()
    {
        lock (_sync)
            return ResetCore();
    }

    // Deterministic managed-model fault at the settlement/release boundary.
    internal void InjectResetBeforeNextRelease()
    {
        lock (_sync) _resetBeforeNextRelease = true;
    }

    private KernelResult ResetCore()
    {
        if (_providerGeneration == ulong.MaxValue)
        {
            // A reset attempt cannot leave the terminal generation usable. Retain
            // every live reservation until explicit model/provider reconciliation.
            _providerGenerationExhausted = true;
            foreach (var record in _records.Values)
                if (record.Reservation.State != V6TemporalProviderReservationState.Released)
                    record.Reservation = record.Reservation with
                    { State = V6TemporalProviderReservationState.Quarantined };
            return KernelResult.Fail(KernelError.CapacityExhausted, "Temporal provider generation is exhausted.");
        }
        _providerGeneration++;
        ulong retained = 0;
        foreach (var (id, record) in _records.ToArray())
        {
            if (record.Reservation.State is V6TemporalProviderReservationState.InUse or
                V6TemporalProviderReservationState.Quarantined)
            {
                record.Reservation = record.Reservation with
                {
                    State = V6TemporalProviderReservationState.Quarantined
                };
                retained = checked(retained + record.Reservation.Envelope.Amount);
            }
            else
                _records.Remove(id);
        }
        _reservedNanoseconds = retained;
        return KernelResult.Ok();
    }

    private KernelResult<V6TemporalProviderReservation> Transition(
        V6TemporalProviderReservationHandle handle,
        V6TemporalProviderReservationState expected,
        V6TemporalProviderReservationState next)
    {
        lock (_sync)
        {
            var resolved = Resolve(handle);
            if (!resolved.IsSuccess) return resolved;
            var record = _records[handle.Id];
            if (record.Reservation.State != expected)
                return KernelResult<V6TemporalProviderReservation>.Fail(KernelError.InvalidTransition,
                    $"Temporal provider transition requires {expected}.");
            record.Reservation = record.Reservation with { State = next };
            return KernelResult<V6TemporalProviderReservation>.Ok(record.Reservation);
        }
    }

    private KernelResult<V6TemporalProviderReservation> Resolve(V6TemporalProviderReservationHandle handle) =>
        handle.Id != Guid.Empty && handle.Generation == 1 && _records.TryGetValue(handle.Id, out var record) &&
        record.Reservation.Handle == handle
            ? KernelResult<V6TemporalProviderReservation>.Ok(record.Reservation)
            : KernelResult<V6TemporalProviderReservation>.Fail(KernelError.StaleGeneration,
                "Temporal provider reservation is missing or stale.");

    private static bool Canonical(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Length > 256 || value.Any(char.IsControl))
            return false;
        try { _ = new System.Text.UTF8Encoding(false, true).GetByteCount(value); }
        catch (System.Text.EncoderFallbackException) { return false; }
        return true;
    }
}

internal enum V6TemporalCapacityOperationState { Admitted = 1, SubmitInFlight, Submitted, Settled, Cancelled, Quarantined }

internal sealed record V6TemporalCapacityBinding(
    Guid Id,
    ProcessHandle Owner,
    BudgetReservationHandle Budget,
    TemporalSemanticsV1 Temporal,
    V6TemporalProviderReservation ProviderReservation,
    V6TemporalCapacityOperationState State,
    ExternalOperationHandle? ExternalOperation = null,
    ulong? ReportedOverrunNanoseconds = null)
{
    internal bool AuthorizesExecution => false;
    internal bool GuaranteesDeadline => false;
    internal bool GuaranteesMinimumService => false;
}

internal sealed class V6TemporalCapacityCoordinator
{
    private readonly ResourceBudgetAuthority budgets;
    private readonly V6ManagedTemporalCapacityProvider provider;
    private readonly RuntimeKernel? effectKernel;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, V6TemporalCapacityBinding> _bindings = [];
    private readonly HashSet<Guid> _cancellingBindings = [];

    internal V6TemporalCapacityCoordinator(ResourceBudgetAuthority budgets,
        V6ManagedTemporalCapacityProvider provider)
    {
        this.budgets = budgets ?? throw new ArgumentNullException(nameof(budgets));
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    internal V6TemporalCapacityCoordinator(RuntimeKernel kernel,
        V6ManagedTemporalCapacityProvider provider) : this(kernel?.Budgets ??
            throw new ArgumentNullException(nameof(kernel)), provider) => effectKernel = kernel;

    internal KernelResult<V6TemporalCapacityBinding> AdmitBoundToExternalOperation(
        ProcessHandle owner,
        BudgetReservationHandle budget,
        ExternalOperationHandle operation,
        TemporalSemanticsV1 temporal)
    {
        if (effectKernel is null)
            return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.PlatformUnsupported,
                "A live SingNext external-operation owner is required.");
        var current = effectKernel.QueryExternalOperation(owner, operation);
        if (!current.IsSuccess)
            return KernelResult<V6TemporalCapacityBinding>.Fail(current.Error, current.Message!);
        if (current.Value!.State is not (ExternalOperationState.Prepared or ExternalOperationState.Admitted) ||
            current.Value.Disposition != ExternalOperationDisposition.Active)
            return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.InvalidTransition,
                "Temporal capacity can bind only an active pre-submit external operation.");
        var correlation = $"external:{operation.OperationId.Value}:{operation.Generation.Value}";
        var admitted = AdmitCore(owner, budget, correlation, temporal, commitReceipt: false);
        if (!admitted.IsSuccess) return admitted;
        lock (_sync)
        {
            var bound = admitted.Value! with { ExternalOperation = operation };
            _bindings[bound.Id] = bound;
            // The initial owner query predates capacity admission. Re-read every
            // mutable owner immediately before returning the composition receipt.
            // This is an admission observation, not an atomic execution fence;
            // SubmitBoundExternalOperation still performs its own owner commit.
            current = effectKernel.QueryExternalOperation(owner, operation);
            var capacity = provider.Query(bound.ProviderReservation.Handle);
            var budgetState = budgets.Query(budget);
            var ownerReady = current.IsSuccess &&
                current.Value!.State is ExternalOperationState.Prepared or ExternalOperationState.Admitted &&
                current.Value.Disposition == ExternalOperationDisposition.Active;
            var resourcesReady = capacity.IsSuccess && capacity.Value! == bound.ProviderReservation &&
                capacity.Value.State == V6TemporalProviderReservationState.Reserved &&
                capacity.Value.ProviderGeneration == provider.ProviderGeneration &&
                budgetState.IsSuccess && budgetState.Value!.Owner == owner &&
                budgetState.Value.State == BudgetReservationState.Bound;
            if (!ownerReady || !resourcesReady)
                return RejectUnpublishedAdmission(bound, !current.IsSuccess ? current.Error :
                    !ownerReady ? KernelError.InvalidTransition : KernelError.StaleGeneration);
            var committed = budgets.CommitTemporalComposition(owner, budget, bound.Id);
            if (!committed.IsSuccess)
                return RejectUnpublishedAdmission(bound, committed.Error);
            return KernelResult<V6TemporalCapacityBinding>.Ok(bound);
        }
    }

    internal KernelResult<V6TemporalCapacityBinding> Admit(
        ProcessHandle owner,
        BudgetReservationHandle budget,
        string operationCorrelation,
        TemporalSemanticsV1 temporal) => AdmitCore(owner, budget, operationCorrelation, temporal, commitReceipt: true);

    private KernelResult<V6TemporalCapacityBinding> AdmitCore(
        ProcessHandle owner, BudgetReservationHandle budget, string operationCorrelation,
        TemporalSemanticsV1 temporal, bool commitReceipt)
    {
        TemporalSemanticsV1 exact;
        try { exact = temporal.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.InvalidMessage, exception.Message); }
        if (exact.Assurance != ResourceAssuranceV1.GuaranteedReservation ||
            exact.DeadlineSemantics != TemporalDeadlineSemanticsV1.None)
            return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.PlatformUnsupported,
                "Managed protected capacity is a reservation only and cannot claim a completion deadline.");
        lock (_sync)
        {
            // The budget owner arbitrates composition uniqueness across instances;
            // this dictionary only retains the committed composition relationship.
            // No provider callback runs here.
            var snapshot = budgets.Query(budget);
            if (!snapshot.IsSuccess || snapshot.Value!.Owner != owner ||
                snapshot.Value.State != BudgetReservationState.Bound ||
                snapshot.Value.Lifetime != BudgetReservationLifetime.ExternalEffect ||
                snapshot.Value.Amounts.SingleOrDefault(static amount => amount.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Amount < exact.ComputeEnvelope.Amount)
                return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.PlatformDenied,
                    "Temporal capacity requires the exact bound compute-time budget envelope.");
            var composition = Guid.NewGuid();
            var claimed = budgets.ClaimTemporalComposition(owner, budget, composition);
            if (!claimed.IsSuccess)
                return KernelResult<V6TemporalCapacityBinding>.Fail(claimed.Error, claimed.Message!);
            var reserved = provider.Reserve(operationCorrelation, exact.ComputeEnvelope);
            if (!reserved.IsSuccess)
            {
                budgets.AbandonTemporalComposition(owner, budget, composition);
                return KernelResult<V6TemporalCapacityBinding>.Fail(reserved.Error, reserved.Message!);
            }
            var binding = new V6TemporalCapacityBinding(composition, owner, budget, exact,
                reserved.Value!, V6TemporalCapacityOperationState.Admitted);
            try { _bindings.Add(binding.Id, binding); }
            catch (Exception exception) when (exception is ArgumentException or OutOfMemoryException)
            {
                var released = provider.Release(reserved.Value!.Handle, unusedOnly: true);
                if (released.IsSuccess)
                    budgets.AbandonTemporalComposition(owner, budget, composition);
                return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.CapacityExhausted, exception.Message);
            }
            if (commitReceipt)
            {
                var committed = budgets.CommitTemporalComposition(owner, budget, composition);
                if (!committed.IsSuccess)
                    return RejectUnpublishedAdmission(binding, committed.Error);
            }
            return KernelResult<V6TemporalCapacityBinding>.Ok(binding);
        }
    }

    // Caller holds coordinator _sync; this compensates only unpublished model capacity.
    private KernelResult<V6TemporalCapacityBinding> RejectUnpublishedAdmission(
        V6TemporalCapacityBinding binding, KernelError error)
    {
        var capacity = provider.Query(binding.ProviderReservation.Handle);
        var unused = capacity.IsSuccess && capacity.Value!.State == V6TemporalProviderReservationState.Reserved;
        var released = unused ? provider.Release(binding.ProviderReservation.Handle, unusedOnly: true) : default;
        var modelResetDroppedUnused = unused && released.Error == KernelError.StaleGeneration &&
            provider.ProviderGeneration != binding.ProviderReservation.ProviderGeneration &&
            !provider.Query(binding.ProviderReservation.Handle).IsSuccess;
        if (released.IsSuccess || modelResetDroppedUnused)
        {
            _bindings.Remove(binding.Id);
            budgets.AbandonTemporalComposition(binding.Owner, binding.Budget, binding.Id);
            return KernelResult<V6TemporalCapacityBinding>.Fail(error,
                "Temporal composition dependencies changed before receipt publication.");
        }
        _ = budgets.QuarantineLease(binding.Owner, binding.Budget);
        var quarantine = provider.Quarantine(binding.ProviderReservation.Handle);
        _bindings[binding.Id] = binding with
        {
            ProviderReservation = quarantine.IsSuccess ? quarantine.Value! : binding.ProviderReservation,
            State = V6TemporalCapacityOperationState.Quarantined,
        };
        return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.ExternalEffectUncontained,
            "Unpublished temporal composition could not close its exact provider reservation.");
    }

    internal KernelResult<V6TemporalCapacityBinding> Submit(
        V6TemporalCapacityBinding supplied,
        Func<KernelResult> singNextAdmission,
        Func<KernelResult> providerAdmission,
        Func<KernelResult> runtimeLegality,
        Func<KernelResult> providerSubmit) => SubmitCore(supplied, null,
            singNextAdmission, providerAdmission, runtimeLegality, providerSubmit);

    internal KernelResult<V6TemporalCapacityBinding> SubmitBoundExternalOperation(
        V6TemporalCapacityBinding supplied,
        OperationDependencySnapshot currentDependencies,
        Func<KernelResult> singNextAdmission,
        Func<KernelResult> providerAdmission,
        Func<KernelResult> runtimeLegality,
        Func<KernelResult> providerSubmit) => SubmitCore(supplied, currentDependencies,
            singNextAdmission, providerAdmission, runtimeLegality, providerSubmit);

    private KernelResult<V6TemporalCapacityBinding> SubmitCore(
        V6TemporalCapacityBinding supplied,
        OperationDependencySnapshot? boundDependencies,
        Func<KernelResult> singNextAdmission,
        Func<KernelResult> providerAdmission,
        Func<KernelResult> runtimeLegality,
        Func<KernelResult> providerSubmit)
    {
        if ((supplied.ExternalOperation is null) != (boundDependencies is null))
            return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.PlatformDenied,
                "Bound external capacity requires the exact owner-submit path.");
        foreach (var gate in new[] { singNextAdmission, providerAdmission, runtimeLegality })
        {
            KernelResult decision;
            try { decision = gate(); }
            catch (Exception exception) { return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.PlatformDenied, exception.Message); }
            if (!decision.IsSuccess) return KernelResult<V6TemporalCapacityBinding>.Fail(decision.Error, decision.Message!);
        }
        // Independent runtime legality may revoke either earlier admission.
        // Re-read both owners before consuming the budget or provider capacity.
        foreach (var gate in new[] { singNextAdmission, providerAdmission })
        {
            KernelResult decision;
            try { decision = gate(); }
            catch (Exception exception) { return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.PlatformDenied, exception.Message); }
            if (!decision.IsSuccess) return KernelResult<V6TemporalCapacityBinding>.Fail(decision.Error, decision.Message!);
        }
        V6TemporalCapacityBinding binding;
        lock (_sync)
        {
            if (!_bindings.TryGetValue(supplied.Id, out binding!) || binding != supplied ||
                binding.State != V6TemporalCapacityOperationState.Admitted || _cancellingBindings.Contains(supplied.Id))
                return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.StaleGeneration,
                    "Temporal capacity binding is stale or already consumed.");
            if (binding.ExternalOperation is { } operation)
            {
                var effect = effectKernel!.QueryExternalOperation(binding.Owner, operation);
                if (!effect.IsSuccess || effect.Value!.State != ExternalOperationState.Admitted)
                    return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.StaleGeneration,
                        "Bound external operation changed before temporal submit.");
            }
            var budget = budgets.Query(binding.Budget);
            var capacity = provider.Query(binding.ProviderReservation.Handle);
            if (!budget.IsSuccess || budget.Value!.Owner != binding.Owner || budget.Value.State != BudgetReservationState.Bound ||
                !capacity.IsSuccess || capacity.Value! != binding.ProviderReservation ||
                capacity.Value!.ProviderGeneration != provider.ProviderGeneration)
            {
                if (capacity.IsSuccess && capacity.Value!.State is V6TemporalProviderReservationState.InUse or
                    V6TemporalProviderReservationState.Quarantined)
                {
                    _ = budgets.QuarantineLease(binding.Owner, binding.Budget);
                    var quarantined = provider.Quarantine(binding.ProviderReservation.Handle);
                    _bindings[binding.Id] = binding with
                    {
                        ProviderReservation = quarantined.IsSuccess ? quarantined.Value! : binding.ProviderReservation,
                        State = V6TemporalCapacityOperationState.Quarantined,
                    };
                    return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.StaleGeneration,
                        "Provider use changed before submit; budget cancellation cannot establish closure.");
                }
                // The budget owner may have cancelled the still-bound lease after admission.
                // No submit can have begun while this binding remains Admitted, so close only
                // an exact, still-reserved provider record instead of leaking model capacity.
                if (budget.IsSuccess && budget.Value!.Owner == binding.Owner &&
                    budget.Value.State != BudgetReservationState.Bound && capacity.IsSuccess &&
                    capacity.Value! == binding.ProviderReservation &&
                    capacity.Value!.State == V6TemporalProviderReservationState.Reserved &&
                    capacity.Value!.ProviderGeneration == provider.ProviderGeneration)
                {
                    var released = provider.Release(binding.ProviderReservation.Handle, unusedOnly: true);
                    if (released.IsSuccess)
                    {
                        binding = binding with
                        {
                            ProviderReservation = released.Value!,
                            State = V6TemporalCapacityOperationState.Cancelled,
                        };
                        _bindings[binding.Id] = binding;
                    }
                }
                if (budget.IsSuccess && budget.Value!.State == BudgetReservationState.Bound)
                    _ = budgets.CancelLeasePreSubmit(binding.Owner, binding.Budget, binding.Id);
                return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.StaleGeneration,
                    "Temporal budget or provider reservation changed before submit.");
            }
            // Begin both reservations before the owner submit. Cancellation cannot
            // remove the budget while the owner records a possible effect.
            var consuming = budgets.BeginConsumption(binding.Owner, binding.Budget, binding.Id);
            var inUse = consuming.IsSuccess ? provider.BeginUse(binding.ProviderReservation.Handle) : default;
            if (!consuming.IsSuccess)
            {
                // No owner submit or provider callback has run. Close a still-reserved
                // provider record; a concurrent provider reset already drops it.
                var released = provider.Release(binding.ProviderReservation.Handle, unusedOnly: true);
                var closed = released.IsSuccess
                    ? released.Value!
                    : released.Error == KernelError.StaleGeneration &&
                      provider.ProviderGeneration != binding.ProviderReservation.ProviderGeneration
                        ? binding.ProviderReservation with { State = V6TemporalProviderReservationState.Released }
                        : null;
                if (closed is not null)
                {
                    _bindings[binding.Id] = binding with
                    {
                        ProviderReservation = closed,
                        State = V6TemporalCapacityOperationState.Cancelled,
                    };
                    return KernelResult<V6TemporalCapacityBinding>.Fail(consuming.Error, consuming.Message!);
                }
                _ = budgets.QuarantineLease(binding.Owner, binding.Budget);
                var quarantined = provider.Quarantine(binding.ProviderReservation.Handle);
                _bindings[binding.Id] = binding with
                {
                    ProviderReservation = quarantined.IsSuccess ? quarantined.Value! : binding.ProviderReservation,
                    State = V6TemporalCapacityOperationState.Quarantined,
                };
                return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.PlatformFaulted,
                    "Budget consumption failed before submit and provider capacity could not close.");
            }
            if (!inUse.IsSuccess)
            {
                _ = budgets.QuarantineLease(binding.Owner, binding.Budget);
                _ = provider.Quarantine(binding.ProviderReservation.Handle);
                binding = binding with { State = V6TemporalCapacityOperationState.Quarantined };
                _bindings[binding.Id] = binding;
                return KernelResult<V6TemporalCapacityBinding>.Fail(inUse.Error, inUse.Message!);
            }
            binding = binding with
            {
                ProviderReservation = inUse.Value!,
                State = V6TemporalCapacityOperationState.SubmitInFlight,
            };
            _bindings[binding.Id] = binding;
        }

        if (boundDependencies is { } dependencies)
        {
            var operation = binding.ExternalOperation!.Value;
            KernelResult<OperationBinding> ownerSubmit;
            try { ownerSubmit = effectKernel!.RecordExternalOperationSubmission(binding.Owner,
                operation, dependencies); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                ownerSubmit = KernelResult<OperationBinding>.Fail(KernelError.PlatformFaulted,
                    $"External owner submission failed: {exception.GetType().Name}.");
            }
            if (!ownerSubmit.IsSuccess)
            {
                KernelResult<ExternalOperationSnapshot> owner;
                try { owner = effectKernel!.CancelExternalOperation(binding.Owner, operation, false); }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    owner = KernelResult<ExternalOperationSnapshot>.Fail(KernelError.PlatformFaulted,
                        $"External owner cancellation failed: {exception.GetType().Name}.");
                }
                lock (_sync)
                {
                    var current = _bindings[binding.Id];
                    if (!owner.IsSuccess || owner.Value!.Disposition != ExternalOperationDisposition.Cancelled ||
                        (owner.Value.State is not (ExternalOperationState.Prepared or ExternalOperationState.Admitted) &&
                         !(owner.Value.State == ExternalOperationState.Released && owner.Value.Binding is null)) ||
                        owner.Value.Transitions.Any(transition =>
                            transition.Event == "Submitted"))
                    {
                        _ = budgets.QuarantineLease(current.Owner, current.Budget);
                        var quarantined = provider.Quarantine(current.ProviderReservation.Handle);
                        _bindings[current.Id] = current with
                        {
                            ProviderReservation = quarantined.IsSuccess ? quarantined.Value! : current.ProviderReservation,
                            State = V6TemporalCapacityOperationState.Quarantined,
                        };
                        return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.ExternalEffectUncontained,
                            "External owner may have submitted despite an unsuccessful submit response.");
                    }
                    // Owner-confirmed pre-submit cancellation makes absence of Submitted
                    // stable against competing admission. Our provider callback never ran.
                    // Even definite owner denial cannot refund before the exact
                    // provider record closes: reset may retain it in quarantine.
                    var released = provider.Release(current.ProviderReservation.Handle);
                    var settled = released.IsSuccess
                        ? budgets.SettleLease(current.Owner, current.Budget, [], current.Id)
                        : default;
                    if (!settled.IsSuccess || !released.IsSuccess)
                    {
                        _ = budgets.QuarantineLease(current.Owner, current.Budget);
                        var quarantined = provider.Quarantine(current.ProviderReservation.Handle);
                        _bindings[current.Id] = current with
                        {
                            ProviderReservation = quarantined.IsSuccess ? quarantined.Value! : current.ProviderReservation,
                            State = V6TemporalCapacityOperationState.Quarantined,
                        };
                        return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.ExternalEffectUncontained,
                            "Definite owner denial could not close both temporal reservations.");
                    }
                    _bindings[current.Id] = current with
                    {
                        ProviderReservation = released.Value!,
                        State = V6TemporalCapacityOperationState.Cancelled,
                    };
                }
                return KernelResult<V6TemporalCapacityBinding>.Fail(ownerSubmit.Error, ownerSubmit.Message!);
            }
            lock (_sync)
            {
                binding = _bindings[binding.Id];
                var budget = budgets.Query(binding.Budget);
                var capacity = provider.Query(binding.ProviderReservation.Handle);
                if (!budget.IsSuccess || budget.Value!.Owner != binding.Owner ||
                    budget.Value.State != BudgetReservationState.Consuming ||
                    !capacity.IsSuccess || capacity.Value! != binding.ProviderReservation ||
                    capacity.Value!.ProviderGeneration != provider.ProviderGeneration ||
                    capacity.Value.State != V6TemporalProviderReservationState.InUse)
                {
                    _ = budgets.QuarantineLease(binding.Owner, binding.Budget);
                    var quarantined = provider.Quarantine(binding.ProviderReservation.Handle);
                    _bindings[binding.Id] = binding with
                    {
                        ProviderReservation = quarantined.IsSuccess ? quarantined.Value! : binding.ProviderReservation,
                        State = V6TemporalCapacityOperationState.Quarantined,
                    };
                    return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.StaleGeneration,
                        "Temporal reservation changed after external owner submission.");
                }
            }
        }

        KernelResult submitted;
        try { submitted = providerSubmit(); }
        catch (Exception exception) { submitted = KernelResult.Fail(KernelError.PlatformUnavailable, exception.Message); }
        if (boundDependencies is not null)
        {
            var effect = effectKernel!.QueryExternalOperation(binding.Owner, binding.ExternalOperation!.Value);
            if (submitted.IsSuccess && (!effect.IsSuccess || effect.Value!.Disposition is ExternalOperationDisposition.ProviderLost or
                    ExternalOperationDisposition.Faulted or ExternalOperationDisposition.Cancelled or
                    ExternalOperationDisposition.Discarded or ExternalOperationDisposition.CancellationPending ||
                effect.Value.State is ExternalOperationState.Prepared or ExternalOperationState.Admitted))
                submitted = KernelResult.Fail(KernelError.StaleGeneration,
                    "Bound external owner changed during provider submit; temporal capacity remains quarantined.");
        }
        lock (_sync)
        {
            binding = _bindings[binding.Id];
            var currentCapacity = provider.Query(binding.ProviderReservation.Handle);
            var providerStable = provider.ProviderGeneration == binding.ProviderReservation.ProviderGeneration &&
                currentCapacity.IsSuccess && currentCapacity.Value!.State == V6TemporalProviderReservationState.InUse;
            var currentBudget = budgets.Query(binding.Budget);
            var budgetStable = currentBudget.IsSuccess && currentBudget.Value!.Owner == binding.Owner &&
                currentBudget.Value.State == BudgetReservationState.Consuming;
            if (!submitted.IsSuccess || !providerStable || !budgetStable)
            {
                _ = budgets.QuarantineLease(binding.Owner, binding.Budget);
                var quarantined = provider.Quarantine(binding.ProviderReservation.Handle);
                binding = binding with
                {
                    ProviderReservation = quarantined.IsSuccess ? quarantined.Value! : binding.ProviderReservation,
                    State = V6TemporalCapacityOperationState.Quarantined,
                };
                _bindings[binding.Id] = binding;
                return KernelResult<V6TemporalCapacityBinding>.Fail(
                    submitted.IsSuccess ? KernelError.StaleGeneration : submitted.Error,
                    submitted.IsSuccess ? "Temporal provider or budget reservation changed during submit." : submitted.Message!);
            }
            binding = binding with { State = V6TemporalCapacityOperationState.Submitted };
            _bindings[binding.Id] = binding;
            return KernelResult<V6TemporalCapacityBinding>.Ok(binding);
        }
    }

    internal KernelResult<V6TemporalCapacityBinding> Settle(
        V6TemporalCapacityBinding supplied, ulong actualComputeNanoseconds)
    {
        if (supplied.ExternalOperation is { } operation)
        {
            if (effectKernel is null)
                return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.StaleGeneration,
                    "Bound external-operation owner is absent.");
            var effect = effectKernel.QueryExternalOperation(supplied.Owner, operation);
            if (!effect.IsSuccess || effect.Value!.State is not (ExternalOperationState.Published or
                    ExternalOperationState.Released) ||
                effect.Value.Disposition != ExternalOperationDisposition.Published)
                return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.ExternalEffectUncontained,
                    "Bound external effect requires owner publication before temporal settlement.");
        }
        lock (_sync)
        {
            if (!_bindings.TryGetValue(supplied.Id, out var binding) || binding != supplied ||
                binding.State != V6TemporalCapacityOperationState.Submitted)
                return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.InvalidTransition,
                    "Temporal capacity is not eligible for exact settlement.");
            if (actualComputeNanoseconds > binding.Temporal.ComputeEnvelope.Amount)
            {
                _ = budgets.QuarantineLease(binding.Owner, binding.Budget);
                var quarantined = provider.Quarantine(binding.ProviderReservation.Handle);
                binding = binding with
                {
                    ProviderReservation = quarantined.IsSuccess ? quarantined.Value! : binding.ProviderReservation,
                    State = V6TemporalCapacityOperationState.Quarantined,
                    ReportedOverrunNanoseconds = actualComputeNanoseconds,
                };
                _bindings[binding.Id] = binding;
                return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.BudgetExceeded,
                    "Reported compute time exceeded the reserved bound; ordinary reconciliation cannot settle it.");
            }
            var capacity = provider.Query(binding.ProviderReservation.Handle);
            if (!capacity.IsSuccess || capacity.Value! != binding.ProviderReservation ||
                capacity.Value!.State != V6TemporalProviderReservationState.InUse ||
                provider.ProviderGeneration != binding.ProviderReservation.ProviderGeneration)
            {
                _ = budgets.QuarantineLease(binding.Owner, binding.Budget);
                var quarantined = provider.Quarantine(binding.ProviderReservation.Handle);
                binding = binding with
                {
                    ProviderReservation = quarantined.IsSuccess ? quarantined.Value! : binding.ProviderReservation,
                    State = V6TemporalCapacityOperationState.Quarantined,
                };
                _bindings[binding.Id] = binding;
                return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.StaleGeneration,
                    "Temporal provider capacity changed before budget settlement; both owners remain quarantined.");
            }
            IReadOnlyList<BudgetAmount> actual = actualComputeNanoseconds == 0
                ? [] : [new(ServiceBudgetDimension.ComputeTimeNanoseconds, actualComputeNanoseconds)];
            var settled = budgets.SettleLease(binding.Owner, binding.Budget, actual, binding.Id);
            if (!settled.IsSuccess)
            {
                _ = budgets.QuarantineLease(binding.Owner, binding.Budget);
                var quarantined = provider.Quarantine(binding.ProviderReservation.Handle);
                binding = binding with
                {
                    ProviderReservation = quarantined.IsSuccess ? quarantined.Value! : binding.ProviderReservation,
                    State = V6TemporalCapacityOperationState.Quarantined,
                };
                _bindings[binding.Id] = binding;
                return KernelResult<V6TemporalCapacityBinding>.Fail(settled.Error, settled.Message!);
            }
            var released = provider.Release(binding.ProviderReservation.Handle);
            if (!released.IsSuccess)
            {
                var quarantined = provider.Quarantine(binding.ProviderReservation.Handle);
                binding = binding with
                {
                    ProviderReservation = quarantined.IsSuccess ? quarantined.Value! : binding.ProviderReservation,
                    State = V6TemporalCapacityOperationState.Quarantined,
                };
                _bindings[binding.Id] = binding;
                return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.PlatformFaulted,
                    "Budget settled but managed provider capacity could not close.");
            }
            binding = binding with
            {
                ProviderReservation = released.Value!,
                State = V6TemporalCapacityOperationState.Settled,
            };
            _bindings[binding.Id] = binding;
            return KernelResult<V6TemporalCapacityBinding>.Ok(binding);
        }
    }

    internal KernelResult<V6TemporalCapacityBinding> CancelAdmitted(V6TemporalCapacityBinding supplied)
    {
        lock (_sync)
        {
            if (!_bindings.TryGetValue(supplied.Id, out var observed) || observed != supplied ||
                observed.State != V6TemporalCapacityOperationState.Admitted || !_cancellingBindings.Add(supplied.Id))
                return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.InvalidTransition,
                    "Only exact idle pre-submit temporal capacity can begin cancellation.");
        }
        try
        {
            var ownerClosed = true;
            if (supplied.ExternalOperation is { } operation)
            {
                ownerClosed = false;
                try
                {
                    var cancelledOwner = effectKernel!.CancelExternalOperation(supplied.Owner, operation, false);
                    ownerClosed = cancelledOwner.IsSuccess &&
                        cancelledOwner.Value!.Disposition == ExternalOperationDisposition.Cancelled &&
                        (cancelledOwner.Value.State is ExternalOperationState.Prepared or ExternalOperationState.Admitted ||
                         cancelledOwner.Value.State == ExternalOperationState.Released && cancelledOwner.Value.Binding is null) &&
                        !cancelledOwner.Value.Transitions.Any(transition => transition.Event == "Submitted");
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    // Failed owner response cannot establish pre-submit closure.
                }
            }
            lock (_sync)
            {
                if (!_bindings.TryGetValue(supplied.Id, out var binding) || binding != supplied ||
                    binding.State != V6TemporalCapacityOperationState.Admitted)
                    return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.InvalidTransition,
                        "Only exact pre-submit temporal capacity can be cancelled.");
                if (!ownerClosed)
                {
                    _ = budgets.QuarantineLease(binding.Owner, binding.Budget);
                    var quarantine = provider.Quarantine(binding.ProviderReservation.Handle);
                    _bindings[binding.Id] = binding with
                    {
                        ProviderReservation = quarantine.IsSuccess ? quarantine.Value! : binding.ProviderReservation,
                        State = V6TemporalCapacityOperationState.Quarantined,
                    };
                    return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.ExternalEffectUncontained,
                        "Bound external owner did not confirm stable pre-submit cancellation.");
                }
                var budget = budgets.Query(binding.Budget);
                if (!budget.IsSuccess || budget.Value!.Owner != binding.Owner)
                    return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.StaleGeneration,
                        "Temporal cancellation budget owner changed.");
                if (budget.Value.State is not (BudgetReservationState.Reserved or BudgetReservationState.Bound or
                    BudgetReservationState.CancelledPreSubmit))
                    return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.InvalidTransition,
                        "Temporal cancellation budget is no longer pre-submit.");
                // Close the exact unused provider record atomically before refund.
                // A coordinator admission receipt cannot substitute for current provider state.
                var released = provider.Release(binding.ProviderReservation.Handle, unusedOnly: true);
                var providerReservation = released.IsSuccess
                    ? released.Value!
                    : released.Error == KernelError.StaleGeneration &&
                      provider.ProviderGeneration != binding.ProviderReservation.ProviderGeneration &&
                      provider.Query(binding.ProviderReservation.Handle).Error == KernelError.StaleGeneration
                        ? binding.ProviderReservation with { State = V6TemporalProviderReservationState.Released }
                        : null;
                if (providerReservation is null)
                {
                    _ = budgets.QuarantineLease(binding.Owner, binding.Budget);
                    var quarantine = provider.Quarantine(binding.ProviderReservation.Handle);
                    _bindings[binding.Id] = binding with
                    {
                        ProviderReservation = quarantine.IsSuccess ? quarantine.Value! : binding.ProviderReservation,
                        State = V6TemporalCapacityOperationState.Quarantined,
                    };
                    return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.ExternalEffectUncontained,
                        "Exact unused provider closure failed; budget refund remains forbidden.");
                }
                binding = binding with { ProviderReservation = providerReservation, State = V6TemporalCapacityOperationState.Cancelled };
                _bindings[binding.Id] = binding;
                var cancelled = budgets.CancelLeasePreSubmit(binding.Owner, binding.Budget, binding.Id);
                if (!cancelled.IsSuccess)
                    return KernelResult<V6TemporalCapacityBinding>.Fail(cancelled.Error, cancelled.Message!);
                return KernelResult<V6TemporalCapacityBinding>.Ok(binding);
            }
        }
        finally { lock (_sync) _cancellingBindings.Remove(supplied.Id); }
    }

    internal KernelResult<V6TemporalCapacityBinding> ReconcileQuarantined(
        V6TemporalCapacityBinding supplied,
        Func<KernelResult> confirmEffectClosure)
    {
        if (supplied.ExternalOperation is not null)
            return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.PlatformDenied,
                "Bound external effects require closure from ExternalOperationAuthority.");
        return ReconcileQuarantinedCore(supplied, confirmEffectClosure);
    }

    internal KernelResult<V6TemporalCapacityBinding> ReconcileReportedOverrun(
        V6TemporalCapacityBinding supplied,
        Func<KernelResult> confirmEffectClosure)
    {
        if (supplied.ExternalOperation is not null)
            return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.PlatformDenied,
                "Bound external effects require closure from ExternalOperationAuthority.");
        return ReconcileQuarantinedCore(supplied, confirmEffectClosure, allowReportedOverrun: true);
    }

    internal KernelResult<V6TemporalCapacityBinding> ReconcileQuarantinedBoundExternalOperation(
        V6TemporalCapacityBinding supplied)
    {
        if (effectKernel is null || supplied.ExternalOperation is not { } operation)
            return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.PlatformUnsupported,
                "An exact bound external operation is required.");
        return ReconcileQuarantinedCore(supplied, () =>
        {
            var effect = effectKernel.QueryExternalOperation(supplied.Owner, operation);
            if (!effect.IsSuccess) return KernelResult.Fail(effect.Error, effect.Message!);
            return effect.Value!.State == ExternalOperationState.Released
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.ExternalEffectUncontained,
                    "ExternalOperationAuthority has not released the exact effect.");
        });
    }

    internal KernelResult<V6TemporalCapacityBinding> ReconcileReportedOverrunBoundExternalOperation(
        V6TemporalCapacityBinding supplied)
    {
        if (effectKernel is null || supplied.ExternalOperation is not { } operation)
            return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.PlatformUnsupported,
                "An exact bound external operation is required.");
        return ReconcileQuarantinedCore(supplied, () =>
        {
            var effect = effectKernel.QueryExternalOperation(supplied.Owner, operation);
            if (!effect.IsSuccess) return KernelResult.Fail(effect.Error, effect.Message!);
            return effect.Value!.State == ExternalOperationState.Released
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.ExternalEffectUncontained,
                    "ExternalOperationAuthority has not released the exact effect.");
        }, allowReportedOverrun: true);
    }

    private KernelResult<V6TemporalCapacityBinding> ReconcileQuarantinedCore(
        V6TemporalCapacityBinding supplied,
        Func<KernelResult> confirmEffectClosure,
        bool allowReportedOverrun = false)
    {
        ArgumentNullException.ThrowIfNull(confirmEffectClosure);
        V6TemporalCapacityBinding observed;
        lock (_sync)
        {
            if (!_bindings.TryGetValue(supplied.Id, out observed!) ||
                observed.State != V6TemporalCapacityOperationState.Quarantined ||
                observed.Owner != supplied.Owner || observed.Budget != supplied.Budget ||
                observed.ProviderReservation.Handle != supplied.ProviderReservation.Handle ||
                observed.ExternalOperation != supplied.ExternalOperation)
                return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.InvalidTransition,
                    "Only exact quarantined temporal capacity can request effect closure.");
            if (observed.ReportedOverrunNanoseconds is not null && !allowReportedOverrun)
                return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.BudgetExceeded,
                    "Reported compute time exceeds the reserved envelope; closure alone cannot settle unrepresentable usage.");
            if (observed.ReportedOverrunNanoseconds is null && allowReportedOverrun)
                return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.InvalidTransition,
                    "Corrective overrun settlement requires a recorded overrun.");
        }

        KernelResult closure;
        try { closure = confirmEffectClosure(); }
        catch (Exception exception)
        {
            return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.PlatformDenied,
                $"Effect closure owner failed: {exception.Message}");
        }
        if (!closure.IsSuccess)
            return KernelResult<V6TemporalCapacityBinding>.Fail(closure.Error, closure.Message!);

        lock (_sync)
        {
            if (!_bindings.TryGetValue(supplied.Id, out var binding) || binding != observed ||
                binding.State != V6TemporalCapacityOperationState.Quarantined)
                return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.StaleGeneration,
                    "Temporal capacity changed while effect closure was confirmed.");
            var budget = budgets.Query(binding.Budget);
            if (!budget.IsSuccess || budget.Value!.Owner != binding.Owner)
                return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.StaleGeneration,
                    "Temporal budget changed before reconciliation.");
            if (budget.Value.State == BudgetReservationState.Quarantined)
            {
                KernelResult<BudgetReservationSnapshot> settled;
                if (binding.ReportedOverrunNanoseconds is { } reported)
                    settled = budgets.SettleReportedComputeOverrun(binding.Owner, binding.Budget, reported, binding.Id);
                else
                {
                    var reconciled = budgets.ReconcileLease(binding.Owner, binding.Budget, binding.Id);
                    if (!reconciled.IsSuccess)
                        return KernelResult<V6TemporalCapacityBinding>.Fail(reconciled.Error, reconciled.Message!);
                    settled = budgets.SettleLease(binding.Owner, binding.Budget, [], binding.Id);
                }
                if (!settled.IsSuccess)
                    return KernelResult<V6TemporalCapacityBinding>.Fail(settled.Error, settled.Message!);
            }
            else if (budget.Value.State != BudgetReservationState.Released ||
                     binding.ReportedOverrunNanoseconds is { } exactCharge &&
                     budget.Value.ChargedAmounts?.SingleOrDefault(static amount =>
                         amount.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Amount != exactCharge)
                return KernelResult<V6TemporalCapacityBinding>.Fail(KernelError.InvalidTransition,
                    "Only a quarantined or exactly charged terminal budget can reconcile provider capacity.");
            var released = provider.ReconcileAndRelease(binding.ProviderReservation.Handle);
            var providerReservation = released.IsSuccess
                ? released.Value!
                : released.Error == KernelError.StaleGeneration &&
                  provider.ProviderGeneration != binding.ProviderReservation.ProviderGeneration
                    ? binding.ProviderReservation with { State = V6TemporalProviderReservationState.Released }
                    : null;
            if (providerReservation is null)
                return KernelResult<V6TemporalCapacityBinding>.Fail(released.Error, released.Message!);
            binding = binding with { ProviderReservation = providerReservation, State = V6TemporalCapacityOperationState.Settled };
            _bindings[binding.Id] = binding;
            return KernelResult<V6TemporalCapacityBinding>.Ok(binding);
        }
    }
}
