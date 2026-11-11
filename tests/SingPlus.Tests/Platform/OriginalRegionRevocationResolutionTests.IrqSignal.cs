using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Tests.Platform;
public sealed partial class OriginalRegionRevocationResolutionTests
{
    [Theory]
    [InlineData("exact")] [InlineData("lost-reply")] [InlineData("request-copy")]
    [InlineData("source-revision")] [InlineData("signal-history")] [InlineData("raw-copy")] [InlineData("foreign-realm")]
    [InlineData("pending-close")]
    [InlineData("pending-close-lost")]
    public void OriginalSoftwareSignalRelaysOnceAndClosesOnlyOriginalLifetime(string fault)
    {
        NeutralDomainRuntimeFacade native =new NeutralDomainRuntimeFacade();
        IResolvingNeutralRuntime proxy =DispatchProxy.Create<IResolvingNeutralRuntime,RuntimeSpy>(); RuntimeSpy spy =(RuntimeSpy)proxy;spy.Inner=native;
        RuntimeKernel kernel =new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy));
        ProcessHandle owner =TestFixtures.Create(kernel,790,1190).Handle; ProcessHandle admin =TestFixtures.Create(kernel,98,998).Handle;
        CapabilityId ac =kernel.MintCapability(new(998),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,ac,owner,"irq-signal",[new(ServiceBudgetDimension.OwnedMemoryBytes,512)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region =kernel.AllocateBuffer<byte>(owner,512).Value!; PlatformDomainBinding root =kernel.BindPlatformDomain(owner).Value;
        CapabilityId dc =kernel.MintCapability(new(1190),owner,ResourceKind.Device,"device/irq-signal",CapabilityRights.Configure).Value!.CapabilityId;
        PlatformDeviceLease device =kernel.BindPlatformDevice(owner,root,dc,PlatformDeviceRights.Configure).Value;
        CapabilityId ic =kernel.MintCapability(new(1190),owner,ResourceKind.Irq,CapabilityResourceIds.Irq("device/irq-signal","signal",IrqTriggerMode.Edge),CapabilityRights.Signal).Value!.CapabilityId;
        KernelEventEndpoint endpoint =kernel.CreateKernelEventEndpoint(owner).Value; PlatformIrqBinding bound =kernel.BindPlatformInterrupt(owner,device,ic,endpoint).Value;
        NeutralOriginalIrqSignalRequest request =native.PrepareOriginalIrqSignal("device/irq-signal","signal")!;Assert.NotNull(request);
        const BindingFlags privateFields=BindingFlags.Instance|BindingFlags.NonPublic;
        object registry =native.GetType().GetField("_dependencies",privateFields)!.GetValue(native)!;
        IDictionary states =(IDictionary)registry.GetType().GetField("Interrupts",privateFields)!.GetValue(registry)!;
        object source =states[request.Binding.Handle]!; IList history =(IList)source.GetType().GetProperty("Signals")!.GetValue(source)!;
        object fact =history[0]!;
        NeutralDomainRuntimeFacade foreign =new NeutralDomainRuntimeFacade(); NeutralDomainBindingLease fr =foreign.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        NeutralDeviceLease fd =foreign.BindDevice(fr,request.Binding.DeviceLease.Device,request.Binding.DeviceLease.Rights).Lease;
        NeutralInterruptLease fi =foreign.BindInterrupt(fd,request.Binding.Source).Lease;Assert.Equal(request.Binding,fi);
        if(fault=="foreign-realm")
        {Assert.Null(foreign.SignalOriginalIrq(request));Assert.Null(foreign.ResolveOriginalIrqSignal(request));}
        if(fault=="request-copy")
        {
            NeutralOriginalIrqSignalRequest copy =(NeutralOriginalIrqSignalRequest)typeof(object).GetMethod("MemberwiseClone",privateFields)!.Invoke(request,null)!;
            Assert.Null(native.SignalOriginalIrq(copy));Assert.Null(native.ResolveOriginalIrqSignal(copy));
        }
        if(fault=="source-revision")
        { PropertyInfo state =source.GetType().GetProperty("Delivery")!;state.SetValue(source,state.GetValue(source));}
        if(fault=="signal-history")
        {history.Add(fact);source.GetType().GetProperty("SignalsIssued")!.SetValue(source,2UL);}
        NeutralOriginalIrqSignalObservation? raw=null;
        if(fault=="lost-reply")
            Assert.Throws<IOException>((Action)(()=>{native.SignalOriginalIrq(request);throw new IOException("Reply lost after native signal");}));
        else raw=native.SignalOriginalIrq(request);
        raw??=native.ResolveOriginalIrqSignal(request);Assert.NotNull(raw);
        if(fault=="raw-copy")
        {
            FieldInfo field =fact.GetType().GetField("Raw")!;
            field.SetValue(fact,typeof(object).GetMethod("MemberwiseClone",privateFields)!.Invoke(raw,null));
            Assert.Null(native.SignalOriginalIrq(request));Assert.Null(native.ResolveOriginalIrqSignal(request));
            field.SetValue(fact,raw);Assert.Same(raw,native.ResolveOriginalIrqSignal(request));
        }
        Assert.Same(raw,native.SignalOriginalIrq(request));
        if(fault is "pending-close" or "pending-close-lost")
        {
            NeutralOriginalIrqSignalRequest preparedBeforeClosure =native.PrepareOriginalIrqSignal("device/irq-signal","signal")!;
            Assert.NotNull(preparedBeforeClosure);
            int contains =0;
            spy.BeforeOperation=name=>{if(name=="ContainInterrupt")contains++;};
            if(fault=="pending-close-lost")
            {
                spy.AfterOperation=name=>{if(name is "ContainInterrupt" or "ResolveIrqContainment")throw new IOException("Containing reply/query lost");};
                Assert.False(kernel.RevokePlatformInterrupt(owner,bound).IsSuccess);
                Assert.False(kernel.CloseKernelEventEndpoint(owner,endpoint).IsSuccess);
                Assert.Equal(0,native.ActiveInterruptLeaseCount);
                Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
                Assert.False(kernel.RevokePlatformInterrupt(owner,bound).IsSuccess);Assert.Equal(1,contains);
                spy.AfterOperation=null;
            }
            Assert.True(kernel.RevokePlatformInterrupt(owner,bound).IsSuccess);
            Assert.Equal(1,contains);
            Assert.False(kernel.PollPlatformInterrupt(owner,bound).IsSuccess);
            Assert.Same(raw,native.SignalOriginalIrq(request));
            Assert.Equal(0,native.ActiveInterruptLeaseCount);
            Assert.False(kernel.ConsumeKernelEvent(owner,endpoint).IsSuccess);
            Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.True(kernel.CloseKernelEventEndpoint(owner,endpoint).IsSuccess);
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            NeutralOriginalIrqSignalObservation? refused =native.SignalOriginalIrq(preparedBeforeClosure);
            Assert.NotNull(refused);Assert.True(refused.NoEffectGuaranteed);Assert.False(refused.IsSignaled);
            Assert.Same(refused,native.ResolveOriginalIrqSignal(preparedBeforeClosure));
            Assert.Same(refused,native.SignalOriginalIrq(preparedBeforeClosure));
            Assert.Null(native.PrepareOriginalIrqSignal("device/irq-signal","signal"));
            Assert.Equal(0,native.ActiveInterruptLeaseCount);Assert.Equal(0,native.ActiveDeviceLeaseCount);
            Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
            Assert.Equal(1,foreign.ActiveInterruptLeaseCount);Assert.False(foreign.PollInterrupt(fi).DeliveryAvailable);
            return;
        }
        if(fault is "source-revision" or "signal-history")
        {
            Assert.True(raw.NoEffectGuaranteed);Assert.False(raw.IsSignaled);
            Assert.False(kernel.PollPlatformInterrupt(owner,bound).Value.DeliveryAvailable);
        }
        else
        {
            Assert.True(raw.IsSignaled);Assert.Equal(1UL,raw.Sequence.Value);
            KernelResult<PlatformInterruptPollResult> polled =kernel.PollPlatformInterrupt(owner,bound);
            Assert.True(polled.IsSuccess,polled.Message);Assert.True(polled.Value.DeliveryAvailable);
            Assert.True(kernel.ConsumeKernelEvent(owner,endpoint).IsSuccess);
            Assert.Same(raw,native.SignalOriginalIrq(request));
            Assert.False(kernel.PollPlatformInterrupt(owner,bound).Value.DeliveryAvailable);
        }
        Assert.False(kernel.ConsumeKernelEvent(owner,endpoint).IsSuccess);
        Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.True(kernel.RevokePlatformInterrupt(owner,bound).IsSuccess);
        Assert.True(kernel.CloseKernelEventEndpoint(owner,endpoint).IsSuccess);Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(0UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State);
        Assert.Equal(0,native.ActiveInterruptLeaseCount);Assert.Equal(0,native.ActiveDeviceLeaseCount);
        Assert.Equal(1,foreign.ActiveInterruptLeaseCount);Assert.Equal(1,foreign.ActiveDeviceLeaseCount);
        Assert.False(foreign.PollInterrupt(fi).DeliveryAvailable);
    }
}
