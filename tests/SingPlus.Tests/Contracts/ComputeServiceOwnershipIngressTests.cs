using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Compute;

namespace SingPlus.Tests.Contracts;

public sealed class ComputeServiceOwnershipIngressTests
{
    [Fact]
    [Trait("Category", "Runtime")]
    public async Task TypedIngressCarriesReadBorrowAndExclusiveDestinationThenReturnsOwnership()
    {
        Scenario scenario = CreateScenario();
        for (int index = 0; index < scenario.Source.Length; index++)
            scenario.Source.Span[index] = unchecked((byte)(index * 7 + 3));
        scenario.Destination.Span.Fill(0xCC);

        RuntimeSipClientTransport transport = new RuntimeSipClientTransport(
            scenario.Kernel,
            scenario.Requester,
            scenario.Responder,
            scenario.Endpoints.Left,
            new[] { scenario.ComputeCapability });
        IComputeService client = IComputeServiceRuntimeClient.Create(transport);

        Task<OwnedBuffer<byte>> pending = client.CopyAsync(scenario.Source, scenario.Destination).AsTask();

        Assert.False(pending.IsCompleted);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Source.Span[0];
        });
        Assert.False(scenario.Destination.IsValid);

        KernelResult<ChannelEnvelope> request = scenario.Kernel.Receive(scenario.Responder, scenario.Endpoints.Right);
        Assert.True(request.IsSuccess, request.Message);
        Assert.Equal(IComputeServiceProtocol.Message_CopyAsync, request.Value!.MessageId);
        BorrowLease<byte> sourceLease = Assert.IsType<BorrowLease<byte>>(request.Value.Payload);
        OwnedBuffer<byte> serviceDestination = Assert.IsType<OwnedBuffer<byte>>(request.Value.SecondaryPayload);
        Assert.Equal(scenario.Source.Handle.RegionId, sourceLease.Handle.Region.RegionId);
        Assert.Equal(scenario.Source.Length, sourceLease.Length);
        Assert.Equal(scenario.Source.Length, serviceDestination.Length);

        // This is only a local service-model action for the transport test. It does not
        // claim executable HybridCPU DSC1 support.
        sourceLease.Span.CopyTo(serviceDestination.Span);
        KernelResult returnedBorrow = scenario.Kernel.ReturnBorrow(scenario.Responder, sourceLease.Handle);
        Assert.True(returnedBorrow.IsSuccess, returnedBorrow.Message);
        Assert.False(sourceLease.IsValid);
        Assert.Equal(unchecked((byte)3), scenario.Source.Span[0]);

        KernelResult<ResponseEnvelope> published = scenario.Kernel.PublishResponse(
            scenario.Responder,
            scenario.Endpoints.Right,
            request.Value.Sequence,
            serviceDestination);
        Assert.True(published.IsSuccess, published.Message);
        Assert.False(serviceDestination.IsValid);

        OwnedBuffer<byte> returnedDestination = await pending;
        Assert.True(returnedDestination.IsValid);
        Assert.Equal(scenario.Destination.Handle.RegionId, returnedDestination.Handle.RegionId);
        Assert.True(returnedDestination.Handle.Generation.Value > scenario.Destination.Handle.Generation.Value);
        Assert.Equal(scenario.Source.Span.ToArray(), returnedDestination.Span.ToArray());

        KernelResult replayedReturn = scenario.Kernel.ReturnBorrow(scenario.Responder, sourceLease.Handle);
        Assert.False(replayedReturn.IsSuccess);
        Assert.Equal(KernelError.InvalidRegionState, replayedReturn.Error);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void CapabilityFailuresHappenBeforeEitherOwnershipAuthorityChanges()
    {
        Scenario scenario = CreateScenario();
        CapabilityId wrongRights = Mint(
            scenario.Kernel,
            scenario.Requester,
            scenario.RequesterDomain,
            ResourceKind.Compute,
            CapabilityResourceIds.Dsc1Copy,
            CapabilityRights.Read);
        CapabilityId wrongResource = Mint(
            scenario.Kernel,
            scenario.Requester,
            scenario.RequesterDomain,
            ResourceKind.Compute,
            "compute:not-dsc1",
            CapabilityRights.Execute);

        foreach (IReadOnlyCollection<CapabilityId> capabilities in new IReadOnlyCollection<CapabilityId>[]
                 {
                     Array.Empty<CapabilityId>(),
                     new[] { new CapabilityId(999_999) },
                     new[] { wrongRights },
                     new[] { wrongResource },
                 })
        {
            KernelResult<ChannelEnvelope> send = scenario.Kernel.SendOwnershipPair(
                scenario.Requester,
                scenario.Responder,
                scenario.Endpoints.Left,
                IComputeServiceProtocol.Message_CopyAsync,
                scenario.Source,
                scenario.Destination,
                capabilities);

            Assert.False(send.IsSuccess);
            Assert.Equal(KernelError.MissingCapability, send.Error);
            Assert.True(scenario.Source.IsValid);
            Assert.True(scenario.Destination.IsValid);
            scenario.Source.Span[0] = 0x11;
            scenario.Destination.Span[0] = 0x22;
        }
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ForgedGenerationWrongOwnerSameRegionAndReplayFailClosed()
    {
        Scenario scenario = CreateScenario();
        (SingProcess _, ProcessHandle intruder) = TestFixtures.Create(scenario.Kernel, 3103, 3130);
        OwnedBuffer<byte> intruderSource = scenario.Kernel.AllocateBuffer<byte>(intruder, scenario.Source.Length).Value!;
        OwnedBuffer<byte> forgedSource = new OwnedBuffer<byte>(
            new RegionHandle(
                scenario.Source.Handle.RegionId,
                new RegionGeneration(scenario.Source.Handle.Generation.Value + 1)),
            new byte[scenario.Source.Length]);

        KernelResult<ChannelEnvelope> forged = scenario.Kernel.SendOwnershipPair(
            scenario.Requester,
            scenario.Responder,
            scenario.Endpoints.Left,
            IComputeServiceProtocol.Message_CopyAsync,
            forgedSource,
            scenario.Destination,
            new[] { scenario.ComputeCapability });
        Assert.False(forged.IsSuccess);
        Assert.Equal(KernelError.StaleGeneration, forged.Error);
        Assert.True(scenario.Source.IsValid);
        Assert.True(scenario.Destination.IsValid);

        KernelResult<ChannelEnvelope> wrongOwner = scenario.Kernel.SendOwnershipPair(
            scenario.Requester,
            scenario.Responder,
            scenario.Endpoints.Left,
            IComputeServiceProtocol.Message_CopyAsync,
            intruderSource,
            scenario.Destination,
            new[] { scenario.ComputeCapability });
        Assert.False(wrongOwner.IsSuccess);
        Assert.Equal(KernelError.WrongRegionOwner, wrongOwner.Error);
        Assert.True(intruderSource.IsValid);
        Assert.True(scenario.Destination.IsValid);

        KernelResult<ChannelEnvelope> sameRegion = scenario.Kernel.SendOwnershipPair(
            scenario.Requester,
            scenario.Responder,
            scenario.Endpoints.Left,
            IComputeServiceProtocol.Message_CopyAsync,
            scenario.Source,
            scenario.Source,
            new[] { scenario.ComputeCapability });
        Assert.False(sameRegion.IsSuccess);
        Assert.Equal(KernelError.InvalidRegionState, sameRegion.Error);
        scenario.Source.Span[0] = 0x33;

        KernelResult<ChannelEnvelope> accepted = scenario.Kernel.SendOwnershipPair(
            scenario.Requester,
            scenario.Responder,
            scenario.Endpoints.Left,
            IComputeServiceProtocol.Message_CopyAsync,
            scenario.Source,
            scenario.Destination,
            new[] { scenario.ComputeCapability });
        Assert.True(accepted.IsSuccess, accepted.Message);

        KernelResult<ChannelEnvelope> replay = scenario.Kernel.SendOwnershipPair(
            scenario.Requester,
            scenario.Responder,
            scenario.Endpoints.Left,
            IComputeServiceProtocol.Message_CopyAsync,
            scenario.Source,
            scenario.Destination,
            new[] { scenario.ComputeCapability });
        Assert.False(replay.IsSuccess);
        Assert.Equal(KernelError.InvalidRegionState, replay.Error);
        Assert.False(scenario.Destination.IsValid);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Source.Span[0];
        });
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void DestinationReservationIsRejectedBeforeSourceLoanBegins()
    {
        Scenario scenario = CreateScenario();
        using (scenario.Destination.ReserveForRuntime(RuntimeBufferAccess.Write))
        {
            KernelResult<ChannelEnvelope> send = scenario.Kernel.SendOwnershipPair(
                scenario.Requester,
                scenario.Responder,
                scenario.Endpoints.Left,
                IComputeServiceProtocol.Message_CopyAsync,
                scenario.Source,
                scenario.Destination,
                new[] { scenario.ComputeCapability });

            Assert.False(send.IsSuccess);
            Assert.Equal(KernelError.InvalidRegionState, send.Error);
            Assert.True(scenario.Source.IsValid);
            Assert.True(scenario.Destination.IsValid);
            scenario.Source.Span[0] = 0x44;
            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = scenario.Destination.Span[0];
            });
        }

        scenario.Destination.Span[0] = 0x55;
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public async Task ResponderTeardownReturnsBorrowBeforeReclaimAndCancelsUnpublishedResponse()
    {
        Scenario scenario = CreateScenario();
        RuntimeSipClientTransport transport = new RuntimeSipClientTransport(
            scenario.Kernel,
            scenario.Requester,
            scenario.Responder,
            scenario.Endpoints.Left,
            new[] { scenario.ComputeCapability });
        IComputeService client = IComputeServiceRuntimeClient.Create(transport);
        Task<OwnedBuffer<byte>> pending = client.CopyAsync(scenario.Source, scenario.Destination).AsTask();
        KernelResult<ChannelEnvelope> request = scenario.Kernel.Receive(scenario.Responder, scenario.Endpoints.Right);
        Assert.True(request.IsSuccess, request.Message);
        BorrowLease<byte> sourceLease = Assert.IsType<BorrowLease<byte>>(request.Value!.Payload);
        OwnedBuffer<byte> serviceDestination = Assert.IsType<OwnedBuffer<byte>>(request.Value.SecondaryPayload);

        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Source.Span[0];
        });
        Assert.True(sourceLease.IsValid);
        Assert.True(serviceDestination.IsValid);

        KernelResult teardown = scenario.Kernel.TerminateProcess(scenario.Responder);
        Assert.True(teardown.IsSuccess, teardown.Message);

        Assert.False(sourceLease.IsValid);
        scenario.Source.Span[0] = 0x66;
        Assert.False(serviceDestination.IsValid);
        Assert.False(scenario.Destination.IsValid);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
    }

    private static Scenario CreateScenario()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        DomainId requesterDomain = new DomainId(3110);
        DomainId responderDomain = new DomainId(3120);
        (SingProcess _, ProcessHandle requester) = TestFixtures.Create(kernel, 3101, requesterDomain.Value);
        (SingProcess _, ProcessHandle responder) = TestFixtures.Create(kernel, 3102, responderDomain.Value);
        OwnedBuffer<byte> source = kernel.AllocateBuffer<byte>(requester, 32).Value!;
        OwnedBuffer<byte> destination = kernel.AllocateBuffer<byte>(requester, 32).Value!;
        CapabilityId computeCapability = Mint(
            kernel,
            requester,
            requesterDomain,
            ResourceKind.Compute,
            CapabilityResourceIds.Dsc1Copy,
            CapabilityRights.Execute);
        KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> channel = kernel.CreateChannel(
            requester,
            responder,
            IComputeServiceProtocol.CreateDefinition(),
            IComputeServiceResponseProtocol.Definition,
            capacity: 4);
        Assert.True(channel.IsSuccess, channel.Message);

        return new Scenario(
            kernel,
            requester,
            responder,
            requesterDomain,
            source,
            destination,
            computeCapability,
            channel.Value);
    }

    private static CapabilityId Mint(
        RuntimeKernel kernel,
        ProcessHandle subject,
        DomainId issuerDomain,
        ResourceKind kind,
        string resourceId,
        CapabilityRights rights)
    {
        KernelResult<CapabilityDescriptorV1> minted = kernel.MintCapability(issuerDomain, subject, kind, resourceId, rights);
        Assert.True(minted.IsSuccess, minted.Message);
        return minted.Value!.CapabilityId;
    }

    private sealed record Scenario(
        RuntimeKernel Kernel,
        ProcessHandle Requester,
        ProcessHandle Responder,
        DomainId RequesterDomain,
        OwnedBuffer<byte> Source,
        OwnedBuffer<byte> Destination,
        CapabilityId ComputeCapability,
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Endpoints);
}
