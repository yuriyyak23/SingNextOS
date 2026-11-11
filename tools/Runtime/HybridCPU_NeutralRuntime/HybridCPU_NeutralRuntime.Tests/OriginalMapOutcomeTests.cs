using System.Collections;
using System.Reflection;
using YAKSys_Hybrid_CPU.Core;

namespace HybridCPU_NeutralRuntime.Tests;
public sealed class OriginalMapOutcomeTests
{
    [Theory]
    [InlineData("exact")] [InlineData("copy")] [InlineData("loss")]
    public void SameOriginalMapLookupNeverMapsAndRejectsForeignHistory(string fault)
    {
        NeutralDomainRuntimeFacade runtime =new NeutralDomainRuntimeFacade();
        NeutralOriginalDomainBindRequest bind =new NeutralOriginalDomainBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease root =runtime.Bind(bind).Observation.Lease;
        NeutralOriginalOwnedRegionMapRequest request =new NeutralOriginalOwnedRegionMapRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,bind.InvocationId,root,new(0,128,NeutralMemoryAccess.Read));
        Assert.Null(runtime.ResolveOriginalOwnedRegionMap(request));
        NeutralOriginalOwnedRegionMapObservation raw =runtime.MapOwnedRegion(request);Assert.True(raw.Observation.IsMapped);
        Assert.Equal(raw,runtime.ResolveOriginalOwnedRegionMap(request));
        Assert.False(runtime.MapOwnedRegion(request).Observation.IsMapped);
        Assert.Equal(1,runtime.ActiveOwnedRegionMappingCount);
        Assert.Null(runtime.ResolveOriginalOwnedRegionMap(request with{InvocationId=Guid.NewGuid()}));
        Assert.Null(new NeutralDomainRuntimeFacade().ResolveOriginalOwnedRegionMap(request));
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        object registry =runtime.GetType().GetField("_dependencies",flags)!.GetValue(runtime)!;
        IDictionary domains =(IDictionary)registry.GetType().GetField("Domains",flags)!.GetValue(registry)!;
        object parent =domains[root.Handle]!;
        IList history =(IList)parent.GetType().GetProperty("OriginalMaps")!.GetValue(parent)!;
        if(fault=="copy")history[0]=typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(history[0],null);
        if(fault=="loss")history.Clear();
        Assert.Equal(fault=="exact",runtime.ResolveOriginalOwnedRegionMap(request) is not null);
        Assert.False(runtime.MapOwnedRegion(request).Observation.IsMapped);Assert.Equal(1,runtime.ActiveOwnedRegionMappingCount);
    }
}
