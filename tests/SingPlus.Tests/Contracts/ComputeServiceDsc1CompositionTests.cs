using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Compute;

namespace SingPlus.Tests.Contracts;

public sealed class ComputeServiceDsc1CompositionTests
{
    [Fact]
    [Trait("Category", "Runtime")]
    public async Task TypedCopyComposesThroughHostModelAndReturnsOwnershipOnlyAfterPlatformClosure()
    {
        var provider = new HostPlatformAuthorityProvider();
        var scenario = CreateScenario(provider, 3201, 3210, 3202, 3220, 48, 48);
        for (var index = 0; index < scenario.Source.Length; index++)
            scenario.Source.Span[index] = unchecked((byte)(index * 11 + 7));
        scenario.Destination.Span.Fill(0xC7);
        var expected = scenario.Source.Span.ToArray();
        var oldDestination = scenario.Destination.Handle;

        var pending = CreateClient(scenario)
            .CopyAsync(scenario.Source, scenario.Destination)
            .AsTask();

        Assert.False(pending.IsCompleted);
        Assert.False(scenario.Destination.IsValid);
        Assert.Throws<InvalidOperationException>(() => _ = scenario.Source.Span[0]);

        var processed = scenario.Host.ProcessNextCopy();

        Assert.True(processed.IsSuccess, processed.Message);
        Assert.False(scenario.Host.HasPendingCopy);
        Assert.Equal(1, provider.SubmitDsc1CopyCallCount);
        Assert.Equal(1, provider.ObserveDsc1CompletionCallCount);
        Assert.Equal(0, provider.CancelDsc1CallCount);
        Assert.Equal(0, provider.ActiveDsc1SubmissionCount);

        var returned = await pending;
        Assert.True(returned.IsValid);
        Assert.Equal(oldDestination.RegionId, returned.Handle.RegionId);
        Assert.True(returned.Handle.Generation.Value > oldDestination.Generation.Value);
        Assert.Equal(expected, returned.Span.ToArray());
        Assert.Equal(expected[0], scenario.Source.Span[0]);
        Assert.True(scenario.Kernel.RevokePlatformDomain(
            scenario.Service,
            scenario.Binding).IsSuccess);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public async Task DeferredCompletionKeepsBorrowAndResponsePendingUntilExactObservation()
    {
        var provider = new HostPlatformAuthorityProvider(deferDsc1Completion: true);
        var scenario = CreateScenario(provider, 3203, 3230, 3204, 3240, 32, 32);
        scenario.Source.Span.Fill(0x5A);
        scenario.Destination.Span.Fill(0x19);
        var pending = CreateClient(scenario)
            .CopyAsync(scenario.Source, scenario.Destination)
            .AsTask();

        var first = scenario.Host.ProcessNextCopy();

        Assert.False(first.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, first.Error);
        Assert.True(scenario.Host.HasPendingCopy);
        Assert.False(pending.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => _ = scenario.Source.Span[0]);
        Assert.True(provider.LastDsc1Submission.HasValue);
        Assert.Equal(1, provider.ActiveDsc1SubmissionCount);
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.RevokePlatformDomain(
                scenario.Service,
                scenario.Binding).Error);

        var providerClosed = provider.CompleteDsc1Copy(
            provider.LastDsc1Submission.Value);
        Assert.True(providerClosed.IsSuccess, providerClosed.Message);

        var advanced = scenario.Host.AdvancePendingCopy();

        Assert.True(advanced.IsSuccess, advanced.Message);
        Assert.False(scenario.Host.HasPendingCopy);
        var returned = await pending;
        Assert.All(returned.Span.ToArray(), value => Assert.Equal((byte)0x5A, value));
        Assert.Equal((byte)0x5A, scenario.Source.Span[0]);
        Assert.Equal(0, provider.ActiveDsc1SubmissionCount);
        Assert.True(scenario.Kernel.RevokePlatformDomain(
            scenario.Service,
            scenario.Binding).IsSuccess);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public async Task ExactCancellationClosesPlatformUseBeforeBorrowReturnAndCancelsOwnershipResponse()
    {
        var provider = new HostPlatformAuthorityProvider(deferDsc1Completion: true);
        var scenario = CreateScenario(provider, 3205, 3250, 3206, 3260, 32, 32);
        scenario.Source.Span.Fill(0x2C);
        scenario.Destination.Span.Fill(0xA1);
        var pending = CreateClient(scenario)
            .CopyAsync(scenario.Source, scenario.Destination)
            .AsTask();

        var first = scenario.Host.ProcessNextCopy();
        Assert.False(first.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, first.Error);
        Assert.True(scenario.Host.HasPendingCopy);
        Assert.Throws<InvalidOperationException>(() => _ = scenario.Source.Span[0]);

        var cancelled = scenario.Host.CancelPendingCopy();

        Assert.True(cancelled.IsSuccess, cancelled.Message);
        Assert.False(scenario.Host.HasPendingCopy);
        Assert.Equal(1, provider.CancelDsc1CallCount);
        Assert.Equal(0, provider.ActiveDsc1SubmissionCount);
        Assert.Equal((byte)0x2C, scenario.Source.Span[0]);
        Assert.False(scenario.Destination.IsValid);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        Assert.True(scenario.Kernel.RevokePlatformDomain(
            scenario.Service,
            scenario.Binding).IsSuccess);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public async Task BoundedShapeFailureCancelsResponseWithoutAnyDsc1ProviderCall()
    {
        var provider = new HostPlatformAuthorityProvider();
        var scenario = CreateScenario(provider, 3207, 3270, 3208, 3280, 32, 16);
        scenario.Source.Span.Fill(0x44);
        scenario.Destination.Span.Fill(0xDD);
        var pending = CreateClient(scenario)
            .CopyAsync(scenario.Source, scenario.Destination)
            .AsTask();

        var rejected = scenario.Host.ProcessNextCopy();

        Assert.False(rejected.IsSuccess);
        Assert.Equal(KernelError.PlatformDenied, rejected.Error);
        Assert.False(scenario.Host.HasPendingCopy);
        Assert.Equal(0, provider.SubmitDsc1CopyCallCount);
        Assert.Equal((byte)0x44, scenario.Source.Span[0]);
        Assert.False(scenario.Destination.IsValid);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        Assert.True(scenario.Kernel.RevokePlatformDomain(
            scenario.Service,
            scenario.Binding).IsSuccess);
    }

    private static IComputeService CreateClient(Scenario scenario)
    {
        var transport = new RuntimeSipClientTransport(
            scenario.Kernel,
            scenario.Requester,
            scenario.Service,
            scenario.Endpoints.Left,
            new[] { scenario.RequesterComputeCapability });
        return IComputeServiceRuntimeClient.Create(transport);
    }

    private static Scenario CreateScenario(
        HostPlatformAuthorityProvider provider,
        ulong requesterId,
        ulong requesterDomainId,
        ulong serviceId,
        ulong serviceDomainId,
        int sourceLength,
        int destinationLength)
    {
        var kernel = new RuntimeKernel(provider);
        var requesterDomain = new DomainId(requesterDomainId);
        var serviceDomain = new DomainId(serviceDomainId);
        var (_, requester) = TestFixtures.Create(
            kernel,
            requesterId,
            requesterDomain.Value);
        var (_, service) = TestFixtures.Create(
            kernel,
            serviceId,
            serviceDomain.Value);

        var source = kernel.AllocateBuffer<byte>(requester, sourceLength).Value!;
        var destination = kernel.AllocateBuffer<byte>(requester, destinationLength).Value!;
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
        var channel = kernel.CreateChannel(
            requester,
            service,
            IComputeServiceProtocol.CreateDefinition(),
            IComputeServiceResponseProtocol.Definition,
            capacity: 4);
        Assert.True(channel.IsSuccess, channel.Message);

        var host = RuntimeComputeServiceHost.Create(
            kernel,
            service,
            channel.Value.Right,
            binding,
            new Dsc1ComputeCapability(serviceCompute));
        Assert.True(host.IsSuccess, host.Message);

        return new Scenario(
            kernel,
            requester,
            service,
            source,
            destination,
            requesterCompute,
            binding,
            channel.Value,
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
        CapabilityId RequesterComputeCapability,
        PlatformDomainBinding Binding,
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Endpoints,
        RuntimeComputeServiceHost Host);
}
