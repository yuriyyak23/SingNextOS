using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using HybridCPU.ExternalRuntime;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Diagnostics;

public static class SoftwareSecureMemoryOracle
{
    public static readonly string[] Scenarios = ["initializer-alias", "positive", "offset", "lost-reply", "duplicate", "foreign-source", "foreign-request", "source-cancel", "source-route-loss", "read-history-loss", "range", "unsupported", "child-aba", "lost-source-reply", "original-request-close", "source-copy-loss", "source-pointer-loss", "close-before-pending-load", "load-before-pending-close", "precreate-refusal-cleanup", "early-refusal", "refusal-history-loss", "refusal-route-loss"];
    public static void Run(string scenario)
    {
        Check(Scenarios.Contains(scenario), "known schedule");
        var host = new NeutralDomainRuntimeFacade(); var parent = host.Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var adapter = new HybridCpuExecutableChildAdapter();
        const NeutralChildAuthorityClass rights = NeutralChildAuthorityClass.Lifecycle | NeutralChildAuthorityClass.GuestMemory;
        var childResult = adapter.CreateChildDomain(parent, new(new(1,4096), new(rights,rights)));
        Check(childResult.IsSuccess, childResult.Reason); var child = childResult.Value;
        var region = host.MapOwnedRegion(parent, new(0,4096,NeutralMemoryAccess.Read)).Lease;
        var mapResult = adapter.MapGuestRegion(new(child, region, new(0,4096), NeutralGuestMemoryAccess.Read));
        Check(mapResult.IsSuccess, mapResult.Reason); var mapping = mapResult.Value;
        const ulong word = 0xFEDCBA9876543210;
        byte[] bytes = new byte[16]; BinaryPrimitives.WriteUInt64LittleEndian(bytes, word); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), ~word);
        if(scenario is "early-refusal" or "refusal-history-loss" or "refusal-route-loss")
        {
            var original=new NeutralSoftwareSecureMemorySourceRequest(mapping,Guid.NewGuid(),1,4096,bytes);
            Check(adapter.CreateSoftwareSecureMemorySource(original) is null && adapter.CloseOriginalSoftwareSecureMemorySource(original) && adapter.IsOriginalSoftwareSecureMemorySourceClosed(original),"early original refusal is terminal no-effect");
            var foreign=new NeutralSoftwareSecureMemorySourceRequest(mapping,original.Operation,1,0,bytes);
            Check(adapter.CreateSoftwareSecureMemorySource(foreign) is null && !adapter.IsOriginalSoftwareSecureMemorySourceClosed(foreign),"same operation foreign request cannot remint or borrow no-effect");
            Action? repair=null;
            if(scenario == "refusal-history-loss")
            {
                var history=(IList)adapter.GetType().GetField("originalSoftwareMemoryRefusals",Hidden)!.GetValue(adapter)!;history.Clear();repair=()=>history.Add(original);
            }
            if(scenario == "refusal-route-loss")
            {
                var routes=(IDictionary)adapter.GetType().GetField("softwareMemoryRefusals",Hidden)!.GetValue(adapter)!;routes.Remove(original.Operation);repair=()=>routes.Add(original.Operation,original);
            }
            if(repair is not null)
            {
                Check(!adapter.IsOriginalSoftwareSecureMemorySourceClosed(original) && !adapter.UnmapGuestRegion(mapping).IsSuccess && adapter.CreateSoftwareSecureMemorySource(foreign) is null,"unknown original refusal history denies cleanup and new source");repair();
            }
            Check(((IDictionary)adapter.GetType().GetField("softwareMemorySources",Hidden)!.GetValue(adapter)!).Count==0,"early refusal never creates native source owner");
            Check(adapter.CreateSoftwareSecureMemorySource(original) is null && adapter.UnmapGuestRegion(mapping).IsSuccess && adapter.CloseChildDomain(child).IsSuccess,"terminal refusal preserved with independent mapping/child release");
            Console.WriteLine($"PASS neutral-memory={scenario} nativeCreate=0 sourceBytes=0 originalNoEffect=true");return;
        }
        if (scenario == "range")
            Check(adapter.CreateSoftwareSecureMemorySource(new(mapping,Guid.NewGuid(),1,4096,bytes)) is null, "initial source cannot escape original mapping range");
        var initialization = new NeutralSoftwareSecureMemorySourceRequest(mapping, Guid.NewGuid(), 1, 0, bytes);
        if(scenario=="initializer-alias")
        {
            byte[] original=initialization.InitialBytes.ToArray();
            Array.Fill(bytes,(byte)0x11);
            if(MemoryMarshal.TryGetArray(initialization.InitialBytes,out ArraySegment<byte> exposed))
                exposed.AsSpan().Fill(0x22);
            Check(initialization.InitialBytes.Span.SequenceEqual(original),"public initializer alias must not mutate original retained bytes");
        }
        if(scenario == "precreate-refusal-cleanup")
        {
            var ownedMapping=((IDictionary)adapter.GetType().GetField("originalMappings",Hidden)!.GetValue(adapter)!)[mapping.Handle]!;
            var pending=ownedMapping.GetType().GetProperty("ClosureMayHaveEffect")!;pending.SetValue(ownedMapping,true);
            Check(adapter.CreateSoftwareSecureMemorySource(initialization) is null,"original map no longer available before native Create");
            Check(adapter.CloseOriginalSoftwareSecureMemorySource(initialization) && adapter.IsOriginalSoftwareSecureMemorySourceClosed(initialization),"known pre-create refusal independently closes original source request");
            var retained=((IDictionary)adapter.GetType().GetField("softwareMemorySources",Hidden)!.GetValue(adapter)!)[initialization.Operation]!;
            Check(Field(retained,"Domain") is null && Field(retained,"Source") is null,"pre-create refusal allocates no native source/domain");
            pending.SetValue(ownedMapping,false);
            Check(adapter.UnmapGuestRegion(mapping).IsSuccess && adapter.CloseChildDomain(child).IsSuccess,"pre-create refusal does not pin independent mapping/child forever");
            Console.WriteLine("PASS neutral-memory=precreate-refusal-cleanup nativeCreate=0 sourceBytes=0");return;
        }
        NeutralSoftwareSecureMemorySourceLease? lease;
        if(scenario == "lost-source-reply")
        {
            try { _=adapter.CreateSoftwareSecureMemorySource(initialization); throw new IOException("Lost source reply after actual mint."); }
            catch(IOException) { lease=adapter.ResolveSoftwareSecureMemorySource(initialization); }
        }
        else lease = adapter.CreateSoftwareSecureMemorySource(initialization);
        Check(lease is not null && ReferenceEquals(lease, adapter.ResolveSoftwareSecureMemorySource(initialization)), "original source mint/query");
        if (scenario == "lost-source-reply")
        {
            Check(ReferenceEquals(lease, adapter.ResolveSoftwareSecureMemorySource(initialization)) && ReferenceEquals(lease,adapter.CreateSoftwareSecureMemorySource(initialization)), "lost original source reply never remints");
        }
        bytes.AsSpan().Fill(0xEE);
        Check(!adapter.UnmapGuestRegion(mapping).IsSuccess && !adapter.CloseChildDomain(child).IsSuccess, "actual source pins original mapping/child");
        var fact = ((IDictionary)adapter.GetType().GetField("softwareMemorySources",Hidden)!.GetValue(adapter)!)[initialization.Operation]!;
        var nativeSource = (SoftwareSecureReadOnlyMemorySource)Field(fact,"Source")!;
        var provider = (SoftwareSecureComputeProvider)Field(fact,"Provider")!;
        if (scenario == "foreign-source")
        {
            var copy = lease! with { };
            Check(adapter.ExecuteSoftwareSecureMemoryRead(new(copy, Guid.NewGuid(), 1, 0)).Status == NeutralSoftwareSecureMemoryReadStatus.Denied && nativeSource.ReadCount == 0, "foreign source facts are not authority");
        }
        if (scenario == "source-cancel") Check(adapter.CancelSoftwareSecureMemorySource(lease!), "original cancel");
        if (scenario == "child-aba")
        {
            var owner = Field(fact,"Child")!; var state = owner.GetType().GetProperty("State")!;
            state.SetValue(owner, NeutralChildDomainState.Parked); state.SetValue(owner, NeutralChildDomainState.Created);
        }
        var request = new NeutralSoftwareSecureMemoryReadRequest(lease!, Guid.NewGuid(), 1, scenario == "offset" ? 8UL : scenario == "range" ? 16UL : 0UL, scenario == "unsupported" ? 2U : 1U);
        NeutralSoftwareSecureMemoryReadObservation result;
        if (scenario == "close-before-pending-load")
        {
            var gate=adapter.GetType().GetField("sync",Hidden)!.GetValue(adapter)!;
            using var entered=new ManualResetEventSlim(); Task<NeutralSoftwareSecureMemoryReadObservation> pending;
            lock(gate)
            {
                pending=Task.Run(()=>{entered.Set();return adapter.ExecuteSoftwareSecureMemoryRead(request);});
                Check(entered.Wait(TimeSpan.FromSeconds(10)),"bounded load invocation pending behind original adapter gate");
                Check(adapter.CloseSoftwareSecureMemorySource(lease!) is not null && nativeSource.AllocatedBytes==0,"source close linearized before pending load");
            }
            Check(pending.Wait(TimeSpan.FromSeconds(10)),"pending load terminates after close");result=pending.GetAwaiter().GetResult();
        }
        else if(scenario == "load-before-pending-close")
        {
            var gate=adapter.GetType().GetField("sync",Hidden)!.GetValue(adapter)!;
            using var entered=new ManualResetEventSlim();Task<NeutralSoftwareSecureMemorySourceClosure?> pending;
            lock(gate)
            {
                pending=Task.Run(()=>{entered.Set();return adapter.CloseSoftwareSecureMemorySource(lease!);});
                Check(entered.Wait(TimeSpan.FromSeconds(10)),"bounded close invocation pending behind original adapter gate");
                result=adapter.ExecuteSoftwareSecureMemoryRead(request);
                Check(result.Status==NeutralSoftwareSecureMemoryReadStatus.LoadRetired && nativeSource.AllocatedBytes>0,"load retired before pending independent source close");
            }
            Check(pending.Wait(TimeSpan.FromSeconds(10)) && pending.GetAwaiter().GetResult() is not null,"pending source close terminates");
        }
        else if(scenario == "lost-reply")
        {
            try { _=adapter.ExecuteSoftwareSecureMemoryRead(request); throw new IOException("Lost reply after actual native retire/cleanup."); }
            catch(IOException) { result=adapter.ResolveSoftwareSecureMemoryRead(request); }
        }
        else result = adapter.ExecuteSoftwareSecureMemoryRead(request);
        bool positive = scenario is not ("source-cancel" or "child-aba" or "range" or "unsupported" or "close-before-pending-load");
        Check((result.Status == NeutralSoftwareSecureMemoryReadStatus.LoadRetired) == positive && !result.IsExecutionAllowed, "historical standalone load boundary");
        if (positive) Check(result.Value == (scenario == "offset" ? ~word : word) && result.MachineAttempt > 0 && result.Commit > 0 && result.ResourcesClosed, "actual packaged canonical load");
        if (scenario is "lost-reply" or "duplicate") Check(ReferenceEquals(result, adapter.ResolveSoftwareSecureMemoryRead(request)) && ReferenceEquals(result, adapter.ExecuteSoftwareSecureMemoryRead(request)), "lost reply/duplicate are pure original lookup");
        if (scenario == "foreign-request")
            Check(adapter.ResolveSoftwareSecureMemoryRead(new(lease!, request.Operation, 1, request.Address)).Status == NeutralSoftwareSecureMemoryReadStatus.CleanupRequired, "foreign request cannot recover original");
        if (scenario == "source-route-loss")
        {
            var routes = (IDictionary)adapter.GetType().GetField("softwareMemorySources",Hidden)!.GetValue(adapter)!;
            routes.Remove(initialization.Operation);
            Check(adapter.CloseSoftwareSecureMemorySource(lease!) is null && !adapter.UnmapGuestRegion(mapping).IsSuccess && nativeSource.AllocatedBytes > 0, "unknown source retains original page/custody");
            routes.Add(initialization.Operation,fact);
        }
        if (scenario == "read-history-loss")
        {
            var history = (IList)Field(fact,"OriginalReads")!; var retained=history[0]!; history.Clear();
            Check(adapter.CloseSoftwareSecureMemorySource(lease!) is null && nativeSource.AllocatedBytes > 0, "unknown read retains source custody"); history.Add(retained);
        }
        if (scenario == "source-copy-loss")
        {
            var routes=(IDictionary)adapter.GetType().GetField("softwareMemorySources",Hidden)!.GetValue(adapter)!;
            var history=(IList)adapter.GetType().GetField("originalSoftwareMemorySources",Hidden)!.GetValue(adapter)!;
            var copy=typeof(object).GetMethod("MemberwiseClone",Hidden)!.Invoke(fact,null)!;
            int index=history.IndexOf(fact); routes[initialization.Operation]=copy; history[index]=copy;
            Check(adapter.CloseSoftwareSecureMemorySource(lease!) is null && !adapter.UnmapGuestRegion(mapping).IsSuccess && nativeSource.AllocatedBytes>0, "equal copied source cannot replace original custody");
            routes[initialization.Operation]=fact; history[index]=fact;
        }
        if (scenario == "source-pointer-loss")
        {
            fact.GetType().GetField("Source",Hidden)!.SetValue(fact,null);
            Check(adapter.CloseSoftwareSecureMemorySource(lease!) is null && nativeSource.AllocatedBytes>0 && !adapter.UnmapGuestRegion(mapping).IsSuccess, "original source pointer loss retains original storage");
            fact.GetType().GetField("Source",Hidden)!.SetValue(fact,nativeSource);
        }
        Check(nativeSource.ReadCount == (positive ? 1 : 0) && nativeSource.OutstandingPins == 0 && provider.OutstandingReservations == 0, "actual native reads/pins/holds");
        if (scenario == "original-request-close")
            Check(adapter.CloseOriginalSoftwareSecureMemorySource(initialization) && adapter.IsOriginalSoftwareSecureMemorySourceClosed(initialization) && nativeSource.AllocatedBytes == 0, "independent cleanup from original mint request");
        var closed = adapter.CloseSoftwareSecureMemorySource(lease!);
        Check(closed is {NoFurtherUse:true} && ReferenceEquals(closed,adapter.ResolveSoftwareSecureMemorySourceClosure(lease!)) && nativeSource.AllocatedBytes == 0, "original independent source zeroing");
        Check(adapter.UnmapGuestRegion(mapping).IsSuccess && adapter.CloseChildDomain(child).IsSuccess, "original mapping/child cleanup after source close");
        Console.WriteLine($"PASS neutral-memory={scenario} reads={nativeSource.ReadCount} pins={nativeSource.OutstandingPins} bytes={nativeSource.AllocatedBytes}");
    }
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Field(object target,string name) => target.GetType().GetField(name,Hidden)!.GetValue(target);
    private static void Check(bool condition,string? message) {if(!condition)throw new InvalidOperationException(message);}
}
