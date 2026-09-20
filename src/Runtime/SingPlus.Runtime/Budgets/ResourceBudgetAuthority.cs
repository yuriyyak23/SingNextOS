using SingPlus.Contracts;

namespace SingPlus.Runtime;

/// <summary>Single accounting ledger. Reservations admit capacity but never authorize effects.</summary>
public sealed class ResourceBudgetAuthority
{
    private sealed class AccountRecord
    {
        public required BudgetAccountHandle Handle { get; init; }
        public required BudgetAccountLevel Level { get; init; }
        public required BudgetAccountHandle? Parent { get; init; }
        public required string OwnerCorrelation { get; init; }
        public required Dictionary<ServiceBudgetDimension, ulong> Limits { get; init; }
        public Dictionary<ServiceBudgetDimension, ulong> Used { get; } = AllDimensions.ToDictionary(static dimension => dimension, static _ => 0UL);
        public bool Retired { get; set; }
    }

    private sealed class ReservationRecord
    {
        public required BudgetReservationHandle Handle { get; init; }
        public required BudgetAccountHandle Account { get; init; }
        public required ProcessHandle Owner { get; init; }
        public required BudgetAmount[] Amounts { get; set; }
        public required BudgetReservationLifetime Lifetime { get; init; }
        public required AdmissionQosHint QosHint { get; init; }
        public BudgetReservationState State { get; set; }
        public BudgetAmount[] ChargedAmounts { get; set; } = [];
        public bool SettlementTerminal { get; set; }
    }

    private static readonly ServiceBudgetDimension[] AllDimensions = Enum.GetValues<ServiceBudgetDimension>();
    private readonly object _gate = new();
    private readonly Dictionary<BudgetAccountId, AccountRecord> _accounts = [];
    private readonly Dictionary<BudgetReservationId, ReservationRecord> _reservations = [];
    private readonly Dictionary<ProcessHandle, BudgetAccountHandle> _processAccounts = [];
    private ulong _nextAccountId = 2;
    private ulong _nextReservationId = 1;

    internal ResourceBudgetAuthority(ulong initialReservationId = 1)
    {
        if (initialReservationId == 0) throw new ArgumentOutOfRangeException(nameof(initialReservationId));
        _nextReservationId = initialReservationId;
        SystemBudget = new(new(1), new(1));
        _accounts.Add(SystemBudget.AccountId, new AccountRecord
        {
            Handle = SystemBudget,
            Level = BudgetAccountLevel.System,
            Parent = null,
            OwnerCorrelation = "system",
            Limits = AllDimensions.ToDictionary(static dimension => dimension, static _ => ulong.MaxValue),
        });
    }

    public BudgetAccountHandle SystemBudget { get; }

    internal bool HasProcessAccount(ProcessHandle process)
    {
        lock (_gate) return _processAccounts.ContainsKey(process);
    }

    internal KernelResult<BudgetAccountSnapshot> ConfigureSystem(IReadOnlyList<BudgetAmount> limits)
    {
        lock (_gate)
        {
            var validation = ValidateAmounts(limits);
            if (!validation.IsSuccess) return KernelResult<BudgetAccountSnapshot>.Fail(validation.Error, validation.Message!);
            if (_accounts.Count != 1 || _reservations.Values.Any(static reservation => IsCapacityHeld(reservation.State)))
                return KernelResult<BudgetAccountSnapshot>.Fail(KernelError.InvalidTransition, "System budget can be configured only before child allocation or reservations.");
            var root = _accounts[SystemBudget.AccountId];
            foreach (var amount in limits) root.Limits[amount.Dimension] = amount.Amount;
            return KernelResult<BudgetAccountSnapshot>.Ok(Snapshot(root));
        }
    }

    internal KernelResult<BudgetAccountSnapshot> CreateChild(
        BudgetAccountHandle parent,
        BudgetAccountLevel level,
        string ownerCorrelation,
        IReadOnlyList<BudgetAmount> limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerCorrelation);
        lock (_gate)
        {
            var parentResult = ResolveAccount(parent);
            if (!parentResult.IsSuccess) return KernelResult<BudgetAccountSnapshot>.Fail(parentResult.Error, parentResult.Message!);
            var parentRecord = parentResult.Value!;
            if ((int)level != (int)parentRecord.Level + 1)
                return KernelResult<BudgetAccountSnapshot>.Fail(KernelError.InvalidTransition, "Budget hierarchy must be System -> Service -> ProcessDomain.");
            if (limits.Count > 0)
            {
                var validation = ValidateAmounts(limits);
                if (!validation.IsSuccess) return KernelResult<BudgetAccountSnapshot>.Fail(validation.Error, validation.Message!);
            }
            var selected = new Dictionary<ServiceBudgetDimension, ulong>(parentRecord.Limits);
            foreach (var limit in limits)
            {
                if (limit.Amount > parentRecord.Limits[limit.Dimension])
                    return KernelResult<BudgetAccountSnapshot>.Fail(KernelError.BudgetExceeded, $"Child {limit.Dimension} limit exceeds its parent limit.");
                selected[limit.Dimension] = limit.Amount;
            }
            if (_nextAccountId == 0)
                return KernelResult<BudgetAccountSnapshot>.Fail(KernelError.CapacityExhausted, "Budget account identity space is exhausted.");
            var handle = new BudgetAccountHandle(new(_nextAccountId++), new(1));
            var record = new AccountRecord
            {
                Handle = handle,
                Level = level,
                Parent = parent,
                OwnerCorrelation = ownerCorrelation,
                Limits = selected,
            };
            _accounts.Add(handle.AccountId, record);
            return KernelResult<BudgetAccountSnapshot>.Ok(Snapshot(record));
        }
    }

    internal KernelResult AttachProcess(ProcessHandle process, BudgetAccountHandle account)
    {
        lock (_gate)
        {
            var resolved = ResolveAccount(account);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            if (resolved.Value!.Level != BudgetAccountLevel.ProcessDomain)
                return KernelResult.Fail(KernelError.InvalidTransition, "Only a ProcessDomain budget can attach to a process generation.");
            if (_processAccounts.TryGetValue(process, out var existing))
                return existing == account
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.DuplicateIdentity, "Process generation already has a different budget account.");
            _processAccounts.Add(process, account);
            return KernelResult.Ok();
        }
    }

    internal KernelResult<BudgetReservationSnapshot> Reserve(
        ProcessHandle owner,
        IReadOnlyList<BudgetAmount> amounts,
        BudgetReservationLifetime lifetime,
        AdmissionQosHint qosHint)
    {
        lock (_gate)
        {
            if (!_processAccounts.TryGetValue(owner, out var account))
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.BudgetNotConfigured, "Process generation has no admitted budget hierarchy.");
            return ReserveCore(owner, account, amounts, lifetime, qosHint);
        }
    }

    internal KernelResult<BudgetReservationSnapshot> ReserveCheckpointStorage(
        ProcessHandle owner,
        ulong bytes)
    {
        lock (_gate)
        {
            if (!_processAccounts.TryGetValue(owner, out var account))
                return KernelResult<BudgetReservationSnapshot>.Fail(
                    KernelError.BudgetNotConfigured,
                    "Checkpoint source generation has no admitted budget hierarchy.");
            return ReserveCore(owner, account,
                [new(ServiceBudgetDimension.CheckpointStorageBytes, bytes)],
                BudgetReservationLifetime.CheckpointImage,
                AdmissionQosHint.Background);
        }
    }

    internal KernelResult<BudgetReservationSnapshot?> ReserveIfAttached(
        ProcessHandle owner,
        IReadOnlyList<BudgetAmount> amounts,
        BudgetReservationLifetime lifetime,
        AdmissionQosHint qosHint = AdmissionQosHint.None)
    {
        lock (_gate)
        {
            if (!_processAccounts.TryGetValue(owner, out var account))
                return KernelResult<BudgetReservationSnapshot?>.Ok(null);
            var reserved = ReserveCore(owner, account, amounts, lifetime, qosHint);
            return reserved.IsSuccess
                ? KernelResult<BudgetReservationSnapshot?>.Ok(reserved.Value)
                : KernelResult<BudgetReservationSnapshot?>.Fail(reserved.Error, reserved.Message!);
        }
    }

    internal KernelResult<BudgetReservationSnapshot> Release(
        ProcessHandle owner,
        BudgetReservationHandle reservation)
    {
        lock (_gate)
        {
            if (!_reservations.TryGetValue(reservation.ReservationId, out var record) || record.Handle != reservation)
                return KernelResult<BudgetReservationSnapshot>.Ok(Stale(reservation, owner));
            if (record.Owner != owner)
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.WrongRegionOwner, "Budget reservation belongs to another process generation.");
            if (record.State == BudgetReservationState.Released)
                return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
            if (record.State == BudgetReservationState.CancelledPreSubmit)
                return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
            if (record.State is BudgetReservationState.Consuming or BudgetReservationState.Quarantined or BudgetReservationState.Reconciled or BudgetReservationState.Settling)
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition, "A possibly consumed lease requires settlement or reconciliation, not release.");

            foreach (var account in Ancestors(record.Account))
            foreach (var amount in record.Amounts)
            {
                if (account.Used[amount.Dimension] < amount.Amount)
                    return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.PlatformFaulted, "Budget ledger underflow was prevented.");
            }
            foreach (var account in Ancestors(record.Account))
            foreach (var amount in record.Amounts)
                account.Used[amount.Dimension] -= amount.Amount;
            record.State = BudgetReservationState.Released;
            return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
        }
    }

    internal KernelResult<BudgetReservationSnapshot> BindLease(ProcessHandle owner, BudgetReservationHandle reservation) =>
        Transition(owner, reservation, BudgetReservationState.Reserved, BudgetReservationState.Bound);

    internal KernelResult<BudgetReservationSnapshot> BeginConsumption(ProcessHandle owner, BudgetReservationHandle reservation) =>
        Transition(owner, reservation, BudgetReservationState.Bound, BudgetReservationState.Consuming);

    internal KernelResult<BudgetReservationSnapshot> QuarantineLease(ProcessHandle owner, BudgetReservationHandle reservation)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.State == BudgetReservationState.Quarantined) return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
            if (record.State is not (BudgetReservationState.Bound or BudgetReservationState.Consuming))
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition, "Only a bound or consuming lease can become quarantined.");
            record.State = BudgetReservationState.Quarantined;
            return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
        }
    }

    internal KernelResult<BudgetReservationSnapshot> ReconcileLease(ProcessHandle owner, BudgetReservationHandle reservation)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.State == BudgetReservationState.Reconciled) return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
            if (record.State != BudgetReservationState.Quarantined)
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition, "Only a quarantined lease can be reconciled.");
            record.State = BudgetReservationState.Reconciled;
            return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
        }
    }

    internal KernelResult<BudgetReservationSnapshot> CancelLeasePreSubmit(ProcessHandle owner, BudgetReservationHandle reservation)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.State == BudgetReservationState.CancelledPreSubmit) return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
            if (record.State is not (BudgetReservationState.Reserved or BudgetReservationState.Bound))
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition, "Pre-submit cancellation is no longer safe.");
            var released = ReleaseAmounts(record, record.Amounts);
            if (!released.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(released.Error, released.Message!);
            record.State = BudgetReservationState.CancelledPreSubmit;
            return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
        }
    }

    internal KernelResult<BudgetReservationSnapshot> SettleLease(
        ProcessHandle owner, BudgetReservationHandle reservation, IReadOnlyList<BudgetAmount> actualUsage)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.SettlementTerminal) return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
            if (record.State is not (BudgetReservationState.Consuming or BudgetReservationState.Reconciled))
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition, "Lease is not eligible for settlement.");
            var validation = actualUsage.Count == 0 ? KernelResult.Ok() : ValidateAmounts(actualUsage);
            if (!validation.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(validation.Error, validation.Message!);
            var reserved = record.Amounts.ToDictionary(static amount => amount.Dimension, static amount => amount.Amount);
            if (actualUsage.Any(amount => !reserved.TryGetValue(amount.Dimension, out var maximum) || amount.Amount > maximum))
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.BudgetExceeded, "Settlement usage exceeds the reserved envelope.");
            var charged = actualUsage.OrderBy(static amount => amount.Dimension).ToArray();
            var unused = record.Amounts.Select(amount => new BudgetAmount(amount.Dimension,
                amount.Amount - charged.FirstOrDefault(item => item.Dimension == amount.Dimension).Amount))
                .Where(static amount => amount.Amount != 0).ToArray();
            record.State = BudgetReservationState.Settling;
            var released = ReleaseAmounts(record, unused);
            if (!released.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(released.Error, released.Message!);
            record.ChargedAmounts = charged;
            record.SettlementTerminal = true;
            record.State = BudgetReservationState.Released;
            return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
        }
    }

    internal KernelResult<BudgetReservationSnapshot> SplitLease(
        ProcessHandle owner, BudgetReservationHandle reservation, IReadOnlyList<BudgetAmount> childAmounts)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var parent = resolved.Value!;
            if (parent.State != BudgetReservationState.Reserved)
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition, "Only a reserved lease can be split.");
            var validation = ValidateAmounts(childAmounts);
            if (!validation.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(validation.Error, validation.Message!);
            var requested = childAmounts.ToDictionary(static amount => amount.Dimension, static amount => amount.Amount);
            if (parent.Amounts.Any(amount => requested.GetValueOrDefault(amount.Dimension) >= amount.Amount) ||
                requested.Keys.Any(dimension => parent.Amounts.All(amount => amount.Dimension != dimension)))
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.BudgetExceeded, "Split must be a strict positive subset and leave parent capacity.");
            if (_nextReservationId == 0)
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.CapacityExhausted, "Budget reservation identity space is exhausted.");
            parent.Amounts = parent.Amounts.Select(amount => new BudgetAmount(amount.Dimension,
                amount.Amount - requested.GetValueOrDefault(amount.Dimension))).ToArray();
            var handle = AllocateReservationHandle();
            var child = new ReservationRecord
            {
                Handle = handle, Account = parent.Account, Owner = owner,
                Amounts = childAmounts.OrderBy(static amount => amount.Dimension).ToArray(),
                Lifetime = parent.Lifetime, QosHint = parent.QosHint, State = BudgetReservationState.Reserved,
            };
            _reservations.Add(handle.ReservationId, child);
            return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(child));
        }
    }

    internal KernelResult<BudgetAccountSnapshot> Query(BudgetAccountHandle account)
    {
        lock (_gate)
        {
            var resolved = ResolveAccount(account);
            return resolved.IsSuccess
                ? KernelResult<BudgetAccountSnapshot>.Ok(Snapshot(resolved.Value!))
                : KernelResult<BudgetAccountSnapshot>.Fail(resolved.Error, resolved.Message!);
        }
    }

    internal KernelResult<BudgetReservationSnapshot> Query(BudgetReservationHandle reservation)
    {
        lock (_gate)
            return _reservations.TryGetValue(reservation.ReservationId, out var record) && record.Handle == reservation
                ? KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record))
                : KernelResult<BudgetReservationSnapshot>.Fail(KernelError.BudgetReservationNotFound, "Budget reservation was not found or is stale.");
    }

    internal BudgetReservationSnapshot[] InspectionSnapshot()
    {
        lock (_gate)
            return _reservations.Values
                .Where(static reservation => IsCapacityHeld(reservation.State))
                .Select(Snapshot)
                .OrderBy(static reservation => reservation.Reservation.ReservationId.Value)
                .ToArray();
    }

    internal KernelResult RetireProcessHierarchy(
        ProcessHandle process,
        BudgetAccountHandle processAccount,
        BudgetAccountHandle serviceAccount)
    {
        lock (_gate)
        {
            if (!_processAccounts.TryGetValue(process, out var attached) || attached != processAccount)
                return KernelResult.Fail(KernelError.StaleGeneration, "Process budget attachment is stale.");
            if (_reservations.Values.Any(record => IsCapacityHeld(record.State) &&
                                                   record.Account == processAccount &&
                                                   record.Lifetime != BudgetReservationLifetime.CheckpointImage))
                return KernelResult.Fail(KernelError.ReplacementBlocked, "Active live-resource reservations keep the process budget charged.");
            var processResolved = ResolveAccount(processAccount);
            var serviceResolved = ResolveAccount(serviceAccount);
            if (!processResolved.IsSuccess || !serviceResolved.IsSuccess)
                return KernelResult.Fail(KernelError.StaleGeneration, "Budget hierarchy is stale.");
            _processAccounts.Remove(process);
            processResolved.Value!.Retired = true;
            serviceResolved.Value!.Retired = true;
            return KernelResult.Ok();
        }
    }

    private KernelResult<BudgetReservationSnapshot> ReserveCore(
        ProcessHandle owner,
        BudgetAccountHandle account,
        IReadOnlyList<BudgetAmount> amounts,
        BudgetReservationLifetime lifetime,
        AdmissionQosHint qosHint)
    {
        if (!Enum.IsDefined(lifetime) || !Enum.IsDefined(qosHint))
            return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidMessage, "Budget lifetime or QoS hint is invalid.");
        var validation = ValidateAmounts(amounts);
        if (!validation.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(validation.Error, validation.Message!);
        var accountResult = ResolveAccount(account);
        if (!accountResult.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(accountResult.Error, accountResult.Message!);
        var chain = Ancestors(account).ToArray();
        foreach (var ancestor in chain)
        foreach (var amount in amounts)
        {
            var used = ancestor.Used[amount.Dimension];
            var limit = ancestor.Limits[amount.Dimension];
            if (amount.Amount > limit - used)
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.BudgetExceeded, $"{amount.Dimension} budget is exhausted at {ancestor.Level}.");
        }
        if (_nextReservationId == 0)
            return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.CapacityExhausted, "Budget reservation identity space is exhausted.");
        foreach (var ancestor in chain)
        foreach (var amount in amounts)
            ancestor.Used[amount.Dimension] += amount.Amount;
        var handle = AllocateReservationHandle();
        var record = new ReservationRecord
        {
            Handle = handle,
            Account = account,
            Owner = owner,
            Amounts = amounts.OrderBy(static amount => amount.Dimension).ToArray(),
            Lifetime = lifetime,
            QosHint = qosHint,
            State = BudgetReservationState.Active,
        };
        _reservations.Add(handle.ReservationId, record);
        return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
    }

    private IEnumerable<AccountRecord> Ancestors(BudgetAccountHandle leaf)
    {
        var current = _accounts[leaf.AccountId];
        while (true)
        {
            yield return current;
            if (current.Parent is not { } parent) yield break;
            current = _accounts[parent.AccountId];
        }
    }

    private BudgetReservationHandle AllocateReservationHandle()
    {
        var id = _nextReservationId;
        _nextReservationId = id == ulong.MaxValue ? 0 : id + 1;
        return new(new(id), new(1));
    }

    private KernelResult<ReservationRecord> ResolveReservation(ProcessHandle owner, BudgetReservationHandle reservation)
    {
        if (!_reservations.TryGetValue(reservation.ReservationId, out var record) || record.Handle != reservation)
            return KernelResult<ReservationRecord>.Fail(KernelError.StaleGeneration, "Budget lease generation is stale.");
        if (record.Owner != owner)
            return KernelResult<ReservationRecord>.Fail(KernelError.WrongRegionOwner, "Budget lease belongs to another process generation.");
        return KernelResult<ReservationRecord>.Ok(record);
    }

    private KernelResult<BudgetReservationSnapshot> Transition(
        ProcessHandle owner, BudgetReservationHandle reservation,
        BudgetReservationState expected, BudgetReservationState next)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.State == next) return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
            if (record.State != expected)
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition, $"Lease transition requires {expected}.");
            record.State = next;
            return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
        }
    }

    private KernelResult ReleaseAmounts(ReservationRecord record, IReadOnlyList<BudgetAmount> amounts)
    {
        foreach (var account in Ancestors(record.Account))
        foreach (var amount in amounts)
            if (account.Used[amount.Dimension] < amount.Amount)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Budget ledger underflow was prevented.");
        foreach (var account in Ancestors(record.Account))
        foreach (var amount in amounts)
            account.Used[amount.Dimension] -= amount.Amount;
        return KernelResult.Ok();
    }

    private KernelResult<AccountRecord> ResolveAccount(BudgetAccountHandle handle)
    {
        if (!_accounts.TryGetValue(handle.AccountId, out var account) || account.Handle != handle || account.Retired)
            return KernelResult<AccountRecord>.Fail(KernelError.StaleGeneration, "Budget account generation is stale.");
        return KernelResult<AccountRecord>.Ok(account);
    }

    private static KernelResult ValidateAmounts(IReadOnlyList<BudgetAmount> amounts)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        if (amounts.Count == 0 || amounts.Any(static amount => !Enum.IsDefined(amount.Dimension) || amount.Amount == 0))
            return KernelResult.Fail(KernelError.InvalidMessage, "Budget amounts require unique defined dimensions and positive values.");
        if (amounts.Select(static amount => amount.Dimension).Distinct().Count() != amounts.Count)
            return KernelResult.Fail(KernelError.InvalidMessage, "Budget dimensions must be unique within one reservation or limit set.");
        return KernelResult.Ok();
    }

    private BudgetAccountSnapshot Snapshot(AccountRecord record)
    {
        var usage = AllDimensions.Select(dimension => new BudgetUsage(dimension, record.Limits[dimension], record.Used[dimension])).ToArray();
        var pinned = _reservations.Values.Any(reservation => IsCapacityHeld(reservation.State) &&
            reservation.Lifetime == BudgetReservationLifetime.ExternalEffect && Ancestors(reservation.Account).Any(account => account.Handle == record.Handle));
        var pressure = pinned
            ? BudgetPressureState.PinnedByExternalEffect
            : usage.Any(static item => item.Used == item.Limit && item.Limit != 0)
                ? BudgetPressureState.HardLimit
                : usage.Any(static item => item.Limit != ulong.MaxValue && item.Used >= item.Limit - item.Limit / 5)
                    ? BudgetPressureState.SoftLimit
                    : BudgetPressureState.Normal;
        return new(record.Handle, record.Level, record.Parent, record.OwnerCorrelation, usage, pressure);
    }

    private static BudgetReservationSnapshot Snapshot(ReservationRecord record) => new(
        record.Handle, record.Account, record.Owner, record.Amounts, record.Lifetime, record.QosHint, record.State,
        ChargedAmounts: record.ChargedAmounts);

    private static BudgetReservationSnapshot Stale(BudgetReservationHandle handle, ProcessHandle owner) => new(
        handle, default, owner, [], BudgetReservationLifetime.LocalResource, AdmissionQosHint.None, BudgetReservationState.Stale);

    private static bool IsCapacityHeld(BudgetReservationState state) => state is
        BudgetReservationState.Reserved or BudgetReservationState.Bound or BudgetReservationState.Consuming or
        BudgetReservationState.Settling or BudgetReservationState.Quarantined or BudgetReservationState.Reconciled;
}
