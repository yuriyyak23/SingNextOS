namespace SingPlus.Runtime;

/// <summary>
/// Closed vocabulary for SipJob qualification contours. A gate is an operational
/// kill switch only; it never grants authority.
/// </summary>
internal enum SipJobFeatureGate
{
    Linear = 0,
    DirectSentry,
    RegionBorrow,
    RegionMove,
    MultiSessionSegment,
    BarrierModel,
    AsyncStage,
    ReadOnlyDag,
    DagParallel,
    ExternalEffectStage,
    DynamicPlanBind,
    PlanCache,
    SplitRuntime,
    HybridCpuHints,
    HybridCpuAccel,
    NativeAotJob,
    NativeIsolatedFusion,
    MutableDag,
    ConfidentialDomainFusion,
}

internal static class SipJobFeatureGates
{
    private static readonly IReadOnlyDictionary<string, SipJobFeatureGate> Known =
        new Dictionary<string, SipJobFeatureGate>(StringComparer.Ordinal)
        {
            ["FG-JOB-LINEAR"] = SipJobFeatureGate.Linear,
            ["FG-DIRECT-SENTRY"] = SipJobFeatureGate.DirectSentry,
            ["FG-REGION-BORROW"] = SipJobFeatureGate.RegionBorrow,
            ["FG-REGION-MOVE"] = SipJobFeatureGate.RegionMove,
            ["FG-MULTI-SESSION-SEGMENT"] = SipJobFeatureGate.MultiSessionSegment,
            ["FG-BARRIER-MODEL"] = SipJobFeatureGate.BarrierModel,
            ["FG-ASYNC-STAGE"] = SipJobFeatureGate.AsyncStage,
            ["FG-READONLY-DAG"] = SipJobFeatureGate.ReadOnlyDag,
            ["FG-DAG-PARALLEL"] = SipJobFeatureGate.DagParallel,
            ["FG-EXTERNAL-EFFECT-STAGE"] = SipJobFeatureGate.ExternalEffectStage,
            ["FG-DYNAMIC-PLAN-BIND"] = SipJobFeatureGate.DynamicPlanBind,
            ["FG-PLAN-CACHE"] = SipJobFeatureGate.PlanCache,
            ["FG-SPLIT-RUNTIME"] = SipJobFeatureGate.SplitRuntime,
            ["FG-HYBRIDCPU-HINTS"] = SipJobFeatureGate.HybridCpuHints,
            ["FG-HYBRIDCPU-ACCEL"] = SipJobFeatureGate.HybridCpuAccel,
            ["FG-NATIVEAOT-JOB"] = SipJobFeatureGate.NativeAotJob,
            ["FG-NATIVEISOLATED-FUSION"] = SipJobFeatureGate.NativeIsolatedFusion,
            ["FG-MUTABLE-DAG"] = SipJobFeatureGate.MutableDag,
            ["FG-CONFIDENTIAL-DOMAIN-FUSION"] = SipJobFeatureGate.ConfidentialDomainFusion,
        };

    internal static IReadOnlyCollection<string> Names { get; } =
        Array.AsReadOnly(Known.Keys.Order(StringComparer.Ordinal).ToArray());

    internal static bool TryResolve(string? name, out SipJobFeatureGate gate)
    {
        if (name is not null && Known.TryGetValue(name, out gate))
            return true;

        gate = default;
        return false;
    }

    // P14-0 intentionally exposes no configuration path. Every known and unknown
    // contour is disabled until its later phase supplies executable evidence.
    internal static bool IsEnabled(string? name) =>
        TryResolve(name, out _) && false;
}
