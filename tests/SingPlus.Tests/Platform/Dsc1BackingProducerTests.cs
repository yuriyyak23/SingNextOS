using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using YAKSys_Hybrid_CPU.Core;
using System.Reflection;
using System.Collections;
namespace SingPlus.Tests.Platform;
public sealed class Dsc1BackingProducerTests
{
    [Fact]
    public void ActualProviderProducesBytesOnceFromOriginalMappingFactories()
    {
        NeutralDomainRuntimeFacade native =new NeutralDomainRuntimeFacade(); HybridCpuPlatformAuthorityProvider provider =new HybridCpuPlatformAuthorityProvider(native);
        PlatformDomainIdentity subject =new PlatformDomainIdentity(new(2490),new(new(2490),1));
        PlatformProviderDomainLease root =provider.BindDomain(new PlatformDomainBindRequest(Guid.NewGuid(),subject)).Value;
        PlatformProviderRegionMappingLease Map(ulong id,PlatformMemoryAccess rights)
        {
            PlatformRegionIdentity region =new PlatformRegionIdentity(new(new(id),new(1)),new(subject.DomainId,subject.ProcessGeneration),32);
            PlatformAuthorityResult<PlatformProviderOwnedRegionMapping> mapped =provider.MapOwnedRegionSlice(new PlatformOwnedRegionMapRequest(Guid.NewGuid(),root,new(region,0,32,rights)));
            Assert.True(mapped.IsSuccess,mapped.Message);return mapped.Value.Lease;
        }
        PlatformProviderRegionMappingLease input =Map(1,PlatformMemoryAccess.Read); PlatformProviderRegionMappingLease output =Map(2,PlatformMemoryAccess.Write);
        byte[] bytes=Enumerable.Repeat((byte)0x5A,32).ToArray(),destination=new byte[32];
        PlatformOriginalDsc1CopyBackingRequest request =new PlatformOriginalDsc1CopyBackingRequest(new(root,new(input,0,32),new(output,0,32),PlatformDsc1CopyProfile.UInt8AllOrNone),bytes,destination);
        PlatformAuthorityResult<PlatformOriginalDsc1CopyBackingObservation> moved =provider.CopyDsc1Backing(request);Assert.True(moved.IsSuccess,moved.Message);
        Assert.True(moved.Value!.Completed);Assert.True(moved.Value.NoFurtherByteUseGuaranteed);Assert.Same(request,moved.Value.Request);
        Assert.All(destination,b=>Assert.Equal((byte)0x5A,b));destination.AsSpan().Fill(0x17);
        Assert.Same(moved.Value,provider.CopyDsc1Backing(request).Value);Assert.Same(moved.Value,provider.ResolveDsc1Backing(request).Value);
        Assert.All(destination,b=>Assert.Equal((byte)0x17,b));
        // Malformed dependent history must deny through the public provider API,
        // including the cached result path, rather than throw or repeat bytes.
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        IDictionary routes =(global::System.Collections.IDictionary)provider.GetType().GetField("_providerMappings",flags)!.GetValue(provider)!;
        object sourceRecord =routes[input.MappingId]!;
        IList history =(global::System.Collections.IList)sourceRecord.GetType().GetProperty("Dsc1Copies")!.GetValue(sourceRecord)!;
        PropertyInfo issued =sourceRecord.GetType().GetProperty("Dsc1CopiesIssued")!;
        ulong originalCount =(ulong)issued.GetValue(sourceRecord)!;
        history.Add(null); issued.SetValue(sourceRecord,originalCount+1);
        Assert.False(provider.ResolveDsc1Backing(request).IsSuccess);
        Assert.False(provider.CopyDsc1Backing(request).IsSuccess);
        Assert.All(destination,b=>Assert.Equal((byte)0x17,b));
        history.RemoveAt(history.Count-1); issued.SetValue(sourceRecord,originalCount);
        Assert.Same(moved.Value,provider.ResolveDsc1Backing(request).Value);
        object destinationRecord =routes[output.MappingId]!;
        IList destinationHistory =(global::System.Collections.IList)destinationRecord.GetType().GetProperty("Dsc1Copies")!.GetValue(destinationRecord)!;
        PropertyInfo destinationIssued =destinationRecord.GetType().GetProperty("Dsc1CopiesIssued")!;
        object? originalFact =destinationHistory[0];
        destinationHistory.Clear(); destinationIssued.SetValue(destinationRecord,0UL);
        Assert.False(provider.ResolveDsc1Backing(request).IsSuccess);
        Assert.False(provider.CopyDsc1Backing(request).IsSuccess);
        Assert.All(destination,b=>Assert.Equal((byte)0x17,b));
        destinationHistory.Add(originalFact); destinationIssued.SetValue(destinationRecord,1UL);
        Assert.Same(moved.Value,provider.ResolveDsc1Backing(request).Value);
    }
}
