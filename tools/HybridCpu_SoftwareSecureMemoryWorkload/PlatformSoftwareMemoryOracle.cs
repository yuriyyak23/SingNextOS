using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using HybridCPU.ExternalRuntime;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Diagnostics;

public static class PlatformSoftwareMemoryOracle
{
    public static readonly string[] Scenarios = ["initializer-alias", "positive", "lost-reply", "foreign-request", "history-loss", "copy-loss", "early-refusal", "refusal-history-loss", "refusal-copy-loss", "lost-native-create", "lost-native-read", "lost-native-close", "pending-native-read", "withdraw-before-read", "withdraw-before-projection"];
    private const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    public static void Run(string scenario)
    {
        var adapter=new HybridCpuExecutableChildAdapter();
        var provider=new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(),adapter);
        var parent=provider.BindDomain(new PlatformDomainIdentity(new DomainId(10),new ProcessHandle(new ProcessId(12),7))).Value!;
        const PlatformChildAuthorityClass rights=PlatformChildAuthorityClass.Lifecycle|PlatformChildAuthorityClass.GuestMemory;
        var child=provider.CreateChildDomain(parent,new(new(1,4096),new(rights,rights))).Value!;
        var region=new PlatformRegionIdentity(new RegionHandle(new RegionId(700),new RegionGeneration(1)),
            new RegionOwner(parent.Subject.DomainId,parent.Subject.ProcessGeneration),4096);
        var backing=provider.MapOwnedRegionSlice(parent,new(region,0,4096,PlatformMemoryAccess.Read)).Value!;
        var mapping=provider.MapGuestRegion(new(child,backing,new(0,4096),PlatformGuestMemoryAccess.Read)).Value!;
        byte[] bytes=new byte[8];BinaryPrimitives.WriteUInt64LittleEndian(bytes,0xFEDCBA9876543210);
        var request=new PlatformSoftwareSecureMemoryReadRequest(mapping,Guid.NewGuid(),1,0,bytes);
        if(scenario=="initializer-alias")
        {
            byte[] original=request.InitialBytes.ToArray();
            Array.Fill(bytes,(byte)0x11);
            if(MemoryMarshal.TryGetArray(request.InitialBytes,out ArraySegment<byte> exposed))
                exposed.AsSpan().Fill(0x22);
            Check(request.InitialBytes.Span.SequenceEqual(original),"public initializer alias must not mutate original retained bytes");
        }
        if(scenario.StartsWith("lost-native-",StringComparison.Ordinal) || scenario=="pending-native-read" || scenario.StartsWith("withdraw-",StringComparison.Ordinal))
        {
            var runtime=DispatchProxy.Create<IMemoryCallbackRuntime,MemoryCallbackProxy>();
            var proxy=(MemoryCallbackProxy)(object)runtime;proxy.Target=adapter;
            string method=scenario is "lost-native-create" or "withdraw-before-read" ? nameof(INeutralSoftwareSecureMemoryReadProvider.CreateSoftwareSecureMemorySource) :
                scenario=="lost-native-close" ? nameof(INeutralSoftwareSecureMemoryReadProvider.CloseOriginalSoftwareSecureMemorySource) : nameof(INeutralSoftwareSecureMemoryReadProvider.ExecuteSoftwareSecureMemoryRead);
            int callbacks=0;using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
            var mappingRoutes=(IDictionary)provider.GetType().GetField("_providerGuestMappings",Hidden)!.GetValue(provider)!;
            var mappingOwner=mappingRoutes[mapping.LeaseId]!;
            proxy.After=(name)=>
            {
                if(name!=method)return;callbacks++;
                if(scenario.StartsWith("withdraw-",StringComparison.Ordinal)){mappingRoutes.Remove(mapping.LeaseId);return;}
                if(scenario!="pending-native-read")throw new IOException("Lost original native reply after actual owner effect.");
                entered.Set();Check(release.Wait(TimeSpan.FromSeconds(10)),"bounded native callback gate");
            };
            var childRuntime=provider.GetType().GetField("_childRuntime",Hidden)!;childRuntime.SetValue(provider,runtime);
            PlatformSoftwareSecureMemoryReadObservation first;
            if(scenario=="pending-native-read")
            {
                var executing=Task.Run(()=>provider.ExecuteSoftwareSecureMemoryRead(request));
                try
                {
                    Check(entered.Wait(TimeSpan.FromSeconds(10)),"original native read/retire before late callback");
                    Check(!provider.UnmapGuestRegion(mapping).IsSuccess && !provider.ExecuteSoftwareSecureMemoryRead(request).ResourcesClosed && !provider.ResolveSoftwareSecureMemoryRead(request).ResourcesClosed,"active platform original pins mapping and refuses duplicate/resolution");
                    Check(!provider.TransitionChildDomain(child,PlatformChildDomainTransition.BeginDrain).IsSuccess,"active source blocks provider transition before neutral callback");
                }
                finally{release.Set();}
                Check(executing.Wait(TimeSpan.FromSeconds(10)),"native callback completion bounded");first=executing.GetAwaiter().GetResult();
            }
            else first=provider.ExecuteSoftwareSecureMemoryRead(request);
            Check(callbacks==1 && first.ResourcesClosed==(scenario is "pending-native-read" or "withdraw-before-projection"),"one original native callback, lost replies retain custody");
            proxy.After=null;
            var reconciled=provider.ResolveSoftwareSecureMemoryRead(request);
            Check(reconciled.ResourcesClosed && (reconciled.Value is not null)==(scenario=="pending-native-read"),"original resolve closes independently without late positive projection");
            Check(proxy.Counts.GetValueOrDefault(nameof(INeutralSoftwareSecureMemoryReadProvider.CreateSoftwareSecureMemorySource))==1 && proxy.Counts.GetValueOrDefault(nameof(INeutralSoftwareSecureMemoryReadProvider.ExecuteSoftwareSecureMemoryRead))==(scenario is "lost-native-create" or "withdraw-before-read"?0:1),"queries never repeat Create/Execute");
            var callbackNative=((IDictionary)adapter.GetType().GetField("softwareMemorySources",Hidden)!.GetValue(adapter)!).Values.Cast<object>().Single();
            var callbackSource=(SoftwareSecureReadOnlyMemorySource)callbackNative.GetType().GetField("Source",Hidden)!.GetValue(callbackNative)!;
            Check(callbackSource.AllocatedBytes==0 && callbackSource.OutstandingPins==0 && callbackSource.ReadCount==(scenario is "lost-native-create" or "withdraw-before-read"?0:1),$"actual original source bytes/pins: scenario={scenario} bytes={callbackSource.AllocatedBytes} pins={callbackSource.OutstandingPins} reads={callbackSource.ReadCount}");
            if(scenario.StartsWith("withdraw-",StringComparison.Ordinal))mappingRoutes.Add(mapping.LeaseId,mappingOwner);
            childRuntime.SetValue(provider,adapter);
            Check(provider.UnmapGuestRegion(mapping).IsSuccess && provider.CloseChildDomain(child).IsSuccess,"independent actual mapping/child close after native reply loss");
            Console.WriteLine($"PASS platform-memory={scenario} nativeCreate=1 nativeRead={callbackSource.ReadCount} bytes=0 callbacks=1");return;
        }
        if(scenario is "early-refusal" or "refusal-history-loss" or "refusal-copy-loss")
        {
            var routes=(IDictionary)provider.GetType().GetField("_providerGuestMappings",Hidden)!.GetValue(provider)!;
            var owner=routes[mapping.LeaseId]!;routes.Remove(mapping.LeaseId);
            var refused=provider.ExecuteSoftwareSecureMemoryRead(request);
            Check(refused.ResourcesClosed && refused.Value is null,"known refusal before native source creation");
            routes.Add(mapping.LeaseId,owner);
            Check(ReferenceEquals(refused,provider.ExecuteSoftwareSecureMemoryRead(request)) && ReferenceEquals(refused,provider.ResolveSoftwareSecureMemoryRead(request)),"repaired mapping cannot revive refused original operation");
            var foreign=new PlatformSoftwareSecureMemoryReadRequest(mapping,request.Operation,1,0,bytes);
            Check(!provider.ResolveSoftwareSecureMemoryRead(foreign).ResourcesClosed,"foreign request cannot borrow original no-effect");
            var refusedRoutes=(IDictionary)provider.GetType().GetField("_softwareMemoryRefusals",Hidden)!.GetValue(provider)!;
            var history=(IList)provider.GetType().GetField("_originalSoftwareMemoryRefusals",Hidden)!.GetValue(provider)!;
            var fact=refusedRoutes[request.Operation]!;
            if(scenario!="early-refusal")
            {
                if(scenario=="refusal-history-loss")history.Clear();
                else{var copy=typeof(object).GetMethod("MemberwiseClone",Hidden)!.Invoke(fact,null)!;refusedRoutes[request.Operation]=copy;history[0]=copy;}
                Check(!provider.ResolveSoftwareSecureMemoryRead(request).ResourcesClosed && !provider.UnmapGuestRegion(mapping).IsSuccess,"damaged refusal history never proves closure");
                if(scenario=="refusal-history-loss")history.Add(fact);else{refusedRoutes[request.Operation]=fact;history[0]=fact;}
            }
            Check(((IDictionary)adapter.GetType().GetField("softwareMemorySources",Hidden)!.GetValue(adapter)!).Count==0,"refusal has zero native source/domain creations");
            Check(provider.UnmapGuestRegion(mapping).IsSuccess && provider.CloseChildDomain(child).IsSuccess,"original no-effect cleanup independent of mapping availability");
            Console.WriteLine($"PASS platform-memory={scenario} nativeCreate=0 originalNoEffect=true");return;
        }
        PlatformSoftwareSecureMemoryReadObservation observed;
        if(scenario=="lost-reply")
        {
            try{_=provider.ExecuteSoftwareSecureMemoryRead(request);throw new IOException("Lost completed original platform reply.");}
            catch(IOException){observed=provider.ResolveSoftwareSecureMemoryRead(request);}
        }
        else observed=provider.ExecuteSoftwareSecureMemoryRead(request);
        Check(observed.ResourcesClosed && observed.Value==0xFEDCBA9876543210 && observed.MachineAttempt>0 && observed.Commit>0 && !observed.IsExecutionAllowed,"actual platform→neutral→packaged canonical load/cleanup");
        Check(ReferenceEquals(observed,provider.ExecuteSoftwareSecureMemoryRead(request)) && ReferenceEquals(observed,provider.ResolveSoftwareSecureMemoryRead(request)),"one original platform result");
        if(scenario=="foreign-request")
            Check(!provider.ResolveSoftwareSecureMemoryRead(new(mapping,request.Operation,1,0,bytes)).ResourcesClosed,"foreign original cannot borrow closure");
        var nativeFacts=(IDictionary)adapter.GetType().GetField("softwareMemorySources",Hidden)!.GetValue(adapter)!;
        var native= nativeFacts.Values.Cast<object>().Single();
        var source=(SoftwareSecureReadOnlyMemorySource)native.GetType().GetField("Source",Hidden)!.GetValue(native)!;
        Check(source.ReadCount==1 && source.OutstandingPins==0 && source.AllocatedBytes==0,"actual native source owner reclaimed once");
        if(scenario is "history-loss" or "copy-loss")
        {
            var routes=(IDictionary)provider.GetType().GetField("_softwareMemoryFacts",Hidden)!.GetValue(provider)!;
            var history=(IList)provider.GetType().GetField("_softwareMemoryOwners",Hidden)!.GetValue(provider)!;
            var original=routes[request.Operation]!;
            if(scenario=="history-loss")history.Clear();
            else{var copy=typeof(object).GetMethod("MemberwiseClone",Hidden)!.Invoke(original,null)!;routes[request.Operation]=copy;history[0]=copy;}
            Check(!provider.ResolveSoftwareSecureMemoryRead(request).ResourcesClosed && !provider.UnmapGuestRegion(mapping).IsSuccess,"unknown platform history retains dependent custody");
            if(scenario=="history-loss")history.Add(original);else{routes[request.Operation]=original;history[0]=original;}
        }
        Check(provider.UnmapGuestRegion(mapping).IsSuccess,"independent original mapping release");
        Check(provider.CloseChildDomain(child).IsSuccess,"independent original child close");
        Console.WriteLine($"PASS platform-memory={scenario} nativeReads=1 nativeBytes=0 closed=true");
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}

    public interface IMemoryCallbackRuntime : INeutralChildDomainProvider, INeutralSoftwareSecureMemoryReadProvider { }
    public class MemoryCallbackProxy : DispatchProxy
    {
        public HybridCpuExecutableChildAdapter Target=null!;
        public Action<string>? After;
        public Dictionary<string,int> Counts=[];
        protected override object? Invoke(MethodInfo? method,object?[]? args)
        {
            var call=method!;Counts[call.Name]=Counts.GetValueOrDefault(call.Name)+1;
            object? result;
            try{result=call.Invoke(Target,args);}catch(TargetInvocationException e) when(e.InnerException is not null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
            After?.Invoke(call.Name);return result;
        }
    }
}



