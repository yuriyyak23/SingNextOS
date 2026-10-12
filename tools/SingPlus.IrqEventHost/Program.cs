using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;
using SingPlus.Sip.Drivers;
using System.Security.Cryptography;

var native=new NeutralDomainRuntimeFacade();
var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(native));
void Check(bool value,string operation){if(!value)throw new InvalidOperationException(operation);}
ProcessHandle Create(ulong id,ulong domain)
{
    var manifest=new SingProcessManifestV1(new(id),new(domain),1,$"irq-host-{id}",ExecutionRole.Sip,MemoryProfile.SipRegion);
    Check(kernel.CreateProcess(manifest).IsSuccess,"create");return new(manifest.ProcessId,manifest.Generation);
}
var owner=Create(2301,23010);var admin=Create(2302,23020);
var ac=kernel.MintCapability(new(23020),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
var budget=kernel.AdmitProcessBudget(admin,ac,owner,"irq-host",[new(ServiceBudgetDimension.OwnedMemoryBytes,512)]).Value!.ProcessBudget;
var region=kernel.AllocateBuffer<byte>(owner,512).Value!;
var root=kernel.BindPlatformDomain(owner).Value;
const string deviceName="device/software-irq-host",sourceName="software-irq-host";
var dc=kernel.MintCapability(new(23010),owner,ResourceKind.Device,deviceName,CapabilityRights.Configure).Value!.CapabilityId;
var device=kernel.BindPlatformDevice(owner,root,dc,PlatformDeviceRights.Configure).Value;
var ic=kernel.MintCapability(new(23010),owner,ResourceKind.Irq,CapabilityResourceIds.Irq(deviceName,sourceName,IrqTriggerMode.Edge),CapabilityRights.Signal).Value!.CapabilityId;
var endpoint=kernel.CreateKernelEventEndpoint(owner).Value;
var bound=kernel.BindPlatformInterrupt(owner,device,ic,endpoint);Check(bound.IsSuccess,bound.Message??"bind IRQ");
var request=native.PrepareOriginalIrqSignal(deviceName,sourceName);Check(request is not null,"original source preparation");
var signaled=native.SignalOriginalIrq(request!);Check(signaled is {IsSignaled:true,NoEffectGuaranteed:false},"signal producer");
Check(ReferenceEquals(signaled,native.ResolveOriginalIrqSignal(request!)),"same original observation");
var delivered=kernel.PollPlatformInterrupt(owner,bound.Value);Check(delivered.IsSuccess && delivered.Value.DeliveryAvailable,"kernel delivery");
var consumed=kernel.ConsumeKernelEvent(owner,endpoint);Check(consumed.IsSuccess && consumed.Value.EventClass==KernelEventClass.ExternalSignal,"event consume");
Check(ReferenceEquals(signaled,native.SignalOriginalIrq(request!)),"original signal replay");
var replay=kernel.PollPlatformInterrupt(owner,bound.Value);Check(replay.IsSuccess && !replay.Value.DeliveryAvailable,"no repeated delivery after ack");
Check(!kernel.ConsumeKernelEvent(owner,endpoint).IsSuccess,"no repeated Sing event");
var next=native.PrepareOriginalIrqSignal(deviceName,sourceName);Check(next is not null,"next original source");
var second=native.SignalOriginalIrq(next!);Check(second is {IsSignaled:true} && second.Sequence.Value==signaled!.Sequence.Value+1,"next delivery identity");
var preparedBeforeClosure=native.PrepareOriginalIrqSignal(deviceName,sourceName)!;
Check(preparedBeforeClosure is not null,"prepared pending source");
Check(kernel.RevokePlatformInterrupt(owner,bound.Value).IsSuccess,"exact IRQ closure");
Check(ReferenceEquals(second,native.SignalOriginalIrq(next!)),"contained signal observation only");
Check(!kernel.PollPlatformInterrupt(owner,bound.Value).IsSuccess,"contained signal cannot publish");
Check(!kernel.ConsumeKernelEvent(owner,endpoint).IsSuccess,"pending signal contained without new event");
Check(kernel.CloseKernelEventEndpoint(owner,endpoint).IsSuccess,"endpoint closure");
Check(kernel.TerminateProcess(owner).IsSuccess,"process cleanup");
var refused=native.SignalOriginalIrq(preparedBeforeClosure);
Check(refused is {NoEffectGuaranteed:true,IsSignaled:false},"prepared signal after retirement has no effect");
Check(ReferenceEquals(refused,native.ResolveOriginalIrqSignal(preparedBeforeClosure)),"original refusal retention");
Check(ReferenceEquals(refused,native.SignalOriginalIrq(preparedBeforeClosure)),"refused replay no repin");
Check(kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==0,"charge reclaim");
Check(kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State==RegionState.Released,"Region release");
Check(native.ActiveInterruptLeaseCount==0 && native.ActiveDeviceLeaseCount==0 && native.ActiveBindingCount==0,"native retirement");
// Independent component-scoped consumer of the same producing path.
var caller=Create(2303,23030);
var identity=new ComponentIdentity("software-irq-driver");
const string componentDevice="device/software-irq-driver",componentSource="rx";
var irqResource=CapabilityResourceIds.Irq(componentDevice,componentSource,IrqTriggerMode.Edge);
CapabilityRequirementV1[] requirements=[new(ResourceKind.Device,componentDevice,CapabilityRights.Configure),new(ResourceKind.Irq,irqResource,CapabilityRights.Signal)];
var process=new SingProcessManifestV1(new(2304),new(23040),1,"software-irq-driver",ExecutionRole.Driver,MemoryProfile.SipRegion,requirements);
var protocol=DriverInterruptServiceClient.CreateProtocol();var contract=new ServiceContractIdentity(protocol.ContractName,"1",protocol.ContractDigest);
var provided=new ProvidedServiceManifestV1("software-irq",contract);byte[] image=[0x49,0x52,0x51];
var manifest=new ServiceManifestV1(identity,new ComponentVersion("1"),Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),process,[provided],[],requiresPlatformDomain:true,
    platformRequirements:[
        new PlatformRequirementV1(ComponentPlatformFeatureFamily.NeutralDomains,PlatformDomainContract.ContractVersion,ComponentPlatformFeatureAvailability.RuntimeAdmission),
        new PlatformRequirementV1(ComponentPlatformFeatureFamily.IoDomainBinding,PlatformDeviceLeaseContract.ContractVersion,ComponentPlatformFeatureAvailability.RuntimeAdmission),
        new PlatformRequirementV1(ComponentPlatformFeatureFamily.IrqBinding,PlatformIrqBindingContract.ContractVersion,ComponentPlatformFeatureAvailability.RuntimeAdmission)],
    budgetRequests:[new ServiceBudgetRequestV1(ServiceBudgetDimension.OwnedMemoryBytes,512)]);
var admission=kernel.AdmitComponent(new ComponentAdmissionPlan(manifest,image,requirements.Select(r=>new ComponentCapabilityGrant(new(23020),r)),
    [new ComponentProvidedServiceRegistration(provided,protocol,DriverInterruptServiceClient.ResponseProtocol)],
    new ComponentDriverResourcePlan(componentDevice,PlatformDeviceRights.Configure,irqs:[new DriverIrqResourcePlan(irqResource)])));
Check(admission.IsSuccess,admission.Message??"component admission");
var driverBudget=admission.Value!.ProcessBudget;
var driverRegion=kernel.AllocateBuffer<byte>(new(process.ProcessId,process.Generation),512).Value!;
Check(kernel.QueryBudget(driverBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==512,"driver original charge");
var descriptor=kernel.ResolveByContract(contract).Value!.Single();var session=kernel.OpenSession(caller,descriptor).Value;
var host=RuntimeDriverInterruptServiceHost.CreateForComponent(kernel,identity,session,irqResource).Value!;
var client=DriverInterruptServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session));
var componentSignal=native.PrepareOriginalIrqSignal(componentDevice,componentSource)!;
Check(native.SignalOriginalIrq(componentSignal) is {IsSignaled:true},"component signal");
var pending=client.PollAsync();Check(host.ProcessNext().IsSuccess,"session dispatch");var response=await pending;
Check(response.Status==DriverInterruptPollStatus.Delivered && response.Event.EventClass==KernelEventClass.ExternalSignal,"typed event response");
Check(native.SignalOriginalIrq(componentSignal) is {IsSignaled:true},"same source replay");
var empty=client.PollAsync();Check(host.ProcessNext().IsSuccess,"empty dispatch");Check((await empty).Status==DriverInterruptPollStatus.NoDelivery,"no repeated typed event");
Check(kernel.CloseSession(caller,session).IsSuccess,"caller session closure");
var retired=kernel.FaultComponent(identity);Check(retired.IsSuccess,retired.Message??"component retirement");
Check(kernel.QueryBudget(driverBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==0,"driver charge reclaim");
Check(kernel.Regions.Snapshot().Single(r=>r.Handle==driverRegion.Handle).State==RegionState.Released,"driver Region reclaim");
Check(native.ActiveInterruptLeaseCount==0 && native.ActiveDeviceLeaseCount==0 && native.ActiveBindingCount==0,"component native retirement");
Console.WriteLine("Software IRQ/event host: signals=2; delivered=1; pendingContained=1; replay=0; Region=Released; charge=512->0; IRQ/devices/roots=0; gates=OFF; ISA=NONE; ProductionQualified=false");
Console.WriteLine("Driver typed session: delivered=1; replay=NoDelivery; component charge=512->0; Region=Released");
