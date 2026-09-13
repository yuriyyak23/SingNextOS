using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class ProcessTeardownLifecycleTests
{
    [Fact]
    [Trait("Category", "Runtime")]
    public async Task TerminationClosesWaitersAndKillsLocalAuthorityBeforeDeferredPlatformDrainCompletes()
    {
        var provider = new HostPlatformAuthorityProvider(
            deferRegionRevocationCompletion: true);
        var kernel = new RuntimeKernel(provider);
        var (_, requester) = TestFixtures.Create(kernel, 501, 510);
        var (responderProcess, responder) = TestFixtures.Create(kernel, 502, 520);
        var region = kernel.AllocateRegion(responder, 123).Value!;
        var binding = kernel.BindPlatformDomain(responder).Value!;
        var capability = MintRegionCapability(
            kernel,
            responder,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        _ = kernel.MapPlatformOwnedRegion(
            responder,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read).Value!;
        var eventEndpoint = kernel.CreateKernelEventEndpoint(responder).Value!;
        var eventWait = kernel.WaitForKernelEventAsync(responder, eventEndpoint).AsTask();
        var endpoints = CreateResponseChannel(kernel, requester, responder);
        var transport = new RuntimeSipClientTransport(kernel, requester, responder, endpoints.Left);
        var pending = transport.InvokeAsync(1).AsTask();

        var terminate = kernel.TerminateProcess(responder);

        Assert.False(terminate.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, terminate.Error);
        Assert.Equal(ProcessState.Exiting, responderProcess.State);
        Assert.True(provider.LastRegionRevocationOperation.HasValue);

        // This await completes while the provider operation is deliberately still Draining,
        // proving channel/waiter cancellation precedes external closure.
        var cancelled = await pending;
        Assert.Equal(ResponsePublicationStatus.Cancelled, cancelled.Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => eventWait);
        var lateEventWait = await kernel.WaitForKernelEventAsync(responder, eventEndpoint);
        Assert.Equal(KernelError.InvalidTransition, lateEventWait.Error);

        var lifecycle = kernel.QueryProcessTeardown(responder);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformDraining, lifecycle.Value!.Phase);
        Assert.True(lifecycle.Value.ChannelsClosed);
        Assert.True(lifecycle.Value.LocalAuthorizationRevoked);
        Assert.Equal(1, lifecycle.Value.PendingPlatformMappings);
        Assert.False(lifecycle.Value.PlatformDomainClosed);
        Assert.False(lifecycle.Value.LocalReclaimCompleted);

        var localCapability = kernel.ValidateCapability(
            responder,
            capability,
            CapabilityRights.Map | CapabilityRights.Read);
        Assert.False(localCapability.IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, localCapability.Error);

        Assert.Equal(
            KernelError.InvalidTransition,
            kernel.AllocateRegion(responder, 7).Error);
        Assert.Equal(
            KernelError.InvalidTransition,
            kernel.MintCapability(
                new DomainId(520),
                responder,
                ResourceKind.Device,
                "late-device",
                CapabilityRights.Read).Error);
        Assert.Equal(
            KernelError.InvalidTransition,
            kernel.CreateChannel(
                requester,
                responder,
                SimpleProtocol(),
                1).Error);
        Assert.Equal(
            KernelError.InvalidTransition,
            kernel.MapPlatformOwnedRegion(
                responder,
                binding,
                capability,
                region.Handle,
                PlatformMemoryAccess.Read).Error);

        var loanWhileDraining = kernel.Regions.Loan(
            region.Handle,
            new RegionOwner(new DomainId(520), responder.Generation),
            new RegionOwner(new DomainId(510), requester.Generation));
        Assert.False(loanWhileDraining.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, loanWhileDraining.Error);

        var close = provider.CompleteRegionMappingRevocation(
            provider.LastRegionRevocationOperation.Value);
        Assert.True(close.IsSuccess, close.Message);

        var observed = kernel.ObserveProcessTeardown(responder);

        Assert.True(observed.IsSuccess, observed.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformClosed, observed.Value!.Phase);
        Assert.Equal(ProcessState.Exited, observed.Value.TargetTerminalState);
        Assert.True(observed.Value.PlatformDomainClosed);
        Assert.True(observed.Value.LocalReclaimCompleted);
        Assert.Equal(ProcessState.Exited, responderProcess.State);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(responder).Error);
        Assert.False(kernel.Domains.Contains(new DomainId(520)));
        Assert.Equal(
            RegionState.Released,
            kernel.Regions.Snapshot().Single(r => r.Handle.RegionId == region.Handle.RegionId).State);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void FaultProcessDoesNotPublishFaultedUntilPlatformClosureIsVerified()
    {
        var provider = new HostPlatformAuthorityProvider(
            deferRegionRevocationCompletion: true);
        var kernel = new RuntimeKernel(provider);
        var (process, handle) = TestFixtures.Create(kernel, 511, 530);
        var region = kernel.AllocateRegion(handle, 1).Value!;
        var binding = kernel.BindPlatformDomain(handle).Value!;
        var capability = MintRegionCapability(
            kernel,
            handle,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Write);
        _ = kernel.MapPlatformOwnedRegion(
            handle,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Write).Value!;

        var fault = kernel.FaultProcess(handle);

        Assert.False(fault.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, fault.Error);
        Assert.Equal(ProcessState.Exiting, process.State);
        Assert.True(provider.LastRegionRevocationOperation.HasValue);

        Assert.True(provider.CompleteRegionMappingRevocation(
            provider.LastRegionRevocationOperation.Value).IsSuccess);
        var observed = kernel.ObserveProcessTeardown(handle);

        Assert.True(observed.IsSuccess, observed.Message);
        Assert.Equal(ProcessState.Faulted, observed.Value!.TargetTerminalState);
        Assert.True(observed.Value.LocalReclaimCompleted);
        Assert.Equal(ProcessState.Faulted, process.State);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(handle).Error);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public async Task CommittedResponseSurvivesLaterPlatformTeardownFault()
    {
        var provider = new HostPlatformAuthorityProvider(
            regionRevocationFailure: PlatformAuthorityStatus.Faulted);
        var kernel = new RuntimeKernel(provider);
        var (_, requester) = TestFixtures.Create(kernel, 521, 540);
        var (responderProcess, responder) = TestFixtures.Create(kernel, 522, 550);
        var region = kernel.AllocateRegion(responder, 1).Value!;
        var binding = kernel.BindPlatformDomain(responder).Value!;
        var capability = MintRegionCapability(
            kernel,
            responder,
            region.Handle,
            CapabilityRights.Map | CapabilityRights.Read);
        _ = kernel.MapPlatformOwnedRegion(
            responder,
            binding,
            capability,
            region.Handle,
            PlatformMemoryAccess.Read).Value!;
        var endpoints = CreateResponseChannel(kernel, requester, responder);
        var transport = new RuntimeSipClientTransport(kernel, requester, responder, endpoints.Left);
        var pending = transport.InvokeAsync(1).AsTask();
        var request = kernel.Receive(responder, endpoints.Right);
        Assert.True(request.IsSuccess, request.Message);
        Assert.True(kernel.PublishResponse(
            responder,
            endpoints.Right,
            request.Value!.Sequence,
            77).IsSuccess);

        var terminate = kernel.TerminateProcess(responder);
        var response = await pending;

        Assert.False(terminate.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, terminate.Error);
        Assert.Equal(ResponsePublicationStatus.Published, response.Status);
        Assert.Equal(77, Assert.IsType<int>(response.Payload));
        Assert.Equal(ProcessState.Exiting, responderProcess.State);

        var lifecycle = kernel.QueryProcessTeardown(responder);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, lifecycle.Value!.Phase);
        Assert.Equal(KernelError.PlatformFaulted, lifecycle.Value.BlockingError);
        Assert.False(lifecycle.Value.LocalReclaimCompleted);

        var loan = kernel.Regions.Loan(
            region.Handle,
            new RegionOwner(new DomainId(550), responder.Generation),
            new RegionOwner(new DomainId(540), requester.Generation));
        Assert.False(loan.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, loan.Error);
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ProcessExitClosesExactChannelsButPreservesSharedDomainRegionsUntilFinalMember()
    {
        var kernel = new RuntimeKernel();
        var (leftProcess, left) = TestFixtures.Create(kernel, 531, 560);
        var (_, sibling) = TestFixtures.Create(kernel, 532, 560);
        var (_, peer) = TestFixtures.Create(kernel, 533, 570);
        var leftRegion = kernel.AllocateRegion(left, 1).Value!;
        var siblingRegion = kernel.AllocateRegion(sibling, 2).Value!;
        var siblingCapability = kernel.MintCapability(
            new DomainId(560),
            sibling,
            ResourceKind.Device,
            "sibling-device",
            CapabilityRights.Read).Value!.CapabilityId;
        var leftChannel = kernel.CreateChannel(left, peer, SimpleProtocol(), 1).Value!;
        var siblingChannel = kernel.CreateChannel(sibling, peer, SimpleProtocol(), 1).Value!;

        var terminateLeft = kernel.TerminateProcess(left);

        Assert.True(terminateLeft.IsSuccess, terminateLeft.Message);
        Assert.Equal(ProcessState.Exited, leftProcess.State);
        Assert.True(kernel.Domains.Contains(new DomainId(560)));
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(left).Error);
        Assert.True(kernel.Processes.Resolve(sibling).IsSuccess);
        Assert.True(kernel.ValidateCapability(
            sibling,
            siblingCapability,
            CapabilityRights.Read).IsSuccess);
        Assert.False(kernel.Channels.GetEndpoint(leftChannel.Left).IsSuccess);
        Assert.True(kernel.Channels.GetEndpoint(siblingChannel.Left).IsSuccess);

        var whileSiblingLives = kernel.Regions.Snapshot();
        Assert.Equal(
            RegionState.Owned,
            whileSiblingLives.Single(r => r.Handle.RegionId == leftRegion.Handle.RegionId).State);
        Assert.Equal(
            RegionState.Owned,
            whileSiblingLives.Single(r => r.Handle.RegionId == siblingRegion.Handle.RegionId).State);

        var terminateSibling = kernel.TerminateProcess(sibling);
        Assert.True(terminateSibling.IsSuccess, terminateSibling.Message);
        Assert.False(kernel.Domains.Contains(new DomainId(560)));

        var afterFinalMember = kernel.Regions.Snapshot();
        Assert.Equal(
            RegionState.Released,
            afterFinalMember.Single(r => r.Handle.RegionId == leftRegion.Handle.RegionId).State);
        Assert.Equal(
            RegionState.Released,
            afterFinalMember.Single(r => r.Handle.RegionId == siblingRegion.Handle.RegionId).State);
    }

    private static CapabilityId MintRegionCapability(
        RuntimeKernel kernel,
        ProcessHandle subject,
        RegionHandle region,
        CapabilityRights rights)
    {
        var process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);

        var capability = kernel.MintCapability(
            process.Value!.DomainId,
            subject,
            ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.RegionId),
            rights);
        Assert.True(capability.IsSuccess, capability.Message);
        return capability.Value!.CapabilityId;
    }

    private static (ChannelEndpointHandle Left, ChannelEndpointHandle Right) CreateResponseChannel(
        RuntimeKernel kernel,
        ProcessHandle requester,
        ProcessHandle responder)
    {
        const string contractName = "SingPlus.Tests.IProcessTeardownProtocol";
        var protocol = SimpleProtocol(contractName);
        var responseProtocol = new ResponseProtocolDefinitionV1(
            contractName,
            "process-teardown-response-digest",
            new[]
            {
                new ResponseMessageDescriptorV1(
                    1,
                    "Read",
                    new ResponsePayloadDescriptorV1(
                        ResponsePayloadKind.Primitive,
                        typeof(int).FullName!))
            });

        var channel = kernel.CreateChannel(
            requester,
            responder,
            protocol,
            responseProtocol,
            4);
        Assert.True(channel.IsSuccess, channel.Message);
        return channel.Value;
    }

    private static ProtocolDefinitionV1 SimpleProtocol(
        string contractName = "SingPlus.Tests.IProcessTeardownSimpleProtocol") =>
        new(
            contractName,
            "process-teardown-request-digest",
            "Idle",
            terminalStates: null,
            messages: new[]
            {
                new ProtocolMessageDescriptorV1(1, "Read")
            },
            transitions: new[]
            {
                new ProtocolTransitionV1(1, "Idle", "Idle")
            });
}
