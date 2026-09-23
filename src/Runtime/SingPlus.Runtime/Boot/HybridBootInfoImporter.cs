using SingPlus.Platform;
using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace SingPlus.Runtime;

public enum FirmwareApertureDisposition { Absent, Released, Stale, Quarantined }
public enum HybridBootImportFailure { None, InvalidBootInfo, InvalidEvidenceClassification, MissingSecurityEvidence, DuplicateSecurityEvidence, SecurityRejected, NoFreshEndpoint, ProviderRefused }

public sealed record HybridBootEvidenceSnapshot(
    Guid PlatformId,
    ResetReason ResetReason,
    ulong ResetSequence,
    BootSecurityEvidenceV1 Security,
    bool HasTemporaryAperture,
    IReadOnlyList<BootEvidenceRecord> Diagnostics);

public sealed record FreshCxlAdmissionSnapshot(
    HybridBootEvidenceSnapshot BootEvidence,
    CxlEndpointSnapshot Endpoint,
    FirmwareApertureDisposition FirmwareAperture,
    ulong AdmissionSequence);

public readonly record struct HybridBootTakeoverResult(
    FreshCxlAdmissionSnapshot? Value, HybridBootImportFailure Failure, BootParseFailure ParseFailure, string? Detail)
{
    public bool IsSuccess => Failure == HybridBootImportFailure.None;
    public static HybridBootTakeoverResult Success(FreshCxlAdmissionSnapshot value) => new(value, HybridBootImportFailure.None, BootParseFailure.None, null);
    public static HybridBootTakeoverResult Fail(HybridBootImportFailure failure, string detail, BootParseFailure parse = BootParseFailure.None) => new(null, failure, parse, detail);
}

public interface IFreshCxlBootDiscovery
{
    IReadOnlyList<CxlEndpointId> EnumerateCurrentEndpoints();
}

public interface IFirmwareApertureRetirement
{
    FirmwareApertureDisposition ReleaseInvalidateOrQuarantine(ulong resetSequence);
}

/// <summary>
/// Validates immutable firmware evidence, then performs a new provider discovery. It never
/// converts firmware identifiers, addresses, mappings, or records into SingNext authority.
/// </summary>
public sealed class HybridBootInfoImporter(
    IFreshCxlBootDiscovery freshDiscovery,
    ICxlDiscoveryProvider provider,
    IFirmwareApertureRetirement apertureRetirement)
{
    private static readonly ISet<BootEvidenceKind> SupportedRequiredRecords = new HashSet<BootEvidenceKind> { BootEvidenceKind.Security };
    private ulong _nextAdmissionSequence;

    public HybridBootTakeoverResult ImportAndDiscover(ReadOnlySpan<byte> wireBytes)
    {
        var parsed = HybridBootInfoCodec.Parse(wireBytes, SupportedRequiredRecords);
        if (!parsed.IsSuccess)
            return HybridBootTakeoverResult.Fail(HybridBootImportFailure.InvalidBootInfo, parsed.Detail ?? "BootInfo rejected.", parsed.Failure);
        var info = parsed.Value!;
        var securityRecords = info.Records.Where(static x => x.Kind == BootEvidenceKind.Security).ToArray();
        if (securityRecords.Length == 0)
            return HybridBootTakeoverResult.Fail(HybridBootImportFailure.MissingSecurityEvidence, "BootInfo has no security evidence.");
        if (securityRecords.Length != 1)
            return HybridBootTakeoverResult.Fail(HybridBootImportFailure.DuplicateSecurityEvidence, "BootInfo has duplicate security evidence.");
        if ((securityRecords[0].Flags & (BootRecordFlags.Required | BootRecordFlags.EvidenceOnly)) !=
            (BootRecordFlags.Required | BootRecordFlags.EvidenceOnly))
            return HybridBootTakeoverResult.Fail(HybridBootImportFailure.SecurityRejected, "Security record is not required evidence.");
        var security = BootEvidencePayloadCodec.ParseSecurity(securityRecords[0].Payload.Span);
        if (!security.IsSuccess)
            return HybridBootTakeoverResult.Fail(HybridBootImportFailure.SecurityRejected, security.Detail ?? "Security evidence rejected.", security.Failure);

        foreach (var record in info.Records.Where(static x => x.Kind is BootEvidenceKind.Selection or BootEvidenceKind.PhysicalDevice or BootEvidenceKind.TemporaryAperture or BootEvidenceKind.Diagnostic))
        {
            if ((record.Flags & BootRecordFlags.EvidenceOnly) == 0)
                return HybridBootTakeoverResult.Fail(HybridBootImportFailure.InvalidEvidenceClassification, $"{record.Kind} is not classified as evidence-only.");
            if (record.Kind == BootEvidenceKind.TemporaryAperture &&
                (record.Flags & (BootRecordFlags.Temporary | BootRecordFlags.MustNotUseAsRam)) !=
                (BootRecordFlags.Temporary | BootRecordFlags.MustNotUseAsRam))
                return HybridBootTakeoverResult.Fail(HybridBootImportFailure.InvalidEvidenceClassification, "Temporary aperture is not marked temporary and unusable as RAM.");
        }

        var hasAperture = info.Records.Any(static x => x.Kind == BootEvidenceKind.TemporaryAperture);
        var evidence = new HybridBootEvidenceSnapshot(info.PlatformId, info.ResetReason, info.ResetSequence,
            security.Value, hasAperture, info.Records.ToArray());

        // Endpoint candidates come only from a current OS discovery pass, never from BootInfo.
        var sawFreshCandidate = false;
        var sawProviderRefusal = false;
        foreach (var endpointId in freshDiscovery.EnumerateCurrentEndpoints().Distinct().OrderBy(static x => x.Value, StringComparer.Ordinal))
        {
            sawFreshCandidate = true;
            var current = provider.QueryEndpoint(endpointId);
            if (!current.IsSuccess) { sawProviderRefusal = true; continue; }
            var endpoint = current.Value!;
            if (!endpoint.Available || endpoint.DeviceGeneration.Value == 0 ||
                (endpoint.Features & (CxlEndpointFeatures.Io | CxlEndpointFeatures.Memory)) != (CxlEndpointFeatures.Io | CxlEndpointFeatures.Memory))
                continue;
            var disposition = hasAperture ? apertureRetirement.ReleaseInvalidateOrQuarantine(info.ResetSequence) : FirmwareApertureDisposition.Absent;
            if (hasAperture && disposition is not (FirmwareApertureDisposition.Released or FirmwareApertureDisposition.Stale or FirmwareApertureDisposition.Quarantined))
                disposition = FirmwareApertureDisposition.Quarantined;
            return HybridBootTakeoverResult.Success(new(evidence, endpoint, disposition, checked(++_nextAdmissionSequence)));
        }

        if (hasAperture)
            _ = apertureRetirement.ReleaseInvalidateOrQuarantine(info.ResetSequence);
        if (sawFreshCandidate && sawProviderRefusal)
            return HybridBootTakeoverResult.Fail(HybridBootImportFailure.ProviderRefused, "The current provider refused a freshly enumerated endpoint.");
        return HybridBootTakeoverResult.Fail(HybridBootImportFailure.NoFreshEndpoint, "Fresh CXL discovery found no admissible endpoint.");
    }
}
