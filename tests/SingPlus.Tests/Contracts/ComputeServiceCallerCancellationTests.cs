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
    public async Task CallerCancellationBeforeProviderAcceptanceReturnsBorrowWithoutSubmittingDsc1()
    {
        var provider = new HostPlatformAuthorityProvider();
        var scenario = CreateScenario(provider, 3401, 3410, 3402, 3420);
        scenario.Source.Span.Fill(0x41);
        using var cancellation = new CancellationTokenSource();
        var client = CreateClient(scenario, cancellation.Token);
        var pending = client.CopyAsync(scenario.Source, scenario.Destination).AsTask();
        Assert.False(scenario.Destination.IsValid);

        cancellation.Cancel();
        var settled = scenario.Host.ProcessNextCopy();

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
        var provider = new HostPlatformAuthorityProvider(deferDsc1Completion: true);
        var scenario = CreateScenario(provider, 3403, 3430, 3404, 3440);
        scenario.Source.Span.Fill(0x52);
        using var cancellation = new CancellationTokenSource();
        var client = CreateClient(scenario, cancellation.Token);
        var pending = client.CopyAsync(scenario.Source, scenario.Destination).AsTask();

        var accepted = scenario.Host.ProcessNextCopy();
        Assert.False(accepted.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, accepted.Error);
        Assert.True(scenario.Host.HasPendingCopy);
        Assert.Equal(1, provider.SubmitDsc1CopyCallCount);
        Assert.Equal(1, provider.ActiveDsc1SubmissionCount);
        Assert.Throws<InvalidOperationException>(() => _ = scenario.Source.Span[0]);

        cancellation.Cancel();
        var settled = scenario.Host.AdvancePendingCopy();

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
        var transport = new RuntimeSipClientTransport(
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
        var kernel = new RuntimeKernel(provider);
        var requesterDomain = new DomainId(requesterDomainId);
        var serviceDomain = new DomainId(serviceDomainId);
        var requester = TestFixtures.Create(
            kernel,
            requesterId,
            requesterDomain.Value).Handle;
        var service = TestFixtures.Create(
            kernel,
            serviceId,
            serviceDomain.Value).Handle;

        var source = kernel.AllocateBuffer<byte>(requester, 32).Value!;
        var destination = kernel.AllocateBuffer<byte>(requester, 32).Value!;
        var requesterCompute = Mint(
            kernel,
            requester,
            requesterDomain,
            ResourceKind.Compute,
            CapabilityResourceIds.Dsc1Copy,
            CapabilityRights.Execute);
        var serviceCompute = Mint(
            kernel,
            service,
            serviceDomain,
            ResourceKind.Compute,
            CapabilityResourceIds.Dsc1Copy,
            CapabilityRights.Execute);
        var binding = kernel.BindPlatformDomain(service).Value!;
        var protocol = IComputeServiceProtocol.CreateDefinition();
        var contract = new ServiceContractIdentity(
            protocol.ContractName,
            "1",
            protocol.ContractDigest);
        var descriptor = kernel.RegisterService(
            service,
            $"compute-cancel-{serviceId}",
            contract,
            protocol,
            IComputeServiceResponseProtocol.Definition).Value!;
        var session = kernel.OpenSession(
            requester,
            descriptor,
            [requesterCompute],
            capacity: 4);
        Assert.True(session.IsSuccess, session.Message);

        var host = RuntimeComputeServiceHost.CreateForSession(
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
        var minted = kernel.MintCapability(
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
