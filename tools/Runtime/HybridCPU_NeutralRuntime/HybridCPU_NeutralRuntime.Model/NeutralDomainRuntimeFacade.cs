namespace YAKSys_Hybrid_CPU.Core;

/// <summary>Deterministic ModelOnly composition root for the neutral authority operations.</summary>
public sealed partial class NeutralDomainRuntimeFacade
{
    private static readonly NeutralRuntimeFeatureManifest ModelFeatures = new(
    [
        new(NeutralRuntimeFeatureFamily.DomainLifecycle, NeutralRuntimeFeatureContracts.DomainLifecycleVersion, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
        new(NeutralRuntimeFeatureFamily.OwnedRegionMapping, NeutralRuntimeFeatureContracts.OwnedRegionMappingVersion, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
        new(NeutralRuntimeFeatureFamily.DeviceBinding, NeutralRuntimeFeatureContracts.DeviceBindingVersion, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
        new(NeutralRuntimeFeatureFamily.MmioMapping, NeutralRuntimeFeatureContracts.MmioMappingVersion, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
        new(NeutralRuntimeFeatureFamily.InterruptBinding, NeutralRuntimeFeatureContracts.InterruptBindingVersion, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
        new(NeutralRuntimeFeatureFamily.ExplicitMemoryVisibility, NeutralRuntimeFeatureContracts.ExplicitMemoryVisibilityVersion, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
        new(NeutralRuntimeFeatureFamily.DmaAdmission, NeutralRuntimeFeatureContracts.DmaAdmissionVersion, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
    ]);

    public NeutralRuntimeImplementationProfile ImplementationProfile => NeutralRuntimeImplementationProfile.ModelOnly;
    public NeutralRuntimeFeatureManifest QueryNeutralFeatures() => ModelFeatures;

    private readonly NeutralDependencyRegistry _dependencies = new();
    private readonly INeutralLeaseValidator _leases;
    private ulong _nextResource = 1;
    private ulong _nextCycle = 1;

    public NeutralDomainRuntimeFacade() => _leases = new NeutralLeaseValidator(_dependencies);

    private static bool TryAllocate(ref ulong next, out ulong value)
    {
        if (next == 0 || next == ulong.MaxValue)
        {
            value = 0;
            return false;
        }

        value = next++;
        return true;
    }
}
