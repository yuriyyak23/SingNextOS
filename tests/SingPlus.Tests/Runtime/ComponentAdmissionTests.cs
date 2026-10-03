using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Sip.Compute;

namespace SingPlus.Tests.Runtime;

public sealed class ComponentAdmissionTests
{
    [Theory]
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    public void ComponentObserveRetriesExactComposedPreparationTarget(int entry, bool throws)
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        byte[] image = [2, 3];
        var manifest = new ServiceManifestV1(new("composed-retry"), new("1"), Digest(image),
            TestFixtures.Manifest(1020, 2020),
            requiredContracts: entry == 0 ? [new ServiceContractIdentity("absent", "1", "absent-digest")] : [],
            requiresPlatformDomain: true);
        var process = new ProcessHandle(manifest.Process.ProcessId, manifest.Process.Generation);
        var target = entry == 2 ? ProcessState.Faulted : ProcessState.Exited;
        var calls = 0;
        kernel.RegisterComposedWorkTeardownParticipant(new ComponentClosureCallback(() =>
        {
            Assert.Equal(target, kernel.QueryProcessTeardown(process).Value!.TargetTerminalState);
            if (++calls != 1) return KernelResult.Ok();
            var overlapping = kernel.ObserveComponentTeardown(manifest.Identity);
            Assert.True(overlapping.IsSuccess);
            Assert.False(overlapping.Value!.Reclaimable);
            Assert.True(kernel.Processes.Resolve(process).IsSuccess);
            Assert.Equal(1, calls);
            if (throws) throw new InvalidOperationException("Closure receipt unavailable.");
            return KernelResult.Fail(KernelError.PlatformBindingDraining, "Existing participant is draining.");
        }));

        var admitted = kernel.AdmitComponent(new(manifest, image));
        if (entry != 0)
        {
            Assert.True(admitted.IsSuccess);
            _ = entry == 2 ? kernel.FaultComponent(manifest.Identity) : kernel.DrainComponent(manifest.Identity);
        }
        else Assert.False(admitted.IsSuccess);
        Assert.Equal(1, calls);
        Assert.False(kernel.QueryComponent(manifest.Identity).Value!.Reclaimable);
        Assert.Equal(0, provider.RevokeDomainCallCount);
        var observed = kernel.ObserveComponentTeardown(manifest.Identity);
        Assert.True(observed.IsSuccess);
        Assert.True(observed.Value!.Reclaimable);
        Assert.Equal(2, calls);
        Assert.Equal(1, provider.RevokeDomainCallCount);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(process).Error);
        Assert.Null(observed.Value.DeviceResources);
        Assert.False(observed.Value.PlatformAuthorityLive);

        var (_, next) = TestFixtures.Create(kernel, 1020, 2020, 2);
        Assert.True(kernel.ObserveComponentTeardown(manifest.Identity).Value!.Reclaimable);
        Assert.True(kernel.Processes.Resolve(next).IsSuccess);
        Assert.Equal(2, calls);
        Assert.Equal(1, provider.RevokeDomainCallCount);
    }

    [Fact]
    public async Task ConcurrentComponentObserveCannotCloseWhileRetryCallbackIsInFlight()
    {
        var kernel = new RuntimeKernel(new HostPlatformAuthorityProvider());
        byte[] image = [4];
        var manifest = new ServiceManifestV1(new("composed-overlap"), new("1"), Digest(image),
            TestFixtures.Manifest(1021, 2021), requiresPlatformDomain: true);
        var component = kernel.AdmitComponent(new(manifest, image)).Value!;
        using var resume = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        kernel.RegisterComposedWorkTeardownParticipant(new ComponentClosureCallback(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                return KernelResult.Fail(KernelError.PlatformFaulted, "Initial preparation refused.");
            entered.SetResult();
            return resume.Wait(TimeSpan.FromSeconds(10)) ? KernelResult.Ok() :
                KernelResult.Fail(KernelError.PlatformFaulted, "Test rendezvous expired.");
        }));
        Assert.Equal(KernelError.PlatformFaulted, kernel.FaultComponent(manifest.Identity).Error);
        var retry = Task.Run(() => kernel.ObserveComponentTeardown(manifest.Identity));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var overlapping = kernel.ObserveComponentTeardown(manifest.Identity);
            Assert.True(overlapping.IsSuccess);
            Assert.False(overlapping.Value!.Reclaimable);
            Assert.True(kernel.Processes.Resolve(component.Process).IsSuccess);
            Assert.Equal(ProcessState.Faulted, kernel.QueryProcessTeardown(component.Process).Value!.TargetTerminalState);
            Assert.Equal(2, Volatile.Read(ref calls));
        }
        finally { resume.Set(); }
        Assert.True((await retry.WaitAsync(TimeSpan.FromSeconds(10))).Value!.Reclaimable);
        Assert.Equal(2, calls);
    }

    private sealed class ComponentClosureCallback(Func<KernelResult> close) : IComposedWorkTeardownParticipant
    {
        public KernelResult CloseComposedWorkForProcess(ProcessHandle process, RegionOwner owner) => close();
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ProcessCreationPreflightRefusesBeforeComponentHierarchyAllocation(int fault)
    {
        var kernel = new RuntimeKernel();
        byte[] image = [1, 2, 3];
        var process = TestFixtures.Manifest(1010, 2010, identity: "preflight-process");
        if (fault == 0) TestFixtures.Create(kernel, 1010, 2010, identity: "different-entry");
        if (fault == 1) TestFixtures.Create(kernel, 1011, 2011, identity: "preflight-process");
        if (fault == 2)
        {
            var (_, newer) = TestFixtures.Create(kernel, 1010, 2010, 2);
            Assert.True(kernel.TerminateProcess(newer).IsSuccess);
        }
        var manifest = new ServiceManifestV1(new("pre-process-refusal"), new("1"), Digest(image), process);
        var refused = kernel.AdmitComponent(new(manifest, image));
        Assert.Equal(fault switch { 0 => KernelError.DuplicateProcessId, 1 => KernelError.DuplicateIdentity,
            _ => KernelError.StaleGeneration }, refused.Error);
        Assert.Equal(KernelError.ComponentNotFound, kernel.QueryComponent(manifest.Identity).Error);
        Assert.False(kernel.Budgets.HasProcessAccount(new(process.ProcessId, process.Generation)));
        var accounts = (global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority).GetField("_accounts",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel.Budgets)!;
        Assert.Single(accounts);
    }

    [Fact]
    public async Task ProcessCreationRacePreservesForeignProcessAndFaultsComponentWithoutClosureClaim()
    {
        var kernel = new RuntimeKernel();
        byte[] image = [3, 2, 1];
        var process = TestFixtures.Manifest(1012, 2012, identity: "raced-process");
        var handle = new ProcessHandle(process.ProcessId, process.Generation);
        var manifest = new ServiceManifestV1(new("raced-component"), new("1"), Digest(image), process);
        var gate = typeof(ResourceBudgetAuthority).GetField("_gate", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!.GetValue(kernel.Budgets)!;
        var completion = new TaskCompletionSource<KernelResult<ComponentLifecycleSnapshot>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { completion.SetResult(kernel.AdmitComponent(new(manifest, image))); }
            catch (Exception exception) { completion.SetException(exception); }
        }) { IsBackground = true };
        lock (gate)
        {
            worker.Start();
            Assert.True(SpinWait.SpinUntil(() => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)));
            Assert.False(completion.Task.IsCompleted);
            Assert.True(kernel.CreateProcess(process).IsSuccess);
        }
        Assert.Equal(KernelError.DuplicateProcessId, (await completion.Task.WaitAsync(TimeSpan.FromSeconds(10))).Error);
        var component = kernel.QueryComponent(manifest.Identity).Value!;
        Assert.Equal(ComponentLifecycleState.Faulted, component.State);
        Assert.False(component.Reclaimable);
        Assert.True(kernel.Processes.Resolve(handle).IsSuccess);
        Assert.True(kernel.Budgets.HasProcessAccount(handle));
        Assert.True(kernel.AllocateBuffer<byte>(handle, 4).IsSuccess);
        Assert.Equal(KernelError.ReplacementBlocked, kernel.RetireReclaimableComponent(manifest.Identity, handle).Error);
        Assert.False(kernel.FaultComponent(manifest.Identity).IsSuccess);
        Assert.True(kernel.Processes.Resolve(handle).IsSuccess);
        Assert.True(kernel.QueryBudget(component.ProcessBudget).IsSuccess);
        Assert.Equal(4ul, Assert.Single(kernel.QueryBudget(component.ProcessBudget).Value!.Usage,
            usage => usage.Dimension == ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Fact]
    public void ExistingComputeServiceContractLaunchesAsDeclarativeComponent()
    {
        var kernel = new RuntimeKernel(new HostPlatformAuthorityProvider());
        var image = new byte[] { 0x53, 0x49, 0x50 };
        var protocol = IComputeServiceProtocol.CreateDefinition();
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var provided = new ProvidedServiceManifestV1("compute", contract);
        var manifest = new ServiceManifestV1(new ComponentIdentity("compute-service"), new ComponentVersion("1"), Digest(image), TestFixtures.Manifest(11, 110), [provided], requiresPlatformDomain: true);
        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image, providedServices: [new ComponentProvidedServiceRegistration(provided, protocol, IComputeServiceResponseProtocol.Definition, [new CapabilityRequirementV1(ResourceKind.Compute, CapabilityResourceIds.Dsc1Copy, CapabilityRights.Execute)])]));

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(ComponentLifecycleState.Running, result.Value!.State);
        Assert.True(result.Value.PlatformAuthorityLive);
        Assert.Equal(contract, Assert.Single(result.Value.ProvidedContracts));
        Assert.Contains(result.Value.ResourceRequirements, requirement => requirement.Kind == ComponentResourceRequirementKind.PlatformAuthorityDomain);
        Assert.Equal(provided.ServiceName, kernel.ResolveByServiceName("compute").Value.Service.Name);
    }

    [Fact]
    public void DeclarativeComponentLaunchesAndReportsDistinctAuthorityAssociations()
    {
        var kernel = new RuntimeKernel();
        _ = TestFixtures.Create(kernel, 90, 900);
        var image = new byte[] { 1, 2, 3 };
        var requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "clock", CapabilityRights.Read);
        var process = TestFixtures.Manifest(1, 10, capabilities: [requirement]);
        var serviceContract = new ServiceContractIdentity("Clock", "1", "clock-digest");
        var provided = new ProvidedServiceManifestV1("clock", serviceContract);
        var manifest = new ServiceManifestV1(new ComponentIdentity("clock-component"), new ComponentVersion("1.0.0"), Digest(image), process, [provided]);
        var protocol = Protocol("Clock", "clock-digest");
        var plan = new ComponentAdmissionPlan(manifest, image, [new ComponentCapabilityGrant(new DomainId(900), requirement)], [new ComponentProvidedServiceRegistration(provided, protocol)]);

        var admitted = kernel.AdmitComponent(plan);

        Assert.True(admitted.IsSuccess, admitted.Message);
        Assert.Equal(ComponentLifecycleState.Running, admitted.Value!.State);
        Assert.Equal(new ProcessHandle(new ProcessId(1), 1), admitted.Value.Process);
        Assert.Equal(new DomainId(10), admitted.Value.Domain);
        Assert.Single(admitted.Value.Capabilities);
        var resource = Assert.Single(admitted.Value.ResourceRequirements);
        Assert.Equal(ComponentResourceRequirementV1.ForCapability(requirement), resource);
        Assert.Single(kernel.ResolveByContract(serviceContract).Value!);
    }

    [Fact]
    public void ImageMismatchFailsBeforeProcessOrAuthorityCreation()
    {
        var kernel = new RuntimeKernel();
        var manifest = new ServiceManifestV1(new ComponentIdentity("bad-image"), new ComponentVersion("1"), Digest([1]), TestFixtures.Manifest(2, 20));
        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, new byte[] { 2 }));
        Assert.Equal(KernelError.ComponentDigestMismatch, result.Error);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new ProcessHandle(new ProcessId(2), 1)).Error);
    }

    [Fact]
    public void MissingCapabilityResourceFailsBeforeProcessCreation()
    {
        var kernel = new RuntimeKernel();
        var image = new byte[] { 4 };
        var requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "missing", CapabilityRights.Read);
        var process = TestFixtures.Manifest(3, 30, capabilities: [requirement]);
        var contract = new ServiceContractIdentity("Missing", "1", "missing-digest");
        var provided = new ProvidedServiceManifestV1("missing", contract);
        var manifest = new ServiceManifestV1(new ComponentIdentity("missing-cap"), new ComponentVersion("1"), Digest(image), process, [provided]);
        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image, providedServices: [new ComponentProvidedServiceRegistration(provided, Protocol("Missing", "missing-digest"))]));

        Assert.Equal(KernelError.MissingCapability, result.Error);
        Assert.Empty(kernel.ResolveByContract(contract).Value!);
        Assert.Equal(KernelError.ComponentNotFound, kernel.QueryComponent(manifest.Identity).Error);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new ProcessHandle(new ProcessId(3), 1)).Error);
    }

    [Fact]
    public void UndeclaredCapabilityGrantFailsBeforeProcessCreation()
    {
        var kernel = new RuntimeKernel();
        var image = new byte[] { 4, 1 };
        var grant = new CapabilityRequirementV1(ResourceKind.Device, "undeclared-device", CapabilityRights.Read);
        var manifest = new ServiceManifestV1(new ComponentIdentity("extra-grant"), new ComponentVersion("1"), Digest(image), TestFixtures.Manifest(31, 310));

        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(
            manifest,
            image,
            [new ComponentCapabilityGrant(new DomainId(999), grant)]));

        Assert.Equal(KernelError.InvalidManifest, result.Error);
        Assert.Equal(KernelError.ComponentNotFound, kernel.QueryComponent(manifest.Identity).Error);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new ProcessHandle(new ProcessId(31), 1)).Error);
    }

    [Fact]
    public void BroaderCapabilityGrantThanManifestFailsBeforeProcessCreation()
    {
        var kernel = new RuntimeKernel();
        var image = new byte[] { 4, 2 };
        var required = new CapabilityRequirementV1(ResourceKind.Device, "sensor", CapabilityRights.Read);
        var broader = required with { Rights = CapabilityRights.Read | CapabilityRights.Configure };
        var manifest = new ServiceManifestV1(
            new ComponentIdentity("broad-grant"),
            new ComponentVersion("1"),
            Digest(image),
            TestFixtures.Manifest(32, 320, capabilities: [required]));

        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(
            manifest,
            image,
            [new ComponentCapabilityGrant(new DomainId(999), broader)]));

        Assert.Equal(KernelError.InvalidManifest, result.Error);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new ProcessHandle(new ProcessId(32), 1)).Error);
    }

    [Fact]
    public void RequiredContractSessionDenialUnwindsPublishedServicesAndProcess()
    {
        var kernel = new RuntimeKernel();
        var provider = TestFixtures.Create(kernel, 80, 800).Handle;
        Assert.True(kernel.AdmitProcess(provider).IsSuccess);
        var required = new ServiceContractIdentity("Dependency", "1", "dependency-digest");
        Assert.True(kernel.RegisterService(provider, "dependency", required, Protocol("Dependency", "dependency-digest"), requiredCapabilities: [new CapabilityRequirementV1(ResourceKind.KernelService, "dependency", CapabilityRights.Read)]).IsSuccess);

        var image = new byte[] { 5 };
        var ownContract = new ServiceContractIdentity("Consumer", "1", "consumer-digest");
        var own = new ProvidedServiceManifestV1("consumer", ownContract);
        var manifest = new ServiceManifestV1(new ComponentIdentity("consumer"), new ComponentVersion("1"), Digest(image), TestFixtures.Manifest(4, 40), [own], [required]);
        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image, providedServices: [new ComponentProvidedServiceRegistration(own, Protocol("Consumer", "consumer-digest"))]));

        Assert.Equal(KernelError.MissingCapability, result.Error);
        Assert.Empty(kernel.ResolveByContract(ownContract).Value!);
        Assert.Equal(ComponentLifecycleState.Reclaimable, kernel.QueryComponent(manifest.Identity).Value!.State);
    }

    [Fact]
    public void MissingPlatformFeatureFailsBeforeProcessCreation()
    {
        var image = new byte[] { 6 };
        var manifest = new ServiceManifestV1(new ComponentIdentity("platform-required"), new ComponentVersion("1"), Digest(image), TestFixtures.Manifest(6, 60), requiresPlatformDomain: true);
        var kernel = new RuntimeKernel();
        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image));
        Assert.Equal(KernelError.PlatformUnsupported, result.Error);
        Assert.Equal(KernelError.ComponentNotFound, kernel.QueryComponent(manifest.Identity).Error);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new ProcessHandle(new ProcessId(6), 1)).Error);
    }

    [Fact]
    public void FailureAfterPlatformBindRevokesBindingBeforeLocalReclaim()
    {
        var image = new byte[] { 7 };
        var missing = new ServiceContractIdentity("Absent", "1", "absent-digest");
        var manifest = new ServiceManifestV1(new ComponentIdentity("platform-rollback"), new ComponentVersion("1"), Digest(image), TestFixtures.Manifest(7, 70), requiredContracts: [missing], requiresPlatformDomain: true);
        var kernel = new RuntimeKernel(new HostPlatformAuthorityProvider());
        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image));
        var snapshot = kernel.QueryComponent(manifest.Identity).Value!;
        Assert.Equal(KernelError.ServiceNotFound, result.Error);
        Assert.False(snapshot.PlatformAuthorityLive);
        Assert.True(snapshot.Reclaimable);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(snapshot.Process).Error);
    }

    [Fact]
    public void PlatformFeatureRequirementAloneDoesNotMaterializeOrdinaryDomain()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var image = new byte[] { 7, 1 };
        var manifest = new ServiceManifestV1(
            new ComponentIdentity("feature-only"),
            new ComponentVersion("1"),
            Digest(image),
            TestFixtures.Manifest(71, 710),
            platformRequirements:
            [
                new PlatformRequirementV1(
                    ComponentPlatformFeatureFamily.ExecutionPolicy,
                    PlatformExecutionPolicyContract.ContractVersion,
                    ComponentPlatformFeatureAvailability.ModelOnly)
            ]);

        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image));

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(0, provider.BindDomainCallCount);
        Assert.False(result.Value!.PlatformAuthorityLive);
        Assert.Empty(result.Value.ResourceRequirements);
    }

    [Fact]
    public void ExplicitPlatformAuthorityResourceBindsExactlyOnce()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var image = new byte[] { 7, 2 };
        var manifest = new ServiceManifestV1(
            new ComponentIdentity("domain-resource"),
            new ComponentVersion("1"),
            Digest(image),
            TestFixtures.Manifest(72, 720),
            platformRequirements:
            [
                new PlatformRequirementV1(
                    ComponentPlatformFeatureFamily.NeutralDomains,
                    PlatformDomainContract.ContractVersion,
                    ComponentPlatformFeatureAvailability.RuntimeAdmission)
            ],
            resourceRequirements: [ComponentResourceRequirementV1.ForPlatformAuthorityDomain()]);

        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image));

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, provider.BindDomainCallCount);
        Assert.True(result.Value!.PlatformAuthorityLive);
        Assert.Equal(ComponentResourceRequirementKind.PlatformAuthorityDomain, Assert.Single(result.Value.ResourceRequirements).Kind);
    }

    [Fact]
    public void PlatformAuthorityResourceRequiresNeutralDomainsFeatureRequirement()
    {
        var image = new byte[] { 7, 3 };
        Assert.Throws<ArgumentException>(() => new ServiceManifestV1(
            new ComponentIdentity("invalid-domain-resource"),
            new ComponentVersion("1"),
            Digest(image),
            TestFixtures.Manifest(73, 730),
            resourceRequirements: [ComponentResourceRequirementV1.ForPlatformAuthorityDomain()]));
    }

    [Fact]
    public void ExplicitLocalResourcesCannotBroadenOrDivergeFromProcessManifest()
    {
        var image = new byte[] { 7, 4 };
        var processRequirement = new CapabilityRequirementV1(ResourceKind.Device, "sensor", CapabilityRights.Read);
        var broader = ComponentResourceRequirementV1.ForCapability(processRequirement with { Rights = CapabilityRights.Read | CapabilityRights.Configure });

        Assert.Throws<ArgumentException>(() => new ServiceManifestV1(
            new ComponentIdentity("divergent-resource"),
            new ComponentVersion("1"),
            Digest(image),
            TestFixtures.Manifest(74, 740, capabilities: [processRequirement]),
            resourceRequirements: [broader]));
    }

    [Fact]
    public void UnknownResourceRequirementKindIsRejected()
    {
        var image = new byte[] { 7, 5 };
        var unknown = new ComponentResourceRequirementV1((ComponentResourceRequirementKind)99, null, null, CapabilityRights.None);

        Assert.Throws<ArgumentException>(() => new ServiceManifestV1(
            new ComponentIdentity("unknown-resource"),
            new ComponentVersion("1"),
            Digest(image),
            TestFixtures.Manifest(75, 750),
            resourceRequirements: [unknown]));
    }

    [Fact]
    public void ComponentManifestCannotCarryProviderAuthorityVocabulary()
    {
        var names = typeof(ServiceManifestV1).GetProperties().Select(x => x.Name)
            .Concat(typeof(ComponentResourceRequirementV1).GetProperties().Select(x => x.Name))
            .ToArray();
        foreach (var forbidden in new[] { "ProviderLease", "PhysicalAddress", "PageTableRoot", "Iommu", "Vmcs", "Vmx", "Lane", "Opcode" })
            Assert.DoesNotContain(names, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TypedPlatformRequirementIsCheckedBeforeProcessOrProviderAuthorityCreation()
    {
        var provider = new HostPlatformAuthorityProvider();
        var kernel = new RuntimeKernel(provider);
        var image = new byte[] { 8 };
        var manifest = new ServiceManifestV1(
            new ComponentIdentity("needs-executable-vm"),
            new ComponentVersion("1"),
            Digest(image),
            TestFixtures.Manifest(8, 80),
            platformRequirements:
            [
                new PlatformRequirementV1(
                    ComponentPlatformFeatureFamily.VirtualizationDomains,
                    PlatformVirtualizationContract.ContractVersion,
                    ComponentPlatformFeatureAvailability.Executable)
            ]);

        var result = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image));

        Assert.Equal(KernelError.PlatformUnsupported, result.Error);
        Assert.Equal(0, provider.BindDomainCallCount);
        Assert.Equal(KernelError.ProcessNotFound,
            kernel.Processes.Resolve(new ProcessHandle(new ProcessId(8), 1)).Error);
    }

    private static ProtocolDefinitionV1 Protocol(string name, string digest) =>
        new(name, digest, "Idle", ["Done"], [new ProtocolMessageDescriptorV1(1, "Invoke")], [new ProtocolTransitionV1(1, "Idle", "Done")]);

    private static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
