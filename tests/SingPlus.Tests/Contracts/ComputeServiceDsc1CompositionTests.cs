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
    public async Task SessionCloseAfterDeliveryRetainsOriginalPairAndRefusesAllProgression()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        ReentryClock clock = new ReentryClock();
        Scenario scenario = CreateScenario(provider, 3255, 3265, 3256, 3266, 8, 8, clock);
        Task<OwnedBuffer<byte>> response = CreateClient(scenario).CopyAsync(scenario.Source, scenario.Destination).AsTask();
        Assert.True(scenario.Kernel.StartTraceSession(scenario.Service, 128).IsSuccess);
        bool closed = false;
        clock.Callback = () =>
        {
            Assert.True(scenario.Kernel.CloseSession(scenario.Requester, scenario.Session).IsSuccess);
            closed = true;
        };

        KernelResult refused = scenario.Host.ProcessNextCopy();
        Assert.True(closed);
        Assert.False(refused.IsSuccess);
        Assert.True(scenario.Host.HasPendingCopy);
        Assert.Equal(refused, scenario.Host.AdvancePendingCopy());
        Assert.Equal(refused, scenario.Host.CancelPendingCopy());
        Assert.Equal(KernelError.InvalidTransition, scenario.Host.ProcessNextCopy().Error);
        Assert.Equal(0, provider.SubmitDsc1CopyCallCount);
        Assert.Equal(0, provider.CancelDsc1CallCount);
        Assert.Equal(RegionState.Loaned, scenario.Kernel.Regions.Snapshot()
            .Single(r => r.Handle.RegionId == scenario.Source.Handle.RegionId).State);
        Assert.Equal(RegionState.Owned, scenario.Kernel.Regions.Snapshot()
            .Single(r => r.Handle.RegionId == scenario.Destination.Handle.RegionId).State);
        Assert.False(scenario.Destination.IsValid);
        Assert.Throws<InvalidOperationException>(() => _ = scenario.Source.Span[0]);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await response.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(scenario.Host.HasPendingCopy);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task MappingCloseRetryUsesOwnerLifecycleAfterChildDrainRefusal(bool source)
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider(deferDsc1Completion: true);
        Scenario scenario = CreateScenario(provider, 3253, 3263, 3254, 3264, 8, 8);
        scenario.Source.Span.Fill(0x4D);
        Task<OwnedBuffer<byte>> response = CreateClient(scenario).CopyAsync(scenario.Source, scenario.Destination).AsTask();
        Assert.Equal(KernelError.PlatformBindingDraining, scenario.Host.ProcessNextCopy().Error);
        object pending = typeof(RuntimeComputeServiceHost).GetField("_pending",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!
            .GetValue(scenario.Host)!;
        PlatformRegionMapping mapping = (PlatformRegionMapping)pending.GetType().GetProperty(
            source ? "StagingMapping" : "DestinationMapping")!.GetValue(pending)!;
        Assert.True(provider.CompleteDsc1Copy(provider.LastDsc1Submission!.Value).IsSuccess);
        Assert.True(scenario.Kernel.PlatformAuthority.BeginMappingChildDrain(
            mapping, scenario.Binding.Subject, out Action? release).IsSuccess);
        try
        {
            Assert.Equal(KernelError.PlatformBindingDraining, scenario.Host.AdvancePendingCopy().Error);
            Assert.Equal(PlatformExternalClosureState.Active,
                scenario.Kernel.QueryPlatformRegionMappingLifecycle(scenario.Service, mapping).Value.PlatformClosure);
            Assert.True(scenario.Host.HasPendingCopy);
            Assert.False(response.IsCompleted);
            Assert.Equal(0, provider.ActiveDsc1SubmissionCount);
            Assert.Throws<InvalidOperationException>(() => _ = scenario.Source.Span[0]);
        }
        finally { release(); }

        Assert.True(scenario.Host.AdvancePendingCopy().IsSuccess);
        OwnedBuffer<byte> returned = await response.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(returned.Span.ToArray(), value => Assert.Equal((byte)0x4D, value));
        Assert.False(scenario.Host.HasPendingCopy);
        Assert.Equal(1, provider.SubmitDsc1CopyCallCount);
        Assert.Equal((byte)0x4D, scenario.Source.Span[0]);
        Assert.True(scenario.Kernel.RevokePlatformDomain(scenario.Service, scenario.Binding).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task TraceReentryCannotProgressOriginalCopyAndLaterRetryStillCloses(int entry)
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider(deferDsc1Completion: true);
        ReentryClock clock = new ReentryClock();
        Scenario scenario = CreateScenario(provider, 3251, 3261, 3252, 3262, 8, 8, clock);
        scenario.Source.Span.Fill(0x3B);
        Assert.True(scenario.Kernel.StartTraceSession(scenario.Service, 128).IsSuccess);
        Task<OwnedBuffer<byte>> pending = CreateClient(scenario).CopyAsync(scenario.Source, scenario.Destination).AsTask();
        bool observed = false;
        clock.Callback = () =>
        {
            Task<KernelResult> worker = Task.Run(() => entry switch
            {
                0 => scenario.Host.ProcessNextCopy(),
                1 => scenario.Host.AdvancePendingCopy(),
                _ => scenario.Host.CancelPendingCopy()
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.InvalidTransition, worker.Result.Error);
            observed = true;
        };

        Assert.Equal(KernelError.PlatformBindingDraining, scenario.Host.ProcessNextCopy().Error);
        Assert.True(observed);
        Assert.True(scenario.Host.HasPendingCopy);
        Assert.Equal(1, provider.SubmitDsc1CopyCallCount);
        Assert.Equal(0, provider.CancelDsc1CallCount);
        Assert.False(pending.IsCompleted);
        Assert.True(provider.CompleteDsc1Copy(provider.LastDsc1Submission!.Value).IsSuccess);
        Assert.True(scenario.Host.AdvancePendingCopy().IsSuccess);
        OwnedBuffer<byte> returned = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(returned.Span.ToArray(), value => Assert.Equal((byte)0x3B, value));
        Assert.False(scenario.Host.HasPendingCopy);
        Assert.Equal(1, provider.SubmitDsc1CopyCallCount);
        Assert.Equal(0, provider.ActiveDsc1SubmissionCount);
        Assert.Equal((byte)0x3B, scenario.Source.Span[0]);
        Assert.True(scenario.Kernel.RevokePlatformDomain(scenario.Service, scenario.Binding).IsSuccess);
    }

    private sealed class ReentryClock : TimeProvider
    {
        public Action? Callback { get; set; }
        public override long GetTimestamp()
        {
            Action? callback = Callback;
            Callback = null;
            callback?.Invoke();
            return 100;
        }
    }

    [Fact]
    [Trait("Category", "Runtime")]
    public async Task TypedCopyComposesThroughHostModelAndReturnsOwnershipOnlyAfterPlatformClosure()
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        Scenario scenario = CreateScenario(provider, 3201, 3210, 3202, 3220, 48, 48);
        for (int index = 0; index < scenario.Source.Length; index++)
            scenario.Source.Span[index] = unchecked((byte)(index * 11 + 7));
        scenario.Destination.Span.Fill(0xC7);
        byte[] expected = scenario.Source.Span.ToArray();
        RegionHandle oldDestination = scenario.Destination.Handle;

        Task<OwnedBuffer<byte>> pending = CreateClient(scenario)
            .CopyAsync(scenario.Source, scenario.Destination)
            .AsTask();

        Assert.False(pending.IsCompleted);
        Assert.False(scenario.Destination.IsValid);
        Assert.Throws<InvalidOperationException>(() => _ = scenario.Source.Span[0]);

        KernelResult processed = scenario.Host.ProcessNextCopy();

        Assert.True(processed.IsSuccess, processed.Message);
        Assert.False(scenario.Host.HasPendingCopy);
        Assert.Equal(1, provider.SubmitDsc1CopyCallCount);
        Assert.Equal(1, provider.ObserveDsc1CompletionCallCount);
        Assert.Equal(0, provider.CancelDsc1CallCount);
        Assert.Equal(0, provider.ActiveDsc1SubmissionCount);

        OwnedBuffer<byte> returned = await pending;
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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider(deferDsc1Completion: true);
        Scenario scenario = CreateScenario(provider, 3203, 3230, 3204, 3240, 32, 32);
        scenario.Source.Span.Fill(0x5A);
        scenario.Destination.Span.Fill(0x19);
        Task<OwnedBuffer<byte>> pending = CreateClient(scenario)
            .CopyAsync(scenario.Source, scenario.Destination)
            .AsTask();

        KernelResult first = scenario.Host.ProcessNextCopy();

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

        PlatformAuthorityResult<PlatformProviderDsc1Completion> providerClosed = provider.CompleteDsc1Copy(
            provider.LastDsc1Submission.Value);
        Assert.True(providerClosed.IsSuccess, providerClosed.Message);

        KernelResult advanced = scenario.Host.AdvancePendingCopy();

        Assert.True(advanced.IsSuccess, advanced.Message);
        Assert.False(scenario.Host.HasPendingCopy);
        OwnedBuffer<byte> returned = await pending;
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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider(deferDsc1Completion: true);
        Scenario scenario = CreateScenario(provider, 3205, 3250, 3206, 3260, 32, 32);
        scenario.Source.Span.Fill(0x2C);
        scenario.Destination.Span.Fill(0xA1);
        Task<OwnedBuffer<byte>> pending = CreateClient(scenario)
            .CopyAsync(scenario.Source, scenario.Destination)
            .AsTask();

        KernelResult first = scenario.Host.ProcessNextCopy();
        Assert.False(first.IsSuccess);
        Assert.Equal(KernelError.PlatformBindingDraining, first.Error);
        Assert.True(scenario.Host.HasPendingCopy);
        Assert.Throws<InvalidOperationException>(() => _ = scenario.Source.Span[0]);

        KernelResult cancelled = scenario.Host.CancelPendingCopy();

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
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider();
        Scenario scenario = CreateScenario(provider, 3207, 3270, 3208, 3280, 32, 16);
        scenario.Source.Span.Fill(0x44);
        scenario.Destination.Span.Fill(0xDD);
        Task<OwnedBuffer<byte>> pending = CreateClient(scenario)
            .CopyAsync(scenario.Source, scenario.Destination)
            .AsTask();

        KernelResult rejected = scenario.Host.ProcessNextCopy();

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
        RuntimeSipClientTransport transport = new RuntimeSipClientTransport(
            scenario.Kernel,
            scenario.Requester,
            scenario.Session);
        return IComputeServiceRuntimeClient.Create(transport);
    }

    private static Scenario CreateScenario(
        HostPlatformAuthorityProvider provider,
        ulong requesterId,
        ulong requesterDomainId,
        ulong serviceId,
        ulong serviceDomainId,
        int sourceLength,
        int destinationLength,
        TimeProvider? clock = null)
    {
        RuntimeKernel kernel = new RuntimeKernel(provider, timeProvider: clock);
        DomainId requesterDomain = new DomainId(requesterDomainId);
        DomainId serviceDomain = new DomainId(serviceDomainId);
        (SingProcess _, ProcessHandle requester) = TestFixtures.Create(
            kernel,
            requesterId,
            requesterDomain.Value);
        (SingProcess _, ProcessHandle service) = TestFixtures.Create(
            kernel,
            serviceId,
            serviceDomain.Value);

        OwnedBuffer<byte> source = kernel.AllocateBuffer<byte>(requester, sourceLength).Value!;
        OwnedBuffer<byte> destination = kernel.AllocateBuffer<byte>(requester, destinationLength).Value!;
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
        ServiceContractIdentity contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, $"compute-{serviceId}", contract, protocol, IComputeServiceResponseProtocol.Definition).Value!;
        KernelResult<EndpointSessionHandle> session = kernel.OpenSession(requester, descriptor, [requesterCompute], capacity: 4);
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
            requesterCompute,
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
        CapabilityId RequesterComputeCapability,
        PlatformDomainBinding Binding,
        EndpointSessionHandle Session,
        RuntimeComputeServiceHost Host);
}
