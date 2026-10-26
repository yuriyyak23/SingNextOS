using SingPlus.Contracts;
namespace SingPlus.Runtime;

public sealed partial class RegionAuthority
{
    internal KernelResult<RegionUseDescriptor> AcquireMmioUse(MmioBufferPayload payload)
    {
        if(!_regions.TryGetValue(payload.Buffer.Handle.RegionId,out RegionRecord? region))
            return KernelResult<RegionUseDescriptor>.Fail(KernelError.RegionNotFound,"MMIO backing Region absent.");
        lock(region.Gate)
        {
            KernelResult<RegionDescriptor> valid =ValidateCore(region,payload.Buffer.Handle,payload.Owner,RegionState.Owned);
            if(!valid.IsSuccess)return KernelResult<RegionUseDescriptor>.Fail(valid.Error,valid.Message!);
            if(!ReferenceEquals(region.Payload,payload.Buffer))
                return KernelResult<RegionUseDescriptor>.Fail(KernelError.StaleGeneration,"MMIO backing must be the original Region payload.");
            if(region.MutationEpoch.Value>ulong.MaxValue-3)
                return KernelResult<RegionUseDescriptor>.Fail(KernelError.CapacityExhausted,"MMIO acquisition/invalidation/release mutation space exhausted before effect.");
            // Existing mapped-region acquisition guard remains mandatory.
            KernelResult<RegionUseDescriptor> acquired = AcquireUseCore(region,payload.Owner,RegionUseMode.ExclusiveWrite,new(0,region.ByteLength),releaseOwnerMmio:payload);
            if (acquired.IsSuccess) payload.UseAuthority = this;
            return acquired;
        }
    }
    internal KernelResult ReleaseMmioUse(MmioBufferPayload payload)
    {
        if (!ReferenceEquals(payload.Original, payload) || !ReferenceEquals(payload.UseAuthority, this))
            return KernelResult.Fail(KernelError.StaleGeneration,"Foreign or copied MMIO use owner cannot release this authority's original use.");
        if(payload.Use is not { } handle)return KernelResult.Ok();
        KernelResult<(RegionRecord Region, RegionUseRecord Use)> found =FindUse(handle);if(!found.IsSuccess)return KernelResult.Fail(found.Error,found.Message!);
        (RegionRecord? region, RegionUseRecord? use) = found.Value!;
        lock(region.Gate)
        {
            if(!ReferenceEquals(use.ReleaseOwnerMmio,payload) || use.Principal!=payload.Owner || use.Region!=payload.Region)
                return KernelResult.Fail(KernelError.StaleGeneration,"Original MMIO backing use changed.");
            if(use.State==RegionUseState.Released)return KernelResult.Ok();
            KernelResult mutation =AdvanceMutation(region,invalidateActiveUses:false);if(!mutation.IsSuccess)return mutation;
            use.State=RegionUseState.Released;return KernelResult.Ok();
        }
    }

    internal KernelResult ReleaseUnadmittedMmioUse(MmioBufferPayload payload)
    {
        if (!ReferenceEquals(payload.Original, payload) || !ReferenceEquals(payload.UseAuthority, this) ||
            !payload.LocalRollbackPending || payload.AdmissionStarted || payload.AdmissionOwner is not null || payload.Use is not { } handle)
            return KernelResult.Fail(KernelError.StaleGeneration,"Original MMIO local rollback custody changed.");
        KernelResult<(RegionRecord Region, RegionUseRecord Use)> found = FindUse(handle);
        if (!found.IsSuccess) return KernelResult.Fail(found.Error, found.Message!);
        (RegionRecord? region, RegionUseRecord? use) = found.Value!;
        lock (region.Gate)
        {
            if (!ReferenceEquals(use.ReleaseOwnerMmio, payload) || use.Principal != payload.Owner || use.Region != payload.Region)
                return KernelResult.Fail(KernelError.StaleGeneration,"Original MMIO rollback use changed.");
            // No provider owner was admitted. Close the independent original
            // storage reservation before allowing Region/charge reclaim.
            try { payload.ReleaseBytes(); }
            catch (InvalidOperationException exception)
            {
                return KernelResult.Fail(KernelError.PlatformFaulted,
                    $"Unadmitted MMIO bytes/use retained for exact cleanup retry: {exception.Message}");
            }
            KernelResult released = ReleaseMmioUse(payload);
            if (released.IsSuccess) payload.LocalRollbackPending = false;
            return released;
        }
    }

    internal KernelResult ConsumeUnadmittedMmioBacking(RegionOwner owner, RegionHandle? target = null)
    {
        foreach (RegionRecord region in OrderedRecords())
        {
            if (target is { } handle && region.Id != handle.RegionId) continue;
            MmioBufferPayload[] payloads;
            lock (region.Gate)
                payloads = region.Uses.Values.Where(use => use.Principal == owner &&
                    use.ReleaseOwnerMmio is { LocalRollbackPending: true })
                    .Select(use => use.ReleaseOwnerMmio!).ToArray();
            foreach (MmioBufferPayload payload in payloads)
            {
                KernelResult released = ReleaseUnadmittedMmioUse(payload);
                if (!released.IsSuccess) return released;
            }
        }
        return KernelResult.Ok();
    }
}
