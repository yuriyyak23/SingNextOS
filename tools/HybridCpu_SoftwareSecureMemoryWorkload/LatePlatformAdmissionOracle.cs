using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Diagnostics;

public static class LatePlatformAdmissionOracle
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Field(object target, string name) => target.GetType().GetField(name, Hidden)!.GetValue(target)!;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static ProcessTeardownSnapshot Snapshot(object record) => (ProcessTeardownSnapshot)record.GetType().GetProperty("Snapshot")!.GetValue(record)!;

    public static void Run(string scenario)
    {
        bool mapping = scenario.StartsWith("late-mapping-", StringComparison.Ordinal);
        var actual = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), new HybridCpuExecutableChildAdapter());
        var surface = DispatchProxy.Create<ITrackingProvider, TrackingProxy>();
        var proxy = (TrackingProxy)(object)surface; proxy.Target = actual;
        var kernel = new RuntimeKernel(surface);
        var manifest = new SingProcessManifestV1(new(951), new(952), 1, "late-admission", ExecutionRole.Sip, MemoryProfile.SipRegion);
        Check(kernel.CreateProcess(manifest).IsSuccess, "original process admission");
        var owner = new ProcessHandle(manifest.ProcessId, manifest.Generation);
        var cap = kernel.MintCapability(manifest.DomainId, owner, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(owner, cap, owner, "late-admission", [
            new(ServiceBudgetDimension.OwnedMemoryBytes, 8), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 8)]).Value;
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!; buffer.Span[0] = 0xA3;
        var peerManifest = new SingProcessManifestV1(new(961), new(962), 1, "late-peer", ExecutionRole.Sip, MemoryProfile.SipRegion);
        Check(kernel.CreateProcess(peerManifest).IsSuccess, "independent peer admission");
        var peer = new ProcessHandle(peerManifest.ProcessId, peerManifest.Generation);
        var peerBuffer = kernel.AllocateBuffer<byte>(peer, 8).Value!; peerBuffer.Span[0] = 0x7D;
        var other = new RuntimeKernel(); Check(other.CreateProcess(manifest).IsSuccess, "foreign Kernel process");
        var otherHook = typeof(RuntimeKernel).GetProperty("AfterInitializedLoadDrainBeforeTeardownCommit", Hidden)!;
        otherHook.SetValue(other, (Action<ProcessHandle>)(_ => throw new IOException("Retain foreign original stop.")));
        try { other.TerminateProcess(owner); } catch (IOException) { }
        otherHook.SetValue(other, null);
        var routes = (IDictionary)Field(kernel, "_processTeardowns");
        object? original = null, substituted = null;
        ProcessTeardownSnapshot before = default;
        void StopAndSubstitute()
        {
            Check(!Monitor.IsEntered(Field(kernel, "_platformMemoryUseGate")), "provider callback outside kernel gate");
            Check(!kernel.TerminateProcess(owner).IsSuccess, "in-flight accepted admission keeps process pinned");
            original = routes[owner]!;
            if (scenario.EndsWith("-loss", StringComparison.Ordinal)) { routes.Remove(owner); return; }
            substituted = scenario.EndsWith("-foreign", StringComparison.Ordinal)
                ? ((IDictionary)Field(other, "_processTeardowns"))[owner]!
                : typeof(object).GetMethod("MemberwiseClone", Hidden)!.Invoke(original, null)!;
            if (!mapping) substituted.GetType().GetProperty("PlatformDomainClosed")!.SetValue(substituted, true);
            before = Snapshot(substituted);
            routes[owner] = substituted;
        }
        PlatformDomainBinding binding = default;
        if (mapping)
        {
            var bound = kernel.BindPlatformAuthorityDomain(owner); Check(bound.IsSuccess, $"root bind: {bound.Error} {bound.Message}"); binding = bound.Value;
            var regionCap = kernel.MintCapability(manifest.DomainId, owner, ResourceKind.MemoryRegion,
                CapabilityResourceIds.MemoryRegion(buffer.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
            proxy.After = name => { if (name == nameof(IPlatformAuthorityProvider.MapOwnedRegion)) StopAndSubstitute(); };
            var mapped = kernel.MapPlatformOwnedRegion(owner, binding, regionCap, buffer.Handle, PlatformMemoryAccess.Read);
            Check(mapped.IsSuccess, $"known mapping cleanup receipt: {mapped.Error} {mapped.Message}");
            Check(((IDictionary)Field(kernel, "_processPlatformMappings")).Contains(owner), "accepted mapping retained by existing tracking owner");
        }
        else
        {
            var hook = typeof(RuntimeKernel).GetProperty("BeforePlatformBindingAttachmentQualificationHook", Hidden)!;
            hook.SetValue(kernel, (Action)StopAndSubstitute);
            var bound = kernel.BindPlatformAuthorityDomain(owner);
            Check(original is not null, $"actual root callback required: {bound.Error} {bound.Message}");
            Check(!bound.IsSuccess, "stopped root is cleanup-only, not execution attachment");
            hook.SetValue(kernel, null);
            Check(((IDictionary)Field(kernel, "_processPlatformBindings")).Contains(owner), "accepted root retained by existing tracking owner");
        }
        proxy.After = null;
        Check(original is not null, "actual public stop captured original cleanup owner");
        Check(substituted is null || Snapshot(substituted) == before, "late accepted effect must not mutate foreign teardown record");
        Check(kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State == RegionState.Owned &&
            peerBuffer.IsValid && peerBuffer.Span[0] == 0x7D, "unknown route preserves original and independent backing");
        var retained = kernel.QueryBudget(budget.ProcessBudget).Value!.Usage;
        Check(retained.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used == 8 &&
            retained.Single(x => x.Dimension == ServiceBudgetDimension.PinnedMappedMemoryBytes).Used == (mapping ? 8UL : 0UL),
            "original attached charges retained before route repair");
        Check(!kernel.TerminateProcess(owner).IsSuccess, "no foreign/absent route authorizes completion");
        routes[owner] = original;
        var stop = kernel.TerminateProcess(owner); Check(stop.IsSuccess, $"exact repair must close accepted original: {stop.Error} {stop.Message}");
        Check(kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State == RegionState.Released &&
            kernel.QueryBudget(budget.ProcessBudget).Value!.Usage.All(x => x.Used == 0), "original Region/charges reclaimed");
        Check(proxy.Calls.GetValueOrDefault(nameof(IPlatformAuthorityProvider.BindDomain)) == 1 &&
            proxy.Calls.GetValueOrDefault(nameof(IPlatformAuthorityProvider.MapOwnedRegion)) == (mapping ? 1 : 0) &&
            proxy.Calls.GetValueOrDefault(nameof(IPlatformAuthorityProvider.RevokeRegionMapping)) == 0 &&
            proxy.Calls.GetValueOrDefault(nameof(IPlatformRegionRevocationProvider.BeginRegionMappingRevocation)) == (mapping ? 1 : 0) &&
            proxy.Calls.GetValueOrDefault(nameof(IPlatformAuthorityProvider.RevokeDomain)) == 1, "one original effect and independent closure, no reissue");
        Check(peerBuffer.IsValid && peerBuffer.Span[0] == 0x7D && kernel.TerminateProcess(peer).IsSuccess &&
            other.TerminateProcess(owner).IsSuccess, "foreign backing and foreign original cleanup remain independent");
        Console.WriteLine($"PASS late-admission={scenario} bind=1 map={(mapping ? 1 : 0)} originalRegionReleased=true charges=0 peerIntact=true");
    }

    public interface ITrackingProvider : IPlatformAuthorityProvider, IPlatformProviderIncarnationSource, IPlatformFeatureProvider,
        IPlatformOriginalAdmissionResolutionProvider, IPlatformOriginalDomainClosureContinuationProvider,
        IPlatformRegionRevocationProvider, IPlatformCompletionProvider, IPlatformRegionRevocationResolutionProvider,
        IPlatformRegionRevocationNoEffectProvider { }
    public class TrackingProxy : DispatchProxy
    {
        public HybridCpuPlatformAuthorityProvider Target = null!;
        public Action<string>? After;
        public Dictionary<string, int> Calls = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var call = method!; Calls[call.Name] = Calls.GetValueOrDefault(call.Name) + 1;
            object? result;
            try { result = call.Invoke(Target, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
            After?.Invoke(call.Name); return result;
        }
    }
}
