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
        var scenario = CreateScenario();
        for (var index = 0; index < scenario.Source.Length; index++)
            scenario.Source.Span[index] = unchecked((byte)(index * 7 + 3));
        scenario.Destination.Span.Fill(0xCC);

        var transport = new RuntimeSipClientTransport(
            scenario.Kernel,
            scenario.Requester,
            scenario.Responder,
            scenario.Endpoints.Left,
            new[] { scenario.ComputeCapability });
        var client = IComputeServiceRuntimeClient.Create(transport);

        var pending = client.CopyAsync(scenario.Source, scenario.Destination).AsTask();

        Assert.False(pending.IsCompleted);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Source.Span[0];
        });
        Assert.False(scenario.Destination.IsValid);

        var request = scenario.Kernel.Receive(scenario.Responder, scenario.Endpoints.Right);
        Assert.True(request.IsSuccess, request.Message);
        Assert.Equal(IComputeServiceProtocol.Message_CopyAsync, request.Value!.MessageId);
        var sourceLease = Assert.IsType<BorrowLease<byte>>(request.Value.Payload);
        var serviceDestination = Assert.IsType<OwnedBuffer<byte>>(request.Value.SecondaryPayload);
        Assert.Equal(scenario.Source.Handle.RegionId, sourceLease.Handle.Region.RegionId);
        Assert.Equal(scenario.Source.Length, sourceLease.Length);
        Assert.Equal(scenario.Source.Length, serviceDestination.Length);

        // This is only a local service-model action for the transport test. It does not
        // claim executable HybridCPU DSC1 support.
        sourceLease.Span.CopyTo(serviceDestination.Span);
        var returnedBorrow = scenario.Kernel.ReturnBorrow(scenario.Responder, sourceLease.Handle);
        Assert.True(returnedBorrow.IsSuccess, returnedBorrow.Message);
        Assert.False(sourceLease.IsValid);
        Assert.Equal(unchecked((byte)3), scenario.Source.Span[0]);

        var published = scenario.Kernel.PublishResponse(
            scenario.Responder,
            scenario.Endpoints.Right,
            request.Value.Sequence,
            serviceDestination);
        Assert.True(published.IsSuccess, published.Message);
        Assert.False(serviceDestination.IsValid);

        var returnedDestination = await pending;
        Assert.True(returnedDestination.IsValid);
        Assert.Equal(scenario.Destination.Handle.RegionId, returnedDestination.Handle.RegionId);
        Assert.True(returnedDestination.Handle.Generation.Value > scenario.Destination.Handle.Generation.Value);
        Assert.Equal(scenario.Source.Span.ToArray(), returnedDestination.Span.ToArray());

        var replayedReturn = scenario.Kernel.ReturnBorrow(scenario.Responder, sourceLease.Handle);
        Assert.False(replayedReturn.IsSuccess);
        Assert.Equal(KernelError.InvalidRegionState, replayedReturn.Error);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void CapabilityFailuresHappenBeforeEitherOwnershipAuthorityChanges()
    {
        var scenario = CreateScenario();
        var wrongRights = Mint(
            scenario.Kernel,
            scenario.Requester,
            scenario.RequesterDomain,
            ResourceKind.Compute,
            CapabilityResourceIds.Dsc1Copy,
            CapabilityRights.Read);
        var wrongResource = Mint(
            scenario.Kernel,
            scenario.Requester,
            scenario.RequesterDomain,
            ResourceKind.Compute,
            "compute:not-dsc1",
            CapabilityRights.Execute);

        foreach (var capabilities in new IReadOnlyCollection<CapabilityId>[]
                 {
                     Array.Empty<CapabilityId>(),
                     new[] { new CapabilityId(999_999) },
                     new[] { wrongRights },
                     new[] { wrongResource },
                 })
        {
            var send = scenario.Kernel.SendOwnershipPair(
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
        var scenario = CreateScenario();
        var (_, intruder) = TestFixtures.Create(scenario.Kernel, 3103, 3130);
        var intruderSource = scenario.Kernel.AllocateBuffer<byte>(intruder, scenario.Source.Length).Value!;
        var forgedSource = new OwnedBuffer<byte>(
            new RegionHandle(
                scenario.Source.Handle.RegionId,
                new RegionGeneration(scenario.Source.Handle.Generation.Value + 1)),
            new byte[scenario.Source.Length]);

        var forged = scenario.Kernel.SendOwnershipPair(
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

        var wrongOwner = scenario.Kernel.SendOwnershipPair(
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

        var sameRegion = scenario.Kernel.SendOwnershipPair(
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

        var accepted = scenario.Kernel.SendOwnershipPair(
            scenario.Requester,
            scenario.Responder,
            scenario.Endpoints.Left,
            IComputeServiceProtocol.Message_CopyAsync,
            scenario.Source,
            scenario.Destination,
            new[] { scenario.ComputeCapability });
        Assert.True(accepted.IsSuccess, accepted.Message);

        var replay = scenario.Kernel.SendOwnershipPair(
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
        var scenario = CreateScenario();
        using (scenario.Destination.ReserveForRuntime(RuntimeBufferAccess.Write))
        {
            var send = scenario.Kernel.SendOwnershipPair(
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
        var scenario = CreateScenario();
        var transport = new RuntimeSipClientTransport(
            scenario.Kernel,
            scenario.Requester,
            scenario.Responder,
            scenario.Endpoints.Left,
            new[] { scenario.ComputeCapability });
        var client = IComputeServiceRuntimeClient.Create(transport);
        var pending = client.CopyAsync(scenario.Source, scenario.Destination).AsTask();
        var request = scenario.Kernel.Receive(scenario.Responder, scenario.Endpoints.Right);
        Assert.True(request.IsSuccess, request.Message);
        var sourceLease = Assert.IsType<BorrowLease<byte>>(request.Value!.Payload);
        var serviceDestination = Assert.IsType<OwnedBuffer<byte>>(request.Value.SecondaryPayload);

        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = scenario.Source.Span[0];
        });
        Assert.True(sourceLease.IsValid);
        Assert.True(serviceDestination.IsValid);

        var teardown = scenario.Kernel.TerminateProcess(scenario.Responder);
        Assert.True(teardown.IsSuccess, teardown.Message);

        Assert.False(sourceLease.IsValid);
        scenario.Source.Span[0] = 0x66;
        Assert.False(serviceDestination.IsValid);
        Assert.False(scenario.Destination.IsValid);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
    }

    private static Scenario CreateScenario()
    {
        var kernel = new RuntimeKernel();
        var requesterDomain = new DomainId(3110);
        var responderDomain = new DomainId(3120);
        var (_, requester) = TestFixtures.Create(kernel, 3101, requesterDomain.Value);
        var (_, responder) = TestFixtures.Create(kernel, 3102, responderDomain.Value);
        var source = kernel.AllocateBuffer<byte>(requester, 32).Value!;
        var destination = kernel.AllocateBuffer<byte>(requester, 32).Value!;
        var computeCapability = Mint(
            kernel,
            requester,
            requesterDomain,
            ResourceKind.Compute,
            CapabilityResourceIds.Dsc1Copy,
            CapabilityRights.Execute);
        var channel = kernel.CreateChannel(
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
        var minted = kernel.MintCapability(issuerDomain, subject, kind, resourceId, rights);
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
