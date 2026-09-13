using System.Xml.Linq;
using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.FileSystem;
using SingPlus.Sip.Networking;
using SingPlus.Sip.Process;
using SingPlus.System;

namespace SingPlus.Tests.NativeServices;

public sealed class NativeSystemServiceVerticalSliceTests
{
    [Fact]
    public async Task ProcessFacadeUsesGeneratedClientSessionAndExactChildAuthority()
    {
        var kernel = new RuntimeKernel();
        var protocol = IProcessServiceProtocol.CreateDefinition();
        var service = AdmitNativeComponent(kernel, 2000, 20000, "process", "process-positive-component", protocol, IProcessServiceResponseProtocol.Definition, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Process;
        var caller = TestFixtures.Create(kernel, 2001, 20010).Handle;
        var create = kernel.MintCapability(new DomainId(20010), caller, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Value!.CapabilityId;
        var descriptor = kernel.ResolveByServiceName("process").Value;
        var session = kernel.OpenSession(caller, descriptor, [create]).Value;
        var host = RuntimeProcessServiceHost.CreateForSession(kernel, service, session).Value!;
        var manager = new ProcessManager(IProcessServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)), create);
        var manifest = TestFixtures.Manifest(2002, 20020, 1, "native-child");

        var pendingCreate = manager.CreateAsync(manifest).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var child = await pendingCreate;
        await Dispatch(child.StartAsync(), host);
        await Dispatch(child.ParkAsync(), host);
        await Dispatch(child.ResumeAsync(), host);
        await Dispatch(child.TerminateAsync(), host);
        var wait = child.WaitForExitAsync().AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        Assert.Equal(ProcessState.Exited, await wait);

        var forged = new ProcessCommand(child.Authority.Process, create);
        var forgedTask = IProcessServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)).StartAsync(forged).AsTask();
        Assert.Equal(KernelError.InsufficientRights, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => forgedTask);
    }

    [Fact]
    public async Task ProcessCreationDelegatesExactSubsetBeforeAdmissionAndRejectsBroadeningBeforeChildEffect()
    {
        var kernel = new RuntimeKernel();
        var service = TestFixtures.Create(kernel, 2040, 20400).Handle;
        var caller = TestFixtures.Create(kernel, 2041, 20410).Handle;
        var create = kernel.MintCapability(new DomainId(20410), caller, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Value!.CapabilityId;
        var source = kernel.MintCapability(new DomainId(20410), caller, ResourceKind.KernelService, "child-clock", CapabilityRights.Read | CapabilityRights.Delegate).Value!.CapabilityId;
        var protocol = IProcessServiceProtocol.CreateDefinition();
        var descriptor = kernel.RegisterService(service, "process-admission", Contract(protocol), protocol, IProcessServiceResponseProtocol.Definition, [new(ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute)]).Value!;
        var session = kernel.OpenSession(caller, descriptor, [create]).Value;
        var host = RuntimeProcessServiceHost.CreateForSession(kernel, service, session).Value!;
        var manager = new ProcessManager(IProcessServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)), create);
        var requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "child-clock", CapabilityRights.Read);
        var manifest = TestFixtures.Manifest(2042, 20420, 1, "admitted-with-delegation", capabilities: [requirement]);

        var creating = manager.CreateAsync(manifest, initialCapabilities: [new(source, CapabilityRights.Read)]).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var child = await creating;
        Assert.True(kernel.Processes.Resolve(child.Authority.Process).IsSuccess);

        var broadenedManifest = TestFixtures.Manifest(2043, 20430, 1, "rejected-broader-delegation", capabilities: [requirement]);
        var rejected = manager.CreateAsync(broadenedManifest, initialCapabilities: [new(source, CapabilityRights.Read | CapabilityRights.Write)]).AsTask();
        Assert.Equal(KernelError.DelegationDenied, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rejected);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new(new ProcessId(2043), 1)).Error);
    }

    [Fact]
    public async Task WaitForExitPreservesExactFaultedTerminalReceipt()
    {
        var kernel = new RuntimeKernel();
        var service = TestFixtures.Create(kernel, 2050, 20500).Handle;
        var caller = TestFixtures.Create(kernel, 2051, 20510).Handle;
        var create = kernel.MintCapability(new DomainId(20510), caller, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Value!.CapabilityId;
        var protocol = IProcessServiceProtocol.CreateDefinition();
        var descriptor = kernel.RegisterService(service, "process-terminal", Contract(protocol), protocol, IProcessServiceResponseProtocol.Definition, [new(ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute)]).Value!;
        var session = kernel.OpenSession(caller, descriptor, [create]).Value;
        var host = RuntimeProcessServiceHost.CreateForSession(kernel, service, session).Value!;
        var manager = new ProcessManager(IProcessServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)), create);
        var creating = manager.CreateAsync(TestFixtures.Manifest(2052, 20520, 1, "faulted-child")).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var child = await creating;

        Assert.True(kernel.FaultProcess(child.Authority.Process).IsSuccess);
        var waiting = child.WaitForExitAsync().AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        Assert.Equal(ProcessState.Faulted, await waiting);
    }

    [Fact]
    public async Task UnsupportedIndependentChildPolicyFailsBeforeProcessCreation()
    {
        var kernel = new RuntimeKernel();
        var service = TestFixtures.Create(kernel, 2060, 20600).Handle;
        var caller = TestFixtures.Create(kernel, 2061, 20610).Handle;
        var create = kernel.MintCapability(new DomainId(20610), caller, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Value!.CapabilityId;
        var protocol = IProcessServiceProtocol.CreateDefinition();
        var descriptor = kernel.RegisterService(service, "process-attached-only", Contract(protocol), protocol, IProcessServiceResponseProtocol.Definition, [new(ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute)]).Value!;
        var session = kernel.OpenSession(caller, descriptor, [create]).Value;
        var host = RuntimeProcessServiceHost.CreateForSession(kernel, service, session).Value!;
        var manager = new ProcessManager(IProcessServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)), create);

        var rejected = manager.CreateAsync(TestFixtures.Manifest(2062, 20620, 1, "unsupported-independent"), ChildProcessPolicy.Independent).AsTask();

        Assert.Equal(KernelError.PlatformUnsupported, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rejected);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new(new ProcessId(2062), 1)).Error);
    }

    [Fact]
    public async Task FileFacadeUsesObjectGenerationCapabilityAndSanitizingCopy()
    {
        var (kernel, service, caller, capability, session, descriptor) = FileScenario();
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session).Value!;
        var facade = new SingPlus.System.FileSystem(IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)), capability);
        var open = facade.OpenAsync("/service-owned/path", create: true).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var file = await open;
        byte[] source = [1, 2, 3];
        var write = file.WriteAsync(source).AsTask();
        source[0] = 99;
        Assert.True(host.ProcessNext().IsSuccess);
        await write;
        var read = file.ReadAsync().AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        Assert.Equal([1, 2, 3], await read);
        await Dispatch(file.CloseAsync(), host);

        var staleRead = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)).ReadAsync(new(file.Authority.File, file.Authority.Capability)).AsTask();
        Assert.Equal(KernelError.StaleHandle, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => staleRead);
        Assert.True(kernel.SetServiceAvailability(descriptor.Service, ServiceAvailability.Draining).IsSuccess);
        Assert.Equal(KernelError.ServiceUnavailable, kernel.OpenSession(caller, descriptor, [capability]).Error);
    }

    [Fact]
    public async Task NetworkFacadeUsesSocketAuthorityAndNoDeviceAuthority()
    {
        var kernel = new RuntimeKernel();
        var protocol = INetworkServiceProtocol.CreateDefinition();
        var service = AdmitNativeComponent(kernel, 2020, 20200, "network", "network-positive-component", protocol, INetworkServiceResponseProtocol.Definition, ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure).Process;
        var caller = TestFixtures.Create(kernel, 2021, 20210).Handle;
        var endpoint = kernel.MintCapability(new DomainId(20210), caller, ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure).Value!.CapabilityId;
        var descriptor = kernel.ResolveByServiceName("network").Value;
        var session = kernel.OpenSession(caller, descriptor, [endpoint]).Value;
        var host = RuntimeNetworkServiceHost.CreateForSession(kernel, service, session).Value!;
        var network = new Network(INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)), endpoint);
        var opening = network.OpenAsync("loopback:test").AsTask(); Assert.True(host.ProcessNext().IsSuccess); var socket = await opening;
        await Dispatch(socket.SendAsync(new byte[] { 7, 8, 9 }), host);
        var receive = socket.ReceiveAsync().AsTask(); Assert.True(host.ProcessNext().IsSuccess); Assert.Equal([7, 8, 9], await receive);
        Assert.DoesNotContain(kernel.CapabilityAuthority.SnapshotForDomain(new DomainId(20210)), capability => capability.ResourceKind == ResourceKind.Device);
    }

    [Fact]
    public async Task FileObjectAuthorityCannotCrossEndpointSessionsEvenWhenCallerAndObjectIdMatch()
    {
        var (kernel, service, caller, capability, firstSession, descriptor) = FileScenario();
        var secondSession = kernel.OpenSession(caller, descriptor, [capability]).Value;
        var firstHost = RuntimeFileServiceHost.CreateForSession(kernel, service, firstSession).Value!;
        var secondHost = RuntimeFileServiceHost.CreateForSession(kernel, service, secondSession).Value!;
        var firstClient = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, firstSession));
        var secondClient = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, secondSession));

        var firstOpen = firstClient.OpenAsync(new(capability, "/first", true)).AsTask();
        Assert.True(firstHost.ProcessNext().IsSuccess);
        var first = (await firstOpen).Authority;
        var secondOpen = secondClient.OpenAsync(new(capability, "/second", true)).AsTask();
        Assert.True(secondHost.ProcessNext().IsSuccess);
        var second = (await secondOpen).Authority;

        Assert.Equal(first.File.ObjectId, second.File.ObjectId);
        Assert.NotEqual(first.File.Session, second.File.Session);
        var crossSession = secondClient.ReadAsync(new(first.File, first.Capability)).AsTask();
        Assert.Equal(KernelError.WrongSessionOwner, secondHost.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => crossSession);
    }

    [Fact]
    public async Task SocketAuthorityCannotCrossEndpointSessionsEvenWhenCallerAndObjectIdMatch()
    {
        var kernel = new RuntimeKernel();
        var service = TestFixtures.Create(kernel, 2030, 20300).Handle;
        var caller = TestFixtures.Create(kernel, 2031, 20310).Handle;
        var capability = kernel.MintCapability(new DomainId(20310), caller, ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure).Value!.CapabilityId;
        var protocol = INetworkServiceProtocol.CreateDefinition();
        var descriptor = kernel.RegisterService(service, "network-session-binding", Contract(protocol), protocol, INetworkServiceResponseProtocol.Definition, [new(ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure)]).Value!;
        var firstSession = kernel.OpenSession(caller, descriptor, [capability]).Value;
        var secondSession = kernel.OpenSession(caller, descriptor, [capability]).Value;
        var firstHost = RuntimeNetworkServiceHost.CreateForSession(kernel, service, firstSession).Value!;
        var secondHost = RuntimeNetworkServiceHost.CreateForSession(kernel, service, secondSession).Value!;
        var firstClient = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, firstSession));
        var secondClient = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, secondSession));

        var firstOpen = firstClient.OpenAsync(new(capability, "first")).AsTask();
        Assert.True(firstHost.ProcessNext().IsSuccess);
        var first = (await firstOpen).Authority;
        var secondOpen = secondClient.OpenAsync(new(capability, "second")).AsTask();
        Assert.True(secondHost.ProcessNext().IsSuccess);
        var second = (await secondOpen).Authority;

        Assert.Equal(first.Socket.ObjectId, second.Socket.ObjectId);
        Assert.NotEqual(first.Socket.Session, second.Socket.Session);
        var crossSession = secondClient.ReceiveAsync(new(first.Socket, first.Capability)).AsTask();
        Assert.Equal(KernelError.WrongSessionOwner, secondHost.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => crossSession);
    }

    [Fact]
    public async Task LateSourceCancellationCannotOrphanCommittedObjectAuthority()
    {
        var (kernel, service, caller, capability, session, _) = FileScenario();
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session).Value!;
        var facade = new SingPlus.System.FileSystem(IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)), capability);
        using var cancellation = new CancellationTokenSource();
        var wait = facade.OpenAsync("/still-commits", true, cancellation.Token).AsTask();
        cancellation.Cancel();
        Assert.False(wait.IsCompleted);
        Assert.True(host.ProcessNext().IsSuccess);
        var file = await wait;
        Assert.True(kernel.ValidateCapability(caller, file.Authority.Capability, CapabilityRights.Read).IsSuccess);
    }

    [Fact]
    public async Task ServiceCrashDrainsObjectsBeforeClosingSessionsAndReclaimsAttachedChildren()
    {
        var (kernel, fileService, caller, fileCapability, fileSession, _) = FileScenario();
        var fileHost = RuntimeFileServiceHost.CreateForSession(kernel, fileService, fileSession).Value!;
        var fileClient = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, fileSession));
        var opening = fileClient.OpenAsync(new(fileCapability, "/crash-owned", true)).AsTask();
        Assert.True(fileHost.ProcessNext().IsSuccess);
        var file = (await opening).Authority;

        var fileCrash = kernel.FaultComponent(new ComponentIdentity("file-positive-component"));
        Assert.True(fileCrash.IsSuccess, fileCrash.Message);
        Assert.Equal(ComponentLifecycleState.Reclaimable, fileCrash.Value!.State);
        Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(caller, file.Capability, CapabilityRights.Read).Error);
        var afterCrash = fileClient.ReadAsync(new(file.File, file.Capability)).AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() => afterCrash);

        var protocol = IProcessServiceProtocol.CreateDefinition();
        var processService = AdmitNativeComponent(kernel, 2060, 20600, "process-crash", "process-crash-component", protocol, IProcessServiceResponseProtocol.Definition, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Process;
        var create = kernel.MintCapability(new DomainId(20110), caller, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Value!.CapabilityId;
        var descriptor = kernel.ResolveByServiceName("process-crash").Value;
        var processSession = kernel.OpenSession(caller, descriptor, [create]).Value;
        var processHost = RuntimeProcessServiceHost.CreateForSession(kernel, processService, processSession).Value!;
        var manager = new ProcessManager(IProcessServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, processSession)), create);
        var creating = manager.CreateAsync(TestFixtures.Manifest(2061, 20610, 1, "attached-crash-child")).AsTask();
        Assert.True(processHost.ProcessNext().IsSuccess);
        var child = await creating;

        var processCrash = kernel.FaultComponent(new ComponentIdentity("process-crash-component"));
        Assert.True(processCrash.IsSuccess, processCrash.Message);
        Assert.Equal(ComponentLifecycleState.Reclaimable, processCrash.Value!.State);
        Assert.Equal(ProcessState.Exited, kernel.Processes.ResolveTerminalState(child.Authority.Process).Value);
        Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(caller, child.Authority.ControlCapability, CapabilityRights.Configure).Error);
    }

    [Fact]
    public void AllNativeServiceFamiliesAreAdmittedAsDeclarativeComponents()
    {
        var kernel = new RuntimeKernel();
        var process = AdmitNativeComponent(kernel, 2070, 20700, "phase8-process", "process-component", IProcessServiceProtocol.CreateDefinition(), IProcessServiceResponseProtocol.Definition, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute);
        var file = AdmitNativeComponent(kernel, 2071, 20710, "phase8-file", "file-component", IFileServiceProtocol.CreateDefinition(), IFileServiceResponseProtocol.Definition, ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write);
        var network = AdmitNativeComponent(kernel, 2072, 20720, "phase8-network", "network-component", INetworkServiceProtocol.CreateDefinition(), INetworkServiceResponseProtocol.Definition, ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure);

        Assert.Equal(ComponentLifecycleState.Running, process.State);
        Assert.Equal(ComponentLifecycleState.Running, file.State);
        Assert.Equal(ComponentLifecycleState.Running, network.State);
        Assert.Equal("phase8-process", kernel.ResolveByServiceName("phase8-process").Value.Service.Name);
        Assert.Equal("phase8-file", kernel.ResolveByServiceName("phase8-file").Value.Service.Name);
        Assert.Equal("phase8-network", kernel.ResolveByServiceName("phase8-network").Value.Service.Name);
    }

    [Fact]
    public async Task PendingRequestsFaultWithoutEffectWhenEachServiceFamilyCrashesBeforeAcceptance()
    {
        var kernel = new RuntimeKernel();
        var caller = TestFixtures.Create(kernel, 2080, 20800).Handle;

        var processProtocol = IProcessServiceProtocol.CreateDefinition();
        var processService = AdmitNativeComponent(kernel, 2081, 20810, "pending-process", "pending-process-component", processProtocol, IProcessServiceResponseProtocol.Definition, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Process;
        var processCapability = kernel.MintCapability(new DomainId(20800), caller, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Value!.CapabilityId;
        var processSession = kernel.OpenSession(caller, kernel.ResolveByServiceName("pending-process").Value, [processCapability]).Value;
        _ = RuntimeProcessServiceHost.CreateForSession(kernel, processService, processSession).Value!;
        var processClient = IProcessServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, processSession));
        var processRequest = processClient.CreateAsync(new(processCapability, TestFixtures.Manifest(2082, 20820, 1, "never-admitted"), ChildProcessPolicy.Attached)).AsTask();
        Assert.True(kernel.FaultComponent(new ComponentIdentity("pending-process-component")).IsSuccess);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processRequest);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new(new ProcessId(2082), 1)).Error);

        var fileProtocol = IFileServiceProtocol.CreateDefinition();
        var fileService = AdmitNativeComponent(kernel, 2083, 20830, "pending-file", "pending-file-component", fileProtocol, IFileServiceResponseProtocol.Definition, ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write).Process;
        var fileCapability = kernel.MintCapability(new DomainId(20800), caller, ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var fileSession = kernel.OpenSession(caller, kernel.ResolveByServiceName("pending-file").Value, [fileCapability]).Value;
        _ = RuntimeFileServiceHost.CreateForSession(kernel, fileService, fileSession).Value!;
        var fileRequest = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, fileSession)).OpenAsync(new(fileCapability, "/never-opened", true)).AsTask();
        Assert.True(kernel.FaultComponent(new ComponentIdentity("pending-file-component")).IsSuccess);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fileRequest);
        Assert.DoesNotContain(kernel.CapabilityAuthority.SnapshotForDomain(new DomainId(20800)), capability => capability.ResourceKind == ResourceKind.File && capability.ResourceId.StartsWith("file-object:", StringComparison.Ordinal));

        var networkProtocol = INetworkServiceProtocol.CreateDefinition();
        var networkService = AdmitNativeComponent(kernel, 2084, 20840, "pending-network", "pending-network-component", networkProtocol, INetworkServiceResponseProtocol.Definition, ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure).Process;
        var networkCapability = kernel.MintCapability(new DomainId(20800), caller, ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure).Value!.CapabilityId;
        var networkSession = kernel.OpenSession(caller, kernel.ResolveByServiceName("pending-network").Value, [networkCapability]).Value;
        _ = RuntimeNetworkServiceHost.CreateForSession(kernel, networkService, networkSession).Value!;
        var networkRequest = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, networkSession)).OpenAsync(new(networkCapability, "never-opened")).AsTask();
        Assert.True(kernel.FaultComponent(new ComponentIdentity("pending-network-component")).IsSuccess);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => networkRequest);
        Assert.DoesNotContain(kernel.CapabilityAuthority.SnapshotForDomain(new DomainId(20800)), capability => capability.ResourceKind == ResourceKind.Network && capability.ResourceId.StartsWith("socket-object:", StringComparison.Ordinal));
    }

    [Fact]
    public void DrainingRejectsNewSessionsForEveryNativeServiceFamily()
    {
        var kernel = new RuntimeKernel();
        var caller = TestFixtures.Create(kernel, 2090, 20900).Handle;
        var cases = new[]
        {
            (Name: "draining-process", Snapshot: AdmitNativeComponent(kernel, 2091, 20910, "draining-process", "draining-process-component", IProcessServiceProtocol.CreateDefinition(), IProcessServiceResponseProtocol.Definition, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute), Kind: ResourceKind.Process, Resource: CapabilityResourceIds.ProcessCreate, Rights: CapabilityRights.Execute),
            (Name: "draining-file", Snapshot: AdmitNativeComponent(kernel, 2092, 20920, "draining-file", "draining-file-component", IFileServiceProtocol.CreateDefinition(), IFileServiceResponseProtocol.Definition, ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read), Kind: ResourceKind.File, Resource: CapabilityResourceIds.FileNamespace, Rights: CapabilityRights.Read),
            (Name: "draining-network", Snapshot: AdmitNativeComponent(kernel, 2093, 20930, "draining-network", "draining-network-component", INetworkServiceProtocol.CreateDefinition(), INetworkServiceResponseProtocol.Definition, ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure), Kind: ResourceKind.Network, Resource: CapabilityResourceIds.NetworkEndpoint, Rights: CapabilityRights.Configure),
        };

        foreach (var item in cases)
        {
            var descriptor = kernel.ResolveByServiceName(item.Name).Value;
            var capability = kernel.MintCapability(new DomainId(20900), caller, item.Kind, item.Resource, item.Rights).Value!.CapabilityId;
            Assert.True(kernel.SetServiceAvailability(descriptor.Service, ServiceAvailability.Draining).IsSuccess);
            Assert.Equal(KernelError.ServiceUnavailable, kernel.OpenSession(caller, descriptor, [capability]).Error);
        }
    }

    [Fact]
    public async Task ExactSessionCloseDrainsItsObjectsWithoutAffectingSiblingSession()
    {
        var (kernel, service, caller, capability, firstSession, descriptor) = FileScenario();
        var secondSession = kernel.OpenSession(caller, descriptor, [capability]).Value;
        var firstHost = RuntimeFileServiceHost.CreateForSession(kernel, service, firstSession).Value!;
        var secondHost = RuntimeFileServiceHost.CreateForSession(kernel, service, secondSession).Value!;
        var firstClient = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, firstSession));
        var secondClient = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, secondSession));
        var firstOpen = firstClient.OpenAsync(new(capability, "/first-close", true)).AsTask();
        Assert.True(firstHost.ProcessNext().IsSuccess);
        var first = (await firstOpen).Authority;
        var secondOpen = secondClient.OpenAsync(new(capability, "/second-live", true)).AsTask();
        Assert.True(secondHost.ProcessNext().IsSuccess);
        var second = (await secondOpen).Authority;

        Assert.True(kernel.CloseSession(caller, firstSession).IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(caller, first.Capability, CapabilityRights.Read).Error);
        Assert.True(kernel.ValidateCapability(caller, second.Capability, CapabilityRights.Read).IsSuccess);
        var readSecond = secondClient.ReadAsync(new(second.File, second.Capability)).AsTask();
        Assert.True(secondHost.ProcessNext().IsSuccess);
        Assert.Empty((await readSecond).Data.ToArray());
    }

    [Fact]
    public async Task GeneratedClientRejectsMalformedResponseBeforeFacadePublication()
    {
        var client = IFileServiceRuntimeClient.Create(new MalformedTransport());
        var facade = new SingPlus.System.FileSystem(client, new CapabilityId(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.OpenAsync("/x").AsTask());
    }

    [Fact]
    public void SourceLibraryDependencyAndPublicSurfaceStayNeutral()
    {
        var root = FindRoot();
        var project = XDocument.Load(Path.Combine(root, "sdk", "SingPlus.System", "SingPlus.System.csproj"));
        var references = project.Descendants("ProjectReference").Select(x => ((string?)x.Attribute("Include") ?? "").Replace('\\', '/')).ToArray();
        Assert.Equal(2, references.Length);
        Assert.Contains(references, x => x.EndsWith("contracts/SingPlus.Contracts/SingPlus.Contracts.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(references, x => x.EndsWith("src/Sip/SingPlus.Sip/SingPlus.Sip.csproj", StringComparison.OrdinalIgnoreCase));
        var forbidden = new[] { "Runtime", "Platform", "HybridCpu", "Vmcs", "Vmx", "Mmio", "Irq", "Dma", "Provider" };
        foreach (var member in typeof(ProcessManager).Assembly.GetExportedTypes().SelectMany(t => t.GetMembers()))
        foreach (var word in forbidden) Assert.DoesNotContain(word, member.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static (RuntimeKernel Kernel, ProcessHandle Service, ProcessHandle Caller, CapabilityId Capability, EndpointSessionHandle Session, ServiceEndpointDescriptor Descriptor) FileScenario()
    {
        var kernel = new RuntimeKernel();
        var caller = TestFixtures.Create(kernel, 2011, 20110).Handle;
        var protocol = IFileServiceProtocol.CreateDefinition();
        var service = AdmitNativeComponent(kernel, 2012, 20120, "file", "file-positive-component", protocol, IFileServiceResponseProtocol.Definition, ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write).Process;
        var capability = kernel.MintCapability(new DomainId(20110), caller, ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var descriptor = kernel.ResolveByServiceName("file").Value;
        return (kernel, service, caller, capability, kernel.OpenSession(caller, descriptor, [capability]).Value, descriptor);
    }
    private static ServiceContractIdentity Contract(ProtocolDefinitionV1 protocol) => new(protocol.ContractName, "1", protocol.ContractDigest);
    private static ComponentLifecycleSnapshot AdmitNativeComponent(
        RuntimeKernel kernel,
        ulong processId,
        ulong domainId,
        string serviceName,
        string componentName,
        ProtocolDefinitionV1 protocol,
        ResponseProtocolDefinitionV1 response,
        ResourceKind admissionKind,
        string admissionResource,
        CapabilityRights admissionRights)
    {
        byte[] image = [(byte)(processId & 0xff), 0x53, 0x38];
        var contract = Contract(protocol);
        var provided = new ProvidedServiceManifestV1(serviceName, contract);
        var manifest = new ServiceManifestV1(
            new ComponentIdentity(componentName),
            new ComponentVersion("1"),
            Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
            TestFixtures.Manifest(processId, domainId, 1, componentName),
            [provided]);
        var registration = new ComponentProvidedServiceRegistration(
            provided,
            protocol,
            response,
            [new CapabilityRequirementV1(admissionKind, admissionResource, admissionRights)]);
        var admitted = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image, providedServices: [registration]));
        Assert.True(admitted.IsSuccess, admitted.Message);
        return admitted.Value!;
    }
    private static async Task Dispatch(ValueTask operation, dynamic host) { var task = operation.AsTask(); Assert.True(host.ProcessNext().IsSuccess); await task.WaitAsync(TimeSpan.FromSeconds(2)); }
    private static string FindRoot() { var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx"))) directory = directory.Parent; return directory!.FullName; }

    private sealed class MalformedTransport : ISipClientRuntimeTransport
    {
        public ResponseEnvelope Invoke(uint messageId, object? requestPayload = null) => throw new NotSupportedException();
        public ValueTask<ResponseEnvelope> InvokeAsync(uint messageId, object? requestPayload = null) => ValueTask.FromResult(new ResponseEnvelope(1, messageId, ResponsePublicationStatus.Published, "wrong"));
        public ValueTask<ResponseEnvelope> InvokeAsync(uint messageId, object? requestPayload, CancellationToken cancellationToken) => InvokeAsync(messageId, requestPayload);
        public ResponseEnvelope InvokeOwnershipPair(uint messageId, object firstOwnershipPayload, object secondOwnershipPayload) => throw new NotSupportedException();
        public ValueTask<ResponseEnvelope> InvokeOwnershipPairAsync(uint messageId, object firstOwnershipPayload, object secondOwnershipPayload) => throw new NotSupportedException();
        public ValueTask<ResponseEnvelope> InvokeOwnershipPairAsync(uint messageId, object firstOwnershipPayload, object secondOwnershipPayload, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
