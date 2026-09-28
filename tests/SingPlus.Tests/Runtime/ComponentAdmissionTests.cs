using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Sip.Compute;

namespace SingPlus.Tests.Runtime;

public sealed class ComponentAdmissionTests
{
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
