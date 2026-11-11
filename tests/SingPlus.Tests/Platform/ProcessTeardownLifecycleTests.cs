using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Platform;

public sealed class ProcessTeardownLifecycleTests
{
    [Fact]
    public void ObserveCannotReclaimWhileComposedPreparationCallbackIsInFlight()
    {
        RuntimeKernel kernel = new RuntimeKernel(new HostPlatformAuthorityProvider());
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 509, 590);
        int calls = 0;
        KernelResult<ProcessTeardownSnapshot> observation = default;
        KernelResult overlapping = default;
        bool resolvedDuringCallback = false;
        kernel.RegisterComposedWorkTeardownParticipant(new CallbackParticipant(() =>
        {
            calls++;
            observation = kernel.ObserveProcessTeardown(owner);
            resolvedDuringCallback = kernel.Processes.Resolve(owner).IsSuccess;
            overlapping = kernel.TerminateProcess(owner);
            return KernelResult.Ok();
        }));
        KernelResult terminated = kernel.TerminateProcess(owner);
        Assert.True(observation!.IsSuccess, observation.Message);
        Assert.False(observation.Value!.LocalReclaimCompleted);
        Assert.True(resolvedDuringCallback);
        Assert.Equal(KernelError.PlatformBindingDraining, overlapping!.Error);
        Assert.Equal(1, calls);
        Assert.True(terminated.IsSuccess, terminated.Message);
        Assert.False(kernel.Processes.Resolve(owner).IsSuccess);
    }
    [Fact]
    public void ThrownComposedPreparationDoesNotReclaimAndCanRetryExistingParticipant()
    {
        RuntimeKernel kernel = new RuntimeKernel(new HostPlatformAuthorityProvider());
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 510, 591);
        int calls = 0;
        kernel.RegisterComposedWorkTeardownParticipant(new CallbackParticipant(() =>
        {
            if (++calls == 1) throw new InvalidOperationException("Closure receipt unavailable.");
            return KernelResult.Ok();
        }));
        Assert.Equal(KernelError.PlatformFaulted, kernel.TerminateProcess(owner).Error);
        KernelResult<ProcessTeardownSnapshot> observed = kernel.ObserveProcessTeardown(owner);
        Assert.True(observed.IsSuccess);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, observed.Value!.Phase);
        Assert.False(observed.Value.LocalReclaimCompleted);
        Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
        Assert.Equal(1, calls);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(2, calls);
        Assert.False(kernel.Processes.Resolve(owner).IsSuccess);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentPreparationFailureKeepsProcessUntilExactParticipantRetry(bool throws)
    {
        RuntimeKernel kernel = new RuntimeKernel(new HostPlatformAuthorityProvider());
        (SingProcess _, ProcessHandle owner) = TestFixtures.Create(kernel, 511, 592);
        using ManualResetEventSlim resume = new ManualResetEventSlim();
        TaskCompletionSource entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        kernel.RegisterComposedWorkTeardownParticipant(new CallbackParticipant(() =>
        {
            if (Interlocked.Increment(ref calls) != 1) return KernelResult.Ok();
            entered.SetResult();
            if (!resume.Wait(TimeSpan.FromSeconds(10)))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Test rendezvous expired.");
            if (throws) throw new InvalidOperationException("Closure response lost.");
            return KernelResult.Fail(KernelError.PlatformBindingDraining, "Existing owner still draining.");
        }));
        Task<KernelResult> preparing = Task.Run(() => kernel.TerminateProcess(owner));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(KernelError.PlatformBindingDraining, kernel.TerminateProcess(owner).Error);
            Assert.Equal(KernelError.InvalidTransition, kernel.FaultProcess(owner).Error);
            KernelResult<ProcessTeardownSnapshot> observed = kernel.ObserveProcessTeardown(owner);
            Assert.True(observed.IsSuccess);
            Assert.False(observed.Value!.LocalReclaimCompleted);
            Assert.False(kernel.QueryProcessTeardown(owner).Value!.LocalReclaimCompleted);
            Assert.True(kernel.Processes.Resolve(owner).IsSuccess);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            resume.Set();
            await preparing.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(throws ? KernelError.PlatformFaulted : KernelError.PlatformBindingDraining,
            (await preparing).Error);
        Assert.False(kernel.ObserveProcessTeardown(owner).Value!.LocalReclaimCompleted);
        Assert.Equal(1, calls);
        Assert.True(kernel.TerminateProcess(owner).IsSuccess);
        Assert.Equal(2, calls);
        Assert.False(kernel.Processes.Resolve(owner).IsSuccess);
    }
    private sealed class CallbackParticipant(Func<KernelResult> close) : IComposedWorkTeardownParticipant
    {
        public KernelResult CloseComposedWorkForProcess(ProcessHandle process, RegionOwner owner) => close();
    }
    [Fact]
    [Trait("Category", "Runtime")]
    public async Task TerminationClosesWaitersAndKillsLocalAuthorityBeforeDeferredPlatformDrainCompletes()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider(
            deferRegionRevocationCompletion: true);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle requester) = TestFixtures.Create(kernel, 501, 510);
        (SingProcess? responderProcess, ProcessHandle responder) = TestFixtures.Create(kernel, 502, 520);
        OwnedRegion<int> region = kernel.AllocateRegion(responder, 123).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(responder).Value!;
        CapabilityId capability = MintRegionCapability(
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
        KernelEventEndpoint eventEndpoint = kernel.CreateKernelEventEndpoint(responder).Value!;
        Task<KernelResult<KernelEvent>> eventWait = kernel.WaitForKernelEventAsync(responder, eventEndpoint).AsTask();
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints = CreateResponseChannel(kernel, requester, responder);
        RuntimeSipClientTransport transport = new RuntimeSipClientTransport(kernel, requester, responder, endpoints.Left);
        Task<ResponseEnvelope> pending = transport.InvokeAsync(1).AsTask();

        KernelResult terminate = kernel.TerminateProcess(responder);

        Assert.False(terminate.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, terminate.Error);
        Assert.Equal(ProcessState.Exiting, responderProcess.State);
        Assert.True(provider.LastRegionRevocationOperation.HasValue);

        // This await completes while the provider operation is deliberately still Draining,
        // proving channel/waiter cancellation precedes external closure.
        ResponseEnvelope cancelled = await pending;
        Assert.Equal(ResponsePublicationStatus.Cancelled, cancelled.Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => eventWait);
        KernelResult<KernelEvent> lateEventWait = await kernel.WaitForKernelEventAsync(responder, eventEndpoint);
        Assert.Equal(KernelError.InvalidTransition, lateEventWait.Error);

        KernelResult<ProcessTeardownSnapshot> lifecycle = kernel.QueryProcessTeardown(responder);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformDraining, lifecycle.Value!.Phase);
        Assert.True(lifecycle.Value.ChannelsClosed);
        Assert.True(lifecycle.Value.LocalAuthorizationRevoked);
        Assert.Equal(1, lifecycle.Value.PendingPlatformMappings);
        Assert.False(lifecycle.Value.PlatformDomainClosed);
        Assert.False(lifecycle.Value.LocalReclaimCompleted);

        KernelResult<CapabilityDescriptorV1> localCapability = kernel.ValidateCapability(
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

        KernelResult<BorrowLeaseHandle> loanWhileDraining = kernel.Regions.Loan(
            region.Handle,
            new RegionOwner(new DomainId(520), responder.Generation),
            new RegionOwner(new DomainId(510), requester.Generation));
        Assert.False(loanWhileDraining.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, loanWhileDraining.Error);

        PlatformAuthorityResult<PlatformCompletionReceipt> close = provider.CompleteRegionMappingRevocation(
            provider.LastRegionRevocationOperation.Value);
        Assert.True(close.IsSuccess, close.Message);

        KernelResult<ProcessTeardownSnapshot> observed = kernel.ObserveProcessTeardown(responder);

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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider(
            deferRegionRevocationCompletion: true);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess? process, ProcessHandle handle) = TestFixtures.Create(kernel, 511, 530);
        OwnedRegion<int> region = kernel.AllocateRegion(handle, 1).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(handle).Value!;
        CapabilityId capability = MintRegionCapability(
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

        KernelResult fault = kernel.FaultProcess(handle);

        Assert.False(fault.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, fault.Error);
        Assert.Equal(ProcessState.Exiting, process.State);
        Assert.True(provider.LastRegionRevocationOperation.HasValue);

        Assert.True(provider.CompleteRegionMappingRevocation(
            provider.LastRegionRevocationOperation.Value).IsSuccess);
        KernelResult<ProcessTeardownSnapshot> observed = kernel.ObserveProcessTeardown(handle);

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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider(
            regionRevocationFailure: PlatformAuthorityStatus.Faulted);
        RuntimeKernel kernel = new RuntimeKernel(provider);
        (SingProcess _, ProcessHandle requester) = TestFixtures.Create(kernel, 521, 540);
        (SingProcess? responderProcess, ProcessHandle responder) = TestFixtures.Create(kernel, 522, 550);
        OwnedRegion<int> region = kernel.AllocateRegion(responder, 1).Value!;
        PlatformDomainBinding binding = kernel.BindPlatformDomain(responder).Value!;
        CapabilityId capability = MintRegionCapability(
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
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints = CreateResponseChannel(kernel, requester, responder);
        RuntimeSipClientTransport transport = new RuntimeSipClientTransport(kernel, requester, responder, endpoints.Left);
        Task<ResponseEnvelope> pending = transport.InvokeAsync(1).AsTask();
        KernelResult<ChannelEnvelope> request = kernel.Receive(responder, endpoints.Right);
        Assert.True(request.IsSuccess, request.Message);
        Assert.True(kernel.PublishResponse(
            responder,
            endpoints.Right,
            request.Value!.Sequence,
            77).IsSuccess);

        KernelResult terminate = kernel.TerminateProcess(responder);
        ResponseEnvelope response = await pending;

        Assert.False(terminate.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, terminate.Error);
        Assert.Equal(ResponsePublicationStatus.Published, response.Status);
        Assert.Equal(77, Assert.IsType<int>(response.Payload));
        Assert.Equal(ProcessState.Exiting, responderProcess.State);

        KernelResult<ProcessTeardownSnapshot> lifecycle = kernel.QueryProcessTeardown(responder);
        Assert.True(lifecycle.IsSuccess, lifecycle.Message);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, lifecycle.Value!.Phase);
        Assert.Equal(KernelError.PlatformFaulted, lifecycle.Value.BlockingError);
        Assert.False(lifecycle.Value.LocalReclaimCompleted);

        KernelResult<BorrowLeaseHandle> loan = kernel.Regions.Loan(
            region.Handle,
            new RegionOwner(new DomainId(550), responder.Generation),
            new RegionOwner(new DomainId(540), requester.Generation));
        Assert.False(loan.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, loan.Error);
    }

    [Fact]
    public async Task PublishedResponseConsumptionUsesOwnerGateAcrossChannelTeardown()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle requester) = TestFixtures.Create(kernel, 523, 541);
        (SingProcess _, ProcessHandle responder) = TestFixtures.Create(kernel, 524, 551);
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) endpoints = CreateResponseChannel(kernel, requester, responder);
        RuntimeSipClientTransport transport = new RuntimeSipClientTransport(kernel, requester, responder, endpoints.Left);
        Task<ResponseEnvelope> pending = transport.InvokeAsync(1).AsTask();
        KernelResult<ChannelEnvelope> request = kernel.Receive(responder, endpoints.Right);
        Assert.True(request.IsSuccess, request.Message);
        object gate = typeof(RuntimeKernel).GetField("_requestResponseCorrelationGate",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel)!;

        lock (gate)
        {
            Assert.True(kernel.PublishResponse(responder, endpoints.Right, request.Value!.Sequence, 78).IsSuccess);
            Assert.False(pending.Wait(TimeSpan.FromMilliseconds(200)),
                "The waiter must not consume its completion outside the existing owner gate.");
            Assert.True(kernel.TerminateProcess(responder).IsSuccess);
            Assert.False(kernel.Channels.GetEndpoint(endpoints.Left).IsSuccess);
        }

        ResponseEnvelope response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ResponsePublicationStatus.Published, response.Status);
        Assert.Equal(78, Assert.IsType<int>(response.Payload));
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public void ProcessExitClosesExactChannelsButPreservesSharedDomainRegionsUntilFinalMember()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess? leftProcess, ProcessHandle left) = TestFixtures.Create(kernel, 531, 560);
        (SingProcess _, ProcessHandle sibling) = TestFixtures.Create(kernel, 532, 560);
        (SingProcess _, ProcessHandle peer) = TestFixtures.Create(kernel, 533, 570);
        OwnedRegion<int> leftRegion = kernel.AllocateRegion(left, 1).Value!;
        OwnedRegion<int> siblingRegion = kernel.AllocateRegion(sibling, 2).Value!;
        CapabilityId siblingCapability = kernel.MintCapability(
            new DomainId(560),
            sibling,
            ResourceKind.Device,
            "sibling-device",
            CapabilityRights.Read).Value!.CapabilityId;
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) leftChannel = kernel.CreateChannel(left, peer, SimpleProtocol(), 1).Value!;
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) siblingChannel = kernel.CreateChannel(sibling, peer, SimpleProtocol(), 1).Value!;

        KernelResult terminateLeft = kernel.TerminateProcess(left);

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

        IReadOnlyList<RegionDescriptor> whileSiblingLives = kernel.Regions.Snapshot();
        Assert.Equal(
            RegionState.Owned,
            whileSiblingLives.Single(r => r.Handle.RegionId == leftRegion.Handle.RegionId).State);
        Assert.Equal(
            RegionState.Owned,
            whileSiblingLives.Single(r => r.Handle.RegionId == siblingRegion.Handle.RegionId).State);

        KernelResult terminateSibling = kernel.TerminateProcess(sibling);
        Assert.True(terminateSibling.IsSuccess, terminateSibling.Message);
        Assert.False(kernel.Domains.Contains(new DomainId(560)));

        IReadOnlyList<RegionDescriptor> afterFinalMember = kernel.Regions.Snapshot();
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
        KernelResult<SingProcess> process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);

        KernelResult<CapabilityDescriptorV1> capability = kernel.MintCapability(
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
        ProtocolDefinitionV1 protocol = SimpleProtocol(contractName);
        ResponseProtocolDefinitionV1 responseProtocol = new ResponseProtocolDefinitionV1(
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

        KernelResult<(ChannelEndpointHandle Left, ChannelEndpointHandle Right)> channel = kernel.CreateChannel(
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
