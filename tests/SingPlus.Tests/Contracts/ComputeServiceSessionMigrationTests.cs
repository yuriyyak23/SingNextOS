using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip.Compute;

namespace SingPlus.Tests.Contracts;

public sealed class ComputeServiceSessionMigrationTests
{
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
            TestFixtures.Manifest(901, 9010, capabilities: [internalCompute]), [serviceRegistration], requiresPlatformDomain: true);
        var component = kernel.AdmitComponent(new ComponentAdmissionPlan(
            serviceManifest, image,
            [new ComponentCapabilityGrant(new DomainId(9000), internalCompute)],
            [new ComponentProvidedServiceRegistration(serviceRegistration, protocol, IComputeServiceResponseProtocol.Definition, [internalCompute])]));
        Assert.True(component.IsSuccess, component.Message);

        var caller = TestFixtures.Create(kernel, 902, 9020).Handle;
        var callerCompute = kernel.MintCapability(new DomainId(9020), caller, ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute).Value!.CapabilityId;
        var source = kernel.AllocateBuffer<byte>(caller, 32).Value!;
        var destination = kernel.AllocateBuffer<byte>(caller, 32).Value!;
        source.Span.Fill(0x5A);
        var discovered = Assert.Single(kernel.ResolveByContract(contract).Value!);
        var session = kernel.OpenSession(caller, discovered, [callerCompute]).Value;
        var host = RuntimeComputeServiceHost.CreateForComponent(kernel, serviceManifest.Identity, session).Value!;
        var client = IComputeServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));

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

    private static ComponentScenario CreateComponentScenario(HostPlatformAuthorityProvider provider)
    {
        var kernel = new RuntimeKernel(provider);
        _ = TestFixtures.Create(kernel, 920, 9200);
        var protocol = IComputeServiceProtocol.CreateDefinition();
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var registration = new ProvidedServiceManifestV1("compute", contract);
        var internalCompute = new CapabilityRequirementV1(ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute);
        var image = new byte[] { 9, 2, 0 };
        var manifest = new ServiceManifestV1(new ComponentIdentity("compute-session-test"), new ComponentVersion("1"), Digest(image), TestFixtures.Manifest(921, 9210, capabilities: [internalCompute]), [registration], requiresPlatformDomain: true);
        var admitted = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image, [new ComponentCapabilityGrant(new DomainId(9200), internalCompute)], [new ComponentProvidedServiceRegistration(registration, protocol, IComputeServiceResponseProtocol.Definition, [internalCompute])]));
        Assert.True(admitted.IsSuccess, admitted.Message);
        var caller = TestFixtures.Create(kernel, 922, 9220).Handle;
        var callerCapability = kernel.MintCapability(new DomainId(9220), caller, ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute).Value!.CapabilityId;
        var descriptor = kernel.ResolveByServiceName("compute").Value;
        var session = kernel.OpenSession(caller, descriptor, [callerCapability]).Value;
        var host = RuntimeComputeServiceHost.CreateForComponent(kernel, manifest.Identity, session).Value!;
        return new ComponentScenario(kernel, caller, admitted.Value!.Process, descriptor, session, callerCapability, kernel.AllocateBuffer<byte>(caller, 16).Value!, kernel.AllocateBuffer<byte>(caller, 16).Value!, host);
    }

    private sealed record ComponentScenario(RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service, ServiceEndpointDescriptor Descriptor, EndpointSessionHandle Session, CapabilityId CallerCapability, SingPlus.Sip.OwnedBuffer<byte> Source, SingPlus.Sip.OwnedBuffer<byte> Destination, RuntimeComputeServiceHost Host);

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
