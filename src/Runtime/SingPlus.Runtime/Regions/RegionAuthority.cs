using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

internal readonly record struct BorrowLeaseGrant(BorrowLeaseHandle Handle, BorrowLeaseLifetime Lifetime);

internal readonly record struct BorrowLeaseAuthoritySnapshot(
    BorrowLeaseHandle Handle,
    RegionOwner Owner,
    RegionOwner Borrower,
    long ByteLength,
    BorrowLeaseLifetime Lifetime);

public sealed class RegionAuthority
{
    private sealed class RegionUseRecord
    {
        public required RegionUseHandle Handle { get; init; }
        public required RegionHandle Region { get; init; }
        public required RegionOwner Principal { get; init; }
        public required RegionUseRange Range { get; init; }
        public required RegionUseMode Mode { get; init; }
        public required MutationEpoch MutationEpoch { get; init; }
        public required RegionUseState State { get; set; }
    }

    private sealed class RegionRecord
    {
        public required RegionId Id { get; init; }
        public required RegionGeneration Generation { get; set; }
        public required RegionOwner Owner { get; set; }
        public required long ByteLength { get; init; }
        public required string ElementType { get; init; }
        public required RegionState State { get; set; }
        public required MutationEpoch MutationEpoch { get; set; }
        public RegionOwner? Borrower { get; set; }
        public BorrowLeaseGeneration BorrowGeneration { get; set; }
        public BorrowLeaseLifetime? BorrowLifetime { get; set; }
        public ITransferableOwnedPayload? Payload { get; set; }
        public bool PlatformMappingReserved { get; set; }
        public bool ExternalBorrowReadGrantReserved { get; set; }
        public RegionBackingLeaseDescriptor? BackingLease { get; set; }
        public Dictionary<RegionUseId, RegionUseRecord> Uses { get; } = [];
    }

    private readonly Dictionary<RegionId, RegionRecord> _regions = [];
    private ulong _nextRegionId = 1;
    private ulong _nextRegionUseId = 1;
    private ulong _nextBackingLeaseId = 1;

    public KernelResult<RegionBackingLeaseDescriptor> ReserveBacking(RegionHandle handle, RegionOwner owner)
    {
        var validation = Validate(handle, owner);
        if (!validation.IsSuccess) return KernelResult<RegionBackingLeaseDescriptor>.Fail(validation.Error, validation.Message!);
        var record = _regions[handle.RegionId];
        if (record.BackingLease is not null)
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.PlatformBindingActive, "The region already has an active backing lease.");
        if (_nextBackingLeaseId == 0)
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.CapacityExhausted, "Backing lease identity space is exhausted.");
        var lease = new RegionBackingLeaseDescriptor(new(new(_nextBackingLeaseId++), 1), handle, owner, record.ByteLength);
        record.BackingLease = lease;
        return KernelResult<RegionBackingLeaseDescriptor>.Ok(lease);
    }

    public KernelResult<RegionBackingLeaseDescriptor> ValidateBacking(RegionBackingLeaseHandle handle, RegionOwner owner)
    {
        var record = _regions.Values.SingleOrDefault(item => item.BackingLease?.Handle.LeaseId == handle.LeaseId);
        if (record?.BackingLease is not { } lease)
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.PlatformBindingNotFound, "Region backing lease was not found.");
        if (lease.Handle != handle || lease.Region.Generation != record.Generation)
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.StaleGeneration, "Region backing lease generation is stale.");
        if (lease.Owner != owner)
            return KernelResult<RegionBackingLeaseDescriptor>.Fail(KernelError.WrongRegionOwner, "Region backing lease owner does not match.");
        return KernelResult<RegionBackingLeaseDescriptor>.Ok(lease);
    }

    public KernelResult ReleaseBacking(RegionBackingLeaseHandle handle, RegionOwner owner)
    {
        var validation = ValidateBacking(handle, owner);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        _regions[validation.Value!.Region.RegionId].BackingLease = null;
        return KernelResult.Ok();
    }

    internal RegionDescriptor Allocate(RegionOwner owner, long byteLength, string elementType)
    {
        if (byteLength <= 0) throw new ArgumentOutOfRangeException(nameof(byteLength));
        var id = new RegionId(_nextRegionId++);
        var record = new RegionRecord
        {
            Id = id,
            Generation = new RegionGeneration(1),
            Owner = owner,
            ByteLength = byteLength,
            ElementType = elementType,
            State = RegionState.Allocated,
            MutationEpoch = new MutationEpoch(1),
            BorrowGeneration = new BorrowLeaseGeneration(0)
        };
        _regions.Add(id, record);
        record.State = RegionState.Owned;
        return Descriptor(record);
    }

    public KernelResult<RegionDescriptor> Validate(RegionHandle handle, RegionOwner owner, RegionState requiredState = RegionState.Owned)
    {
        if (!_regions.TryGetValue(handle.RegionId, out var record)) return KernelResult<RegionDescriptor>.Fail(KernelError.RegionNotFound, $"Region {handle.RegionId.Value} was not found.");
        if (record.Generation != handle.Generation) return KernelResult<RegionDescriptor>.Fail(KernelError.StaleGeneration, "Region generation is stale.");
        if (record.Owner != owner) return KernelResult<RegionDescriptor>.Fail(KernelError.WrongRegionOwner, "Region owner does not match.");
        if (record.State != requiredState) return KernelResult<RegionDescriptor>.Fail(KernelError.InvalidRegionState, $"Expected {requiredState}, got {record.State}.");
        return KernelResult<RegionDescriptor>.Ok(Descriptor(record));
    }

    public KernelResult<BorrowLeaseHandle> Loan(RegionHandle handle, RegionOwner owner, RegionOwner borrower)
    {
        var acquired = AcquireLoan(handle, owner, borrower);
        return acquired.IsSuccess
            ? KernelResult<BorrowLeaseHandle>.Ok(acquired.Value!.Handle)
            : KernelResult<BorrowLeaseHandle>.Fail(acquired.Error, acquired.Message!);
    }

    internal KernelResult<BorrowLeaseGrant> AcquireLoan(RegionHandle handle, RegionOwner owner, RegionOwner borrower)
    {
        if (owner == borrower) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.InvalidRegionState, "A region cannot be loaned to its owner.");
        var validation = Validate(handle, owner);
        if (!validation.IsSuccess) return KernelResult<BorrowLeaseGrant>.Fail(validation.Error, validation.Message!);
        var record = _regions[handle.RegionId];
        if (record.PlatformMappingReserved) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.PlatformBindingActive, "An owned region with an active platform mapping cannot be loaned.");
        if (record.ExternalBorrowReadGrantReserved) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.PlatformBindingActive, "An owned region with an active external borrow read grant cannot be loaned.");
        if (record.BackingLease is not null) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.PlatformBindingActive, "An owned region with an active backing lease cannot be loaned.");
        if (HasActiveWriteUse(record)) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.RegionUseConflict, "An owned region with an active writable use cannot be loaned.");
        if (record.BorrowGeneration.Value == ulong.MaxValue) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.CapacityExhausted, "Borrow lease generation is exhausted.");
        var mutation = AdvanceMutation(record);
        if (!mutation.IsSuccess) return KernelResult<BorrowLeaseGrant>.Fail(mutation.Error, mutation.Message!);

        var generation = new BorrowLeaseGeneration(record.BorrowGeneration.Value + 1);
        var lifetime = new BorrowLeaseLifetime();
        var lease = new BorrowLeaseHandle(new RegionHandle(record.Id, record.Generation), generation);
        record.BorrowGeneration = generation;
        record.Borrower = borrower;
        record.BorrowLifetime = lifetime;
        record.State = RegionState.Loaned;
        return KernelResult<BorrowLeaseGrant>.Ok(new BorrowLeaseGrant(lease, lifetime));
    }

    internal KernelResult<BorrowLeaseAuthoritySnapshot> ValidateBorrowLease(
        BorrowLeaseHandle lease,
        RegionOwner owner,
        RegionOwner borrower)
    {
        if (!_regions.TryGetValue(lease.Region.RegionId, out var record))
            return KernelResult<BorrowLeaseAuthoritySnapshot>.Fail(KernelError.RegionNotFound, "Region was not found.");
        if (record.Generation != lease.Region.Generation)
            return KernelResult<BorrowLeaseAuthoritySnapshot>.Fail(KernelError.StaleGeneration, "Region generation is stale.");
        if (record.BorrowGeneration != lease.Generation)
            return KernelResult<BorrowLeaseAuthoritySnapshot>.Fail(KernelError.StaleGeneration, "Borrow lease generation is stale.");
        if (record.Owner != owner)
            return KernelResult<BorrowLeaseAuthoritySnapshot>.Fail(KernelError.WrongRegionOwner, "Region owner does not match the borrow lease.");
        if (record.State != RegionState.Loaned || record.Borrower != borrower || record.BorrowLifetime is null || !record.BorrowLifetime.IsActive)
            return KernelResult<BorrowLeaseAuthoritySnapshot>.Fail(KernelError.InvalidRegionState, "Borrow lease is not active for the specified borrower.");

        return KernelResult<BorrowLeaseAuthoritySnapshot>.Ok(
            new BorrowLeaseAuthoritySnapshot(
                lease,
                record.Owner,
                borrower,
                record.ByteLength,
                record.BorrowLifetime));
    }

    internal KernelResult ReserveExternalBorrowReadGrant(
        BorrowLeaseHandle lease,
        RegionOwner owner,
        RegionOwner borrower)
    {
        var validation = ValidateBorrowLease(lease, owner, borrower);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        var record = _regions[lease.Region.RegionId];
        if (record.PlatformMappingReserved)
            return KernelResult.Fail(KernelError.PlatformBindingActive, "The borrowed region already has an owned-region platform mapping reservation.");
        if (record.ExternalBorrowReadGrantReserved)
            return KernelResult.Fail(KernelError.PlatformBindingActive, "The borrow lease already has an active external read grant.");
        record.ExternalBorrowReadGrantReserved = true;
        return KernelResult.Ok();
    }

    internal KernelResult ReleaseExternalBorrowReadGrantReservation(
        BorrowLeaseHandle lease,
        RegionOwner owner,
        RegionOwner borrower,
        BorrowLeaseLifetime expectedLifetime)
    {
        var validation = ValidateBorrowLease(lease, owner, borrower);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        var record = _regions[lease.Region.RegionId];
        if (!ReferenceEquals(record.BorrowLifetime, expectedLifetime))
            return KernelResult.Fail(KernelError.StaleGeneration, "Borrow lease lifetime is stale.");
        if (!record.ExternalBorrowReadGrantReserved)
            return KernelResult.Fail(KernelError.PlatformBindingNotFound, "The borrow lease does not have an active external read grant reservation.");
        record.ExternalBorrowReadGrantReserved = false;
        return KernelResult.Ok();
    }

    public KernelResult ReturnLoan(BorrowLeaseHandle lease, RegionOwner borrower)
    {
        if (!_regions.TryGetValue(lease.Region.RegionId, out var record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        if (record.Generation != lease.Region.Generation) return KernelResult.Fail(KernelError.StaleGeneration, "Region generation is stale.");
        if (record.BorrowGeneration != lease.Generation) return KernelResult.Fail(KernelError.StaleGeneration, "Borrow lease generation is stale.");
        if (record.State != RegionState.Loaned || record.Borrower != borrower || record.BorrowLifetime is null) return KernelResult.Fail(KernelError.InvalidRegionState, "Borrow lease is not active for the specified borrower.");
        if (record.ExternalBorrowReadGrantReserved) return KernelResult.Fail(KernelError.PlatformBindingActive, "The CPU borrow cannot complete while its external read grant is active or draining.");
        var mutation = AdvanceMutation(record);
        if (!mutation.IsSuccess) return mutation;
        record.BorrowLifetime.InvalidateForRuntime();
        record.BorrowLifetime = null;
        record.Borrower = null;
        record.State = RegionState.Owned;
        return KernelResult.Ok();
    }

    public KernelResult RevokeLoan(BorrowLeaseHandle lease, RegionOwner owner)
    {
        if (!_regions.TryGetValue(lease.Region.RegionId, out var record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        if (record.Generation != lease.Region.Generation) return KernelResult.Fail(KernelError.StaleGeneration, "Region generation is stale.");
        if (record.BorrowGeneration != lease.Generation) return KernelResult.Fail(KernelError.StaleGeneration, "Borrow lease generation is stale.");
        if (record.Owner != owner) return KernelResult.Fail(KernelError.WrongRegionOwner, "Region owner does not match.");
        if (record.State != RegionState.Loaned || record.Borrower is null || record.BorrowLifetime is null) return KernelResult.Fail(KernelError.InvalidRegionState, "Region does not have an active borrow lease.");
        if (record.ExternalBorrowReadGrantReserved) return KernelResult.Fail(KernelError.PlatformBindingActive, "The CPU borrow cannot be revoked while its external read grant is active or draining.");
        var mutation = AdvanceMutation(record);
        if (!mutation.IsSuccess) return mutation;
        record.BorrowLifetime.InvalidateForRuntime();
        record.BorrowLifetime = null;
        record.Borrower = null;
        record.State = RegionState.Owned;
        return KernelResult.Ok();
    }

    internal KernelResult ReservePlatformMapping(RegionHandle handle, RegionOwner owner)
    {
        var validation = Validate(handle, owner);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        var record = _regions[handle.RegionId];
        if (record.PlatformMappingReserved) return KernelResult.Fail(KernelError.PlatformBindingActive, "The owned region already has an active platform mapping.");
        if (record.ExternalBorrowReadGrantReserved) return KernelResult.Fail(KernelError.PlatformBindingActive, "The region has an active external borrow read grant.");
        if (record.Uses.Values.Any(use => use.State == RegionUseState.Active &&
                use.MutationEpoch == record.MutationEpoch && use.Region.Generation == record.Generation &&
                use.Mode != RegionUseMode.DevicePrivate))
            return KernelResult.Fail(KernelError.RegionUseConflict, "The whole region has an incompatible active use and cannot acquire a separate platform mapping reservation.");
        record.PlatformMappingReserved = true;
        return KernelResult.Ok();
    }

    internal KernelResult ReleasePlatformMappingReservation(RegionHandle handle, RegionOwner owner)
    {
        var validation = Validate(handle, owner);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        var record = _regions[handle.RegionId];
        if (!record.PlatformMappingReserved) return KernelResult.Fail(KernelError.PlatformBindingNotFound, "The owned region does not have an active platform mapping.");
        record.PlatformMappingReserved = false;
        return KernelResult.Ok();
    }

    internal KernelResult<RegionHandle> Transfer(RegionHandle handle, RegionOwner source, RegionOwner target)
    {
        var validation = Validate(handle, source);
        if (!validation.IsSuccess) return KernelResult<RegionHandle>.Fail(validation.Error, validation.Message!);
        var record = _regions[handle.RegionId];
        if (record.PlatformMappingReserved) return KernelResult<RegionHandle>.Fail(KernelError.PlatformBindingActive, "An owned region with an active platform mapping cannot be transferred.");
        if (record.ExternalBorrowReadGrantReserved) return KernelResult<RegionHandle>.Fail(KernelError.PlatformBindingActive, "A region with an active external borrow read grant cannot be transferred.");
        if (record.BackingLease is not null) return KernelResult<RegionHandle>.Fail(KernelError.PlatformBindingActive, "A region with an active backing lease cannot be transferred.");
        if (HasActiveWriteUse(record)) return KernelResult<RegionHandle>.Fail(KernelError.RegionUseConflict, "An owned region with an active writable use cannot be transferred.");
        if (record.Generation.Value == ulong.MaxValue) return KernelResult<RegionHandle>.Fail(KernelError.CapacityExhausted, "Region generation is exhausted.");
        var mutation = AdvanceMutation(record);
        if (!mutation.IsSuccess) return KernelResult<RegionHandle>.Fail(mutation.Error, mutation.Message!);
        record.State = RegionState.Transferred;
        record.Owner = target;
        record.Generation = new RegionGeneration(record.Generation.Value + 1);
        record.State = RegionState.Owned;
        return KernelResult<RegionHandle>.Ok(new RegionHandle(record.Id, record.Generation));
    }

    internal KernelResult Release(RegionHandle handle, RegionOwner owner)
    {
        var validation = Validate(handle, owner);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        var record = _regions[handle.RegionId];
        if (record.PlatformMappingReserved) return KernelResult.Fail(KernelError.PlatformBindingActive, "An owned region with an active platform mapping cannot be released.");
        if (record.ExternalBorrowReadGrantReserved) return KernelResult.Fail(KernelError.PlatformBindingActive, "A region with an active external borrow read grant cannot be released.");
        if (record.BackingLease is not null) return KernelResult.Fail(KernelError.PlatformBindingActive, "A region with an active backing lease cannot be released.");
        if (HasActiveWriteUse(record)) return KernelResult.Fail(KernelError.RegionUseConflict, "An owned region with an active writable use cannot be released.");
        var mutation = AdvanceMutation(record);
        if (!mutation.IsSuccess) return mutation;
        record.State = RegionState.Released;
        record.Payload = null;
        return KernelResult.Ok();
    }

    internal void RegisterPayload(RegionHandle handle, ITransferableOwnedPayload payload) =>
        _regions[handle.RegionId].Payload = payload;

    internal void ReplacePayload(RegionHandle oldHandle, RegionHandle newHandle, ITransferableOwnedPayload payload)
    {
        var record = _regions[newHandle.RegionId];
        if (record.Generation != newHandle.Generation || oldHandle.RegionId != newHandle.RegionId)
            throw new InvalidOperationException("Region payload handle does not match the authoritative record.");
        record.Payload = payload;
    }

    public KernelResult<RegionUseDescriptor> AcquireUse(
        RegionHandle handle,
        RegionOwner principal,
        RegionUseMode mode,
        RegionUseRange range)
    {
        var validation = Validate(handle, principal);
        if (!validation.IsSuccess)
            return KernelResult<RegionUseDescriptor>.Fail(validation.Error, validation.Message!);

        return AcquireUseCore(_regions[handle.RegionId], principal, mode, range);
    }

    public KernelResult ProbeUse(
        RegionHandle handle,
        RegionOwner principal,
        RegionUseMode mode,
        RegionUseRange range)
    {
        var validation = Validate(handle, principal);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        return ValidateUseRequest(_regions[handle.RegionId], mode, range);
    }

    public KernelResult<RegionUseDescriptor> AcquireBorrowUse(
        BorrowLeaseHandle lease,
        RegionOwner owner,
        RegionOwner borrower,
        RegionUseMode mode,
        RegionUseRange range)
    {
        var validation = ValidateBorrowLease(lease, owner, borrower);
        if (!validation.IsSuccess)
            return KernelResult<RegionUseDescriptor>.Fail(validation.Error, validation.Message!);
        if (IsWriteMode(mode))
        {
            return KernelResult<RegionUseDescriptor>.Fail(
                KernelError.InsufficientRights,
                "A read-only borrow lease cannot authorize a writable region use.");
        }

        return AcquireUseCore(_regions[lease.Region.RegionId], borrower, mode, range);
    }

    public KernelResult<RegionUseDescriptor> ValidateUse(
        RegionUseHandle handle,
        RegionOwner principal)
    {
        var lookup = FindUse(handle);
        if (!lookup.IsSuccess)
            return KernelResult<RegionUseDescriptor>.Fail(lookup.Error, lookup.Message!);

        var (region, use) = lookup.Value!;
        if (use.Principal != principal)
        {
            return KernelResult<RegionUseDescriptor>.Fail(
                KernelError.WrongRegionOwner,
                "Region use principal does not match.");
        }
        if (use.State == RegionUseState.Released)
        {
            return KernelResult<RegionUseDescriptor>.Fail(
                KernelError.InvalidRegionState,
                "Region use has been released.");
        }
        if (use.State == RegionUseState.Invalidated ||
            use.Region.Generation != region.Generation ||
            use.MutationEpoch != region.MutationEpoch)
        {
            return KernelResult<RegionUseDescriptor>.Fail(
                KernelError.StaleGeneration,
                "Region use mutation or ownership generation is stale; reacquire instead of refreshing it.");
        }

        return KernelResult<RegionUseDescriptor>.Ok(UseDescriptor(use));
    }

    public KernelResult ReleaseUse(RegionUseHandle handle, RegionOwner principal)
    {
        var lookup = FindUse(handle);
        if (!lookup.IsSuccess) return KernelResult.Fail(lookup.Error, lookup.Message!);

        var (region, use) = lookup.Value!;
        if (use.Principal != principal)
            return KernelResult.Fail(KernelError.WrongRegionOwner, "Region use principal does not match.");
        if (use.State == RegionUseState.Released) return KernelResult.Ok();

        use.State = RegionUseState.Released;
        if (!IsWriteMode(use.Mode) || use.MutationEpoch != region.MutationEpoch)
            return KernelResult.Ok();

        return AdvanceMutation(region);
    }

    public KernelResult InvalidateUse(RegionUseHandle handle, RegionOwner principal)
    {
        var lookup = FindUse(handle);
        if (!lookup.IsSuccess) return KernelResult.Fail(lookup.Error, lookup.Message!);

        var (region, use) = lookup.Value!;
        if (use.Principal != principal)
            return KernelResult.Fail(KernelError.WrongRegionOwner, "Region use principal does not match.");
        if (use.State != RegionUseState.Active) return KernelResult.Ok();

        use.State = RegionUseState.Invalidated;
        if (!IsWriteMode(use.Mode) || use.MutationEpoch != region.MutationEpoch)
            return KernelResult.Ok();

        return AdvanceMutation(region);
    }

    public IReadOnlyList<RegionUseDescriptor> SnapshotUses() =>
        _regions.Values
            .SelectMany(static region => region.Uses.Values)
            .Select(UseDescriptor)
            .OrderBy(static use => use.Handle.UseId.Value)
            .ToArray();

    internal IReadOnlyList<RegionHandle> ReturnAllLoansForBorrowerDomain(DomainId borrowerDomainId)
    {
        var candidates = _regions.Values
            .Where(r => r.State == RegionState.Loaned && r.Borrower?.DomainId == borrowerDomainId)
            .ToArray();
        if (candidates.Any(static r => r.ExternalBorrowReadGrantReserved))
            throw new InvalidOperationException("External borrow read grants must reach verified closure before borrower-domain loan reclaim.");

        var returned = new List<RegionHandle>();
        foreach (var record in candidates)
        {
            returned.Add(new RegionHandle(record.Id, record.Generation));
            AdvanceMutationOrThrow(record);
            record.BorrowLifetime?.InvalidateForRuntime();
            record.BorrowLifetime = null;
            record.Borrower = null;
            record.State = RegionState.Owned;
        }
        return returned.OrderBy(static h => h.RegionId.Value).ToArray();
    }

    internal IReadOnlyList<RegionHandle> ReclaimAllForDomain(DomainId domainId)
    {
        var reclaimed = new List<RegionHandle>();
        foreach (var record in _regions.Values.Where(r => r.Owner.DomainId == domainId && r.State is RegionState.Owned or RegionState.Loaned))
        {
            if (record.PlatformMappingReserved) throw new InvalidOperationException("Platform-mapped regions must be revoked before domain reclaim.");
            if (record.ExternalBorrowReadGrantReserved) throw new InvalidOperationException("External borrow read grants must be revoked before domain reclaim.");
            if (record.BackingLease is not null) throw new InvalidOperationException("External backing leases must reach verified closure before domain reclaim.");
            reclaimed.Add(new RegionHandle(record.Id, record.Generation));
            AdvanceMutationOrThrow(record);
            record.BorrowLifetime?.InvalidateForRuntime();
            record.BorrowLifetime = null;
            record.Payload?.InvalidateForRuntime();
            record.Payload = null;
            record.Borrower = null;
            record.State = RegionState.Released;
        }
        return reclaimed.OrderBy(static h => h.RegionId.Value).ToArray();
    }

    public IReadOnlyList<RegionDescriptor> Snapshot() => _regions.Values.Select(Descriptor).OrderBy(static d => d.Handle.RegionId.Value).ToArray();

    private KernelResult<RegionUseDescriptor> AcquireUseCore(
        RegionRecord record,
        RegionOwner principal,
        RegionUseMode mode,
        RegionUseRange range)
    {
        var requestValidation = ValidateUseRequest(record, mode, range);
        if (!requestValidation.IsSuccess)
            return KernelResult<RegionUseDescriptor>.Fail(requestValidation.Error, requestValidation.Message!);
        if (_nextRegionUseId == 0)
            return KernelResult<RegionUseDescriptor>.Fail(KernelError.CapacityExhausted, "Region use identity space is exhausted.");

        if (IsWriteMode(mode))
        {
            var mutation = AdvanceMutation(record);
            if (!mutation.IsSuccess)
                return KernelResult<RegionUseDescriptor>.Fail(mutation.Error, mutation.Message!);
        }

        var use = new RegionUseRecord
        {
            Handle = new RegionUseHandle(new RegionUseId(_nextRegionUseId++), 1),
            Region = new RegionHandle(record.Id, record.Generation),
            Principal = principal,
            Range = range,
            Mode = mode,
            MutationEpoch = record.MutationEpoch,
            State = RegionUseState.Active
        };
        record.Uses.Add(use.Handle.UseId, use);
        return KernelResult<RegionUseDescriptor>.Ok(UseDescriptor(use));
    }

    private static KernelResult ValidateUseRequest(
        RegionRecord record,
        RegionUseMode mode,
        RegionUseRange range)
    {
        if (!Enum.IsDefined(mode))
            return KernelResult.Fail(KernelError.InvalidRegionState, "Region use mode is invalid.");
        if (mode == RegionUseMode.SharedReadMostly)
            return KernelResult.Fail(KernelError.PlatformUnsupported, "SharedReadMostly is future-gated.");
        if (mode == RegionUseMode.DirectCoherentWrite)
            return KernelResult.Fail(KernelError.PlatformUnsupported, "DirectCoherentWrite is future-gated until CPU alias exclusion and symmetric coherent release are implemented.");
        if (!IsValidRange(record, range))
            return KernelResult.Fail(KernelError.InvalidRegionState, "Region use range is outside the exact region bounds or overflows them.");
        if (record.PlatformMappingReserved)
            return KernelResult.Fail(KernelError.RegionUseConflict, "A separate platform mapping already reserves the whole region.");

        var activeUses = record.Uses.Values.Where(use =>
            use.State == RegionUseState.Active &&
            use.MutationEpoch == record.MutationEpoch &&
            use.Region.Generation == record.Generation);
        return activeUses.Any(existing => !AreCompatible(existing.Mode, mode))
            ? KernelResult.Fail(KernelError.RegionUseConflict, "The whole region already has an incompatible active use.")
            : KernelResult.Ok();
    }

    private KernelResult<(RegionRecord Region, RegionUseRecord Use)> FindUse(RegionUseHandle handle)
    {
        if (handle.UseId.Value == 0 || handle.Generation != 1)
            return KernelResult<(RegionRecord, RegionUseRecord)>.Fail(KernelError.RegionUseNotFound, "Region use handle is invalid.");

        foreach (var region in _regions.Values)
        {
            if (region.Uses.TryGetValue(handle.UseId, out var use) && use.Handle == handle)
                return KernelResult<(RegionRecord, RegionUseRecord)>.Ok((region, use));
        }

        return KernelResult<(RegionRecord, RegionUseRecord)>.Fail(KernelError.RegionUseNotFound, "Region use was not found.");
    }

    private static bool IsValidRange(RegionRecord record, RegionUseRange range) =>
        range.Offset >= 0 &&
        range.Length > 0 &&
        range.Offset <= record.ByteLength - range.Length;

    private static bool IsWriteMode(RegionUseMode mode) => mode is
        RegionUseMode.ExclusiveWrite or
        RegionUseMode.StagedOutput or
        RegionUseMode.DirectCoherentWrite or
        RegionUseMode.DevicePrivate;

    private static bool AreCompatible(RegionUseMode left, RegionUseMode right) =>
        !IsWriteMode(left) && !IsWriteMode(right);

    private static bool HasActiveUse(RegionRecord record) => record.Uses.Values.Any(use =>
        use.State == RegionUseState.Active &&
        use.MutationEpoch == record.MutationEpoch &&
        use.Region.Generation == record.Generation);

    private static bool HasActiveWriteUse(RegionRecord record) => record.Uses.Values.Any(use =>
        use.State == RegionUseState.Active &&
        use.MutationEpoch == record.MutationEpoch &&
        use.Region.Generation == record.Generation &&
        IsWriteMode(use.Mode));

    private static KernelResult AdvanceMutation(RegionRecord record)
    {
        if (record.MutationEpoch.Value == ulong.MaxValue)
            return KernelResult.Fail(KernelError.CapacityExhausted, "Region mutation epoch is exhausted.");

        foreach (var use in record.Uses.Values.Where(static use => use.State == RegionUseState.Active))
            use.State = RegionUseState.Invalidated;
        record.MutationEpoch = new MutationEpoch(record.MutationEpoch.Value + 1);
        return KernelResult.Ok();
    }

    private static void AdvanceMutationOrThrow(RegionRecord record)
    {
        var result = AdvanceMutation(record);
        if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
    }

    private static RegionUseDescriptor UseDescriptor(RegionUseRecord use) => new(
        use.Handle,
        use.Region,
        use.Principal,
        use.Range,
        use.Mode,
        use.MutationEpoch,
        use.State);

    private static RegionDescriptor Descriptor(RegionRecord record) => new(
        new RegionHandle(record.Id, record.Generation),
        record.Owner,
        record.ByteLength,
        record.ElementType,
        record.State,
        record.MutationEpoch);
}
