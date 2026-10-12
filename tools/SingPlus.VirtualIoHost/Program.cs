using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Sip.Native;
using SingPlus.Sip.Virtualization;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

var native=new NeutralDomainRuntimeFacade();
var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(native,new HybridCpuExecutableChildAdapter()));
ProcessHandle Create(ulong id,ulong domain)
{
    var manifest=new SingProcessManifestV1(new(id),new(domain),1,$"virtual-io-host-{id}",ExecutionRole.Sip,MemoryProfile.SipRegion);
    Check(kernel.CreateProcess(manifest).IsSuccess,"process creation");return new(manifest.ProcessId,manifest.Generation);
}
void Check(bool value,string operation){if(!value)throw new InvalidOperationException(operation);}
var owner=Create(2101,21010);var service=Create(2102,21020);var admin=Create(2103,21030);
var budgetCap=kernel.MintCapability(new(21030),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
var budget=kernel.AdmitProcessBudget(admin,budgetCap,owner,"virtual-io-host",[new(ServiceBudgetDimension.OwnedMemoryBytes,520)]).Value!.ProcessBudget;
var createCap=kernel.MintCapability(new(21010),owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
var created=kernel.CreateVirtualDomain(owner,createCap,new(1,4096));Check(created.IsSuccess,created.Message??"create");
var domain=created.Value!;Check(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess,"configure");
var root=kernel.QueryVirtualDomainPlatformBinding(owner,domain.Domain);Check(root.IsSuccess,root.Message??"parent");
var deviceCap=kernel.MintCapability(new(21010),owner,ResourceKind.Device,"device/virtual-io-host",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
var admitted=kernel.BindPlatformDevice(owner,root.Value,deviceCap,PlatformDeviceRights.Read|PlatformDeviceRights.Write);Check(admitted.IsSuccess,admitted.Message??"device");
var device=admitted.Value!;
var protocol=VirtualizationServiceClient.CreateProtocol();
var descriptor=kernel.RegisterService(service,"virtual-io",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
var session=kernel.OpenSession(owner,descriptor,[]).Value;
var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
var client=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
async Task<T> Dispatch<T>(ValueTask<T> pending){var result=host.ProcessNext();Check(result.IsSuccess,result.Message??"dispatch");return await pending;}
ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
var io=await Dispatch(client.BindIoAsync(new(domain.Domain,device.LeaseId.Value,device.Generation.Value,VirtualIoAccess.Read|VirtualIoAccess.Write,512)));
Check(Used()==512,"backing charge");
var written=await Dispatch(client.TransferIoAsync(new(io.IoId,io.Generation,4,true,4,new BoundedBytes(new byte[]{17,19,23,29}))));Check(written.Completed,"write");
var read=await Dispatch(client.TransferIoAsync(new(io.IoId,io.Generation,4,false,4,default)));Check(read.Completed && read.Data.ToArray().SequenceEqual(new byte[]{17,19,23,29}),"read bytes");
Check(Used()==516,"read snapshot charge");
var closing=client.CloseIoAsync(new(io.IoId,io.Generation));Check(host.ProcessNext().IsSuccess,"IO Close dispatch");await closing;
Check(Used()==0,"exact backing/snapshot reclaim");Check(kernel.CloseSession(owner,session).IsSuccess,"session Close");Check(kernel.TerminateProcess(owner).IsSuccess,"process cleanup");
Check(native.ActiveDeviceLeaseCount==0,"native device release");
Console.WriteLine("Virtual-I/O software host: bytes=17,19,23,29; charge=512->516->0; nativeDevices=0; gates=OFF; ISA=NONE; ProductionQualified=false");
