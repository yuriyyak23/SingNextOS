using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter.Backend;

internal static class ExternalChildContractQualification
{
    private static readonly HybridCpuExternalFeatureFamily[] RequiredFamilies =
    [
        HybridCpuExternalFeatureFamily.ChildDomainLifecycle,
        HybridCpuExternalFeatureFamily.ChildGuestMemory,
        HybridCpuExternalFeatureFamily.ChildEventDelivery,
        HybridCpuExternalFeatureFamily.ChildTrapDelivery,
    ];

    public static bool SupportsAdmission(HybridCpuExternalFeatureManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.ContractVersion.Major == HybridCpuExternalRuntimePrerequisite.RequiredContractMajor &&
            manifest.ContractVersion.Minor >= HybridCpuExternalRuntimePrerequisite.RequiredContractMinor &&
            manifest.Generation != 0 &&
            RequiredFamilies.All(family =>
                manifest.GetFeature(family).Availability is
                    HybridCpuExternalFeatureAvailability.RuntimeAdmission or
                    HybridCpuExternalFeatureAvailability.Executable);
    }

    public static bool CanAdvertiseExecutable(HybridCpuExternalFeatureManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return SupportsAdmission(manifest) &&
            manifest.GetFeature(HybridCpuExternalFeatureFamily.ChildExecutableImage).Availability ==
                HybridCpuExternalFeatureAvailability.Executable &&
            manifest.GetFeature(HybridCpuExternalFeatureFamily.ChildVirtualIo).Availability ==
                HybridCpuExternalFeatureAvailability.Executable;
    }

    /// <summary>
    /// Contract-shape qualification only. The scaffold does not publish this
    /// manifest and does not become a runtime provider by constructing it.
    /// </summary>
    public static NeutralRuntimeFeatureManifest QualifyNeutralAdmissionManifest(
        HybridCpuExternalFeatureManifest manifest)
    {
        if (!SupportsAdmission(manifest)) return NeutralRuntimeFeatureManifest.Empty;
        return new NeutralRuntimeFeatureManifest(
        [
            new(NeutralRuntimeFeatureFamily.ChildDomainLifecycle, NeutralRuntimeFeatureContracts.ChildDomainLifecycleVersion, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
            new(NeutralRuntimeFeatureFamily.ChildGuestMemory, NeutralRuntimeFeatureContracts.ChildGuestMemoryVersion, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
            new(NeutralRuntimeFeatureFamily.ChildEventDelivery, NeutralRuntimeFeatureContracts.ChildEventDeliveryVersion, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
            new(NeutralRuntimeFeatureFamily.ChildTrapDelivery, NeutralRuntimeFeatureContracts.ChildTrapDeliveryVersion, NeutralRuntimeFeatureAvailability.RuntimeAdmission),
        ]);
    }

    public static bool IsExactTerminalClose(
        ExternalChildDomainCloseReceipt? receipt,
        ExternalChildDomainLease expectedLease,
        ExternalOperationIdentity expectedOperation,
        HybridCpuExternalFeatureManifest manifest) =>
        receipt is not null && receipt.Lease == expectedLease &&
        receipt.Operation == expectedOperation &&
        receipt.ContractVersion == manifest.ContractVersion &&
        receipt.ManifestGeneration == manifest.Generation &&
        receipt.ResultingState == ExternalChildDomainState.Closed && receipt.IsTerminal;
}
