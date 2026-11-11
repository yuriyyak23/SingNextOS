using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Compute;

namespace SingPlus.Tests.Contracts;

public sealed class ComputeServiceCallerCancellationTests
{
    [Fact]
    public async Task PreSubmitCancellationRetainsExactCleanupAfterDestinationReleaseCallbackFault()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        Scenario scenario = CreateScenario(provider, 3405, 3450, 3406, 3460);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        Task<OwnedBuffer<byte>> response = CreateClient(scenario, cancellation.Token)
            .CopyAsync(scenario.Source, scenario.Destination).AsTask();
        cancellation.Cancel();
        scenario.Kernel.BeforeRegionReleaseQualificationHook = () =>
        {
            scenario.Kernel.BeforeRegionReleaseQualificationHook = null;
            throw new InvalidOperationException("destination release admission callback");
        };

        Assert.Throws<InvalidOperationException>(() => scenario.Host.ProcessNextCopy());
        Assert.True(scenario.Host.HasPendingCopy);
        Assert.Equal(0, provider.SubmitDsc1CopyCallCount);
        Assert.Equal(0, provider.CancelDsc1CallCount);
        Assert.Equal(RegionState.Owned, scenario.Kernel.Regions.Snapshot()
            .Single(r => r.Handle.RegionId == scenario.Destination.Handle.RegionId).State);
        Assert.Equal(0, scenario.Source.Span[0]);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await response);

        Assert.True(scenario.Host.AdvancePendingCopy().IsSuccess);
        Assert.False(scenario.Host.HasPendingCopy);
        Assert.Equal(RegionState.Released, scenario.Kernel.Regions.Snapshot()
            .Single(r => r.Handle.RegionId == scenario.Destination.Handle.RegionId).State);
        Assert.Equal(0, provider.SubmitDsc1CopyCallCount);
        Assert.Equal(0, provider.CancelDsc1CallCount);
        Assert.True(scenario.Kernel.RevokePlatformDomain(scenario.Service, scenario.Binding).IsSuccess);
    }

    [Fact]
    public async Task CallerCancellationBeforeProviderAcceptanceReturnsBorrowWithoutSubmittingDsc1()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        Scenario scenario = CreateScenario(provider, 3401, 3410, 3402, 3420);
        scenario.Source.Span.Fill(0x41);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        IComputeService client = CreateClient(scenario, cancellation.Token);
        Task<OwnedBuffer<byte>> pending = client.CopyAsync(scenario.Source, scenario.Destination).AsTask();
        Assert.False(scenario.Destination.IsValid);

        cancellation.Cancel();
        KernelResult settled = scenario.Host.ProcessNextCopy();

        Assert.True(settled.IsSuccess, settled.Message);
        Assert.Equal(0, provider.SubmitDsc1CopyCallCount);
        Assert.Equal((byte)0x41, scenario.Source.Span[0]);
        Assert.False(scenario.Destination.IsValid);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        Assert.True(scenario.Kernel.RevokePlatformDomain(
            scenario.Service,
            scenario.Binding).IsSuccess);
    }

    [Fact]
    public async Task CallerCancellationAfterProviderAcceptanceDrainsExactDsc1BeforeCancelledResponse()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider(deferDsc1Completion: true);
        Scenario scenario = CreateScenario(provider, 3403, 3430, 3404, 3440);
        scenario.Source.Span.Fill(0x52);
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        IComputeService client = CreateClient(scenario, cancellation.Token);
        Task<OwnedBuffer<byte>> pending = client.CopyAsync(scenario.Source, scenario.Destination).AsTask();

        KernelResult accepted = scenario.Host.ProcessNextCopy();
        Assert.False(accepted.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, accepted.Error);
        Assert.True(scenario.Host.HasPendingCopy);
        Assert.Equal(1, provider.SubmitDsc1CopyCallCount);
        Assert.Equal(1, provider.ActiveDsc1SubmissionCount);
        Assert.Throws<InvalidOperationException>(() => _ = scenario.Source.Span[0]);

        cancellation.Cancel();
        KernelResult settled = scenario.Host.AdvancePendingCopy();

        Assert.True(settled.IsSuccess, settled.Message);
        Assert.False(scenario.Host.HasPendingCopy);
        Assert.Equal(1, provider.CancelDsc1CallCount);
        Assert.Equal(0, provider.ActiveDsc1SubmissionCount);
        Assert.Equal((byte)0x52, scenario.Source.Span[0]);
        Assert.False(scenario.Destination.IsValid);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        Assert.True(scenario.Kernel.RevokePlatformDomain(
            scenario.Service,
            scenario.Binding).IsSuccess);
    }

    private static IComputeService CreateClient(
        Scenario scenario,
        CancellationToken cancellationToken)
    {
        RuntimeSipClientTransport transport = new RuntimeSipClientTransport(
            scenario.Kernel,
            scenario.Requester,
            scenario.Session,
            cancellationToken);
        return IComputeServiceRuntimeClient.Create(transport);
    }

    private static Scenario CreateScenario(
        HostPlatformAuthorityProvider provider,
        ulong requesterId,
        ulong requesterDomainId,
        ulong serviceId,
        ulong serviceDomainId)
    {
        RuntimeKernel kernel = new RuntimeKernel(provider);
        DomainId requesterDomain = new DomainId(requesterDomainId);
        DomainId serviceDomain = new DomainId(serviceDomainId);
        ProcessHandle requester = TestFixtures.Create(
            kernel,
            requesterId,
            requesterDomain.Value).Handle;
        ProcessHandle service = TestFixtures.Create(
            kernel,
            serviceId,
            serviceDomain.Value).Handle;

        OwnedBuffer<byte> source = kernel.AllocateBuffer<byte>(requester, 32).Value!;
        OwnedBuffer<byte> destination = kernel.AllocateBuffer<byte>(requester, 32).Value!;
        CapabilityId requesterCompute = Mint(
            kernel,
            requester,
            requesterDomain,
            ResourceKind.Compute,
            CapabilityResourceIds.Dsc1Copy,
            CapabilityRights.Execute);
        CapabilityId serviceCompute = Mint(
            kernel,
            service,
            serviceDomain,
            ResourceKind.Compute,
            CapabilityResourceIds.Dsc1Copy,
            CapabilityRights.Execute);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(service).Value!;
        ProtocolDefinitionV1 protocol = IComputeServiceProtocol.CreateDefinition();
        ServiceContractIdentity contract = new ServiceContractIdentity(
            protocol.ContractName,
            "1",
            protocol.ContractDigest);
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(
            service,
            $"compute-cancel-{serviceId}",
            contract,
            protocol,
            IComputeServiceResponseProtocol.Definition).Value!;
        KernelResult<EndpointSessionHandle> session = kernel.OpenSession(
            requester,
            descriptor,
            [requesterCompute],
            capacity: 4);
        Assert.True(session.IsSuccess, session.Message);

        KernelResult<RuntimeComputeServiceHost> host = RuntimeComputeServiceHost.CreateForSession(
            kernel,
            service,
            session.Value,
            binding,
            new Dsc1ComputeCapability(serviceCompute));
        Assert.True(host.IsSuccess, host.Message);

        return new Scenario(
            kernel,
            requester,
            service,
            source,
            destination,
            binding,
            session.Value,
            host.Value!);
    }

    private static CapabilityId Mint(
        RuntimeKernel kernel,
        ProcessHandle subject,
        DomainId issuerDomain,
        ResourceKind kind,
        string resourceId,
        CapabilityRights rights)
    {
        KernelResult<CapabilityDescriptorV1> minted = kernel.MintCapability(
            issuerDomain,
            subject,
            kind,
            resourceId,
            rights);
        Assert.True(minted.IsSuccess, minted.Message);
        return minted.Value!.CapabilityId;
    }

    private sealed record Scenario(
        RuntimeKernel Kernel,
        ProcessHandle Requester,
        ProcessHandle Service,
        OwnedBuffer<byte> Source,
        OwnedBuffer<byte> Destination,
        PlatformDomainBinding Binding,
        EndpointSessionHandle Session,
        RuntimeComputeServiceHost Host);
}
