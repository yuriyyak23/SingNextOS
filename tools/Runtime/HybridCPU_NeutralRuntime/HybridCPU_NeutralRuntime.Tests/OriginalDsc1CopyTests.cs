using System.Reflection;
using YAKSys_Hybrid_CPU.Core;
namespace HybridCPU_NeutralRuntime.Tests;
public sealed class OriginalDsc1CopyTests
{
    [Theory]
    [InlineData("exact")] [InlineData("lost-reply")] [InlineData("retired-source")]
    [InlineData("request-copy")] [InlineData("foreign")]
    public void OriginalMappingBackedSoftwareCopyRetainsSameFactAndEndsByteCustody(string fault)
    {
        var runtime=new NeutralDomainRuntimeFacade();
        var rootRequest=new NeutralOriginalDomainBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        var root=runtime.Bind(rootRequest).Observation.Lease;
        NeutralOwnedRegionMappingLease Map(long offset,NeutralMemoryAccess rights)=>runtime.MapOwnedRegion(new NeutralOriginalOwnedRegionMapRequest(
            Guid.NewGuid(),runtime.MappingClosureIncarnation,rootRequest.InvocationId,root,new(offset,32,rights))).Observation.Lease;
        var source=Map(0,NeutralMemoryAccess.Read);var destination=Map(64,NeutralMemoryAccess.Write);
        var bytes=Enumerable.Repeat((byte)0x5A,32).ToArray();var output=new byte[32];
        var request=runtime.PrepareDsc1Copy(source,0,destination,0,bytes,output)!;Assert.NotNull(request);
        if(fault=="retired-source")Assert.Equal(NeutralOwnedRegionCloseDecision.Closed,runtime.CloseOwnedRegionMapping(source).Decision);
        if(fault=="request-copy")
        {var copy=(NeutralOriginalDsc1CopyRequest)typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(request,null)!;Assert.Null(runtime.CopyDsc1(copy));}
        if(fault=="foreign")Assert.Null(new NeutralDomainRuntimeFacade().CopyDsc1(request));
        NeutralOriginalDsc1CopyObservation? raw=null;
        if(fault=="lost-reply")Assert.Throws<IOException>((Action)(()=>{runtime.CopyDsc1(request);throw new IOException("Lost original byte producer reply");}));
        else raw=runtime.CopyDsc1(request);
        raw??=runtime.ResolveDsc1Copy(request);Assert.NotNull(raw);Assert.Same(request,raw.Request);Assert.True(raw.NoFurtherByteUseGuaranteed);
        Assert.Equal(fault!="retired-source",raw.Completed);Assert.Equal(fault=="retired-source",raw.NoEffectGuaranteed);
        Assert.All(output,b=>Assert.Equal(fault=="retired-source"?(byte)0:(byte)0x5A,b));
        output.AsSpan().Fill(0x17);bytes.AsSpan().Fill(0x29);
        Assert.Same(raw,runtime.CopyDsc1(request));Assert.Same(raw,runtime.ResolveDsc1Copy(request));Assert.All(output,b=>Assert.Equal((byte)0x17,b));
        if(fault!="retired-source")Assert.Equal(NeutralOwnedRegionCloseDecision.Closed,runtime.CloseOwnedRegionMapping(source).Decision);
        Assert.Equal(NeutralOwnedRegionCloseDecision.Closed,runtime.CloseOwnedRegionMapping(destination).Decision);
        Assert.Equal(NeutralDomainCloseDecision.Closed,runtime.Close(root).Decision);
        Assert.Same(raw,runtime.CopyDsc1(request));Assert.Equal(0,runtime.ActiveOwnedRegionMappingCount);Assert.Equal(0,runtime.ActiveBindingCount);
    }
}
