using System.Security.Cryptography;
using System.Text;
using SingPlus.Platform;

namespace SingPlus.Runtime;

internal enum VNextCallerContract
{
    LegacySipV1 = 1,
    ResourceAwareSipV2 = 2,
}

internal enum VNextResourceMigrationRequirement
{
    None = 0,
    OptionalTransportOptimization,
    RequiredForSemanticExecution,
}

internal enum VNextMigrationDisposition
{
    OrdinaryFallback = 0,
    ResourcePath,
    Denied,
    Quarantined,
}

internal readonly record struct VNextMigrationRequest(
    uint Version,
    VNextCallerContract CallerContract,
    VNextResourceMigrationRequirement Requirement,
    uint ProviderResourceContractVersion,
    VNextFeatureGateLease? GateLease,
    bool PossibleSubmit)
{
    internal const uint CurrentVersion = 1;
}

internal readonly record struct VNextMigrationDecision(
    VNextMigrationDisposition Disposition,
    KernelError Error,
    string Message)
{
    internal bool IsSuccess => Error == KernelError.None;
}

/// <summary>
/// Additive migration policy only. It does not grant semantic effects, reserve
/// budgets, bind providers, publish output, or own compatibility truth outside
/// the exact generation-bound gate and live-consumer inputs supplied to it.
/// </summary>
internal static class VNextMigrationCoordinator
{
    internal const string HostResourceGate = "FG-VNX-HOST-RESOURCE-ADAPTER";

    internal static VNextMigrationDecision Evaluate(
        VNextFeatureGateAuthority gates,
        VNextMigrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(gates);
        if (request.Version != VNextMigrationRequest.CurrentVersion ||
            !Enum.IsDefined(request.CallerContract) ||
            !Enum.IsDefined(request.Requirement))
            return Denied(KernelError.InvalidMessage, "Unknown migration request version or enum value.");

        if (request.CallerContract == VNextCallerContract.LegacySipV1)
        {
            if (request.Requirement != VNextResourceMigrationRequirement.None ||
                request.ProviderResourceContractVersion != 0 || request.GateLease is not null || request.PossibleSubmit)
                return Denied(KernelError.InvalidMessage,
                    "A legacy SIP caller cannot carry resource intent, provider version, gate lease, or submit state.");
            return Ordinary("Legacy SIP v1 remains on the ordinary semantic contour.");
        }

        if (request.Requirement == VNextResourceMigrationRequirement.None)
        {
            if (request.GateLease is not null || request.PossibleSubmit)
                return Denied(KernelError.InvalidMessage,
                    "A request without resource intent cannot carry a resource gate lease or submit state.");
            return Ordinary("The resource-aware caller requested no resource contour.");
        }

        if (request.ProviderResourceContractVersion is not 0 and not PlatformResourceContract.ContractVersion)
            return Denied(KernelError.PlatformUnsupported,
                "Unknown provider resource contract versions fail closed without version guessing.");

        if (request.ProviderResourceContractVersion == 0 || request.GateLease is null)
            return request.Requirement == VNextResourceMigrationRequirement.OptionalTransportOptimization
                ? Ordinary("The old provider or disabled gate preserves the ordinary semantic contour.")
                : Denied(KernelError.PlatformUnsupported,
                    "Required resource execution cannot fall back to an old provider or disabled gate.");

        var gate = gates.Evaluate(request.GateLease.Value, request.PossibleSubmit);
        if (gate == VNextGateUseDisposition.Quarantine)
            return new(VNextMigrationDisposition.Quarantined, KernelError.Quarantined,
                "Rollback after possible submit requires exact reconciliation; fallback would risk resubmission.");
        if (gate == VNextGateUseDisposition.OrdinaryFallback)
            return request.Requirement == VNextResourceMigrationRequirement.OptionalTransportOptimization
                ? Ordinary("Pre-submit rollback returned optional work to the ordinary contour.")
                : Denied(KernelError.PlatformUnsupported,
                    "Pre-submit rollback cannot weaken required resource semantics.");
        return new(VNextMigrationDisposition.ResourcePath, KernelError.None,
            "The exact versioned provider and gate generation admit the resource path.");
    }

    private static VNextMigrationDecision Ordinary(string message) =>
        new(VNextMigrationDisposition.OrdinaryFallback, KernelError.None, message);

    private static VNextMigrationDecision Denied(KernelError error, string message) =>
        new(VNextMigrationDisposition.Denied, error, message);
}

internal enum VNextCompatibilityConsumerKind
{
    LegacyManifest = 1,
    LegacyGeneratedSip,
    LegacyProvider,
    OrdinaryFallback,
}

internal readonly record struct VNextCompatibilityConsumerId(ulong Value);

internal readonly record struct VNextCompatibilityConsumerLease(
    VNextCompatibilityConsumerId Id,
    ulong Generation,
    VNextCompatibilityConsumerKind Kind,
    uint ContractVersion);

internal readonly record struct VNextNoLiveConsumerProof(
    uint Version,
    string CompatibilityPath,
    ulong RegistryGeneration,
    string SnapshotSha256)
{
    internal const uint CurrentVersion = 1;
}

/// <summary>
/// Tracks migration consumers solely to prove whether compatibility cleanup is
/// safe. The registry has no capability, budget, provider, Region, publication,
/// or feature-gate transition API.
/// </summary>
internal sealed class VNextCompatibilityUsageRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<VNextCompatibilityConsumerId, VNextCompatibilityConsumerLease> _live = [];
    private ulong _generation = 1;

    internal ulong Generation { get { lock (_gate) return _generation; } }

    internal KernelResult<VNextCompatibilityConsumerLease> Register(
        VNextCompatibilityConsumerId id,
        VNextCompatibilityConsumerKind kind,
        uint contractVersion)
    {
        if (id.Value == 0 || !Enum.IsDefined(kind) || contractVersion == 0)
            return KernelResult<VNextCompatibilityConsumerLease>.Fail(KernelError.InvalidMessage,
                "Compatibility consumer identity, kind, and contract version must be canonical.");
        lock (_gate)
        {
            if (_live.ContainsKey(id))
                return KernelResult<VNextCompatibilityConsumerLease>.Fail(KernelError.DuplicateIdentity,
                    "Compatibility consumer identity is already live.");
            if (_generation == ulong.MaxValue)
                return KernelResult<VNextCompatibilityConsumerLease>.Fail(KernelError.CapacityExhausted,
                    "Compatibility usage generation is exhausted.");
            var nextGeneration = _generation + 1;
            var lease = new VNextCompatibilityConsumerLease(id, nextGeneration, kind, contractVersion);
            _live.Add(id, lease);
            _generation = nextGeneration;
            return KernelResult<VNextCompatibilityConsumerLease>.Ok(lease);
        }
    }

    internal KernelResult Release(VNextCompatibilityConsumerLease lease)
    {
        lock (_gate)
        {
            if (!_live.TryGetValue(lease.Id, out var current) || current != lease)
                return KernelResult.Fail(KernelError.StaleGeneration,
                    "Compatibility consumer lease is stale or already released.");
            if (_generation == ulong.MaxValue)
                return KernelResult.Fail(KernelError.CapacityExhausted,
                    "Compatibility usage generation is exhausted.");
            var nextGeneration = _generation + 1;
            _live.Remove(lease.Id);
            _generation = nextGeneration;
            return KernelResult.Ok();
        }
    }

    internal KernelResult<VNextNoLiveConsumerProof> TryProveNoLiveConsumers(
        string compatibilityPath,
        ulong expectedGeneration)
    {
        if (string.IsNullOrWhiteSpace(compatibilityPath))
            return KernelResult<VNextNoLiveConsumerProof>.Fail(KernelError.InvalidMessage,
                "Compatibility path identity is required.");
        lock (_gate)
        {
            if (_generation != expectedGeneration)
                return KernelResult<VNextNoLiveConsumerProof>.Fail(KernelError.StaleGeneration,
                    "Compatibility usage generation changed during the cleanup scan.");
            if (_live.Count != 0)
                return KernelResult<VNextNoLiveConsumerProof>.Fail(KernelError.InvalidTransition,
                    "Compatibility cleanup is forbidden while any live consumer remains.");
            var material = Encoding.UTF8.GetBytes(
                $"v{VNextNoLiveConsumerProof.CurrentVersion}\n{compatibilityPath}\n{_generation}\nno-live-consumers");
            return KernelResult<VNextNoLiveConsumerProof>.Ok(new(
                VNextNoLiveConsumerProof.CurrentVersion, compatibilityPath, _generation,
                Convert.ToHexStringLower(SHA256.HashData(material))));
        }
    }

    internal bool IsCurrent(VNextNoLiveConsumerProof proof)
    {
        lock (_gate)
        {
            if (proof.Version != VNextNoLiveConsumerProof.CurrentVersion ||
                proof.RegistryGeneration != _generation || _live.Count != 0 ||
                string.IsNullOrWhiteSpace(proof.CompatibilityPath) ||
                !IsCanonicalSha256(proof.SnapshotSha256)) return false;
            var material = Encoding.UTF8.GetBytes(
                $"v{proof.Version}\n{proof.CompatibilityPath}\n{proof.RegistryGeneration}\nno-live-consumers");
            var expected = Convert.ToHexStringLower(SHA256.HashData(material));
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(proof.SnapshotSha256));
        }
    }

    private static bool IsCanonicalSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
