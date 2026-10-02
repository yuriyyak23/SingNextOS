using SingPlus.Contracts;

namespace SingPlus.Runtime;

internal readonly record struct V6StatefulSuspensionHandle(Guid Id, ulong Generation);

internal enum V6StatefulSuspensionState
{
    Suspended = 1,
    ResumeInFlight,
    DiscardInFlight,
    SettlementPending,
    Resumed,
    Discarded,
    Quarantined,
}

internal sealed record V6StatefulSuspensionReceipt(
    V6StatefulSuspensionHandle Handle,
    ResumeBindingV1 ResumeBinding,
    BudgetReservationHandle StorageReservation,
    ulong CapturedStateBytes,
    V6StatefulSuspensionState State)
{
    public BudgetReservationHandle StorageReservation { get; internal set; } = StorageReservation;
    public V6StatefulSuspensionState State { get; internal set; } = State;
    internal bool AuthorizesResume => false;
    internal bool PreservesCapability => false;
}

/// <summary>
/// Storage escrow and fresh-admission coordinator for an already captured opaque
/// provider state. It is not a capture provider and does not mint resume authority.
/// </summary>
internal sealed class V6StatefulResumeAccounting(ResourceBudgetAuthority budgets)
{
    private sealed class Record(V6StatefulSuspensionReceipt receipt)
    {
        internal V6StatefulSuspensionReceipt Receipt { get; set; } = receipt;
    }

    private readonly object _sync = new();
    private readonly Dictionary<Guid, Record> _records = [];
    private readonly HashSet<string> _pendingCorrelations = new(StringComparer.Ordinal);
    private bool _failNextSettlementForTest;
    private Action? _afterStorageBindForTest;
    private bool _failNextStorageRecordAllocationForTest;
    private Action<BudgetReservationHandle>? _beforeStorageBindForTest;

    internal void BeforeNextStorageBindForTest(Action<BudgetReservationHandle> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_sync) _beforeStorageBindForTest = callback;
    }

    internal void FailNextStorageRecordAllocationForTest()
    {
        lock (_sync) _failNextStorageRecordAllocationForTest = true;
    }

    internal void AfterNextStorageBindForTest(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_sync) _afterStorageBindForTest = callback;
    }

    internal void FailNextSettlementForTest()
    {
        lock (_sync) _failNextSettlementForTest = true;
    }

    internal KernelResult ValidateCaptureBudgetOwner(ProcessHandle owner) =>
        budgets.HasProcessAccount(owner)
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.BudgetNotConfigured,
                "Checkpoint source generation has no admitted budget hierarchy.");

    internal KernelResult<V6StatefulSuspensionReceipt> FindRecoverySuspension(
        ProcessHandle owner, ResumeBindingV1 binding)
    {
        try { binding.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.InvalidMessage, exception.Message); }
        lock (_sync)
        {
            var matches = _records.Values.Where(record =>
                record.Receipt.ResumeBinding == binding &&
                record.Receipt.State is V6StatefulSuspensionState.Quarantined or
                    V6StatefulSuspensionState.SettlementPending).ToArray();
            if (matches.Length != 1)
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(
                    matches.Length == 0 ? KernelError.StaleGeneration : KernelError.DuplicateIdentity,
                    "No unique quarantined suspension matches the exact provider binding.");
            var receipt = matches[0].Receipt;
            var storage = budgets.Query(receipt.StorageReservation);
            if (!storage.IsSuccess || storage.Value!.Owner != owner)
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.StaleGeneration,
                    "The exact captured-state storage owner is stale or different.");
            return KernelResult<V6StatefulSuspensionReceipt>.Ok(receipt);
        }
    }

    internal KernelResult<V6StatefulSuspensionReceipt> FindRecoverySuspensionByCorrelation(
        ProcessHandle owner, string operationCorrelation)
    {
        if (string.IsNullOrWhiteSpace(operationCorrelation))
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.InvalidMessage,
                "Captured-state recovery correlation is required.");
        lock (_sync)
        {
            var matches = _records.Values.Where(record =>
                record.Receipt.ResumeBinding.OperationCorrelation == operationCorrelation &&
                record.Receipt.State is V6StatefulSuspensionState.Quarantined or
                    V6StatefulSuspensionState.SettlementPending).ToArray();
            if (matches.Length != 1)
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(
                    matches.Length == 0 ? KernelError.StaleGeneration : KernelError.DuplicateIdentity,
                    "No unique quarantined suspension matches the recovery correlation.");
            var receipt = matches[0].Receipt;
            var storage = budgets.Query(receipt.StorageReservation);
            return storage.IsSuccess && storage.Value!.Owner == owner
                ? KernelResult<V6StatefulSuspensionReceipt>.Ok(receipt)
                : KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.StaleGeneration,
                    "The exact captured-state storage owner is stale or different.");
        }
    }

    internal KernelResult<V6StatefulSuspensionReceipt> AdmitCapturedState(
        ProcessHandle owner,
        ResumeBindingV1 resumeBinding,
        ulong capturedStateBytes,
        Func<ulong> currentProviderGeneration,
        Func<ulong> currentRuntimeGeneration)
    {
        ArgumentNullException.ThrowIfNull(currentProviderGeneration);
        ArgumentNullException.ThrowIfNull(currentRuntimeGeneration);
        try { resumeBinding.Validate(); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.InvalidMessage, exception.Message); }
        if (capturedStateBytes == 0)
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.InvalidMessage, "Captured state storage must be non-zero.");
        ulong providerGeneration;
        ulong runtimeGeneration;
        try { providerGeneration = currentProviderGeneration(); runtimeGeneration = currentRuntimeGeneration(); }
        catch (Exception exception)
        { return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.PlatformUnavailable, exception.Message); }
        if (providerGeneration != resumeBinding.ProviderGeneration || runtimeGeneration != resumeBinding.RuntimeGeneration)
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.StaleGeneration,
                "Captured state generations changed before storage admission.");

        var handle = new V6StatefulSuspensionHandle(Guid.NewGuid(), 1);
        V6StatefulSuspensionReceipt receipt;
        try
        {
          lock (_sync)
          {
            if (_pendingCorrelations.Contains(resumeBinding.OperationCorrelation) ||
                _records.Values.Any(record => record.Receipt.ResumeBinding.OperationCorrelation == resumeBinding.OperationCorrelation &&
                    (record.Receipt.State is not (V6StatefulSuspensionState.Resumed or
                        V6StatefulSuspensionState.Discarded or V6StatefulSuspensionState.SettlementPending) ||
                     (record.Receipt.State == V6StatefulSuspensionState.SettlementPending &&
                        record.Receipt.ResumeBinding == resumeBinding))))
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.DuplicateIdentity,
                    "The captured operation correlation already has a live suspension escrow.");
            _pendingCorrelations.Add(resumeBinding.OperationCorrelation);
            try
            {
                if (_failNextStorageRecordAllocationForTest)
                {
                    _failNextStorageRecordAllocationForTest = false;
                    throw new OutOfMemoryException("Injected storage record allocation failure.");
                }
                receipt = new(handle, resumeBinding, default, capturedStateBytes, V6StatefulSuspensionState.Suspended);
                _records.Add(handle.Id, new(receipt));
            }
            catch
            {
                _pendingCorrelations.Remove(resumeBinding.OperationCorrelation);
                throw;
            }
          }
        }
        catch (Exception exception) when (exception is ArgumentException or OutOfMemoryException)
        { return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.CapacityExhausted, exception.Message); }

        var reserved = budgets.ReserveCheckpointStorage(owner, capturedStateBytes);
        if (!reserved.IsSuccess)
        {
            lock (_sync) _records.Remove(handle.Id);
            RemovePending(resumeBinding.OperationCorrelation);
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(reserved.Error, reserved.Message!);
        }
        var reservation = reserved.Value!.Reservation;
        receipt.StorageReservation = reservation;
        Action<BudgetReservationHandle>? beforeStorageBind;
        lock (_sync)
        {
            beforeStorageBind = _beforeStorageBindForTest;
            _beforeStorageBindForTest = null;
        }
        KernelResult<BudgetReservationSnapshot> bound;
        try
        {
            beforeStorageBind?.Invoke(reservation);
            bound = budgets.BindLease(owner, reservation);
        }
        catch (Exception exception)
        { bound = KernelResult<BudgetReservationSnapshot>.Fail(KernelError.PlatformUnavailable, exception.Message); }
        if (!bound.IsSuccess)
        {
            var released = budgets.Release(owner, reservation);
            if (!released.IsSuccess || released.Value!.Reservation != reservation || released.Value.Owner != owner ||
                released.Value.State is not (BudgetReservationState.Released or BudgetReservationState.CancelledPreSubmit))
            {
                _ = budgets.QuarantineLease(owner, reservation);
                lock (_sync)
                {
                    receipt.State = V6StatefulSuspensionState.Quarantined;
                    _pendingCorrelations.Remove(resumeBinding.OperationCorrelation);
                }
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.PlatformFaulted,
                    "Captured-state bind failed and exact budget cleanup was not confirmed; recovery escrow is retained.");
            }
            lock (_sync) _records.Remove(handle.Id);
            RemovePending(resumeBinding.OperationCorrelation);
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(bound.Error, bound.Message!);
        }
        Action? afterStorageBind;
        lock (_sync)
        {
            afterStorageBind = _afterStorageBindForTest;
            _afterStorageBindForTest = null;
        }
        var generationsCurrent = GenerationsMatch(resumeBinding,
            afterStorageBind is null ? currentProviderGeneration : () =>
            {
                afterStorageBind();
                return currentProviderGeneration();
            }, currentRuntimeGeneration, out var generationError);
        if (!generationsCurrent)
        {
            // The payload was captured before this escrow admission. A generation
            // loss cannot prove its closure or authorize releasing storage.
            var quarantined = budgets.QuarantineLease(owner, reservation);
            if (!quarantined.IsSuccess)
                generationError = KernelResult.Fail(KernelError.PlatformFaulted,
                    "Captured-state continuity was lost and storage quarantine was not confirmed.");
        }
        lock (_sync)
        {
            receipt.State = generationsCurrent ? V6StatefulSuspensionState.Suspended : V6StatefulSuspensionState.Quarantined;
            _pendingCorrelations.Remove(resumeBinding.OperationCorrelation);
        }
        return generationsCurrent
            ? KernelResult<V6StatefulSuspensionReceipt>.Ok(receipt)
            : KernelResult<V6StatefulSuspensionReceipt>.Fail(generationError.Error, generationError.Message!);
    }

    internal KernelResult<V6StatefulSuspensionReceipt> Resume(
        ProcessHandle owner,
        V6StatefulSuspensionHandle handle,
        Func<KernelResult> singNextAdmission,
        Func<KernelResult> providerAdmission,
        Func<KernelResult> runtimeLegality,
        Func<ulong> currentProviderGeneration,
        Func<ulong> currentRuntimeGeneration,
        Func<KernelResult> providerResume)
    {
        ArgumentNullException.ThrowIfNull(singNextAdmission);
        ArgumentNullException.ThrowIfNull(providerAdmission);
        ArgumentNullException.ThrowIfNull(runtimeLegality);
        ArgumentNullException.ThrowIfNull(currentProviderGeneration);
        ArgumentNullException.ThrowIfNull(currentRuntimeGeneration);
        ArgumentNullException.ThrowIfNull(providerResume);
        Record record;
        lock (_sync)
        {
            var resolved = Resolve(handle);
            if (!resolved.IsSuccess) return KernelResult<V6StatefulSuspensionReceipt>.Fail(resolved.Error, resolved.Message!);
            record = resolved.Value!;
            if (record.Receipt.State != V6StatefulSuspensionState.Suspended)
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.InvalidTransition,
                    "Only an exactly suspended captured state can resume.");
            var initialStorage = budgets.Query(record.Receipt.StorageReservation);
            if (!initialStorage.IsSuccess || initialStorage.Value!.Owner != owner ||
                initialStorage.Value.State != BudgetReservationState.Bound)
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.StaleGeneration,
                    "Captured-state storage owner or reservation changed before resume admission.");
        }
        foreach (var gate in new[] { singNextAdmission, providerAdmission, runtimeLegality })
        {
            KernelResult decision;
            try { decision = gate(); }
            catch (Exception exception) { return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.PlatformDenied, exception.Message); }
            if (!decision.IsSuccess)
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(decision.Error, decision.Message!);
        }
        if (!GenerationsMatch(record.Receipt.ResumeBinding, currentProviderGeneration, currentRuntimeGeneration, out var generationError))
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(generationError.Error, generationError.Message!);
        var storage = budgets.Query(record.Receipt.StorageReservation);
        if (!storage.IsSuccess || storage.Value!.Owner != owner || storage.Value.State != BudgetReservationState.Bound ||
            storage.Value.Lifetime != BudgetReservationLifetime.CheckpointImage ||
            storage.Value.Amounts.SingleOrDefault(static amount => amount.Dimension == ServiceBudgetDimension.CheckpointStorageBytes).Amount != record.Receipt.CapturedStateBytes)
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.StaleGeneration,
                "Captured-state storage escrow changed before resume.");

        lock (_sync)
        {
            var current = Resolve(handle);
            if (!current.IsSuccess || current.Value!.Receipt.State != V6StatefulSuspensionState.Suspended)
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.InvalidTransition,
                    "Captured state was concurrently consumed or discarded.");
            current.Value.Receipt = current.Value.Receipt with { State = V6StatefulSuspensionState.ResumeInFlight };
            record = current.Value;
        }

        // The first pass filters invalid requests; these mutable decisions must
        // be fresh again after this suspension has a single resume winner.
        foreach (var gate in new[] { singNextAdmission, providerAdmission, runtimeLegality })
        {
            KernelResult decision;
            try { decision = gate(); }
            catch (Exception exception)
            { decision = KernelResult.Fail(KernelError.PlatformDenied, exception.Message); }
            if (decision.IsSuccess) continue;
            lock (_sync) record.Receipt = record.Receipt with { State = V6StatefulSuspensionState.Suspended };
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(decision.Error, decision.Message!);
        }
        if (!GenerationsMatch(record.Receipt.ResumeBinding,
                currentProviderGeneration, currentRuntimeGeneration, out generationError))
        {
            lock (_sync) record.Receipt = record.Receipt with { State = V6StatefulSuspensionState.Suspended };
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(generationError.Error, generationError.Message!);
        }

        // Admission and generation callbacks run outside the escrow owner lock.
        // Their success cannot preserve a reservation that the budget owner changed.
        storage = budgets.Query(record.Receipt.StorageReservation);
        if (!storage.IsSuccess || storage.Value!.Owner != owner || storage.Value.State != BudgetReservationState.Bound ||
            storage.Value.Lifetime != BudgetReservationLifetime.CheckpointImage ||
            storage.Value.Amounts.SingleOrDefault(static amount => amount.Dimension == ServiceBudgetDimension.CheckpointStorageBytes).Amount != record.Receipt.CapturedStateBytes)
        {
            lock (_sync) record.Receipt = record.Receipt with { State = V6StatefulSuspensionState.Suspended };
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.StaleGeneration,
                "Captured-state storage escrow changed during final resume admission.");
        }

        KernelResult resumed;
        try { resumed = providerResume(); }
        catch (Exception exception) { resumed = KernelResult.Fail(KernelError.PlatformUnavailable, exception.Message); }
        var generationsStillMatch = GenerationsMatch(record.Receipt.ResumeBinding,
            currentProviderGeneration, currentRuntimeGeneration, out _);
        if (!resumed.IsSuccess || !generationsStillMatch)
        {
            _ = budgets.QuarantineLease(owner, record.Receipt.StorageReservation);
            lock (_sync) record.Receipt = record.Receipt with { State = V6StatefulSuspensionState.Quarantined };
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(
                resumed.IsSuccess ? KernelError.StaleGeneration : resumed.Error,
                resumed.IsSuccess ? "Resume generations changed during the provider callback." : resumed.Message!);
        }
        var released = budgets.Release(owner, record.Receipt.StorageReservation);
        if (!released.IsSuccess)
        {
            _ = budgets.QuarantineLease(owner, record.Receipt.StorageReservation);
            lock (_sync) record.Receipt = record.Receipt with { State = V6StatefulSuspensionState.Quarantined };
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.PlatformFaulted,
                "Provider resume succeeded but captured-state storage could not be released.");
        }
        lock (_sync) record.Receipt = record.Receipt with { State = V6StatefulSuspensionState.Resumed };
        return KernelResult<V6StatefulSuspensionReceipt>.Ok(record.Receipt);
    }

    internal KernelResult<V6StatefulSuspensionReceipt> Discard(
        ProcessHandle owner, V6StatefulSuspensionHandle handle)
    {
        lock (_sync)
        {
            var resolved = Resolve(handle);
            if (!resolved.IsSuccess) return KernelResult<V6StatefulSuspensionReceipt>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.Receipt.State == V6StatefulSuspensionState.Discarded)
                return KernelResult<V6StatefulSuspensionReceipt>.Ok(record.Receipt);
            if (record.Receipt.State != V6StatefulSuspensionState.Suspended)
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.InvalidTransition,
                    "Only a suspended captured state can be discarded normally.");
            var released = budgets.Release(owner, record.Receipt.StorageReservation);
            if (!released.IsSuccess)
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(released.Error, released.Message!);
            record.Receipt = record.Receipt with { State = V6StatefulSuspensionState.Discarded };
            return KernelResult<V6StatefulSuspensionReceipt>.Ok(record.Receipt);
        }
    }

    internal KernelResult<V6StatefulSuspensionReceipt> DiscardWithProvider(
        ProcessHandle owner,
        V6StatefulSuspensionHandle handle,
        Func<KernelResult> providerDiscard)
    {
        ArgumentNullException.ThrowIfNull(providerDiscard);
        Record record;
        lock (_sync)
        {
            var resolved = Resolve(handle);
            if (!resolved.IsSuccess)
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(resolved.Error, resolved.Message!);
            record = resolved.Value!;
            if (record.Receipt.State != V6StatefulSuspensionState.Suspended)
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.InvalidTransition,
                    "Only suspended state can begin provider discard.");
            var storage = budgets.Query(record.Receipt.StorageReservation);
            if (!storage.IsSuccess || storage.Value!.Owner != owner ||
                storage.Value.State != BudgetReservationState.Bound)
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.StaleGeneration,
                    "Captured-state storage owner or reservation changed before discard.");
            record.Receipt = record.Receipt with { State = V6StatefulSuspensionState.DiscardInFlight };
        }

        KernelResult discarded;
        try { discarded = providerDiscard(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { discarded = KernelResult.Fail(KernelError.PlatformUnavailable, exception.Message); }
        if (!discarded.IsSuccess)
        {
            _ = budgets.QuarantineLease(owner, record.Receipt.StorageReservation);
            lock (_sync) record.Receipt = record.Receipt with { State = V6StatefulSuspensionState.Quarantined };
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(discarded.Error, discarded.Message!);
        }

        var released = budgets.Release(owner, record.Receipt.StorageReservation);
        if (!released.IsSuccess)
        {
            _ = budgets.QuarantineLease(owner, record.Receipt.StorageReservation);
            lock (_sync) record.Receipt = record.Receipt with { State = V6StatefulSuspensionState.Quarantined };
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.PlatformFaulted,
                "Provider discard succeeded but captured-state storage could not be released.");
        }
        lock (_sync) record.Receipt = record.Receipt with { State = V6StatefulSuspensionState.Discarded };
        return KernelResult<V6StatefulSuspensionReceipt>.Ok(record.Receipt);
    }

    internal KernelResult<V6StatefulSuspensionReceipt> ReconcileQuarantinedDiscard(
        ProcessHandle owner, V6StatefulSuspensionHandle handle)
    {
        lock (_sync)
        {
            var resolved = Resolve(handle);
            if (!resolved.IsSuccess) return KernelResult<V6StatefulSuspensionReceipt>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.Receipt.State != V6StatefulSuspensionState.Quarantined)
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.InvalidTransition,
                    "Only quarantined captured-state storage requires reconciliation.");
            var reconciled = budgets.ReconcileLease(owner, record.Receipt.StorageReservation);
            if (!reconciled.IsSuccess)
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(reconciled.Error, reconciled.Message!);
            var settled = budgets.SettleLease(owner, record.Receipt.StorageReservation, []);
            if (!settled.IsSuccess)
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(settled.Error, settled.Message!);
            record.Receipt = record.Receipt with { State = V6StatefulSuspensionState.Discarded };
            return KernelResult<V6StatefulSuspensionReceipt>.Ok(record.Receipt);
        }
    }

    internal KernelResult<V6StatefulSuspensionReceipt> ReconcileQuarantinedDiscardWithProvider(
        ProcessHandle owner,
        V6StatefulSuspensionHandle handle,
        Func<KernelResult> providerDiscard)
    {
        ArgumentNullException.ThrowIfNull(providerDiscard);
        Record record;
        bool providerClosurePending;
        lock (_sync)
        {
            var resolved = Resolve(handle);
            if (!resolved.IsSuccess)
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(resolved.Error, resolved.Message!);
            record = resolved.Value!;
            providerClosurePending = record.Receipt.State == V6StatefulSuspensionState.Quarantined;
            if (!providerClosurePending && record.Receipt.State != V6StatefulSuspensionState.SettlementPending)
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.InvalidTransition,
                    "Only quarantined or settlement-pending captured state can be reconciled.");
            var storage = budgets.Query(record.Receipt.StorageReservation);
            if (!storage.IsSuccess || storage.Value!.Owner != owner ||
                (providerClosurePending
                    ? storage.Value.State != BudgetReservationState.Quarantined
                    : storage.Value.State is not (BudgetReservationState.Quarantined or BudgetReservationState.Reconciled)))
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.StaleGeneration,
                    "Captured-state storage owner or settlement state changed before reconciliation.");
            record.Receipt = record.Receipt with { State = V6StatefulSuspensionState.DiscardInFlight };
        }

        if (providerClosurePending)
        {
            KernelResult discarded;
            try { discarded = providerDiscard(); }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            { discarded = KernelResult.Fail(KernelError.PlatformUnavailable, exception.Message); }
            if (!discarded.IsSuccess)
            {
                lock (_sync) record.Receipt = record.Receipt with { State = V6StatefulSuspensionState.Quarantined };
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(discarded.Error, discarded.Message!);
            }
        }
        var reconciled = budgets.ReconcileLease(owner, record.Receipt.StorageReservation);
        if (!reconciled.IsSuccess)
        {
            lock (_sync) record.Receipt = record.Receipt with { State = V6StatefulSuspensionState.SettlementPending };
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(reconciled.Error, reconciled.Message!);
        }
        lock (_sync)
        {
            if (_failNextSettlementForTest)
            {
                _failNextSettlementForTest = false;
                record.Receipt = record.Receipt with { State = V6StatefulSuspensionState.SettlementPending };
                return KernelResult<V6StatefulSuspensionReceipt>.Fail(KernelError.PlatformFaulted,
                    "Injected settlement interruption after provider closure.");
            }
        }
        var settled = budgets.SettleLease(owner, record.Receipt.StorageReservation, []);
        if (!settled.IsSuccess)
        {
            lock (_sync) record.Receipt = record.Receipt with { State = V6StatefulSuspensionState.SettlementPending };
            return KernelResult<V6StatefulSuspensionReceipt>.Fail(settled.Error, settled.Message!);
        }
        lock (_sync) record.Receipt = record.Receipt with { State = V6StatefulSuspensionState.Discarded };
        return KernelResult<V6StatefulSuspensionReceipt>.Ok(record.Receipt);
    }

    private KernelResult<Record> Resolve(V6StatefulSuspensionHandle handle) =>
        handle.Id != Guid.Empty && handle.Generation == 1 && _records.TryGetValue(handle.Id, out var record) && record.Receipt.Handle == handle
            ? KernelResult<Record>.Ok(record)
            : KernelResult<Record>.Fail(KernelError.StaleGeneration, "Stateful suspension handle is missing or stale.");

    private void RemovePending(string correlation)
    {
        lock (_sync) _pendingCorrelations.Remove(correlation);
    }

    private static bool GenerationsMatch(
        ResumeBindingV1 binding,
        Func<ulong> currentProviderGeneration,
        Func<ulong> currentRuntimeGeneration,
        out KernelResult error)
    {
        try
        {
            if (currentProviderGeneration() == binding.ProviderGeneration &&
                currentRuntimeGeneration() == binding.RuntimeGeneration)
            {
                error = KernelResult.Ok();
                return true;
            }
            error = KernelResult.Fail(KernelError.StaleGeneration, "Resume provider or runtime generation is stale.");
            return false;
        }
        catch (Exception exception)
        {
            error = KernelResult.Fail(KernelError.PlatformUnavailable, exception.Message);
            return false;
        }
    }
}
