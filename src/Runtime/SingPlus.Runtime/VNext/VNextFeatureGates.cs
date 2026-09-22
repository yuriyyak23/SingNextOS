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
    HostResourceAdapter,
    SemanticAdmissionSentry,
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
            ["FG-VNX-HOST-RESOURCE-ADAPTER"] = VNextFeatureGate.HostResourceAdapter,
            ["FG-VNX-SEMANTIC-SENTRY"] = VNextFeatureGate.SemanticAdmissionSentry,
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

internal enum VNextGateUseDisposition
{
    Enabled = 0,
    OrdinaryFallback,
    Quarantine,
}

internal readonly record struct VNextFeatureGateLease(
    VNextFeatureGate Gate,
    ulong ConfigurationGeneration,
    string Contour,
    string EvidenceSha256);

internal sealed record VNextFeatureGateConfiguration(
    uint Version,
    ulong Generation,
    string Contour,
    string ProviderIdentity,
    uint ProviderContractVersion,
    string RuntimeMode,
    string EvidenceSha256,
    IReadOnlyList<string> EnabledGates)
{
    internal const uint CurrentVersion = 1;

    internal static VNextFeatureGateConfiguration DefaultOff(ulong generation = 1) =>
        new(CurrentVersion, generation, "ordinary", "none", 0, "default", new string('0', 64), []);
}

/// <summary>
/// Release/configuration owner for rollout switches. It grants no semantic or
/// quantitative authority. Provider availability, receipts, plans, caches and
/// telemetry are intentionally absent from its inputs.
/// </summary>
internal sealed class VNextFeatureGateAuthority
{
    internal const string QualifiedHostContour = "host-jit/compute-time-ns/resource-contract-v1";
    private readonly object _gate = new();
    private VNextFeatureGateConfiguration _configuration = VNextFeatureGateConfiguration.DefaultOff();

    internal VNextFeatureGateConfiguration Snapshot
    {
        get { lock (_gate) return _configuration with { EnabledGates = _configuration.EnabledGates.ToArray() }; }
    }

    internal KernelResult<VNextFeatureGateConfiguration> Apply(
        ulong expectedGeneration,
        VNextFeatureGateConfiguration next)
    {
        ArgumentNullException.ThrowIfNull(next);
        lock (_gate)
        {
            if (_configuration.Generation != expectedGeneration || next.Generation != expectedGeneration + 1)
                return KernelResult<VNextFeatureGateConfiguration>.Fail(KernelError.StaleGeneration,
                    "Feature-gate configuration generation is stale.");
            var validation = Validate(next);
            if (!validation.IsSuccess)
                return KernelResult<VNextFeatureGateConfiguration>.Fail(validation.Error, validation.Message!);
            _configuration = next with
            {
                EnabledGates = next.EnabledGates.Order(StringComparer.Ordinal).ToArray()
            };
            return KernelResult<VNextFeatureGateConfiguration>.Ok(Snapshot);
        }
    }

    internal KernelResult<VNextFeatureGateLease> TryAcquire(string name)
    {
        lock (_gate)
        {
            if (!VNextFeatureGates.TryResolve(name, out var gate))
                return KernelResult<VNextFeatureGateLease>.Fail(KernelError.InvalidMessage,
                    "Unknown vNext feature gate.");
            if (!_configuration.EnabledGates.Contains(name, StringComparer.Ordinal))
                return KernelResult<VNextFeatureGateLease>.Fail(KernelError.PlatformUnsupported,
                    "The exact vNext contour is disabled; ordinary fallback is required.");
            return KernelResult<VNextFeatureGateLease>.Ok(new(gate, _configuration.Generation,
                _configuration.Contour, _configuration.EvidenceSha256));
        }
    }

    internal VNextGateUseDisposition Evaluate(VNextFeatureGateLease lease, bool possibleSubmit)
    {
        lock (_gate)
        {
            var name = VNextFeatureGates.Names.SingleOrDefault(candidate =>
                VNextFeatureGates.TryResolve(candidate, out var gate) && gate == lease.Gate);
            var current = name is not null &&
                          _configuration.Generation == lease.ConfigurationGeneration &&
                          string.Equals(_configuration.Contour, lease.Contour, StringComparison.Ordinal) &&
                          string.Equals(_configuration.EvidenceSha256, lease.EvidenceSha256, StringComparison.Ordinal) &&
                          _configuration.EnabledGates.Contains(name, StringComparer.Ordinal);
            return current
                ? VNextGateUseDisposition.Enabled
                : possibleSubmit
                    ? VNextGateUseDisposition.Quarantine
                    : VNextGateUseDisposition.OrdinaryFallback;
        }
    }

    private static KernelResult Validate(VNextFeatureGateConfiguration configuration)
    {
        if (configuration.Version != VNextFeatureGateConfiguration.CurrentVersion ||
            configuration.Generation == 0 || configuration.EnabledGates is null ||
            configuration.EnabledGates.Count != configuration.EnabledGates.Distinct(StringComparer.Ordinal).Count())
            return KernelResult.Fail(KernelError.InvalidMessage,
                "Feature-gate configuration version, generation, or gate set is invalid.");
        if (configuration.EnabledGates.Any(name => !VNextFeatureGates.TryResolve(name, out _)))
            return KernelResult.Fail(KernelError.InvalidMessage,
                "Feature-gate configuration contains an unknown gate.");

        if (configuration.EnabledGates.Count == 0)
            return KernelResult.Ok();

        string[] exactGates = ["FG-VNX-HOST-RESOURCE-ADAPTER", "FG-VNX-SIPJOB-RESOURCE"];
        var selected = configuration.EnabledGates.Order(StringComparer.Ordinal).ToArray();
        var gateSetValid = selected.SequenceEqual(exactGates.Take(1).Order(StringComparer.Ordinal)) ||
                           selected.SequenceEqual(exactGates.Order(StringComparer.Ordinal));
        if (!gateSetValid ||
            configuration.Contour != QualifiedHostContour ||
            configuration.ProviderIdentity != "host-test" ||
            configuration.ProviderContractVersion != Platform.PlatformResourceContract.ContractVersion ||
            configuration.RuntimeMode != "Windows-x64/.NET-11/JIT" ||
            configuration.EvidenceSha256.Length != 64 ||
            !configuration.EvidenceSha256.All(Uri.IsHexDigit) ||
            configuration.EvidenceSha256.All(static value => value == '0'))
            return KernelResult.Fail(KernelError.PlatformUnsupported,
                "Only the exact qualified host/JIT ComputeTime resource adapter contour may be enabled.");
        return KernelResult.Ok();
    }
}
