using SingPlus.Contracts;

namespace SingPlus.Runtime;

/// <summary>
/// Conservative gate until a separately reviewed distributed ownership protocol exists.
/// Fenced means local admission is stopped, not remote isolation. Without an
/// original external closure producer all acquired uses remain retained.
/// </summary>
public sealed class CxlMultiHostGate : ICxlTeardownParticipant
{
    private readonly RegionAuthority regions;
    private readonly RuntimeKernel kernel;
    private readonly object _gate = new();
    private sealed class Record(MultiHostMemoryBinding binding, RegionOwner owner, RegionUseHandle use)
    {
        public MultiHostMemoryBinding Binding { get; set; } = binding;
        public RegionOwner Owner { get; } = owner;
        public RegionUseHandle Use { get; set; } = use;
    }
    private readonly Dictionary<MultiHostBindingId, Record> _bindings = [];
    private ulong _nextId = 1;

    public CxlMultiHostGate(RuntimeKernel kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        this.kernel = kernel;
        regions = kernel.Regions;
        kernel.RegisterCxlTeardownParticipant(this);
    }

    public KernelResult<MultiHostMemoryBinding> Bind(
        RegionHandle region,
        RegionOwner owner,
        ExternalHostIdentity host,
        MultiHostAccessMode access,
        bool providerSupportsReadOnlySharing,
        bool distributedWritableAuthorityAvailable = false)
    {
        KernelResult<MultiHostMemoryBinding> result = default;
        var admitted = kernel.CommitCxlRegionOwnerAdmission(owner, () =>
        {
            lock (_gate)
                result = BindLocked(region, owner, host, access, providerSupportsReadOnlySharing, distributedWritableAuthorityAvailable);
            return result.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(result.Error, result.Message!);
        });
        return admitted.IsSuccess ? result : KernelResult<MultiHostMemoryBinding>.Fail(admitted.Error, admitted.Message!);
    }

    private KernelResult<MultiHostMemoryBinding> BindLocked(
        RegionHandle region, RegionOwner owner, ExternalHostIdentity host,
        MultiHostAccessMode access, bool providerSupportsReadOnlySharing, bool distributedWritableAuthorityAvailable)
    {
        if (string.IsNullOrWhiteSpace(host.Value) || !Enum.IsDefined(access))
            return KernelResult<MultiHostMemoryBinding>.Fail(KernelError.InvalidMessage, "Multi-host binding request is invalid.");
        var descriptor = regions.Validate(region, owner);
        if (!descriptor.IsSuccess) return KernelResult<MultiHostMemoryBinding>.Fail(descriptor.Error, descriptor.Message!);
        var live = _bindings.Values.Where(record => record.Binding.Region.RegionId == region.RegionId && record.Binding.State != MultiHostBindingState.Released).ToArray();
        if (access == MultiHostAccessMode.Writable && distributedWritableAuthorityAvailable)
            return KernelResult<MultiHostMemoryBinding>.Fail(KernelError.PlatformUnsupported, "Distributed writable ownership is not implemented or reviewed.");
        if (access == MultiHostAccessMode.Writable && live.Length != 0)
            return KernelResult<MultiHostMemoryBinding>.Fail(KernelError.RegionUseConflict, "Writable host rebinding requires prior host fence and verified reclaim.");
        if (access == MultiHostAccessMode.ReadOnly && (!providerSupportsReadOnlySharing || live.Any(record => record.Binding.Access != MultiHostAccessMode.ReadOnly)))
            return KernelResult<MultiHostMemoryBinding>.Fail(KernelError.PlatformUnsupported, "Read-only multi-host sharing is not explicitly supported or conflicts with a writer.");

        if (_nextId == 0)
            return KernelResult<MultiHostMemoryBinding>.Fail(KernelError.CapacityExhausted, "Multi-host binding identity space is exhausted.");
        // Prepare local publication before acquiring the irreversible use.
        _bindings.EnsureCapacity(_bindings.Count + 1);
        var handle = new MultiHostBindingHandle(new(_nextId), new(1));
        _nextId = _nextId == ulong.MaxValue ? 0 : _nextId + 1;
        var binding = new MultiHostMemoryBinding(handle, host, region, access, MultiHostBindingState.Active);
        var record = new Record(binding, owner, default);
        var mode = access == MultiHostAccessMode.Writable ? RegionUseMode.DevicePrivate : RegionUseMode.ReadOnly;
        var use = regions.AcquireUse(region, owner, mode, new(0, descriptor.Value!.ByteLength));
        if (!use.IsSuccess) return KernelResult<MultiHostMemoryBinding>.Fail(use.Error, use.Message!);
        record.Use = use.Value!.Handle;
        _bindings.Add(handle.BindingId, record);
        return KernelResult<MultiHostMemoryBinding>.Ok(binding);
    }

    public KernelResult<MultiHostMemoryBinding> Fence(MultiHostBindingHandle binding)
    {
        lock (_gate)
        {
        var record = Resolve(binding);
        if (!record.IsSuccess) return KernelResult<MultiHostMemoryBinding>.Fail(record.Error, record.Message!);
        if (record.Value!.Binding.State == MultiHostBindingState.Released) return KernelResult<MultiHostMemoryBinding>.Ok(record.Value.Binding);
        record.Value.Binding = record.Value.Binding with { State = MultiHostBindingState.Fenced };
        return KernelResult<MultiHostMemoryBinding>.Ok(record.Value.Binding);
        }
    }

    public KernelResult<MultiHostMemoryBinding> RecordHostDisappearance(MultiHostBindingHandle binding)
        => Fence(binding);

    public KernelResult BeginReclaim(MultiHostBindingHandle binding)
    {
        lock (_gate)
        {
        var record = Resolve(binding);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        if (record.Value!.Binding.State != MultiHostBindingState.Fenced)
            return KernelResult.Fail(KernelError.InvalidTransition, "Host binding must be fenced before reclaim.");
        return KernelResult.Fail(KernelError.PlatformUnsupported,
            "A local fence does not prove original remote no-further-use; RegionUse remains retained.");
        }
    }

    public KernelResult CompleteReclaim(MultiHostBindingHandle binding)
    {
        lock (_gate)
        {
        var record = Resolve(binding);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        return KernelResult.Fail(KernelError.PlatformUnsupported,
            "No original remote isolation producer is installed; RegionUse cannot be released.");
        }
    }

    public KernelResult<MultiHostMemoryBinding> Query(MultiHostBindingHandle binding)
    {
        lock (_gate)
        {
        var record = Resolve(binding);
        return record.IsSuccess
            ? KernelResult<MultiHostMemoryBinding>.Ok(record.Value!.Binding)
            : KernelResult<MultiHostMemoryBinding>.Fail(record.Error, record.Message!);
        }
    }

    private KernelResult<Record> Resolve(MultiHostBindingHandle handle)
    {
        if (!_bindings.TryGetValue(handle.BindingId, out var record))
            return KernelResult<Record>.Fail(KernelError.PlatformBindingNotFound, "Multi-host binding does not exist.");
        if (record.Binding.Binding.Generation != handle.Generation || record.Binding.Binding != handle)
            return KernelResult<Record>.Fail(KernelError.StaleGeneration, "Multi-host binding generation is stale.");
        return KernelResult<Record>.Ok(record);
    }

    KernelResult ICxlTeardownParticipant.CloseForProcess(ProcessHandle process, RegionOwner owner)
    {
        lock (_gate)
        {
        var retained = false;
        foreach (var record in _bindings.Values.Where(item => item.Owner == owner && item.Binding.State != MultiHostBindingState.Released).ToArray())
        {
            record.Binding = record.Binding with { State = MultiHostBindingState.Fenced };
            retained = true;
        }
        return retained ? KernelResult.Fail(KernelError.PlatformBindingDraining,
            "Local process teardown cannot prove remote isolation; original multi-host uses remain pinned.") : KernelResult.Ok();
        }
    }
}
