using System.Collections.ObjectModel;

namespace YAKSys_Hybrid_CPU.Core;

public enum NeutralRuntimeFeatureFamily
{
    DomainLifecycle = 0,
    OwnedRegionMapping,
    DeviceBinding,
    MmioMapping,
    InterruptBinding,
    ExplicitMemoryVisibility,
    DmaAdmission,
    ChildDomainLifecycle,
    ChildGuestMemory,
    ChildEventDelivery,
    ChildTrapDelivery,
    BoundedVirtualIo,
    ChildExecutableArtifact,
}

public enum NeutralRuntimeFeatureAvailability
{
    Unavailable = 0,
    RuntimeAdmission,
    Executable,
}

public readonly record struct NeutralRuntimeFeatureDescriptor(
    NeutralRuntimeFeatureFamily Family,
    uint ContractVersion,
    NeutralRuntimeFeatureAvailability Availability);

public static class NeutralRuntimeFeatureContracts
{
    public const uint DomainLifecycleVersion = 1;
    public const uint OwnedRegionMappingVersion = 1;
    public const uint DeviceBindingVersion = 1;
    public const uint MmioMappingVersion = 1;
    public const uint InterruptBindingVersion = 1;
    public const uint ExplicitMemoryVisibilityVersion = 1;
    public const uint DmaAdmissionVersion = 1;
    public const uint ChildDomainLifecycleVersion = NeutralChildDomainContract.ContractVersion;
    public const uint ChildGuestMemoryVersion = NeutralGuestMemoryContract.ContractVersion;
    public const uint ChildEventDeliveryVersion = NeutralVirtualEventContract.ContractVersion;
    public const uint ChildTrapDeliveryVersion = NeutralVirtualTrapContract.ContractVersion;
    public const uint BoundedVirtualIoVersion = NeutralVirtualIoContract.ContractVersion;
    public const uint ChildExecutableArtifactVersion = NeutralChildExecutionContract.ContractVersion;

    public static uint VersionFor(NeutralRuntimeFeatureFamily family) => family switch
    {
        NeutralRuntimeFeatureFamily.DomainLifecycle => DomainLifecycleVersion,
        NeutralRuntimeFeatureFamily.OwnedRegionMapping => OwnedRegionMappingVersion,
        NeutralRuntimeFeatureFamily.DeviceBinding => DeviceBindingVersion,
        NeutralRuntimeFeatureFamily.MmioMapping => MmioMappingVersion,
        NeutralRuntimeFeatureFamily.InterruptBinding => InterruptBindingVersion,
        NeutralRuntimeFeatureFamily.ExplicitMemoryVisibility => ExplicitMemoryVisibilityVersion,
        NeutralRuntimeFeatureFamily.DmaAdmission => DmaAdmissionVersion,
        NeutralRuntimeFeatureFamily.ChildDomainLifecycle => ChildDomainLifecycleVersion,
        NeutralRuntimeFeatureFamily.ChildGuestMemory => ChildGuestMemoryVersion,
        NeutralRuntimeFeatureFamily.ChildEventDelivery => ChildEventDeliveryVersion,
        NeutralRuntimeFeatureFamily.ChildTrapDelivery => ChildTrapDeliveryVersion,
        NeutralRuntimeFeatureFamily.BoundedVirtualIo => BoundedVirtualIoVersion,
        NeutralRuntimeFeatureFamily.ChildExecutableArtifact => ChildExecutableArtifactVersion,
        _ => throw new ArgumentOutOfRangeException(nameof(family)),
    };
}

public sealed class NeutralRuntimeFeatureManifest
{
    private readonly NeutralRuntimeFeatureDescriptor[] _features;
    private readonly ReadOnlyCollection<NeutralRuntimeFeatureDescriptor> _view;

    public NeutralRuntimeFeatureManifest(IEnumerable<NeutralRuntimeFeatureDescriptor> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        _features = features.OrderBy(static feature => feature.Family).ToArray();

        for (var index = 0; index < _features.Length; index++)
        {
            var feature = _features[index];
            if (!Enum.IsDefined(feature.Family))
                throw new ArgumentOutOfRangeException(nameof(features), "Feature family is not defined.");
            if (!Enum.IsDefined(feature.Availability) ||
                feature.Availability == NeutralRuntimeFeatureAvailability.Unavailable)
                throw new ArgumentOutOfRangeException(nameof(features), "Manifest entries must be available.");
            if (feature.ContractVersion == 0)
                throw new ArgumentOutOfRangeException(nameof(features), "Feature contract versions must be positive.");
            if (index > 0 && _features[index - 1].Family == feature.Family)
                throw new ArgumentException($"Feature family {feature.Family} is declared more than once.", nameof(features));
        }

        _view = Array.AsReadOnly(_features);
    }

    public static NeutralRuntimeFeatureManifest Empty { get; } = new([]);
    public IReadOnlyList<NeutralRuntimeFeatureDescriptor> Features => _view;

    public NeutralRuntimeFeatureDescriptor Resolve(NeutralRuntimeFeatureFamily family)
    {
        if (!Enum.IsDefined(family))
            throw new ArgumentOutOfRangeException(nameof(family));

        foreach (var feature in _features)
            if (feature.Family == family)
                return feature;

        return new NeutralRuntimeFeatureDescriptor(family, 0, NeutralRuntimeFeatureAvailability.Unavailable);
    }
}

public interface INeutralRuntimeFeatureProvider
{
    NeutralRuntimeFeatureManifest QueryNeutralFeatures();
}
