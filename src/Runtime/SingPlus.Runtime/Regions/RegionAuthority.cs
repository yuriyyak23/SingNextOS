using SingPlus.Contracts;

namespace SingPlus.Runtime;

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
            State = RegionState.Allocated
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

    public KernelResult Loan(RegionHandle handle, RegionOwner owner, RegionOwner borrower)
    {
        var validation = Validate(handle, owner);
        if (!validation.IsSuccess) return KernelResult.Fail(validation.Error, validation.Message!);
        var record = _regions[handle.RegionId];
        record.Borrower = borrower;
        record.State = RegionState.Loaned;
        return KernelResult.Ok();
    }

    public KernelResult ReturnLoan(RegionHandle handle, RegionOwner owner, RegionOwner borrower)
    {
        if (!_regions.TryGetValue(handle.RegionId, out var record)) return KernelResult.Fail(KernelError.RegionNotFound, "Region was not found.");
        if (record.Generation != handle.Generation) return KernelResult.Fail(KernelError.StaleGeneration, "Region generation is stale.");
        if (record.Owner != owner || record.Borrower != borrower || record.State != RegionState.Loaned) return KernelResult.Fail(KernelError.InvalidRegionState, "Region is not loaned to the specified borrower.");
        record.Borrower = null;
        record.State = RegionState.Owned;
        return KernelResult.Ok();
    }

    internal KernelResult<RegionHandle> Transfer(RegionHandle handle, RegionOwner source, RegionOwner target)
    {
        var validation = Validate(handle, source);
        if (!validation.IsSuccess) return KernelResult<RegionHandle>.Fail(validation.Error, validation.Message!);
        var record = _regions[handle.RegionId];
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
        _regions[handle.RegionId].State = RegionState.Released;
        return KernelResult.Ok();
    }

    internal IReadOnlyList<RegionHandle> ReclaimAllForDomain(DomainId domainId)
    {
        var reclaimed = new List<RegionHandle>();
        foreach (var record in _regions.Values.Where(r => r.Owner.DomainId == domainId && r.State is RegionState.Owned or RegionState.Loaned))
        {
            reclaimed.Add(new RegionHandle(record.Id, record.Generation));
            record.Borrower = null;
            record.State = RegionState.Released;
        }
        return reclaimed.OrderBy(static h => h.RegionId.Value).ToArray();
    }

    public IReadOnlyList<RegionDescriptor> Snapshot() => _regions.Values.Select(Descriptor).OrderBy(static d => d.Handle.RegionId.Value).ToArray();

    private static RegionDescriptor Descriptor(RegionRecord record) => new(new RegionHandle(record.Id, record.Generation), record.Owner, record.ByteLength, record.ElementType, record.State);
}
