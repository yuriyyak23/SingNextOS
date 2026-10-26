using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Platform.HybridCpu;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Diagnostics;

public static class KernelArtifactScenarioOracle
{
    public static readonly string[] Schedules = ["positive", "lost-native-reply", "revoke-execute", "revoke-region", "revoke-memory",
        "bridge-child-aba", "bridge-artifact-route-loss", "lost-staging-reply", "wrong-operands", "destroy-inflight",
        "closure-loss", "closure-copy", "entry-history-loss", "entry-route-loss", "deny-artifact-execute", "deny-software-profile"];
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run(string schedule)
    {
        var adapter = new HybridCpuExecutableChildAdapter();
        var provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(),adapter);
        var kernel = new RuntimeKernel(provider);
        var manifest = new SingProcessManifestV1(new(911),new(912),1,"kernel-artifact-oracle",ExecutionRole.Sip,MemoryProfile.SipRegion);
        var processResult = kernel.CreateProcess(manifest); Check(processResult.IsSuccess,processResult.Message);
        var process = processResult.Value!; var owner = new ProcessHandle(manifest.ProcessId,manifest.Generation);
        var budgetCap = kernel.MintCapability(process.DomainId,owner,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(owner,budgetCap,owner,"artifact-entry",[new(ServiceBudgetDimension.ExternalOperations,1),
            new(ServiceBudgetDimension.OwnedMemoryBytes,32 * 1024 * 1024),new(ServiceBudgetDimension.PinnedMappedMemoryBytes,16 * 1024 * 1024),
            new(ServiceBudgetDimension.GuestMemoryBytes,16 * 1024 * 1024),new(ServiceBudgetDimension.RegionUses,8)]).Value!;
        ulong Used(ServiceBudgetDimension dimension) => kernel.QueryBudget(budget.ProcessBudget).Value!.Usage.Single(x=>x.Dimension==dimension).Used;
        var buffer = kernel.AllocateBuffer<byte>(owner,16 * 1024 * 1024).Value!;
        var create = kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var vm = kernel.CreateVirtualDomain(owner,create,new(1,16 * 1024 * 1024)).Value;
        Check(kernel.ConfigureVirtualDomain(owner,vm.Domain,vm.ConfigureCapability).IsSuccess,"configure");
        var regionCap = kernel.MintCapability(process.DomainId,owner,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),CapabilityRights.Map|CapabilityRights.Read|CapabilityRights.Execute).Value!.CapabilityId;
        var mapped = kernel.MapGuestRegion(owner,vm.Domain,vm.MemoryCapability,regionCap,buffer.Handle,new(0,16 * 1024 * 1024),GuestMemoryAccess.Read|GuestMemoryAccess.Execute,16 * 1024 * 1024);
        Check(mapped.IsSuccess,mapped.Message);
        var image = SoftwareSecureArtifactOracle.Image();
        var bound = kernel.BindVirtualDomainExecutable(owner,vm.Domain,vm.ExecuteCapability,mapped.Value.Mapping,image.PackageBytes,32);
        Check(bound.IsSuccess,bound.Message);
        var execute = kernel.MintCapability(process.DomainId,owner,ResourceKind.SecureCompute,RuntimeKernel.SoftwareSecureRegisterLocalResource(vm.Domain),CapabilityRights.Execute).Value!.CapabilityId;
        if(schedule is "deny-artifact-execute" or "deny-software-profile")
        {
            var noEffect = kernel.MintCapability(process.DomainId,owner,ResourceKind.SecureCompute,RuntimeKernel.SoftwareSecureAdmissionResource(vm.Domain),CapabilityRights.Configure|CapabilityRights.Execute).Value!.CapabilityId;
            var denied = kernel.ExecuteSoftwareSecureArtifactEntry(owner,vm.Domain,vm.ConfigureCapability,
                schedule=="deny-artifact-execute"?vm.ConfigureCapability:vm.ExecuteCapability,
                schedule=="deny-software-profile"?noEffect:execute,image.InitialRegisters!.ReturnAddress,image.InitialRegisters.StackPointer);
            Check(!denied.IsSuccess,"distinct exact Execute permissions required");
            Check(((IDictionary)adapter.GetType().GetField("softwareArtifactEntries",Hidden)!.GetValue(adapter)!).Count==0,"denied permission never invokes native");
            Check(Used(ServiceBudgetDimension.ExternalOperations)==0,"denied permission no operation charge");
            Check(kernel.DestroyVirtualDomain(owner,vm.Domain,vm.ConfigureCapability).IsSuccess,"denied permission original cleanup");
            Check(Used(ServiceBudgetDimension.PinnedMappedMemoryBytes)==0 && Used(ServiceBudgetDimension.GuestMemoryBytes)==0,"denied mapping cleanup");
            Check(kernel.TerminateProcess(owner).IsSuccess,"denied process cleanup");
            Console.WriteLine($"PASS kernel-artifact={schedule} native=0"); return;
        }
        Exception? callbackFault = null;
        object? record = null; int nativeCallbacks = 0, stages = 0; Action? repair = null;
        adapter.GetType().GetProperty("BeforeSoftwareSecureArtifactPublication",Hidden)!.SetValue(adapter,(Action)(() =>
        {
            try
            {
            nativeCallbacks++;
            record = ((IDictionary)typeof(RuntimeKernel).GetField("_softwareSecureRoutes",Hidden)!.GetValue(kernel)!).Values.Cast<object>().Single();
            var operation = (ExternalOperationHandle)Field(record,"Operation")!;
            Check(Field(record,"PublishedResult") is null,"native retire is not OS publication");
            Check(kernel.QueryExternalOperation(owner,operation).Value!.State == ExternalOperationState.Submitted,"original submitted state");
            Check(Used(ServiceBudgetDimension.ExternalOperations)==1,"original operation charge pinned");
            Check(!kernel.CloseGuestRegionMapping(owner,vm.Domain,vm.MemoryCapability,mapped.Value.Mapping).IsSuccess,"mapping custody");
            if (schedule=="destroy-inflight") Check(!kernel.DestroyVirtualDomain(owner,vm.Domain,vm.ConfigureCapability).IsSuccess,"child custody");
            var nativeFact = ((IDictionary)adapter.GetType().GetField("softwareArtifactEntries",Hidden)!.GetValue(adapter)!).Values.Cast<object>().Single();
            var native = (ChildSoftwareSecureEntryObservation)Field(nativeFact,"NativeObservation")!;
            Check(native.ResourcesClosed,"original native reservation cleanup");
            if (schedule != "wrong-operands")
                Check(native.Result?.RegisterLocalRetirement is { EffectCount:1 } retired && native.Register3After==retired.Register3Value &&
                    native.Register3After==unchecked(image.InitialRegisters!.ReturnAddress+image.InitialRegisters.StackPointer),"actual original child x3");
            else Check(native.Result?.RegisterLocalRetirement is null && native.Register3Before==native.Register3After,"wrong operands no sink effect");
            if (schedule=="lost-native-reply") throw new IOException("original native projection reply lost");
            } catch (Exception error) { if(error is not IOException) callbackFault=error; throw; }
        }));
        typeof(RuntimeKernel).GetProperty("BeforeSoftwareSecureRegisterLocalPublication",Hidden)!.SetValue(kernel,(Action)(() =>
        {
            if(schedule=="revoke-execute")Check(kernel.RevokeCapability(execute).IsSuccess,"revoke software source");
            if(schedule=="revoke-region")Check(kernel.RevokeCapability(regionCap).IsSuccess,"revoke region source");
            if(schedule=="revoke-memory")Check(kernel.RevokeCapability(vm.MemoryCapability).IsSuccess,"revoke memory source");
        }));
        typeof(RuntimeKernel).GetProperty("AfterSoftwareSecureRegisterLocalStaging",Hidden)!.SetValue(kernel,(Action)(() =>
        {
            stages++;
            Check(Field(record!,"PublishedResult") is null,"staging is not publication");
            if(schedule=="bridge-child-aba")
            {
                var child = ((IDictionary)typeof(PlatformAuthorityBridge).GetField("_childBindings",Hidden)!.GetValue(kernel.PlatformAuthority)!).Values.Cast<object>().Single();
                var state = child.GetType().GetProperty("State")!; state.SetValue(child,state.GetValue(child));
            }
            if(schedule=="bridge-artifact-route-loss")
            {
                var routes = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_executableArtifacts",Hidden)!.GetValue(kernel.PlatformAuthority)!;
                var key = routes.Keys.Cast<object>().Single(); var value = routes[key]; routes.Remove(key); repair=()=>routes.Add(key,value);
            }
            if(schedule=="lost-staging-reply")throw new IOException("OS staging reply lost before final source commit");
        }));
        ulong left=image.InitialRegisters!.ReturnAddress+(schedule=="wrong-operands"?1UL:0UL),right=image.InitialRegisters.StackPointer;
        var result = kernel.ExecuteSoftwareSecureArtifactEntry(owner,vm.Domain,vm.ConfigureCapability,vm.ExecuteCapability,execute,left,right);
        if (callbackFault is not null) throw callbackFault;
        Check(result.IsSuccess,result.Message);
        adapter.GetType().GetProperty("BeforeSoftwareSecureArtifactPublication",Hidden)!.SetValue(adapter,null);
        repair?.Invoke();
        var recovered = kernel.ResolveSoftwareSecureRegisterLocal(owner,result.Value!.Operation);
        Check(recovered.IsSuccess,recovered.Message);
        bool positive=schedule is "positive" or "closure-loss" or "closure-copy" or "entry-history-loss" or "entry-route-loss";
        Check(recovered.Value!.ResourcesClosed && recovered.Value.WasPublished==positive,"original cleanup/publication disposition");
        Check(recovered.Value.Value==(positive?unchecked(left+right):(ulong?)null),"OS result sink");
        Check(recovered.Value.RegisterLocalCommit==(positive?1UL:0UL),"original retire commit projection");
        Check(Used(ServiceBudgetDimension.ExternalOperations)==0,"independent operation charge release");
        Check(nativeCallbacks==1,"no duplicate native invocation");
        Check(kernel.QueryExternalOperation(owner,result.Value.Operation).Value!.State==ExternalOperationState.Released,"original operation released");
        Check(kernel.ResolveSoftwareSecureRegisterLocal(owner,result.Value.Operation).Value==recovered.Value,"historical query no re-publication");
        Check(!kernel.ExecuteSoftwareSecureArtifactEntry(owner,vm.Domain,vm.ConfigureCapability,vm.ExecuteCapability,execute,left,right).IsSuccess,"fresh operation cannot repeat entry");
        Check(!kernel.StartVirtualDomain(owner,vm.Domain,vm.ExecuteCapability).IsSuccess,"ordinary Start cannot repeat entry");
        if(schedule is "closure-loss" or "closure-copy" or "entry-history-loss" or "entry-route-loss")
        {
            var artifact = ((IDictionary)typeof(PlatformAuthorityBridge).GetField("_originalExecutableArtifactOwners",Hidden)!.GetValue(kernel.PlatformAuthority)!).Values.Cast<object>().Single();
            var property = artifact.GetType().GetProperty("SoftwareEntryClosure")!;
            var retained = (SingPlus.Platform.PlatformSoftwareSecureAdmissionObservation)property.GetValue(artifact)!;
            var originalProperty = artifact.GetType().GetProperty("OriginalSoftwareEntryClosure")!;
            var requestProperty = artifact.GetType().GetProperty("SoftwareEntryRequest")!;
            var retainedRequest = requestProperty.GetValue(artifact);
            var routes = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_softwareArtifactLifetimes",Hidden)!.GetValue(kernel.PlatformAuthority)!;
            var routeKey = routes.Keys.Cast<object>().Single(); var retainedRoute = routes[routeKey];
            if(schedule=="entry-route-loss") routes.Remove(routeKey);
            property.SetValue(artifact,schedule=="closure-copy"?retained with { }:null);
            if(schedule=="entry-history-loss") { originalProperty.SetValue(artifact,null); requestProperty.SetValue(artifact,null); }
            Check(!kernel.DestroyVirtualDomain(owner,vm.Domain,vm.ConfigureCapability).IsSuccess,"original closure evidence custody");
            Check(Used(ServiceBudgetDimension.PinnedMappedMemoryBytes)>0 && Used(ServiceBudgetDimension.GuestMemoryBytes)>0,"damaged closure retains mapped charges");
            property.SetValue(artifact,retained);
            originalProperty.SetValue(artifact,retained); requestProperty.SetValue(artifact,retainedRequest);
            if(schedule=="entry-route-loss") routes.Add(routeKey,retainedRoute);
        }
        var destroyed=kernel.DestroyVirtualDomain(owner,vm.Domain,vm.ConfigureCapability);
        Check(destroyed.IsSuccess,destroyed.Message);
        Check(Used(ServiceBudgetDimension.PinnedMappedMemoryBytes)==0 && Used(ServiceBudgetDimension.GuestMemoryBytes)==0,"independent mapped charges released");
        Check(kernel.TerminateProcess(owner).IsSuccess,"process cleanup");
        Console.WriteLine($"PASS kernel-artifact={schedule} native={nativeCallbacks} staging={stages}");
    }
    static object? Field(object source,string name)=>source.GetType().GetField(name,Hidden)!.GetValue(source);
    static void Check(bool value,string? reason){if(!value)throw new InvalidOperationException(reason);}
}
