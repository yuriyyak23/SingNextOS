using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace SingPlus.Tests.Runtime;

public sealed class HybridBootInfoImporterTests
{
    [Fact]
    public void UsesFreshProviderDiscoveryAndNeverAdoptsFirmwarePhysicalEvidence()
    {
        var provider = Provider("os-endpoint");
        var retirement = new Retirement(FirmwareApertureDisposition.Released);
        var importer = new HybridBootInfoImporter(new Discovery("os-endpoint"), provider, retirement);
        var bootInfo = Wire(hasAperture: true, physicalMarker: 0xDEADBEEF);

        var result = importer.ImportAndDiscover(bootInfo);

        Assert.True(result.IsSuccess);
        Assert.Equal("os-endpoint", result.Value!.Endpoint.EndpointId.Value);
        Assert.Equal(new CxlDeviceGeneration(1), result.Value.Endpoint.DeviceGeneration);
        Assert.Equal(FirmwareApertureDisposition.Released, result.Value.FirmwareAperture);
        Assert.Equal(1UL, result.Value.AdmissionSequence);
        Assert.Equal(1, retirement.Calls);
        Assert.DoesNotContain(result.Value.GetType().GetProperties(), p =>
            p.PropertyType.Name.Contains("OwnedRegion", StringComparison.Ordinal) ||
            p.PropertyType.Name.Contains("RegionUse", StringComparison.Ordinal));
    }

    [Fact]
    public void BdfDsnRouteAndAddressChangesCannotChangeFreshSelection()
    {
        var provider = Provider("fresh");
        var importer = new HybridBootInfoImporter(new Discovery("fresh"), provider, new Retirement(FirmwareApertureDisposition.Stale));

        var a = importer.ImportAndDiscover(Wire(true, 1));
        var b = importer.ImportAndDiscover(Wire(true, ulong.MaxValue));

        Assert.True(a.IsSuccess && b.IsSuccess);
        Assert.Equal(a.Value!.Endpoint, b.Value!.Endpoint);
        Assert.Equal(FirmwareApertureDisposition.Stale, b.Value.FirmwareAperture);
        Assert.NotEqual(a.Value.AdmissionSequence, b.Value.AdmissionSequence);
    }

    [Fact]
    public void DeviceDisappearanceOrProviderRefusalFailsClosedAndRetiresAperture()
    {
        var retirement = new Retirement(FirmwareApertureDisposition.Quarantined);
        var result = new HybridBootInfoImporter(new Discovery("gone"), new CxlType3ModelProvider(), retirement)
            .ImportAndDiscover(Wire(true, 7));

        Assert.False(result.IsSuccess);
        Assert.Equal(HybridBootImportFailure.NoFreshEndpoint, result.Failure);
        Assert.Equal(1, retirement.Calls);
    }

    [Fact]
    public void FirmwareMappingMayBeAbsentAndNumericCollisionCannotAuthorizeAnEffect()
    {
        var result = new HybridBootInfoImporter(new Discovery("1"), Provider("1"), new Retirement(FirmwareApertureDisposition.Released))
            .ImportAndDiscover(Wire(false, 1));

        Assert.True(result.IsSuccess);
        Assert.Equal(FirmwareApertureDisposition.Absent, result.Value!.FirmwareAperture);
        Assert.Equal(1UL, result.Value.Endpoint.DeviceGeneration.Value);
        Assert.Equal(1UL, result.Value.AdmissionSequence);
        Assert.All(result.Value.BootEvidence.Diagnostics, record => Assert.True((record.Flags & BootRecordFlags.EvidenceOnly) != 0));
    }

    [Fact]
    public void CorruptionMissingSecurityAndRollbackViolationAreRejectedBeforeDiscovery()
    {
        var discovery = new Discovery("fresh");
        var importer = new HybridBootInfoImporter(discovery, Provider("fresh"), new Retirement(FirmwareApertureDisposition.Released));
        var corrupt = Wire(false, 0); corrupt[16] ^= 1;
        Assert.Equal(HybridBootImportFailure.InvalidBootInfo, importer.ImportAndDiscover(corrupt).Failure);

        var missing = HybridBootInfoCodec.Encode(new(0, Guid.NewGuid(), 1, 1, ResetReason.ColdPowerOn, 1, []));
        Assert.Equal(HybridBootImportFailure.MissingSecurityEvidence, importer.ImportAndDiscover(missing).Failure);
        Assert.Equal(0, discovery.Calls);
        Assert.Throws<ArgumentOutOfRangeException>(() => BootEvidencePayloadCodec.EncodeSecurity(
            new(BootSecurityStatus.ManifestVerified, 1, 10, 9)));
    }

    [Fact]
    public void Required_unimplemented_records_and_misclassified_cxl_evidence_fail_before_discovery()
    {
        var discovery = new Discovery("fresh");
        var importer = new HybridBootInfoImporter(discovery, Provider("fresh"), new Retirement(FirmwareApertureDisposition.Released));
        var security = new BootEvidenceRecord(BootEvidenceKind.Security, BootRecordFlags.Required | BootRecordFlags.EvidenceOnly,
            BootEvidencePayloadCodec.EncodeSecurity(new(BootSecurityStatus.ManifestVerified, 3, 4, 5)));

        var requiredPhysical = HybridBootInfoCodec.Encode(new(0, Guid.NewGuid(), 1, 1, ResetReason.ColdPowerOn, 1,
            [security, new(BootEvidenceKind.PhysicalDevice, BootRecordFlags.Required | BootRecordFlags.EvidenceOnly, new byte[] { 1 })]));
        var requiredResult = importer.ImportAndDiscover(requiredPhysical);
        Assert.Equal(HybridBootImportFailure.InvalidBootInfo, requiredResult.Failure);
        Assert.Equal(BootParseFailure.UnknownRequiredRecord, requiredResult.ParseFailure);

        var physicalWithoutEvidenceFlag = HybridBootInfoCodec.Encode(new(0, Guid.NewGuid(), 1, 1, ResetReason.ColdPowerOn, 1,
            [security, new(BootEvidenceKind.PhysicalDevice, BootRecordFlags.None, new byte[] { 1 })]));
        Assert.Equal(HybridBootImportFailure.InvalidEvidenceClassification, importer.ImportAndDiscover(physicalWithoutEvidenceFlag).Failure);

        var unsafeAperture = HybridBootInfoCodec.Encode(new(0, Guid.NewGuid(), 1, 1, ResetReason.ColdPowerOn, 1,
            [security, new(BootEvidenceKind.TemporaryAperture, BootRecordFlags.EvidenceOnly, new byte[] { 1 })]));
        Assert.Equal(HybridBootImportFailure.InvalidEvidenceClassification, importer.ImportAndDiscover(unsafeAperture).Failure);
        Assert.Equal(0, discovery.Calls);
    }

    private static CxlType3ModelProvider Provider(string endpoint)
    {
        var provider = new CxlType3ModelProvider();
        Assert.True(provider.RegisterEndpoint(new(endpoint), new($"device:{endpoint}"), 4096).IsSuccess);
        return provider;
    }

    private static byte[] Wire(bool hasAperture, ulong physicalMarker)
    {
        var flags = BootRecordFlags.Required | BootRecordFlags.EvidenceOnly;
        var records = new List<BootEvidenceRecord>
        {
            new(BootEvidenceKind.Security, flags, BootEvidencePayloadCodec.EncodeSecurity(
                new(BootSecurityStatus.ManifestVerified, 3, 4, 5))),
            new(BootEvidenceKind.PhysicalDevice, BootRecordFlags.EvidenceOnly, BitConverter.GetBytes(physicalMarker))
        };
        if (hasAperture)
            records.Add(new(BootEvidenceKind.TemporaryAperture,
                BootRecordFlags.EvidenceOnly | BootRecordFlags.Temporary | BootRecordFlags.MustNotUseAsRam,
                BitConverter.GetBytes(physicalMarker)));
        return HybridBootInfoCodec.Encode(new(0, Guid.NewGuid(), 1, 1, ResetReason.WarmSoftware, 42, records));
    }

    private sealed class Discovery(params string[] endpoints) : IFreshCxlBootDiscovery
    {
        public int Calls { get; private set; }
        public IReadOnlyList<CxlEndpointId> EnumerateCurrentEndpoints()
        {
            Calls++;
            return endpoints.Select(static x => new CxlEndpointId(x)).ToArray();
        }
    }

    private sealed class Retirement(FirmwareApertureDisposition disposition) : IFirmwareApertureRetirement
    {
        public int Calls { get; private set; }
        public FirmwareApertureDisposition ReleaseInvalidateOrQuarantine(ulong resetSequence)
        {
            Calls++;
            return disposition;
        }
    }
}
