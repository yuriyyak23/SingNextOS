using SingPlus.Contracts;

using System.Security.Cryptography;
using System.Text;

namespace SingPlus.Platform.Host;

/// <summary>Evidence-only security model; it never implements an authority provider role.</summary>
public sealed class CxlSecurityModelProvider : ICxlSecurityStateProvider
{
    private sealed class Record(CxlDeviceGeneration device, CxlSecurityProperties properties,
        CxlSecurityHealth health, string firmwareMeasurementDigest)
    {
        public CxlDeviceGeneration Device { get; set; } = device;
        public CxlSecurityProperties Properties { get; set; } = properties;
        public CxlSecurityHealth Health { get; set; } = health;
        public ulong EvidenceGeneration { get; set; } = 1;
        public ulong ProviderTrustGeneration { get; set; } = 1;
        public ulong ResetGeneration { get; set; } = 1;
        public string FirmwareMeasurementDigest { get; } = firmwareMeasurementDigest;
    }
    private readonly Dictionary<CxlEndpointId, Record> _records = [];
    private readonly object _sync = new();

    public PlatformAuthorityResult Register(CxlEndpointId endpoint, CxlDeviceGeneration deviceGeneration,
        CxlSecurityProperties properties, CxlSecurityHealth health = CxlSecurityHealth.Ready,
        string? firmwareMeasurementDigest = null)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Value) || deviceGeneration.Value == 0 || !Enum.IsDefined(health))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Security model registration is invalid or duplicate.");
        var measurement = firmwareMeasurementDigest ?? Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"model:{endpoint.Value}:{deviceGeneration.Value}")));
        if (measurement.Length != 64 || measurement.Any(character =>
                !Uri.IsHexDigit(character) || character is >= 'A' and <= 'F'))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied,
                "Firmware measurement must be lowercase SHA-256 hex.");
        lock (_sync)
        {
            if (_records.ContainsKey(endpoint))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Security model registration is invalid or duplicate.");
            _records.Add(endpoint, new(deviceGeneration, properties, health, measurement));
            return PlatformAuthorityResult.Ok();
        }
    }

    public PlatformAuthorityResult ResetSecuritySession(CxlEndpointId endpoint)
    {
        lock (_sync)
        {
            if (!_records.TryGetValue(endpoint, out var record))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Unavailable, "Security endpoint is unavailable.");
            if (record.EvidenceGeneration == ulong.MaxValue ||
                record.ProviderTrustGeneration == ulong.MaxValue || record.ResetGeneration == ulong.MaxValue)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted,
                    "Security model generation is exhausted; no partial reset was recorded.");
            record.EvidenceGeneration++;
            record.ProviderTrustGeneration++;
            record.ResetGeneration++;
            record.Health = CxlSecurityHealth.Failed;
            record.Properties &= ~(CxlSecurityProperties.IdeEnabled | CxlSecurityProperties.DeviceAuthenticated);
            return PlatformAuthorityResult.Ok();
        }
    }

    public PlatformAuthorityResult<CxlSecurityStateSnapshot> QuerySecurityState(CxlEndpointId endpointId, CxlDeviceGeneration expectedDeviceGeneration)
    {
        lock (_sync)
        {
            if (!_records.TryGetValue(endpointId, out var record))
                return PlatformAuthorityResult<CxlSecurityStateSnapshot>.Fail(PlatformAuthorityStatus.Unavailable, "Security state is unavailable.");
            if (record.Device != expectedDeviceGeneration)
                return PlatformAuthorityResult<CxlSecurityStateSnapshot>.Fail(PlatformAuthorityStatus.Stale, "Security evidence belongs to another device generation.");
            var evidence = Evidence(endpointId, record);
            return PlatformAuthorityResult<CxlSecurityStateSnapshot>.Ok(new(endpointId, record.Device,
                new(record.EvidenceGeneration), record.Properties, record.Health, evidence,
                CxlSecurityEvidenceAssurance.ModelOnly, record.FirmwareMeasurementDigest,
                record.ProviderTrustGeneration, record.ResetGeneration));
        }
    }

    public PlatformAuthorityResult<EvidenceRecord> QuerySecurityEvidence(CxlEndpointId endpointId, CxlDeviceGeneration expectedGeneration)
    {
        var state = QuerySecurityState(endpointId, expectedGeneration);
        return state.IsSuccess
            ? PlatformAuthorityResult<EvidenceRecord>.Ok(state.Value!.Evidence)
            : PlatformAuthorityResult<EvidenceRecord>.Fail(state.Status, state.Message!);
    }

    private static EvidenceRecord Evidence(CxlEndpointId endpoint, Record record) => new(
        new("cxl.security-state", 1),
        new(new DomainId(0), new ProcessId(0), 0, null, null),
        new("cxl-security-model"), EvidenceVisibilityClass.SecurityMeasurement,
        record.Health.ToString(), $"endpoint={endpoint.Value};properties={record.Properties}",
        new(record.EvidenceGeneration, record.EvidenceGeneration), false);
}
