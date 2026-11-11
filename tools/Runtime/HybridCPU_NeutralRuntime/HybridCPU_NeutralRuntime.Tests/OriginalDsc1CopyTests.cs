using System.Collections;
using System.Reflection;
using YAKSys_Hybrid_CPU.Core;
namespace HybridCPU_NeutralRuntime.Tests;
public sealed class OriginalDsc1CopyTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CopiedFactoryBeforePreparationCannotAcquireByteCustody(bool rootFactory)
    {
        NeutralDomainRuntimeFacade runtime = new NeutralDomainRuntimeFacade();
        NeutralOriginalDomainBindRequest bind = new NeutralOriginalDomainBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease root = runtime.Bind(bind).Observation.Lease;
        NeutralOwnedRegionMappingLease Map(long offset,NeutralMemoryAccess access) => runtime.MapOwnedRegion(new NeutralOriginalOwnedRegionMapRequest(
            Guid.NewGuid(),runtime.MappingClosureIncarnation,bind.InvocationId,root,new(offset,32,access))).Observation.Lease;
        NeutralOwnedRegionMappingLease source =Map(0,NeutralMemoryAccess.Read); NeutralOwnedRegionMappingLease destination =Map(64,NeutralMemoryAccess.Write);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        object registry =typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(runtime)!;
        IDictionary records =(global::System.Collections.IDictionary)registry.GetType().GetField(rootFactory ? "Domains" : "Mappings",flags)!.GetValue(registry)!;
        object state =records[rootFactory ? (object)root.Handle : source.Handle]!;
        PropertyInfo factory =state.GetType().GetProperty("OriginalAdmission")!;
        object original =factory.GetValue(state)!;
        factory.SetValue(state,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null));
        byte[] input=Enumerable.Repeat((byte)0x49,32).ToArray(), output=new byte[32];
        NeutralOriginalDsc1Preparation preparation =new NeutralOriginalDsc1Preparation();
        Assert.Null(runtime.PrepareDsc1Copy(preparation,source,0,destination,0,input,output));
        Assert.Null(runtime.ResolveDsc1Preparation(preparation,source,destination));
        Assert.All(output,b=>Assert.Equal((byte)0,b));
        factory.SetValue(state,original);
        NeutralOriginalDsc1CopyRequest request =runtime.PrepareDsc1Copy(preparation,source,0,destination,0,input,output)!;
        Assert.NotNull(request); Assert.True(runtime.EndDsc1PreparedCustody(request)!.NoEffectGuaranteed);
        Assert.All(output,b=>Assert.Equal((byte)0,b));
        Assert.Equal(NeutralOwnedRegionCloseDecision.Closed,runtime.CloseOwnedRegionMapping(source).Decision);
        Assert.Equal(NeutralOwnedRegionCloseDecision.Closed,runtime.CloseOwnedRegionMapping(destination).Decision);
        Assert.Equal(NeutralDomainCloseDecision.Closed,runtime.Close(root).Decision);
    }
    [Theory]
    [InlineData("exact")] [InlineData("lost-reply")] [InlineData("retired-source")]
    [InlineData("request-copy")] [InlineData("foreign")]
    public void OriginalMappingBackedSoftwareCopyRetainsSameFactAndEndsByteCustody(string fault)
    {
        NeutralDomainRuntimeFacade runtime =new NeutralDomainRuntimeFacade();
        NeutralOriginalDomainBindRequest rootRequest =new NeutralOriginalDomainBindRequest(Guid.NewGuid(),runtime.MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease root =runtime.Bind(rootRequest).Observation.Lease;
        NeutralOwnedRegionMappingLease Map(long offset,NeutralMemoryAccess rights)=>runtime.MapOwnedRegion(new NeutralOriginalOwnedRegionMapRequest(
            Guid.NewGuid(),runtime.MappingClosureIncarnation,rootRequest.InvocationId,root,new(offset,32,rights))).Observation.Lease;
        NeutralOwnedRegionMappingLease source =Map(0,NeutralMemoryAccess.Read); NeutralOwnedRegionMappingLease destination =Map(64,NeutralMemoryAccess.Write);
        byte[] bytes =Enumerable.Repeat((byte)0x5A,32).ToArray(); byte[] output =new byte[32];
        NeutralOriginalDsc1Preparation preparation = new NeutralOriginalDsc1Preparation();
        NeutralOriginalDsc1CopyRequest request =runtime.PrepareDsc1Copy(preparation,source,0,destination,0,bytes,output)!;Assert.NotNull(request);
        Assert.Same(request, runtime.ResolveDsc1Preparation(preparation, source, destination));
        Assert.Null(runtime.PrepareDsc1Copy(preparation,source,0,destination,0,bytes,output));
        NeutralOriginalDsc1Preparation copiedPreparation =(NeutralOriginalDsc1Preparation)typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(preparation,null)!;
        Assert.Null(runtime.ResolveDsc1Preparation(copiedPreparation,source,destination));
        Assert.Null(runtime.PrepareDsc1Copy(copiedPreparation,source,0,destination,0,bytes,output));
        Assert.Null(new NeutralDomainRuntimeFacade().ResolveDsc1Preparation(preparation,source,destination));
        if(fault=="retired-source")
        {
            Assert.Equal(NeutralOwnedRegionCloseDecision.Closed,runtime.CloseOwnedRegionMapping(source).Decision);
            NeutralOriginalDsc1CopyObservation? closed = runtime.ResolveDsc1Copy(request);
            Assert.NotNull(closed);Assert.True(closed.NoEffectGuaranteed);Assert.True(closed.NoFurtherByteUseGuaranteed);
            Assert.Equal(0, ((ReadOnlyMemory<byte>)typeof(NeutralOriginalDsc1CopyRequest).GetProperty("Source",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(request)!).Length);
            Assert.Equal(0, ((Memory<byte>)typeof(NeutralOriginalDsc1CopyRequest).GetProperty("Destination",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(request)!).Length);
        }
        if(fault=="request-copy")
        { NeutralOriginalDsc1CopyRequest copy =(NeutralOriginalDsc1CopyRequest)typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(request,null)!;Assert.Null(runtime.CopyDsc1(copy));}
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
