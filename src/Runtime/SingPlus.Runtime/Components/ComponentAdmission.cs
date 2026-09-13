using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public enum ComponentLifecycleState
{
    Declared = 0,
    Admitting = 1,
    Created = 2,
    Starting = 3,
    Running = 4,
    Draining = 5,
    Stopped = 6,
    Faulted = 7,
    Reclaimable = 8
}

public sealed record ComponentCapabilityGrant(DomainId IssuerDomain, CapabilityRequirementV1 Requirement);

public sealed record ComponentProvidedServiceRegistration(
    ProvidedServiceManifestV1 Manifest,
    ProtocolDefinitionV1 Protocol,
    ResponseProtocolDefinitionV1? ResponseProtocol = null,
    IReadOnlyList<CapabilityRequirementV1>? SessionRequirements = null);

public sealed class ComponentAdmissionPlan
{
    public ComponentAdmissionPlan(
        ServiceManifestV1 manifest,
        ReadOnlyMemory<byte> image,
        IEnumerable<ComponentCapabilityGrant>? grants = null,
        IEnumerable<ComponentProvidedServiceRegistration>? providedServices = null,
        ComponentDriverResourcePlan? driverResources = null)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        Image = image;
        Grants = (grants ?? []).ToArray();
        ProvidedServices = (providedServices ?? []).ToArray();
        DriverResources = driverResources;
    }

    public ServiceManifestV1 Manifest { get; }
    public ReadOnlyMemory<byte> Image { get; }
    public IReadOnlyList<ComponentCapabilityGrant> Grants { get; }
    public IReadOnlyList<ComponentProvidedServiceRegistration> ProvidedServices { get; }
    public ComponentDriverResourcePlan? DriverResources { get; }
}

public sealed record ComponentLifecycleSnapshot(
    ComponentIdentity Identity,
    ComponentVersion Version,
    string ImageDigest,
    ProcessHandle Process,
    DomainId Domain,
    ComponentLifecycleState State,
    IReadOnlyList<ServiceContractIdentity> ProvidedContracts,
    IReadOnlyList<ServiceContractIdentity> RequiredContracts,
    IReadOnlyList<ComponentResourceRequirementV1> ResourceRequirements,
    IReadOnlyList<CapabilityId> Capabilities,
    IReadOnlyList<EndpointSessionHandle> Sessions,
    bool PlatformAuthorityLive,
    DeviceResourceSetSnapshot? DeviceResources,
    bool Reclaimable,
    KernelError? Failure);

internal sealed class ComponentAdmissionRecord
{
    public required ServiceManifestV1 Manifest { get; init; }
    public required ProcessHandle Process { get; init; }
    public ComponentLifecycleState State { get; set; } = ComponentLifecycleState.Declared;
    public List<CapabilityId> Capabilities { get; } = [];
    public List<EndpointSessionHandle> Sessions { get; } = [];
    public List<ServiceEndpointDescriptor> Services { get; } = [];
    public PlatformDomainBinding? PlatformBinding { get; set; }
    public DeviceResourceSet? DeviceResources { get; set; }
    public KernelError? Failure { get; set; }

    public ComponentLifecycleSnapshot Snapshot => new(
        Manifest.Identity, Manifest.Version, Manifest.ImageDigest, Process, Manifest.Process.DomainId, State,
        Manifest.ProvidedContracts.Select(x => x.Contract).ToArray(), Manifest.RequiredContracts.ToArray(),
        Manifest.ResourceRequirements.ToArray(), Capabilities.ToArray(), Sessions.ToArray(), PlatformBinding is not null,
        DeviceResources?.Snapshot, State == ComponentLifecycleState.Reclaimable, Failure);
}
