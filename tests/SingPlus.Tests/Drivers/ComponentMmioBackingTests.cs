using System.Reflection;
using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Tests.Platform;
using YAKSys_Hybrid_CPU.Core;
namespace SingPlus.Tests.Drivers;

public sealed class ComponentMmioBackingTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void ReusableCommandCancellationKeepsOriginalMetadataSeparateFromDispatchAndClosure(bool before)
    {
        IRuntime proxy =DispatchProxy.Create<IRuntime,MmioRegisterBackingTests.Spy>(); MmioRegisterBackingTests.Spy spy =(MmioRegisterBackingTests.Spy)(object)proxy;
        RuntimeKernel kernel =new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy)); ComponentIdentity identity =new ComponentIdentity("cancel-command-driver");
        (KernelResult<ComponentLifecycleSnapshot> admitted, string? resource) = Admit(kernel,identity,3131,31310);Assert.True(admitted.IsSuccess,admitted.Message);
        PlatformMmio32Invocation write =kernel.CreateComponentDriverMmio32Invocation(identity,resource,0,true,42).Value!;
        Assert.True(kernel.AccessComponentDriverMmio32(identity,write).IsSuccess);
        RuntimeDriverCommandCaller command =RuntimeDriverCommandCaller.Create(kernel,identity,resource).Value!;
        using CancellationTokenSource cancellation =new CancellationTokenSource();
        if(before)cancellation.Cancel();else spy.BeforeAccess=()=>cancellation.Cancel();
        Assert.Equal(KernelError.CancellationPending,command.ReadForDispatch(cancellation.Token).Error);
        spy.BeforeAccess=null;
        Assert.False(command.ReadForDispatch().IsSuccess);Assert.False(command.ResolveForDispatch().IsSuccess);
        Assert.Equal(before?0:1,spy.Reads);
        KernelResult<uint> metadata =command.ResolveMetadata();Assert.Equal(!before,metadata.IsSuccess);
        if(!before)Assert.Equal(42U,metadata.Value);
        Assert.Equal(64UL,kernel.QueryBudget(admitted.Value!.ProcessBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(kernel.FaultComponent(identity).IsSuccess);
        Assert.All(kernel.QueryBudget(admitted.Value.ProcessBudget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
        if(!before)Assert.Equal(42U,command.ResolveMetadata().Value);
        Assert.False(command.ResolveForDispatch().IsSuccess);Assert.Equal(before?0:1,spy.Reads);
    }
    public interface IRuntime : INeutralDomainRuntime,INeutralOriginalMmioRegistersProvider,INeutralOriginalIrqProvider { }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ComponentOriginalRegisterReadAndFaultCloseHoldExactBackingCharge(bool loss)
    {
        IRuntime proxy =DispatchProxy.Create<IRuntime,MmioRegisterBackingTests.Spy>(); MmioRegisterBackingTests.Spy spy =(MmioRegisterBackingTests.Spy)(object)proxy;
        RuntimeKernel kernel =new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy)); ComponentIdentity identity =new ComponentIdentity("register-driver");
        (KernelResult<ComponentLifecycleSnapshot> admission, string? resource) = Admit(kernel,identity,3101,31010);Assert.True(admission.IsSuccess,admission.Message);
        BudgetAccountHandle budget =admission.Value!.ProcessBudget; ProcessHandle process =admission.Value.Process;
        PlatformMmio32Invocation original =kernel.CreateComponentDriverMmio32Invocation(identity,resource,0,true,42).Value!;
        Assert.True(kernel.AccessComponentDriverMmio32(identity,original).IsSuccess);
        PlatformMmio32Invocation read =kernel.CreateComponentDriverMmio32Invocation(identity,resource,0,false).Value!;spy.LoseAccess=loss;
        KernelResult<uint> result =kernel.AccessComponentDriverMmio32(identity,read);
        if(loss)
        {
            Assert.False(result.IsSuccess);Assert.False(kernel.AccessComponentDriverMmio32(identity,read).IsSuccess);
            Assert.False(kernel.ResolveComponentDriverMmio32(identity,read).IsSuccess);Assert.Equal(1,spy.Reads);spy.LoseAccess=false;
            result=kernel.ResolveComponentDriverMmio32(identity,read);
        }
        Assert.True(result.IsSuccess,result.Message);Assert.Equal(42U,result.Value);
        Assert.Equal(64UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        RegionUseDescriptor use =Assert.Single(kernel.Regions.SnapshotUses());Assert.False(kernel.ReleaseRegionUse(process,use.Handle).IsSuccess);
        spy.LoseClose=loss;
        KernelResult<ComponentLifecycleSnapshot> closed =kernel.FaultComponent(identity);
        if(loss)
        {
            Assert.False(closed.IsSuccess);Assert.NotEqual(RegionUseState.Released,Assert.Single(kernel.Regions.SnapshotUses()).State);
            Assert.Equal(64UL,kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
            spy.LoseClose=false;closed=kernel.ObserveComponentTeardown(identity);
        }
        Assert.True(closed.IsSuccess,closed.Message);Assert.Equal(ComponentLifecycleState.Reclaimable,closed.Value!.State);
        Assert.Equal(1,spy.Maps);Assert.Equal(1,spy.Writes);Assert.Equal(1,spy.Reads);Assert.Equal(1,spy.Closes);
        Assert.Equal(RegionUseState.Released,Assert.Single(kernel.Regions.SnapshotUses()).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
        Assert.False(kernel.AccessComponentDriverMmio32(identity,original).IsSuccess);
        Assert.Equal(42U,kernel.ResolveComponentDriverMmio32(identity,read).Value);
        PlatformMmio32Invocation copied =(PlatformMmio32Invocation)typeof(object).GetMethod("MemberwiseClone",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(read,null)!;
        Assert.False(kernel.ResolveComponentDriverMmio32(identity,copied).IsSuccess);
        Assert.False(kernel.ResolveComponentDriverMmio32(new("foreign-component"),read).IsSuccess);
        Assert.False(new RuntimeKernel().ResolveComponentDriverMmio32(identity,read).IsSuccess);
        Assert.False(kernel.ResolveComponentDriverMmio32(identity,new(read.Mapping,read.Offset,read.Write)).IsSuccess);
        Assert.True(kernel.RetireReclaimableComponent(identity,process).IsSuccess);
        (ComponentAdmissionPlan? nextPlan, string _) = Plan(identity,3101,31010,2);
        KernelResult<ComponentLifecycleSnapshot> next =kernel.AdmitComponent(nextPlan);Assert.True(next.IsSuccess,next.Message);
        Assert.Equal(42U,kernel.ResolveComponentDriverMmio32(identity,read).Value);
        Assert.False(kernel.AccessComponentDriverMmio32(identity,read).IsSuccess);
        Assert.Equal(64UL,kernel.QueryBudget(next.Value!.ProcessBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(kernel.FaultComponent(identity).IsSuccess);
    }
    [Fact]
    public void ComponentIntentDuringNativeCallbackDeniesPublicationWithoutRepeatingOriginalWrite()
    {
        IRuntime proxy =DispatchProxy.Create<IRuntime,MmioRegisterBackingTests.Spy>(); MmioRegisterBackingTests.Spy spy =(MmioRegisterBackingTests.Spy)(object)proxy;
        RuntimeKernel kernel =new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy)); ComponentIdentity identity =new ComponentIdentity("reentry-driver");
        (KernelResult<ComponentLifecycleSnapshot> admission, string? resource) = Admit(kernel,identity,3111,31110);Assert.True(admission.IsSuccess,admission.Message);
        PlatformMmio32Invocation original =kernel.CreateComponentDriverMmio32Invocation(identity,resource,0,true,42).Value!;
        spy.BeforeAccess=()=>Assert.False(kernel.FaultComponent(identity).IsSuccess);
        Assert.False(kernel.AccessComponentDriverMmio32(identity,original).IsSuccess);Assert.Equal(1,spy.Writes);spy.BeforeAccess=null;
        Assert.False(kernel.AccessComponentDriverMmio32(identity,original).IsSuccess);
        KernelResult<uint> known =kernel.ResolveComponentDriverMmio32(identity,original);Assert.True(known.IsSuccess,known.Message);Assert.Equal(42U,known.Value);
        KernelResult<ComponentLifecycleSnapshot> closed =kernel.ObserveComponentTeardown(identity);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(1,spy.Writes);Assert.Equal(1,spy.Closes);
        Assert.All(kernel.QueryBudget(admission.Value!.ProcessBudget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
    }
    private static (KernelResult<ComponentLifecycleSnapshot>,string) Admit(RuntimeKernel kernel,ComponentIdentity identity,ulong pid,ulong domain)
    {
        (ComponentAdmissionPlan? plan, string? resource) = Plan(identity,pid,domain);
        return (kernel.AdmitComponent(plan),resource);
    }
    [Fact]
    public void RevokedSupervisorControlBeforeBackingAllocationCreatesNoRegionOrNativeMap()
    {
        IRuntime proxy =DispatchProxy.Create<IRuntime,MmioRegisterBackingTests.Spy>(); MmioRegisterBackingTests.Spy spy =(MmioRegisterBackingTests.Spy)(object)proxy;
        RuntimeKernel kernel =new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(proxy));
        (Sip.SingProcess _, ProcessHandle principal) = TestFixtures.Create(kernel,3120,31200,identity:"mmio-supervisor");
        Assert.True(kernel.AdmitProcess(principal).IsSuccess);
        CapabilityId control =kernel.MintCapability(new DomainId(31200),principal,ResourceKind.KernelService,
            CapabilityResourceIds.ServiceSupervisor,CapabilityRights.Configure|CapabilityRights.Execute).Value!.CapabilityId;
        ComponentIdentity identity =new ComponentIdentity("revoked-mmio-driver");(ComponentAdmissionPlan? plan, string _) = Plan(identity,3121,31210);
        kernel.BeforeComponentDriverMmioQualificationHook=()=>Assert.True(kernel.RevokeCapability(control).IsSuccess);
        Assert.False(kernel.AdmitComponent(plan,principal,control).IsSuccess);
        Assert.Equal(0,spy.Maps);Assert.Empty(kernel.Regions.SnapshotUses());
        ComponentLifecycleSnapshot component =kernel.QueryComponent(identity).Value!;
        Assert.Equal(ComponentLifecycleState.Reclaimable,component.State);
        Assert.All(kernel.QueryBudget(component.ProcessBudget).Value!.Usage,u=>Assert.Equal(0UL,u.Used));
    }
    private static (ComponentAdmissionPlan,string) Plan(ComponentIdentity identity,ulong pid,ulong domain,ulong generation=1)
    {
        string device ="device/"+identity.Name; string resource =CapabilityResourceIds.MmioRegion(device,"registers",64);
        CapabilityRequirementV1[] requirements=[new(ResourceKind.Device,device,CapabilityRights.Configure|CapabilityRights.Read|CapabilityRights.Write),new(ResourceKind.MmioRegion,resource,CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Write)];
        SingProcessManifestV1 process =new SingProcessManifestV1(new(pid),new(domain),generation,identity.Name,ExecutionRole.Driver,MemoryProfile.SipRegion,requirements);
        byte[] image=[0x4d,0x49,0x4f];
        ServiceManifestV1 manifest =new ServiceManifestV1(identity,new("1"),Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),process,[],[],requiresPlatformDomain:true,
            platformRequirements:[new(ComponentPlatformFeatureFamily.NeutralDomains,PlatformDomainContract.ContractVersion,ComponentPlatformFeatureAvailability.RuntimeAdmission),new(ComponentPlatformFeatureFamily.IoDomainBinding,PlatformDeviceLeaseContract.ContractVersion,ComponentPlatformFeatureAvailability.RuntimeAdmission),new(ComponentPlatformFeatureFamily.MmioMapping,PlatformMmioLeaseContract.ContractVersion,ComponentPlatformFeatureAvailability.RuntimeAdmission)],
            budgetRequests:[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]);
        ComponentAdmissionPlan plan =new ComponentAdmissionPlan(manifest,image,requirements.Select(r=>new ComponentCapabilityGrant(new(domain),r)),driverResources:new(device,PlatformDeviceRights.Configure|PlatformDeviceRights.Read|PlatformDeviceRights.Write,[new(resource,0,64,PlatformMmioAccess.Read|PlatformMmioAccess.Write,SoftwareRegisterBacking:true)]));
        return (plan,resource);
    }
}
