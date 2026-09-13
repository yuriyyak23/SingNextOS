using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;
using SingPlus.Sip.Native;
using SingPlus.Sip.FileSystem;
using SingPlus.Sip.Networking;
using SingPlus.Sip.Process;
using SingPlus.Sip.Virtualization;

namespace SingPlus.Tests.Virtualization;

public sealed class Phase8ResidualVirtualizationTests
{
    [Fact]
    public void NestedContractRejectsAnyImmediateParentAmplification()
    {
        var root = ParentLease();
        var parentIntent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var parent = new PlatformProviderChildDomainLease(new(10), new(23), root, parentIntent);
        var amplified = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.Execution);

        var result = PlatformNestedDomainContract.ValidateRequest(new(parent, amplified));

        Assert.Equal(PlatformAuthorityStatus.Denied, result.Status);
    }

    [Fact]
    public void NestedIdentityAndAuthorityAreValidatedBeforeProviderCallAndEpochsStayDistinct()
    {
        var scenario = CreateScenario();
        var root = CreateConfiguredRoot(scenario);
        var requested = new NestedVirtualDomainRequestProfile(new(1, 4096),
            VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory);

        Assert.Equal(KernelError.CapabilityNotFound,
            scenario.Kernel.CreateNestedVirtualDomain(scenario.Owner, root.Domain, default, requested).Error);
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Kernel.CreateNestedVirtualDomain(scenario.Owner,
                root.Domain with { Generation = new(2) }, root.ConfigureCapability, requested).Error);
        Assert.Equal(0, scenario.Provider.NestedCreateCalls);

        var nested = scenario.Kernel.CreateNestedVirtualDomain(
            scenario.Owner, root.Domain, root.ConfigureCapability, requested);

        Assert.True(nested.IsSuccess, nested.Message);
        Assert.Equal(1, scenario.Provider.NestedCreateCalls);
        Assert.Equal<ulong>(1, nested.Value!.Domain.Generation.Value);
        Assert.Equal<ulong>(47, scenario.Provider.LastNestedLease.Generation.Value);
        Assert.NotEqual(nested.Value.Domain.Generation.Value, scenario.Provider.LastNestedLease.Generation.Value);
        Assert.Equal(0UL, nested.Value.ExecuteCapability.Value);
        Assert.Equal(0UL, nested.Value.EventCapability.Value);
        Assert.Equal(0UL, nested.Value.TrapCapability.Value);
    }

    [Fact]
    public void WrongOwnerAndAmplifiedRuntimeRequestNeverReachNestedProvider()
    {
        var scenario = CreateScenario();
        var root = CreateConfiguredRoot(scenario);
        var other = TestFixtures.Create(scenario.Kernel, 502, 5002).Handle;
        var request = new NestedVirtualDomainRequestProfile(new(1, 4096),
            VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.Io);

        Assert.Equal(KernelError.WrongVirtualDomainOwner,
            scenario.Kernel.CreateNestedVirtualDomain(other, root.Domain, root.ConfigureCapability, request).Error);
        Assert.Equal(KernelError.PlatformDenied,
            scenario.Kernel.CreateNestedVirtualDomain(scenario.Owner, root.Domain,
                root.ConfigureCapability, request).Error);
        Assert.Equal(0, scenario.Provider.NestedCreateCalls);
    }

    [Fact]
    public async Task OrdinaryProcessFileAndNetworkSipRoutesDoNotAllocateNestedAuthority()
    {
        var scenario = CreateScenario();
        var processService = TestFixtures.Create(scenario.Kernel, 504, 5004).Handle;
        var processCapability = scenario.Kernel.MintCapability(new DomainId(5001), scenario.Owner,
            ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Value!.CapabilityId;
        var processProtocol = IProcessServiceProtocol.CreateDefinition();
        var processDescriptor = scenario.Kernel.RegisterService(processService, "ordinary-process",
            new(processProtocol.ContractName, "1", processProtocol.ContractDigest), processProtocol,
            IProcessServiceResponseProtocol.Definition,
            [new(ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute)]).Value!;
        var processSession = scenario.Kernel.OpenSession(scenario.Owner, processDescriptor, [processCapability]).Value;
        var processHost = RuntimeProcessServiceHost.CreateForSession(scenario.Kernel, processService, processSession).Value!;
        var processClient = IProcessServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Owner, processSession));
        var creating = processClient.CreateAsync(new(processCapability, TestFixtures.Manifest(506, 5006, 1, "ordinary-child"), ChildProcessPolicy.Attached)).AsTask();
        Assert.True(processHost.ProcessNext().IsSuccess);
        Assert.NotEqual(default, (await creating).Authority.Process);

        var fileService = TestFixtures.Create(scenario.Kernel, 507, 5007).Handle;
        var fileCapability = scenario.Kernel.MintCapability(new DomainId(5001), scenario.Owner,
            ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var fileProtocol = IFileServiceProtocol.CreateDefinition();
        var fileDescriptor = scenario.Kernel.RegisterService(fileService, "ordinary-file",
            new(fileProtocol.ContractName, "1", fileProtocol.ContractDigest), fileProtocol,
            IFileServiceResponseProtocol.Definition,
            [new(ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write)]).Value!;
        var fileSession = scenario.Kernel.OpenSession(scenario.Owner, fileDescriptor, [fileCapability]).Value;
        var fileHost = RuntimeFileServiceHost.CreateForSession(scenario.Kernel, fileService, fileSession).Value!;
        var fileOpen = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Owner, fileSession)).OpenAsync(new(fileCapability, "/ordinary", true)).AsTask();
        Assert.True(fileHost.ProcessNext().IsSuccess);
        Assert.NotEqual(default, (await fileOpen).Authority.File);

        var networkService = TestFixtures.Create(scenario.Kernel, 508, 5008).Handle;
        var networkCapability = scenario.Kernel.MintCapability(new DomainId(5001), scenario.Owner,
            ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure).Value!.CapabilityId;
        var networkProtocol = INetworkServiceProtocol.CreateDefinition();
        var networkDescriptor = scenario.Kernel.RegisterService(networkService, "ordinary-network",
            new(networkProtocol.ContractName, "1", networkProtocol.ContractDigest), networkProtocol,
            INetworkServiceResponseProtocol.Definition,
            [new(ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure)]).Value!;
        var networkSession = scenario.Kernel.OpenSession(scenario.Owner, networkDescriptor, [networkCapability]).Value;
        var networkHost = RuntimeNetworkServiceHost.CreateForSession(scenario.Kernel, networkService, networkSession).Value!;
        var socket = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Owner, networkSession)).OpenAsync(new(networkCapability, "ordinary:loopback")).AsTask();
        Assert.True(networkHost.ProcessNext().IsSuccess);
        Assert.NotEqual(default, (await socket).Authority.Socket);

        Assert.Equal(0, scenario.Provider.NestedCreateCalls);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.Denied)]
    [InlineData(PlatformAuthorityStatus.Revoked)]
    [InlineData(PlatformAuthorityStatus.Faulted)]
    [InlineData((PlatformAuthorityStatus)999)]
    public void ProviderNestedAdmissionFailuresFailClosed(PlatformAuthorityStatus status)
    {
        var scenario = CreateScenario(status);
        var root = CreateConfiguredRoot(scenario);
        var request = new NestedVirtualDomainRequestProfile(new(1, 4096),
            VirtualDomainAuthorityClass.Lifecycle);

        var nested = scenario.Kernel.CreateNestedVirtualDomain(
            scenario.Owner, root.Domain, root.ConfigureCapability, request);

        Assert.False(nested.IsSuccess);
        Assert.Equal(1, scenario.Provider.NestedCreateCalls);
    }

    [Fact]
    public void MalformedNestedLeaseQuarantinesParentAndCannotBecomeReclaimAuthority()
    {
        var scenario = CreateScenario(malformedNestedLease: true);
        var root = CreateConfiguredRoot(scenario);
        var request = new NestedVirtualDomainRequestProfile(new(1, 4096),
            VirtualDomainAuthorityClass.Lifecycle);

        var nested = scenario.Kernel.CreateNestedVirtualDomain(
            scenario.Owner, root.Domain, root.ConfigureCapability, request);

        Assert.Equal(KernelError.PlatformFaulted, nested.Error);
        Assert.Equal(KernelError.InvalidTransition,
            scenario.Kernel.StartVirtualDomain(scenario.Owner, root.Domain, root.ExecuteCapability).Error);
    }

    [Fact]
    public void WrongImmediateParentNestedReceiptQuarantinesParentImmediately()
    {
        var scenario = CreateScenario(wrongNestedImmediateParent: true);
        var root = CreateConfiguredRoot(scenario);

        var nested = scenario.Kernel.CreateNestedVirtualDomain(scenario.Owner, root.Domain,
            root.ConfigureCapability, new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle));

        Assert.Equal(KernelError.PlatformFaulted, nested.Error);
        Assert.Equal(VirtualDomainState.Quarantined,
            scenario.Kernel.QueryVirtualDomain(scenario.Owner, root.Domain).Value);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.DestroyVirtualDomain(scenario.Owner, root.Domain, root.ConfigureCapability).Error);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.WrongDomain)]
    [InlineData((PlatformAuthorityStatus)999)]
    public void WrongDomainAndUnknownNestedProviderOutcomeQuarantineParentImmediately(PlatformAuthorityStatus status)
    {
        var scenario = CreateScenario(status);
        var root = CreateConfiguredRoot(scenario);

        Assert.False(scenario.Kernel.CreateNestedVirtualDomain(scenario.Owner, root.Domain,
            root.ConfigureCapability, new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).IsSuccess);
        Assert.Equal(VirtualDomainState.Quarantined,
            scenario.Kernel.QueryVirtualDomain(scenario.Owner, root.Domain).Value);
    }

    [Fact]
    public void ParentDestroyClosesNestedMappingThenNestedThenRootBeforeParentReclaim()
    {
        var scenario = CreateScenario();
        var root = CreateConfiguredRoot(scenario);
        var nested = scenario.Kernel.CreateNestedVirtualDomain(scenario.Owner, root.Domain,
            root.ConfigureCapability, new(new(1, 4096),
                VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory)).Value!;
        Assert.True(scenario.Kernel.ConfigureVirtualDomain(scenario.Owner, nested.Domain,
            nested.ConfigureCapability).IsSuccess);
        var region = scenario.Kernel.AllocateRegion(scenario.Owner, 4096).Value!;
        var regionCapability = scenario.Kernel.MintCapability(new DomainId(5001), scenario.Owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        Assert.True(scenario.Kernel.MapGuestRegion(scenario.Owner, nested.Domain, nested.MemoryCapability,
            regionCapability, region.Handle, new(0, 4096), GuestMemoryAccess.Read).IsSuccess);
        scenario.Provider.CallLog.Clear();

        var destroyed = scenario.Kernel.DestroyVirtualDomain(
            scenario.Owner, root.Domain, root.ConfigureCapability);

        Assert.True(destroyed.IsSuccess, destroyed.Message);
        Assert.Equal(new[] { "guest-unmap", "mapping-close", "nested-drain", "nested-close", "root-drain", "root-close", "parent-close" },
            scenario.Provider.CallLog);
        Assert.True(scenario.Kernel.ReleaseRegion(scenario.Owner, region).IsSuccess);
        Assert.Equal(KernelError.VirtualDomainNotFound,
            scenario.Kernel.QueryVirtualDomain(scenario.Owner, nested.Domain).Error);
    }

    [Fact]
    public void StaleNestedMappingIsNeitherUsedNorReclaimed()
    {
        var scenario = CreateScenario();
        var root = CreateConfiguredRoot(scenario);
        var nested = scenario.Kernel.CreateNestedVirtualDomain(scenario.Owner, root.Domain,
            root.ConfigureCapability, new(new(1, 4096),
                VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory)).Value!;
        Assert.True(scenario.Kernel.ConfigureVirtualDomain(scenario.Owner, nested.Domain,
            nested.ConfigureCapability).IsSuccess);
        var region = scenario.Kernel.AllocateRegion(scenario.Owner, 4096).Value!;
        var regionCapability = scenario.Kernel.MintCapability(new DomainId(5001), scenario.Owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapping = scenario.Kernel.MapGuestRegion(scenario.Owner, nested.Domain, nested.MemoryCapability,
            regionCapability, region.Handle, new(0, 4096), GuestMemoryAccess.Read).Value!;
        var stale = mapping.Mapping with { Generation = new(mapping.Mapping.Generation.Value + 1) };

        Assert.Equal(KernelError.StaleGeneration,
            scenario.Kernel.CloseGuestRegionMapping(scenario.Owner, nested.Domain,
                nested.MemoryCapability, stale).Error);
        Assert.Equal(0, scenario.Provider.GuestUnmapCalls);
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.ReleaseRegion(scenario.Owner, region).Error);
    }

    [Fact]
    public void AmbiguousNestedClosePinsParentAndCannotBeRetriedIntoCompletion()
    {
        var scenario = CreateScenario(malformedNestedClose: true);
        var root = CreateConfiguredRoot(scenario);
        Assert.True(scenario.Kernel.CreateNestedVirtualDomain(scenario.Owner, root.Domain,
            root.ConfigureCapability, new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).IsSuccess);

        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.DestroyVirtualDomain(scenario.Owner, root.Domain, root.ConfigureCapability).Error);
        int closes = scenario.Provider.NestedCloseCalls;
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.DestroyVirtualDomain(scenario.Owner, root.Domain, root.ConfigureCapability).Error);
        Assert.Equal(closes, scenario.Provider.NestedCloseCalls);
        Assert.Equal(VirtualDomainState.Draining,
            scenario.Kernel.QueryVirtualDomain(scenario.Owner, root.Domain).Value);
    }

    [Fact]
    public void TrapRouteRequiresExactCapabilityAndRejectsAfterDrainWithoutFakeObservation()
    {
        var scenario = CreateScenario();
        var root = CreateConfiguredRoot(scenario);
        var region = scenario.Kernel.AllocateRegion(scenario.Owner, 4096).Value!;
        var regionCapability = scenario.Kernel.MintCapability(new DomainId(5001), scenario.Owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        var mapped = scenario.Kernel.MapGuestRegion(scenario.Owner, root.Domain, root.MemoryCapability,
            regionCapability, region.Handle, new(0, 4096), GuestMemoryAccess.Read);
        Assert.True(mapped.IsSuccess, mapped.Message);
        var mapping = mapped.Value!;
        Assert.True(scenario.Kernel.StartVirtualDomain(scenario.Owner, root.Domain, root.ExecuteCapability).IsSuccess);
        Assert.Equal(KernelError.CapabilityNotFound,
            scenario.Kernel.ObserveVirtualTrap(scenario.Owner, root.Domain, default).Error);
        Assert.Equal(0, scenario.Provider.TrapCalls);
        var trap = scenario.Kernel.ObserveVirtualTrap(scenario.Owner, root.Domain, root.TrapCapability);
        Assert.True(trap.IsSuccess, trap.Message);
        Assert.Equal(VirtualTrapKind.Timer, trap.Value!.Kind);
        Assert.Equal(root.Domain, trap.Value.Domain);
        Assert.Equal(1, scenario.Provider.TrapCalls);
        Assert.Equal(KernelError.PlatformBindingDraining,
            scenario.Kernel.DestroyVirtualDomain(scenario.Owner, root.Domain, root.ConfigureCapability).Error);
        Assert.Equal(KernelError.InvalidTransition,
            scenario.Kernel.ObserveVirtualTrap(scenario.Owner, root.Domain, root.TrapCapability).Error);
        Assert.Equal(1, scenario.Provider.TrapCalls);
        Assert.True(scenario.Kernel.CloseGuestRegionMapping(scenario.Owner, root.Domain,
            root.MemoryCapability, mapping.Mapping).IsSuccess);
        Assert.True(scenario.Kernel.DestroyVirtualDomain(scenario.Owner, root.Domain,
            root.ConfigureCapability).IsSuccess);
    }

    [Fact]
    public void MalformedTrapEvidenceQuarantinesAndCannotActAsCompletion()
    {
        var scenario = CreateScenario(malformedTrap: true);
        var root = CreateConfiguredRoot(scenario);
        Assert.True(scenario.Kernel.StartVirtualDomain(scenario.Owner, root.Domain, root.ExecuteCapability).IsSuccess);

        var trap = scenario.Kernel.ObserveVirtualTrap(scenario.Owner, root.Domain, root.TrapCapability);

        Assert.Equal(KernelError.PlatformFaulted, trap.Error);
        Assert.Equal(VirtualDomainState.Quarantined,
            scenario.Kernel.QueryVirtualDomain(scenario.Owner, root.Domain).Value);
        Assert.Equal(KernelError.PlatformFaulted,
            scenario.Kernel.DestroyVirtualDomain(scenario.Owner, root.Domain, root.ConfigureCapability).Error);
    }

    [Fact]
    public void FeatureFamiliesRemainIndependentAndVmxAbsenceDoesNotDisableNeutralDomains()
    {
        var host = new SingPlus.Platform.Host.HostPlatformAuthorityProvider();
        var manifest = host.QueryFeatures();

        Assert.Equal(PlatformFeatureAvailability.ModelOnly,
            manifest.Resolve(PlatformFeatureFamily.VirtualizationDomains).Availability);
        Assert.Equal(PlatformFeatureAvailability.ModelOnly,
            manifest.Resolve(PlatformFeatureFamily.GuestMemory).Availability);
        Assert.Equal(PlatformFeatureAvailability.ModelOnly,
            manifest.Resolve(PlatformFeatureFamily.VirtualEvents).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            manifest.Resolve(PlatformFeatureFamily.NestedDomains).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            manifest.Resolve(PlatformFeatureFamily.VirtualTraps).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            manifest.Resolve(PlatformFeatureFamily.BoundedVirtualIo).Availability);
        Assert.Equal(PlatformFeatureAvailability.Unavailable,
            manifest.Resolve(PlatformFeatureFamily.VmxCompatibility).Availability);
    }

    [Fact]
    public async Task ExistingTypedSessionRoutesNestedAdmissionAndSemanticTrapObservation()
    {
        var scenario = CreateScenario();
        var service = TestFixtures.Create(scenario.Kernel, 503, 5003).Handle;
        var protocol = IVirtualizationServiceProtocol.CreateDefinition();
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var descriptor = scenario.Kernel.RegisterService(service, "virtualization", contract, protocol,
            IVirtualizationServiceResponseProtocol.Definition,
            [new(ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure)]).Value!;
        var session = scenario.Kernel.OpenSession(scenario.Owner, descriptor, [scenario.CreateCapability]).Value;
        var host = RuntimeVirtualizationServiceHost.CreateForSession(scenario.Kernel, service, session).Value!;
        var client = IVirtualizationServiceRuntimeClient.Create(
            new RuntimeSipClientTransport(scenario.Kernel, scenario.Owner, session));

        var createTask = client.CreateAsync(new(scenario.CreateCapability, new(2, 8192))).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var root = (await createTask).Authority;
        await Dispatch(client.ConfigureAsync(new(root.Domain, root.ConfigureCapability)), host);
        var nestedTask = client.CreateNestedAsync(new(root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle))).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var nested = (await nestedTask).Authority;
        Assert.NotEqual(root.Domain, nested.Domain);

        await Dispatch(client.StartAsync(new(root.Domain, root.ExecuteCapability)), host);
        var trapTask = client.ObserveTrapAsync(new(root.Domain, root.TrapCapability)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        Assert.Equal(VirtualTrapKind.Timer, (await trapTask).Trap.Kind);
        await Dispatch(client.DestroyAsync(new(root.Domain, root.ConfigureCapability)), host);
        Assert.True(scenario.Kernel.CloseSession(scenario.Owner, session).IsSuccess);
    }

    [Fact]
    public async Task TypedSipV3ArtifactContourRunsCreateMapAdmitStartRetiredWorkAndDestroy()
    {
        var scenario = CreateScenario(executableArtifact: true);
        var service = TestFixtures.Create(scenario.Kernel, 505, 5005).Handle;
        var protocol = IVirtualizationServiceProtocol.CreateDefinition();
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var descriptor = scenario.Kernel.RegisterService(service, "virtualization.v3", contract, protocol,
            IVirtualizationServiceResponseProtocol.Definition,
            [new(ResourceKind.Virtualization, VirtualizationResourceIds.Create, CapabilityRights.Configure)]).Value!;
        var session = scenario.Kernel.OpenSession(scenario.Owner, descriptor, [scenario.CreateCapability]).Value;
        var host = RuntimeVirtualizationServiceHost.CreateForSession(scenario.Kernel, service, session).Value!;
        var client = IVirtualizationServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Owner, session));

        var createTask = client.CreateAsync(new(scenario.CreateCapability, new(1, 4096))).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var domain = (await createTask).Authority;
        await Dispatch(client.ConfigureAsync(new(domain.Domain, domain.ConfigureCapability)), host);
        var region = scenario.Kernel.AllocateRegion(scenario.Owner, 4096).Value!;
        var regionCapability = scenario.Kernel.MintCapability(new DomainId(5001), scenario.Owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(region.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Execute).Value!.CapabilityId;
        var mapTask = client.MapGuestRegionAsync(new(domain.Domain, domain.MemoryCapability, regionCapability,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read | GuestMemoryAccess.Execute)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var mapping = (await mapTask).Mapping;
        await Dispatch(client.BindExecutableArtifactAsync(new(domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, new BoundedBytes([0x56, 0x33]), 1)), host);
        await Dispatch(client.StartAsync(new(domain.Domain, domain.ExecuteCapability)), host);
        Assert.NotNull(scenario.Provider.LastExecutionReceipt);
        Assert.True(scenario.Provider.LastExecutionReceipt!.Value.IsTerminal);
        Assert.True(scenario.Provider.LastExecutionReceipt!.Value.RetiredWorkUnits > 0);
        await Dispatch(client.CloseGuestRegionMappingAsync(new(domain.Domain, domain.MemoryCapability, mapping.Mapping)), host);
        await Dispatch(client.DestroyAsync(new(domain.Domain, domain.ConfigureCapability)), host);
        Assert.True(scenario.Kernel.CloseSession(scenario.Owner, session).IsSuccess);
    }

    private static async Task Dispatch(ValueTask pending, RuntimeVirtualizationServiceHost host)
    {
        var task = pending.AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        await task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static Scenario CreateScenario(
        PlatformAuthorityStatus? nestedFailure = null,
        bool malformedNestedLease = false,
        bool malformedTrap = false,
        bool malformedNestedClose = false,
        bool wrongNestedImmediateParent = false,
        bool executableArtifact = false)
    {
        var provider = new NestedProvider(nestedFailure, malformedNestedLease, malformedTrap, malformedNestedClose,
            wrongNestedImmediateParent, executableArtifact);
        var kernel = new RuntimeKernel(provider);
        var owner = TestFixtures.Create(kernel, 501, 5001).Handle;
        var create = kernel.MintCapability(new DomainId(5001), owner, ResourceKind.Virtualization,
            VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
        return new(kernel, provider, owner, create);
    }

    private static VirtualDomainAuthoritySet CreateConfiguredRoot(Scenario scenario)
    {
        var root = scenario.Kernel.CreateVirtualDomain(scenario.Owner, scenario.CreateCapability,
            new VirtualDomainProfile(2, 8192)).Value!;
        Assert.True(scenario.Kernel.ConfigureVirtualDomain(scenario.Owner, root.Domain,
            root.ConfigureCapability).IsSuccess);
        return root;
    }

    private static PlatformProviderDomainLease ParentLease() => new(new(1), new(7),
        new(new DomainId(1), new(new ProcessId(1), 1)));

    private static PlatformChildDomainIntent Intent(PlatformChildAuthorityClass authority) =>
        new(new(1, 8192), new(authority, authority));

    private sealed record Scenario(RuntimeKernel Kernel, NestedProvider Provider,
        ProcessHandle Owner, CapabilityId CreateCapability);

    private sealed class NestedProvider : IPlatformAuthorityProvider, IPlatformFeatureProvider,
        IPlatformChildDomainProvider, IPlatformNestedDomainProvider, IPlatformGuestMemoryProvider,
        IPlatformVirtualEventProvider, IPlatformVirtualTrapProvider,
        IPlatformRegionRevocationProvider, IPlatformCompletionProvider, IPlatformChildExecutionProvider
    {
        private readonly PlatformAuthorityStatus? _nestedFailure;
        private readonly bool _malformedNestedLease;
        private readonly bool _malformedTrap;
        private readonly bool _malformedNestedClose;
        private readonly bool _wrongNestedImmediateParent;
        private readonly bool _executableArtifact;
        private ulong _next = 10;
        private readonly Dictionary<PlatformProviderChildDomainLeaseId, bool> _nested = [];
        private readonly Dictionary<PlatformProviderChildDomainLeaseId, ulong> _trapSequences = [];

        public NestedProvider(PlatformAuthorityStatus? nestedFailure, bool malformedNestedLease,
            bool malformedTrap, bool malformedNestedClose, bool wrongNestedImmediateParent, bool executableArtifact)
        {
            _nestedFailure = nestedFailure;
            _malformedNestedLease = malformedNestedLease;
            _malformedTrap = malformedTrap;
            _malformedNestedClose = malformedNestedClose;
            _wrongNestedImmediateParent = wrongNestedImmediateParent;
            _executableArtifact = executableArtifact;
        }

        public int NestedCreateCalls { get; private set; }
        public int TrapCalls { get; private set; }
        public int GuestUnmapCalls { get; private set; }
        public int NestedCloseCalls { get; private set; }
        public PlatformProviderChildDomainLease LastNestedLease { get; private set; }
        public PlatformChildExecutionReceipt? LastExecutionReceipt { get; private set; }
        public List<string> CallLog { get; } = [];
        public PlatformProviderDescriptor Descriptor { get; } = new(new("phase8.nested-test"), 2,
            PlatformAuthorityFeatures.NeutralDomainBinding | PlatformAuthorityFeatures.DirectOwnedRegionMapping);

        public PlatformFeatureManifest QueryFeatures()
        {
            var features = new List<PlatformFeatureDescriptor>
            {
            new(PlatformFeatureFamily.NeutralDomains, PlatformDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.OwnedRegionMapping, PlatformOwnedRegionMappingContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.VirtualizationDomains, PlatformChildDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.NestedDomains, PlatformNestedDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.ChildDomainLifecycle, PlatformChildDomainContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.ChildGuestMemory, PlatformGuestMemoryContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.GuestMemory, PlatformGuestMemoryContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.ChildEventDelivery, PlatformVirtualEventContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.VirtualEvents, PlatformVirtualEventContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.ChildTrapDelivery, PlatformVirtualTrapContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            new(PlatformFeatureFamily.VirtualTraps, PlatformVirtualTrapContract.ContractVersion, PlatformFeatureAvailability.RuntimeAdmission),
            };
            if (_executableArtifact)
                features.Add(new(PlatformFeatureFamily.ChildExecutableArtifact,
                    PlatformChildExecutionContract.ContractVersion, PlatformFeatureAvailability.Executable));
            return new(features);
        }

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject) =>
            PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(new(new(_next++), new(7), subject));

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            CallLog.Add("parent-close");
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease, PlatformRegionIdentity region, PlatformMemoryAccess access) =>
            PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Ok(new(new(_next++), new(11), domainLease, region, access));

        public PlatformAuthorityResult RevokeRegionMapping(PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) => PlatformAuthorityResult.Ok();

        public PlatformAuthorityResult<PlatformRegionRevocationTicket> BeginRegionMappingRevocation(
            PlatformProviderRegionMappingLease mapping, PlatformRegionRevocationPolicy policy)
        {
            CallLog.Add("mapping-close");
            return PlatformAuthorityResult<PlatformRegionRevocationTicket>.Ok(new(mapping.MappingId,
                mapping.Generation, new(new(_next++), new(1), mapping.DomainLease)));
        }

        public PlatformAuthorityResult<PlatformCompletionReceipt> ObserveCompletion(PlatformOperationIdentity operation) =>
            PlatformAuthorityResult<PlatformCompletionReceipt>.Ok(new(operation.OperationId,
                operation.Generation, operation.DomainLease, PlatformCompletionState.Closed));

        public PlatformAuthorityResult<PlatformProviderChildDomainLease> CreateChildDomain(
            PlatformProviderDomainLease parentLease, PlatformChildDomainIntent intent) =>
            PlatformAuthorityResult<PlatformProviderChildDomainLease>.Ok(new(new(_next++), new(23), parentLease, intent));

        public PlatformAuthorityResult<PlatformProviderNestedDomainLease> CreateNestedChildDomain(
            PlatformNestedDomainRequest request)
        {
            NestedCreateCalls++;
            if (_nestedFailure is { } failure)
                return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Fail(failure, "Injected nested admission failure.");
            LastNestedLease = new(new(_next++), new(47), request.ParentChildLease.ParentDomainLease,
                _malformedNestedLease
                    ? request.ChildIntent with { Authority = new(request.ChildIntent.Authority.ParentAuthority,
                        request.ChildIntent.Authority.ParentAuthority | PlatformChildAuthorityClass.Execution) }
                    : request.ChildIntent);
            _nested[LastNestedLease.LeaseId] = true;
            return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Ok(new(LastNestedLease,
                _wrongNestedImmediateParent ? new PlatformProviderChildDomainLeaseId(999) : request.ParentChildLease.LeaseId,
                request.ParentChildLease.Generation));
        }

        public PlatformAuthorityResult TransitionChildDomain(PlatformProviderChildDomainLease lease,
            PlatformChildDomainTransition transition)
        {
            if (transition == PlatformChildDomainTransition.BeginDrain)
                CallLog.Add(_nested.ContainsKey(lease.LeaseId) ? "nested-drain" : "root-drain");
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformChildDomainClosureReceipt> CloseChildDomain(
            PlatformProviderChildDomainLease lease)
        {
            bool nested = _nested.ContainsKey(lease.LeaseId);
            if (nested) NestedCloseCalls++;
            CallLog.Add(nested ? "nested-close" : "root-close");
            return PlatformAuthorityResult<PlatformChildDomainClosureReceipt>.Ok(new(lease.LeaseId,
                nested && _malformedNestedClose ? new(lease.Generation.Value + 1) : lease.Generation,
                lease.ParentDomainLease.LeaseId, lease.ParentDomainLease.Generation,
                PlatformChildDomainClosureDisposition.Closed));
        }

        public PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease> MapGuestRegion(
            PlatformGuestRegionMappingRequest request) =>
            PlatformAuthorityResult<PlatformProviderGuestRegionMappingLease>.Ok(new(new(_next++), new(31),
                request.ChildLease, request.ParentMapping.Lease, request.GuestRange, request.Access));

        public PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt> UnmapGuestRegion(
            PlatformProviderGuestRegionMappingLease lease)
        {
            GuestUnmapCalls++;
            CallLog.Add("guest-unmap");
            return PlatformAuthorityResult<PlatformGuestRegionMappingClosureReceipt>.Ok(new(lease.LeaseId,
                lease.Generation, lease.ChildLease.LeaseId, lease.ChildLease.Generation,
                lease.ChildLease.ParentDomainLease.LeaseId, lease.ChildLease.ParentDomainLease.Generation, true));
        }

        public PlatformAuthorityResult<PlatformVirtualEventReceipt> InjectVirtualEvent(PlatformVirtualEventRequest request) =>
            PlatformAuthorityResult<PlatformVirtualEventReceipt>.Ok(new(request.ChildLease.LeaseId,
                request.ChildLease.Generation, request.ChildLease.ParentDomainLease.LeaseId,
                request.ChildLease.ParentDomainLease.Generation, 1, request.EventClass, request.SourceResourceId));

        public PlatformAuthorityResult<PlatformVirtualTrapEvidence> ObserveVirtualTrap(
            PlatformProviderChildDomainLease lease)
        {
            TrapCalls++;
            var sequence = _trapSequences.GetValueOrDefault(lease.LeaseId) + 1;
            _trapSequences[lease.LeaseId] = sequence;
            return PlatformAuthorityResult<PlatformVirtualTrapEvidence>.Ok(new(lease.LeaseId,
                _malformedTrap ? new(lease.Generation.Value + 1) : lease.Generation,
                lease.ParentDomainLease.LeaseId, lease.ParentDomainLease.Generation,
                sequence, PlatformVirtualTrapKind.Timer));
        }

        public PlatformAuthorityResult<PlatformExecutableArtifactReceipt> BindExecutableArtifact(
            PlatformExecutableArtifactRequest request) => PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Ok(new(
                new(1), new(1), request.ChildLease.LeaseId, request.ChildLease.Generation,
                request.GuestMapping.LeaseId, request.GuestMapping.Generation,
                request.ChildLease.ParentDomainLease.LeaseId, request.ChildLease.ParentDomainLease.Generation,
                new string('a', 64), request.MaximumExecutionSteps));

        public PlatformAuthorityResult<PlatformChildExecutionReceipt> StartExecutableArtifact(
            PlatformChildExecutionStartRequest request)
        {
            LastExecutionReceipt = new(request.Artifact.ArtifactId, request.Artifact.ArtifactGeneration,
                request.Artifact.ChildLeaseId, request.Artifact.ChildGeneration, request.Artifact.MappingLeaseId,
                request.Artifact.MappingGeneration, request.Artifact.ParentLeaseId, request.Artifact.ParentGeneration,
                request.Artifact.ContentDigest, request.OperationId, request.OperationGeneration, new(1), 1, 1, 0, true);
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Ok(LastExecutionReceipt.Value);
        }
    }
}
