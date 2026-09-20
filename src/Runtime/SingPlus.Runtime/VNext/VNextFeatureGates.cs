namespace SingPlus.Runtime;

/// <summary>
/// Closed vocabulary for vNext rollout contours. These gates are operational
/// kill switches only and never grant capability, budget, ownership, provider,
/// visibility, or publication authority.
/// </summary>
internal enum VNextFeatureGate
{
    ResourceGrant = 0,
    ResourceLease,
    CrossOwnerAdmission,
    SipResource,
    SessionDonation,
    ExternalOperationResourceBinding,
    ComputeV2,
    HybridCpuResourceContract,
    HybridCpuUsageEvidence,
    ResourceScheduler,
    TemporalUpperBound,
    GuaranteedReservation,
    SipJobResource,
    DmaThroughput,
    NetworkThroughput,
    FabricThroughput,
    DeviceOccupancy,
    Energy,
    AuditOnly,
}

internal static class VNextFeatureGates
{
    private static readonly IReadOnlyDictionary<string, VNextFeatureGate> Known =
        new Dictionary<string, VNextFeatureGate>(StringComparer.Ordinal)
        {
            ["FG-VNX-RESOURCE-GRANT"] = VNextFeatureGate.ResourceGrant,
            ["FG-VNX-RESOURCE-LEASE"] = VNextFeatureGate.ResourceLease,
            ["FG-VNX-CROSSOWNER-ADMISSION"] = VNextFeatureGate.CrossOwnerAdmission,
            ["FG-VNX-SIP-RESOURCE"] = VNextFeatureGate.SipResource,
            ["FG-VNX-SESSION-DONATION"] = VNextFeatureGate.SessionDonation,
            ["FG-VNX-EXTOP-RESOURCE-BIND"] = VNextFeatureGate.ExternalOperationResourceBinding,
            ["FG-VNX-COMPUTE-V2"] = VNextFeatureGate.ComputeV2,
            ["FG-VNX-HCPU-RESOURCE-CONTRACT"] = VNextFeatureGate.HybridCpuResourceContract,
            ["FG-VNX-HCPU-USAGE-EVIDENCE"] = VNextFeatureGate.HybridCpuUsageEvidence,
            ["FG-VNX-RESOURCE-SCHEDULER"] = VNextFeatureGate.ResourceScheduler,
            ["FG-VNX-TEMPORAL-UPPER-BOUND"] = VNextFeatureGate.TemporalUpperBound,
            ["FG-VNX-GUARANTEED-RESERVATION"] = VNextFeatureGate.GuaranteedReservation,
            ["FG-VNX-SIPJOB-RESOURCE"] = VNextFeatureGate.SipJobResource,
            ["FG-VNX-DMA-THROUGHPUT"] = VNextFeatureGate.DmaThroughput,
            ["FG-VNX-NETWORK-THROUGHPUT"] = VNextFeatureGate.NetworkThroughput,
            ["FG-VNX-FABRIC-THROUGHPUT"] = VNextFeatureGate.FabricThroughput,
            ["FG-VNX-DEVICE-OCCUPANCY"] = VNextFeatureGate.DeviceOccupancy,
            ["FG-VNX-ENERGY"] = VNextFeatureGate.Energy,
            ["FG-VNX-AUDIT-ONLY"] = VNextFeatureGate.AuditOnly,
        };

    internal static IReadOnlyCollection<string> Names { get; } =
        Array.AsReadOnly(Known.Keys.Order(StringComparer.Ordinal).ToArray());

    internal static bool TryResolve(string? name, out VNextFeatureGate gate)
    {
        if (name is not null && Known.TryGetValue(name, out gate))
            return true;

        gate = (VNextFeatureGate)(-1);
        return false;
    }

    // P00 intentionally has no configuration or enablement path. Later phases
    // must add exact executable evidence before any independently scoped gate
    // can acquire an ON state.
    internal static bool IsEnabled(string? name) =>
        TryResolve(name, out _) && false;
}
