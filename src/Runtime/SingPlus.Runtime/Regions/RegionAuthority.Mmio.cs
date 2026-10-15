using SingPlus.Contracts;
namespace SingPlus.Runtime;

public sealed partial class RegionAuthority
{
    internal KernelResult<RegionUseDescriptor> AcquireMmioUse(MmioBufferPayload payload)
    {
        if(!_regions.TryGetValue(payload.Buffer.Handle.RegionId,out var region))
            return KernelResult<RegionUseDescriptor>.Fail(KernelError.RegionNotFound,"MMIO backing Region absent.");
        lock(region.Gate)
        {
            var valid=ValidateCore(region,payload.Buffer.Handle,payload.Owner,RegionState.Owned);
            if(!valid.IsSuccess)return KernelResult<RegionUseDescriptor>.Fail(valid.Error,valid.Message!);
            if(!ReferenceEquals(region.Payload,payload.Buffer))
                return KernelResult<RegionUseDescriptor>.Fail(KernelError.StaleGeneration,"MMIO backing must be the original Region payload.");
            if(region.MutationEpoch.Value>ulong.MaxValue-3)
                return KernelResult<RegionUseDescriptor>.Fail(KernelError.CapacityExhausted,"MMIO acquisition/invalidation/release mutation space exhausted before effect.");
            // Existing mapped-region acquisition guard remains mandatory.
            return AcquireUseCore(region,payload.Owner,RegionUseMode.ExclusiveWrite,new(0,region.ByteLength),releaseOwnerMmio:payload);
        }
    }
    internal KernelResult ReleaseMmioUse(MmioBufferPayload payload)
    {
        if(payload.Use is not { } handle)return KernelResult.Ok();
        var found=FindUse(handle);if(!found.IsSuccess)return KernelResult.Fail(found.Error,found.Message!);
        var (region,use)=found.Value!;
        lock(region.Gate)
        {
            if(!ReferenceEquals(use.ReleaseOwnerMmio,payload) || use.Principal!=payload.Owner || use.Region!=payload.Region)
                return KernelResult.Fail(KernelError.StaleGeneration,"Original MMIO backing use changed.");
            if(use.State==RegionUseState.Released)return KernelResult.Ok();
            var mutation=AdvanceMutation(region,invalidateActiveUses:false);if(!mutation.IsSuccess)return mutation;
            use.State=RegionUseState.Released;return KernelResult.Ok();
        }
    }
}
