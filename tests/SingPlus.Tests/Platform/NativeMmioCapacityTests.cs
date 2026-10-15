using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Tests.Platform;
public sealed class NativeMmioCapacityTests
{
    public interface ILossRuntime : INeutralDomainRuntime,INeutralOriginalMmioProvider { }
    public class LossSpy : DispatchProxy
    {
        public NeutralDomainRuntimeFacade Inner=new();public bool Lose;public int Binds;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(Lose && method!.Name=="ResolveMmioMap")throw new IOException("MMIO query lost");
            var raw=method!.Invoke(Inner,args);
            if(method.Name=="MapMmio"){Binds++;if(Lose)throw new IOException("MMIO reply lost");}
            return raw;
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualCallerCapacityDispositionPreservesIndependentMmioAndReclaimsExactRegion(bool lost)
    {
        var proxy=DispatchProxy.Create<ILossRuntime,LossSpy>();var spy=(LossSpy)(object)proxy;
        var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy));
        var (_,owner)=TestFixtures.Create(kernel,2981,29810);var service=TestFixtures.Create(kernel,2982,29820).Handle;
        var buffer=kernel.AllocateBuffer<byte>(owner,64).Value!;var foreign=kernel.AllocateBuffer<byte>(service,32).Value!;
        var root=kernel.BindPlatformDomain(owner).Value;
        var deviceCap=kernel.MintCapability(new(29810),owner,ResourceKind.Device,"device/mmio",CapabilityRights.Read|CapabilityRights.Configure).Value!.CapabilityId;
        var device=kernel.BindPlatformDevice(owner,root,deviceCap,PlatformDeviceRights.Read|PlatformDeviceRights.Configure).Value;
        CapabilityId Cap(string name)=>kernel.MintCapability(new(29810),owner,ResourceKind.MmioRegion,CapabilityResourceIds.MmioRegion("device/mmio",name,128),CapabilityRights.Read|CapabilityRights.Map).Value!.CapabilityId;
        var peerMap=kernel.BindPlatformMmio(owner,device,Cap("peer"),0,64,PlatformMmioAccess.Read);Assert.True(peerMap.IsSuccess,peerMap.Message);
        typeof(NeutralDomainRuntimeFacade).GetField("_nextResource",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(spy.Inner,ulong.MaxValue);
        spy.Lose=lost;
        Assert.False(kernel.BindPlatformMmio(owner,device,Cap("refusal"),0,64,PlatformMmioAccess.Read).IsSuccess);
        Assert.Equal(2,spy.Binds);Assert.Equal(1,spy.Inner.ActiveMmioLeaseCount);
        if(lost)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(1,spy.Inner.ActiveMmioLeaseCount);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
            spy.Lose=false;
        }
        var closed=kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(2,spy.Binds);Assert.Equal(0,spy.Inner.ActiveMmioLeaseCount);Assert.Equal(0,spy.Inner.ActiveBindingCount);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==foreign.Handle).State);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void NativeNoEffectRequiresOriginalHistoryRootAndKnownDecision(int fault)
    {
        var native=new NeutralDomainRuntimeFacade();
        var rootRequest=new NeutralOriginalDomainBindRequest(Guid.NewGuid(),((INeutralOriginalMappingClosureProvider)native).MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        var root=native.Bind(rootRequest).Observation.Lease;
        var deviceRequest=new NeutralOriginalDeviceBindRequest(Guid.NewGuid(),rootRequest.RuntimeIncarnation,rootRequest.InvocationId,root,new("device/mmio"),NeutralDeviceRights.Read);
        var device=native.BindDevice(deviceRequest).Observation.Lease;
        var peer=new NeutralOriginalMmioMapRequest(Guid.NewGuid(),rootRequest.RuntimeIncarnation,deviceRequest.InvocationId,device,new("peer",128),new(0,64),NeutralMmioAccess.Read);
        var peerMap=native.MapMmio(peer);Assert.True(peerMap.Observation.IsMapped);
        var peerLease=peerMap.Observation.Lease;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(NeutralDomainRuntimeFacade).GetField("_nextResource",flags)!.SetValue(native,ulong.MaxValue);
        var request=peer with {InvocationId=Guid.NewGuid(),Region=new("refusal",128)};
        var raw=native.MapMmio(request);Assert.True(raw.NoEffectGuaranteed);Assert.Equal(raw,native.ResolveMmioMap(request));
        Assert.Null(native.ResolveMmioMap(request with {DeviceBindInvocationId=Guid.NewGuid()}));
        var dependencies=typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(native)!;
        var domains=(IDictionary)dependencies.GetType().GetField("Devices",flags)!.GetValue(dependencies)!;
        var parent=Assert.Single(domains.Values.Cast<object>());
        var facts=(IList)parent.GetType().GetProperty("OriginalMmioAdmissions")!.GetValue(parent)!;var original=facts[1]!;
        if(fault==0)facts[1]=typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null);
        else if(fault==1)facts.RemoveAt(1);
        else if(fault==2)original.GetType().GetProperty("Observation")!.SetValue(original,raw with {Observation=new(){Decision=(NeutralMmioMapDecision)999}});
        else if(fault==4)original.GetType().GetProperty("Observation")!.SetValue(original,raw with {Observation=new(){Decision=NeutralMmioMapDecision.Mapped}});
        else if(fault==5)original.GetType().GetProperty("NoEffect")!.SetValue(original,false);
        else
        {
            var property=parent.GetType().GetProperty("OriginalAdmission")!;var rootFact=property.GetValue(parent)!;
            property.SetValue(parent,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(rootFact,null));
        }
        Assert.Null(native.ResolveMmioMap(request));Assert.False(native.MapMmio(request).NoEffectGuaranteed);
        Assert.Equal(1,native.ActiveMmioLeaseCount);Assert.Equal(1,native.ActiveBindingCount);
        if(fault != 3)
        {
            // Retire the independent mapping first: malformed no-effect alone must still pin its parent.
            var close=native.CloseMmio(new NeutralOriginalMmioCloseRequest(Guid.NewGuid(),rootRequest.RuntimeIncarnation,peer.InvocationId,peerLease));
            Assert.Equal(NeutralMmioCloseDecision.Closed,close.Observation.Decision);
            Assert.NotEqual(NeutralDeviceCloseDecision.Closed,native.CloseDevice(new NeutralOriginalDeviceCloseRequest(Guid.NewGuid(),rootRequest.RuntimeIncarnation,deviceRequest.InvocationId,device)).Observation.Decision);
            Assert.Equal(0,native.ActiveMmioLeaseCount);Assert.Equal(1,native.ActiveDeviceLeaseCount);
        }
    }
}
