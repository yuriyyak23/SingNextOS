using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Platform.HybridCpu;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

// Host orchestration -> external provider -> CPU model -> compiler-produced HCEXE.
// The second mapping is an independent live user, not an execution predecessor.
var startLoss=args.Contains("--service-start-loss",StringComparer.Ordinal);
var mapLoss=args.Contains("--service-map-loss",StringComparer.Ordinal);
var childCreateLoss=args.Contains("--service-cached-create-loss",StringComparer.Ordinal);
var freshChildLoss=args.Contains("--service-child-create-loss",StringComparer.Ordinal);
var sourceRefusal=args.Contains("--service-create-source-refusal",StringComparer.Ordinal);
var capacityRefusal=args.Contains("--service-create-capacity-refusal",StringComparer.Ordinal);
var parentMapCapacity=args.Contains("--service-parent-map-capacity-refusal",StringComparer.Ordinal);
var serviceMode=startLoss || args.Contains("--service-bind-loss",StringComparer.Ordinal);
var lossMode=freshChildLoss || childCreateLoss || mapLoss || serviceMode || args.Contains("--lost-release-reply",StringComparer.Ordinal);
var packageArgument=args.SingleOrDefault(arg=>!arg.StartsWith("--",StringComparison.Ordinal));
var packagePath=packageArgument is null?Path.Combine(AppContext.BaseDirectory,"guest.hcexe"):Path.GetFullPath(packageArgument);
var package=File.ReadAllBytes(packagePath);
var native=new NeutralDomainRuntimeFacade();
var createLoss=args.Contains("--service-create-loss",StringComparer.Ordinal);
GuestCreateReplyLossRuntime? createResponseLoss=null;
INeutralDomainRuntime rootRuntime=native;
if(createLoss || capacityRefusal || parentMapCapacity)
{
    var proxy=global::System.Reflection.DispatchProxy.Create<IGuestCreateReplyLossRuntime,GuestCreateReplyLossRuntime>();
    createResponseLoss=(GuestCreateReplyLossRuntime)(object)proxy;createResponseLoss.Inner=native;rootRuntime=proxy;
    if(parentMapCapacity)createResponseLoss.Lose=false;
    if(capacityRefusal)
    {
        typeof(NeutralDomainRuntimeFacade).GetField("_nextHandle",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.SetValue(native,ulong.MaxValue);
        createResponseLoss.Lose=args.Contains("--lost-capacity-reply",StringComparer.Ordinal);
    }
}
GuestReplyLossRuntime? responseLoss=null;
var adapter=new HybridCpuExecutableChildAdapter();
if(lossMode)
{
    var external=new HybridCPU.ExternalRuntime.HybridCpuExternalRuntime();
    var proxy=global::System.Reflection.DispatchProxy.Create<IGuestReplyLossRuntime,GuestReplyLossRuntime>();
    responseLoss=(GuestReplyLossRuntime)(object)proxy;responseLoss.Inner=external;
    var constructor=typeof(HybridCpuExecutableChildAdapter).GetConstructor(global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic,null,
        [typeof(HybridCPU.ExternalRuntime.Contracts.IHybridCpuExternalRuntime),typeof(HybridCPU.ExternalRuntime.Contracts.IHybridCpuChildDomainRuntimeV3),typeof(ISemanticTraceSinkV1)],null)!;
    adapter=(HybridCpuExecutableChildAdapter)constructor.Invoke([external,proxy,null]);
}
var actualProvider=new HybridCpuPlatformAuthorityProvider(rootRuntime,adapter);
SingPlus.Platform.IPlatformAuthorityProvider provider=actualProvider;
GuestSourceRefusalProvider? sourceProbe=null;
if(sourceRefusal)
{
    var proxy=global::System.Reflection.DispatchProxy.Create<IGuestSourceRefusalProvider,GuestSourceRefusalProvider>();
    sourceProbe=(GuestSourceRefusalProvider)(object)proxy;sourceProbe.Inner=actualProvider;provider=proxy;
}
var kernel=new RuntimeKernel(provider);
void Check(bool value,string operation){if(!value)throw new InvalidOperationException(operation);}
ProcessHandle Create(ulong id,ulong domain)
{
    var manifest=new SingProcessManifestV1(new(id),new(domain),1,$"guest-host-{id}",ExecutionRole.Sip,MemoryProfile.SipRegion);
    var created=kernel.CreateProcess(manifest);Check(created.IsSuccess,created.Message??"process create");return new(manifest.ProcessId,manifest.Generation);
}
const ulong bytes=1024*1024;
var owner=Create(2301,23010);var admin=Create(2302,23020);var foreign=Create(2303,23030);
var adminCapability=kernel.MintCapability(new(23020),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
var budget=kernel.AdmitProcessBudget(admin,adminCapability,owner,"guest-host",
    [new(ServiceBudgetDimension.OwnedMemoryBytes,bytes*3),new(ServiceBudgetDimension.PinnedMappedMemoryBytes,bytes*2),
     new(ServiceBudgetDimension.GuestMemoryBytes,bytes*2),new(ServiceBudgetDimension.RegionUses,8)]).Value!.ProcessBudget;
ulong Used(ServiceBudgetDimension dimension)=>kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==dimension).Used;
var code=kernel.AllocateBuffer<byte>(owner,(int)bytes).Value!;
var peer=kernel.AllocateBuffer<byte>(owner,(int)bytes).Value!;peer.Span[0]=0x6B;
var create=kernel.MintCapability(new(23010),owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
if(createLoss || childCreateLoss || freshChildLoss || sourceRefusal || capacityRefusal)
{
    if(childCreateLoss)
    {
        Check(kernel.BindPlatformDomain(owner).IsSuccess,"pre-existing root admission");
        responseLoss!.LoseChildCreateResponses=true;
    }
    if(freshChildLoss)responseLoss!.LoseChildCreateResponses=true;
    var protocol=SingPlus.Sip.Virtualization.VirtualizationServiceClient.CreateProtocol();
    var descriptor=kernel.RegisterService(foreign,"guest-create-host",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,
        SingPlus.Sip.Virtualization.VirtualizationServiceClient.ResponseProtocol,[]).Value!;
    var session=kernel.OpenSession(owner,descriptor,[]).Value;
    var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,foreign,session).Value!;
    var client=SingPlus.Sip.Virtualization.VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
    var serviceRegion=sourceRefusal || capacityRefusal?kernel.AllocateBuffer<byte>(foreign,32).Value:null;
    if(sourceRefusal)sourceProbe!.BeforeSource=()=>
    {
        if(args.Contains("--early-create-teardown",StringComparer.Ordinal))Check(!kernel.TerminateProcess(owner).IsSuccess,"pending root must retain caller");
        if(args.Contains("--revoke-create-source",StringComparer.Ordinal))Check(kernel.RevokeCapability(create).IsSuccess,"revoke source before root effect");
        else throw new IOException("Root source observation unavailable before provider Bind");
    };
    var pending=client.CreateAsync(new(create,new(1,(long)bytes*2)));
    Check(!host.ProcessNext().IsSuccess,"unknown Create not published");
    var denied=false;try{await pending;}catch(Exception){denied=true;}Check(denied,"failed accepted Create response");
    if(capacityRefusal)
    {
        Check(createResponseLoss!.Binds==1 && native.ActiveBindingCount==0,"original capacity refusal invoked once without root");
        Check(Used(ServiceBudgetDimension.OwnedMemoryBytes)==bytes*2,"caller capacity refusal charges retained");
        if(createResponseLoss.Lose)
        {
            Check(!kernel.TerminateProcess(owner).IsSuccess,"unobserved original capacity disposition remains pinned");
            Check(Used(ServiceBudgetDimension.OwnedMemoryBytes)==bytes*2,"unknown original retains charges");
            createResponseLoss.Lose=false;
        }
        var capacityTeardown=kernel.TerminateProcess(owner);Check(capacityTeardown.IsSuccess,capacityTeardown.Message??"exact capacity no-effect cleanup");
        Check(kernel.QueryBudget(budget).Value!.Usage.All(u=>u.Used==0),"capacity refusal exact quantitative reclaim");
        Check(kernel.Regions.Snapshot().Single(r=>r.Handle==code.Handle).State==RegionState.Released && kernel.Regions.Snapshot().Single(r=>r.Handle==peer.Handle).State==RegionState.Released,"capacity caller Regions released");
        Check(kernel.Regions.Snapshot().Single(r=>r.Handle==serviceRegion!.Handle).State==RegionState.Owned && kernel.Processes.Resolve(foreign).IsSuccess,"capacity foreign backing survives");
        Check(createResponseLoss.Binds==1 && native.ActiveBindingCount==0,"capacity same-original no repeat");
        Console.WriteLine("Create capacity refusal: nativeBind1; nativeRoots0; OwnedMemory2097152->0; callerRegionsReleased2; serviceRegionOwned; gatesOFF; ISA NONE; ProductionQualified=false");
        return;
    }
    if(sourceRefusal)
    {
        Check(sourceProbe!.RootBinds==0 && sourceProbe.ChildCreates==0 && native.ActiveBindingCount==0,"source refusal before any root/child effect");
        Check(Used(ServiceBudgetDimension.OwnedMemoryBytes)==bytes*2,"original caller charges retained before reclaim");
        var sourceTeardown=kernel.TerminateProcess(owner);Check(sourceTeardown.IsSuccess,sourceTeardown.Message??"never-invoked caller closure");
        Check(kernel.QueryBudget(budget).Value!.Usage.All(u=>u.Used==0),"exact quantitative caller reclaim");
        Check(kernel.Regions.Snapshot().Single(r=>r.Handle==code.Handle).State==RegionState.Released && kernel.Regions.Snapshot().Single(r=>r.Handle==peer.Handle).State==RegionState.Released,"both original caller Regions released");
        Check(kernel.Regions.Snapshot().Single(r=>r.Handle==serviceRegion!.Handle).State==RegionState.Owned && kernel.Processes.Resolve(foreign).IsSuccess,"foreign service/backing survives");
        Console.WriteLine("Create source refusal: RootBind0; ChildCreate0; nativeRoots0; OwnedMemory2097152->0; callerRegionsReleased2; serviceRegionOwned; gatesOFF; ISA NONE; ProductionQualified=false");
        return;
    }
    if(childCreateLoss)
    {
        Check(responseLoss!.ChildCreates==0 && native.ActiveBindingCount==1,"existing root refusal before child effect");
        Check(kernel.CloseSession(owner,session).IsSuccess,"pre-acceptance refusal session closure");
        Check(kernel.TerminateProcess(owner).IsSuccess,"existing root ordinary owner teardown");
        Check(kernel.QueryBudget(budget).Value!.Usage.All(u=>u.Used==0),"refused Create original charges reclaim");
        Console.WriteLine("Existing-root Create refused before acceptance: ChildCreate0; nativeRoots0; charges0; service survives; gatesOFF; ISA NONE; ProductionQualified=false");
        return;
    }
    Check((freshChildLoss?responseLoss!.ChildCreates:createResponseLoss!.Binds)==1 && native.ActiveBindingCount==1,"original root/child effect once");
    Check(!kernel.TerminateProcess(owner).IsSuccess,"unknown root/query retains caller");
    if(freshChildLoss)responseLoss!.LoseChildCreateResponses=false;else createResponseLoss!.Lose=false;
    if(freshChildLoss && args.Contains("--lost-child-close-reply",StringComparer.Ordinal))
    {
        responseLoss!.LoseChildCloseResponses=true;
        Check(!kernel.TerminateProcess(owner).IsSuccess,"unknown child Close retains original caller");
        Check(Used(ServiceBudgetDimension.OwnedMemoryBytes)==bytes*2 && native.ActiveBindingCount==1,"partial child Close retains independent Regions/charges/root");
        responseLoss.LoseChildCloseResponses=false;
    }
    var teardown=kernel.TerminateProcess(owner);
    Console.WriteLine($"Create loss: effects={(freshChildLoss?responseLoss!.ChildCreates:createResponseLoss!.Binds)}; nativeRoots={native.ActiveBindingCount}; OwnedMemory={Used(ServiceBudgetDimension.OwnedMemoryBytes)}; teardown={teardown.Error}; codeRegion={kernel.Regions.Snapshot().Single(r=>r.Handle==code.Handle).State}; serviceAlive={kernel.Processes.Resolve(foreign).IsSuccess}");
    Check(teardown.IsSuccess,teardown.Message??"original service Create invocation closure");
    Check(kernel.QueryBudget(budget).Value!.Usage.All(u=>u.Used==0),"Create quantitative reclaim");
    if(freshChildLoss)Check(responseLoss!.ChildCreates==1 && responseLoss.ChildCloses==1,"original child Create/Close no-repeat");
    return;
}
var domain=kernel.CreateVirtualDomain(owner,create,new(1,(long)bytes*2)).Value!;
Check(kernel.ConfigureVirtualDomain(owner,domain.Domain,domain.ConfigureCapability).IsSuccess,"configure guest");
CapabilityId Memory(SingPlus.Sip.OwnedBuffer<byte> buffer,bool executable)=>kernel.MintCapability(new(23010),owner,ResourceKind.MemoryRegion,
    CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),CapabilityRights.Map|CapabilityRights.Read|(executable?CapabilityRights.Execute:CapabilityRights.None)).Value!.CapabilityId;
if(parentMapCapacity)
{
    var independent=kernel.MapGuestRegion(owner,domain.Domain,domain.MemoryCapability,Memory(peer,false),peer.Handle,new((long)bytes,(long)bytes),GuestMemoryAccess.Read);
    Check(independent.IsSuccess,independent.Message??"independent parent/guest mapping");
    var serviceRegion=kernel.AllocateBuffer<byte>(foreign,32).Value!;
    typeof(NeutralDomainRuntimeFacade).GetField("_nextResource",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.SetValue(native,ulong.MaxValue);
    createResponseLoss!.LoseMap=args.Contains("--lost-parent-map-reply",StringComparer.Ordinal);
    var protocol=SingPlus.Sip.Virtualization.VirtualizationServiceClient.CreateProtocol();
    var descriptor=kernel.RegisterService(foreign,"parent-capacity",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,SingPlus.Sip.Virtualization.VirtualizationServiceClient.ResponseProtocol,[]).Value!;
    var session=kernel.OpenSession(owner,descriptor,[]).Value;
    var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,foreign,session).Value!;
    var client=SingPlus.Sip.Virtualization.VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
    var pending=client.MapGuestRegionAsync(new(domain.Domain,domain.MemoryCapability,Memory(code,true),code.Handle,new(0,(long)bytes),GuestMemoryAccess.Read|GuestMemoryAccess.Execute){MaximumPrivateExecutableDataBytes=bytes});
    Check(!host.ProcessNext().IsSuccess,"capacity Map not published");
    var denied=false;try{await pending;}catch(Exception){denied=true;}Check(denied,"accepted capacity Map failure");
    Check(createResponseLoss.Maps==2 && native.ActiveOwnedRegionMappingCount==1,"one independent map and one original refusal");
    Check(Used(ServiceBudgetDimension.GuestMemoryBytes)==(createResponseLoss.LoseMap?bytes*2:bytes) && peer.Span[0]==0x6B,"independent guest/data survives refusal");
    Check(!kernel.AcquireRegionUse(owner,peer.Handle,RegionUseMode.ExclusiveRead,new(0,(long)bytes)).IsSuccess,"independent mapped acquisition guard");
    if(createResponseLoss.LoseMap)
    {
        Check(Used(ServiceBudgetDimension.OwnedMemoryBytes)==bytes*3,"unknown original quota retained");
        Check(!kernel.AcquireRegionUse(owner,code.Handle,RegionUseMode.ExclusiveRead,new(0,(long)bytes)).IsSuccess,"unknown parent reservation guard");
        Check(!kernel.TerminateProcess(owner).IsSuccess,"unknown parent Map blocks teardown");
        createResponseLoss.LoseMap=false;
    }
    else Check(Used(ServiceBudgetDimension.OwnedMemoryBytes)==bytes*2,"known no-effect quota consumed without independent closure");
    var parentTeardown=kernel.TerminateProcess(owner);Check(parentTeardown.IsSuccess,parentTeardown.Message??"exact parent no-effect closure");
    Check(kernel.QueryBudget(budget).Value!.Usage.All(u=>u.Used==0),"parent capacity quantitative reclaim");
    Check(createResponseLoss.Maps==2 && native.ActiveOwnedRegionMappingCount==0 && native.ActiveBindingCount==0,"parent no-repeat/original users closure");
    Check(kernel.Regions.Snapshot().Single(r=>r.Handle==code.Handle).State==RegionState.Released && kernel.Regions.Snapshot().Single(r=>r.Handle==peer.Handle).State==RegionState.Released,"caller Regions released");
    Check(kernel.Regions.Snapshot().Single(r=>r.Handle==serviceRegion.Handle).State==RegionState.Owned && kernel.Processes.Resolve(foreign).IsSuccess,"foreign backing survives parent cleanup");
    Console.WriteLine("Parent Map capacity: nativeMap2 (independent1/refusal1); no repeat; independent mapping/data/guard retained; quota3145728->2097152->0; callerRegionsReleased2; foreignRegionOwned; gatesOFF; ISA NONE; ProductionQualified=false");
    return;
}
if(mapLoss)
{
    var protocol=SingPlus.Sip.Virtualization.VirtualizationServiceClient.CreateProtocol();
    var descriptor=kernel.RegisterService(foreign,"guest-map-host",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,
        SingPlus.Sip.Virtualization.VirtualizationServiceClient.ResponseProtocol,[]).Value!;
    var session=kernel.OpenSession(owner,descriptor,[]).Value;
    var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,foreign,session).Value!;
    var client=SingPlus.Sip.Virtualization.VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
    responseLoss!.LoseMapResponses=true;
    var pending=client.MapGuestRegionAsync(new(domain.Domain,domain.MemoryCapability,Memory(code,true),code.Handle,new(0,(long)bytes),GuestMemoryAccess.Read|GuestMemoryAccess.Execute){MaximumPrivateExecutableDataBytes=bytes});
    Check(!host.ProcessNext().IsSuccess,"unknown Map not published");
    var denied=false;try{await pending;}catch(Exception){denied=true;}Check(denied,"failed accepted Map response");
    Check(responseLoss.MapCalls==1 && Used(ServiceBudgetDimension.OwnedMemoryBytes)==bytes*3,"original Map/quota retained");
    Check(!kernel.AcquireRegionUse(owner,code.Handle,RegionUseMode.ExclusiveRead,new(0,(long)bytes)).IsSuccess,"unknown Map acquisition guard");
    responseLoss.LoseMapResponses=false;
    var teardown=kernel.TerminateProcess(owner);Check(teardown.IsSuccess,teardown.Message??"original guest Map caller closure");
    Check(kernel.QueryBudget(budget).Value!.Usage.All(u=>u.Used==0),"original mapping charges reclaimed");
    Check(responseLoss.MapCalls==1 && responseLoss.BindCalls==0 && responseLoss.StartCalls==0,"Map reconciliation no-repeat/no execution");
    Check(native.ActiveOwnedRegionMappingCount==0 && native.ActiveBindingCount==0,"original native backing/root closure");
    Check(kernel.Processes.Resolve(foreign).IsSuccess,"service survives Map cleanup");
    Console.WriteLine("Guest service Map loss: Map1/Bind0/Start0; original Region/quota closure; charges0; native maps/roots0; service alive; gatesOFF; ISA NONE; ProductionQualified=false");
    return;
}
var codeMap=kernel.MapGuestRegion(owner,domain.Domain,domain.MemoryCapability,Memory(code,true),code.Handle,new(0,(long)bytes),GuestMemoryAccess.Read|GuestMemoryAccess.Execute,bytes);
Check(codeMap.IsSuccess,codeMap.Message??"code mapping");
var peerMap=kernel.MapGuestRegion(owner,domain.Domain,domain.MemoryCapability,Memory(peer,false),peer.Handle,new((long)bytes,(long)bytes),GuestMemoryAccess.Read);
Check(peerMap.IsSuccess,peerMap.Message??"independent mapping");
Check(!kernel.AcquireRegionUse(owner,code.Handle,RegionUseMode.ExclusiveRead,new(0,(long)bytes)).IsSuccess,"mapped acquisition guard");
Check(Used(ServiceBudgetDimension.OwnedMemoryBytes)==bytes*3 && Used(ServiceBudgetDimension.GuestMemoryBytes)==bytes*2,"pre-effect Region/private quota and guest charges");
if(serviceMode)
{
    var protocol=SingPlus.Sip.Virtualization.VirtualizationServiceClient.CreateProtocol();
    var descriptor=kernel.RegisterService(foreign,"guest-host",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,
        SingPlus.Sip.Virtualization.VirtualizationServiceClient.ResponseProtocol,[]).Value!;
    var session=kernel.OpenSession(owner,descriptor,[]).Value;
    var host=RuntimeVirtualizationServiceHost.CreateForSession(kernel,foreign,session).Value!;
    var client=SingPlus.Sip.Virtualization.VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
    responseLoss!.LoseBindResponses=!startLoss;
    var pending=client.BindExecutablePackageAsync(new(domain.Domain,domain.ExecuteCapability,codeMap.Value!.Mapping,new SingPlus.Sip.Virtualization.BoundedExecutablePackage(package),256));
    var dispatch=host.ProcessNext();
    if(startLoss)
    {
        Check(dispatch.IsSuccess,"service Bind publication");await pending;
        responseLoss.LoseStartResponses=true;
        pending=client.StartAsync(new(domain.Domain,domain.ExecuteCapability));
        dispatch=host.ProcessNext();
    }
    Check(!dispatch.IsSuccess,"unknown artifact effect not published");
    var refused=false;try{await pending;}catch(Exception){refused=true;}Check(refused,"failed accepted Bind response");
    Check(Used(ServiceBudgetDimension.OwnedMemoryBytes)==bytes*3,"unknown service Bind retains quota");
    responseLoss.LoseBindResponses=false;
    responseLoss.LoseStartResponses=false;
    if(args.Contains("--lost-release-reply",StringComparer.Ordinal))
    {
        responseLoss.LoseReleaseResponses=true;
        Check(!kernel.TerminateProcess(owner).IsSuccess,"unknown artifact Release retains service invocation");
        Check(Used(ServiceBudgetDimension.OwnedMemoryBytes)==bytes*3,"partial retirement retains charges");
        responseLoss.LoseReleaseResponses=false;
    }
    var teardown=kernel.TerminateProcess(owner);Check(teardown.IsSuccess,teardown.Message??"service artifact original caller closure");
    Check(kernel.QueryBudget(budget).Value!.Usage.All(u=>u.Used==0),"service artifact quantitative reclaim");
    Check(kernel.Processes.Resolve(foreign).IsSuccess,"service survives caller teardown");
    Check(responseLoss.BindCalls==1 && responseLoss.StartCalls==(startLoss?1:0) && responseLoss.ReleaseCalls==1,"original artifact effects no-repeat");
    Console.WriteLine($"Guest service {(startLoss?"Start":"Bind")} lost reply: Bind1/Start{responseLoss.StartCalls}/Release1; exact artifact/guest cleanup; caller closed; charges0; service survives; gates=OFF; ISA=NONE; ProductionQualified=false");
    return;
}
var bound=kernel.BindVirtualDomainExecutable(owner,domain.Domain,domain.ExecuteCapability,codeMap.Value!.Mapping,package,256);
Check(bound.IsSuccess,bound.Message??"bind compiler-produced guest");
var started=kernel.StartVirtualDomain(owner,domain.Domain,domain.ExecuteCapability);
Check(started.IsSuccess,started.Message??"execute guest");
Check(kernel.QueryVirtualDomain(owner,domain.Domain).Value==VirtualDomainState.Completed,"original completed lifetime");
var result=kernel.ObserveVirtualExecutionResult(owner,domain.Domain,domain.TrapCapability);
Check(result.IsSuccess,result.Message??"original result");
Check(result.Value.ProcessExitCode==42,"compiler-produced result42");
Check(!kernel.ObserveVirtualExecutionResult(foreign,domain.Domain,domain.TrapCapability).IsSuccess,"foreign result denied");
Check(!kernel.StartVirtualDomain(owner,domain.Domain,domain.ExecuteCapability).IsSuccess,"completed Start no-repeat");
Check(Used(ServiceBudgetDimension.OwnedMemoryBytes)==bytes*3,"result does not reclaim private quota");
if(responseLoss is not null)responseLoss.LoseReleaseResponses=true;
var closed=kernel.CloseGuestRegionMapping(owner,domain.Domain,domain.MemoryCapability,codeMap.Value.Mapping);
if(responseLoss is not null)
{
    Check(!closed.IsSuccess,"unknown release cannot close guest mapping");
    Check(responseLoss.StartCalls==1 && responseLoss.ReleaseCalls==1,"original Start and Release invoked once");
    Check(Used(ServiceBudgetDimension.OwnedMemoryBytes)==bytes*3 && Used(ServiceBudgetDimension.GuestMemoryBytes)==bytes*2,
        "unknown release retains code/peer/quota charges");
    Check(code.IsValid && peer.IsValid,"unknown release retains both Regions");
    Check(!kernel.ReleaseRegion(owner,peer).IsSuccess,"independent mapping survives unknown release");
    responseLoss.LoseReleaseResponses=false;
    var teardown=kernel.TerminateProcess(owner);Check(teardown.IsSuccess,teardown.Message??"same original release teardown");
    Check(responseLoss.StartCalls==1 && responseLoss.ReleaseCalls==1,"teardown no-repeat");
    Check(kernel.QueryBudget(budget).Value!.Usage.All(u=>u.Used==0),"exact original and independent quantitative reclaim");
    Check(!code.IsValid && !peer.IsValid && native.ActiveOwnedRegionMappingCount==0 && native.ActiveBindingCount==0,"Region/native lifetime closure");
    Check(kernel.Processes.Resolve(foreign).IsSuccess,"foreign process survives release reconciliation");
    Console.WriteLine($"Guest lost release: result42; Start1/Release1; OwnedMemory={bytes*3}->retained->{0}; GuestMemory={bytes*2}->retained->0; Regions/nativeMaps/roots closed; foreign survives; gates=OFF; ISA=NONE; ProductionQualified=false");
    return;
}
Check(closed.IsSuccess,closed.Message??"exact artifact release and code guest closure");
Check(Used(ServiceBudgetDimension.OwnedMemoryBytes)==bytes*2 && Used(ServiceBudgetDimension.GuestMemoryBytes)==bytes &&
    Used(ServiceBudgetDimension.PinnedMappedMemoryBytes)==bytes,"independent guest mapping retained after artifact release");
Check(kernel.ReleaseRegion(owner,code).IsSuccess,"closed code Region reclaim");
Check(Used(ServiceBudgetDimension.OwnedMemoryBytes)==bytes,"only code Region charge consumed");
Check(!kernel.ReleaseRegion(owner,peer).IsSuccess,"independent mapped Region cannot release");
Check(peer.IsValid && Used(ServiceBudgetDimension.OwnedMemoryBytes)==bytes,"independent Region charge survives");
Check(kernel.CloseGuestRegionMapping(owner,domain.Domain,domain.MemoryCapability,peerMap.Value!.Mapping).IsSuccess,"independent mapping closure");
Check(peer.Span[0]==0x6B,"independent backing data survives artifact cleanup");
Check(Used(ServiceBudgetDimension.GuestMemoryBytes)==0 && Used(ServiceBudgetDimension.PinnedMappedMemoryBytes)==0,"both exact mapping charges consumed");
Check(kernel.ReleaseRegion(owner,peer).IsSuccess,"independent Region reclaim");
Check(kernel.TerminateProcess(owner).IsSuccess,"guest/root/process teardown");
Check(kernel.QueryBudget(budget).Value!.Usage.All(u=>u.Used==0),"all quantitative reclaim");
Check(native.ActiveOwnedRegionMappingCount==0 && native.ActiveBindingCount==0,"native mappings/root closure");
Check(kernel.Processes.Resolve(foreign).IsSuccess,"foreign process survives");
Console.WriteLine($"Guest software host: result={result.Value.ProcessExitCode}; retired={result.Value.Sequence}; OwnedMemory={bytes*3}->{bytes*2}->{bytes}->0; GuestMemory={bytes*2}->{bytes}->0; independent byte=6B; nativeMaps/roots=0; gates=OFF; ISA=NONE; ProductionQualified=false");
