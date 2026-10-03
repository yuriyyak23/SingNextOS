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
    public void NestedCreateReceiptLossPinsImmediateParentAndRoot()
    {
        var provider = new NestedProvider(null, false, false, false, false, false)
        {
            ThrowAfterNestedMaterialize = true,
        };
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var parent = bridge.CreateChildDomain(root, subject, intent).Value!;

        Assert.Equal(KernelError.PlatformFaulted,
            bridge.CreateNestedChildDomain(parent, subject.Process, intent).Error);
        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(parent, PlatformChildDomainTransition.BeginDrain).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(root, subject).Error);
        Assert.DoesNotContain("parent-close", provider.CallLog);
    }

    [Fact]
    public void NestedCreateCallbackBlocksImmediateParentTransition()
    {
        var provider = new NestedProvider(null, false, false, false, false, false);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var parent = bridge.CreateChildDomain(root, subject, intent).Value!;
        provider.AfterNestedMaterialize = () => Assert.Equal(KernelError.PlatformBindingActive,
            bridge.TransitionChildDomain(parent, PlatformChildDomainTransition.BeginDrain).Error);

        Assert.True(bridge.CreateNestedChildDomain(parent, subject.Process, intent).IsSuccess);
        Assert.Equal(1, provider.NestedCreateCalls);
    }

    [Fact]
    public void ResetInsideNestedCreateCallbackCannotPublishLateChild()
    {
        var provider = new NestedProvider(null, false, false, false, false, false);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var parent = bridge.CreateChildDomain(root, subject, intent).Value!;
        provider.AfterNestedMaterialize = () => Assert.True(bridge.ObserveBackendReset().IsSuccess);

        Assert.Equal(KernelError.PlatformFaulted,
            bridge.CreateNestedChildDomain(parent, subject.Process, intent).Error);
        Assert.Equal(1, provider.NestedCreateCalls);
        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(parent, PlatformChildDomainTransition.BeginDrain).Error);
        Assert.True(bridge.TryGetQuarantinedDomainBinding(subject, out var current));
        Assert.NotEqual(root.Generation, current.Generation);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(current, subject).Error);
    }

    [Fact]
    public void ImmediateParentTransitionCallbackRejectsNestedCreateBeforeProvider()
    {
        var provider = new NestedProvider(null, false, false, false, false, false);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.GuestMemory);
        var parent = bridge.CreateChildDomain(root, subject, intent).Value!;
        provider.BeforeChildTransition = () => Assert.Equal(KernelError.PlatformBindingActive,
            bridge.CreateNestedChildDomain(parent, subject.Process, intent).Error);

        Assert.True(bridge.TransitionChildDomain(parent,
            PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.Equal(0, provider.NestedCreateCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExecutableArtifactBindOrStartReceiptLossPinsChildAndMapping(bool start)
    {
        var provider = new NestedProvider(null, false, false, false, false, true)
        {
            ThrowAfterArtifactBind = !start,
            ThrowAfterArtifactStart = start,
        };
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory);
        var child = bridge.CreateChildDomain(root, subject, intent).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), subject.ProcessGeneration), 4096);
        var parentMapping = bridge.MapOwnedRegion(root, subject, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var guest = bridge.MapChildGuestRegion(child, parentMapping,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;

        if (start)
        {
            var artifact = bridge.BindChildExecutableArtifact(child, guest,
                new byte[] { 0x56, 0x33 }, 1).Value!;
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.StartChildExecutableArtifact(artifact).Error);
        }
        else
        {
            Assert.Equal(KernelError.PlatformFaulted,
                bridge.BindChildExecutableArtifact(child, guest,
                    new byte[] { 0x56, 0x33 }, 1).Error);
        }
        Assert.Equal(KernelError.PlatformFaulted, bridge.ValidateMapping(parentMapping, subject).Error);
        Assert.Equal(KernelError.PlatformFaulted,
            bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.RevokeDomain(root, subject).Error);
    }

    [Fact]
    public void RevokedParentMappingCannotReachExecutableChildStartProvider()
    {
        var provider = new NestedProvider(null, false, false, false, false, true);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory);
        var child = bridge.CreateChildDomain(root, subject, intent).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), subject.ProcessGeneration), 4096);
        var parentMapping = bridge.MapOwnedRegion(root, subject, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var guest = bridge.MapChildGuestRegion(child, parentMapping,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        var artifact = bridge.BindChildExecutableArtifact(child, guest,
            new byte[] { 0x56, 0x33 }, 1).Value!;

        Assert.Single(bridge.BeginCapabilityRevocation(new CapabilityId(8), static id => id == new CapabilityId(8)));
        Assert.False(bridge.StartChildExecutableArtifact(artifact).IsSuccess);
        Assert.Equal(0, provider.ArtifactStartCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundArtifactPinsOnlyItsExactGuestMappingAfterCallbackSettles(bool start)
    {
        var provider = new NestedProvider(null, false, false, false, false, true);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory);
        var child = bridge.CreateChildDomain(root, subject, intent).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), subject.ProcessGeneration), 8192);
        var parent = bridge.MapOwnedRegion(root, subject, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var boundGuest = bridge.MapChildGuestRegion(child, parent,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        var otherGuest = bridge.MapChildGuestRegion(child, parent,
            new(4096, 4096), PlatformGuestMemoryAccess.Read).Value!;
        var artifact = bridge.BindChildExecutableArtifact(child, boundGuest,
            new byte[] { 0x56, 0x33 }, 1).Value!;

        Assert.Equal(KernelError.PlatformBindingActive,
            bridge.UnmapChildGuestRegion(boundGuest).Error);
        Assert.Equal(0, provider.GuestUnmapCalls);
        Assert.True(bridge.UnmapChildGuestRegion(otherGuest).IsSuccess);
        Assert.Equal(1, provider.GuestUnmapCalls);
        if (start)
            Assert.True(bridge.StartChildExecutableArtifact(artifact).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive,
            bridge.UnmapChildGuestRegion(boundGuest).Error);
        Assert.Equal(1, provider.GuestUnmapCalls);
        Assert.True(bridge.TransitionChildDomain(child,
            PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive, bridge.CloseChildDomain(child).Error);
        Assert.Equal(KernelError.PlatformBindingActive,
            bridge.BeginRegionMappingRevocation(parent, subject,
                PlatformRegionRevocationPolicy.DrainBeforeRevoke).Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuestUnmapCannotOvertakeExecutableBindOrStartCallback(bool start)
    {
        var provider = new NestedProvider(null, false, false, false, false, true);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory);
        var child = bridge.CreateChildDomain(root, subject, intent).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), subject.ProcessGeneration), 4096);
        var parent = bridge.MapOwnedRegion(root, subject, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var guest = bridge.MapChildGuestRegion(child, parent,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        Action probe = () =>
        {
            Assert.Equal(KernelError.PlatformBindingActive,
                bridge.UnmapChildGuestRegion(guest).Error);
            Assert.Equal(KernelError.PlatformBindingActive,
                bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
        };

        if (start)
        {
            var artifact = bridge.BindChildExecutableArtifact(child, guest,
                new byte[] { 0x56, 0x33 }, 1).Value!;
            provider.AfterArtifactStart = probe;
            Assert.True(bridge.StartChildExecutableArtifact(artifact).IsSuccess);
        }
        else
        {
            provider.AfterArtifactBind = probe;
            Assert.True(bridge.BindChildExecutableArtifact(child, guest,
                new byte[] { 0x56, 0x33 }, 1).IsSuccess);
        }
    }

    [Fact]
    public void ReentrantExecutableStartCannotEnterProviderTwice()
    {
        var provider = new NestedProvider(null, false, false, false, false, true);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory);
        var child = bridge.CreateChildDomain(root, subject, intent).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), subject.ProcessGeneration), 4096);
        var parent = bridge.MapOwnedRegion(root, subject, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var guest = bridge.MapChildGuestRegion(child, parent,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        var artifact = bridge.BindChildExecutableArtifact(child, guest,
            new byte[] { 0x56, 0x33 }, 1).Value!;
        provider.AfterArtifactStart = () => Assert.False(bridge.StartChildExecutableArtifact(artifact).IsSuccess);

        Assert.True(bridge.StartChildExecutableArtifact(artifact).IsSuccess);
        Assert.Equal(1, provider.ArtifactStartCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParentAuthorizationRevokedInsideExecutableCallbackPreventsPublication(bool start)
    {
        var provider = new NestedProvider(null, false, false, false, false, true);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(1),
            new ProcessHandle(new ProcessId(1), 1));
        var root = bridge.BindDomain(subject).Value!;
        var intent = Intent(PlatformChildAuthorityClass.Lifecycle |
            PlatformChildAuthorityClass.Execution | PlatformChildAuthorityClass.GuestMemory);
        var child = bridge.CreateChildDomain(root, subject, intent).Value!;
        var region = new PlatformRegionIdentity(new(new RegionId(5), new RegionGeneration(1)),
            new(new DomainId(1), subject.ProcessGeneration), 4096);
        var parent = bridge.MapOwnedRegion(root, subject, new CapabilityId(8), region,
            PlatformMemoryAccess.Read | PlatformMemoryAccess.Write).Value!;
        var guest = bridge.MapChildGuestRegion(child, parent,
            new(0, 4096), PlatformGuestMemoryAccess.Read).Value!;
        Action revoke = () => Assert.Single(bridge.BeginCapabilityRevocation(new CapabilityId(8), static id => id == new CapabilityId(8)));

        if (start)
        {
            var artifact = bridge.BindChildExecutableArtifact(child, guest,
                new byte[] { 0x56, 0x33 }, 1).Value!;
            provider.AfterArtifactStart = revoke;
            Assert.Equal(KernelError.PlatformFaulted, bridge.StartChildExecutableArtifact(artifact).Error);
        }
        else
        {
            provider.AfterArtifactBind = revoke;
            Assert.Equal(KernelError.PlatformFaulted, bridge.BindChildExecutableArtifact(child, guest,
                new byte[] { 0x56, 0x33 }, 1).Error);
        }
        Assert.Equal(KernelError.PlatformFaulted, bridge.UnmapChildGuestRegion(guest).Error);
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
        Assert.True(scenario.Kernel.DestroyVirtualDomain(scenario.Owner, root.Domain, root.ConfigureCapability).IsSuccess);
        Assert.Equal(KernelError.VirtualDomainNotFound,
            scenario.Kernel.ObserveVirtualTrap(scenario.Owner, root.Domain, root.TrapCapability).Error);
        Assert.Equal(1, scenario.Provider.TrapCalls);
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
    public async Task TypedSipV3ArtifactContourPinsMappingAfterRetiredWorkUntilReleaseEvidence()
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
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.CloseGuestRegionMapping(scenario.Owner, domain.Domain,
                domain.MemoryCapability, mapping.Mapping).Error);
        Assert.Equal(0, scenario.Provider.GuestUnmapCalls);
        Assert.Equal(VirtualDomainState.Quarantined,
            scenario.Kernel.QueryVirtualDomain(scenario.Owner, domain.Domain).Value);
        Assert.False(scenario.Kernel.DestroyVirtualDomain(scenario.Owner, domain.Domain,
            domain.ConfigureCapability).IsSuccess);
        Assert.Equal(KernelError.PlatformBindingActive,
            scenario.Kernel.ReleaseRegion(scenario.Owner, region).Error);
        Assert.True(scenario.Kernel.CloseSession(scenario.Owner, session).IsSuccess);
    }

    private static async Task Dispatch(ValueTask pending, RuntimeVirtualizationServiceHost host)
    {
        var task = pending.AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        await task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static (Scenario Scenario, VirtualDomainAuthoritySet Domain, GuestRegionMapping Mapping) ArtifactScenario()
    {
        var s = CreateScenario(executableArtifact: true);
        var domain = CreateConfiguredRoot(s);
        var region = s.Kernel.AllocateRegion(s.Owner, 4096).Value!;
        var capability = s.Kernel.MintCapability(new DomainId(5001), s.Owner, ResourceKind.MemoryRegion,
            CapabilityResourceIds.MemoryRegion(region.Handle.RegionId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Execute).Value!.CapabilityId;
        var mapping = s.Kernel.MapGuestRegion(s.Owner, domain.Domain, domain.MemoryCapability, capability,
            region.Handle, new(0, 4096), GuestMemoryAccess.Read | GuestMemoryAccess.Execute).Value!;
        return (s, domain, mapping);
    }

    private static object ArtifactField(object owner, string name) => owner.GetType().GetField(name,
        global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(owner)!;

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task KernelArtifactPendingRevokeOrStopRetainsReceiptWithoutLateState(bool start, bool stop)
    {
        var (s, domain, mapping) = ArtifactScenario();
        KernelResult Bind() => s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain,
            domain.ExecuteCapability, mapping.Mapping, new byte[] { 0x56, 0x33 }, 1);
        if (start) Assert.True(Bind().IsSuccess);
        KernelResult Run() => start ? s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability) : Bind();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Action hold = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        if (start) s.Provider.AfterArtifactStart = hold; else s.Provider.AfterArtifactBind = hold;
        var work = Task.Run(Run);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingActive, Run().Error);
            var activeRecord = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
            Assert.False(s.Kernel.PlatformAuthority.UnmapChildGuestRegion(activeRecord.PlatformMappings[mapping.Mapping.MappingId].Guest).IsSuccess);
            if (stop) Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            else Assert.True(s.Kernel.RevokeCapability(domain.ExecuteCapability).IsSuccess);
            Assert.Equal(0, s.Provider.GuestUnmapCalls);
        }
        finally { release.Set(); }
        var result = await work.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(result.IsSuccess);
        if (!stop) Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        s.Provider.AfterArtifactStart = null;
        s.Provider.AfterArtifactBind = null;
        var record = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        if (start)
        {
            var table = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_executableArtifacts");
            var receiptOwner = Assert.Single(table.Values.Cast<object>());
            Assert.NotNull(receiptOwner.GetType().GetProperty("ExecutionReceipt")!.GetValue(receiptOwner));
        }
        Assert.NotNull(record.ExecutableArtifact); // known binding retained by real cleanup owner
        Assert.NotEqual(VirtualDomainState.Running, record.State);
        Assert.False(Run().IsSuccess);
        Assert.Equal(1, s.Provider.ArtifactBindCalls);
        Assert.Equal(start ? 1 : 0, s.Provider.ArtifactStartCalls);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        Assert.Equal(0, s.Provider.GuestUnmapCalls); // no artifact release consumer/evidence exists
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(false, 3)] [InlineData(false, 4)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)] [InlineData(true, 3)] [InlineData(true, 4)]
    public void KernelArtifactFaultsPreserveExactDependencyPinsAndBalancedCounters(bool start, int fault)
    {
        var (s, domain, mapping) = ArtifactScenario();
        KernelResult Bind() => s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain,
            domain.ExecuteCapability, mapping.Mapping, new byte[] { 0x56, 0x33 }, 1);
        if (start) Assert.True(Bind().IsSuccess);
        if (fault == 0) { s.Provider.ThrowAfterArtifactBind = !start; s.Provider.ThrowAfterArtifactStart = start; }
        if (fault == 1)
        {
            Action reset = () => Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
            if (start) s.Provider.AfterArtifactStart = reset; else s.Provider.AfterArtifactBind = reset;
        }
        if (fault == 2) s.Provider.ArtifactStatus = PlatformAuthorityStatus.Denied;
        if (fault == 3) s.Provider.MalformedArtifactEvidence = true;
        if (fault == 4) s.Provider.ArtifactStatus = PlatformAuthorityStatus.NotAccepted;
        Assert.False((start ? s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability) : Bind()).IsSuccess);
        s.Provider.ThrowAfterArtifactBind = s.Provider.ThrowAfterArtifactStart = false;
        s.Provider.AfterArtifactBind = s.Provider.AfterArtifactStart = null;
        s.Provider.ArtifactStatus = null; s.Provider.MalformedArtifactEvidence = false;
        foreach (var name in new[] { "_childBindings", "_guestBindings", "_executableArtifacts" })
        {
            var table = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, name);
            foreach (var value in table.Values)
            {
                var count = value!.GetType().GetProperty(name == "_childBindings" ? "PendingChildEffects" : "PendingExecutableEffects");
                if (count is not null) Assert.Equal(0, count.GetValue(value));
                var pending = value.GetType().GetProperty("StartInFlight");
                if (pending is not null) Assert.False((bool)pending.GetValue(value)!);
            }
        }
        if (fault == 4 && !start)
        {
            Assert.True(Bind().IsSuccess); // explicit no-admission response permits fresh admission
            return;
        }
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
    }

    [Fact]
    public void KernelArtifactCapturesCallerPackageAndRejectsDuplicateBind()
    {
        var (s, domain, mapping) = ArtifactScenario();
        byte[] package = [0x56, 0x33];
        s.Provider.AfterArtifactBind = () => package[0] = 0xff;
        Assert.True(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, package, 1).IsSuccess);
        Assert.Equal(new byte[] { 0x56, 0x33 }, s.Provider.LastArtifactPackage.ToArray());
        Assert.False(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, package, 1).IsSuccess);
        Assert.Equal(1, s.Provider.ArtifactBindCalls);
        Assert.True(s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        Assert.Equal(VirtualDomainState.Running, s.Kernel.QueryVirtualDomain(s.Owner, domain.Domain).Value);
        Assert.Equal(1, s.Provider.ArtifactStartCalls);
    }

    [Fact]
    public void KernelArtifactSourceAndBoundsRefuseWithoutProviderOrIdentityMutation()
    {
        var (s, domain, mapping) = ArtifactScenario();
        var bridge = s.Kernel.PlatformAuthority;
        var before = ArtifactField(bridge, "_nextExecutableArtifactBindingId");
        Assert.False(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, ReadOnlyMemory<byte>.Empty, 1).IsSuccess);
        Assert.False(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, new byte[] { 1 }, 0).IsSuccess);
        Assert.True(s.Kernel.RevokeCapability(domain.ExecuteCapability).IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain,
            domain.ExecuteCapability, mapping.Mapping, new byte[] { 1 }, 1).Error);
        Assert.Equal(before, ArtifactField(bridge, "_nextExecutableArtifactBindingId"));
        Assert.Equal(0, s.Provider.ArtifactBindCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ArtifactCompletionConsumerRemainsInsideDeliveryInterlock(bool start)
    {
        var (s, domain, mapping) = ArtifactScenario();
        var record = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var child = record.ChildBinding!.Value;
        var guest = record.PlatformMappings[mapping.Mapping.MappingId].Guest;
        var bridge = s.Kernel.PlatformAuthority;
        KernelResult Probe(Func<KernelResult> commit)
        {
            Assert.False(bridge.UnmapChildGuestRegion(guest).IsSuccess);
            Assert.Equal(KernelError.PlatformBindingActive, bridge.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain).Error);
            return commit();
        }
        if (!start)
            Assert.True(bridge.BindChildExecutableArtifact(child, guest, new byte[] { 0x56, 0x33 }, 1,
                completionConsumer: (_, commit) => Probe(() => commit(() => KernelResult.Ok()))).IsSuccess);
        else
        {
            var bound = bridge.BindChildExecutableArtifact(child, guest, new byte[] { 0x56, 0x33 }, 1).Value!;
            Assert.True(bridge.StartChildExecutableArtifact(bound,
                completionConsumer: commit => Probe(() => commit(() => KernelResult.Ok()))).IsSuccess);
        }
    }

    [Fact]
    public void ArtifactStaleGenerationOrWrongTupleCannotConsumeStartIdentity()
    {
        var (s, domain, mapping) = ArtifactScenario();
        Assert.True(s.Kernel.BindVirtualDomainExecutable(s.Owner, domain.Domain, domain.ExecuteCapability,
            mapping.Mapping, new byte[] { 0x56, 0x33 }, 1).IsSuccess);
        var record = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        var exact = record.ExecutableArtifact!.Value;
        var before = ArtifactField(s.Kernel.PlatformAuthority, "_nextChildStartOperationId");
        Assert.Equal(KernelError.StaleGeneration, s.Kernel.PlatformAuthority.StartChildExecutableArtifact(
            exact with { Generation = new(exact.Generation.Value + 1) }).Error);
        Assert.False(s.Kernel.PlatformAuthority.StartChildExecutableArtifact(exact with
        {
            Child = exact.Child with { Owner = new(new ProcessId(9999), 2) },
        }).IsSuccess);
        Assert.Equal(before, ArtifactField(s.Kernel.PlatformAuthority, "_nextChildStartOperationId"));
        Assert.Equal(0, s.Provider.ArtifactStartCalls);
        Assert.True(s.Kernel.StartVirtualDomain(s.Owner, domain.Domain, domain.ExecuteCapability).IsSuccess);
        Assert.Equal(1, s.Provider.ArtifactStartCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void NestedSourceStopAndUnknownEffectsUseRealOwnersAndCleanup(int fault)
    {
        var s = CreateScenario(nestedFailure: fault == 6 ? PlatformAuthorityStatus.Denied : null);
        var parent = CreateConfiguredRoot(s);
        var request = new NestedVirtualDomainRequestProfile(new(1, 4096),
            VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory | VirtualDomainAuthorityClass.Execution);
        if (fault == 0) s.Provider.AfterNestedMaterialize = () => Assert.True(s.Kernel.RevokeCapability(parent.ConfigureCapability).IsSuccess);
        if (fault == 1) s.Provider.AfterNestedMaterialize = () => Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        if (fault == 2) s.Provider.ThrowAfterNestedMaterialize = true;
        if (fault == 3) s.Provider.AfterNestedMaterialize = () => Assert.True(s.Kernel.ObservePlatformBackendReset().IsSuccess);
        if (fault is 4 or 5)
        {
            int nonce = 0;
            s.Kernel.CapabilityAuthority.GetType().GetField("_nonceFactory",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.SetValue(
                s.Kernel.CapabilityAuthority, (Func<Guid>)(() =>
                {
                    if (++nonce == 2)
                    {
                        if (fault == 4) Assert.True(s.Kernel.RevokeCapability(parent.ConfigureCapability).IsSuccess);
                        else Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
                    }
                    return Guid.NewGuid();
                }));
        }
        Assert.False(s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain, parent.ConfigureCapability, request).IsSuccess);
        s.Provider.AfterNestedMaterialize = null; s.Provider.ThrowAfterNestedMaterialize = false;
        var records = ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner);
        if (fault is 0 or 1 or 4 or 5)
        {
            var nested = Assert.Single(records, x => x.ParentDomain == parent.Domain);
            Assert.False(nested.AuthorityPublished);
            Assert.Equal(fault is 4 or 5 ? 1 : 0, nested.Capabilities.Length);
            if (nested.Capabilities.Length != 0)
                Assert.False(s.Kernel.ConfigureVirtualDomain(s.Owner, nested.Handle, nested.Capabilities[0]).IsSuccess);
            Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.Equal(1, s.Provider.NestedCloseCalls);
        }
        else
        {
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
            Assert.Equal(0, s.Provider.NestedCloseCalls);
        }
        Assert.Equal(1, s.Provider.NestedCreateCalls);
        var table = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        foreach (var record in table.Values)
            Assert.Equal(0, record!.GetType().GetProperty("PendingChildEffects")!.GetValue(record));
    }

    [Fact]
    public void NestedPartialGrantIsNotPublishedAndSubsetCapabilitiesStayExact()
    {
        var s = CreateScenario();
        var parent = CreateConfiguredRoot(s);
        int nonce = 0;
        s.Kernel.CapabilityAuthority.GetType().GetField("_nonceFactory",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.SetValue(
            s.Kernel.CapabilityAuthority, (Func<Guid>)(() =>
            {
                if (++nonce == 2)
                {
                    var nested = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner), x => x.ParentDomain == parent.Domain);
                    Assert.False(nested.AuthorityPublished);
                    Assert.Equal(KernelError.PlatformBindingActive,
                        s.Kernel.ConfigureVirtualDomain(s.Owner, nested.Handle, nested.Capabilities[0]).Error);
                }
                return Guid.NewGuid();
            }));
        var result = s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain, parent.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.GuestMemory));
        Assert.True(result.IsSuccess, result.Message);
        Assert.NotEqual(default, result.Value!.ConfigureCapability);
        Assert.NotEqual(default, result.Value.MemoryCapability);
        Assert.Equal(default, result.Value.ExecuteCapability);
        Assert.Equal(default, result.Value.EventCapability);
        Assert.Equal(default, result.Value.TrapCapability);
        Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, result.Value.Domain, result.Value.ConfigureCapability).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentNestedCallbackStopsBeforeLatePublicationAndPreservesCleanup()
    {
        var s = CreateScenario();
        var parent = CreateConfiguredRoot(s);
        var request = new NestedVirtualDomainRequestProfile(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        s.Provider.AfterNestedMaterialize = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        var work = Task.Run(() => s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain, parent.ConfigureCapability, request));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingActive,
                s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain, parent.ConfigureCapability, request).Error);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        s.Provider.AfterNestedMaterialize = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.NestedCreateCalls);
        Assert.Equal(1, s.Provider.NestedCloseCalls);
    }

    [Fact]
    public void NestedAncestorDrainDeniesNewAdmissionAndPublicationButKeepsRealCleanup()
    {
        var s = CreateScenario();
        var parent = CreateConfiguredRoot(s);
        var first = s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain, parent.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).Value!;
        Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, first.Domain, first.ConfigureCapability).IsSuccess);
        var second = s.Kernel.CreateNestedVirtualDomain(s.Owner, first.Domain, first.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).Value!;
        var records = ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner);
        var rootRecord = Assert.Single(records, x => x.Handle == parent.Domain);
        Assert.True(s.Kernel.PlatformAuthority.TransitionChildDomain(rootRecord.ChildBinding!.Value,
            PlatformChildDomainTransition.BeginDrain).IsSuccess);
        Assert.False(s.Kernel.CreateNestedVirtualDomain(s.Owner, first.Domain, first.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).IsSuccess);
        Assert.Equal(2, s.Provider.NestedCreateCalls);
        Assert.False(s.Kernel.ConfigureVirtualDomain(s.Owner, second.Domain, second.ConfigureCapability).IsSuccess);
        Assert.Equal(VirtualDomainState.Created, s.Kernel.QueryVirtualDomain(s.Owner, second.Domain).Value);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(2, s.Provider.NestedCloseCalls);
    }

    [Fact]
    public void NestedUnknownAuthorityAndRevokedSourceRefuseBeforeMutation()
    {
        var s = CreateScenario();
        var parent = CreateConfiguredRoot(s);
        int before = s.Kernel.Processes.Resolve(s.Owner).Value!.Capabilities.Items.Count;
        Assert.Equal(KernelError.PlatformDenied, s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain,
            parent.ConfigureCapability, new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | (VirtualDomainAuthorityClass)0x4000)).Error);
        Assert.Equal(before, s.Kernel.Processes.Resolve(s.Owner).Value!.Capabilities.Items.Count);
        Assert.True(s.Kernel.RevokeCapability(parent.ConfigureCapability).IsSuccess);
        int afterRevoke = s.Kernel.Processes.Resolve(s.Owner).Value!.Capabilities.Items.Count;
        Assert.Equal(KernelError.CapabilityRevoked, s.Kernel.CreateNestedVirtualDomain(s.Owner, parent.Domain,
            parent.ConfigureCapability, new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).Error);
        Assert.Equal(afterRevoke, s.Kernel.Processes.Resolve(s.Owner).Value!.Capabilities.Items.Count);
        Assert.Equal(0, s.Provider.NestedCreateCalls);
        Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner));
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)]
    public void NestedLifecycleAncestorDrainRefusesAdmissionOrLatePublication(int step, bool during)
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var nested = s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.Execution)).Value!;
        Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, nested.Domain, nested.ConfigureCapability).IsSuccess);
        if (step > 0) Assert.True(s.Kernel.StartVirtualDomain(s.Owner, nested.Domain, nested.ExecuteCapability).IsSuccess);
        if (step > 1) Assert.True(s.Kernel.ParkVirtualDomain(s.Owner, nested.Domain, nested.ExecuteCapability).IsSuccess);
        var records = ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner);
        var parentRecord = Assert.Single(records, x => x.Handle == root.Domain);
        var local = Assert.Single(records, x => x.Handle == nested.Domain);
        var initial = local.State;
        int effects = 0;
        s.Provider.BeforeChildTransition = () =>
        {
            if (!during) { effects++; return; }
            effects++;
            s.Provider.BeforeChildTransition = null;
            Assert.True(s.Kernel.PlatformAuthority.TransitionChildDomain(parentRecord.ChildBinding!.Value,
                PlatformChildDomainTransition.BeginDrain).IsSuccess);
            Assert.False(s.Kernel.PlatformAuthority.CloseChildDomain(parentRecord.ChildBinding.Value).IsSuccess);
        };
        if (!during) Assert.True(s.Kernel.PlatformAuthority.TransitionChildDomain(parentRecord.ChildBinding!.Value,
            PlatformChildDomainTransition.BeginDrain).IsSuccess);
        effects = 0;
        KernelResult Run() => step switch
        {
            0 => s.Kernel.StartVirtualDomain(s.Owner, nested.Domain, nested.ExecuteCapability),
            1 => s.Kernel.ParkVirtualDomain(s.Owner, nested.Domain, nested.ExecuteCapability),
            _ => s.Kernel.ResumeVirtualDomain(s.Owner, nested.Domain, nested.ExecuteCapability),
        };
        Assert.False(Run().IsSuccess);
        Assert.Equal(initial, local.State);
        Assert.Equal(during ? 1 : 0, effects);
        Assert.False(Run().IsSuccess);
        Assert.Equal(during ? 1 : 0, effects);
        s.Provider.BeforeChildTransition = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.NestedCloseCalls);
    }

    [Fact]
    public void NestedLifecycleLocalAncestorQuarantineCannotBeBypassedByActiveBridgeIdentity()
    {
        var (s, root, mapping) = ArtifactScenario();
        var nested = s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.Execution)).Value!;

        Assert.True(s.Kernel.BindVirtualDomainExecutable(s.Owner, root.Domain, root.ExecuteCapability,
            mapping.Mapping, new byte[] { 0x56, 0x33 }, 1).IsSuccess);
        Assert.False(s.Kernel.CloseGuestRegionMapping(s.Owner, root.Domain, root.MemoryCapability, mapping.Mapping).IsSuccess);
        Assert.Equal(VirtualDomainState.Quarantined, s.Kernel.QueryVirtualDomain(s.Owner, root.Domain).Value);
        int effects = 0; s.Provider.BeforeChildTransition = () => effects++;
        var local = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner), x => x.Handle == nested.Domain);
        Assert.Equal(KernelError.PlatformFaulted,
            ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ValidateActiveAncestors(local).Error);
        Assert.True(s.Kernel.PlatformAuthority.CommitActiveChildDomainConsumer(local.ChildBinding!.Value, () => KernelResult.Ok()).IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, s.Kernel.ConfigureVirtualDomain(s.Owner, nested.Domain, nested.ConfigureCapability).Error);
        Assert.Equal(0, effects);
        Assert.Equal(VirtualDomainState.Created, local.State);
        s.Provider.BeforeChildTransition = null;
        Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess); // exact root artifact release still Missing
        Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
    }

    [Fact]
    public async Task ConcurrentNestedLifecycleDeliveryPinsClosureAfterAncestorDrainAndStop()
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var nested = s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.Execution)).Value!;
        Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, nested.Domain, nested.ConfigureCapability).IsSuccess);
        var records = ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner);
        var parent = Assert.Single(records, x => x.Handle == root.Domain);
        var local = Assert.Single(records, x => x.Handle == nested.Domain);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        s.Provider.BeforeChildTransition = () =>
        {
            s.Provider.BeforeChildTransition = null;
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        };
        var work = Task.Run(() => s.Kernel.StartVirtualDomain(s.Owner, nested.Domain, nested.ExecuteCapability));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(s.Kernel.PlatformAuthority.TransitionChildDomain(parent.ChildBinding!.Value,
                PlatformChildDomainTransition.BeginDrain).IsSuccess);
            Assert.False(s.Kernel.PlatformAuthority.CloseChildDomain(parent.ChildBinding.Value).IsSuccess);
            Assert.False(s.Kernel.PlatformAuthority.CloseChildDomain(local.ChildBinding!.Value).IsSuccess);
            Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            Assert.False(s.Kernel.QueryProcessTeardown(s.Owner).Value!.LocalReclaimCompleted);
            Assert.Equal(0, s.Provider.NestedCloseCalls);
            Assert.DoesNotContain("root-close", s.Provider.CallLog);
        }
        finally { release.Set(); }
        Assert.False((await work.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        Assert.NotEqual(VirtualDomainState.Running, local.State);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(VirtualDomainState.Closed, local.State);
        Assert.Equal(1, s.Provider.NestedCloseCalls);
    }

    [Fact]
    public void NestedSourceCommitRechecksLocalAncestorAfterEarlierAdmissionSnapshot()
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var nested = s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.Execution)).Value!;
        Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, nested.Domain, nested.ConfigureCapability).IsSuccess);
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var records = authority.ForOwner(s.Owner);
        var parent = Assert.Single(records, x => x.Handle == root.Domain);
        var leaf = Assert.Single(records, x => x.Handle == nested.Domain);
        int effects = 0; s.Provider.BeforeChildTransition = () => effects++;
        Assert.True(authority.ValidateActiveAncestors(leaf).IsSuccess);
        var result = s.Kernel.PlatformAuthority.TransitionChildDomain(leaf.ChildBinding!.Value, PlatformChildDomainTransition.Start,
            admissionConsumer: commit => { parent.State = VirtualDomainState.Quarantined; return commit(); },
            sourceCommitConsumer: commit => s.Kernel.CapabilityAuthority.CommitVirtualDomainEffect(nested.ExecuteCapability,
                new DomainId(5001), s.Owner.Generation, CapabilityRights.Execute,
                VirtualizationResourceIds.Domain(nested.Domain.DomainId),
                () => authority.CommitEffectAdmission(leaf, VirtualDomainState.Configured, commit)));
        Assert.False(result.IsSuccess);
        Assert.Equal(0, effects);
        Assert.Equal(VirtualDomainState.Configured, leaf.State);
        s.Provider.BeforeChildTransition = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Fact]
    public async Task VirtualStateSetterSharesActualOwnerCommitGate()
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var nested = s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle)).Value!;
        Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, nested.Domain, nested.ConfigureCapability).IsSuccess);
        var authority = (VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains");
        var records = authority.ForOwner(s.Owner);
        var parent = Assert.Single(records, x => x.Handle == root.Domain);
        var leaf = Assert.Single(records, x => x.Handle == nested.Domain);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var mutationEntered = new ManualResetEventSlim();
        var work = Task.Run(() => authority.CommitEffectAdmission(leaf, VirtualDomainState.Configured, () =>
        {
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); return KernelResult.Ok();
        }));
        Task mutation = Task.CompletedTask;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var gate = ArtifactField(authority, "_gate");
            bool acquired = Monitor.TryEnter(gate);
            if (acquired) Monitor.Exit(gate);
            Assert.False(acquired);
            mutation = Task.Run(() => { mutationEntered.Set(); parent.State = VirtualDomainState.Quarantined; });
            Assert.True(mutationEntered.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(mutation.IsCompleted);
        }
        finally { release.Set(); }
        Assert.True((await work.WaitAsync(TimeSpan.FromSeconds(15))).IsSuccess);
        await mutation.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(VirtualDomainState.Quarantined, parent.State);
        Assert.False(authority.CommitEffectAdmission(leaf, VirtualDomainState.Configured, () => KernelResult.Ok()).IsSuccess);
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrapSourceRevokeRefusesCallbackOrLateObservation(bool during)
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        Action revoke = () => Assert.True(s.Kernel.RevokeCapability(leaf.TrapCapability).IsSuccess);
        if (during) s.Provider.AfterChildTrap = revoke; else revoke();
        var result = s.Kernel.ObserveVirtualTrap(s.Owner, leaf.Domain, leaf.TrapCapability);
        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(during ? 1 : 0, s.Provider.TrapCalls);
        Assert.Equal(KernelError.CapabilityRevoked, s.Kernel.ObserveVirtualTrap(s.Owner, leaf.Domain, leaf.TrapCapability).Error);
        Assert.Equal(during ? 1 : 0, s.Provider.TrapCalls);
        var record = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner), x => x.Handle == leaf.Domain);
        var table = (global::System.Collections.IDictionary)ArtifactField(s.Kernel.PlatformAuthority, "_childBindings");
        var bridgeRecord = table[record.ChildBinding!.Value.BindingId]!;
        Assert.Equal(during ? 1UL : 0UL, (ulong)bridgeRecord.GetType().GetProperty("LastTrapSequence")!.GetValue(bridgeRecord)!);
        Assert.Equal(0, (int)bridgeRecord.GetType().GetProperty("PendingChildEffects")!.GetValue(bridgeRecord)!);
        s.Provider.AfterChildTrap = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.NestedCloseCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConcurrentTrapSourceRevokeOrStopKeepsPendingUntilLatePublication(bool stop)
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        s.Provider.AfterChildTrap = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        var work = Task.Run(() => s.Kernel.ObserveVirtualTrap(s.Owner, leaf.Domain, leaf.TrapCapability));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(KernelError.PlatformBindingActive, s.Kernel.ObserveVirtualTrap(s.Owner, leaf.Domain, leaf.TrapCapability).Error);
            if (stop) Assert.False(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
            else Assert.True(s.Kernel.RevokeCapability(leaf.TrapCapability).IsSuccess);
            Assert.Equal(0, s.Provider.NestedCloseCalls);
        }
        finally { release.Set(); }
        var result = await work.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(result.IsSuccess);
        if (!stop) Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(1, s.Provider.TrapCalls);
        s.Provider.AfterChildTrap = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.NestedCloseCalls);
    }

    [Fact]
    public void TrapPublicationRechecksActualLocalAncestorDespiteActiveBridgeIdentity()
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        var parent = Assert.Single(((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner), x => x.Handle == root.Domain);
        s.Provider.AfterChildTrap = () => parent.State = VirtualDomainState.Quarantined;
        var result = s.Kernel.ObserveVirtualTrap(s.Owner, leaf.Domain, leaf.TrapCapability);
        Assert.False(result.IsSuccess);
        Assert.Equal(1, s.Provider.TrapCalls);
        Assert.False(s.Kernel.ObserveVirtualTrap(s.Owner, leaf.Domain, leaf.TrapCapability).IsSuccess);
        Assert.Equal(1, s.Provider.TrapCalls);
        s.Provider.AfterChildTrap = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.NestedCloseCalls);
    }

    private static (Scenario Scenario, VirtualDomainAuthoritySet Root, VirtualDomainAuthoritySet Leaf) NestedDeliveryScenario()
    {
        var s = CreateScenario();
        var root = CreateConfiguredRoot(s);
        var leaf = s.Kernel.CreateNestedVirtualDomain(s.Owner, root.Domain, root.ConfigureCapability,
            new(new(1, 4096), VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.Execution |
                VirtualDomainAuthorityClass.Events | VirtualDomainAuthorityClass.Traps)).Value!;
        Assert.True(s.Kernel.ConfigureVirtualDomain(s.Owner, leaf.Domain, leaf.ConfigureCapability).IsSuccess);
        Assert.True(s.Kernel.StartVirtualDomain(s.Owner, leaf.Domain, leaf.ExecuteCapability).IsSuccess);
        return (s, root, leaf);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void NestedBridgeDeliveryAncestorDrainRefusesBeforeCallbackOrReceiptPublication(bool trap, bool during)
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        var records = ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner);
        var parent = Assert.Single(records, x => x.Handle == root.Domain);
        var local = Assert.Single(records, x => x.Handle == leaf.Domain);
        var bridge = s.Kernel.PlatformAuthority;
        Action drain = () => Assert.True(bridge.TransitionChildDomain(parent.ChildBinding!.Value, PlatformChildDomainTransition.BeginDrain).IsSuccess);
        if (!during) drain();
        else if (trap) s.Provider.AfterChildTrap = drain; else s.Provider.AfterChildEvent = drain;
        bool Run() => trap ? bridge.ObserveChildTrap(local.ChildBinding!.Value).IsSuccess :
            bridge.InjectChildEvent(local.ChildBinding!.Value, PlatformVirtualEventClass.ExternalSignal,
                VirtualizationResourceIds.Events(leaf.Domain.DomainId)).IsSuccess;
        Assert.False(Run());
        Assert.Equal(during ? 1 : 0, trap ? s.Provider.TrapCalls : s.Provider.EventCalls);
        Assert.False(Run());
        Assert.Equal(during ? 1 : 0, trap ? s.Provider.TrapCalls : s.Provider.EventCalls);
        var table = (global::System.Collections.IDictionary)ArtifactField(bridge, "_childBindings");
        var ownerRecord = table[local.ChildBinding!.Value.BindingId]!;
        Assert.Equal(during ? 1UL : 0UL, ownerRecord.GetType().GetProperty(trap ? "LastTrapSequence" : "LastEventSequence")!.GetValue(ownerRecord));
        Assert.Equal(0, ownerRecord.GetType().GetProperty("PendingChildEffects")!.GetValue(ownerRecord));
        s.Provider.AfterChildTrap = s.Provider.AfterChildEvent = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess); // exact known model effect has real cleanup
        Assert.Equal(1, s.Provider.NestedCloseCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConcurrentNestedBridgeDeliveryKeepsClosurePinnedUntilCallbackEnds(bool trap)
    {
        var (s, root, leaf) = NestedDeliveryScenario();
        var records = ((VirtualDomainAuthority)ArtifactField(s.Kernel, "_virtualDomains")).ForOwner(s.Owner);
        var parent = Assert.Single(records, x => x.Handle == root.Domain);
        var local = Assert.Single(records, x => x.Handle == leaf.Domain);
        var bridge = s.Kernel.PlatformAuthority;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        Action hold = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(15))); };
        if (trap) s.Provider.AfterChildTrap = hold; else s.Provider.AfterChildEvent = hold;
        var work = Task.Run(() => trap ? bridge.ObserveChildTrap(local.ChildBinding!.Value).IsSuccess :
            bridge.InjectChildEvent(local.ChildBinding!.Value, PlatformVirtualEventClass.ExternalSignal,
                VirtualizationResourceIds.Events(leaf.Domain.DomainId)).IsSuccess);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(bridge.TransitionChildDomain(parent.ChildBinding!.Value, PlatformChildDomainTransition.BeginDrain).IsSuccess);
            Assert.Equal(KernelError.PlatformBindingActive, bridge.CloseChildDomain(parent.ChildBinding.Value).Error);
            Assert.Equal(KernelError.PlatformBindingActive, bridge.CloseChildDomain(local.ChildBinding!.Value).Error);
            Assert.Equal(0, s.Provider.NestedCloseCalls);
        }
        finally { release.Set(); }
        Assert.False(await work.WaitAsync(TimeSpan.FromSeconds(15)));
        s.Provider.AfterChildTrap = s.Provider.AfterChildEvent = null;
        Assert.True(s.Kernel.TerminateProcess(s.Owner).IsSuccess);
        Assert.Equal(1, s.Provider.NestedCloseCalls);
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
        public bool ThrowAfterNestedMaterialize { get; set; }
        public Action? AfterNestedMaterialize { get; set; }
        public Action? BeforeChildTransition { get; set; }
        public bool ThrowAfterArtifactBind { get; set; }
        public bool ThrowAfterArtifactStart { get; set; }
        public Action? AfterArtifactBind { get; set; }
        public Action? AfterArtifactStart { get; set; }
        public int ArtifactStartCalls { get; private set; }
        public int ArtifactBindCalls { get; private set; }
        public ReadOnlyMemory<byte> LastArtifactPackage { get; private set; }
        public PlatformAuthorityStatus? ArtifactStatus { get; set; }
        public bool MalformedArtifactEvidence { get; set; }
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
            AfterNestedMaterialize?.Invoke();
            if (ThrowAfterNestedMaterialize)
                throw new InvalidOperationException("Nested child materialized without receipt.");
            return PlatformAuthorityResult<PlatformProviderNestedDomainLease>.Ok(new(LastNestedLease,
                _wrongNestedImmediateParent ? new PlatformProviderChildDomainLeaseId(999) : request.ParentChildLease.LeaseId,
                request.ParentChildLease.Generation));
        }

        public PlatformAuthorityResult TransitionChildDomain(PlatformProviderChildDomainLease lease,
            PlatformChildDomainTransition transition)
        {
            BeforeChildTransition?.Invoke();
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

        public int EventCalls { get; private set; }
        public Action? AfterChildEvent { get; set; }
        public Action? AfterChildTrap { get; set; }
        public PlatformAuthorityResult<PlatformVirtualEventReceipt> InjectVirtualEvent(PlatformVirtualEventRequest request)
        {
            EventCalls++;
            var receipt = new PlatformVirtualEventReceipt(request.ChildLease.LeaseId,
                request.ChildLease.Generation, request.ChildLease.ParentDomainLease.LeaseId,
                request.ChildLease.ParentDomainLease.Generation, 1, request.EventClass, request.SourceResourceId);
            AfterChildEvent?.Invoke();
            return PlatformAuthorityResult<PlatformVirtualEventReceipt>.Ok(receipt);
        }

        public PlatformAuthorityResult<PlatformVirtualTrapEvidence> ObserveVirtualTrap(
            PlatformProviderChildDomainLease lease)
        {
            TrapCalls++;
            var sequence = _trapSequences.GetValueOrDefault(lease.LeaseId) + 1;
            _trapSequences[lease.LeaseId] = sequence;
            AfterChildTrap?.Invoke();
            return PlatformAuthorityResult<PlatformVirtualTrapEvidence>.Ok(new(lease.LeaseId,
                _malformedTrap ? new(lease.Generation.Value + 1) : lease.Generation,
                lease.ParentDomainLease.LeaseId, lease.ParentDomainLease.Generation,
                sequence, PlatformVirtualTrapKind.Timer));
        }

        public PlatformAuthorityResult<PlatformExecutableArtifactReceipt> BindExecutableArtifact(
            PlatformExecutableArtifactRequest request)
        {
            ArtifactBindCalls++;
            if (ArtifactStatus is { } status) return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Fail(status, "controlled admission disposition");
            LastArtifactPackage = request.ImmutablePackage;
            var receipt = new PlatformExecutableArtifactReceipt(new(1), new(1),
                request.ChildLease.LeaseId, request.ChildLease.Generation,
                request.GuestMapping.LeaseId, request.GuestMapping.Generation,
                request.ChildLease.ParentDomainLease.LeaseId, request.ChildLease.ParentDomainLease.Generation,
                MalformedArtifactEvidence ? new string('a', 64) : Convert.ToHexStringLower(global::System.Security.Cryptography.SHA256.HashData(request.ImmutablePackage.Span)), request.MaximumExecutionSteps);
            if (ThrowAfterArtifactBind)
                throw new InvalidOperationException("Artifact binding receipt lost.");
            AfterArtifactBind?.Invoke();
            return PlatformAuthorityResult<PlatformExecutableArtifactReceipt>.Ok(receipt);
        }

        public PlatformAuthorityResult<PlatformChildExecutionReceipt> StartExecutableArtifact(
            PlatformChildExecutionStartRequest request)
        {
            ArtifactStartCalls++;
            if (ArtifactStatus is { } status) return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Fail(status, "controlled start disposition");
            LastExecutionReceipt = new(request.Artifact.ArtifactId, request.Artifact.ArtifactGeneration,
                request.Artifact.ChildLeaseId, request.Artifact.ChildGeneration, request.Artifact.MappingLeaseId,
                request.Artifact.MappingGeneration, request.Artifact.ParentLeaseId, request.Artifact.ParentGeneration,
                request.Artifact.ContentDigest, request.OperationId, request.OperationGeneration, new(1), 1, 1, 0, true);
            if (MalformedArtifactEvidence) LastExecutionReceipt = LastExecutionReceipt.Value with { IsTerminal = false };
            if (ThrowAfterArtifactStart)
                throw new InvalidOperationException("Artifact start receipt lost.");
            AfterArtifactStart?.Invoke();
            return PlatformAuthorityResult<PlatformChildExecutionReceipt>.Ok(LastExecutionReceipt.Value);
        }
    }
}


