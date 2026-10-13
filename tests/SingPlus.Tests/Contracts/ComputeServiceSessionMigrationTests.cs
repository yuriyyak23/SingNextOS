using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip.Compute;

namespace SingPlus.Tests.Contracts;

public sealed class ComputeServiceSessionMigrationTests
{
    [Fact]
    public async Task CallerCancellationAfterActualSoftwareByteEffectClosesOriginalCustodyAndNeverPublishesDestination()
    {
        var proxy=global::System.Reflection.DispatchProxy.Create<SingPlus.Tests.Platform.PlatformDsc1ComputeTests.IOriginalDsc1Runtime,
            SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy>();
        var spy=(SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy)(object)proxy; spy.Native=new();
        var scenario=CreateComponentScenario(new SingPlus.Platform.HybridCpu.HybridCpuPlatformAuthorityProvider(proxy));
        var budget=scenario.Kernel.QueryComponent(new ComponentIdentity("compute-session-test")).Value!.ProcessBudget;
        ulong Used()=>scenario.Kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        var foreign=TestFixtures.Create(scenario.Kernel,2581,2582).Handle;
        var independent=scenario.Kernel.AllocateBuffer<byte>(foreign,16).Value!;
        scenario.Source.Span.Fill(0x67);
        using var cancellation=new CancellationTokenSource();
        spy.AfterCopy=()=>
        {
            spy.AfterCopy=null;
            Assert.Equal(64UL,Used());
            Assert.Throws<InvalidOperationException>(()=>{_=scenario.Source.Span.Length;});
            cancellation.Cancel();
        };
        var client=ComputeServiceClient.Create(new RuntimeSipClientTransport(scenario.Kernel,scenario.Caller,scenario.Session,cancellation.Token));
        var pending=client.CopyAsync(scenario.Source,scenario.Destination).AsTask();
        var settled=scenario.Host.ProcessNextCopy(); Assert.True(settled.IsSuccess,settled.Message);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1,spy.Copies); Assert.Equal(0UL,Used()); Assert.False(scenario.Host.HasPendingCopy);
        Assert.True(scenario.Source.IsValid); Assert.Equal((byte)0x67,scenario.Source.Span[0]);
        Assert.False(scenario.Destination.IsValid); Assert.Equal(16,independent.Span.Length);
        Assert.True(scenario.Kernel.CloseSession(scenario.Caller,scenario.Session).IsSuccess);
        Assert.True(scenario.Kernel.FaultComponent(new ComponentIdentity("compute-session-test")).IsSuccess);
        Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount); Assert.Equal(0,spy.Native.ActiveBindingCount);
    }
    [Fact]
    public async Task ActualSoftwareServiceRetriesOriginalResponseSettlementAfterThrowWithoutCopyRepeat()
    {
        var proxy=global::System.Reflection.DispatchProxy.Create<SingPlus.Tests.Platform.PlatformDsc1ComputeTests.IOriginalDsc1Runtime,
            SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy>();
        var spy=(SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy)(object)proxy; spy.Native=new();
        var scenario=CreateComponentScenario(new SingPlus.Platform.HybridCpu.HybridCpuPlatformAuthorityProvider(proxy));
        var budget=scenario.Kernel.QueryComponent(new ComponentIdentity("compute-session-test")).Value!.ProcessBudget;
        ulong Used()=>scenario.Kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        scenario.Source.Span.Fill(0x72);
        var client=ComputeServiceClient.Create(new RuntimeSipClientTransport(scenario.Kernel,scenario.Caller,scenario.Session));
        var pending=client.CopyAsync(scenario.Source,scenario.Destination).AsTask();
        scenario.Kernel.SessionInvocationSettlementReservedHook=()=>
        {
            scenario.Kernel.SessionInvocationSettlementReservedHook=null;
            throw new IOException("Settlement interrupted before response effect");
        };
        Assert.Throws<IOException>(()=>scenario.Host.ProcessNextCopy());
        Assert.True(scenario.Host.HasPendingCopy); Assert.Equal(1,spy.Copies);
        Assert.Equal(16UL,Used()); Assert.True(scenario.Source.IsValid);
        Assert.False(pending.IsCompleted);
        var resumed=scenario.Host.AdvancePendingCopy(); Assert.True(resumed.IsSuccess,resumed.Message);
        var returned=await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.All(returned.Span.ToArray(),b=>Assert.Equal((byte)0x72,b));
        Assert.Equal(1,spy.Copies); Assert.Equal(0UL,Used()); Assert.False(scenario.Host.HasPendingCopy);
        Assert.True(scenario.Kernel.CloseSession(scenario.Caller,scenario.Session).IsSuccess);
        Assert.True(scenario.Kernel.FaultComponent(new ComponentIdentity("compute-session-test")).IsSuccess);
        Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount); Assert.Equal(0,spy.Native.ActiveBindingCount);
    }
    [Fact]
    public async Task ActualServiceUnknownSubmitRetainsOriginalContinuationAndSettlesWithoutRepeat()
    {
        var proxy=global::System.Reflection.DispatchProxy.Create<SingPlus.Tests.Platform.PlatformDsc1ComputeTests.IOriginalDsc1Runtime,
            SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy>();
        var spy=(SingPlus.Tests.Platform.PlatformDsc1ComputeTests.Dsc1NativeSpy)(object)proxy;
        spy.Native=new();spy.LoseCopyReply=true;spy.LoseQuery=true;
        var scenario=CreateComponentScenario(new SingPlus.Platform.HybridCpu.HybridCpuPlatformAuthorityProvider(proxy));
        var budget=scenario.Kernel.QueryComponent(new ComponentIdentity("compute-session-test")).Value!.ProcessBudget;
        ulong Used()=>scenario.Kernel.QueryBudget(budget).Value!.Usage.Single(u=>u.Dimension==ServiceBudgetDimension.OwnedMemoryBytes).Used;
        var client=ComputeServiceClient.Create(new RuntimeSipClientTransport(scenario.Kernel,scenario.Caller,scenario.Session));
        var pending=client.CopyAsync(scenario.Source,scenario.Destination).AsTask();
        Assert.Equal(KernelError.PlatformFaulted,scenario.Host.ProcessNextCopy().Error);
        Assert.Equal(1,spy.Copies);
        Assert.Equal(64UL,Used());
        Assert.False(scenario.Host.AdvancePendingCopy().IsSuccess);
        Assert.False(pending.IsCompleted);
        var original=typeof(RuntimeComputeServiceHost).GetField("_pending",
            global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.GetValue(scenario.Host)!;
        var invocation=(EndpointSessionInvocationHandle)original.GetType().GetProperty("Invocation")!.GetValue(original)!;
        var copy=typeof(object).GetMethod("MemberwiseClone",
            global::System.Reflection.BindingFlags.Instance|global::System.Reflection.BindingFlags.NonPublic)!.Invoke(original,null)!;
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.ConsumeComputeCopyInvocationClosure(invocation,scenario.Service,copy).Error);
        Assert.Equal(KernelError.WrongSessionOwner,
            scenario.Kernel.ConsumeComputeCopyInvocationClosure(invocation,scenario.Caller,original).Error);
        Assert.True(scenario.Kernel.SnapshotSessionInvocationConsequence(invocation).Value.UnclosedPossibleEffect);
        Assert.Equal(64UL,Used());
        spy.LoseQuery=false;
        var settled=scenario.Host.AdvancePendingCopy();
        Assert.Equal(KernelError.PlatformFaulted,settled.Error); // original request failure remains failure
        Assert.Equal(1,spy.Copies);
        Assert.Equal(0UL,Used());
        var responseFailure=await Assert.ThrowsAnyAsync<Exception>(async()=>await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsNotType<TimeoutException>(responseFailure);
        Assert.True(scenario.Source.IsValid);
        Assert.False(scenario.Destination.IsValid);
        Assert.True(scenario.Kernel.CloseSession(scenario.Caller,scenario.Session).IsSuccess);
        var teardown=scenario.Kernel.FaultComponent(new ComponentIdentity("compute-session-test"));
        Assert.True(teardown.IsSuccess,teardown.Message);
        Assert.Equal(0,spy.Native.ActiveOwnedRegionMappingCount);
        Assert.Equal(0,spy.Native.ActiveBindingCount);
        var callerTeardown=scenario.Kernel.TerminateProcess(scenario.Caller);
        Assert.True(callerTeardown.IsSuccess,callerTeardown.Message);
        Assert.False(scenario.Source.IsValid);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HostCreationRefusesSourceLossAfterComponentAuthoritySnapshot(bool drain)
    {
        var scenario = CreateComponentScenario(new HostPlatformAuthorityProvider());
        var identity = new ComponentIdentity("compute-session-test");
        var capability = Assert.Single(scenario.Kernel.QueryComponent(identity).Value!.Capabilities);
        scenario.Kernel.AfterComputeComponentAuthoritySnapshotQualificationHook = () =>
        {
            scenario.Kernel.AfterComputeComponentAuthoritySnapshotQualificationHook = null;
            if (drain) Assert.True(scenario.Kernel.DrainComponent(identity).Value!.Reclaimable);
            else Assert.True(scenario.Kernel.RevokeCapability(capability).IsSuccess);
        };
        var host = RuntimeComputeServiceHost.CreateForComponent(scenario.Kernel, identity, scenario.Session);
        Assert.Equal(KernelError.MissingCapability, host.Error);
        Assert.Null(host.Value);
        var component = scenario.Kernel.QueryComponent(identity).Value!;
        if (drain)
        {
            Assert.Empty(component.Capabilities);
            Assert.Equal(ProcessState.Exited, scenario.Kernel.Processes.ResolveTerminalState(scenario.Service).Value);
            Assert.True(scenario.Kernel.RetireReclaimableComponent(identity, scenario.Service).IsSuccess);
        }
        else Assert.Equal(ComponentLifecycleState.Running, component.State);
    }

    [Fact]
    public async Task ComponentDiscoverySessionTypedClientAndDsc1LifecycleCompose()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        _ = TestFixtures.Create(kernel, 900, 9000);
        var protocol = IComputeServiceProtocol.CreateDefinition();
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var serviceRegistration = new ProvidedServiceManifestV1("compute", contract);
        var internalCompute = new CapabilityRequirementV1(ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute);
        var image = new byte[] { 0x43, 0x50, 0x55 };
        var serviceManifest = new ServiceManifestV1(
            new ComponentIdentity("compute-service"), new ComponentVersion("1"), Digest(image),
            TestFixtures.Manifest(901, 9010, capabilities: [internalCompute]), [serviceRegistration], requiresPlatformDomain: true,
            budgetRequests: [new(ServiceBudgetDimension.OwnedMemoryBytes, 96), new(ServiceBudgetDimension.ExternalOperations, 4), new(ServiceBudgetDimension.IpcMessages, 4), new(ServiceBudgetDimension.IpcBytes, 4096)]);
        var component = kernel.AdmitComponent(new ComponentAdmissionPlan(
            serviceManifest, image,
            [new ComponentCapabilityGrant(new DomainId(9000), internalCompute)],
            [new ComponentProvidedServiceRegistration(serviceRegistration, protocol, IComputeServiceResponseProtocol.Definition, [internalCompute])]));
        Assert.True(component.IsSuccess, component.Message);

        var caller = TestFixtures.Create(kernel, 902, 9020).Handle;
        AdmitCallerBudget(kernel, caller, 9020);
        var callerCompute = kernel.MintCapability(new DomainId(9020), caller, ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute).Value!.CapabilityId;
        var source = kernel.AllocateBuffer<byte>(caller, 32).Value!;
        var destination = kernel.AllocateBuffer<byte>(caller, 32).Value!;
        source.Span.Fill(0x5A);
        var discovered = Assert.Single(kernel.ResolveByContract(contract).Value!);
        var session = kernel.OpenSession(caller, discovered, [callerCompute]).Value;
        var host = RuntimeComputeServiceHost.CreateForComponent(kernel, serviceManifest.Identity, session).Value!;
        var client = ComputeServiceClient.Create(new RuntimeSipClientTransport(kernel, caller, session));

        var pending = client.CopyAsync(source, destination).AsTask();
        Assert.True(host.ProcessNextCopy().IsSuccess);
        var returned = await pending.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.All(returned.Span.ToArray(), value => Assert.Equal((byte)0x5A, value));
        Assert.Equal(1, provider.SubmitDsc1CopyCallCount);
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
    }

    [Fact]
    public void StaleSessionRejectsBeforeBorrowMoveOrProviderSubmission()
    {
        var provider = new HostPlatformAuthorityProvider();
        var scenario = CreateMinimalSession(provider);
        var stale = scenario.Session with { Generation = new EndpointSessionGeneration(scenario.Session.Generation.Value + 1) };
        var client = IComputeServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, stale));
        var oldSource = scenario.Source.Handle;
        var oldDestination = scenario.Destination.Handle;

        var failure = Assert.Throws<InvalidOperationException>(() => client.CopyAsync(scenario.Source, scenario.Destination).AsTask().GetAwaiter().GetResult());
        Assert.Contains(nameof(KernelError.StaleGeneration), failure.Message, StringComparison.Ordinal);
        Assert.Equal(oldSource, scenario.Source.Handle);
        Assert.Equal(oldDestination, scenario.Destination.Handle);
        Assert.True(scenario.Source.IsValid);
        Assert.True(scenario.Destination.IsValid);
        Assert.Equal(0, provider.SubmitDsc1CopyCallCount);
    }

    [Fact]
    public void MissingCallerComputeAuthorityRejectsSessionBeforeOwnershipIngress()
    {
        var provider = new HostPlatformAuthorityProvider();
        var scenario = CreateComponentScenario(provider);
        var otherCaller = TestFixtures.Create(scenario.Kernel, 923, 9230).Handle;
        var source = scenario.Kernel.AllocateBuffer<byte>(otherCaller, 8).Value!;
        var destination = scenario.Kernel.AllocateBuffer<byte>(otherCaller, 8).Value!;

        var denied = scenario.Kernel.OpenSession(otherCaller, scenario.Descriptor);

        Assert.Equal(KernelError.MissingCapability, denied.Error);
        Assert.True(source.IsValid);
        Assert.True(destination.IsValid);
        Assert.Equal(0, provider.SubmitDsc1CopyCallCount);
    }

    [Fact]
    public async Task CancellationBeforeProviderAcceptanceReturnsBorrowAndPublishesNoDestination()
    {
        var provider = new HostPlatformAuthorityProvider();
        var scenario = CreateComponentScenario(provider);
        var client = IComputeServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session));
        var pending = client.CopyAsync(scenario.Source, scenario.Destination).AsTask();
        Assert.False(scenario.Destination.IsValid);

        var cancelled = scenario.Host.CancelNextRequestBeforeAcceptance();

        Assert.True(cancelled.IsSuccess, cancelled.Message);
        Assert.True(scenario.Source.IsValid);
        Assert.False(scenario.Destination.IsValid);
        Assert.Equal(0, provider.SubmitDsc1CopyCallCount);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
    }

    [Fact]
    public async Task ServiceDrainRejectsNewSessionsButCommittedResponseSurvivesProviderTeardown()
    {
        var provider = new HostPlatformAuthorityProvider();
        var scenario = CreateComponentScenario(provider);
        scenario.Source.Span.Fill(0x33);
        var client = IComputeServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session));
        var pending = client.CopyAsync(scenario.Source, scenario.Destination).AsTask();
        Assert.True(scenario.Kernel.SetServiceAvailability(scenario.Descriptor.Service, ServiceAvailability.Draining).IsSuccess);
        Assert.Equal(KernelError.ServiceUnavailable, scenario.Kernel.OpenSession(scenario.Caller, scenario.Descriptor, [scenario.CallerCapability]).Error);
        Assert.True(scenario.Host.ProcessNextCopy().IsSuccess);
        var returned = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Service).IsSuccess);
        Assert.True(returned.IsValid);
        Assert.All(returned.Span.ToArray(), value => Assert.Equal((byte)0x33, value));
    }

    private static (RuntimeKernel Kernel, ProcessHandle Caller, EndpointSessionHandle Session, SingPlus.Sip.OwnedBuffer<byte> Source, SingPlus.Sip.OwnedBuffer<byte> Destination) CreateMinimalSession(HostPlatformAuthorityProvider provider)
    {
        var kernel = new RuntimeKernel(provider);
        var caller = TestFixtures.Create(kernel, 910, 9100).Handle;
        var service = TestFixtures.Create(kernel, 911, 9110).Handle;
        var capability = kernel.MintCapability(new DomainId(9100), caller, ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute).Value!.CapabilityId;
        var protocol = IComputeServiceProtocol.CreateDefinition();
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var descriptor = kernel.RegisterService(service, "compute", contract, protocol, IComputeServiceResponseProtocol.Definition, [new CapabilityRequirementV1(ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute)]).Value!;
        var session = kernel.OpenSession(caller, descriptor, [capability]).Value;
        return (kernel, caller, session, kernel.AllocateBuffer<byte>(caller, 8).Value!, kernel.AllocateBuffer<byte>(caller, 8).Value!);
    }

    private static ComponentScenario CreateComponentScenario(SingPlus.Platform.IPlatformAuthorityProvider provider)
    {
        var kernel = new RuntimeKernel(provider);
        _ = TestFixtures.Create(kernel, 920, 9200);
        var protocol = IComputeServiceProtocol.CreateDefinition();
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var registration = new ProvidedServiceManifestV1("compute", contract);
        var internalCompute = new CapabilityRequirementV1(ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute);
        var image = new byte[] { 9, 2, 0 };
        var manifest = new ServiceManifestV1(new ComponentIdentity("compute-session-test"), new ComponentVersion("1"), Digest(image), TestFixtures.Manifest(921, 9210, capabilities: [internalCompute]), [registration], requiresPlatformDomain: true,
            budgetRequests: [new(ServiceBudgetDimension.OwnedMemoryBytes, provider is SingPlus.Platform.HybridCpu.HybridCpuPlatformAuthorityProvider ? 128UL : 96UL), new(ServiceBudgetDimension.ExternalOperations, 4), new(ServiceBudgetDimension.IpcMessages, 4), new(ServiceBudgetDimension.IpcBytes, 4096)]);
        var admitted = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image, [new ComponentCapabilityGrant(new DomainId(9200), internalCompute)], [new ComponentProvidedServiceRegistration(registration, protocol, IComputeServiceResponseProtocol.Definition, [internalCompute])]));
        Assert.True(admitted.IsSuccess, admitted.Message);
        var caller = TestFixtures.Create(kernel, 922, 9220).Handle;
        AdmitCallerBudget(kernel, caller, 9220);
        var callerCapability = kernel.MintCapability(new DomainId(9220), caller, ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute).Value!.CapabilityId;
        var descriptor = kernel.ResolveByServiceName("compute").Value;
        var session = kernel.OpenSession(caller, descriptor, [callerCapability]).Value;
        var host = RuntimeComputeServiceHost.CreateForComponent(kernel, manifest.Identity, session).Value!;
        return new ComponentScenario(kernel, caller, admitted.Value!.Process, descriptor, session, callerCapability, kernel.AllocateBuffer<byte>(caller, 16).Value!, kernel.AllocateBuffer<byte>(caller, 16).Value!, host);
    }

    private sealed record ComponentScenario(RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service, ServiceEndpointDescriptor Descriptor, EndpointSessionHandle Session, CapabilityId CallerCapability, SingPlus.Sip.OwnedBuffer<byte> Source, SingPlus.Sip.OwnedBuffer<byte> Destination, RuntimeComputeServiceHost Host);

    private static void AdmitCallerBudget(RuntimeKernel kernel, ProcessHandle caller, ulong domain)
    {
        var configure = kernel.MintCapability(new DomainId(domain), caller, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var admitted = kernel.AdmitProcessBudget(caller, configure, caller, "compute-caller",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 64), new(ServiceBudgetDimension.IpcMessages, 4), new(ServiceBudgetDimension.IpcBytes, 4096)]);
        Assert.True(admitted.IsSuccess, admitted.Message);
    }

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
