using SingPlus.Contracts;

namespace SingPlus.Runtime;

/// <summary>Single accounting ledger. Reservations admit capacity but never authorize effects.</summary>
public sealed partial class ResourceBudgetAuthority
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
        public AccountRecord? OriginalParent { get; init; }
        public List<AccountRecord> Children { get; } = [];
        // Original live attachment facts; _processAccounts only routes them.
        public HashSet<ProcessHandle> AttachedProcesses { get; } = [];
        // Same reservation owner records; routing must not replace quantitative truth.
        public List<ReservationRecord> Reservations { get; } = [];
    }

    private sealed class ReservationRecord
    {
        public required BudgetReservationHandle Handle { get; init; }
        public required BudgetAccountHandle Account { get; init; }
        public required AccountRecord[] OriginalAccounts { get; init; }
        public required ProcessHandle Owner { get; init; }
        public required BudgetAmount[] Amounts { get; set; }
        public required BudgetReservationLifetime Lifetime { get; init; }
        public required AdmissionQosHint QosHint { get; init; }
        // Release provenance belongs to this quantitative owner. Observation of
        // the reservation handle does not detach a live runtime resource charge.
        public bool RuntimeAttached { get; init; }
        public object? OriginalMappingOwner { get; set; }
        public ExternalOperationHandle? OriginalExternalOperation { get; set; }
        public object? OriginalExternalOperationOwner { get; set; }
        // Exact local ownership lifetime, with the same RegionAuthority record.
        public RegionHandle? OriginalRegion { get; set; }
        public DomainId OriginalRegionDomain { get; set; }
        public object? OriginalRegionOwner { get; set; }
        public SingPlus.Sip.PreparedOwnedTransfer? OriginalTransferPreparation { get; set; }
        public bool RegionLifetimeBound { get; set; }
        public object? OriginalVirtualIoOwner { get; set; }
        public object? OriginalLocalGuestOwner { get; init; }
        public ChannelId? OriginalIpcChannel { get; set; }
        public ulong OriginalIpcSequence { get; set; }
        public ProcessHandle OriginalIpcReceiver { get; set; }
        public object? OriginalQueuedLifetime { get; set; }
        public BudgetReservationState State { get; set; }
        public BudgetAmount[] ChargedAmounts { get; set; } = [];
        public bool SettlementTerminal { get; set; }
        // One-shot composition identity, not execution permission or a capacity ledger.
        public Guid? TemporalComposition { get; set; }
        public bool TemporalCompositionCommitted { get; set; }
    }

    private static readonly ServiceBudgetDimension[] AllDimensions = Enum.GetValues<ServiceBudgetDimension>();
    private readonly object _gate = new();
    private readonly Dictionary<BudgetAccountId, AccountRecord> _accounts = [];
    private readonly Dictionary<BudgetReservationId, ReservationRecord> _reservations = [];
    private readonly Dictionary<ProcessHandle, BudgetAccountHandle> _processAccounts = [];
    private readonly AccountRecord _systemAccount;
    private ulong _nextAccountId = 2;
    private ulong _nextReservationId = 1;
    private BudgetAmount[] _stagedRecoveryCharges = [];
    private bool _recoveryImportComplete = true;

    internal ResourceBudgetAuthority(ulong initialReservationId = 1)
    {
        if (initialReservationId == 0) throw new ArgumentOutOfRangeException(nameof(initialReservationId));
        _nextReservationId = initialReservationId;
        SystemBudget = new(new(1), new(1));
        _systemAccount = new AccountRecord
        {
            Handle = SystemBudget,
            Level = BudgetAccountLevel.System,
            Parent = null,
            OwnerCorrelation = "system",
            Limits = AllDimensions.ToDictionary(static dimension => dimension, static _ => ulong.MaxValue),
        };
        _accounts.Add(SystemBudget.AccountId, _systemAccount);
    }

    public BudgetAccountHandle SystemBudget { get; }

    internal bool RecoveryImportComplete
    {
        get { lock (_gate) return _recoveryImportComplete; }
    }

    internal KernelResult StageConservativeRecoveryCharge(IReadOnlyList<BudgetAmount> charges)
    {
        lock (_gate)
        {
            if (_accounts.Count != 1 || _reservations.Count != 0 || _processAccounts.Count != 0)
                return KernelResult.Fail(KernelError.InvalidTransition,
                    "Cold recovery charge must be staged before budget hierarchy construction.");
            if (charges.Count == 0)
            {
                _stagedRecoveryCharges = [];
                _recoveryImportComplete = true;
                return KernelResult.Ok();
            }
            var validation = ValidateAmounts(charges);
            if (!validation.IsSuccess) return validation;
            _stagedRecoveryCharges = charges.OrderBy(static amount => amount.Dimension).ToArray();
            _recoveryImportComplete = false;
            return KernelResult.Ok();
        }
    }

    internal bool HasProcessAccount(ProcessHandle process)
    {
        lock (_gate) return _processAccounts.ContainsKey(process) || HasOriginalProcessAttachment(process);
    }

    internal KernelResult<BudgetAccountSnapshot> ConfigureSystem(IReadOnlyList<BudgetAmount> limits)
    {
        lock (_gate)
        {
            var validation = ValidateAmounts(limits);
            if (!validation.IsSuccess) return KernelResult<BudgetAccountSnapshot>.Fail(validation.Error, validation.Message!);
            if (_accounts.Count != 1 || OriginalReservations().Any(static reservation => IsCapacityHeld(reservation.State)))
                return KernelResult<BudgetAccountSnapshot>.Fail(KernelError.InvalidTransition, "System budget can be configured only before child allocation or reservations.");
            var resolved = ResolveAccount(SystemBudget);
            if (!resolved.IsSuccess) return KernelResult<BudgetAccountSnapshot>.Fail(resolved.Error, resolved.Message!);
            var root = resolved.Value!;
            var configured = new Dictionary<ServiceBudgetDimension, ulong>(root.Limits);
            foreach (var amount in limits) configured[amount.Dimension] = amount.Amount;
            foreach (var charge in _stagedRecoveryCharges)
            {
                if (charge.Amount > configured[charge.Dimension])
                    return KernelResult<BudgetAccountSnapshot>.Fail(KernelError.BudgetExceeded,
                        $"Recovered conservative {charge.Dimension} charge exceeds the new system limit.");
            }
            foreach (var amount in limits) root.Limits[amount.Dimension] = amount.Amount;
            foreach (var charge in _stagedRecoveryCharges)
                root.Used[charge.Dimension] = charge.Amount;
            _stagedRecoveryCharges = [];
            _recoveryImportComplete = true;
            return KernelResult<BudgetAccountSnapshot>.Ok(Snapshot(root));
        }
    }

    private KernelResult<AccountRecord> PrepareChildRecord(BudgetAccountHandle parent,
        BudgetAccountLevel level, string correlation, IReadOnlyList<BudgetAmount> limits,
        BudgetAccountHandle handle, AccountRecord parentRecord)
    {
        if ((int)level != (int)parentRecord.Level + 1)
            return KernelResult<AccountRecord>.Fail(KernelError.InvalidTransition, "Budget hierarchy must be System -> Service -> ProcessDomain.");
        if (limits.Count > 0)
        {
            var validation = ValidateAmounts(limits);
            if (!validation.IsSuccess) return KernelResult<AccountRecord>.Fail(validation.Error, validation.Message!);
        }
        var selected = new Dictionary<ServiceBudgetDimension, ulong>(parentRecord.Limits);
        foreach (var limit in limits)
        {
            if (limit.Amount > parentRecord.Limits[limit.Dimension])
                return KernelResult<AccountRecord>.Fail(KernelError.BudgetExceeded, $"Child {limit.Dimension} limit exceeds its parent limit.");
            selected[limit.Dimension] = limit.Amount;
        }
        return KernelResult<AccountRecord>.Ok(new AccountRecord
        {
            Handle = handle, Level = level, Parent = parent, OwnerCorrelation = correlation, Limits = selected,
            OriginalParent = parentRecord,
        });
    }

    internal KernelResult<BudgetAccountSnapshot> CreateChild(BudgetAccountHandle parent,
        BudgetAccountLevel level, string ownerCorrelation, IReadOnlyList<BudgetAmount> limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerCorrelation);
        lock (_gate)
        {
            var parentResult = ResolveAccount(parent);
            if (!parentResult.IsSuccess) return KernelResult<BudgetAccountSnapshot>.Fail(parentResult.Error, parentResult.Message!);
            var handle = new BudgetAccountHandle(new(_nextAccountId), new(1));
            var prepared = PrepareChildRecord(parent, level, ownerCorrelation, limits, handle, parentResult.Value!);
            if (!prepared.IsSuccess) return KernelResult<BudgetAccountSnapshot>.Fail(prepared.Error, prepared.Message!);
            if (_nextAccountId == 0)
                return KernelResult<BudgetAccountSnapshot>.Fail(KernelError.CapacityExhausted, "Budget account identity space is exhausted.");
            var capacity = PrepareAccountOwnerCapacity(parentResult.Value!, 1);
            if (!capacity.IsSuccess) return KernelResult<BudgetAccountSnapshot>.Fail(capacity.Error, capacity.Message!);
            _accounts.Add(handle.AccountId, prepared.Value!);
            parentResult.Value!.Children.Add(prepared.Value!);
            _nextAccountId++;
            return KernelResult<BudgetAccountSnapshot>.Ok(Snapshot(prepared.Value!));
        }
    }

    internal KernelResult<ProcessBudgetAdmission> CreateProcessHierarchy(ProcessHandle process,
        string serviceCorrelation, string processCorrelation, IReadOnlyList<BudgetAmount> limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceCorrelation);
        ArgumentException.ThrowIfNullOrWhiteSpace(processCorrelation);
        lock (_gate)
        {
            if (_processAccounts.ContainsKey(process) || HasOriginalProcessAttachment(process))
                return KernelResult<ProcessBudgetAdmission>.Fail(KernelError.DuplicateIdentity, "Process generation already has a budget account.");
            var root = ResolveAccount(SystemBudget);
            if (!root.IsSuccess) return KernelResult<ProcessBudgetAdmission>.Fail(root.Error, root.Message!);
            var serviceHandle = new BudgetAccountHandle(new(_nextAccountId), new(1));
            var service = PrepareChildRecord(SystemBudget, BudgetAccountLevel.Service, serviceCorrelation,
                limits, serviceHandle, root.Value!);
            if (!service.IsSuccess) return KernelResult<ProcessBudgetAdmission>.Fail(service.Error, service.Message!);
            // Two identities are required. The final pair may consume MaxValue-1 and MaxValue,
            // after which zero remains the existing terminal exhausted sentinel.
            if (_nextAccountId == 0 || _nextAccountId == ulong.MaxValue)
                return KernelResult<ProcessBudgetAdmission>.Fail(KernelError.CapacityExhausted, "Two budget account identities are required.");
            var processHandle = new BudgetAccountHandle(new(_nextAccountId + 1), new(1));
            var child = PrepareChildRecord(serviceHandle, BudgetAccountLevel.ProcessDomain, processCorrelation,
                limits, processHandle, service.Value!);
            if (!child.IsSuccess) return KernelResult<ProcessBudgetAdmission>.Fail(child.Error, child.Message!);
            var capacity = PrepareAccountOwnerCapacity(root.Value!, 2);
            if (!capacity.IsSuccess) return KernelResult<ProcessBudgetAdmission>.Fail(capacity.Error, capacity.Message!);
            service.Value!.Children.EnsureCapacity(1);
            child.Value!.AttachedProcesses.EnsureCapacity(1);
            _processAccounts.EnsureCapacity(checked(_processAccounts.Count + 1));
            _accounts.Add(serviceHandle.AccountId, service.Value!);
            _accounts.Add(processHandle.AccountId, child.Value!);
            root.Value!.Children.Add(service.Value!);
            service.Value!.Children.Add(child.Value!);
            child.Value!.AttachedProcesses.Add(process);
            _processAccounts.Add(process, processHandle);
            _nextAccountId = unchecked(_nextAccountId + 2);
            return KernelResult<ProcessBudgetAdmission>.Ok(new(process, serviceHandle, processHandle));
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
                return existing == account && resolved.Value.AttachedProcesses.Contains(process)
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.DuplicateIdentity, "Process generation already has a different budget account.");
            if (HasOriginalProcessAttachment(process))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original process budget attachment lost its route.");
            if (resolved.Value.AttachedProcesses.Count == int.MaxValue || _processAccounts.Count == int.MaxValue)
                return KernelResult.Fail(KernelError.CapacityExhausted, "Process budget attachment capacity is exhausted.");
            resolved.Value.AttachedProcesses.EnsureCapacity(resolved.Value.AttachedProcesses.Count + 1);
            _processAccounts.EnsureCapacity(_processAccounts.Count + 1);
            _processAccounts.Add(process, account);
            resolved.Value.AttachedProcesses.Add(process);
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
            var attached = ResolveProcessAttachment(owner);
            if (!attached.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(attached.Error, attached.Message!);
            return ReserveCore(owner, attached.Value!.Handle, amounts, lifetime, qosHint);
        }
    }

    internal KernelResult<BudgetReservationSnapshot> ReserveCheckpointStorage(
        ProcessHandle owner,
        ulong bytes)
    {
        lock (_gate)
        {
            var attached = ResolveProcessAttachment(owner);
            if (!attached.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(attached.Error, attached.Message!);
            return ReserveCore(owner, attached.Value!.Handle,
                [new(ServiceBudgetDimension.CheckpointStorageBytes, bytes)],
                BudgetReservationLifetime.CheckpointImage,
                AdmissionQosHint.Background,
                runtimeAttached: true);
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
            if (!_processAccounts.ContainsKey(owner) && !HasOriginalProcessAttachment(owner))
                return KernelResult<BudgetReservationSnapshot?>.Ok(null);
            var attached = ResolveProcessAttachment(owner);
            if (!attached.IsSuccess) return KernelResult<BudgetReservationSnapshot?>.Fail(attached.Error, attached.Message!);
            var reserved = ReserveCore(owner, attached.Value!.Handle, amounts, lifetime, qosHint, runtimeAttached: true);
            return reserved.IsSuccess
                ? KernelResult<BudgetReservationSnapshot?>.Ok(reserved.Value)
                : KernelResult<BudgetReservationSnapshot?>.Fail(reserved.Error, reserved.Message!);
        }
    }

    // Only local final mapping publication uses this existing quantitative gate.
    // Provider/source/trace callbacks must complete before entering it.
    internal object MappingReservationPublicationGate => _gate;

    internal KernelResult BindOriginalExternalOperationCharge(ProcessHandle owner, BudgetReservationHandle reservation,
        ExternalOperationHandle operation, object operationOwner)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (!record.RuntimeAttached || record.Lifetime != BudgetReservationLifetime.ExternalEffect ||
                record.OriginalExternalOperation is { } prior &&
                (prior != operation || !ReferenceEquals(record.OriginalExternalOperationOwner, operationOwner)))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original operation charge cannot be replaced.");
            record.OriginalExternalOperation = operation;
            record.OriginalExternalOperationOwner = operationOwner;
            return KernelResult.Ok();
        }
    }

    internal (ExternalOperationHandle Operation, object OperationOwner, ProcessHandle Owner, BudgetReservationHandle Reservation)[]
        OriginalExternalOperationCharges(ProcessHandle owner)
    {
        lock (_gate)
            return OriginalReservations().Where(record => record.Owner == owner && record.RuntimeAttached &&
                    record.OriginalExternalOperation is not null && record.OriginalExternalOperationOwner is not null)
                .Select(record => (record.OriginalExternalOperation!.Value, record.OriginalExternalOperationOwner!, record.Owner, record.Handle))
                .ToArray();
    }

    internal KernelResult BindOriginalRegionCharge(ProcessHandle owner, BudgetReservationHandle reservation,
        RegionHandle region, DomainId domain, object regionOwner)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (!record.RuntimeAttached || record.Lifetime != BudgetReservationLifetime.LocalResource || record.OriginalVirtualIoOwner is not null ||
                record.OriginalRegion is { } prior && (prior != region ||
                    !ReferenceEquals(record.OriginalRegionOwner, regionOwner)))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original Region charge lifetime cannot be replaced.");
            record.OriginalRegion = region;
            record.OriginalRegionDomain = domain;
            record.OriginalRegionOwner = regionOwner;
            record.RegionLifetimeBound = true;
            return KernelResult.Ok();
        }
    }

    internal (RegionHandle? Region, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation)[]
        OriginalRegionChargesForCleanup(ProcessHandle process, DomainId domain)
    {
        lock (_gate)
            return OriginalReservations().Where(record => IsCapacityHeld(record.State) && record.RuntimeAttached &&
                    record.Lifetime == BudgetReservationLifetime.LocalResource &&
                    record.Amounts.Any(amount => amount.Dimension == ServiceBudgetDimension.OwnedMemoryBytes) &&
                    (record.RegionLifetimeBound || record.OriginalRegion is not null || record.OriginalRegionOwner is not null) &&
                    (record.Owner == process || record.OriginalRegionDomain == domain))
                .Select(record => (record.OriginalRegion, record.OriginalRegionOwner, record.Owner, record.Handle))
                .ToArray();
    }

    internal KernelResult ValidateOriginalPrivateDataQuota(ProcessHandle owner, object? original, object mappingOwner, bool expected, ulong quota)
    {
        lock (_gate)
        {
            if (!expected && original is null) return ValidateMappingReservationPublicationLocked(owner, null);
            if (quota == 0 || original is not ReservationRecord record || record.Owner != owner ||
                !ReferenceEquals(record.OriginalMappingOwner, mappingOwner) ||
                record.Amounts.Where(amount => amount.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Sum(amount => checked((decimal)amount.Amount)) != quota)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Private executable data quota lacks its exact original quantitative admission.");
            var resolved = ResolveReservation(owner, record.Handle);
            if (!resolved.IsSuccess || !ReferenceEquals(resolved.Value, record))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Private executable data charge owner/history changed.");
            return ValidateMappingReservationPublicationLocked(owner, record.Handle);
        }
    }

    internal object? CaptureOriginalMappingReservationOwner(ProcessHandle owner, BudgetReservationHandle? reservation)
    {
        lock (_gate)
            return reservation is { } exact
                ? OriginalReservations().SingleOrDefault(record => record.Owner == owner && record.Handle == exact &&
                    record.RuntimeAttached && record.Lifetime == BudgetReservationLifetime.PlatformMapping)
                : null;
    }

    internal object? CaptureOriginalTransferReservationOwner(ProcessHandle owner,BudgetReservationHandle? reservation,
        SingPlus.Sip.PreparedOwnedTransfer prepared)
    {
        lock(_gate)
        {
            if(reservation is not { } exact) return null;
            var resolved=ResolveReservation(owner,exact);
            if(!resolved.IsSuccess || !resolved.Value!.RuntimeAttached || resolved.Value.Lifetime!=BudgetReservationLifetime.LocalResource ||
                resolved.Value.OriginalTransferPreparation is { } prior && !ReferenceEquals(prior,prepared)) return null;
            resolved.Value.OriginalTransferPreparation=prepared;
            return resolved.Value;
        }
    }

    // Called only after RegionAuthority independently closes the exact producer raw.
    internal KernelResult ConsumeClosedTransferReservation(object original, ProcessHandle owner,
        BudgetReservationHandle reservation, SingPlus.Sip.PreparedOwnedTransfer prepared,
        RegionHandle region, object regionOwner, DomainId domain)
    {
        lock(_gate)
        {
            var valid=ValidateOriginalRegionCharge(original,owner,reservation);
            if(!valid.IsSuccess) return valid;
            var record=(ReservationRecord)original;
            if(!record.RuntimeAttached || record.Lifetime!=BudgetReservationLifetime.LocalResource ||
                !ReferenceEquals(record.OriginalTransferPreparation,prepared) || record.TemporalComposition is not null ||
                record.RegionLifetimeBound!=(record.OriginalRegion is not null) ||
                (record.OriginalRegion is not null)!=(record.OriginalRegionOwner is not null) ||
                record.OriginalRegion is { } bound && (bound!=region || record.OriginalRegionDomain!=domain || !ReferenceEquals(record.OriginalRegionOwner,regionOwner)) ||
                record.OriginalVirtualIoOwner is not null || record.OriginalLocalGuestOwner is not null)
                return KernelResult.Fail(KernelError.InvalidTransition,"Closed transfer quantitative identity differs.");
            if(record.State==BudgetReservationState.Released) return KernelResult.Ok();
            if(record.State is not (BudgetReservationState.Active or BudgetReservationState.Quarantined))
                return KernelResult.Fail(KernelError.InvalidTransition,"Transfer reservation has another consumer.");
            var released=ReleaseAmounts(record,record.Amounts);
            if(!released.IsSuccess) return released;
            record.State=BudgetReservationState.Released;
            return KernelResult.Ok();
        }
    }

    internal (object Fact, object? RegionOwner, ProcessHandle Owner, BudgetReservationHandle Reservation)[]
        OriginalRegionCharges(RegionHandle region)
    {
        lock (_gate)
            return OriginalReservations().Where(record => record.RuntimeAttached &&
                    record.Lifetime == BudgetReservationLifetime.LocalResource && record.OriginalRegion == region)
                .Select(record => ((object)record, record.OriginalRegionOwner, record.Owner, record.Handle)).ToArray();
    }

    internal KernelResult ValidateOriginalRegionCharge(object original, ProcessHandle owner, BudgetReservationHandle reservation)
    {
        lock (_gate)
        {
            if (original is not ReservationRecord record || record.Owner != owner || record.Handle != reservation ||
                !OwnsOriginalReservation(record))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original Region quantitative fact was replaced.");
            if (record.State == BudgetReservationState.Released) return KernelResult.Ok();
            var resolved = ResolveReservation(owner, reservation);
            return resolved.IsSuccess && ReferenceEquals(resolved.Value, record)
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.PlatformFaulted, "Original Region quantitative route was lost.");
        }
    }

    internal KernelResult<BudgetReservationSnapshot> ReleaseOriginalRegionCharge(object original,
        ProcessHandle owner, BudgetReservationHandle reservation)
    {
        lock (_gate)
        {
            var continuity = ValidateOriginalRegionCharge(original, owner, reservation);
            if (!continuity.IsSuccess)
                return KernelResult<BudgetReservationSnapshot>.Fail(continuity.Error, continuity.Message!);
            if (((ReservationRecord)original).State == BudgetReservationState.Released)
                return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot((ReservationRecord)original));
            return Release(owner, reservation);
        }
    }

    internal KernelResult ValidateOriginalRegionChargeBinding(object original, ProcessHandle owner,
        BudgetReservationHandle reservation, RegionHandle region, object regionOwner, DomainId domain)
    {
        lock(_gate)
        {
            var valid=ValidateOriginalRegionCharge(original,owner,reservation);
            if(!valid.IsSuccess) return valid;
            var record=(ReservationRecord)original;
            return record.RuntimeAttached && record.Lifetime==BudgetReservationLifetime.LocalResource && record.RegionLifetimeBound && record.OriginalRegionDomain==domain &&
                record.OriginalRegion==region && ReferenceEquals(record.OriginalRegionOwner,regionOwner)
                ? KernelResult.Ok() : KernelResult.Fail(KernelError.PlatformFaulted,"Original Region charge association changed.");
        }
    }

    internal KernelResult<object> BindOriginalQueuedCharge(ProcessHandle sender, ProcessHandle receiver,
        BudgetReservationHandle reservation, ChannelId channel, ulong sequence, object lifetime)
    {
        lock (_gate)
        {
            var result = ResolveReservation(sender, reservation);
            if (!result.IsSuccess) return KernelResult<object>.Fail(result.Error, result.Message!);
            var record = result.Value!;
            if (!record.RuntimeAttached || record.Lifetime != BudgetReservationLifetime.IpcQueued ||
                record.OriginalQueuedLifetime is { } prior && !ReferenceEquals(prior, lifetime))
                return KernelResult<object>.Fail(KernelError.PlatformFaulted, "Original queued charge cannot be replaced.");
            record.OriginalIpcChannel = channel;
            record.OriginalIpcSequence = sequence;
            record.OriginalIpcReceiver = receiver;
            record.OriginalQueuedLifetime = lifetime;
            return KernelResult<object>.Ok(record);
        }
    }

    internal KernelResult<BudgetReservationSnapshot> ConsumeWithdrawnQueuedCharge(object original,object lifetime,ProcessHandle sender,BudgetReservationHandle reservation)
    {
        lock(_gate)
        {
            var valid=ValidateOriginalRegionCharge(original,sender,reservation);
            if(!valid.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(valid.Error,valid.Message!);
            var record=(ReservationRecord)original;
            if(record.Lifetime!=BudgetReservationLifetime.IpcQueued || !ReferenceEquals(record.OriginalQueuedLifetime,lifetime) || record.TemporalComposition is not null)
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition,"Original withdrawn queue charge differs.");
            if(record.State==BudgetReservationState.Released) return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
            if(record.State is not (BudgetReservationState.Active or BudgetReservationState.Quarantined))
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition,"Queue charge has another consumer.");
            var released=ReleaseAmounts(record,record.Amounts);
            if(!released.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(released.Error,released.Message!);
            record.State=BudgetReservationState.Released;
            return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
        }
    }

    internal (object Fact, object? Lifetime, ChannelId Channel, ulong Sequence, ProcessHandle Sender,
        ProcessHandle Receiver, BudgetReservationHandle Reservation)[] OriginalQueuedCharges()
    {
        lock (_gate)
            return OriginalReservations().Where(record => record.RuntimeAttached &&
                    record.Lifetime == BudgetReservationLifetime.IpcQueued && record.OriginalIpcChannel is not null)
                .Select(record => ((object)record, record.OriginalQueuedLifetime, record.OriginalIpcChannel!.Value,
                    record.OriginalIpcSequence, record.Owner, record.OriginalIpcReceiver, record.Handle)).ToArray();
    }

    internal KernelResult ValidateOriginalMappingChargeLocked(
        object? original, object mappingOwner, ProcessHandle owner, BudgetReservationHandle reservation)
    {
        if (!Monitor.IsEntered(_gate))
            throw new InvalidOperationException("Original charge consumption requires the quantitative owner gate.");
        if (original is ReservationRecord owned && owned.Owner != owner)
            return KernelResult.Fail(KernelError.WrongRegionOwner, "Budget lease belongs to another process generation.");
        if (original is not ReservationRecord record || record.Handle != reservation ||
            !record.RuntimeAttached || record.Lifetime != BudgetReservationLifetime.PlatformMapping ||
            !ReferenceEquals(record.OriginalMappingOwner, mappingOwner))
            return KernelResult.Fail(KernelError.PlatformFaulted, "Mapping charge route replaced its original quantitative fact.");
        if (IsOriginalMappingChargeConsumedLocked(original, mappingOwner)) return KernelResult.Ok();
        var resolved = ResolveReservation(owner, reservation);
        return resolved.IsSuccess && ReferenceEquals(resolved.Value, record)
            ? KernelResult.Ok()
            : KernelResult.Fail(KernelError.PlatformFaulted, "Original mapping charge owner continuity was lost.");
    }

    internal KernelResult BindOriginalMappingChargeOwnerLocked(object? original, object mappingOwner)
    {
        if (!Monitor.IsEntered(_gate)) throw new InvalidOperationException("Original charge binding requires its owner gate.");
        if (original is not ReservationRecord record || !OwnsOriginalReservation(record) ||
            !record.RuntimeAttached || record.Lifetime != BudgetReservationLifetime.PlatformMapping ||
            record.OriginalMappingOwner is { } bound && !ReferenceEquals(bound, mappingOwner))
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original mapping charge binding cannot be replaced.");
        record.OriginalMappingOwner = mappingOwner;
        return KernelResult.Ok();
    }

    internal bool IsOriginalMappingChargeConsumedLocked(object? original, object mappingOwner)
    {
        if (!Monitor.IsEntered(_gate)) throw new InvalidOperationException("Consumed charge observation requires its owner gate.");
        return original is ReservationRecord record && record.State == BudgetReservationState.Released &&
            record.RuntimeAttached && record.Lifetime == BudgetReservationLifetime.PlatformMapping &&
            ReferenceEquals(record.OriginalMappingOwner, mappingOwner) && OwnsOriginalReservation(record);
    }

    private bool OwnsOriginalReservation(ReservationRecord record)
    {
        var chain = record.OriginalAccounts;
        if (chain.Length is < 1 or > 3 || chain[0].Handle != record.Account ||
            !chain[0].Reservations.Any(owned => ReferenceEquals(owned, record))) return false;
        for (var index = 0; index + 1 < chain.Length; index++)
            if (!ReferenceEquals(chain[index].OriginalParent, chain[index + 1]) || chain[index].Parent != chain[index + 1].Handle ||
                !chain[index + 1].Children.Any(owned => ReferenceEquals(owned, chain[index]))) return false;
        return ReferenceEquals(chain[^1], _systemAccount) && chain[^1].OriginalParent is null && chain[^1].Parent is null;
    }

    internal KernelResult ValidateMappingReservationPublicationLocked(
        ProcessHandle owner, BudgetReservationHandle? reservation)
    {
        if (!Monitor.IsEntered(_gate))
            throw new InvalidOperationException("Mapping publication requires the quantitative owner gate.");
        if (reservation is not { } exact)
            return !_processAccounts.ContainsKey(owner) && !HasOriginalProcessAttachment(owner)
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.PlatformFaulted, "Mapping publication lost its configured quantitative admission.");
        var resolved = ResolveReservation(owner, exact);
        if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
        var record = resolved.Value!;
        if (!record.RuntimeAttached || record.Lifetime != BudgetReservationLifetime.PlatformMapping ||
            record.State != BudgetReservationState.Active || record.OriginalAccounts.Any(account => account.Retired) ||
            !record.OriginalAccounts[0].AttachedProcesses.Contains(owner) ||
            !_processAccounts.TryGetValue(owner, out var attached) || attached != record.Account)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original live mapping charge is unavailable for publication.");
        return KernelResult.Ok();
    }

    internal KernelResult<BudgetReservationSnapshot> Release(
        ProcessHandle owner,
        BudgetReservationHandle reservation) => ReleaseCore(owner, reservation, runtimeConsumer: true);

    internal KernelResult<BudgetReservationSnapshot> ReleaseExplicit(
        ProcessHandle owner,
        BudgetReservationHandle reservation) => ReleaseCore(owner, reservation, runtimeConsumer: false);

    internal KernelResult<object> BindOriginalVirtualIoCharge(ProcessHandle owner, BudgetReservationHandle reservation, object ioOwner)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return KernelResult<object>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (!record.RuntimeAttached || record.Lifetime != BudgetReservationLifetime.LocalResource || record.OriginalRegionOwner is not null || record.OriginalQueuedLifetime is not null ||
                record.OriginalVirtualIoOwner is not null && !ReferenceEquals(record.OriginalVirtualIoOwner, ioOwner))
                return KernelResult<object>.Fail(KernelError.PlatformFaulted, "Original I/O charge cannot change resource owner.");
            record.OriginalVirtualIoOwner = ioOwner;
            return KernelResult<object>.Ok(record);
        }
    }
    internal KernelResult ConsumeOriginalVirtualIoCharge(ProcessHandle owner, BudgetReservationHandle reservation, object ioOwner, object? chargeOwner)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess || !ReferenceEquals(resolved.Value, chargeOwner) || !ReferenceEquals(resolved.Value!.OriginalVirtualIoOwner, ioOwner))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original I/O quantitative owner/history is absent or changed.");
            var released = ReleaseCore(owner, reservation, runtimeConsumer: true, virtualIoOwner: ioOwner);
            return released.IsSuccess && released.Value!.State == BudgetReservationState.Released ? KernelResult.Ok() :
                KernelResult.Fail(released.IsSuccess ? KernelError.PlatformFaulted : released.Error, released.Message ?? "Original I/O charge remains pinned.");
        }
    }

    private KernelResult<BudgetReservationSnapshot> ReleaseCore(
        ProcessHandle owner,
        BudgetReservationHandle reservation,
        bool runtimeConsumer, object? virtualIoOwner = null, object? localGuestOwner = null)
    {
        lock (_gate)
        {
            if (!_reservations.TryGetValue(reservation.ReservationId, out var record) || record.Handle != reservation)
                return KernelResult<BudgetReservationSnapshot>.Ok(Stale(reservation, owner));
            if (record.Owner != owner)
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.WrongRegionOwner, "Budget reservation belongs to another process generation.");
            var continuity = ValidateReservationContinuity(record);
            if (!continuity.IsSuccess)
                return KernelResult<BudgetReservationSnapshot>.Fail(continuity.Error, continuity.Message!);
            if (record.State == BudgetReservationState.Released)
                return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
            if (record.State == BudgetReservationState.CancelledPreSubmit)
                return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
            if (record.TemporalComposition is not null)
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition,
                    "A claimed temporal composition requires its existing cancellation or settlement consumer.");
            if (record.RuntimeAttached && !runtimeConsumer)
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition,
                    "Runtime-attached capacity requires its existing resource release consumer.");
            if (record.OriginalVirtualIoOwner is not null && !ReferenceEquals(record.OriginalVirtualIoOwner, virtualIoOwner))
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition, "Original I/O backing requires its exact closure consumer.");
            if (record.OriginalLocalGuestOwner is not null && !ReferenceEquals(record.OriginalLocalGuestOwner, localGuestOwner))
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition, "Original local guest charge requires its exact VDA closure consumer.");
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

    internal KernelResult ClaimTemporalComposition(ProcessHandle owner,
        BudgetReservationHandle reservation, Guid composition)
    {
        if (composition == Guid.Empty)
            return KernelResult.Fail(KernelError.InvalidMessage, "Temporal composition identity is empty.");
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.TemporalComposition is not null)
                return KernelResult.Fail(KernelError.DuplicateIdentity, "Budget already belongs to a temporal composition.");
            if (record.State != BudgetReservationState.Bound || record.Lifetime != BudgetReservationLifetime.ExternalEffect)
                return KernelResult.Fail(KernelError.InvalidTransition, "Temporal composition requires an idle bound external lease.");
            record.TemporalComposition = composition;
            return KernelResult.Ok();
        }
    }

    // Only an unpublished composition whose model reserve failed can abandon its claim.
    internal void AbandonTemporalComposition(ProcessHandle owner,
        BudgetReservationHandle reservation, Guid composition)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (resolved.IsSuccess && !resolved.Value!.TemporalCompositionCommitted &&
                resolved.Value.TemporalComposition == composition)
                resolved.Value.TemporalComposition = null;
        }
    }

    internal KernelResult CommitTemporalComposition(ProcessHandle owner,
        BudgetReservationHandle reservation, Guid composition)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (composition == Guid.Empty || record.TemporalComposition != composition ||
                record.State != BudgetReservationState.Bound)
                return KernelResult.Fail(KernelError.InvalidTransition,
                    "Only the exact idle provisional temporal composition can commit.");
            record.TemporalCompositionCommitted = true;
            return KernelResult.Ok();
        }
    }

    internal KernelResult CommitReservedDonation(ProcessHandle owner, BudgetReservationHandle reservation,
        Func<KernelResult> publish)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            if (resolved.Value!.State != BudgetReservationState.Reserved)
                return KernelResult.Fail(KernelError.InvalidTransition, "Donation reservation is no longer Reserved.");
            return publish();
        }
    }

    // Trusted invocation activation only; refusal leaves the exact lease untouched.
    internal KernelResult CommitDonationConsumption(ProcessHandle owner, BudgetReservationHandle reservation,
        Func<KernelResult> activate)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.State != BudgetReservationState.Reserved || !MatchesTemporalConsumer(record, null))
                return KernelResult.Fail(KernelError.InvalidTransition, "Donation lease is not an idle uncomposed reservation.");
            var active = activate();
            if (!active.IsSuccess) return active;
            record.State = BudgetReservationState.Consuming;
            return KernelResult.Ok();
        }
    }

    internal KernelResult<BudgetReservationSnapshot> BeginConsumption(ProcessHandle owner,
        BudgetReservationHandle reservation, Guid? temporalComposition = null)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess)
                return KernelResult<BudgetReservationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            // Association selects the quantitative consumer, never execution authority.
            if (!MatchesTemporalConsumer(record, temporalComposition))
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition,
                    "Consumption must match the lease's committed temporal composition, if any.");
            return Transition(owner, reservation, BudgetReservationState.Bound, BudgetReservationState.Consuming);
        }
    }

    internal KernelResult<BudgetReservationSnapshot> QuarantineLease(ProcessHandle owner, BudgetReservationHandle reservation)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (record.State == BudgetReservationState.Quarantined) return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
            if (record.State is not (BudgetReservationState.Reserved or BudgetReservationState.Bound or BudgetReservationState.Consuming))
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition, "Only a reserved, bound or consuming lease can become quarantined.");
            record.State = BudgetReservationState.Quarantined;
            return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
        }
    }

    internal KernelResult<BudgetReservationSnapshot> ReconcileLease(ProcessHandle owner,
        BudgetReservationHandle reservation, Guid? temporalComposition = null)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (!MatchesTemporalConsumer(record, temporalComposition))
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition,
                    "Reconciliation must match the committed temporal consumer, if any.");
            if (record.State == BudgetReservationState.Reconciled) return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
            if (record.State != BudgetReservationState.Quarantined)
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition, "Only a quarantined lease can be reconciled.");
            record.State = BudgetReservationState.Reconciled;
            return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
        }
    }

    internal KernelResult<BudgetReservationSnapshot> CancelLeasePreSubmit(ProcessHandle owner,
        BudgetReservationHandle reservation, Guid? temporalComposition = null)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (!MatchesTemporalConsumer(record, temporalComposition))
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition,
                    "Pre-submit cancellation must match the committed temporal consumer, if any.");
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
        ProcessHandle owner, BudgetReservationHandle reservation, IReadOnlyList<BudgetAmount> actualUsage,
        Guid? temporalComposition = null)
    {
        // Caller enumeration can reenter or change between reads. Capture outside
        // the owner lock, then validate and commit one quantitative fact.
        actualUsage = actualUsage.ToArray();
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (!MatchesTemporalConsumer(record, temporalComposition))
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition,
                    "Settlement must match the committed temporal consumer, if any.");
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
            var released = ReleaseAmounts(record, unused);
            if (!released.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(released.Error, released.Message!);
            record.State = BudgetReservationState.Settling;
            record.ChargedAmounts = charged;
            record.SettlementTerminal = true;
            record.State = BudgetReservationState.Released;
            return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
        }
    }

    internal bool IsExactSettledExternalLease(
        ProcessHandle owner, BudgetReservationHandle reservation, ulong chargedAmount)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return false;
            var record = resolved.Value!;
            return record.Lifetime == BudgetReservationLifetime.ExternalEffect &&
                   record.State == BudgetReservationState.Released && record.SettlementTerminal &&
                   (chargedAmount == 0 ? record.ChargedAmounts.Length == 0 :
                    record.ChargedAmounts.Length == 1 &&
                    record.ChargedAmounts[0] == new BudgetAmount(
                        ServiceBudgetDimension.ComputeTimeNanoseconds, chargedAmount));
        }
    }

    // Corrective accounting for a reported ComputeTime overrun. The budget owner alone
    // decides whether every ancestor can retain the extra charge; this is not a
    // reservation or evidence that the provider enforced an upper bound.
    internal KernelResult<BudgetReservationSnapshot> SettleReportedComputeOverrun(
        ProcessHandle owner, BudgetReservationHandle reservation, ulong reportedNanoseconds,
        Guid? temporalComposition = null)
    {
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess)
                return KernelResult<BudgetReservationSnapshot>.Fail(resolved.Error, resolved.Message!);
            var record = resolved.Value!;
            if (!MatchesTemporalConsumer(record, temporalComposition))
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition,
                    "Corrective settlement must match the committed temporal consumer, if any.");
            if (record.State != BudgetReservationState.Quarantined || record.SettlementTerminal ||
                record.Lifetime != BudgetReservationLifetime.ExternalEffect || record.Amounts.Length != 1 ||
                record.Amounts[0].Dimension != ServiceBudgetDimension.ComputeTimeNanoseconds ||
                reportedNanoseconds <= record.Amounts[0].Amount)
                return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.InvalidTransition,
                    "Corrective ComputeTime settlement requires an exact quarantined overrun lease.");
            var extra = reportedNanoseconds - record.Amounts[0].Amount;
            var chain = Ancestors(record.Account).ToArray();
            foreach (var account in chain)
                if (extra > account.Limits[ServiceBudgetDimension.ComputeTimeNanoseconds] -
                            account.Used[ServiceBudgetDimension.ComputeTimeNanoseconds])
                    return KernelResult<BudgetReservationSnapshot>.Fail(KernelError.BudgetExceeded,
                        $"Reported ComputeTime overrun exceeds the {account.Level} budget.");
            foreach (var account in chain)
                account.Used[ServiceBudgetDimension.ComputeTimeNanoseconds] += extra;
            record.ChargedAmounts = [new(ServiceBudgetDimension.ComputeTimeNanoseconds, reportedNanoseconds)];
            record.SettlementTerminal = true;
            record.State = BudgetReservationState.Released;
            return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
        }
    }

    internal KernelResult<BudgetReservationSnapshot> SplitLease(
        ProcessHandle owner, BudgetReservationHandle reservation, IReadOnlyList<BudgetAmount> childAmounts)
    {
        childAmounts = childAmounts.ToArray(); // No caller callbacks after parent debit.
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
            var capacity = PrepareReservationOwnerCapacity(parent.OriginalAccounts[0]);
            if (!capacity.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(capacity.Error, capacity.Message!);
            var remainingAmounts = parent.Amounts.Select(amount => new BudgetAmount(amount.Dimension,
                amount.Amount - requested.GetValueOrDefault(amount.Dimension))).ToArray();
            var handle = AllocateReservationHandle();
            var child = new ReservationRecord
            {
                Handle = handle, Account = parent.Account, OriginalAccounts = parent.OriginalAccounts, Owner = owner,
                Amounts = childAmounts.OrderBy(static amount => amount.Dimension).ToArray(),
                Lifetime = parent.Lifetime, QosHint = parent.QosHint, State = BudgetReservationState.Reserved,
            };
            parent.Amounts = remainingAmounts;
            _reservations.Add(handle.ReservationId, child);
            parent.OriginalAccounts[0].Reservations.Add(child);
            return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(child));
        }
    }

    // Trusted local invocation publication only. Failed publication returns the
    // split to its exact parent atomically, without releasing or re-reserving capacity.
    internal KernelResult<ResourceDonationBinding> SplitDonationAndPublish(ProcessHandle owner,
        BudgetReservationHandle reservation, IReadOnlyList<BudgetAmount> childAmounts,
        Func<BudgetReservationHandle, KernelResult<ResourceDonationBinding>> publish)
    {
        childAmounts = childAmounts.ToArray();
        lock (_gate)
        {
            var resolved = ResolveReservation(owner, reservation);
            if (!resolved.IsSuccess) return KernelResult<ResourceDonationBinding>.Fail(resolved.Error, resolved.Message!);
            var parent = resolved.Value!;
            var original = parent.Amounts;
            var split = SplitLease(owner, reservation, childAmounts);
            if (!split.IsSuccess) return KernelResult<ResourceDonationBinding>.Fail(split.Error, split.Message!);
            var committed = false;
            try
            {
                var result = publish(split.Value!.Reservation);
                committed = result.IsSuccess;
                return result;
            }
            finally
            {
                if (!committed)
                {
                    parent.Amounts = original;
                    _reservations[split.Value!.Reservation.ReservationId].State = BudgetReservationState.CancelledPreSubmit;
                }
            }
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
            return _reservations.TryGetValue(reservation.ReservationId, out var record) && record.Handle == reservation && ValidateReservationContinuity(record).IsSuccess
                ? KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record))
                : KernelResult<BudgetReservationSnapshot>.Fail(KernelError.BudgetReservationNotFound, "Budget reservation was not found or is stale.");
    }

    internal BudgetReservationSnapshot[] InspectionSnapshot()
    {
        lock (_gate)
            return OriginalReservations()
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
            var attached = ResolveProcessAttachment(process);
            if (!attached.IsSuccess || attached.Value!.Handle != processAccount)
                return KernelResult.Fail(KernelError.StaleGeneration, "Process budget attachment is stale.");
            if (OriginalReservations().Any(record => IsCapacityHeld(record.State) &&
                                                   record.Account == processAccount &&
                                                   record.Lifetime != BudgetReservationLifetime.CheckpointImage))
                return KernelResult.Fail(KernelError.ReplacementBlocked, "Active live-resource reservations keep the process budget charged.");
            var processResolved = ResolveAccount(processAccount);
            var serviceResolved = ResolveAccount(serviceAccount);
            if (!processResolved.IsSuccess || !serviceResolved.IsSuccess)
                return KernelResult.Fail(KernelError.StaleGeneration, "Budget hierarchy is stale.");
            if (processResolved.Value!.Level != BudgetAccountLevel.ProcessDomain ||
                serviceResolved.Value!.Level != BudgetAccountLevel.Service ||
                processResolved.Value.Parent != serviceAccount || serviceResolved.Value.Parent != SystemBudget)
                return KernelResult.Fail(KernelError.StaleGeneration, "Budget retirement requires the exact process/service parent hierarchy.");
            if (processResolved.Value.AttachedProcesses.Any(owner => owner != process) ||
                serviceResolved.Value.Children.Any(item => !item.Retired && item.Handle != processAccount))
                return KernelResult.Fail(KernelError.ReplacementBlocked, "Other live process attachments or children keep the service hierarchy live.");
            _processAccounts.Remove(process);
            processResolved.Value.AttachedProcesses.Remove(process);
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
        AdmissionQosHint qosHint,
        bool runtimeAttached = false, VirtualDomainAuthority.LocalGuestLifetime? localGuestLifetime = null)
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
        var capacity = PrepareReservationOwnerCapacity(chain[0]);
        if (!capacity.IsSuccess) return KernelResult<BudgetReservationSnapshot>.Fail(capacity.Error, capacity.Message!);
        var handle = AllocateReservationHandle();
        var record = new ReservationRecord
        {
            Handle = handle,
            Account = account,
            OriginalAccounts = chain,
            Owner = owner,
            Amounts = amounts.OrderBy(static amount => amount.Dimension).ToArray(),
            Lifetime = lifetime,
            QosHint = qosHint,
            RuntimeAttached = runtimeAttached,
            OriginalLocalGuestOwner = localGuestLifetime,
            State = BudgetReservationState.Active,
        };
        if (localGuestLifetime is not null)
        {
            localGuestLifetime.Reservation = handle;
            localGuestLifetime.ChargeOwner = record;
        }
        foreach (var ancestor in chain)
        foreach (var amount in amounts)
            ancestor.Used[amount.Dimension] += amount.Amount;
        _reservations.Add(handle.ReservationId, record);
        chain[0].Reservations.Add(record);
        return KernelResult<BudgetReservationSnapshot>.Ok(Snapshot(record));
    }

    private KernelResult PrepareReservationOwnerCapacity(AccountRecord account)
    {
        if (account.Reservations.Count == int.MaxValue || _reservations.Count == int.MaxValue)
            return KernelResult.Fail(KernelError.CapacityExhausted, "Reservation owner capacity is exhausted.");
        account.Reservations.EnsureCapacity(account.Reservations.Count + 1);
        _reservations.EnsureCapacity(_reservations.Count + 1);
        return KernelResult.Ok();
    }

    private KernelResult ValidateReservationContinuity(ReservationRecord record)
    {
        var chain = record.OriginalAccounts;
        if (chain.Length == 0 || chain[0].Handle != record.Account ||
            !chain[0].Reservations.Any(original => ReferenceEquals(original, record)))
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original quantitative reservation owner was lost.");
        var continuity = ValidateAccountContinuity(chain[0]);
        if (!continuity.IsSuccess) return continuity;
        for (var index = 0; index < chain.Length; index++)
            if (!_accounts.TryGetValue(chain[index].Handle.AccountId, out var current) || !ReferenceEquals(current, chain[index]) ||
                chain[index].Parent != (index + 1 < chain.Length ? chain[index + 1].Handle : (BudgetAccountHandle?)null))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original quantitative account ancestry was lost.");
        return KernelResult.Ok();
    }

    private IEnumerable<ReservationRecord> OriginalReservations() =>
        OriginalAccounts().SelectMany(account => account.Reservations);

    private IEnumerable<AccountRecord> OriginalAccounts()
    {
        yield return _systemAccount;
        foreach (var service in _systemAccount.Children)
        {
            yield return service;
            foreach (var process in service.Children) yield return process;
        }
    }

    private bool HasOriginalProcessAttachment(ProcessHandle process) =>
        OriginalAccounts().Any(account => account.AttachedProcesses.Contains(process));

    private KernelResult<AccountRecord> ResolveProcessAttachment(ProcessHandle process)
    {
        if (!_processAccounts.TryGetValue(process, out var handle))
            return KernelResult<AccountRecord>.Fail(HasOriginalProcessAttachment(process) ? KernelError.PlatformFaulted : KernelError.BudgetNotConfigured,
                "Process budget attachment is absent or lost its original route.");
        var resolved = ResolveAccount(handle);
        if (!resolved.IsSuccess) return resolved;
        return resolved.Value!.AttachedProcesses.Contains(process)
            ? resolved
            : KernelResult<AccountRecord>.Fail(KernelError.PlatformFaulted, "Process budget route does not refer to its original attachment owner.");
    }

    private KernelResult PrepareAccountOwnerCapacity(AccountRecord parent, int routeCount)
    {
        if (parent.Children.Count == int.MaxValue || _accounts.Count > int.MaxValue - routeCount)
            return KernelResult.Fail(KernelError.CapacityExhausted, "Budget account owner capacity is exhausted.");
        parent.Children.EnsureCapacity(parent.Children.Count + 1);
        _accounts.EnsureCapacity(_accounts.Count + routeCount);
        return KernelResult.Ok();
    }

    private KernelResult ValidateAccountContinuity(AccountRecord account)
    {
        var current = account;
        for (var remaining = 3; remaining > 0; remaining--)
        {
            if (!_accounts.TryGetValue(current.Handle.AccountId, out var routed) || !ReferenceEquals(routed, current))
                break;
            if (current.OriginalParent is not { } parent)
                return ReferenceEquals(current, _systemAccount) && current.Parent is null && current.Level == BudgetAccountLevel.System
                    ? KernelResult.Ok()
                    : KernelResult.Fail(KernelError.PlatformFaulted, "Original quantitative system owner was lost.");
            if (current.Parent != parent.Handle || (int)current.Level != (int)parent.Level + 1 ||
                !parent.Children.Any(child => ReferenceEquals(child, current)))
                break;
            current = parent;
        }
        return KernelResult.Fail(KernelError.PlatformFaulted, "Original quantitative account hierarchy was lost.");
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

    private static bool MatchesTemporalConsumer(ReservationRecord record, Guid? composition) =>
        record.TemporalComposition == composition &&
        (composition is null || record.TemporalCompositionCommitted);

    private KernelResult<ReservationRecord> ResolveReservation(ProcessHandle owner, BudgetReservationHandle reservation)
    {
        if (!_reservations.TryGetValue(reservation.ReservationId, out var record) || record.Handle != reservation)
            return KernelResult<ReservationRecord>.Fail(KernelError.StaleGeneration, "Budget lease generation is stale.");
        if (record.Owner != owner)
            return KernelResult<ReservationRecord>.Fail(KernelError.WrongRegionOwner, "Budget lease belongs to another process generation.");
        var continuity = ValidateReservationContinuity(record);
        if (!continuity.IsSuccess) return KernelResult<ReservationRecord>.Fail(continuity.Error, continuity.Message!);
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
        var continuity = ValidateAccountContinuity(account);
        if (!continuity.IsSuccess) return KernelResult<AccountRecord>.Fail(continuity.Error, continuity.Message!);
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
        var pinned = OriginalReservations().Any(reservation => IsCapacityHeld(reservation.State) &&
            reservation.Lifetime == BudgetReservationLifetime.ExternalEffect && reservation.OriginalAccounts.Any(account => ReferenceEquals(account, record)));
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
        record.Handle, record.Account, record.Owner, Array.AsReadOnly(record.Amounts.ToArray()),
        record.Lifetime, record.QosHint, record.State,
        ChargedAmounts: Array.AsReadOnly(record.ChargedAmounts.ToArray()));

    private static BudgetReservationSnapshot Stale(BudgetReservationHandle handle, ProcessHandle owner) => new(
        handle, default, owner, [], BudgetReservationLifetime.LocalResource, AdmissionQosHint.None, BudgetReservationState.Stale);

    private static bool IsCapacityHeld(BudgetReservationState state) => state is
        BudgetReservationState.Reserved or BudgetReservationState.Bound or BudgetReservationState.Consuming or
        BudgetReservationState.Settling or BudgetReservationState.Quarantined or BudgetReservationState.Reconciled;
}
