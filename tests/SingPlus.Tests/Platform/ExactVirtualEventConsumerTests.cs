using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.HybridCpu;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Virtualization;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Tests.Platform;

public sealed class ExactVirtualEventConsumerTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(false, 2)]
    public async Task ProviderEventSourceExceptionRetainsOriginalBeforeSdkInvocation(bool afterEffect, int closureFault)
    {
        IEventRuntime native = DispatchProxy.Create<IEventRuntime, EventSpy>(); EventSpy spy = (EventSpy)(object)native;
        NativeTransferRawCustodyConsumerTests.ISourceRuntime model = DispatchProxy.Create<NativeTransferRawCustodyConsumerTests.ISourceRuntime, NativeTransferRawCustodyConsumerTests.SourceSpy>();
        NativeTransferRawCustodyConsumerTests.SourceSpy source = (NativeTransferRawCustodyConsumerTests.SourceSpy)(object)model;
        HybridCpuPlatformAuthorityProvider provider = new(model, new HybridCpuExecutableChildAdapter(spy.Inner, native));
        IEventBridgeProvider bridgeProxy = DispatchProxy.Create<IEventBridgeProvider, EventBridgeSpy>();
        EventBridgeSpy bridge = (EventBridgeSpy)(object)bridgeProxy; bridge.Inner = provider;
        RuntimeKernel kernel = new(bridgeProxy);
        ProcessHandle owner = TestFixtures.Create(kernel, 3261, 32610).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 3262, 32620).Handle;
        CapabilityId admin = kernel.MintCapability(new(32620), service, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(service, admin, owner, "provider-event-source",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(service, 32).Value!; foreign.Span.Fill(0x7C);
        CapabilityId create = kernel.MintCapability(new(32610), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        KernelEventEndpoint endpoint = kernel.CreateKernelEventEndpoint(owner).Value!;
        bool unavailable = false;
        source.BeforeSource = () =>
        {
            if (!afterEffect && new global::System.Diagnostics.StackTrace().GetFrames().Any(frame =>
                frame.GetMethod()?.DeclaringType == typeof(HybridCpuPlatformAuthorityProvider) && frame.GetMethod()?.Name == "InjectOriginalVirtualEvent"))
            {
                Assert.False(Monitor.IsEntered(typeof(HybridCpuPlatformAuthorityProvider).GetField("_childCreateCommitGate", Private)!.GetValue(provider)!));
                unavailable = true;
            }
            if (unavailable) throw new IOException("Original provider mapping source temporarily unavailable");
        };
        spy.After = name => { if (afterEffect && name == "ConsumeOriginalChildEvent") unavailable = true; };
        ProtocolDefinitionV1 protocol = VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "provider-event-source",
            new(protocol.ContractName, "1", protocol.ContractDigest), protocol, VirtualizationServiceClient.ResponseProtocol, []).Value!;
        EndpointSessionHandle session = kernel.OpenSession(owner, descriptor, []).Value;
        RuntimeVirtualizationServiceHost host = RuntimeVirtualizationServiceHost.CreateForSession(kernel, service, session).Value!;
        ValueTask reply = VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel, owner, session))
            .InjectEventAsync(new(domain.Domain, domain.EventCapability, endpoint));
        Assert.False(host.ProcessNext().IsSuccess); await Assert.ThrowsAnyAsync<Exception>(async () => await reply);
        Assert.True(unavailable); Assert.False(kernel.ConsumeKernelEvent(owner, endpoint).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        source.BeforeSource = null; spy.After = null;
        if (closureFault != 0)
        {
            object? fact = null, original = null; FieldInfo? route = null;
            bridge.AfterEventContain = () =>
            {
                bridge.AfterEventContain = null;
                IDictionary owners = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderChildren", Private)!.GetValue(provider)!;
                object child = Assert.Single(owners.Values.Cast<object>());
                fact = Assert.Single(((IList)child.GetType().GetProperty("OriginalEvents")!.GetValue(child)!).Cast<object>());
                route = fact.GetType().GetField("Closure")!; original = route.GetValue(fact); Assert.NotNull(original);
                route.SetValue(fact, closureFault == 1 ? null : ((PlatformOriginalVirtualEventClosure)original) with { });
            };
            Assert.False(kernel.TerminateProcess(owner).IsSuccess); Assert.NotNull(original);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            route!.SetValue(fact, original);
        }
        KernelResult closed = kernel.TerminateProcess(owner); Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(afterEffect ? 1 : 0, spy.Calls.GetValueOrDefault("InjectOriginalChildEvent"));
        Assert.Equal(afterEffect ? 1 : 0, spy.Calls.GetValueOrDefault("ConsumeOriginalChildEvent"));
        Assert.Equal(0, spy.Calls.GetValueOrDefault("ContainOriginalChildEvent"));
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x7C, x));
    }
    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(false, 2)]
    public async Task EventSourceExceptionDistinguishesNeverInvokedFromNativeMailbox(bool afterEffect, int closureFault)
    {
        IEventRuntime native = DispatchProxy.Create<IEventRuntime, EventSpy>(); EventSpy spy = (EventSpy)(object)native;
        ExactArtifactReleaseConsumerTests.IOriginalRootRuntime root = DispatchProxy.Create<ExactArtifactReleaseConsumerTests.IOriginalRootRuntime, ExactArtifactReleaseConsumerTests.RootSpy>();
        ExactArtifactReleaseConsumerTests.RootSpy source = (ExactArtifactReleaseConsumerTests.RootSpy)(object)root; source.Inner = spy.Inner;
        HybridCpuExecutableChildAdapter sdk = new(root, native);
        INeutralEventProvider neutralProxy = DispatchProxy.Create<INeutralEventProvider, NeutralEventSpy>();
        NeutralEventSpy neutral = (NeutralEventSpy)(object)neutralProxy; neutral.Inner = sdk;
        RuntimeKernel kernel = new(new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), neutralProxy));
        ProcessHandle owner = TestFixtures.Create(kernel, 3251, 32510).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 3252, 32520).Handle;
        CapabilityId admin = kernel.MintCapability(new(32520), service, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(service, admin, owner, "event-source-exception",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(service, 32).Value!; foreign.Span.Fill(0x7B);
        CapabilityId create = kernel.MintCapability(new(32510), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        KernelEventEndpoint endpoint = kernel.CreateKernelEventEndpoint(owner).Value!;
        int queries = 0;
        source.BeforeQuery = () =>
        {
            if (++queries != (afterEffect ? 2 : 1)) return;
            Assert.False(Monitor.IsEntered(typeof(HybridCpuExecutableChildAdapter).GetField("sync", Private)!.GetValue(sdk)!));
            Assert.False(kernel.InjectVirtualEvent(owner, domain.Domain, domain.EventCapability, endpoint).IsSuccess);
            source.ThrowSource = true;
        };
        ProtocolDefinitionV1 protocol = VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "event-source-exception",
            new(protocol.ContractName, "1", protocol.ContractDigest), protocol, VirtualizationServiceClient.ResponseProtocol, []).Value!;
        EndpointSessionHandle session = kernel.OpenSession(owner, descriptor, []).Value;
        RuntimeVirtualizationServiceHost host = RuntimeVirtualizationServiceHost.CreateForSession(kernel, service, session).Value!;
        ValueTask reply = VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel, owner, session))
            .InjectEventAsync(new(domain.Domain, domain.EventCapability, endpoint));
        Assert.False(host.ProcessNext().IsSuccess); await Assert.ThrowsAnyAsync<Exception>(async () => await reply);
        Assert.True(source.ThrowSource);
        Assert.False(kernel.ConsumeKernelEvent(owner, endpoint).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(afterEffect ? 1 : 0, spy.Calls.GetValueOrDefault("InjectOriginalChildEvent"));
        Assert.Equal(0, spy.Calls.GetValueOrDefault("ConsumeOriginalChildEvent"));
        source.BeforeQuery = null; source.ThrowSource = false;
        object? localFact = null, originalClosure = null; FieldInfo? closureRoute = null;
        if (closureFault != 0)
        {
            neutral.AfterQuery = () =>
            {
                if (originalClosure is not null) return;
                IDictionary owners = (IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("originalChildren", Private)!.GetValue(sdk)!;
                object child = Assert.Single(owners.Values.Cast<object>());
                localFact = Assert.Single(((IList)child.GetType().GetProperty("OriginalEvents")!.GetValue(child)!).Cast<object>());
                closureRoute = localFact.GetType().GetField("Closure")!;
                if (closureRoute.GetValue(localFact) is not NeutralOriginalVirtualEventClosure retained) return;
                originalClosure = retained;
                Assert.True(retained.NoEffect);
                closureRoute.SetValue(localFact, closureFault == 1 ? null : retained with { });
            };
            Assert.False(kernel.TerminateProcess(owner).IsSuccess);
            neutral.AfterQuery = null; Assert.NotNull(originalClosure);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.False(kernel.TerminateProcess(owner).IsSuccess); // Produced closure is never reminted from local metadata.
            closureRoute!.SetValue(localFact, originalClosure);
        }
        KernelResult closed = kernel.TerminateProcess(owner); Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(afterEffect ? 1 : 0, spy.Calls.GetValueOrDefault("InjectOriginalChildEvent"));
        Assert.Equal(afterEffect ? 1 : 0, spy.Calls.GetValueOrDefault("ContainOriginalChildEvent"));
        Assert.Equal(0, spy.Calls.GetValueOrDefault("ConsumeOriginalChildEvent"));
        if (!afterEffect) Assert.Equal(0, spy.Calls.GetValueOrDefault("ResolveOriginalChildEvent"));
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == foreign.Handle).State);
        Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x7B, x));
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task NewEventRefusalSurvivesOlderTerminalRouteLoss(bool copied, bool lostReply)
    {
        IEventRuntime native = DispatchProxy.Create<IEventRuntime, EventSpy>(); EventSpy spy = (EventSpy)(object)native;
        RuntimeKernel kernel = new(new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), new HybridCpuExecutableChildAdapter(spy.Inner, native)));
        ProcessHandle owner = TestFixtures.Create(kernel, 3241, 32410).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 3242, 32420).Handle;
        CapabilityId admin = kernel.MintCapability(new(32420), service, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(service, admin, owner, "next-event-refusal",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(service, 32).Value!; foreign.Span.Fill(0x7A);
        CapabilityId create = kernel.MintCapability(new(32410), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        KernelEventEndpoint endpoint = kernel.CreateKernelEventEndpoint(owner).Value!;
        ProtocolDefinitionV1 protocol = VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "next-event-refusal",
            new(protocol.ContractName, "1", protocol.ContractDigest), protocol, VirtualizationServiceClient.ResponseProtocol, []).Value!;
        EndpointSessionHandle session = kernel.OpenSession(owner, descriptor, []).Value;
        RuntimeVirtualizationServiceHost host = RuntimeVirtualizationServiceHost.CreateForSession(kernel, service, session).Value!;
        IVirtualizationService client = VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel, owner, session));
        ValueTask first = client.InjectEventAsync(new(domain.Domain, domain.EventCapability, endpoint));
        Assert.True(host.ProcessNext().IsSuccess); await first;
        Assert.True(kernel.ConsumeKernelEvent(owner, endpoint).IsSuccess);
        IDictionary children = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(spy.Inner)!;
        object child = Assert.Single(children.Values.Cast<object>());
        IList events = (IList)child.GetType().GetProperty("OriginalEvents")!.GetValue(child)!;
        object fact = Assert.Single(events.Cast<object>());
        FieldInfo route = fact.GetType().GetField("Observation")!;
        ExternalOriginalChildEventObservation original = (ExternalOriginalChildEventObservation)route.GetValue(fact)!;
        route.SetValue(fact, copied ? original with { } : null);
        spy.Fault = lostReply ? "inject-loss" : "exact";
        ValueTask second = client.InjectEventAsync(new(domain.Domain, domain.EventCapability, endpoint));
        Assert.False(host.ProcessNext().IsSuccess); await Assert.ThrowsAnyAsync<Exception>(async () => await second);
        Assert.False(kernel.ConsumeKernelEvent(owner, endpoint).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(2, spy.Calls.GetValueOrDefault("InjectOriginalChildEvent"));
        Assert.Equal(1, spy.Calls.GetValueOrDefault("ConsumeOriginalChildEvent"));
        route.SetValue(fact, original with { });
        Assert.False(kernel.TerminateProcess(owner).IsSuccess); // Equal terminal copy is not original continuity.
        Assert.Equal(1, spy.Calls.GetValueOrDefault("ContinueOriginalChildEventContainment")); // Retained intent, still refused; no mailbox effect.
        route.SetValue(fact, original);
        KernelResult closed = kernel.TerminateProcess(owner); Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(2, events.Count);
        object refused = events[1]!;
        ExternalOriginalChildEventObservation refusal = (ExternalOriginalChildEventObservation)route.GetValue(refused)!;
        Assert.True(refusal.NoEffect); Assert.True(refusal.NoFurtherDelivery); Assert.Null(refusal.Receipt);
        Assert.Null(refused.GetType().GetField("Payload")!.GetValue(refused));
        Assert.Equal(2, spy.Calls.GetValueOrDefault("InjectOriginalChildEvent"));
        Assert.Equal(1, spy.Calls.GetValueOrDefault("ConsumeOriginalChildEvent"));
        Assert.Equal(1, spy.Calls.GetValueOrDefault("ContainOriginalChildEvent"));
        Assert.Equal(1, spy.Calls.GetValueOrDefault("ContinueOriginalChildEventContainment")); // Exact restoration finishes by query only.
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == foreign.Handle).State);
        Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x7A, x));
    }
    [Theory]
    [InlineData(false, false, false, false)] [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)] [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)] [InlineData(false, true, true, false)]
    [InlineData(false, false, false, true)] [InlineData(false, true, false, true)]
    public async Task NativeEventRawRecoveryClosesOriginalGeneratedInvocation(bool terminal, bool copied, bool continuationLoss, bool legacy)
    {
        IHybridCpuChildDomainRuntimeV3 native = legacy ? DispatchProxy.Create<ILegacyEventRuntime, EventSpy>() : DispatchProxy.Create<IEventRuntime, EventSpy>();
        EventSpy spy = (EventSpy)(object)native;
        RuntimeKernel kernel = new(new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), new HybridCpuExecutableChildAdapter(spy.Inner, native)));
        ProcessHandle owner = TestFixtures.Create(kernel, 3231, 32310).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 3232, 32320).Handle;
        CapabilityId admin = kernel.MintCapability(new(32320), service, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(service, admin, owner, "native-event-raw",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(service, 32).Value!; foreign.Span.Fill(0x79);
        CapabilityId create = kernel.MintCapability(new(32310), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        KernelEventEndpoint endpoint = kernel.CreateKernelEventEndpoint(owner).Value!;
        object? fact = null, produced = null; FieldInfo? route = null;
        spy.After = name =>
        {
            if (name != (terminal ? "ConsumeOriginalChildEvent" : "InjectOriginalChildEvent") || fact is not null) return;
            IDictionary children = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(spy.Inner)!;
            object child = Assert.Single(children.Values.Cast<object>());
            fact = Assert.Single(((IList)child.GetType().GetProperty("OriginalEvents")!.GetValue(child)!).Cast<object>());
            route = fact.GetType().GetField("Observation")!; produced = route.GetValue(fact); Assert.NotNull(produced);
            route.SetValue(fact, copied ? ((ExternalOriginalChildEventObservation)produced) with { } : null);
        };
        ProtocolDefinitionV1 protocol = VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "native-event-raw",
            new(protocol.ContractName, "1", protocol.ContractDigest), protocol, VirtualizationServiceClient.ResponseProtocol, []).Value!;
        EndpointSessionHandle session = kernel.OpenSession(owner, descriptor, []).Value;
        RuntimeVirtualizationServiceHost host = RuntimeVirtualizationServiceHost.CreateForSession(kernel, service, session).Value!;
        ValueTask reply = VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel, owner, session))
            .InjectEventAsync(new(domain.Domain, domain.EventCapability, endpoint));
        Assert.False(host.ProcessNext().IsSuccess); await Assert.ThrowsAnyAsync<Exception>(async () => await reply);
        Assert.NotNull(fact); Assert.NotNull(produced); spy.After = null;
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(1, spy.Calls.GetValueOrDefault("InjectOriginalChildEvent"));
        Assert.Equal(terminal ? 1 : 0, spy.Calls.GetValueOrDefault("ConsumeOriginalChildEvent"));
        Assert.Equal(terminal ? 0 : 1, spy.Calls.GetValueOrDefault("ContainOriginalChildEvent"));
        route!.SetValue(fact, produced);
        spy.LoseContinuationResponses = continuationLoss;
        KernelResult closed = kernel.TerminateProcess(owner);
        if (legacy || continuationLoss)
        {
            Assert.False(closed.IsSuccess);
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(legacy ? 0 : 1, spy.Calls.GetValueOrDefault("ContinueOriginalChildEventContainment"));
            Assert.Equal(!legacy, ((ExternalOriginalChildEventObservation)route.GetValue(fact)!).NoFurtherDelivery);
            Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x79, x));
            if (legacy) return; // Incapable V1 retains its original admission and dependent pins.
            spy.LoseContinuationResponses = false;
            closed = kernel.TerminateProcess(owner);
        }
        Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(1, spy.Calls.GetValueOrDefault("InjectOriginalChildEvent"));
        Assert.Equal(terminal ? 1 : 0, spy.Calls.GetValueOrDefault("ConsumeOriginalChildEvent"));
        Assert.Equal(terminal ? 0 : 1, spy.Calls.GetValueOrDefault("ContainOriginalChildEvent"));
        Assert.Equal(terminal ? 0 : 1, spy.Calls.GetValueOrDefault("ContinueOriginalChildEventContainment"));
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x79, x));
    }
    public static IEnumerable<object[]> CachedEventFaults()
    {
        foreach (string layer in new[] { "sdk", "provider" })
        foreach (bool closureRoute in new[] { false, true })
        foreach (bool copied in new[] { false, true })
        foreach (bool contained in new[] { false, true })
            yield return [layer, closureRoute, copied, contained];
    }

    [Theory]
    [MemberData(nameof(CachedEventFaults))]
    public async Task CachedOriginalEventClosureRequiresProducedRawAndWrapper(string layer, bool closureRoute, bool copied, bool contained)
    {
        IEventRuntime native = DispatchProxy.Create<IEventRuntime, EventSpy>(); EventSpy spy = (EventSpy)(object)native;
        spy.Fault = contained ? "consume-null" : "exact";
        HybridCpuExecutableChildAdapter sdk = new(spy.Inner, native);
        HybridCpuPlatformAuthorityProvider provider = new(new NeutralDomainRuntimeFacade(), sdk);
        IEventBridgeProvider proxy = DispatchProxy.Create<IEventBridgeProvider, EventBridgeSpy>();
        EventBridgeSpy bridge = (EventBridgeSpy)(object)proxy; bridge.Inner = provider;
        RuntimeKernel kernel = new(proxy);
        ProcessHandle owner = TestFixtures.Create(kernel, 3221, 32210).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 3222, 32220).Handle;
        CapabilityId admin = kernel.MintCapability(new(32220), service, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(service, admin, owner, "cached-event",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(service, 32).Value!; foreign.Span.Fill(0x78);
        CapabilityId create = kernel.MintCapability(new(32210), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        KernelEventEndpoint endpoint = kernel.CreateKernelEventEndpoint(owner).Value!;
        ProtocolDefinitionV1 protocol = VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "cached-event",
            new(protocol.ContractName, "1", protocol.ContractDigest), protocol, VirtualizationServiceClient.ResponseProtocol, []).Value!;
        EndpointSessionHandle session = kernel.OpenSession(owner, descriptor, []).Value;
        RuntimeVirtualizationServiceHost host = RuntimeVirtualizationServiceHost.CreateForSession(kernel, service, session).Value!;
        IVirtualizationService client = VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel, owner, session));
        ValueTask reply = client.InjectEventAsync(new(domain.Domain, domain.EventCapability, endpoint));
        Assert.Equal(!contained, host.ProcessNext().IsSuccess);
        if (contained) await Assert.ThrowsAnyAsync<Exception>(async () => await reply); else await reply;
        Assert.Equal(!contained, kernel.ConsumeKernelEvent(owner, endpoint).IsSuccess);
        if (contained)
        {
            IDictionary children = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_originalChildOwners", Private)!.GetValue(kernel.PlatformAuthority)!;
            object child = Assert.Single(children.Values.Cast<object>());
            object invocation = Assert.Single(((IList)child.GetType().GetProperty("EventInvocations")!.GetValue(child)!).Cast<object>());
            bool interrupted = false;
            bridge.AfterSource = () =>
            {
                if (invocation.GetType().GetProperty("EventCleanupRaw")!.GetValue(invocation) is null) return;
                bridge.AfterSource = null; interrupted = true;
                throw new IOException("Local closure consumption interrupted after original native containment.");
            };
            Assert.False(kernel.TerminateProcess(owner).IsSuccess); Assert.True(interrupted);
        }
        object holder = layer == "sdk" ? sdk : provider;
        IDictionary records = (IDictionary)(layer == "sdk" ? typeof(HybridCpuExecutableChildAdapter).GetField("originalChildren", Private)!.GetValue(sdk)! :
            typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderChildren", Private)!.GetValue(provider)!);
        object eventChild = Assert.Single(records.Values.Cast<object>());
        object fact = Assert.Single(((IList)eventChild.GetType().GetProperty("OriginalEvents")!.GetValue(eventChild)!).Cast<object>());
        FieldInfo route = fact.GetType().GetField(closureRoute ? "Closure" : layer == "sdk" ? "TerminalRaw" : "CleanupRaw")!;
        object original = route.GetValue(fact)!; Assert.NotNull(original);
        object request = fact.GetType().GetProperty("Request")!.GetValue(fact)!;
        MethodInfo query = holder.GetType().GetMethod("ResolveOriginalVirtualEventClosure")!;
        object originalClosure = query.Invoke(holder, [request])!; Assert.NotNull(originalClosure);
        route.SetValue(fact, copied ? typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(original, null) : null);
        Assert.Null(query.Invoke(holder, [request]));
        object refused = holder.GetType().GetMethod("ContainOriginalVirtualEvent")!.Invoke(holder, [request])!;
        Assert.False((bool)refused.GetType().GetProperty("IsSuccess")!.GetValue(refused)!);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        Assert.False(kernel.TerminateProcess(owner).IsSuccess);
        IDictionary providerChildren = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderChildren", Private)!.GetValue(provider)!;
        object providerChild = Assert.Single(providerChildren.Values.Cast<object>());
        Assert.Null(providerChild.GetType().GetProperty("FailedTransition")!.GetValue(providerChild));
        Assert.Equal(0, spy.Calls.GetValueOrDefault("CloseOriginalChild"));
        Assert.Equal(0, spy.Calls.GetValueOrDefault("TransitionChildDomain"));
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(1, spy.Calls.GetValueOrDefault("InjectOriginalChildEvent"));
        Assert.Equal(contained ? 1 : 0, spy.Calls.GetValueOrDefault("ContainOriginalChildEvent"));
        route.SetValue(fact, original);
        Assert.Same(originalClosure, query.Invoke(holder, [request]));
        KernelResult closed = kernel.TerminateProcess(owner); Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.Equal(1, spy.Calls.GetValueOrDefault("InjectOriginalChildEvent"));
        Assert.Equal(1, spy.Calls.GetValueOrDefault("ConsumeOriginalChildEvent")); // Suppressed, not native delivery, in containment mode.
        Assert.Equal(contained ? 1 : 0, spy.Calls.GetValueOrDefault("ContainOriginalChildEvent"));
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == foreign.Handle).State);
        Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x78, x));
    }
    public interface IEventBridgeProvider : ExactArtifactReleaseConsumerTests.IReleaseBridgeProvider,
        IPlatformOriginalVirtualEventProvider, IPlatformVirtualEventProvider { }
    public class EventBridgeSpy : DispatchProxy
    {
        public HybridCpuPlatformAuthorityProvider Inner = null!;
        public Action? AfterSource;
        public Action? AfterEventContain;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            object? result;
            try { result = method!.Invoke(Inner, args); }
            catch (TargetInvocationException exception) { throw exception.InnerException!; }
            if (method.Name == "get_CurrentIncarnation") AfterSource?.Invoke();
            if (method.Name == "ContainOriginalVirtualEvent") AfterEventContain?.Invoke();
            return result;
        }
    }
    public interface INeutralEventProvider : INeutralChildDomainProvider, INeutralRuntimeFeatureProvider,
        INeutralOriginalChildCreateProvider, INeutralOriginalChildCloseProvider, INeutralExternalParentClosureProvider,
        INeutralOriginalVirtualEventProvider, INeutralVirtualEventProvider { }
    public class NeutralEventSpy : DispatchProxy
    {
        public HybridCpuExecutableChildAdapter Inner = null!;
        public Action? AfterQuery;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            object? result;
            try { result = method!.Invoke(Inner, args); }
            catch (TargetInvocationException exception) { throw exception.InnerException!; }
            if (method.Name == "ResolveOriginalVirtualEventClosure") AfterQuery?.Invoke();
            return result;
        }
    }

    [Theory]
    [InlineData("sdk-admission", false)] [InlineData("sdk-admission", true)]
    [InlineData("sdk-terminal", false)] [InlineData("sdk-terminal", true)]
    [InlineData("provider", false)] [InlineData("provider", true)]
    [InlineData("provider-source", false)]
    public async Task GeneratedEventPublicationRequiresRetainedRawThroughQueries(string layer, bool copied)
    {
        IEventRuntime native = DispatchProxy.Create<IEventRuntime, EventSpy>(); EventSpy spy = (EventSpy)(object)native;
        HybridCpuExecutableChildAdapter sdk = new(spy.Inner, native);
        INeutralEventProvider neutralProxy = DispatchProxy.Create<INeutralEventProvider, NeutralEventSpy>();
        NeutralEventSpy neutral = (NeutralEventSpy)(object)neutralProxy; neutral.Inner = sdk;
        NeutralDomainRuntimeFacade model = new();
        Guid originalIncarnation = model.MappingClosureIncarnation;
        HybridCpuPlatformAuthorityProvider provider = new(model, neutralProxy);
        RuntimeKernel kernel = new(provider);
        ProcessHandle caller = TestFixtures.Create(kernel, 3201, 32010).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 3202, 32020).Handle;
        CapabilityId admin = kernel.MintCapability(new(32020), service, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(service, admin, caller, "event-publication",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!.ProcessBudget;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(caller, 64).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(service, 8).Value!; foreign.Span.Fill(0x76);
        CapabilityId create = kernel.MintCapability(new(32010), caller, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain = kernel.CreateVirtualDomain(caller, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(caller, domain.Domain, domain.ConfigureCapability).IsSuccess);
        KernelEventEndpoint endpoint = kernel.CreateKernelEventEndpoint(caller).Value!;
        object? fact = null, raw = null; FieldInfo? field = null;
        Action change = () =>
        {
            if (fact is not null) return;
            bool atProvider = layer is "provider" or "provider-source";
            IDictionary children = (IDictionary)(atProvider ? typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderChildren", Private)!.GetValue(provider)! :
                typeof(HybridCpuExecutableChildAdapter).GetField("originalChildren", Private)!.GetValue(sdk)!);
            object child = Assert.Single(children.Values.Cast<object>());
            object current = Assert.Single(((IList)child.GetType().GetProperty("OriginalEvents")!.GetValue(child)!).Cast<object>());
            FieldInfo candidate = current.GetType().GetField(atProvider ? "CleanupRaw" : layer == "sdk-admission" ? "AdmissionRaw" : "TerminalRaw")!;
            if (candidate.GetValue(current) is not { } retained) return;
            fact = current; field = candidate; raw = retained;
            if (layer == "provider-source")
            {
                typeof(NeutralDomainRuntimeFacade).GetField("<MappingClosureIncarnation>k__BackingField", Private)!.SetValue(model, Guid.NewGuid());
                return;
            }
            candidate.SetValue(current, copied ? typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(retained, null) : null);
        };
        if (layer is "provider" or "provider-source") neutral.AfterQuery = change;
        else spy.After = name => { if (name == "ResolveOriginalChildEvent") change(); };
        ProtocolDefinitionV1 protocol = VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "event-publication",
            new(protocol.ContractName, "1", protocol.ContractDigest), protocol, VirtualizationServiceClient.ResponseProtocol, []).Value!;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, []).Value;
        RuntimeVirtualizationServiceHost host = RuntimeVirtualizationServiceHost.CreateForSession(kernel, service, session).Value!;
        IVirtualizationService client = VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
        ValueTask reply = client.InjectEventAsync(new(domain.Domain, domain.EventCapability, endpoint));
        Assert.False(host.ProcessNext().IsSuccess); await Assert.ThrowsAnyAsync<Exception>(async () => await reply);
        Assert.NotNull(fact); Assert.NotNull(raw); spy.After = null; neutral.AfterQuery = null;
        if (layer == "provider-source")
        {
            Assert.Null(fact.GetType().GetField("Closure")!.GetValue(fact));
            Assert.False(kernel.TerminateProcess(caller).IsSuccess);
            typeof(NeutralDomainRuntimeFacade).GetField("<MappingClosureIncarnation>k__BackingField", Private)!.SetValue(model, originalIncarnation);
        }
        Assert.False(kernel.ConsumeKernelEvent(caller, endpoint).IsSuccess);
        Assert.Equal(1, spy.Calls.GetValueOrDefault("InjectOriginalChildEvent"));
        Assert.Equal(layer == "sdk-admission" ? 0 : 1, spy.Calls.GetValueOrDefault("ConsumeOriginalChildEvent"));
        Assert.Equal(0, spy.Calls.GetValueOrDefault("ContainOriginalChildEvent"));
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.Equal(64UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        field!.SetValue(fact, raw);
        KernelResult closed = kernel.TerminateProcess(caller); Assert.True(closed.IsSuccess, closed.Message);
        Assert.Equal(1, spy.Calls.GetValueOrDefault("InjectOriginalChildEvent"));
        Assert.Equal(layer == "sdk-admission" ? 1 : 0, spy.Calls.GetValueOrDefault("ContainOriginalChildEvent"));
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x76, x));
    }

    [Theory]
    [InlineData("raw-loss")] [InlineData("raw-copy")] [InlineData("request-copy")]
    [InlineData("invocation-copy")] [InlineData("source-throw")] [InlineData("reentry")]
    [InlineData("sdk-raw-loss")] [InlineData("sdk-raw-copy")]
    [InlineData("provider-raw-loss")] [InlineData("provider-raw-copy")]
    [InlineData("provider-source")]
    public async Task GeneratedEventCleanupRequiresOriginalCustodyAfterSourceCallback(string mode)
    {
        IEventRuntime native = DispatchProxy.Create<IEventRuntime, EventSpy>(); EventSpy spy = (EventSpy)(object)native;
        spy.Fault = "consume-null"; // Native mailbox remains queued; no local signal was published.
        HybridCpuExecutableChildAdapter sdk = new(spy.Inner, native);
        INeutralEventProvider neutralProxy = DispatchProxy.Create<INeutralEventProvider, NeutralEventSpy>();
        NeutralEventSpy neutral = (NeutralEventSpy)(object)neutralProxy; neutral.Inner = sdk;
        NeutralDomainRuntimeFacade model = new();
        Guid originalIncarnation = model.MappingClosureIncarnation;
        HybridCpuPlatformAuthorityProvider provider = new(model, neutralProxy);
        IEventBridgeProvider proxy = DispatchProxy.Create<IEventBridgeProvider, EventBridgeSpy>();
        EventBridgeSpy bridge = (EventBridgeSpy)(object)proxy; bridge.Inner = provider;
        RuntimeKernel kernel = new(proxy);
        ProcessHandle caller = TestFixtures.Create(kernel, 3191, 31910).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 3192, 31920).Handle;
        CapabilityId admin = kernel.MintCapability(new(31920), service, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(service, admin, caller, "event-custody",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(caller, 512).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(service, 32).Value!; foreign.Span.Fill(0x75);
        CapabilityId create = kernel.MintCapability(new(31910), caller, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet domain = kernel.CreateVirtualDomain(caller, create, new(1, 4096)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(caller, domain.Domain, domain.ConfigureCapability).IsSuccess);
        KernelEventEndpoint endpoint = kernel.CreateKernelEventEndpoint(caller).Value!;
        ProtocolDefinitionV1 protocol = VirtualizationServiceClient.CreateProtocol();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, "event-custody",
            new(protocol.ContractName, "1", protocol.ContractDigest), protocol, VirtualizationServiceClient.ResponseProtocol, []).Value!;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor, []).Value;
        RuntimeVirtualizationServiceHost host = RuntimeVirtualizationServiceHost.CreateForSession(kernel, service, session).Value!;
        IVirtualizationService client = VirtualizationServiceClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
        ValueTask reply = client.InjectEventAsync(new(domain.Domain, domain.EventCapability, endpoint));
        Assert.False(host.ProcessNext().IsSuccess);
        await Assert.ThrowsAnyAsync<Exception>(async () => await reply);
        Assert.False(kernel.ConsumeKernelEvent(caller, endpoint).IsSuccess);
        IDictionary owners = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_originalChildOwners", Private)!.GetValue(kernel.PlatformAuthority)!;
        object child = Assert.Single(owners.Values.Cast<object>());
        IList invocations = (IList)child.GetType().GetProperty("EventInvocations")!.GetValue(child)!;
        object invocation = Assert.Single(invocations.Cast<object>());
        PropertyInfo rawProperty = invocation.GetType().GetProperty("EventCleanupRaw")!;
        PropertyInfo requestProperty = invocation.GetType().GetProperty("EventOriginal")!;
        object request = requestProperty.GetValue(invocation)!;
        object? raw = null;
        bool changed = false;
        Action restore = () => { invocations[0] = invocation; rawProperty.SetValue(invocation, raw); requestProperty.SetValue(invocation, request); };
        if (mode.StartsWith("sdk-", StringComparison.Ordinal) || mode.StartsWith("provider-", StringComparison.Ordinal))
        {
            bool atSdk = mode.StartsWith("sdk-", StringComparison.Ordinal);
            IDictionary records = (IDictionary)(atSdk ? typeof(HybridCpuExecutableChildAdapter).GetField("originalChildren", Private)!.GetValue(sdk)! :
                typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderChildren", Private)!.GetValue(provider)!);
            object eventChild = Assert.Single(records.Values.Cast<object>());
            IList events = (IList)eventChild.GetType().GetProperty("OriginalEvents")!.GetValue(eventChild)!;
            object fact = Assert.Single(events.Cast<object>());
            FieldInfo field = fact.GetType().GetField(atSdk ? "TerminalRaw" : "CleanupRaw")!;
            Action change = () =>
            {
                if (changed || field.GetValue(fact) is not { } retained) return;
                changed = true; raw = retained;
                if (mode == "provider-source")
                {
                    typeof(NeutralDomainRuntimeFacade).GetField("<MappingClosureIncarnation>k__BackingField", Private)!.SetValue(model, Guid.NewGuid());
                    return;
                }
                field.SetValue(fact, mode.EndsWith("copy", StringComparison.Ordinal) ? typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(retained, null) : null);
            };
            if (atSdk) spy.After = name => { if (name == "ResolveOriginalChildEvent") change(); };
            else neutral.AfterQuery = change;
            restore = () => field.SetValue(fact, raw);
            if (mode == "provider-source") restore = () =>
            {
                Assert.Null(fact.GetType().GetField("Closure")!.GetValue(fact));
                typeof(NeutralDomainRuntimeFacade).GetField("<MappingClosureIncarnation>k__BackingField", Private)!.SetValue(model, originalIncarnation);
            };
        }
        else bridge.AfterSource = () =>
        {
            if (rawProperty.GetValue(invocation) is not { } retained) return;
            bridge.AfterSource = null; changed = true; raw = retained;
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", Private)!.GetValue(kernel.PlatformAuthority)!));
            if (mode == "raw-loss") rawProperty.SetValue(invocation, null);
            if (mode == "raw-copy") rawProperty.SetValue(invocation, ((PlatformOriginalVirtualEventClosure)retained) with { });
            if (mode == "request-copy") requestProperty.SetValue(invocation, ((PlatformOriginalVirtualEventRequest)request) with { });
            if (mode == "invocation-copy") invocations[0] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(invocation, null);
            if (mode == "source-throw") throw new IOException("Source callback lost after native containment.");
            if (mode == "reentry") Assert.False(kernel.TerminateProcess(caller).IsSuccess);
        };
        KernelResult first = kernel.TerminateProcess(caller);
        spy.After = null; neutral.AfterQuery = null;
        Assert.True(changed); Assert.NotNull(raw);
        Assert.Equal(mode == "reentry", first.IsSuccess);
        Assert.Equal(1, spy.Calls.GetValueOrDefault("InjectOriginalChildEvent"));
        Assert.Equal(1, spy.Calls.GetValueOrDefault("ContainOriginalChildEvent"));
        if (!first.IsSuccess)
        {
            Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
            Assert.Equal(512UL, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Single(invocations.Cast<object>());
            if (mode != "invocation-copy") Assert.Same(invocation, invocations[0]);
            restore();
            KernelResult closed = kernel.TerminateProcess(caller); Assert.True(closed.IsSuccess, closed.Message);
        }
        Assert.Empty(invocations.Cast<object>());
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(x => x.Handle == buffer.Handle).State);
        Assert.All(kernel.QueryBudget(budget).Value!.Usage, x => Assert.Equal(0UL, x.Used));
        Assert.Equal(1, spy.Calls.GetValueOrDefault("InjectOriginalChildEvent"));
        Assert.Equal(1, spy.Calls.GetValueOrDefault("ContainOriginalChildEvent"));
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == foreign.Handle).State);
        Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x75, x));
        Assert.True(kernel.Processes.Resolve(service).IsSuccess);
    }
    public interface ILegacyEventRuntime : ExactArtifactReleaseConsumerTests.IReleaseRuntime, IHybridCpuOriginalChildEventRuntimeV1,
        IHybridCpuOriginalNestedChildRuntimeV1, IHybridCpuPrivateExecutableDataQuotaRuntimeV1 { }
    public interface IEventRuntime : ILegacyEventRuntime, IHybridCpuOriginalChildEventContainmentContinuationRuntimeV1 { }
    public class EventSpy : DispatchProxy
    {
        public HybridCpuExternalRuntime Inner = new();
        public string Fault = "exact";
        public Dictionary<string, int> Calls = [];
        public Action<string>? After;
        public bool LoseContinuationResponses;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            string name = targetMethod!.Name; Calls[name] = Calls.GetValueOrDefault(name) + 1;
            if (LoseContinuationResponses && name == "ResolveOriginalChildEvent" && Calls.GetValueOrDefault("ContinueOriginalChildEventContainment") != 0)
                throw new IOException("Original event continuation query lost after settlement.");
            if (Fault == "query-loss" && name == "ResolveOriginalChildEvent") throw new IOException("Query reply lost.");
            if (Fault == "consume-null" && name == "ConsumeOriginalChildEvent") return null; // Invocation disposition lost before delivery; original mailbox really remains queued.
            object? result;
            try { result = targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException exception) { throw exception.InnerException!; }
            After?.Invoke(name);
            if (LoseContinuationResponses && name == "ContinueOriginalChildEventContainment")
                throw new IOException("Original event continuation reply lost after settlement.");
            if (Fault == "consume-null" && name == "ContainOriginalChildEvent" && result is ExternalOriginalChildEventObservation closed)
                Assert.Same(closed, Inner.ConsumeOriginalChildEvent((ExternalOriginalChildEventRequest)args![0]!)); // Late consume cannot produce another delivery after the exact fence.
            if (Fault == "inject-loss" && name == "InjectOriginalChildEvent" || Fault == "consume-loss" && name == "ConsumeOriginalChildEvent")
                throw new IOException("Reply lost after native effect.");
            if (Fault == "terminal-copy" && name == "ConsumeOriginalChildEvent" && result is ExternalOriginalChildEventObservation raw) return raw with { };
            if (Fault is "history-loss" or "native-history-duplicate" && name == "InjectOriginalChildEvent")
            {
                IDictionary children = (IDictionary)typeof(HybridCpuExternalRuntime).GetField("childRegistry", Private)!.GetValue(Inner)!;
                object child = Assert.Single(children.Values.Cast<object>());
                IList history = (IList)child.GetType().GetProperty("OriginalEvents")!.GetValue(child)!;
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
    [InlineData("exact")] [InlineData("ancestor-aba")] [InlineData("event-copy")] [InlineData("history-loss")]
    public void OriginalChildArtifactCleanupRequiresExactAncestorEventFault(string mode)
    {
        IEventRuntime native = DispatchProxy.Create<IEventRuntime, EventSpy>(); EventSpy spy = (EventSpy)(object)native;
        HybridCpuExecutableChildAdapter sdk = new(spy.Inner, native);
        RuntimeKernel kernel = new(new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), sdk));
        ProcessHandle owner = TestFixtures.Create(kernel, 3211, 32110).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 3212, 32120).Handle;
        const ulong bytes = 1024 * 1024;
        CapabilityId admin = kernel.MintCapability(new(32120), service, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(service, admin, owner, "ancestor-event",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, bytes * 2), new(ServiceBudgetDimension.GuestMemoryBytes, bytes),
             new(ServiceBudgetDimension.PinnedMappedMemoryBytes, bytes), new(ServiceBudgetDimension.RegionUses, 8)]).Value!.ProcessBudget;
        OwnedBuffer<byte> code = kernel.AllocateBuffer<byte>(owner, (int)bytes).Value!;
        OwnedBuffer<byte> foreign = kernel.AllocateBuffer<byte>(service, 16).Value!; foreign.Span.Fill(0x77);
        CapabilityId create = kernel.MintCapability(new(32110), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        VirtualDomainAuthoritySet parent = kernel.CreateVirtualDomain(owner, create, new(1, (long)bytes * 2)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, parent.Domain, parent.ConfigureCapability).IsSuccess);
        VirtualDomainAuthoritySet child = kernel.CreateNestedVirtualDomain(owner, parent.Domain, parent.ConfigureCapability,
            new(new(1, (long)bytes), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory | VirtualDomainAuthorityClass.Execution)).Value!;
        Assert.True(kernel.ConfigureVirtualDomain(owner, child.Domain, child.ConfigureCapability).IsSuccess);
        CapabilityId memory = kernel.MintCapability(new(32110), owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(code.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Execute).Value!.CapabilityId;
        GuestRegionMapping mapped = kernel.MapGuestRegion(owner, child.Domain, child.MemoryCapability, memory, code.Handle,
            new(0, (long)bytes), GuestMemoryAccess.Read | GuestMemoryAccess.Execute, bytes).Value!;
        DirectoryInfo? directory = new(AppContext.BaseDirectory); string package;
        while (true)
        {
            Assert.NotNull(directory);
            package = Path.Combine(directory!.FullName, "tests", "SingPlus.Tests", "Fixtures", "OriginalExecutionTerminal", "CurrentStartupProfile", "finite-branch-materialized.hcexe");
            if (File.Exists(package)) break;
            directory = directory.Parent;
        }
        KernelResult bound = kernel.BindVirtualDomainExecutable(owner, child.Domain, child.ExecuteCapability, mapped.Mapping, File.ReadAllBytes(package), 256);
        Assert.True(bound.IsSuccess, bound.Message);
        spy.Fault = "consume-null";
        KernelEventEndpoint endpoint = kernel.CreateKernelEventEndpoint(owner).Value!;
        Assert.False(kernel.InjectVirtualEvent(owner, parent.Domain, parent.EventCapability, endpoint).IsSuccess);
        IDictionary records = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_originalChildOwners", Private)!.GetValue(kernel.PlatformAuthority)!;
        object parentRecord = records.Values.Cast<object>().Single(x => x.GetType().GetProperty("ParentChild")!.GetValue(x) is null);
        IList events = (IList)parentRecord.GetType().GetProperty("EventInvocations")!.GetValue(parentRecord)!;
        if (mode == "ancestor-aba") parentRecord.GetType().GetProperty("State")!.SetValue(parentRecord, PlatformChildDomainState.Faulted);
        if (mode == "event-copy") events[0] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(events[0], null);
        if (mode == "history-loss") events.Clear();
        KernelResult closed = kernel.TerminateProcess(owner);
        Assert.Equal(mode == "exact", closed.IsSuccess);
        Assert.Equal(1, spy.Calls.GetValueOrDefault("InjectOriginalChildEvent"));
        Assert.Equal(mode == "exact" ? 1 : 0, spy.Calls.GetValueOrDefault("ReleaseChildExecutableArtifact"));
        Assert.Equal(mode == "exact" ? 1 : 0, spy.Calls.GetValueOrDefault("ContainOriginalChildEvent"));
        Assert.Equal(mode == "exact" ? RegionState.Released : RegionState.Owned, kernel.Regions.Snapshot().Single(x => x.Handle == code.Handle).State);
        Assert.Equal(mode == "exact" ? 0UL : bytes * 2, kernel.QueryBudget(budget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.All(foreign.Span.ToArray(), x => Assert.Equal((byte)0x77, x));
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
        IEventRuntime native = DispatchProxy.Create<IEventRuntime, EventSpy>(); EventSpy spy = (EventSpy)(object)native; spy.Fault = mode;
        NeutralDomainRuntimeFacade model = new NeutralDomainRuntimeFacade();
        HybridCpuExecutableChildAdapter sdk = new HybridCpuExecutableChildAdapter(spy.Inner, native);
        HybridCpuPlatformAuthorityProvider provider = new HybridCpuPlatformAuthorityProvider(model, sdk); RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle owner) = TestFixtures.Create(kernel, 784, 1128);
        ProcessHandle admin = TestFixtures.Create(kernel, 99, 999).Handle;
        CapabilityId budgetCap = kernel.MintCapability(new(999), admin, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, budgetCap, owner, "original-event", [new(ServiceBudgetDimension.OwnedMemoryBytes, 512)]).Value!.ProcessBudget;
        OwnedBuffer<byte> region = kernel.AllocateBuffer<byte>(owner, 512).Value!;
        CapabilityId create = kernel.MintCapability(process.DomainId, owner, ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        KernelResult<VirtualDomainAuthoritySet> created = kernel.CreateVirtualDomain(owner, create, new(1, 4096)); Assert.True(created.IsSuccess, created.Message);
        VirtualDomainAuthoritySet domain = created.Value!; Assert.True(kernel.ConfigureVirtualDomain(owner, domain.Domain, domain.ConfigureCapability).IsSuccess);
        KernelEventEndpoint endpoint = kernel.CreateKernelEventEndpoint(owner).Value!;
        KernelEventEndpoint reentryEndpoint = kernel.CreateKernelEventEndpoint(owner).Value!;
        object registry = typeof(RuntimeKernel).GetField("_kernelEvents", Private)!.GetValue(kernel)!;
        IDictionary endpoints = (IDictionary)registry.GetType().GetField("_endpoints", Private)!.GetValue(registry)!;
        object originalEndpoint = endpoints[endpoint.EndpointId]!;
        ProcessHandle foreign = TestFixtures.Create(kernel, 785, 1129).Handle;
        KernelEventEndpoint foreignEndpoint = kernel.CreateKernelEventEndpoint(foreign).Value!;
        object foreignRecord = endpoints[foreignEndpoint.EndpointId]!;
        bool routeLoss = mode is "endpoint-route-loss" or "endpoint-foreign-route";
        Task<KernelResult<KernelEvent>>? waiter = routeLoss ? kernel.WaitForKernelEventAsync(owner, endpoint).AsTask() : null;
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
                    IDictionary records = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_originalChildOwners", Private)!.GetValue(kernel.PlatformAuthority)!;
                    object child = Assert.Single(records.Values.Cast<object>());
                    if (mode == "bridge-aba") child.GetType().GetProperty("State")!.SetValue(child, child.GetType().GetProperty("State")!.GetValue(child));
                    else
                    {
                        child.GetType().GetField("_lifecycleRevision", Private)!.SetValue(child, long.MaxValue);
                        child.GetType().GetField("_lifecycleRevisionExhausted", Private)!.SetValue(child, 1);
                    }
                }
                if (mode == "bridge-history-loss")
                {
                    IDictionary records = (IDictionary)typeof(PlatformAuthorityBridge).GetField("_originalChildOwners", Private)!.GetValue(kernel.PlatformAuthority)!;
                    object child = Assert.Single(records.Values.Cast<object>());
                    ((IList)child.GetType().GetProperty("EventInvocations")!.GetValue(child)!).Clear();
                }
                if (mode == "sdk-history-loss")
                {
                    IDictionary records = (IDictionary)typeof(HybridCpuExecutableChildAdapter).GetField("originalChildren", Private)!.GetValue(sdk)!;
                    object child = Assert.Single(records.Values.Cast<object>());
                    ((IList)child.GetType().GetProperty("OriginalEvents")!.GetValue(child)!).Clear();
                }
                if (mode == "provider-history-loss")
                {
                    IDictionary records = (IDictionary)typeof(HybridCpuPlatformAuthorityProvider).GetField("_originalProviderChildren", Private)!.GetValue(provider)!;
                    object child = Assert.Single(records.Values.Cast<object>());
                    ((IList)child.GetType().GetProperty("OriginalEvents")!.GetValue(child)!).Clear();
                }
            }
        };
        KernelResult injected = kernel.InjectVirtualEvent(owner, domain.Domain, domain.EventCapability, endpoint);
        bool published = mode is "exact" or "endpoint-overlap" or "reentry";
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
        KernelResult terminated = kernel.TerminateProcess(owner);
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
