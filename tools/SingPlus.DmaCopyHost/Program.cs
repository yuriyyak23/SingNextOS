using System.Diagnostics;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using YAKSys_Hybrid_CPU.Core;

var native=new NeutralDomainRuntimeFacade();
var lossMode=args.Contains("--lost-copy-reply",StringComparer.Ordinal);
DmaReplyLossRuntime? responseLoss=null;
INeutralDomainRuntime runtime=native;
if(lossMode)
{
    var proxy=global::System.Reflection.DispatchProxy.Create<IDmaReplyLossRuntime,DmaReplyLossRuntime>();
    responseLoss=(DmaReplyLossRuntime)(object)proxy;responseLoss.Native=native;responseLoss.LoseResponses=true;runtime=proxy;
}
var kernel=new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(runtime));
void Check(bool value,string operation){if(!value)throw new InvalidOperationException(operation);}
ProcessHandle Create(ulong id,ulong domain)
{
    var manifest=new SingProcessManifestV1(new(id),new(domain),1,$"dma-copy-host-{id}",ExecutionRole.Sip,MemoryProfile.SipRegion);
    var created=kernel.CreateProcess(manifest);Check(created.IsSuccess,created.Message??"create");
    return new(manifest.ProcessId,manifest.Generation);
}
var source=Create(2201,22010);var destination=Create(2202,22020);var admin=Create(2203,22030);
var budgetCap=kernel.MintCapability(new(22030),admin,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
BudgetAccountHandle Budget(ProcessHandle owner)=>kernel.AdmitProcessBudget(admin,budgetCap,owner,$"dma-copy-{owner.ProcessId}",[new(ServiceBudgetDimension.OwnedMemoryBytes,64)]).Value!.ProcessBudget;
var sourceBudget=Budget(source);var destinationBudget=Budget(destination);
(OwnedBuffer<byte> Buffer,PlatformOwnedRegionSliceMapping Mapping,PlatformDmaGrant Grant) Leg(ProcessHandle owner,bool read)
{
    var binding=kernel.BindPlatformDomain(owner);Check(binding.IsSuccess,binding.Message??"bind root");var root=binding.Value;
    var dc=kernel.MintCapability(root.Subject.DomainId,owner,ResourceKind.Device,read?"device/dma-source":"device/dma-destination",CapabilityRights.Read|CapabilityRights.Write|CapabilityRights.Configure).Value!.CapabilityId;
    var bound=kernel.BindPlatformDevice(owner,root,dc,PlatformDeviceRights.Read|PlatformDeviceRights.Write|PlatformDeviceRights.Configure);Check(bound.IsSuccess,bound.Message??"device");
    var allocated=kernel.AllocateBuffer<byte>(owner,64);Check(allocated.IsSuccess,allocated.Message??"buffer");var buffer=allocated.Value!;
    var mc=kernel.MintCapability(root.Subject.DomainId,owner,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),CapabilityRights.Map|(read?CapabilityRights.Read:CapabilityRights.Write)).Value!.CapabilityId;
    var mapped=kernel.MapPlatformOwnedRegionSlice(owner,root,mc,buffer.Handle,0,64,read?PlatformMemoryAccess.Read:PlatformMemoryAccess.Write);Check(mapped.IsSuccess,mapped.Message??"mapping");
    var granted=kernel.BindPlatformDma(owner,bound.Value,mapped.Value,0,64,read?PlatformDmaDirection.DeviceReadsMemory:PlatformDmaDirection.DeviceWritesMemory);Check(granted.IsSuccess,granted.Message??"DMA grant");
    return (buffer,mapped.Value,granted.Value);
}
var input=Leg(source,true);var output=Leg(destination,false);input.Buffer.Span.Fill(0x53);
var costs=new V6ManagedLocalityCostProvider();
void ObserveCost(string providerClass,OwnedBuffer<byte> buffer,byte expected)
{
    var began=Stopwatch.GetTimestamp();var observed=buffer.Span[0];var elapsed=Stopwatch.GetTimestamp()-began;
    Check(observed==expected,"advisory host access observation");
    var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var latency=checked((ulong)Math.Max(1,elapsed*(1_000_000_000d/Stopwatch.Frequency)));
    Check(costs.Publish(new(1,providerClass,1,now,now+60000,64,latency,0,0)).IsSuccess,"cost observation");
}
ObserveCost("software-dma-source",input.Buffer,0x53);ObserveCost("software-dma-destination",output.Buffer,0);
var descriptor=kernel.Regions.Validate(input.Buffer.Handle,new(new(22010),source.Generation)).Value!;
var plan=new DataMotionPlanV1(1,"software-dma-source","software-dma-destination",
    costs.CurrentGeneration("software-dma-source").Value,costs.CurrentGeneration("software-dma-destination").Value,
    input.Buffer.Handle.Generation.Value,descriptor.MutationEpoch.Value,64,64,DataMotionModeV1.ProviderDmaThenReleaseSource);
var sp=kernel.PreparePlatformDmaForDevice(source,input.Grant);var dp=kernel.PreparePlatformDmaForDevice(destination,output.Grant);
Check(sp.IsSuccess && dp.IsSuccess,"DMA prepare visibility");
var request=new ProviderDmaDataMotionRequest<byte>(source,destination,input.Buffer,output.Buffer,plan,input.Mapping,input.Grant,sp.Value,output.Mapping,output.Grant,dp.Value);
var moved=kernel.MoveProviderDmaBuffers(request,costs);
if(!moved.IsSuccess)
{
    var cleanup=kernel.CleanupProviderDmaDataMotion(request);
    if(responseLoss is not null)
    {
        Check(responseLoss.CopyCalls==1,"actual byte effect exactly once");
        // Response failure never permits another movement invocation.
        Check(!kernel.MoveProviderDmaBuffers(request,costs).IsSuccess,"unknown effect no-repeat");
        responseLoss.LoseResponses=false;
        if(!cleanup.IsSuccess)cleanup=kernel.CleanupProviderDmaDataMotion(request);
    }
    Check(cleanup.IsSuccess,cleanup.Message??"original byte cleanup retained");
    Check(kernel.CleanupProviderDmaDataMotion(request).IsSuccess,"same original cleanup consumption");
    Check(!kernel.ReadProviderDmaDataMotionResult(request).IsSuccess,"cleanup does not publish a Copy result");
    Check(input.Buffer.Span.Length==64 && output.Buffer.Span.Length==64,"closed byte custody returned to owners");
    Check(kernel.TerminateProcess(source).IsSuccess,"failure source teardown");
    Check(kernel.TerminateProcess(destination).IsSuccess,"failure destination teardown");
    Check(kernel.QueryBudget(sourceBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==0 &&
        kernel.QueryBudget(destinationBudget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used==0,"failure quantitative reclaim");
    Check(native.ActiveDmaGrantCount==0 && native.ActiveOwnedRegionMappingCount==0 && native.ActiveDeviceLeaseCount==0 && native.ActiveBindingCount==0,"failure original retirement");
    if(responseLoss is not null)Check(responseLoss.CopyCalls==1,"cleanup never repeats byte effect");
    Console.WriteLine("DMA-copy original failed response: Copy1; result unavailable; custody closed; charges=0; grants/maps/devices/roots=0; gates=OFF; ISA=NONE; ProductionQualified=false");
    return;
}
Check(!lossMode,"lost response cannot become successful Copy publication");
Check(moved.IsSuccess,moved.Message??"DMA copy");Check(ReferenceEquals(moved.Value!.Buffer,output.Buffer),"original output publication");
Check(moved.Value.Buffer.Span.ToArray().All(b=>b==0x53),"copied bytes");Check(!input.Buffer.IsValid,"source lifetime release");
Check(ReferenceEquals(moved.Value,kernel.ReadProviderDmaDataMotionResult(request).Value),"same original retained result");
Check(ReferenceEquals(moved.Value,kernel.MoveProviderDmaBuffers(request,costs).Value),"replay is read without consumed source effect");
var copiedRequest=(ProviderDmaDataMotionRequest<byte>)typeof(object).GetMethod("MemberwiseClone",global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.Invoke(request,null)!;
Check(!kernel.ReadProviderDmaDataMotionResult(copiedRequest).IsSuccess && !kernel.MoveProviderDmaBuffers(copiedRequest,costs).IsSuccess,"copied continuation denied");
Check(!new RuntimeKernel().ReadProviderDmaDataMotionResult(request).IsSuccess,"foreign kernel denied");
ulong Used(BudgetAccountHandle budget)=>kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
Check(Used(sourceBudget)==0 && Used(destinationBudget)==64,"source release and retained destination charge");
Check(native.ActiveDmaGrantCount==0 && native.ActiveOwnedRegionMappingCount==0,"exact grants/mappings closure");
Check(kernel.TerminateProcess(source).IsSuccess,"source teardown");Check(kernel.TerminateProcess(destination).IsSuccess,"destination teardown");
Check(Used(sourceBudget)==0 && Used(destinationBudget)==0,"quantitative reclaim");
Check(native.ActiveDeviceLeaseCount==0 && native.ActiveBindingCount==0,"device/root retirement");
Check(ReferenceEquals(moved.Value,kernel.ReadProviderDmaDataMotionResult(request).Value),"retired original result readable without repin");
Check(!moved.Value!.Buffer.IsValid && Used(destinationBudget)==0,"read cannot restore consumed ownership");
Console.WriteLine("DMA-copy software host: bytes=64x53; source=64->0; destination=64->64->0; grants=0; mappings=0; devices=0; roots=0; gates=OFF; ISA=NONE; ProductionQualified=false");
