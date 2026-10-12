using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

/// <summary>Published buffer and advisory receipt; neither grants new authority.</summary>
public sealed record ProviderDmaDataMotionResult<T>(OwnedBuffer<T> Buffer, DataMotionReceiptV1 Receipt) where T : unmanaged;

public sealed partial class RuntimeKernel
{
    public KernelResult<ProviderDmaDataMotionResult<T>> MoveProviderDmaBuffers<T>(
        ProcessHandle source, ProcessHandle destination, OwnedBuffer<T> input, OwnedBuffer<T> output,
        DataMotionPlanV1 plan, PlatformOwnedRegionSliceMapping sourceMapping, PlatformDmaGrant sourceGrant,
        PlatformDmaPrepareEvidence sourcePrepare, PlatformOwnedRegionSliceMapping destinationMapping,
        PlatformDmaGrant destinationGrant, PlatformDmaPrepareEvidence destinationPrepare,
        IV6LocalityCostEvidenceProvider costProvider) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(costProvider);
        var moved=ExecuteV6ProviderDmaDataMotion(source,destination,input,output,plan,
            sourceMapping,sourceGrant,sourcePrepare,destinationMapping,destinationGrant,destinationPrepare,
            ()=>FreshGeneration(plan.SourceProviderClass),()=>FreshGeneration(plan.DestinationProviderClass));
        return moved.IsSuccess
            ? KernelResult<ProviderDmaDataMotionResult<T>>.Ok(new(moved.Value!.Moved,moved.Value.Receipt))
            : KernelResult<ProviderDmaDataMotionResult<T>>.Fail(moved.Error,moved.Message!);

        ulong FreshGeneration(string providerClass)
        {
            try
            {
                var observed=costProvider.QueryFresh(providerClass,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if(!observed.IsSuccess)return 0;
                var estimate=observed.Value.Validate();
                return estimate.ProviderClass==providerClass && estimate.DataMotionBytes==plan.ByteLength &&
                    estimate.IsFreshAt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) ? estimate.EstimateGeneration : 0;
            }
            catch(Exception exception) when(exception is not StackOverflowException and not OutOfMemoryException){return 0;}
        }
    }
}
