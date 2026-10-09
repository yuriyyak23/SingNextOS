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
        ServiceManifestV2 manifest,
        ReadOnlyMemory<byte> image,
        IEnumerable<ComponentCapabilityGrant>? grants = null,
        IEnumerable<ComponentProvidedServiceRegistration>? providedServices = null,
        ComponentDriverResourcePlan? driverResources = null)
        : this(manifest?.BaseManifest ?? throw new ArgumentNullException(nameof(manifest)), image, grants, providedServices, driverResources)
    {
        ManifestV2 = manifest;
    }

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
    public ServiceManifestV2? ManifestV2 { get; }
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
    KernelError? Failure,
    string ManifestDigest,
    ManifestAdmissionResultV1 Admission,
    IReadOnlyList<ServiceInstanceHandle> ServiceInstances,
    BudgetAccountHandle ServiceBudget,
    BudgetAccountHandle ProcessBudget);

internal sealed class ComponentAdmissionRecord
{
    // Metadata only. Never hold this gate across registry/provider admission.
    internal object SnapshotGate { get; } = new();
    private ManifestAdmissionResultV1 _admission = null!;
    private ComponentLifecycleState _state = ComponentLifecycleState.Declared;
    private PlatformDomainBinding? _platformBinding;
    private DeviceResourceSet? _deviceResources;
    private KernelError? _failure;
    public required ComponentAdmissionPlan Plan { get; init; }
    public required ServiceManifestV1 Manifest { get; init; }
    public required ProcessHandle Process { get; init; }
    public required ManifestAdmissionResultV1 Admission { get { lock (SnapshotGate) return _admission; } set { lock (SnapshotGate) _admission = value; } }
    public required BudgetAccountHandle ServiceBudget { get; init; }
    public required BudgetAccountHandle ProcessBudget { get; init; }
    public ComponentLifecycleState State { get { lock (SnapshotGate) return _state; } set { lock (SnapshotGate) _state = value; } }
    public List<CapabilityId> Capabilities { get; } = [];
    public List<EndpointSessionHandle> Sessions { get; } = [];
    public Dictionary<ServiceContractIdentity, EndpointSessionHandle> DependencySessions { get; } = [];
    public List<ServiceEndpointDescriptor> Services { get; } = [];
    public PlatformDomainBinding? PlatformBinding { get { lock (SnapshotGate) return _platformBinding; } set { lock (SnapshotGate) _platformBinding = value; } }
    public DeviceResourceSet? DeviceResources { get { lock (SnapshotGate) return _deviceResources; } set { lock (SnapshotGate) _deviceResources = value; } }
    public KernelError? Failure { get { lock (SnapshotGate) return _failure; } set { lock (SnapshotGate) _failure = value; } }

    public ComponentLifecycleSnapshot Snapshot
    {
        get
        {
            lock (SnapshotGate) return new(
        Manifest.Identity, Manifest.Version, Manifest.ImageDigest, Process, Manifest.Process.DomainId, State,
        Manifest.ProvidedContracts.Select(x => x.Contract).ToArray(), Manifest.RequiredContracts.ToArray(),
        Manifest.ResourceRequirements.ToArray(), Capabilities.ToArray(), Sessions.ToArray(), PlatformBinding is not null,
        DeviceResources?.Snapshot, State == ComponentLifecycleState.Reclaimable, Failure,
        Manifest.NormalizedDigest, Admission,
        Services.Select(service => new ServiceInstanceHandle(service.Service.Id, service.Generation, Process, Manifest.Process.DomainId)).ToArray(),
        ServiceBudget,
        ProcessBudget);
        }
    }
}
