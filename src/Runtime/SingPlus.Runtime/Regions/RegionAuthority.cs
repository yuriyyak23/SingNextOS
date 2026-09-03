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
    private sealed class RegionRecord
    {
        public required RegionId Id { get; init; }
        public required RegionGeneration Generation { get; set; }
        public required RegionOwner Owner { get; set; }
        public required long ByteLength { get; init; }
        public required string ElementType { get; init; }
        public required RegionState State { get; set; }
        public RegionOwner? Borrower { get; set; }
        public BorrowLeaseGeneration BorrowGeneration { get; set; }
        public BorrowLeaseLifetime? BorrowLifetime { get; set; }
        public ITransferableOwnedPayload? Payload { get; set; }
        public bool PlatformMappingReserved { get; set; }
        public bool ExternalBorrowReadGrantReserved { get; set; }
    }

    private readonly Dictionary<RegionId, RegionRecord> _regions = [];
    private ulong _nextRegionId = 1;

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
        if (record.BorrowGeneration.Value == ulong.MaxValue) return KernelResult<BorrowLeaseGrant>.Fail(KernelError.CapacityExhausted, "Borrow lease generation is exhausted.");

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
            reclaimed.Add(new RegionHandle(record.Id, record.Generation));
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

    private static RegionDescriptor Descriptor(RegionRecord record) => new(new RegionHandle(record.Id, record.Generation), record.Owner, record.ByteLength, record.ElementType, record.State);
}
