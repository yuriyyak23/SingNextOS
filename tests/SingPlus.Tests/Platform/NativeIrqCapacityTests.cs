using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Tests.Platform;
public sealed class NativeIrqCapacityTests
{
    public interface ILossRuntime : INeutralDomainRuntime,INeutralOriginalIrqProvider { }
    public class LossSpy : DispatchProxy
    {
        public NeutralDomainRuntimeFacade Inner=new();public bool Lose;public int Binds;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(Lose && method!.Name=="ResolveIrqBind")throw new IOException("IRQ query lost");
            object? raw =method!.Invoke(Inner,args);
            if(method.Name=="BindInterrupt"){Binds++;if(Lose)throw new IOException("IRQ reply lost");}
            return raw;
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OriginalCapacityNoEffectReleasesEndpointAdmissionAndWaiterWithoutRepeat(bool lost)
    {
        ILossRuntime proxy =DispatchProxy.Create<ILossRuntime,LossSpy>(); LossSpy spy =(LossSpy)(object)proxy;
        RuntimeKernel kernel =new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy));
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel,2991,29910); ProcessHandle foreign =TestFixtures.Create(kernel,2992,29920).Handle;
        OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(owner,64).Value!; OwnedBuffer<byte> foreignBuffer =kernel.AllocateBuffer<byte>(foreign,32).Value!;
        PlatformDomainBinding root =kernel.BindPlatformDomain(owner).Value;
        const string name="device/irq-capacity";
        CapabilityId dc =kernel.MintCapability(new(29910),owner,ResourceKind.Device,name,CapabilityRights.Configure).Value!.CapabilityId;
        PlatformDeviceLease device =kernel.BindPlatformDevice(owner,root,dc,PlatformDeviceRights.Configure).Value;
        CapabilityId Cap(string source)=>kernel.MintCapability(new(29910),owner,ResourceKind.Irq,CapabilityResourceIds.Irq(name,source,IrqTriggerMode.Edge),CapabilityRights.Signal).Value!.CapabilityId;
        KernelEventEndpoint peer =kernel.CreateKernelEventEndpoint(owner).Value;Assert.True(kernel.BindPlatformInterrupt(owner,device,Cap("peer"),peer).IsSuccess);
        KernelEventEndpoint endpoint =kernel.CreateKernelEventEndpoint(owner).Value;
        typeof(NeutralDomainRuntimeFacade).GetField("_nextResource",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(spy.Inner,ulong.MaxValue);spy.Lose=lost;
        Assert.False(kernel.BindPlatformInterrupt(owner,device,Cap("refusal"),endpoint).IsSuccess);
        Task<KernelResult<KernelEvent>> waiter =kernel.WaitForKernelEventAsync(owner,endpoint).AsTask();Assert.False(waiter.IsCompleted);
        Assert.Equal(2,spy.Binds);Assert.Equal(1,spy.Inner.ActiveInterruptLeaseCount);
        if(lost)
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await waiter.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);spy.Lose=false;
        }
        KernelResult closed =kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await waiter);Assert.Equal(2,spy.Binds);Assert.Equal(0,spy.Inner.ActiveInterruptLeaseCount);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==foreignBuffer.Handle).State);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void NativeLookupRequiresOriginalHistoryDeviceFactoryAndKnownDecision(int fault)
    {
        NeutralDomainRuntimeFacade native =new NeutralDomainRuntimeFacade(); Guid incarnation =((INeutralOriginalMappingClosureProvider)native).MappingClosureIncarnation;
        NeutralOriginalDomainBindRequest rootRequest =new NeutralOriginalDomainBindRequest(Guid.NewGuid(),incarnation,NeutralDomainProfile.OrdinaryService);
        NeutralDomainBindingLease root =native.Bind(rootRequest).Observation.Lease;
        NeutralOriginalDeviceBindRequest deviceRequest =new NeutralOriginalDeviceBindRequest(Guid.NewGuid(),incarnation,rootRequest.InvocationId,root,new("irq-device"),NeutralDeviceRights.Configure);
        NeutralDeviceLease device =native.BindDevice(deviceRequest).Observation.Lease;
        NeutralOriginalIrqBindRequest peer =new NeutralOriginalIrqBindRequest(Guid.NewGuid(),incarnation,deviceRequest.InvocationId,device,new("peer",NeutralInterruptTrigger.Edge));
        Assert.True(native.BindInterrupt(peer).Observation.IsBound);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(NeutralDomainRuntimeFacade).GetField("_nextResource",flags)!.SetValue(native,ulong.MaxValue);
        NeutralOriginalIrqBindRequest request =peer with {InvocationId=Guid.NewGuid(),Source=new("refusal",NeutralInterruptTrigger.Edge)};
        NeutralOriginalIrqBindObservation raw =native.BindInterrupt(request);Assert.True(raw.NoEffectGuaranteed);Assert.Equal(raw,native.ResolveIrqBind(request));
        Assert.Null(native.ResolveIrqBind(request with {DeviceBindInvocationId=Guid.NewGuid()}));
        object dependencies =typeof(NeutralDomainRuntimeFacade).GetField("_dependencies",flags)!.GetValue(native)!;
        IDictionary devices =(IDictionary)dependencies.GetType().GetField("Devices",flags)!.GetValue(dependencies)!; object parent =Assert.Single(devices.Values.Cast<object>());
        IList facts =(IList)parent.GetType().GetProperty("OriginalIrqAdmissions")!.GetValue(parent)!; object original =facts[1]!;
        if(fault==0)facts[1]=typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null);
        else if(fault==1)facts.RemoveAt(1);
        else if(fault==2)original.GetType().GetProperty("Observation")!.SetValue(original,raw with {Observation=new(){Decision=(NeutralInterruptBindDecision)999}});
        else { PropertyInfo property =parent.GetType().GetProperty("OriginalAdmission")!; object factory =property.GetValue(parent)!;property.SetValue(parent,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(factory,null));}
        Assert.Null(native.ResolveIrqBind(request));Assert.False(native.BindInterrupt(request).NoEffectGuaranteed);Assert.Equal(1,native.ActiveInterruptLeaseCount);
    }
}
