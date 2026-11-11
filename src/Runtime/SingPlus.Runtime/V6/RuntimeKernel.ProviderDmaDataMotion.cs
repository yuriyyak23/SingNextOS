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
        return MoveProviderDmaBuffersCore(source,destination,input,output,plan,sourceMapping,sourceGrant,sourcePrepare,
            destinationMapping,destinationGrant,destinationPrepare,costProvider,null);
    }

    private KernelResult<ProviderDmaDataMotionResult<T>> MoveProviderDmaBuffersCore<T>(
        ProcessHandle source, ProcessHandle destination, OwnedBuffer<T> input, OwnedBuffer<T> output,
        DataMotionPlanV1 plan, PlatformOwnedRegionSliceMapping sourceMapping, PlatformDmaGrant sourceGrant,
        PlatformDmaPrepareEvidence sourcePrepare, PlatformOwnedRegionSliceMapping destinationMapping,
        PlatformDmaGrant destinationGrant, PlatformDmaPrepareEvidence destinationPrepare,
        IV6LocalityCostEvidenceProvider costProvider, ProviderDmaDataMotionRequest<T>? originalRequest) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(costProvider);
        KernelResult<V6DataMotionExecution<T>> moved =ExecuteV6ProviderDmaDataMotion(source,destination,input,output,plan,
            sourceMapping,sourceGrant,sourcePrepare,destinationMapping,destinationGrant,destinationPrepare,
            ()=>FreshGeneration(plan.SourceProviderClass),()=>FreshGeneration(plan.DestinationProviderClass),
            originalRequest:originalRequest);
        if(!moved.IsSuccess)return KernelResult<ProviderDmaDataMotionResult<T>>.Fail(moved.Error,moved.Message!);
        return moved.Value!.PreparedProviderResult is { } result
            ? KernelResult<ProviderDmaDataMotionResult<T>>.Ok(result)
            : KernelResult<ProviderDmaDataMotionResult<T>>.Fail(KernelError.PlatformFaulted,"Original DMA result was not prepared before source consumption.");

        ulong FreshGeneration(string providerClass)
        {
            try
            {
                KernelResult<LocalityCostEstimateV1> observed =costProvider.QueryFresh(providerClass,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if(!observed.IsSuccess)return 0;
                LocalityCostEstimateV1 estimate =observed.Value.Validate();
                return estimate.ProviderClass==providerClass && estimate.DataMotionBytes==plan.ByteLength &&
                    estimate.IsFreshAt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) ? estimate.EstimateGeneration : 0;
            }
            catch(Exception exception) when(exception is not StackOverflowException and not OutOfMemoryException){return 0;}
        }
    }
}
