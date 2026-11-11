using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;
using SingPlus.Sip.Drivers;
using System.Security.Cryptography;
using SingPlus.Sip;

if (args.Contains("--supervisor-mmio",StringComparer.Ordinal))
{
    Environment.Exit(SupervisorMmioHost.Run(args));return;
}

NeutralDomainRuntimeFacade native =new NeutralDomainRuntimeFacade();
bool capacity =args.Contains("--irq-capacity-refusal",StringComparer.Ordinal);
bool driverDma =args.Contains("--driver-mmio-dma",StringComparer.Ordinal);
bool driverRegisters =driverDma || args.Contains("--driver-mmio-registers",StringComparer.Ordinal);
IrqReplyLossRuntime? replyLoss=null;INeutralDomainRuntime rootRuntime=native;
if(capacity || driverRegisters)
{
    IIrqReplyLossRuntime proxy =global::System.Reflection.DispatchProxy.Create<IIrqReplyLossRuntime,IrqReplyLossRuntime>();
    replyLoss=(IrqReplyLossRuntime)(object)proxy;replyLoss.Inner=native;rootRuntime=proxy;
}
RuntimeKernel kernel =new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(rootRuntime));
void Check(bool value,string operation){if(!value){Console.Error.WriteLine(operation);Environment.Exit(1);}}
ProcessHandle Create(ulong id,ulong domain)
{
    SingProcessManifestV1 manifest =new SingProcessManifestV1(new(id),new(domain),1,$"irq-host-{id}",ExecutionRole.Sip,MemoryProfile.SipRegion);
    Check(kernel.CreateProcess(manifest).IsSuccess,"create");return new(manifest.ProcessId,manifest.Generation);
}
ProcessHandle owner =Create(2301,23010); ProcessHandle admin =Create(2302,23020);
CapabilityId ac =kernel.MintCapability(new(23020),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,ac,owner,"irq-host",[new(ServiceBudgetDimension.OwnedMemoryBytes,512)]).Value!.ProcessBudget;
OwnedBuffer<byte> region =kernel.AllocateBuffer<byte>(owner,512).Value!;
PlatformDomainBinding root =kernel.BindPlatformDomain(owner).Value;
const string deviceName="device/software-irq-host",sourceName="software-irq-host";
CapabilityId dc =kernel.MintCapability(new(23010),owner,ResourceKind.Device,deviceName,CapabilityRights.Configure).Value!.CapabilityId;
PlatformDeviceLease device =kernel.BindPlatformDevice(owner,root,dc,PlatformDeviceRights.Configure).Value;
CapabilityId ic =kernel.MintCapability(new(23010),owner,ResourceKind.Irq,CapabilityResourceIds.Irq(deviceName,sourceName,IrqTriggerMode.Edge),CapabilityRights.Signal).Value!.CapabilityId;
KernelEventEndpoint endpoint =kernel.CreateKernelEventEndpoint(owner).Value;
KernelResult<PlatformIrqBinding> bound =kernel.BindPlatformInterrupt(owner,device,ic,endpoint);Check(bound.IsSuccess,bound.Message??"bind IRQ");
if(capacity)
{
    ProcessHandle foreign =Create(2303,23030); OwnedBuffer<byte> foreignRegion =kernel.AllocateBuffer<byte>(foreign,32).Value!;
    KernelEventEndpoint failedEndpoint =kernel.CreateKernelEventEndpoint(owner).Value;
    const string failedSource="capacity-refusal";
    CapabilityId failedCap =kernel.MintCapability(new(23010),owner,ResourceKind.Irq,CapabilityResourceIds.Irq(deviceName,failedSource,IrqTriggerMode.Edge),CapabilityRights.Signal).Value!.CapabilityId;
    NeutralOriginalIrqSignalRequest? originalSignal =native.PrepareOriginalIrqSignal(deviceName,sourceName);Check(originalSignal is not null,"independent source live");
    Check(native.SignalOriginalIrq(originalSignal!) is {IsSignaled:true},"independent delivery producer");
    KernelResult<PlatformInterruptPollResult> independentDelivery =kernel.PollPlatformInterrupt(owner,bound.Value);Check(independentDelivery.IsSuccess,independentDelivery.Message??"independent original delivery");
    Check(kernel.ConsumeKernelEvent(owner,endpoint).IsSuccess,"independent event consumed");
    typeof(NeutralDomainRuntimeFacade).GetField("_nextResource",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.SetValue(native,ulong.MaxValue);
    replyLoss!.Lose=args.Contains("--lost-irq-bind-reply",StringComparer.Ordinal);
    KernelResult<PlatformIrqBinding> failed =kernel.BindPlatformInterrupt(owner,device,failedCap,failedEndpoint);Check(!failed.IsSuccess,"capacity IRQ not published");
    Check(replyLoss.Binds==2 && native.ActiveInterruptLeaseCount==1,"independent IRQ/refusal once");
    Task<KernelResult<KernelEvent>> waiter =kernel.WaitForKernelEventAsync(owner,failedEndpoint).AsTask();Check(!waiter.IsCompleted,"failed IRQ endpoint waiter pending");
    if(replyLoss.Lose)
    {
        Check(!kernel.TerminateProcess(owner).IsSuccess,"unknown IRQ admission pins caller");
        bool locallyCancelled =false;
        try{await waiter.WaitAsync(TimeSpan.FromSeconds(10));}catch(OperationCanceledException){locallyCancelled=true;}
        catch(TimeoutException){Check(false,"original waiter cancellation was not observed");}
        Check(locallyCancelled && native.ActiveInterruptLeaseCount==1,"wait cancellation does not contain unknown IRQ admission");
        replyLoss.Lose=false;
    }
    Check(kernel.CloseKernelEventEndpoint(owner,failedEndpoint).IsSuccess,"same-original no-effect endpoint retirement");
    KernelResult irqTeardown =kernel.TerminateProcess(owner);Check(irqTeardown.IsSuccess,irqTeardown.Message??"original IRQ no-effect cleanup");
    bool waitCancelled =false;try{await waiter;}catch(OperationCanceledException){waitCancelled=true;}Check(waitCancelled,"caller closure cancels original waiter");
    Check(replyLoss.Binds==2 && native.ActiveInterruptLeaseCount==0 && native.ActiveDeviceLeaseCount==0 && native.ActiveBindingCount==0,"original IRQ/device/root closure no repeat");
    Check(kernel.QueryBudget(budget).Value!.Usage.All(u=>u.Used==0),"IRQ caller quantitative reclaim");
    Check(kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State==RegionState.Released && kernel.Regions.Snapshot().Single(r=>r.Handle==foreignRegion.Handle).State==RegionState.Owned,"caller Region released/foreign alive");
    Console.WriteLine("IRQ capacity: Bind2 (independent1/refusal1); signal/event1; waiter pending->closed; no repeat; charges512->0; nativeIRQ/device/root0; foreignRegionOwned; gatesOFF; ISA NONE; ProductionQualified=false");
    return;
}
NeutralOriginalIrqSignalRequest? request =native.PrepareOriginalIrqSignal(deviceName,sourceName);Check(request is not null,"original source preparation");
NeutralOriginalIrqSignalObservation? signaled =native.SignalOriginalIrq(request!);Check(signaled is {IsSignaled:true,NoEffectGuaranteed:false},"signal producer");
Check(ReferenceEquals(signaled,native.ResolveOriginalIrqSignal(request!)),"same original observation");
KernelResult<PlatformInterruptPollResult> delivered =kernel.PollPlatformInterrupt(owner,bound.Value);Check(delivered.IsSuccess && delivered.Value.DeliveryAvailable,"kernel delivery");
KernelResult<KernelEvent> consumed =kernel.ConsumeKernelEvent(owner,endpoint);Check(consumed.IsSuccess && consumed.Value.EventClass==KernelEventClass.ExternalSignal,"event consume");
Check(ReferenceEquals(signaled,native.SignalOriginalIrq(request!)),"original signal replay");
KernelResult<PlatformInterruptPollResult> replay =kernel.PollPlatformInterrupt(owner,bound.Value);Check(replay.IsSuccess && !replay.Value.DeliveryAvailable,"no repeated delivery after ack");
Check(!kernel.ConsumeKernelEvent(owner,endpoint).IsSuccess,"no repeated Sing event");
NeutralOriginalIrqSignalRequest? next =native.PrepareOriginalIrqSignal(deviceName,sourceName);Check(next is not null,"next original source");
NeutralOriginalIrqSignalObservation? second =native.SignalOriginalIrq(next!);Check(second is {IsSignaled:true} && second.Sequence.Value==signaled!.Sequence.Value+1,"next delivery identity");
NeutralOriginalIrqSignalRequest? preparedBeforeClosure =native.PrepareOriginalIrqSignal(deviceName,sourceName)!;
Check(preparedBeforeClosure is not null,"prepared pending source");
Check(kernel.RevokePlatformInterrupt(owner,bound.Value).IsSuccess,"exact IRQ closure");
Check(ReferenceEquals(second,native.SignalOriginalIrq(next!)),"contained signal observation only");
Check(!kernel.PollPlatformInterrupt(owner,bound.Value).IsSuccess,"contained signal cannot publish");
Check(!kernel.ConsumeKernelEvent(owner,endpoint).IsSuccess,"pending signal contained without new event");
Check(kernel.CloseKernelEventEndpoint(owner,endpoint).IsSuccess,"endpoint closure");
Check(kernel.TerminateProcess(owner).IsSuccess,"process cleanup");
NeutralOriginalIrqSignalObservation? refused =native.SignalOriginalIrq(preparedBeforeClosure);
Check(refused is {NoEffectGuaranteed:true,IsSignaled:false},"prepared signal after retirement has no effect");
Check(ReferenceEquals(refused,native.ResolveOriginalIrqSignal(preparedBeforeClosure)),"original refusal retention");
Check(ReferenceEquals(refused,native.SignalOriginalIrq(preparedBeforeClosure)),"refused replay no repin");
Check(kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==0,"charge reclaim");
Check(kernel.Regions.Snapshot().Single(r=>r.Handle==region.Handle).State==RegionState.Released,"Region release");
Check(native.ActiveInterruptLeaseCount==0 && native.ActiveDeviceLeaseCount==0 && native.ActiveBindingCount==0,"native retirement");
// Independent component-scoped consumer of the same producing path.
ProcessHandle caller =Create(2303,23030);
ComponentIdentity identity =new ComponentIdentity("software-irq-driver");
const string componentDevice="device/software-irq-driver",componentSource="rx";
string irqResource =CapabilityResourceIds.Irq(componentDevice,componentSource,IrqTriggerMode.Edge);
string mmioResource =CapabilityResourceIds.MmioRegion(componentDevice,"status",64);
CapabilityRequirementV1[] requirements=driverRegisters
    ?[new(ResourceKind.Device,componentDevice,CapabilityRights.Configure|CapabilityRights.Read|CapabilityRights.Write),new(ResourceKind.Irq,irqResource,CapabilityRights.Signal),new(ResourceKind.MmioRegion,mmioResource,CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Write)]
    :[new(ResourceKind.Device,componentDevice,CapabilityRights.Configure),new(ResourceKind.Irq,irqResource,CapabilityRights.Signal)];
SingProcessManifestV1 process =new SingProcessManifestV1(new(2304),new(23040),1,"software-irq-driver",ExecutionRole.Driver,MemoryProfile.SipRegion,requirements);
ProtocolDefinitionV1 protocol =DriverInterruptServiceClient.CreateProtocol(); ServiceContractIdentity contract =new ServiceContractIdentity(protocol.ContractName,"1",protocol.ContractDigest);
ProvidedServiceManifestV1 provided =new ProvidedServiceManifestV1("software-irq",contract);byte[] image=[0x49,0x52,0x51];
ServiceManifestV1 manifest =new ServiceManifestV1(identity,new ComponentVersion("1"),Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),process,[provided],[],requiresPlatformDomain:true,
    platformRequirements:[
        new PlatformRequirementV1(ComponentPlatformFeatureFamily.NeutralDomains,PlatformDomainContract.ContractVersion,ComponentPlatformFeatureAvailability.RuntimeAdmission),
        new PlatformRequirementV1(ComponentPlatformFeatureFamily.IoDomainBinding,PlatformDeviceLeaseContract.ContractVersion,ComponentPlatformFeatureAvailability.RuntimeAdmission),
        new PlatformRequirementV1(ComponentPlatformFeatureFamily.IrqBinding,PlatformIrqBindingContract.ContractVersion,ComponentPlatformFeatureAvailability.RuntimeAdmission),
        ..(driverDma ? new[]{new PlatformRequirementV1(ComponentPlatformFeatureFamily.DmaMapping,PlatformDmaGrantContract.ContractVersion,ComponentPlatformFeatureAvailability.RuntimeAdmission)} : []),
        ..(driverRegisters ? new[]{new PlatformRequirementV1(ComponentPlatformFeatureFamily.MmioMapping,PlatformMmioLeaseContract.ContractVersion,ComponentPlatformFeatureAvailability.RuntimeAdmission)} : [])],
    budgetRequests:[new ServiceBudgetRequestV1(ServiceBudgetDimension.OwnedMemoryBytes,driverDma?640UL:driverRegisters?576UL:512UL)]);
KernelResult<ComponentLifecycleSnapshot> admission =kernel.AdmitComponent(new ComponentAdmissionPlan(manifest,image,requirements.Select(r=>new ComponentCapabilityGrant(new(23020),r)),
    [new ComponentProvidedServiceRegistration(provided,protocol,DriverInterruptServiceClient.ResponseProtocol)],
    new ComponentDriverResourcePlan(componentDevice,PlatformDeviceRights.Configure|(driverRegisters?PlatformDeviceRights.Read|PlatformDeviceRights.Write:PlatformDeviceRights.None),
        mmio:driverRegisters?[new DriverMmioResourcePlan(mmioResource,0,64,PlatformMmioAccess.Read|PlatformMmioAccess.Write,SoftwareRegisterBacking:true)]:[],
        irqs:[new DriverIrqResourcePlan(irqResource)],
        dmaPolicy:driverDma?new DriverDmaPolicy(64,[PlatformDmaDirection.DeviceReadsMemory]):null)));
Check(admission.IsSuccess,admission.Message??"component admission");
BudgetAccountHandle driverBudget =admission.Value!.ProcessBudget;
OwnedBuffer<byte> driverRegion =kernel.AllocateBuffer<byte>(new(process.ProcessId,process.Generation),512).Value!;
Check(kernel.QueryBudget(driverBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==(driverRegisters?576UL:512UL),"driver original charge");
Action? retireDmaDestination=null;
bool malformedDma =driverDma && args.Contains("--malformed-driver-dma-reply",StringComparer.Ordinal);
bool unknownCommand =driverDma && args.Contains("--driver-dma-unknown-command",StringComparer.Ordinal);
bool cancelDmaBefore =driverDma && args.Contains("--cancel-driver-dma-before",StringComparer.Ordinal);
bool cancelDmaAfter =driverDma && args.Contains("--cancel-driver-dma-after",StringComparer.Ordinal);
bool dmaFailure =malformedDma || cancelDmaBefore || cancelDmaAfter || driverDma && args.Contains("--lost-driver-dma-reply",StringComparer.Ordinal);
uint expectedStatus =dmaFailure || unknownCommand?255U:42U;
if(driverRegisters)
{
    KernelResult<PlatformMmio32Invocation> command =kernel.CreateComponentDriverMmio32Invocation(identity,mmioResource,0,true,unknownCommand?99U:17U);Check(command.IsSuccess,command.Message??"component command identity");
    Check(kernel.AccessComponentDriverMmio32(identity,command.Value!).IsSuccess,"component command write");
    if(driverDma)
    {
        bool pendingDmaCleanup =false;
        replyLoss!.LoseDmaCopy=args.Contains("--lost-driver-dma-reply",StringComparer.Ordinal) && !malformedDma;
        replyLoss.CancelDmaBefore=cancelDmaBefore;replyLoss.CancelDmaAfter=cancelDmaAfter;
        replyLoss.MalformedDmaCopy=malformedDma;
        replyLoss.ForeignDmaMapping=args.Contains("--foreign-driver-dma-mapping",StringComparer.Ordinal);
        replyLoss.PartialDmaMapping=args.Contains("--lost-driver-dma-mapping-close-reply",StringComparer.Ordinal);
        replyLoss.PartialDmaGrant=args.Contains("--lost-driver-dma-grant-close-reply",StringComparer.Ordinal);
        replyLoss.LoseCommandReply=args.Contains("--lost-driver-command-reply",StringComparer.Ordinal);
        replyLoss.TeardownCommand=args.Contains("--teardown-driver-command",StringComparer.Ordinal);
        try { retireDmaDestination=DriverDmaFlow.Copy(kernel,identity,mmioResource,admission.Value.Process,admin,driverBudget,out pendingDmaCleanup,replyLoss); }
        catch(Exception exception){Check(false,exception.ToString());}
        if(pendingDmaCleanup)
        {
            retireDmaDestination!.Invoke();
            Console.WriteLine($"DMA dispatch withheld: original retained; Copy{replyLoss.DmaCopies}; no status/IRQ publication. Exact cleanup or retained charges checked by caller; no cold recovery claim.");
            return;
        }
        Check(replyLoss!.DmaCopies==(unknownCommand || cancelDmaBefore?0:1) && native.ActiveDmaGrantCount==0 && native.ActiveOwnedRegionMappingCount==(!unknownCommand && replyLoss.ForeignDmaMapping?1:0),"exact command effect count and own DMA leases/mappings closure before status");
    }
    PlatformMmio32Invocation status =kernel.CreateComponentDriverMmio32Invocation(identity,mmioResource,4,true,expectedStatus).Value!;
    Check(kernel.AccessComponentDriverMmio32(identity,status).IsSuccess,"component status write");
}
ServiceEndpointDescriptor descriptor =kernel.ResolveByContract(contract).Value!.Single(); EndpointSessionHandle session =kernel.OpenSession(caller,descriptor).Value;
RuntimeDriverInterruptServiceHost host =RuntimeDriverInterruptServiceHost.CreateForComponent(kernel,identity,session,irqResource).Value!;
IDriverInterruptService client =DriverInterruptServiceClient.Create(new RuntimeSipClientTransport(kernel,caller,session));
NeutralOriginalIrqSignalRequest componentSignal =native.PrepareOriginalIrqSignal(componentDevice,componentSource)!;
Check(native.SignalOriginalIrq(componentSignal) is {IsSignaled:true},"component signal");
ValueTask<DriverInterruptPollResponse> pending =client.PollAsync(); KernelResult dispatched =host.ProcessNext();
if(args.Contains("--lost-irq-publication-ack",StringComparer.Ordinal))
    Check(host.ReconcileLastPublication().IsSuccess,"original publication acknowledgement without another Poll");
else Check(dispatched.IsSuccess,"session dispatch");
DriverInterruptPollResponse response =await pending;
Check(response.Status==DriverInterruptPollStatus.Delivered && response.Event.EventClass==KernelEventClass.ExternalSignal,"typed event response");
if(driverRegisters)
{
    PlatformMmio32Invocation status =kernel.CreateComponentDriverMmio32Invocation(identity,mmioResource,4,false).Value!;
    replyLoss!.LoseRegister=args.Contains("--lost-driver-status-reply",StringComparer.Ordinal);
    KernelResult<uint> read =kernel.AccessComponentDriverMmio32(identity,status);
    if(replyLoss.LoseRegister)
    {
        Check(!read.IsSuccess && !kernel.AccessComponentDriverMmio32(identity,status).IsSuccess && !kernel.ResolveComponentDriverMmio32(identity,status).IsSuccess,"unknown status read keeps original/no repeat");
        Check(replyLoss.RegisterReads==(driverDma?2:1),"status effect once after command consumption");replyLoss.LoseRegister=false;read=kernel.ResolveComponentDriverMmio32(identity,status);
    }
    Check(read.IsSuccess && read.Value==expectedStatus,read.Message??"component exact status after typed event");
}
Check(native.SignalOriginalIrq(componentSignal) is {IsSignaled:true},"same source replay");
ValueTask<DriverInterruptPollResponse> empty =client.PollAsync();Check(host.ProcessNext().IsSuccess,"empty dispatch");Check((await empty).Status==DriverInterruptPollStatus.NoDelivery,"no repeated typed event");
Check(kernel.CloseSession(caller,session).IsSuccess,"caller session closure");
Check(host.ReconcileLastPublication().IsSuccess,"published original metadata after session routing retirement");
retireDmaDestination?.Invoke();
if(driverRegisters && args.Contains("--lost-driver-mmio-close-reply",StringComparer.Ordinal))
{
    replyLoss!.LoseMmioClose=true;Check(!kernel.FaultComponent(identity).IsSuccess,"unknown component MMIO Close retains backing");
    Check(kernel.QueryBudget(driverBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==576,"component charge retained until original closure");
    Check(kernel.Regions.SnapshotUses().Any(u=>u.State!=RegionUseState.Released && u.Principal.DomainId==process.DomainId),"component original MMIO use retained");
    replyLoss.LoseMmioClose=false;
}
KernelResult<ComponentLifecycleSnapshot> retired =kernel.FaultComponent(identity);Check(retired.IsSuccess,retired.Message??"component retirement");
Check(kernel.QueryBudget(driverBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==0,"driver charge reclaim");
Check(kernel.Regions.Snapshot().Single(r=>r.Handle==driverRegion.Handle).State==RegionState.Released,"driver Region reclaim");
Check(native.ActiveInterruptLeaseCount==0 && native.ActiveDeviceLeaseCount==0 && native.ActiveBindingCount==0,"component native retirement");
Console.WriteLine("Software IRQ/event host: signals=2; delivered=1; pendingContained=1; replay=0; Region=Released; charge=512->0; IRQ/devices/roots=0; gates=OFF; ISA=NONE; ProductionQualified=false");
Console.WriteLine($"Driver typed session: delivered=1; replay=NoDelivery; component charge={(driverRegisters?576:512)}->0; Region=Released");
if(driverRegisters)Console.WriteLine($"Component MMIO/IRQ: command17/status{expectedStatus}/read{expectedStatus} after typed event; Write{replyLoss!.RegisterWrites}/Read{replyLoss.RegisterReads}/Close{replyLoss.MmioCloses}; original use/Region released; charge576->0; gatesOFF; ISA NONE; ProductionQualified=false");
