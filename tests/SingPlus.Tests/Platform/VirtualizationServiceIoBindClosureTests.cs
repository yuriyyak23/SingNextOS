using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip.Virtualization;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Tests.Platform;

public sealed class VirtualizationServiceIoBindClosureTests
{
    public class BindLossSpy : ExactVirtualIoBackingConsumerTests.BackingSpy
    {
        public bool LoseBind=true;
        public bool LoseContainment;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            if(LoseBind && method!.Name=="ResolveOriginalVirtualIo")throw new IOException("Bind query lost");
            if(LoseContainment && method!.Name=="ResolveOriginalVirtualIoBackingContainment")throw new IOException("Containment query lost");
            var result=base.Invoke(method,args);
            if(LoseBind && method!.Name=="BindOriginalVirtualIo")throw new IOException("Bind reply lost after backing effect");
            if(LoseContainment && method!.Name=="ContainOriginalVirtualIoBacking")throw new IOException("Containment reply lost after effect");
            return result;
        }
    }

    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public async Task OriginalServiceBindClosesInvocationOnlyAfterBackingAndCharge(bool copyOwner,bool partialClose)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var runtime=DispatchProxy.Create<ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime,BindLossSpy>();
        var spy=(BindLossSpy)(object)runtime;
        spy.Inner=new HybridCPU.ExternalRuntime.HybridCpuExternalRuntime();
        var adapter=new HybridCpuExecutableChildAdapter(spy.Inner,runtime);
        var native=new NeutralDomainRuntimeFacade();
        var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(native,adapter));
        var (process,owner)=TestFixtures.Create(kernel,2501,25010);
        var service=TestFixtures.Create(kernel,2502,25020).Handle;
        var admin=TestFixtures.Create(kernel,2503,25030).Handle;
        var foreign=kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x6A;
        var ac=kernel.MintCapability(new(25030),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget=kernel.AdmitProcessBudget(admin,ac,owner,"IO Bind",[new(ServiceBudgetDimension.OwnedMemoryBytes,512)]).Value!.ProcessBudget;
        ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        var create=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var domain=kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess);
        var root=kernel.QueryVirtualDomainPlatformBinding(owner,domain.Domain).Value;
        var dc=kernel.MintCapability(process.DomainId,owner,ResourceKind.Device,"device/io-bind",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
        var device=kernel.BindPlatformDevice(owner,root,dc,PlatformDeviceRights.Read|PlatformDeviceRights.Write).Value;
        var protocol=VirtualizationServiceClient.CreateProtocol();
        var descriptor=kernel.RegisterService(service,"IO Bind",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
        var session=kernel.OpenSession(owner,descriptor,[]).Value;
        var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
        var client=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
        var pending=client.BindIoAsync(new(domain.Domain,device.LeaseId.Value,device.Generation.Value,VirtualIoAccess.Read|VirtualIoAccess.Write,512));
        Assert.False(host.ProcessNext().IsSuccess);await Assert.ThrowsAnyAsync<Exception>(async()=>await pending);
        Assert.Equal(512UL,Used());Assert.Equal(1,spy.Calls["BindOriginalVirtualIo"]);
        var registry=(EndpointSessionInvocationRegistry)kernel.GetType().GetField("_endpointSessionInvocationRegistry",flags)!.GetValue(kernel)!;
        var records=(IDictionary)registry.GetType().GetField("_ownedRecords",flags)!.GetValue(registry)!;
        var record=Assert.Single(records.Values.Cast<object>());
        var property=record.GetType().GetProperty("OriginalVirtualIoBind")!;
        var original=property.GetValue(record)!;
        var invocation=(EndpointSessionInvocationHandle)record.GetType().GetProperty("Handle")!.GetValue(record)!;
        Assert.False(registry.ConsumeVirtualIoAdmissionClosure(invocation,service,original).IsSuccess);
        Assert.False(registry.ConsumeVirtualIoAdmissionClosure(invocation,owner,new object()).IsSuccess);
        spy.LoseBind=false;
        if(partialClose)
        {
            spy.LoseContainment=true;
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(512UL,Used());Assert.Equal(1,native.ActiveDeviceLeaseCount);
            Assert.False((bool)record.GetType().GetProperty("VirtualIoBindContained")!.GetValue(record)!);
            spy.LoseContainment=false;
        }
        if(copyOwner)
        {
            property.SetValue(record,typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null));
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.False((bool)record.GetType().GetProperty("VirtualIoBindContained")!.GetValue(record)!);
            Assert.Equal(0UL,Used()); // Exact backing charge closed independently; invocation still pinned.
            property.SetValue(record,original);
        }
        var closed=kernel.TerminateProcess(owner);Assert.True(closed.IsSuccess,closed.Message);
        Assert.Equal(0UL,Used());Assert.Equal(0,native.ActiveDeviceLeaseCount);Assert.Equal(0,native.ActiveBindingCount);
        Assert.Equal(1,spy.Calls["BindOriginalVirtualIo"]);Assert.Equal(1,spy.Calls["ContainOriginalVirtualIoBacking"]);
        Assert.True(kernel.Processes.Resolve(service).IsSuccess);Assert.Equal(0x6A,foreign.Span[0]);
    }
}
