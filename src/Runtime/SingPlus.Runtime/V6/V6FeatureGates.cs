namespace SingPlus.Runtime;

/// <summary>Closed v6 rollout vocabulary. Gate state is release policy, never runtime authority.</summary>
internal enum V6FeatureGate
{
    StagedExclusiveMemory = 0,
    SharedAtomicRegion,
    DirectCoherentMutableOutput,
    DmaGenerationBinding,
    FirstQualificationVertical,
    TemporalAccounting,
    GuaranteedDeadline,
    RasPartialFailure,
    Preemption,
    StatefulResume,
    DurableOutput,
    DeviceAttestation,
    LocalityPlanning,
    EnergyBudgets,
    Ifc,
    FormalRefinement,
    ProofCarryingLowering,
    MultiHostLeases,
}

internal static class V6FeatureGates
{
    private static readonly IReadOnlyDictionary<string, V6FeatureGate> Known =
        new Dictionary<string, V6FeatureGate>(StringComparer.Ordinal)
        {
            ["V6-STAGED-EXCLUSIVE-MEMORY"] = V6FeatureGate.StagedExclusiveMemory,
            ["V6-SHARED-ATOMIC-REGION"] = V6FeatureGate.SharedAtomicRegion,
            ["V6-DIRECT-COHERENT-MUTABLE-OUTPUT"] = V6FeatureGate.DirectCoherentMutableOutput,
            ["V6-DMA-GENERATION-BINDING"] = V6FeatureGate.DmaGenerationBinding,
            ["V6-FIRST-QUALIFICATION-VERTICAL"] = V6FeatureGate.FirstQualificationVertical,
            ["V6-TEMPORAL-ACCOUNTING"] = V6FeatureGate.TemporalAccounting,
            ["V6-GUARANTEED-DEADLINE"] = V6FeatureGate.GuaranteedDeadline,
            ["V6-RAS-PARTIAL-FAILURE"] = V6FeatureGate.RasPartialFailure,
            ["V6-PREEMPTION"] = V6FeatureGate.Preemption,
            ["V6-STATEFUL-RESUME"] = V6FeatureGate.StatefulResume,
            ["V6-DURABLE-OUTPUT"] = V6FeatureGate.DurableOutput,
            ["V6-DEVICE-ATTESTATION"] = V6FeatureGate.DeviceAttestation,
            ["V6-LOCALITY-PLANNING"] = V6FeatureGate.LocalityPlanning,
            ["V6-ENERGY-BUDGETS"] = V6FeatureGate.EnergyBudgets,
            ["V6-IFC"] = V6FeatureGate.Ifc,
            ["V6-FORMAL-REFINEMENT"] = V6FeatureGate.FormalRefinement,
            ["V6-PROOF-CARRYING-LOWERING"] = V6FeatureGate.ProofCarryingLowering,
            ["V6-MULTIHOST-LEASES"] = V6FeatureGate.MultiHostLeases,
        };

    internal static IReadOnlyCollection<string> Names { get; } =
        Array.AsReadOnly(Known.Keys.Order(StringComparer.Ordinal).ToArray());

    internal static bool TryResolve(string? name, out V6FeatureGate gate)
    {
        if (name is not null && Known.TryGetValue(name, out gate))
            return true;
        gate = (V6FeatureGate)(-1);
        return false;
    }

    // No enablement path exists in A0/C0. Promotion requires an exact-tuple qualification artifact.
    internal static bool IsEnabled(string? name) => TryResolve(name, out _) && false;
}
