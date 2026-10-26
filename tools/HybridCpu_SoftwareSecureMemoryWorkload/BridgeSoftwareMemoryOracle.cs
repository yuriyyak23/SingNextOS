using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using HybridCPU.ExternalRuntime;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Diagnostics;

public static class BridgeSoftwareMemoryOracle
{
    public static readonly string[] Scenarios=["initializer-alias", "positive","lost-reply","foreign-intent","history-loss","early-refusal","refusal-history-loss","refusal-copy-loss","lost-provider-reply","unknown-provider-query","pending-provider-reply","guest-aba","child-aba","parent-aba","root-aba","cancel-pending"];
    private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    public static void Run(string scenario)
    {
        var adapter=new HybridCpuExecutableChildAdapter();
        var provider=new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(),adapter);
        var kernel=new RuntimeKernel(provider);
        var manifest=new SingProcessManifestV1(new(921),new(922),1,"bridge-memory-oracle",ExecutionRole.Sip,MemoryProfile.SipRegion);
        var process=kernel.CreateProcess(manifest).Value!;var owner=new ProcessHandle(manifest.ProcessId,manifest.Generation);
        var budgetCap=kernel.MintCapability(process.DomainId,owner,ResourceKind.KernelService,CapabilityResourceIds.BudgetAdministration,CapabilityRights.Configure).Value!.CapabilityId;
        Check(kernel.AdmitProcessBudget(owner,budgetCap,owner,"bridge-memory",[new(ServiceBudgetDimension.ExternalOperations,1),new(ServiceBudgetDimension.OwnedMemoryBytes,8192),new(ServiceBudgetDimension.PinnedMappedMemoryBytes,4096),new(ServiceBudgetDimension.GuestMemoryBytes,4096),new(ServiceBudgetDimension.RegionUses,8)]).IsSuccess,"existing budget admission");
        var buffer=kernel.AllocateBuffer<byte>(owner,4096).Value!;
        var create=kernel.MintCapability(process.DomainId,owner,ResourceKind.Virtualization,VirtualizationResourceIds.Create,CapabilityRights.Configure).Value!.CapabilityId;
        var vm=kernel.CreateVirtualDomain(owner,create,new(1,4096)).Value;
        Check(kernel.ConfigureVirtualDomain(owner,vm.Domain,vm.ConfigureCapability).IsSuccess,"existing configured child");
        var regionCap=kernel.MintCapability(process.DomainId,owner,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId),CapabilityRights.Map|CapabilityRights.Read).Value!.CapabilityId;
        var mapped=kernel.MapGuestRegion(owner,vm.Domain,vm.MemoryCapability,regionCap,buffer.Handle,new(0,4096),GuestMemoryAccess.Read);
        Check(mapped.IsSuccess,mapped.Message);
        var guests=(IDictionary)kernel.PlatformAuthority.GetType().GetField("_guestBindings",Hidden)!.GetValue(kernel.PlatformAuthority)!;
        var guest=guests.Values.Cast<object>().Single();
        var mapping=(PlatformGuestMapping)guest.GetType().GetProperty("Mapping")!.GetValue(guest)!;
        byte[] bytes=new byte[8];BinaryPrimitives.WriteUInt64LittleEndian(bytes,0xF102030405060708);
        var intent=new SoftwareSecureInitializedLoadIntent(mapping,Guid.NewGuid(),1,0,bytes);
        if(scenario=="initializer-alias")
        {
            byte[] original=intent.InitialBytes.ToArray();
            Array.Fill(bytes,(byte)0x11);
            if(MemoryMarshal.TryGetArray(intent.InitialBytes,out ArraySegment<byte> exposed))
                exposed.AsSpan().Fill(0x22);
            Check(intent.InitialBytes.Span.SequenceEqual(original),"public initializer alias must not mutate original retained bytes");
        }
        if(scenario is "lost-provider-reply" or "unknown-provider-query" or "pending-provider-reply" or "cancel-pending" || scenario.EndsWith("-aba",StringComparison.Ordinal))
        {
            var runtime=DispatchProxy.Create<IBridgeProvider,BridgeProviderProxy>();
            var proxy=(BridgeProviderProxy)(object)runtime;proxy.Target=provider;
            using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();int callbacks=0;
            proxy.After=name=>
            {
                if(scenario=="unknown-provider-query" && name==nameof(IPlatformSoftwareSecureMemoryReadProvider.ResolveSoftwareSecureMemoryRead))throw new IOException("Original provider lookup unavailable.");
                if(name!=nameof(IPlatformSoftwareSecureMemoryReadProvider.ExecuteSoftwareSecureMemoryRead))return;
                callbacks++;
                if(scenario=="cancel-pending")
                {
                    Check(!kernel.PlatformAuthority.CancelSoftwareSecureInitializedLoad(new(mapping,intent.Operation,1,0,bytes)),"foreign cancellation cannot withdraw original result");
                    Check(kernel.PlatformAuthority.CancelSoftwareSecureInitializedLoad(intent),"original pending cancellation linearizes before bridge projection");
                    Check((int)guest.GetType().GetProperty("PendingSecureRegionEffects")!.GetValue(guest)! == 1,"cancellation does not release custody");return;
                }
                if(scenario.EndsWith("-aba",StringComparison.Ordinal))
                {
                    object target=guest;string property="Closure";
                    if(scenario!="guest-aba")
                    {
                        var source=guest.GetType().GetProperty("AdmissionSource")!.GetValue(guest)!;
                        target=source.GetType().GetProperty(scenario=="child-aba"?"Child":scenario=="parent-aba"?"Parent":"Root")!.GetValue(source)!;
                        property=scenario=="child-aba"?"State":scenario=="parent-aba"?"ClosureState":"AuthorityState";
                    }
                    var state=target.GetType().GetProperty(property)!;var original=state.GetValue(target);
                    state.SetValue(target,original);state.SetValue(target,original);return;
                }
                if(scenario=="lost-provider-reply")throw new IOException("Lost provider reply after actual source retire/closure.");
                if(scenario=="pending-provider-reply"){entered.Set();Check(release.Wait(TimeSpan.FromSeconds(10)),"bounded provider reply wait");}
            };
            var providerField=kernel.PlatformAuthority.GetType().GetField("_provider",Hidden)!;providerField.SetValue(kernel.PlatformAuthority,runtime);
            SoftwareSecureInitializedLoadObservation first;
            if(scenario=="pending-provider-reply")
            {
                var execution=Task.Run(()=>kernel.PlatformAuthority.ExecuteSoftwareSecureInitializedLoad(intent));
                try
                {
                    Check(entered.Wait(TimeSpan.FromSeconds(10)),"actual provider finished before pending bridge reply");
                    Check((int)guest.GetType().GetProperty("PendingSecureRegionEffects")!.GetValue(guest)! == 1,"original guest pending remains while reply pending");
                    Check(!kernel.CloseGuestRegionMapping(owner,vm.Domain,vm.MemoryCapability,mapped.Value.Mapping).IsSuccess && !kernel.PlatformAuthority.ResolveSoftwareSecureInitializedLoad(intent).ResourcesClosed,"pending provider reply blocks local mapping close and active query");
                }
                finally{release.Set();}
                Check(execution.Wait(TimeSpan.FromSeconds(10)),"original bridge execution completes after pending reply");first=execution.GetAwaiter().GetResult();
            }
            else first=kernel.PlatformAuthority.ExecuteSoftwareSecureInitializedLoad(intent);
            Check(callbacks==1 && first.ResourcesClosed==(scenario is "pending-provider-reply" or "cancel-pending" || scenario.EndsWith("-aba",StringComparison.Ordinal)),"one actual provider invocation, ambiguous reply retains custody");
            if(!first.ResourcesClosed)Check((int)guest.GetType().GetProperty("PendingSecureRegionEffects")!.GetValue(guest)! == 1,"unknown reply retains original guest pending");
            proxy.After=null;
            var reconciled=kernel.PlatformAuthority.ResolveSoftwareSecureInitializedLoad(intent);
            Check(reconciled.ResourcesClosed && (reconciled.Value is not null)==(scenario=="pending-provider-reply") && !reconciled.IsPublished,"resolve closes custody without late positive or publication");
            Check(proxy.Executes==1 && (int)guest.GetType().GetProperty("PendingSecureRegionEffects")!.GetValue(guest)! == 0,"no reissue, pending owner independently released");
            var originalChild=((IDictionary)kernel.PlatformAuthority.GetType().GetField("_childBindings",Hidden)!.GetValue(kernel.PlatformAuthority)!).Values.Cast<object>().Single();
            Check((int)originalChild.GetType().GetProperty("PendingChildEffects")!.GetValue(originalChild)! == 0,"original child pending owner independently released");
            var nativeOwner=((IDictionary)adapter.GetType().GetField("softwareMemorySources",Hidden)!.GetValue(adapter)!).Values.Cast<object>().Single();
            var nativeSource=(SoftwareSecureReadOnlyMemorySource)nativeOwner.GetType().GetField("Source",Hidden)!.GetValue(nativeOwner)!;
            var nativeProvider=(SoftwareSecureComputeProvider)nativeOwner.GetType().GetField("Provider",Hidden)!.GetValue(nativeOwner)!;
            Check(nativeSource.ReadCount==1 && nativeSource.AllocatedBytes==0 && nativeSource.OutstandingPins==0 && nativeProvider.OutstandingReservations==0,"actual original native source/read/page/provider custody closed");
            providerField.SetValue(kernel.PlatformAuthority,provider);
            Check(kernel.CloseGuestRegionMapping(owner,vm.Domain,vm.MemoryCapability,mapped.Value.Mapping).IsSuccess && kernel.DestroyVirtualDomain(owner,vm.Domain,vm.ConfigureCapability).IsSuccess && kernel.TerminateProcess(owner).IsSuccess,"independent original mapping/child/process cleanup");
            Console.WriteLine($"PASS bridge-memory={scenario} providerExecute=1 pending=0 published=false");return;
        }
        if(scenario is "early-refusal" or "refusal-history-loss" or "refusal-copy-loss")
        {
            guests.Remove(mapping.MappingId);
            var refused=kernel.PlatformAuthority.ExecuteSoftwareSecureInitializedLoad(intent);
            guests.Add(mapping.MappingId,guest);
            Check(refused.ResourcesClosed && refused.Value is null && ReferenceEquals(refused,kernel.PlatformAuthority.ExecuteSoftwareSecureInitializedLoad(intent)) && ReferenceEquals(refused,kernel.PlatformAuthority.ResolveSoftwareSecureInitializedLoad(intent)),"exact original bridge refusal terminal after mapping repair");
            Check(!kernel.PlatformAuthority.ResolveSoftwareSecureInitializedLoad(new(mapping,intent.Operation,1,0,bytes)).ResourcesClosed,"foreign intent cannot borrow refusal");
            var routes=(IDictionary)kernel.PlatformAuthority.GetType().GetField("_initializedLoadRefusals",Hidden)!.GetValue(kernel.PlatformAuthority)!;
            var history=(IList)kernel.PlatformAuthority.GetType().GetField("_originalInitializedLoadRefusals",Hidden)!.GetValue(kernel.PlatformAuthority)!;
            var fact=routes[intent.Operation]!;
            if(scenario!="early-refusal")
            {
                if(scenario=="refusal-history-loss")history.Clear();
                else{var copy=typeof(object).GetMethod("MemberwiseClone",Hidden)!.Invoke(fact,null)!;routes[intent.Operation]=copy;history[0]=copy;}
                Check(!kernel.PlatformAuthority.ResolveSoftwareSecureInitializedLoad(intent).ResourcesClosed && !kernel.CloseGuestRegionMapping(owner,vm.Domain,vm.MemoryCapability,mapped.Value.Mapping).IsSuccess,"unknown refusal custody denies cleanup before quarantine");
                if(scenario=="refusal-history-loss")history.Add(fact);else{routes[intent.Operation]=fact;history[0]=fact;}
            }
            Check(((IDictionary)adapter.GetType().GetField("softwareMemorySources",Hidden)!.GetValue(adapter)!).Count==0 && (int)guest.GetType().GetProperty("PendingSecureRegionEffects")!.GetValue(guest)! == 0,"local refusal no native source or pending admission");
            Check(kernel.CloseGuestRegionMapping(owner,vm.Domain,vm.MemoryCapability,mapped.Value.Mapping).IsSuccess && kernel.DestroyVirtualDomain(owner,vm.Domain,vm.ConfigureCapability).IsSuccess && kernel.TerminateProcess(owner).IsSuccess,"independent original cleanup after exact refusal repair");
            Console.WriteLine($"PASS bridge-memory={scenario} nativeCreate=0 pending=0 originalNoEffect=true");return;
        }
        SoftwareSecureInitializedLoadObservation result;
        if(scenario=="lost-reply")
        {
            try{_=kernel.PlatformAuthority.ExecuteSoftwareSecureInitializedLoad(intent);throw new IOException("Lost original completed bridge response.");}
            catch(IOException){result=kernel.PlatformAuthority.ResolveSoftwareSecureInitializedLoad(intent);}
        }
        else result=kernel.PlatformAuthority.ExecuteSoftwareSecureInitializedLoad(intent);
        Check(result.ResourcesClosed && result.Value==0xF102030405060708 && result.MachineAttempt>0 && result.Commit>0 && !result.IsPublished,"actual bridge→provider→neutral→packaged retire, no OS publication");
        Check(ReferenceEquals(result,kernel.PlatformAuthority.ResolveSoftwareSecureInitializedLoad(intent)),"original bridge observation");
        Check((int)guest.GetType().GetProperty("PendingSecureRegionEffects")!.GetValue(guest)! == 0,"original guest pending owner released");
        if(scenario=="foreign-intent")Check(!kernel.PlatformAuthority.ResolveSoftwareSecureInitializedLoad(new(mapping,intent.Operation,1,0,bytes)).ResourcesClosed,"foreign intent cannot borrow original closure");
        if(scenario=="history-loss")
        {
            var history=(IList)kernel.PlatformAuthority.GetType().GetField("_originalInitializedLoads",Hidden)!.GetValue(kernel.PlatformAuthority)!;
            var retained=history[0]!;history.Clear();
            Check(!kernel.PlatformAuthority.ResolveSoftwareSecureInitializedLoad(intent).ResourcesClosed,"unknown bridge history not closure");
            Check(!kernel.CloseGuestRegionMapping(owner,vm.Domain,vm.MemoryCapability,mapped.Value.Mapping).IsSuccess,"unknown bridge history retains original mapping custody");history.Add(retained);
        }
        var mappingClosed=kernel.CloseGuestRegionMapping(owner,vm.Domain,vm.MemoryCapability,mapped.Value.Mapping); Check(mappingClosed.IsSuccess,$"independent mapping closure: {mappingClosed.Error} {mappingClosed.Message}");
        Check(kernel.DestroyVirtualDomain(owner,vm.Domain,vm.ConfigureCapability).IsSuccess,"independent child closure");
        Check(kernel.TerminateProcess(owner).IsSuccess,"original process cleanup");
        Console.WriteLine($"PASS bridge-memory={scenario} pending=0 historicalValue=true published=false");
    }
    private static void Check(bool condition,string? message){if(!condition)throw new InvalidOperationException(message);}
    public interface IBridgeProvider:IPlatformAuthorityProvider,IPlatformSoftwareSecureMemoryReadProvider { }
    public class BridgeProviderProxy:DispatchProxy
    {
        public HybridCpuPlatformAuthorityProvider Target=null!;
        public Action<string>? After;
        public int Executes;
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            var call=method!;if(call.Name==nameof(IPlatformSoftwareSecureMemoryReadProvider.ExecuteSoftwareSecureMemoryRead))Executes++;
            object? result;
            try{result=call.Invoke(Target,args);}catch(TargetInvocationException e)when(e.InnerException is not null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
            After?.Invoke(call.Name);return result;
        }
    }
}



