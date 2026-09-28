using SingPlus.Contracts;

namespace SingPlus.Runtime;

/// <summary>
/// Conservative gate until a separately reviewed distributed ownership protocol exists.
/// Writable bindings are single-host and rebinding requires verified prior reclaim.
/// </summary>
public sealed class CxlMultiHostGate : ICxlTeardownParticipant
{
    private readonly RegionAuthority regions;
    private sealed class Record(MultiHostMemoryBinding binding, RegionOwner owner, RegionUseHandle use)
    {
        public MultiHostMemoryBinding Binding { get; set; } = binding;
        public RegionOwner Owner { get; } = owner;
        public RegionUseHandle Use { get; } = use;
    }
    private readonly Dictionary<MultiHostBindingId, Record> _bindings = [];
    private ulong _nextId = 1;

    public CxlMultiHostGate(RuntimeKernel kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);
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

        var mode = access == MultiHostAccessMode.Writable ? RegionUseMode.DevicePrivate : RegionUseMode.ReadOnly;
        var use = regions.AcquireUse(region, owner, mode, new(0, descriptor.Value!.ByteLength));
        if (!use.IsSuccess) return KernelResult<MultiHostMemoryBinding>.Fail(use.Error, use.Message!);
        var handle = new MultiHostBindingHandle(new(_nextId++), new(1));
        var binding = new MultiHostMemoryBinding(handle, host, region, access, MultiHostBindingState.Active);
        _bindings.Add(handle.BindingId, new(binding, owner, use.Value!.Handle));
        return KernelResult<MultiHostMemoryBinding>.Ok(binding);
    }

    public KernelResult<MultiHostMemoryBinding> Fence(MultiHostBindingHandle binding)
    {
        var record = Resolve(binding);
        if (!record.IsSuccess) return KernelResult<MultiHostMemoryBinding>.Fail(record.Error, record.Message!);
        if (record.Value!.Binding.State == MultiHostBindingState.Released) return KernelResult<MultiHostMemoryBinding>.Ok(record.Value.Binding);
        record.Value.Binding = record.Value.Binding with { State = MultiHostBindingState.Fenced };
        return KernelResult<MultiHostMemoryBinding>.Ok(record.Value.Binding);
    }

    public KernelResult<MultiHostMemoryBinding> RecordHostDisappearance(MultiHostBindingHandle binding)
    {
        var record = Resolve(binding);
        if (!record.IsSuccess) return KernelResult<MultiHostMemoryBinding>.Fail(record.Error, record.Message!);
        if (record.Value!.Binding.State == MultiHostBindingState.Released)
            return KernelResult<MultiHostMemoryBinding>.Ok(record.Value.Binding);
        record.Value.Binding = record.Value.Binding with { State = MultiHostBindingState.Fenced };
        return KernelResult<MultiHostMemoryBinding>.Ok(record.Value.Binding);
    }

    public KernelResult BeginReclaim(MultiHostBindingHandle binding)
    {
        var record = Resolve(binding);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        if (record.Value!.Binding.State != MultiHostBindingState.Fenced)
            return KernelResult.Fail(KernelError.InvalidTransition, "Host binding must be fenced before reclaim.");
        record.Value.Binding = record.Value.Binding with { State = MultiHostBindingState.Reclaiming };
        return KernelResult.Ok();
    }

    public KernelResult CompleteReclaim(MultiHostBindingHandle binding)
    {
        var record = Resolve(binding);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        if (record.Value!.Binding.State != MultiHostBindingState.Reclaiming)
            return KernelResult.Fail(KernelError.InvalidTransition, "Host binding must enter reclaim before release.");
        var release = regions.ReleaseUse(record.Value.Use, record.Value.Owner);
        if (!release.IsSuccess) return release;
        record.Value.Binding = record.Value.Binding with { State = MultiHostBindingState.Released };
        return KernelResult.Ok();
    }

    public KernelResult<MultiHostMemoryBinding> Query(MultiHostBindingHandle binding)
    {
        var record = Resolve(binding);
        return record.IsSuccess
            ? KernelResult<MultiHostMemoryBinding>.Ok(record.Value!.Binding)
            : KernelResult<MultiHostMemoryBinding>.Fail(record.Error, record.Message!);
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
        foreach (var record in _bindings.Values.Where(item => item.Owner == owner && item.Binding.State != MultiHostBindingState.Released).ToArray())
        {
            if (record.Binding.State == MultiHostBindingState.Active)
            {
                var fenced = Fence(record.Binding.Binding);
                if (!fenced.IsSuccess) return KernelResult.Fail(fenced.Error, fenced.Message!);
            }
            if (record.Binding.State == MultiHostBindingState.Fenced)
            {
                var begun = BeginReclaim(record.Binding.Binding);
                if (!begun.IsSuccess) return begun;
            }
            var completed = CompleteReclaim(record.Binding.Binding);
            if (!completed.IsSuccess) return completed;
        }
        return KernelResult.Ok();
    }
}
