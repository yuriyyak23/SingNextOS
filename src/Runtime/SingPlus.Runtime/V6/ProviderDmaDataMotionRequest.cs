using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

/// <summary>Caller-retained original correlation, not admission or cleanup authority.</summary>
public sealed class ProviderDmaDataMotionRequest<T> where T : unmanaged
{
    public ProviderDmaDataMotionRequest(ProcessHandle source,ProcessHandle destination,OwnedBuffer<T> input,OwnedBuffer<T> output,
        DataMotionPlanV1 plan,PlatformOwnedRegionSliceMapping sourceMapping,PlatformDmaGrant sourceGrant,PlatformDmaPrepareEvidence sourcePrepare,
        PlatformOwnedRegionSliceMapping destinationMapping,PlatformDmaGrant destinationGrant,PlatformDmaPrepareEvidence destinationPrepare)
    {
        ArgumentNullException.ThrowIfNull(input);ArgumentNullException.ThrowIfNull(output);
        Original=this; Source=source;Destination=destination;Input=input;Output=output;Plan=plan;
        SourceMapping=sourceMapping;SourceGrant=sourceGrant;SourcePrepare=sourcePrepare;
        DestinationMapping=destinationMapping;DestinationGrant=destinationGrant;DestinationPrepare=destinationPrepare;
    }
    public Guid InvocationId {get;}=Guid.NewGuid();
    internal ProviderDmaDataMotionRequest<T> Original {get;}
    internal ProcessHandle Source {get;}
    internal ProcessHandle Destination {get;}
    internal OwnedBuffer<T> Input {get;}
    internal OwnedBuffer<T> Output {get;}
    internal DataMotionPlanV1 Plan {get;}
    internal PlatformOwnedRegionSliceMapping SourceMapping {get;}
    internal PlatformDmaGrant SourceGrant {get;}
    internal PlatformDmaPrepareEvidence SourcePrepare {get;}
    internal PlatformOwnedRegionSliceMapping DestinationMapping {get;}
    internal PlatformDmaGrant DestinationGrant {get;}
    internal PlatformDmaPrepareEvidence DestinationPrepare {get;}
    internal RuntimeKernel? Owner;
    internal bool Entered;
    internal bool InFlight, CleanupConsumed;
    internal object? CleanupPayload;
    internal ProviderDmaDataMotionResult<T>? Result;
}

public sealed partial class RuntimeKernel
{
    public KernelResult<ProviderDmaDataMotionResult<T>> MoveProviderDmaBuffers<T>(ProviderDmaDataMotionRequest<T> request,
        IV6LocalityCostEvidenceProvider costProvider) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(request);ArgumentNullException.ThrowIfNull(costProvider);
        lock(request)
        {
            if(!ReferenceEquals(request.Original,request) || request.Owner is { } owner && !ReferenceEquals(owner,this))
                return KernelResult<ProviderDmaDataMotionResult<T>>.Fail(KernelError.PlatformDenied,"Foreign DMA continuation.");
            if(request.Result is { } result)return KernelResult<ProviderDmaDataMotionResult<T>>.Ok(result);
            if(request.Entered)return KernelResult<ProviderDmaDataMotionResult<T>>.Fail(KernelError.PlatformFaulted,"Unknown original DMA invocation is not repeated.");
            request.Owner=this;request.Entered=true;request.InFlight=true;
        }
        // Existing executing path performs all admission and original closure.
        // No provider callbacks under the caller correlation monitor.
        try
        {
        var moved=MoveProviderDmaBuffersCore(request.Source,request.Destination,request.Input,request.Output,request.Plan,
            request.SourceMapping,request.SourceGrant,request.SourcePrepare,request.DestinationMapping,request.DestinationGrant,request.DestinationPrepare,costProvider,request);
        if(moved.IsSuccess)lock(request)request.Result=moved.Value;
        return moved;
        }
        finally { lock(request) request.InFlight=false; }
    }

    public KernelResult CleanupProviderDmaDataMotion<T>(ProviderDmaDataMotionRequest<T> request) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(request);
        lock(request)
        {
            if(!ReferenceEquals(request.Original,request)||!ReferenceEquals(request.Owner,this)||!request.Entered)
                return KernelResult.Fail(KernelError.PlatformDenied,"Foreign or unentered DMA cleanup correlation.");
            if(request.InFlight)return KernelResult.Fail(KernelError.PlatformBindingDraining,"Original DMA caller is in flight.");
            if(request.CleanupConsumed)return KernelResult.Ok();
            request.InFlight=true;
        }
        try
        {
            lock(_platformMemoryUseGate)
            {
                _dmaCopyPayloads.TryGetValue(request.SourceGrant.GrantId,out var routed);
                var payload=request.CleanupPayload as DmaCopyPayload ?? routed;
                if(payload is null || routed is not null && !ReferenceEquals(routed,payload) ||
                    !ReferenceEquals(payload.Original,payload)||!payload.Matches(request.Input,request.Output)||
                    payload.Source!=request.Source||payload.Destination!=request.Destination||
                    payload.SourceGrant!=request.SourceGrant||payload.DestinationGrant!=request.DestinationGrant||
                    payload.SourcePrepare!=request.SourcePrepare||payload.DestinationPrepare!=request.DestinationPrepare)
                    return KernelResult.Fail(KernelError.PlatformFaulted,"Original DMA payload custody unavailable; absence is not closure.");
                request.CleanupPayload=payload; // retain original before partial retirement may remove routing
                // Surviving caller custody can repair only the same original route.
                // This is warm cleanup, never permission to submit again.
                if(routed is null && !payload.Consumed)
                {
                    _dmaCopyPayloads.EnsureCapacity(_dmaCopyPayloads.Count+1);
                    _dmaCopyPayloads.Add(request.SourceGrant.GrantId,payload);
                }
            }
            var source=RetirePlatformDmaGrantAndOriginalPeer(request.SourceGrant,request.SourceGrant.DeviceLease.DomainBinding.Subject);
            if(!source.IsSuccess)return source;
            var destination=RetirePlatformDmaGrantAndOriginalPeer(request.DestinationGrant,request.DestinationGrant.DeviceLease.DomainBinding.Subject);
            if(!destination.IsSuccess)return destination;
            lock(_platformMemoryUseGate)
                if(!PlatformAuthority.IsOriginalDmaGrantClosureConsumed(request.SourceGrant,request.SourceGrant.DeviceLease.DomainBinding.Subject)||
                    !PlatformAuthority.IsOriginalDmaGrantClosureConsumed(request.DestinationGrant,request.DestinationGrant.DeviceLease.DomainBinding.Subject)||
                    _dmaCopyPayloads.ContainsKey(request.SourceGrant.GrantId))
                    return KernelResult.Fail(KernelError.PlatformBindingDraining,"Original DMA byte custody remains retained.");
            lock(request) request.CleanupConsumed=true;
            return KernelResult.Ok();
        }
        finally {lock(request)request.InFlight=false;}
    }

    public KernelResult<ProviderDmaDataMotionResult<T>> ReadProviderDmaDataMotionResult<T>(ProviderDmaDataMotionRequest<T> request) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(request);
        lock(request)
            return ReferenceEquals(request.Original,request)&&ReferenceEquals(request.Owner,this)&&request.Result is { } result
                ? KernelResult<ProviderDmaDataMotionResult<T>>.Ok(result)
                : KernelResult<ProviderDmaDataMotionResult<T>>.Fail(KernelError.ResponseNotAvailable,"Original DMA result unavailable; no effect or closure inferred.");
    }
}
