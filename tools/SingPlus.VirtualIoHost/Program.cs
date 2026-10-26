using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Sip.Native;
using SingPlus.Sip.Virtualization;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;
using System.Reflection;
using SingPlus.Sip;
using HybridCPU.ExternalRuntime;

NeutralDomainRuntimeFacade native =new NeutralDomainRuntimeFacade();
bool deviceCapacity =args.Contains("--device-capacity-refusal",StringComparer.Ordinal);
DeviceReplyLossRuntime? deviceLoss=null;
INeutralDomainRuntime rootRuntime=native;
bool mmioCapacity =args.Contains("--mmio-capacity-refusal",StringComparer.Ordinal);
bool mmioEffectLoss =args.Contains("--mmio-map-effect-loss",StringComparer.Ordinal);
bool mmioRegisters =args.Contains("--mmio-registers",StringComparer.Ordinal);
bool mmioMode =mmioCapacity || mmioEffectLoss || mmioRegisters;
bool nestedMode =args.Contains("--nested",StringComparer.Ordinal);
if(nestedMode && (mmioMode || deviceCapacity))throw new ArgumentException("--nested supports Virtual-I/O execution and loss modes.");
MmioReplyLossRuntime? mmioLoss=null;
if(mmioMode)
{
    IMmioReplyLossRuntime proxy =global::System.Reflection.DispatchProxy.Create<IMmioReplyLossRuntime,MmioReplyLossRuntime>();
    mmioLoss=(MmioReplyLossRuntime)(object)proxy;mmioLoss.Inner=native;rootRuntime=proxy;
}
if(deviceCapacity)
{
    IDeviceReplyLossRuntime proxy =global::System.Reflection.DispatchProxy.Create<IDeviceReplyLossRuntime,DeviceReplyLossRuntime>();
    deviceLoss=(DeviceReplyLossRuntime)(object)proxy;deviceLoss.Inner=native;rootRuntime=proxy;
}
VirtualIoReplyLossRuntime? responseLoss=null;
HybridCpuExecutableChildAdapter adapter =new HybridCpuExecutableChildAdapter();
bool bindLoss =args.Contains("--lost-bind-reply",StringComparer.Ordinal);
bool transferCapacity =args.Contains("--transfer-capacity",StringComparer.Ordinal);
bool transferLoss =args.Contains("--lost-transfer-reply",StringComparer.Ordinal);
bool snapshotLoss =args.Contains("--lost-snapshot-release-reply",StringComparer.Ordinal);
if(snapshotLoss && (bindLoss || transferCapacity || mmioMode || deviceCapacity))
    throw new ArgumentException("Snapshot Release loss requires the IO read path; --nested, --lost-transfer-reply and --lost-close-reply are supported.");
if(nestedMode || bindLoss || transferCapacity || transferLoss || snapshotLoss)
{
    HybridCpuExternalRuntime external =new HybridCPU.ExternalRuntime.HybridCpuExternalRuntime();
    IVirtualIoReplyLossRuntime proxy =global::System.Reflection.DispatchProxy.Create<IVirtualIoReplyLossRuntime,VirtualIoReplyLossRuntime>();
    responseLoss=(VirtualIoReplyLossRuntime)(object)proxy;responseLoss.Inner=external;
    ConstructorInfo constructor =typeof(HybridCpuExecutableChildAdapter).GetConstructor(global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic,null,
        [typeof(HybridCPU.ExternalRuntime.Contracts.IHybridCpuExternalRuntime),typeof(HybridCPU.ExternalRuntime.Contracts.IHybridCpuChildDomainRuntimeV3),typeof(ISemanticTraceSinkV1)],null)!;
    adapter=(HybridCpuExecutableChildAdapter)constructor.Invoke([external,proxy,null]);
}
RuntimeKernel kernel =new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(rootRuntime,adapter));
ProcessHandle Create(ulong id,ulong domain)
{
    SingProcessManifestV1 manifest =new SingProcessManifestV1(new(id),new(domain),1,$"virtual-io-host-{id}",ExecutionRole.Sip,MemoryProfile.SipRegion);
    Check(kernel.CreateProcess(manifest).IsSuccess,"process creation");return new(manifest.ProcessId,manifest.Generation);
}
void Check(bool value,string operation){if(!value){Console.Error.WriteLine(operation);Environment.Exit(1);}}
ProcessHandle owner =Create(2101,21010); ProcessHandle service =Create(2102,21020); ProcessHandle admin =Create(2103,21030);
CapabilityId budgetCap =kernel.MintCapability(new(21030),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
BudgetAccountHandle budget =kernel.AdmitProcessBudget(admin,budgetCap,owner,"virtual-io-host",[new(ServiceBudgetDimension.OwnedMemoryBytes,nestedMode?1032UL:520UL)]).Value!.ProcessBudget;
CapabilityId createCap =kernel.MintCapability(new(21010),owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
KernelResult<VirtualDomainAuthoritySet> created =kernel.CreateVirtualDomain(owner,createCap,new(1,4096));Check(created.IsSuccess,created.Message??"create");
VirtualDomainAuthoritySet domain =created.Value!;Check(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess,"configure");
if(nestedMode)
{
    KernelResult<VirtualDomainAuthoritySet> nested =kernel.CreateNestedVirtualDomain(owner,domain.Domain,domain.ConfigureCapability,
        new(new(1,4096),VirtualDomainAuthorityClass.Lifecycle|VirtualDomainAuthorityClass.Io));
    Check(nested.IsSuccess,nested.Message??"original nested IO factory");domain=nested.Value!;
    Check(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess,"configure nested IO child");
}
OwnedBuffer<byte>? foreignBacking =nestedMode?kernel.AllocateBuffer<byte>(service,32).Value!:null;
if(foreignBacking is not null)foreignBacking.Span[0]=0x7E;
void CheckNestedClosure()
{
    if(!nestedMode)return;
    Check(responseLoss!.NestedCreates==1,"original nested factory no-repeat");
    Check(foreignBacking!.IsValid && foreignBacking.Span[0]==0x7E,"foreign Region/data survives nested IO cleanup");
    Check(native.ActiveDeviceLeaseCount==0 && native.ActiveBindingCount==0,"nested original native device/root closure");
    Console.WriteLine("Nested IO: factory1; nativeDevices/roots0; foreignRegion32/byte7E survives.");
}
KernelResult<PlatformDomainBinding> root =kernel.QueryVirtualDomainPlatformBinding(owner,domain.Domain);Check(root.IsSuccess,root.Message??"parent");
CapabilityId deviceCap =kernel.MintCapability(new(21010),owner,ResourceKind.Device,"device/virtual-io-host",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
if(deviceCapacity)
{
    OwnedBuffer<byte> callerRegion =kernel.AllocateBuffer<byte>(owner,64).Value!; OwnedBuffer<byte> serviceRegion =kernel.AllocateBuffer<byte>(service,32).Value!;
    KernelResult<PlatformDeviceLease> independent =kernel.BindPlatformDevice(owner,root.Value,deviceCap,PlatformDeviceRights.Read|PlatformDeviceRights.Write);
    Check(independent.IsSuccess,independent.Message??"independent original device");
    CapabilityId otherCap =kernel.MintCapability(new(21010),owner,ResourceKind.Device,"device/capacity-refusal",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
    typeof(NeutralDomainRuntimeFacade).GetField("_nextResource",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.SetValue(native,ulong.MaxValue);
    deviceLoss!.Lose=args.Contains("--lost-device-reply",StringComparer.Ordinal);
    KernelResult<PlatformDeviceLease> refused =kernel.BindPlatformDevice(owner,root.Value,otherCap,PlatformDeviceRights.Read);
    Check(!refused.IsSuccess,"capacity device not published");
    Check(deviceLoss.DeviceBinds==2 && native.ActiveDeviceLeaseCount==1,"independent device/refusal once");
    Check(kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==64,"caller charge retained until cleanup");
    if(deviceLoss.Lose)
    {
        Check(!kernel.TerminateProcess(owner).IsSuccess,"unknown device outcome pins caller");
        Check(native.ActiveDeviceLeaseCount==1 && kernel.Regions.Snapshot().Single(r=>r.Handle==callerRegion.Handle).State==RegionState.Owned,"independent device/backing retained");
        deviceLoss.Lose=false;
    }
    KernelResult deviceTeardown =kernel.TerminateProcess(owner);Check(deviceTeardown.IsSuccess,deviceTeardown.Message??"original device no-effect cleanup");
    Check(deviceLoss.DeviceBinds==2 && native.ActiveDeviceLeaseCount==0 && native.ActiveBindingCount==0,"device no-repeat/exact root closure");
    Check(kernel.QueryBudget(budget).Value!.Usage.All(u=>u.Used==0),"device caller quantitative reclaim");
    Check(kernel.Regions.Snapshot().Single(r=>r.Handle==callerRegion.Handle).State==RegionState.Released && kernel.Regions.Snapshot().Single(r=>r.Handle==serviceRegion.Handle).State==RegionState.Owned,"caller reclaimed/foreign backing alive");
    Console.WriteLine("Device capacity: nativeBind2 (independent1/refusal1); no repeat; nativeDevices/roots0; charges64->0; callerRegionReleased; serviceRegionOwned; gatesOFF; ISA NONE; ProductionQualified=false");
    return;
}
KernelResult<PlatformDeviceLease> admitted =kernel.BindPlatformDevice(owner,root.Value,deviceCap,PlatformDeviceRights.Read|PlatformDeviceRights.Write|(mmioMode?PlatformDeviceRights.Configure:PlatformDeviceRights.None));Check(admitted.IsSuccess,admitted.Message??"device");
PlatformDeviceLease device =admitted.Value!;
if(mmioRegisters)
{
    OwnedBuffer<byte> buffer =kernel.AllocateBuffer<byte>(owner,64).Value!;
    OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(service,32).Value!;
    CapabilityId cap =kernel.MintCapability(new(21010),owner,ResourceKind.MmioRegion,CapabilityResourceIds.MmioRegion("device/virtual-io-host","registers",64),CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Write).Value!.CapabilityId;
    KernelResult<PlatformMmioLease> mapped =kernel.BindPlatformMmioBacking(owner,device,cap,0,64,PlatformMmioAccess.Read|PlatformMmioAccess.Write,buffer);
    Check(mapped.IsSuccess,mapped.Message??"original register backing Map"); PlatformMmioLease map =mapped.Value;
    RegionUseDescriptor use =kernel.Regions.SnapshotUses().Single(u=>u.Region==buffer.Handle);
    Check(!kernel.ReleaseRegionUse(owner,use.Handle).IsSuccess,"MMIO owns use release");
    PlatformMmio32Invocation command =new PlatformMmio32Invocation(map,0,true,17);
    mmioLoss!.LoseAccess=args.Contains("--lost-mmio-write-reply",StringComparer.Ordinal);
    KernelResult<uint> registerWritten =kernel.AccessPlatformMmio32(owner,command);
    if(mmioLoss.LoseAccess)
    {
        Check(!registerWritten.IsSuccess && mmioLoss.Writes==1,"original command reply unknown");
        Check(!kernel.AccessPlatformMmio32(owner,command).IsSuccess && !kernel.ResolvePlatformMmio32(owner,command).IsSuccess,"unknown original lookup only");
        Check(mmioLoss.Writes==1 && kernel.Regions.SnapshotUses().Single(u=>u.Handle==use.Handle).State==RegionUseState.Active,"write no repeat/use retained");
        mmioLoss.LoseAccess=false;
        KernelResult<uint> known =kernel.ResolvePlatformMmio32(owner,command);Check(known.IsSuccess && known.Value==17,known.Message??"same-original command query");
    }
    else Check(registerWritten.IsSuccess && registerWritten.Value==17,registerWritten.Message??"command write");
    PlatformMmio32Invocation status =new PlatformMmio32Invocation(map,4,true,42);
    KernelResult<uint> statusWritten =kernel.AccessPlatformMmio32(owner,status);Check(statusWritten.IsSuccess,statusWritten.Message??"status write");
    PlatformMmio32Invocation readInvocation =new PlatformMmio32Invocation(map,4,false);
    mmioLoss.LoseAccess=args.Contains("--lost-mmio-read-reply",StringComparer.Ordinal);
    KernelResult<uint> registerRead =kernel.AccessPlatformMmio32(owner,readInvocation);
    if(mmioLoss.LoseAccess)
    {
        Check(!registerRead.IsSuccess && !kernel.AccessPlatformMmio32(owner,readInvocation).IsSuccess && mmioLoss.Reads==1,"unknown original read never repeats");
        Check(!kernel.ResolvePlatformMmio32(owner,readInvocation).IsSuccess,"read query unknown");
        mmioLoss.LoseAccess=false;registerRead=kernel.ResolvePlatformMmio32(owner,readInvocation);
    }
    Check(registerRead.IsSuccess && registerRead.Value==42,registerRead.Message??"status read");
    Check(mmioLoss.Writes==2 && mmioLoss.Reads==1,"native register effects exactly once");
    if(args.Contains("--lost-mmio-close-reply",StringComparer.Ordinal))
    {
        mmioLoss.LoseClose=true;Check(!kernel.TerminateProcess(owner).IsSuccess,"unknown register Close pins caller");
        Check(kernel.Regions.SnapshotUses().Single(u=>u.Handle==use.Handle).State!=RegionUseState.Released && kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==64,"unknown Close retains original backing/charge");
        mmioLoss.LoseClose=false;
    }
    KernelResult ended =kernel.TerminateProcess(owner);Check(ended.IsSuccess,ended.Message??"original register backing closure");
    Check(mmioLoss.MmioMaps==1 && mmioLoss.MmioCloses==1 && mmioLoss.Writes==2 && mmioLoss.Reads==1,"no effect repeat during backing cleanup");
    Check(kernel.Regions.SnapshotUses().Single(u=>u.Handle==use.Handle).State==RegionUseState.Released && kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State==RegionState.Released,"exact use/Region reclaim");
    Check(kernel.QueryBudget(budget).Value!.Usage.All(u=>u.Used==0) && kernel.Regions.Snapshot().Single(r=>r.Handle==foreign.Handle).State==RegionState.Owned,"charges64->0/foreign survival");
    Console.WriteLine("MMIO registers: command17/status42/read42; Map1/Write2/Read1/Close1; original use/backing/Region released; charges64->0; foreignRegionOwned; ModelOnly; gatesOFF; ISA NONE; ProductionQualified=false");return;
}
if(mmioMode)
{
    OwnedBuffer<byte> callerRegion =kernel.AllocateBuffer<byte>(owner,64).Value!;
    OwnedBuffer<byte> foreignRegion =kernel.AllocateBuffer<byte>(service,32).Value!;
    CapabilityId Cap(string region)=>kernel.MintCapability(new(21010),owner,ResourceKind.MmioRegion,
        CapabilityResourceIds.MmioRegion("device/virtual-io-host",region,128),CapabilityRights.Map|CapabilityRights.Read).Value!.CapabilityId;
    KernelResult<PlatformMmioLease> peer =kernel.BindPlatformMmio(owner,device,Cap("peer"),0,64,PlatformMmioAccess.Read);
    Check(peer.IsSuccess,peer.Message??"independent MMIO");
    if(mmioCapacity)typeof(NeutralDomainRuntimeFacade).GetField("_nextResource",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.SetValue(native,ulong.MaxValue);
    mmioLoss!.Lose=mmioEffectLoss || args.Contains("--lost-mmio-map-reply",StringComparer.Ordinal);
    Check(!kernel.BindPlatformMmio(owner,device,Cap("refusal"),0,64,PlatformMmioAccess.Read).IsSuccess,"MMIO capacity not published");
    int expectedActive =mmioEffectLoss?2:1;
    Check(mmioLoss.MmioMaps==2 && native.ActiveMmioLeaseCount==expectedActive,"MMIO independent/admission once");
    if(mmioLoss.Lose)
    {
        Check(!kernel.TerminateProcess(owner).IsSuccess,"unknown MMIO pins caller");
        Check(native.ActiveMmioLeaseCount==expectedActive && kernel.Regions.Snapshot().Single(r=>r.Handle==callerRegion.Handle).State==RegionState.Owned,"unknown MMIO retains backing");
        Check(kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==64,"unknown MMIO retains charge");
        mmioLoss.Lose=false;
    }
    if(args.Contains("--lost-mmio-close-reply",StringComparer.Ordinal))
    {
        mmioLoss.LoseClose=true;
        Check(!kernel.TerminateProcess(owner).IsSuccess,"unknown original Close retains caller");
        Check(mmioLoss.MmioCloses>=1 && mmioLoss.MmioCloses<=expectedActive && native.ActiveMmioLeaseCount==expectedActive-mmioLoss.MmioCloses,"one original Close effect retained");
        Check(kernel.Regions.Snapshot().Single(r=>r.Handle==callerRegion.Handle).State==RegionState.Owned && kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==64,"unknown Close retains Region/charge");
        mmioLoss.LoseClose=false;
    }
    KernelResult closed =kernel.TerminateProcess(owner);Check(closed.IsSuccess,closed.Message??"same original MMIO cleanup");
    Check(mmioLoss.MmioCloses==expectedActive,"original Close does not repeat");
    Check(mmioLoss.MmioMaps==2 && native.ActiveMmioLeaseCount==0 && native.ActiveDeviceLeaseCount==0 && native.ActiveBindingCount==0,"MMIO no repeat/exact closure");
    Check(kernel.QueryBudget(budget).Value!.Usage.All(u=>u.Used==0),"MMIO quantitative reclaim");
    Check(kernel.Regions.Snapshot().Single(r=>r.Handle==callerRegion.Handle).State==RegionState.Released && kernel.Regions.Snapshot().Single(r=>r.Handle==foreignRegion.Handle).State==RegionState.Owned,"MMIO caller reclaim/foreign survival");
    Console.WriteLine($"MMIO {(mmioEffectLoss?"effect loss":"capacity")}: Map2; Close{mmioLoss.MmioCloses}; no repeat; nativeMMIO/device/root0; charges64->0; callerRegionReleased; foreignRegionOwned; gatesOFF; ISA NONE; ProductionQualified=false");
    return;
}
ProtocolDefinitionV1 protocol =VirtualizationServiceClient.CreateProtocol();
ServiceEndpointDescriptor descriptor =kernel.RegisterService(service,"virtual-io",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
EndpointSessionHandle session =kernel.OpenSession(owner,descriptor,[]).Value;
RuntimeVirtualizationServiceHost host =RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
IVirtualizationService client =VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
async Task<T> Dispatch<T>(ValueTask<T> pending){ KernelResult result =host.ProcessNext();Check(result.IsSuccess,result.Message??"dispatch");return await pending;}
ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
if(bindLoss)
{
    responseLoss!.LoseBindResponses=true;
    ValueTask<VirtualIoHandleResponse> pending =client.BindIoAsync(new(domain.Domain,device.LeaseId.Value,device.Generation.Value,VirtualIoAccess.Read|VirtualIoAccess.Write,512));
    Check(!host.ProcessNext().IsSuccess,"unknown IO Bind not published");
    bool denied =false;try{await pending;}catch(Exception){denied=true;}Check(denied,"failed accepted Bind response");
    Check(Used()==512 && responseLoss.BindCalls==1,"original backing effect retained once");
    responseLoss.LoseBindResponses=false;
    if(args.Contains("--lost-close-reply",StringComparer.Ordinal))
    {
        responseLoss.LoseContainmentResponses=true;
        Check(!kernel.TerminateProcess(owner).IsSuccess,"unknown backing Close retains caller");
        Check(Used()==512 && native.ActiveDeviceLeaseCount==1,"unknown Close retains charge/native loan");
        responseLoss.LoseContainmentResponses=false;
    }
    Check(kernel.TerminateProcess(owner).IsSuccess,"original IO Bind caller teardown");
    Check(Used()==0 && responseLoss.BindCalls==1 && responseLoss.ContainmentCalls==1 && responseLoss.TransferCalls==0,"exact IO Bind cleanup no-repeat");
    Check(native.ActiveDeviceLeaseCount==0 && native.ActiveBindingCount==0,"native loan/root closure");
    Check(kernel.Processes.Resolve(service).IsSuccess,"service survives IO Bind cleanup");
    CheckNestedClosure();
    Console.WriteLine("Virtual-I/O Bind loss: Bind1/Contain1/Transfer0; charge512->0; nativeDevices/roots0; caller closed; service alive; gatesOFF; ISA NONE; ProductionQualified=false");
    return;
}
VirtualIoHandleResponse io =await Dispatch(client.BindIoAsync(new(domain.Domain,device.LeaseId.Value,device.Generation.Value,VirtualIoAccess.Read|VirtualIoAccess.Write,512)));
Check(Used()==512,"backing charge");
if(transferCapacity)
{
    OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x6D;
    for(int index =0;index<256;index++)
    {
        VirtualIoTransferResponse completed =await Dispatch(client.TransferIoAsync(new(io.IoId,io.Generation,0,true,1,new BoundedBytes(new byte[]{(byte)index}))));
        Check(completed.Completed,"bounded write completion");
    }
    foreach(bool write in new[]{true,false})
    {
        ValueTask<VirtualIoTransferResponse> pending =client.TransferIoAsync(new(io.IoId,io.Generation,0,write,1,write?new BoundedBytes(new byte[]{0xEE}):default));
        KernelResult refused =host.ProcessNext();Check(!refused.IsSuccess && refused.Error==KernelError.CapacityExhausted,"trusted local capacity refusal");
        bool denied =false;try{await pending;}catch(Exception){denied=true;}Check(denied,"capacity response consumed");
        Check(host.AdvancePendingResult().IsSuccess,"original refusal custody handoff");
        Check(responseLoss!.TransferCalls==256 && Used()==512,"no extra native transfer or snapshot charge");
    }
    ValueTask closingCapacity =client.CloseIoAsync(new(io.IoId,io.Generation));
    Check(host.ProcessNext().IsSuccess,"capacity IO close");await closingCapacity;
    Check(responseLoss!.CloseCalls==1 && Used()==0,"exact capacity backing release");
    Check(kernel.TerminateProcess(owner).IsSuccess,"capacity caller teardown");
    Check(native.ActiveDeviceLeaseCount==0 && native.ActiveBindingCount==0,"capacity native loan/root reclaim");
    Check(foreign.Span[0]==0x6D && kernel.Processes.Resolve(service).IsSuccess,"capacity foreign survives");
    CheckNestedClosure();
    Console.WriteLine("Virtual-I/O capacity: Transfer256; local write/read refusals2; Close1; charge512->0; nativeDevices/roots0; foreign0x6D; gatesOFF; ISA NONE; ProductionQualified=false");
    return;
}
VirtualIoTransferResponse written =await Dispatch(client.TransferIoAsync(new(io.IoId,io.Generation,4,true,4,new BoundedBytes(new byte[]{17,19,23,29}))));Check(written.Completed,"write");
if(snapshotLoss)
{
    OwnedBuffer<byte> foreign =kernel.AllocateBuffer<byte>(service,32).Value!;foreign.Span[0]=0x7E;
    responseLoss!.LoseSnapshotReleaseResponses=true;
    responseLoss.LoseTransferResponses=transferLoss;
    ValueTask<VirtualIoTransferResponse> pending =client.TransferIoAsync(new(io.IoId,io.Generation,4,false,4,default));
    Check(!host.ProcessNext().IsSuccess,"unknown snapshot Release is not response publication");
    bool denied =false;try{await pending;}catch(Exception){denied=true;}Check(denied,"failed accepted read response");
    Check(Used()==516 && responseLoss.TransferCalls==2 && responseLoss.SnapshotReleaseCalls==(transferLoss?0:1),"original unknown read/Release retains snapshot charge");
    if(transferLoss)
    {
        Check(!host.AdvancePendingResult().IsSuccess && Used()==516 && responseLoss.SnapshotReleaseCalls==0,"unknown transfer cannot release an unvalidated snapshot");
        responseLoss.LoseTransferResponses=false;
        Check(!host.AdvancePendingResult().IsSuccess && responseLoss.TransferCalls==2 && responseLoss.SnapshotReleaseCalls==1 && Used()==516,"original transfer query progresses once to unknown Release");
    }
    Check(!host.AdvancePendingResult().IsSuccess && Used()==516 && responseLoss.SnapshotReleaseCalls==1,"query loss never repeats Release");
    Check(native.ActiveDeviceLeaseCount==1 && responseLoss.CloseCalls==0,"independent IO backing stays live");
    responseLoss.LoseSnapshotReleaseResponses=false;
    Check(host.AdvancePendingResult().IsSuccess,"same-original snapshot cleanup");
    Check(Used()==512 && responseLoss.SnapshotReleaseCalls==1 && responseLoss.TransferCalls==2 && responseLoss.CloseCalls==0,"snapshot reclaim is independent of IO backing");
    if(args.Contains("--lost-close-reply",StringComparer.Ordinal))
    {
        responseLoss.LoseCloseResponses=true;
        Check(!kernel.TerminateProcess(owner).IsSuccess && Used()==512 && responseLoss.CloseCalls==1,"unknown original IO closure retains backing charge");
        responseLoss.LoseCloseResponses=false;
    }
    Check(kernel.TerminateProcess(owner).IsSuccess,"original caller teardown after snapshot cleanup");
    Check(Used()==0 && responseLoss.CloseCalls==1 && responseLoss.SnapshotReleaseCalls==1 && responseLoss.TransferCalls==2,"exact independent reclaim without replay");
    Check(native.ActiveDeviceLeaseCount==0 && native.ActiveBindingCount==0,"native loan/root retired");
    Check(foreign.IsValid && foreign.Span[0]==0x7E && kernel.Regions.Snapshot().Single(r=>r.Handle==foreign.Handle).State==RegionState.Owned,"foreign Region survives");
    CheckNestedClosure();
    Console.WriteLine("Virtual-I/O snapshot Release loss: Transfer2/Release1/Close1; charge512->516->512->0; same-original cleanup; nativeDevices/roots0; foreignRegion7E Owned; gatesOFF; ISA NONE; ProductionQualified=false");
    return;
}
if(transferLoss)
{
    responseLoss!.LoseTransferResponses=true;
    ValueTask<VirtualIoTransferResponse> pending =client.TransferIoAsync(new(io.IoId,io.Generation,4,false,4,default));
    Check(!host.ProcessNext().IsSuccess,"lost read is not publication");
    bool denied =false;try{await pending;}catch(Exception){denied=true;}
    Check(denied,"failed response observed");
    Check(responseLoss.TransferCalls==2 && Used()==516,"one write/read and retained snapshot charge");
    responseLoss.LoseTransferResponses=false;
    Check(host.AdvancePendingResult().IsSuccess,"query-only canceled read snapshot cleanup");
    Check(Used()==512 && responseLoss.TransferCalls==2,"snapshot released without transfer repeat or IO Close");
    if(args.Contains("--lost-close-reply",StringComparer.Ordinal))
    {
        responseLoss.LoseCloseResponses=true;
        Check(!kernel.TerminateProcess(owner).IsSuccess,"unknown Close retains caller lifetime");
        Check(Used()==512 && responseLoss.CloseCalls==1,"unknown Close retains independent backing/snapshot charges");
        Check(native.ActiveDeviceLeaseCount==1,"native loan remains pinned until exact closure");
        responseLoss.LoseCloseResponses=false;
    }
    Check(kernel.TerminateProcess(owner).IsSuccess,"original transfer automatic caller teardown");
    Check(responseLoss.TransferCalls==2 && responseLoss.CloseCalls==1,"teardown never repeats transfer or Close");
    Check(Used()==0 && native.ActiveDeviceLeaseCount==0 && native.ActiveBindingCount==0,"exact backing charge/device/root reclaim");
    Check(kernel.Processes.Resolve(service).IsSuccess,"independent service survives");
    CheckNestedClosure();
    Console.WriteLine("Virtual-I/O lost transfer: write1/read1; unpublished snapshot; charge=512->516->512->0; query-only snapshot release; Close1; nativeDevices/roots=0; service survives; gates=OFF; ISA=NONE; ProductionQualified=false");
    return;
}
VirtualIoTransferResponse read =await Dispatch(client.TransferIoAsync(new(io.IoId,io.Generation,4,false,4,default)));Check(read.Completed && read.Data.ToArray().SequenceEqual(new byte[]{17,19,23,29}),"read bytes");
Check(Used()==512,"exact native read snapshot release after bounded response copy; IO backing remains");
if(nestedMode)
{
    CapabilityId peerCap =kernel.MintCapability(new(21010),owner,ResourceKind.Device,"device/nested-io-peer",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
    KernelResult<PlatformDeviceLease> peerDevice =kernel.BindPlatformDevice(owner,root.Value,peerCap,PlatformDeviceRights.Read|PlatformDeviceRights.Write);
    Check(peerDevice.IsSuccess,peerDevice.Message??"independent nested device");
    VirtualIoHandleResponse peer =await Dispatch(client.BindIoAsync(new(domain.Domain,peerDevice.Value!.LeaseId.Value,peerDevice.Value.Generation.Value,VirtualIoAccess.Read|VirtualIoAccess.Write,512)));
    await Dispatch(client.TransferIoAsync(new(peer.IoId,peer.Generation,0,true,1,new BoundedBytes(new byte[]{0x6C}))));
    Check(Used()==1024,"two independent nested IO backings");
    ValueTask closeFirst =client.CloseIoAsync(new(io.IoId,io.Generation));Check(host.ProcessNext().IsSuccess,"first nested IO Close");await closeFirst;
    Check(Used()==512,"first closure retains independent IO charge");
    VirtualIoTransferResponse peerRead =await Dispatch(client.TransferIoAsync(new(peer.IoId,peer.Generation,0,false,1,default)));
    Check(peerRead.Completed && peerRead.Data.ToArray().SequenceEqual(new byte[]{0x6C}) && Used()==512,"independent IO backing remains usable");
    ValueTask closePeer =client.CloseIoAsync(new(peer.IoId,peer.Generation));Check(host.ProcessNext().IsSuccess,"independent nested IO Close");await closePeer;
    Check(Used()==0 && responseLoss!.BindCalls==2 && responseLoss.CloseCalls==2 && responseLoss.TransferCalls==4,"exact independent IO cleanup/effect counts");
    Check(kernel.CloseSession(owner,session).IsSuccess && kernel.TerminateProcess(owner).IsSuccess,"nested caller cleanup");
    CheckNestedClosure();
    Console.WriteLine("Nested IO independent backing: Bind2/Transfer4/Close2; charge512->1024->512->0; peer6C survives first Close; bytes17/19/23/29; gatesOFF; ISA NONE; ProductionQualified=false");
    return;
}
ValueTask closing =client.CloseIoAsync(new(io.IoId,io.Generation));Check(host.ProcessNext().IsSuccess,"IO Close dispatch");await closing;
Check(Used()==0,"exact backing/snapshot reclaim");Check(kernel.CloseSession(owner,session).IsSuccess,"session Close");Check(kernel.TerminateProcess(owner).IsSuccess,"process cleanup");
Check(native.ActiveDeviceLeaseCount==0,"native device release");
Console.WriteLine("Virtual-I/O software host: bytes=17,19,23,29; snapshot released independently; backing charge=512->512->0; nativeDevices=0; gates=OFF; ISA=NONE; ProductionQualified=false");

