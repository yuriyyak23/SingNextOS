using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Tests.Platform;

public sealed class ExactVirtualEventConsumerTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public interface IEventRuntime : ExactArtifactReleaseConsumerTests.IReleaseRuntime, IHybridCpuOriginalChildEventRuntimeV1 { }
    public class EventSpy : DispatchProxy
    {
        public HybridCpuExternalRuntime Inner = new();
        public string Fault = "exact";
        public Dictionary<string, int> Calls = [];
        public Action<string>? After;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name; Calls[name] = Calls.GetValueOrDefault(name) + 1;
            if (Fault == "query-loss" && name == "ResolveOriginalChildEvent") throw new IOException("Query reply lost.");
            if (Fault == "consume-null" && name == "ConsumeOriginalChildEvent") return null; // Invocation disposition lost before delivery; original mailbox really remains queued.
            object? result;
            try { result = targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException exception) { throw exception.InnerException!; }
            After?.Invoke(name);
            if (Fault == "consume-null" && name == "ContainOriginalChildEvent" && result is ExternalOriginalChildEventObservation closed)
                Assert.Same(closed, Inner.ConsumeOriginalChildEvent((ExternalOriginalChildEventRequest)args![0]!)); // Late consume cannot produce another delivery after the exact fence.
            if (Fault == "inject-loss" && name == "InjectOriginalChildEvent" || Fault == "consume-loss" && name == "ConsumeOriginalChildEvent")
                throw new IOException("Reply lost after native effect.");
            if (Fault == "terminal-copy" && name == "ConsumeOriginalChildEvent" && result is ExternalOriginalChildEventObservation raw) return raw with { };
            if (Fault is "history-loss" or "native-history-duplicate" && name == "InjectOriginalChildEvent")
            {
                var children = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
                var child = Assert.Single(children.Values.Cast<object>());
                var history = (IList)child.GetType().GetProperty("OriginalEvents")!.GetValue(child)!;
                if (Fault == "history-loss") history.Clear();
                else
                {
                    history.Add(Assert.Single(history.Cast<object>()));
                    child.GetType().GetProperty("OriginalEventsIssued")!.SetValue(child, 2UL);
                }
            }
            return result;
        }
    }
    [Theory]
    [InlineData("exact")]
    [InlineData("endpoint-route-loss")]
    [InlineData("endpoint-foreign-route")]
    [InlineData("inject-loss")]
    [InlineData("consume-loss")]
    [InlineData("query-loss")]
    [InlineData("terminal-copy")]
    [InlineData("history-loss")]
    [InlineData("native-history-duplicate")]
    [InlineData("source-loss")]
    [InlineData("bridge-history-loss")]
    [InlineData("sdk-history-loss")]
    [InlineData("provider-history-loss")]
    [InlineData("endpoint-overlap")]
    [InlineData("consume-null")]
    [InlineData("bridge-aba")]
    [InlineData("revision-exhausted")]
    [InlineData("reentry")]
    public async Task ActualExternalRelayPublishesOnceOrKeepsOriginalCleanupAndQuantitativePins(string mode)
    {
        var native = DispatchProxy.Create<IEventRuntime, EventSpy>(); var spy = (EventSpy)(object)native; spy.Fault = mode;
        var model = new NeutralDomainRuntimeFacade();
        var sdk = new HybridCpuExecutableChildAdapter(spy.Inner, native);
        var provider = new HybridCpuPlatformAuthorityProvider(model, sdk); var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 784, 1128);
        var admin = TestFixtures.Create(kernel, 99, 999).Handle;
        var budgetCap = kernel.MintCapability(new(999), admin, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "original-event", [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        var region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var created = kernel.CreateVirtualDomain(owner, create, new(1, 4096)); Assert.True(created.IsSuccess, created.Message);
        var domain = created.Value!; Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        var endpoint = kernel.CreateKernelEventEndpoint(owner).Value!;
        var reentryEndpoint = kernel.CreateKernelEventEndpoint(owner).Value!;
        var registry = typeof(RuntimeKernel).GetField("_kernelEvents", Private)!.GetValue(kernel)!;
        var endpoints = (IDictionary)registry.GetType().GetField("_endpoints", Private)!.GetValue(registry)!;
        var originalEndpoint = endpoints[endpoint.EndpointId]!;
        var foreign = TestFixtures.Create(kernel, 785, 1129).Handle;
        var foreignEndpoint = kernel.CreateKernelEventEndpoint(foreign).Value!;
        var foreignRecord = endpoints[foreignEndpoint.EndpointId]!;
        bool routeLoss = mode is "endpoint-route-loss" or "endpoint-foreign-route";
        var waiter = routeLoss ? kernel.WaitForKernelEventAsync(owner, endpoint).AsTask() : null;
        if (waiter is not null) Assert.False(waiter.IsCompleted);
        bool callbackRan = false;
        spy.After = name =>
        {
            if (name.Contains("OriginalChildEvent", StringComparison.Ordinal))
            {
                Assert.False(Monitor.IsEntered(typeof(HybridCpuExecutableChildAdapter).GetField("sync", Private)!.GetValue(sdk)!));
                Assert.False(Monitor.IsEntered(typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate", Private)!.GetValue(provider)!));
                Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", Private)!.GetValue(kernel.PlatformAuthority)!));
            }
            if (!callbackRan && name == "ConsumeOriginalChildEvent")
            {
                callbackRan = true;
                if (mode == "endpoint-route-loss") endpoints.Remove(endpoint.EndpointId);
                if (mode == "endpoint-foreign-route") endpoints[endpoint.EndpointId] = foreignRecord;
                if (mode == "source-loss") Assert.True(kernel.RevokeCapability(domain.EventCapability).IsSuccess);
                if (mode == "endpoint-overlap") Assert.False(kernel.CloseKernelEventEndpoint(owner, endpoint).IsSuccess);
                if (mode == "reentry") Assert.False(kernel.InjectVirtualEvent(owner, domain.Domain, domain.EventCapability, reentryEndpoint).IsSuccess);
                if (mode is "bridge-aba" or "revision-exhausted")
                {
                    var records = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_originalChildOwners", Private)!.GetValue(kernel.PlatformAuthority)!;
                    var child = Assert.Single(records.Values.Cast<object>());
                    if (mode == "bridge-aba") child.GetType().GetProperty("State")!.SetValue(child, child.GetType().GetProperty("State")!.GetValue(child));
                    else
                    {
                        child.GetType().GetField("_lifecycleRevision", Private)!.SetValue(child, long.MaxValue);
                        child.GetType().GetField("_lifecycleRevisionExhausted", Private)!.SetValue(child, 1);
                    }
                }
                if (mode == "bridge-history-loss")
                {
                    var records = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_originalChildOwners", Private)!.GetValue(kernel.PlatformAuthority)!;
                    var child = Assert.Single(records.Values.Cast<object>());
                    ((IList)child.GetType().GetProperty("EventInvocations")!.GetValue(child)!).Clear();
                }
                if (mode == "sdk-history-loss")
                {
                    var records = (IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("originalChildren", Private)!.GetValue(sdk)!;
                    var child = Assert.Single(records.Values.Cast<object>());
                    ((IList)child.GetType().GetProperty("OriginalEvents")!.GetValue(child)!).Clear();
                }
                if (mode == "provider-history-loss")
                {
                    var records = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderChildren", Private)!.GetValue(provider)!;
                    var child = Assert.Single(records.Values.Cast<object>());
                    ((IList)child.GetType().GetProperty("OriginalEvents")!.GetValue(child)!).Clear();
                }
            }
        };
        var injected = kernel.InjectVirtualEvent(owner, domain.Domain, domain.EventCapability, endpoint);
        var published = mode is "exact" or "endpoint-overlap" or "reentry";
        Assert.Equal(published, injected.IsSuccess);
        Assert.Equal(published, kernel.ConsumeKernelEvent(owner, endpoint).IsSuccess);
        Assert.False(kernel.ConsumeKernelEvent(owner, endpoint).IsSuccess);
        Assert.Equal(1, spy.Calls.GetValueOrDefault("InjectOriginalChildEvent"));
        if (!published) Assert.False(kernel.InjectVirtualEvent(owner, domain.Domain, domain.EventCapability, endpoint).IsSuccess);
        if (mode == "query-loss")
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            spy.Fault = "exact";
        }
        if (routeLoss)
        {
            Assert.Null(originalEndpoint.GetType().GetProperty("Staged")!.GetValue(originalEndpoint));
            Assert.False(waiter!.IsCompleted);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        var terminated = kernel.TerminateProcess(owner);
        if (mode is "history-loss" or "native-history-duplicate" or "terminal-copy" or "bridge-history-loss" or "sdk-history-loss" or "provider-history-loss" or "bridge-aba" or "revision-exhausted")
        {
            Assert.False(terminated.IsSuccess);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        else
        {
            Assert.True(terminated.IsSuccess, terminated.Message + " Calls=" + global::System.Text.Json.JsonSerializer.Serialize(spy.Calls) +
                " Teardown=" + global::System.Text.Json.JsonSerializer.Serialize(kernel.QueryProcessTeardown(owner).Value));
            Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == region.Handle).State);
            Assert.Equal(0UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        Assert.Equal(1, spy.Calls.GetValueOrDefault("InjectOriginalChildEvent"));
        Assert.InRange(spy.Calls.GetValueOrDefault("ConsumeOriginalChildEvent"), 0, 1);
        Assert.InRange(spy.Calls.GetValueOrDefault("ContainOriginalChildEvent"), 0, 1);
        if (routeLoss)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await waiter!; });
            Assert.Equal("Closed", originalEndpoint.GetType().GetProperty("State")!.GetValue(originalEndpoint)!.ToString());
            Assert.Equal("Active", foreignRecord.GetType().GetProperty("State")!.GetValue(foreignRecord)!.ToString());
            Assert.Equal(KernelError.ResponseNotAvailable, kernel.ConsumeKernelEvent(foreign, foreignEndpoint).Error);
        }
    }
}
