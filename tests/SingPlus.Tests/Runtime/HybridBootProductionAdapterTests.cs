using SingPlus.Platform;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace SingPlus.Tests.Runtime;

public sealed class HybridBootProductionAdapterTests
{
    [Fact]
    public void P15_10_EntryAdapterValidatesRangeThenCopiesBeforeParsing()
    {
        var platform = new PlatformOwner();
        var entry = HybridBootRuntimeHandoff.Create(new PhysicalReader(ValidBootInfo()), platform, platform, platform);
        Assert.Equal(HybridBootImportFailure.InvalidBootInfo, entry.Import(3, 120).Failure);
        Assert.Equal(HybridBootImportFailure.InvalidBootInfo, entry.Import(0x1000, uint.MaxValue).Failure);
        Assert.True(entry.Import(0x1000, (uint)ValidBootInfo().Length).IsSuccess);
    }

    [Fact]
    public void P15_10_FreshDiscoveryNeverUsesBootInfoIdentityAndRevalidatesProvider()
    {
        var platform = new PlatformOwner();
        var discovery = new ProviderFreshCxlBootDiscovery(platform, platform);
        Assert.Equal([platform.Endpoint], discovery.EnumerateCurrentEndpoints());
        platform.Available = false;
        Assert.Empty(discovery.EnumerateCurrentEndpoints());
    }

    [Theory]
    [InlineData(FirmwareBootApertureRetirementStatus.Released, FirmwareApertureDisposition.Released)]
    [InlineData(FirmwareBootApertureRetirementStatus.ProvenPreviouslyRetired, FirmwareApertureDisposition.Stale)]
    [InlineData(FirmwareBootApertureRetirementStatus.Unsupported, FirmwareApertureDisposition.Quarantined)]
    [InlineData(FirmwareBootApertureRetirementStatus.Ambiguous, FirmwareApertureDisposition.Quarantined)]
    public void P15_10_RetirementOnlyClaimsReleaseFromExactOwnerEvidence(
        FirmwareBootApertureRetirementStatus status, FirmwareApertureDisposition expected)
    {
        var platform = new PlatformOwner { Retirement = status };
        Assert.Equal(expected, new PlatformFirmwareApertureRetirement(platform).ReleaseInvalidateOrQuarantine(9));
        platform.ReturnWrongResetSequence = true;
        Assert.Equal(FirmwareApertureDisposition.Quarantined, new PlatformFirmwareApertureRetirement(platform).ReleaseInvalidateOrQuarantine(9));
    }

    private static byte[] ValidBootInfo()
    {
        var security = BootEvidencePayloadCodec.EncodeSecurity(new(BootSecurityStatus.ManifestVerified, 1, 1, 1));
        return HybridBootInfoCodec.Encode(new(0, Guid.NewGuid(), 6, 1, ResetReason.ColdPowerOn, 9,
        [
            new(BootEvidenceKind.Security, BootRecordFlags.Required | BootRecordFlags.EvidenceOnly, security),
            new(BootEvidenceKind.TemporaryAperture, BootRecordFlags.EvidenceOnly | BootRecordFlags.Temporary | BootRecordFlags.MustNotUseAsRam, new byte[] { 1 })
        ]));
    }

    private sealed class PhysicalReader(byte[] bytes) : IKernelPhysicalBootInfoReader
    {
        public bool TryCopy(ulong physicalAddress, Span<byte> kernelOwnedDestination)
        {
            if (kernelOwnedDestination.Length != bytes.Length) return false;
            bytes.CopyTo(kernelOwnedDestination);
            return true;
        }
    }

    private sealed class PlatformOwner : ICxlEndpointEnumerationProvider, ICxlDiscoveryProvider, IFirmwareBootApertureOwner
    {
        public CxlEndpointId Endpoint { get; } = new("current-provider-enumeration");
        public bool Available { get; set; } = true;
        public FirmwareBootApertureRetirementStatus Retirement { get; set; } = FirmwareBootApertureRetirementStatus.Released;
        public bool ReturnWrongResetSequence { get; set; }
        public PlatformAuthorityResult<IReadOnlyList<CxlEndpointId>> EnumerateCurrentEndpoints() =>
            PlatformAuthorityResult<IReadOnlyList<CxlEndpointId>>.Ok([Endpoint]);
        public PlatformAuthorityResult<CxlEndpointSnapshot> QueryEndpoint(CxlEndpointId endpointId) =>
            Available && endpointId == Endpoint
                ? PlatformAuthorityResult<CxlEndpointSnapshot>.Ok(new(Endpoint, new(44), CxlEndpointFeatures.Io | CxlEndpointFeatures.Memory, true))
                : PlatformAuthorityResult<CxlEndpointSnapshot>.Fail(PlatformAuthorityStatus.Unavailable, "gone");
        public FirmwareBootApertureRetirementResult RetireFirmwareBootAperture(ulong resetSequence) =>
            new(Retirement, ReturnWrongResetSequence ? resetSequence + 1 : resetSequence, null);
    }
}
