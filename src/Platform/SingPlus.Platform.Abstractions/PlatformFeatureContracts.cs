using System.Collections.ObjectModel;

namespace SingPlus.Platform;

public enum PlatformFeatureFamily
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
}

public enum PlatformFeatureAvailability
{
    Unavailable = 0,
    ModelOnly,
    ProjectionOnly,
    RuntimeAdmission,
    Executable,
    ProductionSecure
}

public readonly record struct PlatformFeatureDescriptor(
    PlatformFeatureFamily Family,
    uint ContractVersion,
    PlatformFeatureAvailability Availability);

public sealed class PlatformFeatureManifest
{
    private readonly PlatformFeatureDescriptor[] _features;
    private readonly ReadOnlyCollection<PlatformFeatureDescriptor> _view;

    public PlatformFeatureManifest(IEnumerable<PlatformFeatureDescriptor> features)
    {
        ArgumentNullException.ThrowIfNull(features);

        _features = features
            .OrderBy(static feature => feature.Family)
            .ToArray();

        for (var index = 0; index < _features.Length; index++)
        {
            var feature = _features[index];
            if (!Enum.IsDefined(feature.Family))
                throw new ArgumentOutOfRangeException(nameof(features), "Feature family is not defined.");

            if (!Enum.IsDefined(feature.Availability) ||
                feature.Availability == PlatformFeatureAvailability.Unavailable)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(features),
                    "Manifest entries must use a defined non-Unavailable availability class.");
            }

            if (feature.ContractVersion == 0)
                throw new ArgumentOutOfRangeException(
                    nameof(features),
                    "Feature contract versions must be positive.");

            if (index > 0 && _features[index - 1].Family == feature.Family)
                throw new ArgumentException(
                    $"Feature family {feature.Family} is declared more than once.",
                    nameof(features));
        }

        _view = Array.AsReadOnly(_features);
    }

    public static PlatformFeatureManifest Empty { get; } = new([]);

    public IReadOnlyList<PlatformFeatureDescriptor> Features => _view;

    public PlatformFeatureDescriptor Resolve(PlatformFeatureFamily family)
    {
        if (!Enum.IsDefined(family))
            throw new ArgumentOutOfRangeException(nameof(family));

        foreach (var feature in _features)
        {
            if (feature.Family == family)
                return feature;
        }

        return new PlatformFeatureDescriptor(
            family,
            0,
            PlatformFeatureAvailability.Unavailable);
    }

    public bool Supports(
        PlatformFeatureFamily family,
        uint minimumContractVersion,
        PlatformFeatureAvailability requiredAvailability)
    {
        if (minimumContractVersion == 0)
            throw new ArgumentOutOfRangeException(nameof(minimumContractVersion));

        if (!Enum.IsDefined(requiredAvailability) ||
            requiredAvailability == PlatformFeatureAvailability.Unavailable)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredAvailability));
        }

        var feature = Resolve(family);
        return feature.ContractVersion >= minimumContractVersion &&
               feature.Availability == requiredAvailability;
    }

    public static PlatformFeatureManifest FromLegacy(PlatformAuthorityFeatures features)
    {
        var descriptors = new List<PlatformFeatureDescriptor>();

        if ((features & PlatformAuthorityFeatures.NeutralDomainBinding) != 0)
        {
            descriptors.Add(new PlatformFeatureDescriptor(
                PlatformFeatureFamily.NeutralDomains,
                1,
                PlatformFeatureAvailability.RuntimeAdmission));
        }

        if ((features & PlatformAuthorityFeatures.DirectOwnedRegionMapping) != 0)
        {
            descriptors.Add(new PlatformFeatureDescriptor(
                PlatformFeatureFamily.OwnedRegionMapping,
                1,
                PlatformFeatureAvailability.RuntimeAdmission));
        }

        return new PlatformFeatureManifest(descriptors);
    }
}

public interface IPlatformFeatureProvider
{
    PlatformFeatureManifest QueryFeatures();
}
