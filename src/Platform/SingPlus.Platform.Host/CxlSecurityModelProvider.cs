using SingPlus.Contracts;

namespace SingPlus.Platform.Host;

/// <summary>Evidence-only security model; it never implements an authority provider role.</summary>
public sealed class CxlSecurityModelProvider : ICxlSecurityStateProvider
{
    private sealed class Record(CxlDeviceGeneration device, CxlSecurityProperties properties, CxlSecurityHealth health)
    {
        public CxlDeviceGeneration Device { get; set; } = device;
        public CxlSecurityProperties Properties { get; set; } = properties;
        public CxlSecurityHealth Health { get; set; } = health;
        public ulong EvidenceGeneration { get; set; } = 1;
    }
    private readonly Dictionary<CxlEndpointId, Record> _records = [];

    public PlatformAuthorityResult Register(CxlEndpointId endpoint, CxlDeviceGeneration deviceGeneration,
        CxlSecurityProperties properties, CxlSecurityHealth health = CxlSecurityHealth.Ready)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Value) || deviceGeneration.Value == 0 || !Enum.IsDefined(health) || _records.ContainsKey(endpoint))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Security model registration is invalid or duplicate.");
        _records.Add(endpoint, new(deviceGeneration, properties, health));
        return PlatformAuthorityResult.Ok();
    }

    public PlatformAuthorityResult ResetSecuritySession(CxlEndpointId endpoint)
    {
        if (!_records.TryGetValue(endpoint, out var record))
            return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Unavailable, "Security endpoint is unavailable.");
        checked { record.EvidenceGeneration++; }
        record.Health = CxlSecurityHealth.Failed;
        record.Properties &= ~(CxlSecurityProperties.IdeEnabled | CxlSecurityProperties.DeviceAuthenticated);
        return PlatformAuthorityResult.Ok();
    }

    public PlatformAuthorityResult<CxlSecurityStateSnapshot> QuerySecurityState(CxlEndpointId endpointId, CxlDeviceGeneration expectedDeviceGeneration)
    {
        if (!_records.TryGetValue(endpointId, out var record))
            return PlatformAuthorityResult<CxlSecurityStateSnapshot>.Fail(PlatformAuthorityStatus.Unavailable, "Security state is unavailable.");
        if (record.Device != expectedDeviceGeneration)
            return PlatformAuthorityResult<CxlSecurityStateSnapshot>.Fail(PlatformAuthorityStatus.Stale, "Security evidence belongs to another device generation.");
        var evidence = Evidence(endpointId, record);
        return PlatformAuthorityResult<CxlSecurityStateSnapshot>.Ok(new(endpointId, record.Device,
            new(record.EvidenceGeneration), record.Properties, record.Health, evidence));
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
