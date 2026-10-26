using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Tests.Platform;
public sealed class NativeDeviceCapacityTests
{
    public interface ILossRuntime : INeutralDomainRuntime,INeutralOriginalDeviceProvider { }
    public class LossSpy : DispatchProxy
    {
        public NeutralDomainRuntimeFacade Inner=new();public bool Lose;public int Binds;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(Lose && method!.Name=="ResolveDeviceBind")throw new IOException("Device query lost");
            object? raw =method!.Invoke(Inner,args);
            if(method.Name=="BindDevice"){Binds++;if(Lose)throw new IOException("Device reply lost");}
            return raw;
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualCallerCapacityDispositionPreservesIndependentDeviceAndReclaimsExactRegion(bool lost)
    {
        ILossRuntime proxy =DispatchProxy.Create<ILossRuntime,LossSpy>(); LossSpy spy =(LossSpy)(object)proxy;
        RuntimeKernel kernel =new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy));
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel,2981,29810); ProcessHandle service =TestFixtures.Create(kernel,2982,29820).Handle;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(owner,64).Value!; OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(service,32).Value!;
        PlatformDomainBinding root =kernel.BindPlatformDomain(owner).Value;
        CapabilityId Cap(string name)=>kernel.MintCapability(new(29810),owner,ResourceKind.Device,name,CapabilityRights.Read|CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.BindPlatformDevice(owner,root,Cap("device/independent"),PlatformDeviceRights.Read).IsSuccess);
        typeof(NeutralDomainRuntimeFacade).GetField("_nextResource",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(spy.Inner,ulong.MaxValue);
        spy.Lose=lost;
        Assert.False(kernel.BindPlatformDevice(owner,root,Cap("device/refusal"),PlatformDeviceRights.Read).IsSuccess);
        Assert.Equal(2,spy.Binds);Assert.Equal(1,spy.Inner.ActiveDeviceLeaseCount);
        if(lost)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);Assert.Equal(1,spy.Inner.ActiveDeviceLeaseCount);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
            spy.Lose=false;
        }
        KernelResult closed =kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(2,spy.Binds);Assert.Equal(0,spy.Inner.ActiveDeviceLeaseCount);Assert.Equal(0,spy.Inner.ActiveBindingCount);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==foreign.Handle).State);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void NativeNoEffectRequiresOriginalHistoryRootAndKnownDecision(int fault)
    {
        NeutralDomainRuntimeFacade native =new NeutralDomainRuntimeFacade();
        NeutralOriginalDomainBindRequest rootRequest =new NeutralOriginalDomainBindRequest(Guid.NewGuid(),((INeutralOriginalMappingClosureProvider)native).MappingClosureIncarnation,NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease root =native.Bind(rootRequest).Observation.Lease;
        NeutralOriginalDeviceBindRequest peer =new NeutralOriginalDeviceBindRequest(Guid.NewGuid(),rootRequest.RuntimeIncarnation,rootRequest.InvocationId,root,new("device/peer"),NeutralDeviceRights.Read);
        Assert.True(native.BindDevice(peer).Observation.IsBound);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(NeutralDomainRuntimeFacade).GetField("_nextResource",flags)!.SetValue(native,ulong.MaxValue);
        NeutralOriginalDeviceBindRequest request =peer with {InvocationId=Guid.NewGuid(),Device=new("device/refusal")};
        NeutralOriginalDeviceBindObservation raw =native.BindDevice(request);Assert.True(raw.NoEffectGuaranteed);Assert.Equal(raw,native.ResolveDeviceBind(request));
        Assert.Null(native.ResolveDeviceBind(request with {DomainBindInvocationId=Guid.NewGuid()}));
        object dependencies =typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(native)!;
        IDictionary domains =(IDictionary)dependencies.GetType().GetField("Domains",flags)!.GetValue(dependencies)!;
        object parent =Assert.Single(domains.Values.Cast<object>());
        IList facts =(IList)parent.GetType().GetProperty("OriginalDeviceAdmissions")!.GetValue(parent)!; object original =facts[1]!;
        if(fault==0)facts[1]=typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null);
        else if(fault==1)facts.RemoveAt(1);
        else if(fault==2)original.GetType().GetProperty("Observation")!.SetValue(original,raw with {Observation=new(){Decision=(NeutralDeviceBindDecision)999}});
        else
        {
            PropertyInfo property =parent.GetType().GetProperty("OriginalAdmission")!; object rootFact =property.GetValue(parent)!;
            property.SetValue(parent,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(rootFact,null));
        }
        Assert.Null(native.ResolveDeviceBind(request));Assert.False(native.BindDevice(request).NoEffectGuaranteed);
        Assert.Equal(1,native.ActiveDeviceLeaseCount);Assert.Equal(1,native.ActiveBindingCount);
    }
}
