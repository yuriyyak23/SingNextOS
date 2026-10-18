using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Tests.Platform;

public sealed class IndependentVirtualIoAdmissionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public interface INestedIoRuntime : ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime, IHybridCpuOriginalNestedChildRuntimeV1 { }

    [Theory]
    [InlineData("live")]
    [InlineData("adapter-source-map-history-session")]
    [InlineData("provider-source-map-history-session")]
    [InlineData("adapter-map-history-session")]
    [InlineData("provider-map-history-session")]
    [InlineData("adapter-ancestor-closed")]
    [InlineData("provider-ancestor-history")]
    [InlineData("provider-containment")]
    [InlineData("provider-containment-session")]
    [InlineData("provider-containment-reentry")]
    [InlineData("provider-containment-query-loss")]
    [InlineData("provider-containment-sibling-query-loss")]
    [InlineData("provider-containment-sibling-close-loss")]
    [InlineData("provider-containment-sibling-direct-close-loss")]
    public void NestedTransferAdmitsOnlyIntactAncestorsBeforeNativeBytes(string mode)
    {
        var native = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<INestedIoRuntime, ExactVirtualIoBackingConsumerTests.BackingSpy>();
        var spy = (ExactVirtualIoBackingConsumerTests.BackingSpy)(object)proxy; spy.Inner = native;
        var constructor = typeof(HybridCpuExecutableChildAdapter).GetConstructor(Private, null,
            [typeof(IHybridCpuExternalRuntime), typeof(IHybridCpuChildDomainRuntimeV3), typeof(ISemanticTraceSinkV1)], null)!;
        var rootProxy=DispatchProxy.Create<ExactArtifactReleaseConsumerTests.IOriginalRootRuntime,ExactArtifactReleaseConsumerTests.RootSpy>();
        var rootSpy=(ExactArtifactReleaseConsumerTests.RootSpy)(object)rootProxy; rootSpy.Inner=native;
        var adapter = (HybridCpuExecutableChildAdapter)constructor.Invoke([rootProxy, proxy, null]);
        var model = new NeutralDomainRuntimeFacade();
        var provider = new HybridCpuPlatformAuthorityProvider(model, adapter);
        var kernel = new RuntimeKernel(provider);
        var (process, owner) = TestFixtures.Create(kernel, 895, 1895);
        var foreign = TestFixtures.Create(kernel, 896, 1896).Handle;
        var survivor = kernel.AllocateBuffer<byte>(foreign, 16).Value!; survivor.Span[0] = 0x7E;
        var admin = TestFixtures.Create(kernel, 99, 999).Handle;
        var budgetCap = kernel.MintCapability(new(999), admin, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var mapHistory = mode.EndsWith("map-history-session", StringComparison.Ordinal);
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "nested-transfer", [new(ServiceBudgetDimension.OwnedMemoryBytes, mapHistory ? 1024UL : 512UL), new(ServiceBudgetDimension.GuestMemoryBytes,512), new(ServiceBudgetDimension.PinnedMappedMemoryBytes,512), new(ServiceBudgetDimension.RegionUses,8)]).Value!.ProcessBudget;
        ulong Used() => kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var parent = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, parent.Domain, parent.ConfigureCapability).IsSuccess);
        global::SingPlus.Sip.OwnedBuffer<byte>? guestRegion = null;
        if (mapHistory)
        {
            guestRegion = kernel.AllocateBuffer<byte>(owner,512).Value!;
            var memory = kernel.MintCapability(process.DomainId,owner,ResourceKind.MemoryRegion,CapabilityResourceIds.MemoryRegion(guestRegion.Handle.RegionId),CapabilityRights.Map|CapabilityRights.Read).Value!.CapabilityId;
            Assert.True(kernel.MapGuestRegion(owner,parent.Domain,parent.MemoryCapability,memory,guestRegion.Handle,new(0,512),GuestMemoryAccess.Read).IsSuccess);
        }
        var nested = kernel.CreateNestedVirtualDomain(owner, parent.Domain, parent.ConfigureCapability,
            new(new(1, 1024), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.Io)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, nested.Domain, nested.ConfigureCapability).IsSuccess);
        var binding = kernel.QueryVirtualDomainPlatformBinding(owner, nested.Domain).Value!;
        var deviceCap = kernel.MintCapability(process.DomainId, owner, ResourceKind.Device, "device/nested-transfer", CapabilityRights.Configure | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var device = kernel.BindPlatformDevice(owner, binding, deviceCap, PlatformDeviceRights.Read | PlatformDeviceRights.Write).Value!;
        var io = kernel.BindVirtualIo(owner, nested.Domain, device, new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 512)).Value!;
        Assert.Equal(mapHistory ? 1024UL : 512UL, Used());
        if (mode.StartsWith("provider-containment-sibling",StringComparison.Ordinal))
        {
            var sibling = kernel.CreateNestedVirtualDomain(owner,parent.Domain,parent.ConfigureCapability,
                new(new(1,1024),VirtualDomainAuthorityClass.Lifecycle));
            Assert.True(sibling.IsSuccess,sibling.Message);
        }
        if (mapHistory)
        {
            object holder = mode.StartsWith("adapter",StringComparison.Ordinal) ? adapter : provider;
            var registry = (IDictionary)holder.GetType().GetField(ReferenceEquals(holder,adapter)?"originalChildren":"_originalProviderChildren",Private)!.GetValue(holder)!;
            var ancestor = registry.Values.Cast<object>().Single(x=>x.GetType().GetProperty("NestedParent")!.GetValue(x) is null);
            var history = (IList)ancestor.GetType().GetProperty("OriginalGuestMaps")!.GetValue(ancestor)!;
            var original = Assert.Single(history.Cast<object>());
            var sourceRace=mode.Contains("-source-",StringComparison.Ordinal);
            var observed=0;
            bool callbackLocksFree=false,reentryDenied=false;
            if(sourceRace)
                rootSpy.BeforeQuery=()=>
                {
                    var ioOwners=(IDictionary)holder.GetType().GetField(ReferenceEquals(holder,adapter)?"originalIo":"_originalProviderVirtualIo",Private)!.GetValue(holder)!;
                    var transfers=(IEnumerable)ioOwners.Values.Cast<object>().Single().GetType().GetProperty("Transfers")!.GetValue(ioOwners.Values.Cast<object>().Single())!;
                    if(!transfers.Cast<object>().Any() || observed!=0)return;
                    observed++;
                    callbackLocksFree=!Monitor.IsEntered(typeof(HybridCpuExecutableChildAdapter).GetField("sync",Private)!.GetValue(adapter)!) &&
                        !Monitor.IsEntered(typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate",Private)!.GetValue(provider)!);
                    history.Clear();
                    reentryDenied=!kernel.TransferVirtualIo(owner,new(io,0,true,1,[0xEE])).IsSuccess;
                };
            else history.Clear();
            var protocol = global::SingPlus.Sip.Virtualization.VirtualizationServiceClient.CreateProtocol();
            var descriptor = kernel.RegisterService(foreign,"ancestor-map-history",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,
                global::SingPlus.Sip.Virtualization.VirtualizationServiceClient.ResponseProtocol,[]).Value!;
            var session = kernel.OpenSession(owner,descriptor,[]).Value;
            var host = RuntimeVirtualizationServiceHost.CreateForSession(kernel,foreign,session).Value!;
            var client = global::SingPlus.Sip.Virtualization.VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
            var reply = client.TransferIoAsync(new(io.Id,io.Generation,0,true,1,new global::SingPlus.Sip.Native.BoundedBytes(new byte[]{0x6C}))).AsTask();
            // Provider admission linearizes before entering the SDK freshness callback.
            // A retained original may complete; missing history must deny a new effect.
            var admittedOriginal=sourceRace && ReferenceEquals(holder,provider);
            Assert.Equal(admittedOriginal,host.ProcessNext().IsSuccess);
            if(admittedOriginal) reply.GetAwaiter().GetResult();
            else Assert.ThrowsAny<Exception>(()=>reply.GetAwaiter().GetResult());
            rootSpy.BeforeQuery=null;
            Assert.Equal(sourceRace?1:0,observed);
            if(sourceRace){Assert.True(callbackLocksFree);Assert.True(reentryDenied);}
            Assert.Equal(admittedOriginal?1:0,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.False(kernel.TransferVirtualIo(owner,new(io,0,true,1,[0xEE])).IsSuccess);
            Assert.Equal(admittedOriginal?1:0,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.Equal(1024UL,Used());
            Assert.Equal(RegionState.Owned,kernel.Regions.Snapshot().Single(x=>x.Handle==guestRegion!.Handle).State);
            foreach(var dimension in new[]{ServiceBudgetDimension.GuestMemoryBytes,ServiceBudgetDimension.PinnedMappedMemoryBytes})
                Assert.Equal(512UL,kernel.QueryBudget(budget).Value!.Usage.Single(x=>x.Dimension==dimension).Used);
            Assert.True(survivor.IsValid); Assert.Equal(0x7E,survivor.Span[0]);
            history.Add(original); // Exact original fixture restoration; no reconstructed authority.
            Assert.True(kernel.TransferVirtualIo(owner,new(io,0,true,1,[0x6C])).IsSuccess);
            Assert.Equal(admittedOriginal?2:1,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.True(kernel.CloseVirtualIo(io).IsSuccess);
            Assert.Equal(512UL,Used());
            var terminated=kernel.TerminateProcess(owner);
            Assert.True(terminated.IsSuccess,terminated.Message);
            Assert.Equal(1,spy.Calls.GetValueOrDefault("MapOriginalGuest"));
            Assert.Equal(1,spy.Calls.GetValueOrDefault("UnmapOriginalGuest"));
            Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            Assert.Equal(RegionState.Released,kernel.Regions.Snapshot().Single(x=>x.Handle==guestRegion!.Handle).State);
            Assert.All(kernel.QueryBudget(budget).Value!.Usage,x=>Assert.Equal(0UL,x.Used));
            Assert.Equal(0,model.ActiveDeviceLeaseCount); Assert.Equal(0,model.ActiveBindingCount);
            Assert.True(survivor.IsValid); Assert.Equal(0x7E,survivor.Span[0]);
            return;
        }
        if (mode.StartsWith("provider-containment", StringComparison.Ordinal))
        {
            var children = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderChildren", Private)!.GetValue(provider)!;
            var ancestor = children.Values.Cast<object>().Single(x => x.GetType().GetProperty("NestedParent")!.GetValue(x) is null);
            var factory = ((IEnumerable)ancestor.GetType().GetProperty("OriginalNested")!.GetValue(ancestor)!).Cast<object>().Last();
            var original = (PlatformOriginalNestedDomainRequest)factory.GetType().GetProperty("Request")!.GetValue(factory)!;
            var observed = 0;
            spy.After = name =>
            {
                if (mode.EndsWith("close-loss",StringComparison.Ordinal) && name is "CloseOriginalChild" or "ResolveOriginalChildClose")
                    throw new IOException("Original sibling Close reply/query lost after native producer.");
                if (name != "ResolveOriginalNestedChild") return;
                observed++;
                Assert.False(Monitor.IsEntered(typeof(HybridCpuExecutableChildAdapter).GetField("sync",Private)!.GetValue(adapter)!));
                Assert.False(Monitor.IsEntered(typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate",Private)!.GetValue(provider)!));
                if (mode.EndsWith("query-loss",StringComparison.Ordinal)) throw new IOException("Original nested query reply lost.");
                if (mode == "provider-containment-reentry")
                    Assert.False(kernel.TransferVirtualIo(owner,new(io,0,true,1,[0xEE])).IsSuccess);
            };
            var siblingOwner = factory.GetType().GetField("Child")!.GetValue(factory)!;
            var siblingLease = (PlatformProviderChildDomainLease)siblingOwner.GetType().GetProperty("Lease")!.GetValue(siblingOwner)!;
            if (mode.EndsWith("direct-close-loss",StringComparison.Ordinal))
                Assert.False(provider.CloseChildDomain(siblingLease).IsSuccess);
            else
            {
                Assert.False(provider.ContainOriginalNestedDomain(original).IsSuccess);
                Assert.True(observed > 0);
            }
            spy.After = null;
            if (mode.EndsWith("close-loss",StringComparison.Ordinal))
            {
                Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalChild"));
                Assert.False(kernel.TransferVirtualIo(owner,new(io,0,true,1,[0xEE])).IsSuccess);
                Assert.Equal(0,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
                Assert.Equal(512UL,Used());
                if (mode.EndsWith("direct-close-loss",StringComparison.Ordinal))
                {
                    Assert.True(provider.ResolveOriginalChildClose(siblingLease).IsSuccess);
                    var neutral = (NeutralChildDomainLease)siblingOwner.GetType().GetProperty("Neutral")!.GetValue(siblingOwner)!;
                    var routes = (IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("children",Private)!.GetValue(adapter)!;
                    var exactOwner = routes[neutral.Handle]!;
                    routes[neutral.Handle] = typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(exactOwner,null)!;
                    Assert.False(adapter.ResolveOriginalChildClose(neutral).IsSuccess);
                    Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalChild"));
                    routes[neutral.Handle] = exactOwner; // Fixture restores exact source, never adopts the copied owner.
                    Assert.True(adapter.ResolveOriginalChildClose(neutral).IsSuccess);
                    var invocation = exactOwner.GetType().GetProperty("OriginalCloseInvocation")!.GetValue(exactOwner)!;
                    var validated = invocation.GetType().GetField("Validated")!;
                    var retained = validated.GetValue(invocation)!;
                    validated.SetValue(invocation,typeof(object).GetMethod("MemberwiseClone",Private)!.Invoke(retained,null));
                    Assert.False(adapter.ResolveOriginalChildClose(neutral).IsSuccess);
                    Assert.False(adapter.TransitionChildDomain(neutral,NeutralChildDomainTransition.BeginDrain).IsSuccess);
                    Assert.False(kernel.TransferVirtualIo(owner,new(io,0,true,1,[0xEE])).IsSuccess);
                    Assert.Equal(0,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
                    Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalChild"));
                    validated.SetValue(invocation,retained);
                    Assert.True(adapter.ResolveOriginalChildClose(neutral).IsSuccess);
                }
                else
                {
                    var closure = provider.ResolveOriginalNestedDomainCleanup(original);
                    Assert.True(closure.IsSuccess,closure.Message);
                    Assert.True(closure.Value!.NoFurtherChildUse);
                }
                Assert.Equal(1,spy.Calls.GetValueOrDefault("CloseOriginalChild"));
                Assert.True(kernel.TransferVirtualIo(owner,new(io,0,true,1,[0x6C])).IsSuccess);
                Assert.Equal(1,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
                Assert.Equal(512UL,Used());
                Assert.True(kernel.CloseVirtualIo(io).IsSuccess);
                Assert.Equal(0UL,Used());
                var terminated = kernel.TerminateProcess(owner);
                Assert.True(terminated.IsSuccess,terminated.Message);
                Assert.Equal(3,spy.Calls.GetValueOrDefault("CloseOriginalChild")); // Original sibling, IO child, ancestor: once each.
                Assert.Equal(0,model.ActiveDeviceLeaseCount);
                Assert.Equal(0,model.ActiveBindingCount);
                Assert.True(survivor.IsValid); Assert.Equal(0x7E,survivor.Span[0]);
                return;
            }
            Assert.Equal(0, spy.Calls.GetValueOrDefault("CloseOriginalChild"));
        }
        if (mode == "adapter-ancestor-closed")
        {
            var children = (IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("originalChildren", Private)!.GetValue(adapter)!;
            var ancestor = children.Values.Cast<object>().Single(x => x.GetType().GetProperty("NestedParent")!.GetValue(x) is null);
            ancestor.GetType().GetProperty("State")!.SetValue(ancestor, NeutralChildDomainState.Closed);
        }
        if (mode == "provider-ancestor-history")
        {
            var children = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderChildren", Private)!.GetValue(provider)!;
            var ancestor = children.Values.Cast<object>().Single(x => x.GetType().GetProperty("NestedParent")!.GetValue(x) is null);
            ((IList)ancestor.GetType().GetProperty("OriginalNested")!.GetValue(ancestor)!).Clear();
        }
        if (mode == "provider-containment-session")
        {
            var protocol = global::SingPlus.Sip.Virtualization.VirtualizationServiceClient.CreateProtocol();
            var descriptor = kernel.RegisterService(foreign,"nested-containment-session",new(protocol.ContractName,"1",protocol.ContractDigest),protocol,
                global::SingPlus.Sip.Virtualization.VirtualizationServiceClient.ResponseProtocol,[]).Value!;
            var session = kernel.OpenSession(owner,descriptor,[]).Value;
            var host = RuntimeVirtualizationServiceHost.CreateForSession(kernel,foreign,session).Value!;
            var client = global::SingPlus.Sip.Virtualization.VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel,owner,session));
            var reply = client.TransferIoAsync(new(io.Id,io.Generation,0,true,1,new global::SingPlus.Sip.Native.BoundedBytes(new byte[]{0x6C}))).AsTask();
            Assert.False(host.ProcessNext().IsSuccess);
            Assert.ThrowsAny<Exception>(() => reply.GetAwaiter().GetResult());
            Assert.Equal(0,spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
            Assert.Equal(512UL,Used());
            Assert.True(survivor.IsValid); Assert.Equal(0x7E,survivor.Span[0]);
            return; // No absence/failed reply is used as a lifetime closure fact.
        }
        var result = kernel.TransferVirtualIo(owner, new(io, 0, true, 1, [0x6C]));
        Assert.Equal(mode == "live", result.IsSuccess);
        Assert.Equal(mode == "live" ? 1 : 0, spy.Calls.GetValueOrDefault("TransferOriginalVirtualIo"));
        if (mode.StartsWith("provider-containment",StringComparison.Ordinal))
        {
            Assert.Equal(512UL, Used());
            Assert.True(kernel.CloseVirtualIo(io).IsSuccess);
            Assert.Equal(0UL, Used());
            Assert.Equal(1, spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
            Assert.True(survivor.IsValid); Assert.Equal(0x7E, survivor.Span[0]);
            return;
        }
        if (mode == "live")
        {
            Assert.True(kernel.CloseVirtualIo(io).IsSuccess);
            Assert.True(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(0UL, Used());
            Assert.Equal(0, model.ActiveDeviceLeaseCount);
        }
        else
        {
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            // The denied new transfer does not prevent exact, independent IO
            // cleanup; ancestor closure itself remains pinned by its own fact.
            Assert.Equal(0UL, Used());
            Assert.Equal(0, model.ActiveDeviceLeaseCount);
            Assert.Equal(1, spy.Calls.GetValueOrDefault("CloseOriginalVirtualIo"));
        }
        Assert.True(survivor.IsValid); Assert.Equal(0x7E, survivor.Span[0]);
    }

    [Theory]
    [InlineData("live")]
    [InlineData("unknown-read")]
    [InlineData("copied-owner")]
    [InlineData("child-aba")]
    [InlineData("duplicate-history")]
    public void IndependentAdmissionPreservesOriginalUsersAndUnknownPins(string mode)
    {
        var native = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<ExactVirtualIoBackingConsumerTests.IOriginalBackingRuntime, ExactVirtualIoBackingConsumerTests.BackingSpy>();
        var spy = (ExactVirtualIoBackingConsumerTests.BackingSpy)proxy; spy.Inner = native;
        var constructor = typeof(HybridCpuExecutableChildAdapter).GetConstructor(Private, null,
            [typeof(IHybridCpuExternalRuntime), typeof(IHybridCpuChildDomainRuntimeV3), typeof(ISemanticTraceSinkV1)], null)!;
        var rootProxy=DispatchProxy.Create<ExactArtifactReleaseConsumerTests.IOriginalRootRuntime,ExactArtifactReleaseConsumerTests.RootSpy>();
        var rootSpy=(ExactArtifactReleaseConsumerTests.RootSpy)(object)rootProxy; rootSpy.Inner=native;
        var adapter = (HybridCpuExecutableChildAdapter)constructor.Invoke([rootProxy, proxy, null]);
        var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), adapter));
        var (process, owner) = TestFixtures.Create(kernel, 891, 1891);
        var foreign = TestFixtures.Create(kernel, 892, 1892).Handle;
        var survivor = kernel.AllocateBuffer<byte>(foreign, 32).Value!; survivor.Span[0] = 0x7E;
        var admin = TestFixtures.Create(kernel, 99, 999).Handle;
        var budgetCap = kernel.MintCapability(new(999), admin, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "independent-io", [new(ServiceBudgetDimension.OwnedMemoryBytes, 1028)]).Value!.ProcessBudget;
        ulong Used() => kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used;
        var create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        var domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        var root = kernel.QueryVirtualDomainPlatformBinding(owner, domain.Domain).Value;
        PlatformDeviceLease Device(string id)
        {
            var cap = kernel.MintCapability(process.DomainId, owner, ResourceKind.Device, id, CapabilityRights.Configure | CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
            return kernel.BindPlatformDevice(owner, root, cap, PlatformDeviceRights.Read | PlatformDeviceRights.Write).Value!;
        }
        var first = kernel.BindVirtualIo(owner, domain.Domain, Device("device/first"), new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 512)).Value!;
        Assert.Equal(512UL, Used());
        var originals = (IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("originalIo", Private)!.GetValue(adapter)!;
        var original = Assert.Single(originals.Values.Cast<object>());
        var request = (NeutralOriginalVirtualIoRequest)original.GetType().GetField("Request")!.GetValue(original)!;
        var child = original.GetType().GetField("Child")!.GetValue(original)!;
        var history = (IList)child.GetType().GetProperty("OriginalIo")!.GetValue(child)!;
        if (mode != "live")
        {
            if (mode == "unknown-read")
            {
                spy.Fault = "kernel-read-query-loss";
                Assert.False(kernel.TransferVirtualIo(owner, new KernelVirtualIoTransferRequest(first, 0, false, 2, null)).IsSuccess);
                Assert.Equal(514UL, Used());
            }
            else if (mode == "child-aba")
            {
                var state = child.GetType().GetProperty("State")!;
                state.SetValue(child, state.GetValue(child)); // Equal state, different lifetime revision.
            }
            else if (mode == "duplicate-history")
            {
                history.Add(original);
                child.GetType().GetProperty("OriginalIoIssued")!.SetValue(child, 2UL);
            }
            else
                originals[originals.Keys.Cast<object>().Single()] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(original, null);
            var denied = adapter.BindOriginalVirtualIo(request with { InvocationId = Guid.NewGuid() });
            Assert.False(denied.IsSuccess);
            Assert.Equal(1, spy.Calls["BindOriginalParentDevice"]);
            Assert.Equal(1, spy.Calls["BindOriginalVirtualIo"]);
            originals[originals.Keys.Cast<object>().Single()] = original;
            if (mode == "duplicate-history")
            {
                history.RemoveAt(1);
                child.GetType().GetProperty("OriginalIoIssued")!.SetValue(child, 1UL);
            }
            spy.Fault = "exact";
        }
        else
        {
            var secondResult = kernel.BindVirtualIo(owner, domain.Domain, Device("device/second"), new(PlatformDeviceRights.Read | PlatformDeviceRights.Write, 512));
            Assert.True(secondResult.IsSuccess, secondResult.Message);
            var second = secondResult.Value!;
            Assert.Equal(1024UL, Used());
            Assert.True(kernel.TransferVirtualIo(owner, new KernelVirtualIoTransferRequest(second, 0, true, 1, [0x6C])).IsSuccess);
            Assert.True(kernel.CloseVirtualIo(first).IsSuccess);
            Assert.Equal(512UL, Used());
            Assert.True(kernel.TransferVirtualIo(owner, new KernelVirtualIoTransferRequest(second, 1, true, 1, [0x6D])).IsSuccess);
            Assert.True(kernel.CloseVirtualIo(second).IsSuccess);
            Assert.Equal(2, spy.Calls["CloseOriginalVirtualIo"]);
        }
        var ended = kernel.TerminateProcess(owner);
        Assert.True(ended.IsSuccess, ended.Message);
        Assert.Equal(0UL, Used());
        Assert.True(survivor.IsValid); Assert.Equal(0x7E, survivor.Span[0]);
    }
}
