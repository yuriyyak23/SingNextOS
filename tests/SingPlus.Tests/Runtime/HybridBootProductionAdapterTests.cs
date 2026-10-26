using SingPlus.Platform;
using SingPlus.Runtime;
using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace SingPlus.Tests.Runtime;

public sealed class HybridBootProductionAdapterTests
{
    [Fact]
    public void P15_10_EntryAdapterRejectsNullPhysicalAddressBeforeReadingMemory()
    {
        CountingPhysicalBootInfoReader memory = new CountingPhysicalBootInfoReader();
        HybridBootInfoEntryAdapter adapter = new HybridBootInfoEntryAdapter(memory, null!);

        HybridBootTakeoverResult result = adapter.Import(0, HybridBootInfoCodec.HeaderSize);

        Assert.Equal(HybridBootImportFailure.InvalidBootInfo, result.Failure);
        Assert.Equal(0, memory.Calls);
    }

    [Fact]
    public void P15_10_EntryAdapterValidatesRangeThenCopiesBeforeParsing()
    {
        PlatformOwner platform = new PlatformOwner();
        HybridBootInfoEntryAdapter entry = HybridBootRuntimeHandoff.Create(new PhysicalReader(ValidBootInfo()), platform, platform, platform);
        Assert.Equal(HybridBootImportFailure.InvalidBootInfo, entry.Import(3, 120).Failure);
        Assert.Equal(HybridBootImportFailure.InvalidBootInfo, entry.Import(0x1000, uint.MaxValue).Failure);
        Assert.True(entry.Import(0x1000, (uint)ValidBootInfo().Length).IsSuccess);
    }

    [Fact]
    public void P15_10_FreshDiscoveryNeverUsesBootInfoIdentityAndRevalidatesProvider()
    {
        PlatformOwner platform = new PlatformOwner();
        ProviderFreshCxlBootDiscovery discovery = new ProviderFreshCxlBootDiscovery(platform, platform);
        Assert.Equal([platform.Endpoint], discovery.EnumerateCurrentEndpoints());
        platform.Available = false;
        Assert.Empty(discovery.EnumerateCurrentEndpoints());
    }

    [Theory]
    [InlineData(KernelBootMemoryKind.Mmio)]
    [InlineData(KernelBootMemoryKind.FirmwareReserved)]
    [InlineData(KernelBootMemoryKind.TemporaryCxlAperture)]
    [InlineData(KernelBootMemoryKind.Reserved)]
    public void P15_10_PhysicalBootInfoReaderRejectsEveryNonRamClass(KernelBootMemoryKind kind)
    {
        byte[] destination = Enumerable.Repeat((byte)0xcc, 16).ToArray();
        KernelOwnedPhysicalBootInfoReader reader = new KernelOwnedPhysicalBootInfoReader(new MemoryMap(kind), new PhysicalMemory(new byte[16]),
            new CopyEpoch(1), 1);

        Assert.False(reader.TryCopy(0x1000, destination));
        Assert.All(destination, static value => Assert.Equal(0, value));
    }

    [Fact]
    public void P15_10_PhysicalBootInfoReaderRejectsKernelOverlapAndResetDuringCopy()
    {
        byte[] bytes = Enumerable.Range(0, 16).Select(static value => (byte)value).ToArray();
        byte[] destination = new byte[bytes.Length];
        KernelOwnedPhysicalBootInfoReader overlap = new KernelOwnedPhysicalBootInfoReader(new MemoryMap(KernelBootMemoryKind.NormalRam, overlap: true),
            new PhysicalMemory(bytes), new CopyEpoch(1), 1);
        Assert.False(overlap.TryCopy(0x1000, destination));

        KernelOwnedPhysicalBootInfoReader reset = new KernelOwnedPhysicalBootInfoReader(new MemoryMap(KernelBootMemoryKind.NormalRam),
            new PhysicalMemory(bytes), new CopyEpoch(1, 2), 1);
        Assert.False(reset.TryCopy(0x1000, destination));
        Assert.All(destination, static value => Assert.Equal(0, value));

        KernelOwnedPhysicalBootInfoReader stable = new KernelOwnedPhysicalBootInfoReader(new MemoryMap(KernelBootMemoryKind.NormalRam),
            new PhysicalMemory(bytes), new CopyEpoch(1), 1);
        Assert.True(stable.TryCopy(0x1000, destination));
        Assert.Equal(bytes, destination);
    }

    [Theory]
    [InlineData(FirmwareBootApertureRetirementStatus.Released, FirmwareApertureDisposition.Released)]
    [InlineData(FirmwareBootApertureRetirementStatus.ProvenPreviouslyRetired, FirmwareApertureDisposition.Stale)]
    [InlineData(FirmwareBootApertureRetirementStatus.Unsupported, FirmwareApertureDisposition.Quarantined)]
    [InlineData(FirmwareBootApertureRetirementStatus.Ambiguous, FirmwareApertureDisposition.Quarantined)]
    public void P15_10_RetirementOnlyClaimsReleaseFromExactOwnerEvidence(
        FirmwareBootApertureRetirementStatus status, FirmwareApertureDisposition expected)
    {
        PlatformOwner platform = new PlatformOwner { Retirement = status };
        Assert.Equal(expected, new PlatformFirmwareApertureRetirement(platform).ReleaseInvalidateOrQuarantine(9));
        platform.ReturnWrongResetSequence = true;
        Assert.Equal(FirmwareApertureDisposition.Quarantined, new PlatformFirmwareApertureRetirement(platform).ReleaseInvalidateOrQuarantine(9));
    }

    private static byte[] ValidBootInfo()
    {
        byte[] security = BootEvidencePayloadCodec.EncodeSecurity(new(BootSecurityStatus.ManifestVerified, 1, 1, 1));
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

    private sealed class MemoryMap(KernelBootMemoryKind kind, bool overlap = false) : IKernelBootMemoryMap
    {
        public bool TryClassify(ulong physicalAddress, ulong byteLength, out KernelBootMemoryKind value)
        {
            value = kind;
            return physicalAddress <= ulong.MaxValue - byteLength;
        }
        public bool OverlapsKernelImage(ulong physicalAddress, ulong byteLength) => overlap;
    }

    private sealed class PhysicalMemory(byte[] bytes) : IKernelPhysicalMemory
    {
        public bool TryCopyFromPhysical(ulong physicalAddress, Span<byte> destination)
        {
            if (destination.Length != bytes.Length) return false;
            bytes.CopyTo(destination);
            return true;
        }
    }

    private sealed class CopyEpoch(params ulong[] values) : IKernelBootCopyEpoch
    {
        private int _index;
        public ulong CurrentEpoch => values[Math.Min(_index++, values.Length - 1)];
    }

    private sealed class CountingPhysicalBootInfoReader : IKernelPhysicalBootInfoReader
    {
        public int Calls { get; private set; }

        public bool TryCopy(ulong physicalAddress, Span<byte> kernelOwnedDestination)
        {
            Calls++;
            return false;
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
