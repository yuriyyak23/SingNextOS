using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Sip.Native;
using SingPlus.Sip.Virtualization;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

var native=new NeutralDomainRuntimeFacade();
var deviceCapacity=args.Contains("--device-capacity-refusal",StringComparer.Ordinal);
DeviceReplyLossRuntime? deviceLoss=null;
INeutralDomainRuntime rootRuntime=native;
var mmioCapacity=args.Contains("--mmio-capacity-refusal",StringComparer.Ordinal);
var mmioEffectLoss=args.Contains("--mmio-map-effect-loss",StringComparer.Ordinal);
var mmioRegisters=args.Contains("--mmio-registers",StringComparer.Ordinal);
var mmioMode=mmioCapacity || mmioEffectLoss || mmioRegisters;
MmioReplyLossRuntime? mmioLoss=null;
if(mmioMode)
{
    var proxy=global::System.Reflection.DispatchProxy.Create<IMmioReplyLossRuntime,MmioReplyLossRuntime>();
    mmioLoss=(MmioReplyLossRuntime)(object)proxy;mmioLoss.Inner=native;rootRuntime=proxy;
}
if(deviceCapacity)
{
    var proxy=global::System.Reflection.DispatchProxy.Create<IDeviceReplyLossRuntime,DeviceReplyLossRuntime>();
    deviceLoss=(DeviceReplyLossRuntime)(object)proxy;deviceLoss.Inner=native;rootRuntime=proxy;
}
VirtualIoReplyLossRuntime? responseLoss=null;
var adapter=new HybridCpuExecutableChildAdapter();
var bindLoss=args.Contains("--lost-bind-reply",StringComparer.Ordinal);
if(bindLoss || args.Contains("--lost-transfer-reply",StringComparer.Ordinal))
{
    var external=new HybridCPU.ExternalRuntime.HybridCpuExternalRuntime();
    var proxy=global::System.Reflection.DispatchProxy.Create<IVirtualIoReplyLossRuntime,VirtualIoReplyLossRuntime>();
    responseLoss=(VirtualIoReplyLossRuntime)(object)proxy;responseLoss.Inner=external;
    var constructor=typeof(HybridCpuExecutableChildAdapter).GetConstructor(global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic,null,
        [typeof(HybridCPU.ExternalRuntime.Contracts.IHybridCpuExternalRuntime),typeof(HybridCPU.ExternalRuntime.Contracts.IHybridCpuChildDomainRuntimeV3),typeof(ISemanticTraceSinkV1)],null)!;
    adapter=(HybridCpuExecutableChildAdapter)constructor.Invoke([external,proxy,null]);
}
var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(rootRuntime,adapter));
ProcessHandle Create(ulong id,ulong domain)
{
    var manifest=new SingProcessManifestV1(new(id),new(domain),1,$"virtual-io-host-{id}",ExecutionRole.Sip,MemoryProfile.SipRegion);
    Check(kernel.CreateProcess(manifest).IsSuccess,"process creation");return new(manifest.ProcessId,manifest.Generation);
}
void Check(bool value,string operation){if(!value){Console.Error.WriteLine(operation);Environment.Exit(1);}}
var owner=Create(2101,21010);var service=Create(2102,21020);var admin=Create(2103,21030);
var budgetCap=kernel.MintCapability(new(21030),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
var budget=kernel.AdmitProcessBudget(admin,budgetCap,owner,"virtual-io-host",[new(ServiceBudgetDimension.OwnedMemoryBytes,520)]).Value!.ProcessBudget;
var createCap=kernel.MintCapability(new(21010),owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
var created=kernel.CreateVirtualDomain(owner,createCap,new(1,4096));Check(created.IsSuccess,created.Message??"create");
var domain=created.Value!;Check(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess,"configure");
var root=kernel.QueryVirtualDomainPlatformBinding(owner,domain.Domain);Check(root.IsSuccess,root.Message??"parent");
var deviceCap=kernel.MintCapability(new(21010),owner,ResourceKind.Device,"device/virtual-io-host",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
if(deviceCapacity)
{
    var callerRegion=kernel.AllocateBuffer<byte>(owner,64).Value!;var serviceRegion=kernel.AllocateBuffer<byte>(service,32).Value!;
    var independent=kernel.BindPlatformDevice(owner,root.Value,deviceCap,PlatformDeviceRights.Read|PlatformDeviceRights.Write);
    Check(independent.IsSuccess,independent.Message??"independent original device");
    var otherCap=kernel.MintCapability(new(21010),owner,ResourceKind.Device,"device/capacity-refusal",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
    typeof(NeutralDomainRuntimeFacade).GetField("_nextResource",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.SetValue(native,ulong.MaxValue);
    deviceLoss!.Lose=args.Contains("--lost-device-reply",StringComparer.Ordinal);
    var refused=kernel.BindPlatformDevice(owner,root.Value,otherCap,PlatformDeviceRights.Read);
    Check(!refused.IsSuccess,"capacity device not published");
    Check(deviceLoss.DeviceBinds==2 && native.ActiveDeviceLeaseCount==1,"independent device/refusal once");
    Check(kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==64,"caller charge retained until cleanup");
    if(deviceLoss.Lose)
    {
        Check(!kernel.TerminateProcess(owner).IsSuccess,"unknown device outcome pins caller");
        Check(native.ActiveDeviceLeaseCount==1 && kernel.Regions.Snapshot().Single(r=>r.Handle==callerRegion.Handle).State==RegionState.Owned,"independent device/backing retained");
        deviceLoss.Lose=false;
    }
    var deviceTeardown=kernel.TerminateProcess(owner);Check(deviceTeardown.IsSuccess,deviceTeardown.Message??"original device no-effect cleanup");
    Check(deviceLoss.DeviceBinds==2 && native.ActiveDeviceLeaseCount==0 && native.ActiveBindingCount==0,"device no-repeat/exact root closure");
    Check(kernel.QueryBudget(budget).Value!.Usage.All(u=>u.Used==0),"device caller quantitative reclaim");
    Check(kernel.Regions.Snapshot().Single(r=>r.Handle==callerRegion.Handle).State==RegionState.Released && kernel.Regions.Snapshot().Single(r=>r.Handle==serviceRegion.Handle).State==RegionState.Owned,"caller reclaimed/foreign backing alive");
    Console.WriteLine("Device capacity: nativeBind2 (independent1/refusal1); no repeat; nativeDevices/roots0; charges64->0; callerRegionReleased; serviceRegionOwned; gatesOFF; ISA NONE; ProductionQualified=false");
    return;
}
var admitted=kernel.BindPlatformDevice(owner,root.Value,deviceCap,PlatformDeviceRights.Read|PlatformDeviceRights.Write|(mmioMode?PlatformDeviceRights.Configure:PlatformDeviceRights.None));Check(admitted.IsSuccess,admitted.Message??"device");
var device=admitted.Value!;
if(mmioRegisters)
{
    var buffer=kernel.AllocateBuffer<byte>(owner,64).Value!;
    var foreign=kernel.AllocateBuffer<byte>(service,32).Value!;
    var cap=kernel.MintCapability(new(21010),owner,ResourceKind.MmioRegion,CapabilityResourceIds.MmioRegion("device/virtual-io-host","registers",64),CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Write).Value!.CapabilityId;
    var mapped=kernel.BindPlatformMmioBacking(owner,device,cap,0,64,PlatformMmioAccess.Read|PlatformMmioAccess.Write,buffer);
    Check(mapped.IsSuccess,mapped.Message??"original register backing Map");var map=mapped.Value;
    var use=kernel.Regions.SnapshotUses().Single(u=>u.Region==buffer.Handle);
    Check(!kernel.ReleaseRegionUse(owner,use.Handle).IsSuccess,"MMIO owns use release");
    var command=new PlatformMmio32Invocation(map,0,true,17);
    mmioLoss!.LoseAccess=args.Contains("--lost-mmio-write-reply",StringComparer.Ordinal);
    var registerWritten=kernel.AccessPlatformMmio32(owner,command);
    if(mmioLoss.LoseAccess)
    {
        Check(!registerWritten.IsSuccess && mmioLoss.Writes==1,"original command reply unknown");
        Check(!kernel.AccessPlatformMmio32(owner,command).IsSuccess && !kernel.ResolvePlatformMmio32(owner,command).IsSuccess,"unknown original lookup only");
        Check(mmioLoss.Writes==1 && kernel.Regions.SnapshotUses().Single(u=>u.Handle==use.Handle).State==RegionUseState.Active,"write no repeat/use retained");
        mmioLoss.LoseAccess=false;
        var known=kernel.ResolvePlatformMmio32(owner,command);Check(known.IsSuccess && known.Value==17,known.Message??"same-original command query");
    }
    else Check(registerWritten.IsSuccess && registerWritten.Value==17,registerWritten.Message??"command write");
    var status=new PlatformMmio32Invocation(map,4,true,42);
    var statusWritten=kernel.AccessPlatformMmio32(owner,status);Check(statusWritten.IsSuccess,statusWritten.Message??"status write");
    var readInvocation=new PlatformMmio32Invocation(map,4,false);
    mmioLoss.LoseAccess=args.Contains("--lost-mmio-read-reply",StringComparer.Ordinal);
    var registerRead=kernel.AccessPlatformMmio32(owner,readInvocation);
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
    var ended=kernel.TerminateProcess(owner);Check(ended.IsSuccess,ended.Message??"original register backing closure");
    Check(mmioLoss.MmioMaps==1 && mmioLoss.MmioCloses==1 && mmioLoss.Writes==2 && mmioLoss.Reads==1,"no effect repeat during backing cleanup");
    Check(kernel.Regions.SnapshotUses().Single(u=>u.Handle==use.Handle).State==RegionUseState.Released && kernel.Regions.Snapshot().Single(r=>r.Handle==buffer.Handle).State==RegionState.Released,"exact use/Region reclaim");
    Check(kernel.QueryBudget(budget).Value!.Usage.All(u=>u.Used==0) && kernel.Regions.Snapshot().Single(r=>r.Handle==foreign.Handle).State==RegionState.Owned,"charges64->0/foreign survival");
    Console.WriteLine("MMIO registers: command17/status42/read42; Map1/Write2/Read1/Close1; original use/backing/Region released; charges64->0; foreignRegionOwned; ModelOnly; gatesOFF; ISA NONE; ProductionQualified=false");return;
}
if(mmioMode)
{
    var callerRegion=kernel.AllocateBuffer<byte>(owner,64).Value!;
    var foreignRegion=kernel.AllocateBuffer<byte>(service,32).Value!;
    CapabilityId Cap(string region)=>kernel.MintCapability(new(21010),owner,ResourceKind.MmioRegion,
        CapabilityResourceIds.MmioRegion("device/virtual-io-host",region,128),CapabilityRights.Map|CapabilityRights.Read).Value!.CapabilityId;
    var peer=kernel.BindPlatformMmio(owner,device,Cap("peer"),0,64,PlatformMmioAccess.Read);
    Check(peer.IsSuccess,peer.Message??"independent MMIO");
    if(mmioCapacity)typeof(NeutralDomainRuntimeFacade).GetField("_nextResource",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.SetValue(native,ulong.MaxValue);
    mmioLoss!.Lose=mmioEffectLoss || args.Contains("--lost-mmio-map-reply",StringComparer.Ordinal);
    Check(!kernel.BindPlatformMmio(owner,device,Cap("refusal"),0,64,PlatformMmioAccess.Read).IsSuccess,"MMIO capacity not published");
    var expectedActive=mmioEffectLoss?2:1;
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
    var closed=kernel.TerminateProcess(owner);Check(closed.IsSuccess,closed.Message??"same original MMIO cleanup");
    Check(mmioLoss.MmioCloses==expectedActive,"original Close does not repeat");
    Check(mmioLoss.MmioMaps==2 && native.ActiveMmioLeaseCount==0 && native.ActiveDeviceLeaseCount==0 && native.ActiveBindingCount==0,"MMIO no repeat/exact closure");
    Check(kernel.QueryBudget(budget).Value!.Usage.All(u=>u.Used==0),"MMIO quantitative reclaim");
    Check(kernel.Regions.Snapshot().Single(r=>r.Handle==callerRegion.Handle).State==RegionState.Released && kernel.Regions.Snapshot().Single(r=>r.Handle==foreignRegion.Handle).State==RegionState.Owned,"MMIO caller reclaim/foreign survival");
    Console.WriteLine($"MMIO {(mmioEffectLoss?"effect loss":"capacity")}: Map2; Close{mmioLoss.MmioCloses}; no repeat; nativeMMIO/device/root0; charges64->0; callerRegionReleased; foreignRegionOwned; gatesOFF; ISA NONE; ProductionQualified=false");
    return;
}
var protocol=VirtualizationServiceClient.CreateProtocol();
var descriptor=kernel.RegisterService(service,"virtual-io",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,VirtualizationServiceClient.ResponseProtocol,[]).Value!;
var session=kernel.OpenSession(owner,descriptor,[]).Value;
var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,service,session).Value!;
var client=VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
async Task<T> Dispatch<T>(ValueTask<T> pending){var result=host.ProcessNext();Check(result.IsSuccess,result.Message??"dispatch");return await pending;}
ulong Used()=>kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
if(bindLoss)
{
    responseLoss!.LoseBindResponses=true;
    var pending=client.BindIoAsync(new(domain.Domain,device.LeaseId.Value,device.Generation.Value,VirtualIoAccess.Read|VirtualIoAccess.Write,512));
    Check(!host.ProcessNext().IsSuccess,"unknown IO Bind not published");
    var denied=false;try{await pending;}catch(Exception){denied=true;}Check(denied,"failed accepted Bind response");
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
    Console.WriteLine("Virtual-I/O Bind loss: Bind1/Contain1/Transfer0; charge512->0; nativeDevices/roots0; caller closed; service alive; gatesOFF; ISA NONE; ProductionQualified=false");
    return;
}
var io=await Dispatch(client.BindIoAsync(new(domain.Domain,device.LeaseId.Value,device.Generation.Value,VirtualIoAccess.Read|VirtualIoAccess.Write,512)));
Check(Used()==512,"backing charge");
var written=await Dispatch(client.TransferIoAsync(new(io.IoId,io.Generation,4,true,4,new BoundedBytes(new byte[]{17,19,23,29}))));Check(written.Completed,"write");
if(responseLoss is not null)
{
    responseLoss.LoseTransferResponses=true;
    var pending=client.TransferIoAsync(new(io.IoId,io.Generation,4,false,4,default));
    Check(!host.ProcessNext().IsSuccess,"lost read is not publication");
    var denied=false;try{await pending;}catch(Exception){denied=true;}
    Check(denied,"failed response observed");
    Check(responseLoss.TransferCalls==2 && Used()==516,"one write/read and retained snapshot charge");
    responseLoss.LoseTransferResponses=false;
    if(args.Contains("--lost-close-reply",StringComparer.Ordinal))
    {
        responseLoss.LoseCloseResponses=true;
        Check(!kernel.TerminateProcess(owner).IsSuccess,"unknown Close retains caller lifetime");
        Check(Used()==516 && responseLoss.CloseCalls==1,"unknown Close retains independent backing/snapshot charges");
        Check(native.ActiveDeviceLeaseCount==1,"native loan remains pinned until exact closure");
        responseLoss.LoseCloseResponses=false;
    }
    Check(kernel.TerminateProcess(owner).IsSuccess,"original transfer automatic caller teardown");
    Check(responseLoss.TransferCalls==2 && responseLoss.CloseCalls==1,"teardown never repeats transfer or Close");
    Check(Used()==0 && native.ActiveDeviceLeaseCount==0 && native.ActiveBindingCount==0,"exact backing charge/device/root reclaim");
    Check(kernel.Processes.Resolve(service).IsSuccess,"independent service survives");
    Console.WriteLine("Virtual-I/O lost transfer: write1/read1; unpublished snapshot; charge=512->516->0; Close1; nativeDevices/roots=0; service survives; gates=OFF; ISA=NONE; ProductionQualified=false");
    return;
}
var read=await Dispatch(client.TransferIoAsync(new(io.IoId,io.Generation,4,false,4,default)));Check(read.Completed && read.Data.ToArray().SequenceEqual(new byte[]{17,19,23,29}),"read bytes");
Check(Used()==516,"read snapshot charge");
var closing=client.CloseIoAsync(new(io.IoId,io.Generation));Check(host.ProcessNext().IsSuccess,"IO Close dispatch");await closing;
Check(Used()==0,"exact backing/snapshot reclaim");Check(kernel.CloseSession(owner,session).IsSuccess,"session Close");Check(kernel.TerminateProcess(owner).IsSuccess,"process cleanup");
Check(native.ActiveDeviceLeaseCount==0,"native device release");
Console.WriteLine("Virtual-I/O software host: bytes=17,19,23,29; charge=512->516->0; nativeDevices=0; gates=OFF; ISA=NONE; ProductionQualified=false");
