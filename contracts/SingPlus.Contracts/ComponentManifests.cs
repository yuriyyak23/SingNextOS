using System.Security.Cryptography;

namespace SingPlus.Contracts;

public readonly record struct ComponentIdentity(string Name);
public readonly record struct ComponentVersion(string Value);

public sealed record ProvidedServiceManifestV1(string ServiceName, ServiceContractIdentity Contract);

public enum ComponentPlatformFeatureFamily
{
    NeutralDomains = 0,
    OwnedRegionMapping,
    IoDomainBinding,
    DmaMapping,
    ExplicitMemoryVisibility,
    Dsc1BulkCompute,
    MatrixTileV1,
    ScopedAcceleratorV1,
    VirtualizationDomains,
    NestedDomains,
    PlatformEvidence,
    SecureDomains,
    SurfacePresentation,
    MmioMapping,
    IrqBinding,
    ExecutionPolicy,
}

public enum ComponentPlatformFeatureAvailability
{
    ModelOnly = 1,
    ProjectionOnly,
    RuntimeAdmission,
    Executable,
    ProductionSecure,
}

public readonly record struct PlatformRequirementV1(
    ComponentPlatformFeatureFamily Family,
    uint MinimumContractVersion,
    ComponentPlatformFeatureAvailability Availability);

public enum ComponentResourceRequirementKind
{
    LocalCapability = 0,
    PlatformAuthorityDomain = 1,
}

public readonly record struct ComponentResourceRequirementV1(
    ComponentResourceRequirementKind Kind,
    ResourceKind? ResourceKind,
    string? ResourceId,
    CapabilityRights Rights)
{
    public static ComponentResourceRequirementV1 ForCapability(CapabilityRequirementV1 requirement) =>
        new(ComponentResourceRequirementKind.LocalCapability, requirement.ResourceKind, requirement.ResourceId, requirement.Rights);

    public static ComponentResourceRequirementV1 ForPlatformAuthorityDomain() =>
        new(ComponentResourceRequirementKind.PlatformAuthorityDomain, null, null, CapabilityRights.None);

    public CapabilityRequirementV1 ToCapabilityRequirement()
    {
        if (Kind != ComponentResourceRequirementKind.LocalCapability || ResourceKind is null || ResourceId is null)
            throw new InvalidOperationException("Only local-capability resource requirements convert to capability requirements.");
        return new CapabilityRequirementV1(ResourceKind.Value, ResourceId, Rights);
    }
}

public sealed class ServiceManifestV1
{
    private readonly ProvidedServiceManifestV1[] _provided;
    private readonly ServiceContractIdentity[] _required;
    private readonly PlatformRequirementV1[] _platformRequirements;
    private readonly ComponentResourceRequirementV1[] _resourceRequirements;

    public ServiceManifestV1(
        ComponentIdentity identity,
        ComponentVersion version,
        string imageDigest,
        SingProcessManifestV1 process,
        IEnumerable<ProvidedServiceManifestV1>? providedContracts = null,
        IEnumerable<ServiceContractIdentity>? requiredContracts = null,
        bool requiresPlatformDomain = false,
        IEnumerable<PlatformRequirementV1>? platformRequirements = null,
        IEnumerable<ComponentResourceRequirementV1>? resourceRequirements = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (string.IsNullOrWhiteSpace(identity.Name)) throw new ArgumentException("Component identity is required.", nameof(identity));
        if (string.IsNullOrWhiteSpace(version.Value)) throw new ArgumentException("Component version is required.", nameof(version));
        if (imageDigest.Length != 64 || !imageDigest.All(Uri.IsHexDigit)) throw new ArgumentException("Image digest must be a SHA-256 hexadecimal digest.", nameof(imageDigest));

        _provided = (providedContracts ?? []).OrderBy(x => x.ServiceName, StringComparer.Ordinal).ToArray();
        _required = (requiredContracts ?? []).OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Version, StringComparer.Ordinal).ThenBy(x => x.Digest, StringComparer.Ordinal).ToArray();
        if (_provided.Any(x => string.IsNullOrWhiteSpace(x.ServiceName))) throw new ArgumentException("Provided service names are required.", nameof(providedContracts));
        if (_provided.Select(x => x.ServiceName).Distinct(StringComparer.Ordinal).Count() != _provided.Length) throw new ArgumentException("Provided service names must be unique.", nameof(providedContracts));
        if (_required.Distinct().Count() != _required.Length) throw new ArgumentException("Required contracts must be unique.", nameof(requiredContracts));

        var declaredPlatformRequirements = (platformRequirements ?? []).ToList();
        if (requiresPlatformDomain && declaredPlatformRequirements.All(static requirement => requirement.Family != ComponentPlatformFeatureFamily.NeutralDomains))
        {
            declaredPlatformRequirements.Add(new PlatformRequirementV1(
                ComponentPlatformFeatureFamily.NeutralDomains,
                1,
                ComponentPlatformFeatureAvailability.RuntimeAdmission));
        }
        _platformRequirements = declaredPlatformRequirements.OrderBy(static requirement => requirement.Family).ToArray();
        if (_platformRequirements.Any(static requirement =>
                !Enum.IsDefined(requirement.Family) ||
                !Enum.IsDefined(requirement.Availability) ||
                requirement.MinimumContractVersion == 0))
            throw new ArgumentException("Platform requirements require defined families, exact availability and a positive contract version.", nameof(platformRequirements));
        if (_platformRequirements.Select(static requirement => requirement.Family).Distinct().Count() != _platformRequirements.Length)
            throw new ArgumentException("Platform feature families must be unique in a component manifest.", nameof(platformRequirements));

        var declaredResources = resourceRequirements?.ToList()
            ?? process.RequiredCapabilities.Select(ComponentResourceRequirementV1.ForCapability).ToList();
        if (requiresPlatformDomain && declaredResources.All(static requirement => requirement.Kind != ComponentResourceRequirementKind.PlatformAuthorityDomain))
            declaredResources.Add(ComponentResourceRequirementV1.ForPlatformAuthorityDomain());

        _resourceRequirements = declaredResources
            .OrderBy(static requirement => requirement.Kind)
            .ThenBy(static requirement => requirement.ResourceKind.HasValue ? (int)requirement.ResourceKind.Value : -1)
            .ThenBy(static requirement => requirement.ResourceId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static requirement => (int)requirement.Rights)
            .ToArray();

        var resourceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirement in _resourceRequirements)
        {
            if (!Enum.IsDefined(requirement.Kind))
                throw new ArgumentException("Unknown component resource requirement kind.", nameof(resourceRequirements));

            switch (requirement.Kind)
            {
                case ComponentResourceRequirementKind.LocalCapability:
                    if (requirement.ResourceKind is null || !Enum.IsDefined(requirement.ResourceKind.Value))
                        throw new ArgumentException("Local resource requirements require a defined Sing resource kind.", nameof(resourceRequirements));
                    if (string.IsNullOrWhiteSpace(requirement.ResourceId))
                        throw new ArgumentException("Local resource requirements require a semantic resource identity.", nameof(resourceRequirements));
                    if (requirement.Rights == CapabilityRights.None)
                        throw new ArgumentException("Local resource requirements require non-empty capability rights.", nameof(resourceRequirements));
                    if (!resourceKeys.Add($"local:{(int)requirement.ResourceKind.Value}:{requirement.ResourceId}"))
                        throw new ArgumentException("Local component resource requirements must be unique by resource kind and identity.", nameof(resourceRequirements));
                    break;

                case ComponentResourceRequirementKind.PlatformAuthorityDomain:
                    if (requirement.ResourceKind is not null || requirement.ResourceId is not null || requirement.Rights != CapabilityRights.None)
                        throw new ArgumentException("A platform authority domain requirement cannot carry a local resource id or capability rights.", nameof(resourceRequirements));
                    if (!resourceKeys.Add("platform-authority-domain"))
                        throw new ArgumentException("A component may declare only one platform authority domain requirement.", nameof(resourceRequirements));
                    break;
            }
        }

        var declaredCapabilities = _resourceRequirements
            .Where(static requirement => requirement.Kind == ComponentResourceRequirementKind.LocalCapability)
            .Select(static requirement => requirement.ToCapabilityRequirement())
            .OrderBy(static requirement => requirement.ResourceKind)
            .ThenBy(static requirement => requirement.ResourceId, StringComparer.Ordinal)
            .ThenBy(static requirement => (int)requirement.Rights)
            .ToArray();
        var processCapabilities = process.RequiredCapabilities
            .OrderBy(static requirement => requirement.ResourceKind)
            .ThenBy(static requirement => requirement.ResourceId, StringComparer.Ordinal)
            .ThenBy(static requirement => (int)requirement.Rights)
            .ToArray();
        if (!declaredCapabilities.SequenceEqual(processCapabilities))
            throw new ArgumentException("Local component resource requirements must exactly match the process capability requirements.", nameof(resourceRequirements));

        if (_resourceRequirements.Any(static requirement => requirement.Kind == ComponentResourceRequirementKind.PlatformAuthorityDomain) &&
            _platformRequirements.All(static requirement => requirement.Family != ComponentPlatformFeatureFamily.NeutralDomains))
            throw new ArgumentException("A platform authority domain resource requires a NeutralDomains platform requirement.", nameof(resourceRequirements));

        Identity = identity;
        Version = version;
        ImageDigest = imageDigest.ToLowerInvariant();
        Process = process;
    }

    public ComponentIdentity Identity { get; }
    public ComponentVersion Version { get; }
    public string ImageDigest { get; }
    public SingProcessManifestV1 Process { get; }
    public IReadOnlyList<ProvidedServiceManifestV1> ProvidedContracts => _provided;
    public IReadOnlyList<ServiceContractIdentity> RequiredContracts => _required;
    public IReadOnlyList<PlatformRequirementV1> PlatformRequirements => _platformRequirements;
    public IReadOnlyList<ComponentResourceRequirementV1> ResourceRequirements => _resourceRequirements;
    [Obsolete("Use typed PlatformRequirements and ResourceRequirements. This compatibility projection is removed after manifest migration.")]
    public bool RequiresPlatformDomain => _resourceRequirements.Any(static requirement => requirement.Kind == ComponentResourceRequirementKind.PlatformAuthorityDomain);

    public bool MatchesImage(ReadOnlySpan<byte> image) =>
        string.Equals(Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(), ImageDigest, StringComparison.Ordinal);
}
