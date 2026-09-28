using YAKSys_Hybrid_CPU.Boot.Contracts;
using YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;
using Xunit;
using System.Buffers.Binary;

namespace HybridCpu_ExecutableAdapter.Tests;

public sealed class PciCxlTransportModelTests
{
    [Fact]
    public void Capability_iterator_is_bounded_and_rejects_loops_and_duplicates()
    {
        var iterator = new BoundedPciCapabilityIterator();
        Assert.Equal(TransportFailure.None, iterator.Walk(new Dictionary<ushort, PciExtendedCapability> { [0x100] = new(1, 0x100, 0x104, true), [0x104] = new(2, 0x104, 0, true) }, 0x100).Failure);
        Assert.Equal(TransportFailure.MalformedCapabilityChain, iterator.Walk(new Dictionary<ushort, PciExtendedCapability> { [0x100] = new(1, 0x100, 0x100, true) }, 0x100).Failure);
        Assert.Equal(TransportFailure.DuplicateCapability, iterator.Walk(new Dictionary<ushort, PciExtendedCapability> { [0x100] = new(1, 0x100, 0x104, true), [0x104] = new(1, 0x104, 0, true) }, 0x100).Failure);
        Assert.Equal(TransportFailure.DuplicateCapability, iterator.Walk(new Dictionary<ushort, PciExtendedCapability> { [0x100] = new(1, 0x100, 0x104, false), [0x104] = new(1, 0x104, 0, true) }, 0x100).Failure);
        Assert.Equal(TransportFailure.DuplicateCapability, iterator.Walk(new Dictionary<ushort, PciExtendedCapability> { [0x100] = new(1, 0x100, 0x104, true), [0x104] = new(1, 0x104, 0, false) }, 0x100).Failure);
    }

    [Fact]
    public void Mailbox_deadline_payload_and_unsupported_lsa_are_typed()
    {
        Assert.Equal(TransportFailure.Unsupported, new DeterministicMailboxModel(null, 1, 4096).ReadLsa(0, 1, 1).Failure);
        Assert.Equal(TransportFailure.OversizedReply, new DeterministicMailboxModel(new byte[8192], 1, 4096).ReadLsa(0, 4097, 2).Failure);
        Assert.Equal(TransportFailure.Timeout, new DeterministicMailboxModel(new byte[32], 5, 4096).ReadLsa(0, 1, 4).Failure);
        var mailbox = new DeterministicMailboxModel(new byte[32], 1, 4096);
        Assert.Equal(TransportFailure.None, mailbox.ReadLsa(0, 1, 1).Failure);
        Assert.Equal(TransportFailure.Timeout, mailbox.ReadLsa(0, 1, 1).Failure);
    }

    [Fact]
    public void Locator_crc_version_and_bounds_fail_without_blocking_canonical_fallback()
    {
        var bytes = Locator(); Assert.Equal(TransportFailure.None, HybridBootLocatorParser.Parse(bytes).Failure);
        bytes[0] ^= 1; Assert.Equal(TransportFailure.InvalidLocator, HybridBootLocatorParser.Parse(bytes).Failure);
        Assert.True(CanonicalFallbackAllowed(TransportFailure.InvalidLocator)); Assert.True(CanonicalFallbackAllowed(TransportFailure.Unsupported));

        bytes = Locator();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48), (ulong)int.MaxValue + 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(68), BootWire.Crc32C(bytes.AsSpan(0, 68)));
        Assert.Equal(TransportFailure.None, HybridBootLocatorParser.Parse(bytes).Failure);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(48), ulong.MaxValue - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(68), BootWire.Crc32C(bytes.AsSpan(0, 68)));
        Assert.Equal(TransportFailure.InvalidLocator, HybridBootLocatorParser.Parse(bytes).Failure);
    }

    [Fact]
    public void Missing_dsn_is_valid_physical_evidence()
    {
        var evidence = new BootPhysicalEvidence("type3", 0, 1, 0, 0, null, "route"); Assert.Null(evidence.Dsn);
    }

    private static bool CanonicalFallbackAllowed(TransportFailure failure) => failure is TransportFailure.Unsupported or TransportFailure.InvalidLocator or TransportFailure.Timeout;
    private static byte[] Locator()
    {
        var b = new byte[HybridBootLocatorParser.Size]; BinaryPrimitives.WriteUInt32LittleEndian(b, HybridBootLocatorParser.Magic); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 1); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), HybridBootLocatorParser.Size);
        BootWire.WriteUuid(b.AsSpan(8), Guid.NewGuid()); BootWire.WriteUuid(b.AsSpan(24), Guid.NewGuid()); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(40), 1); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(48), 0); BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(56), 16 * 1024 * 1024); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(68), BootWire.Crc32C(b.AsSpan(0, 68))); return b;
    }
}
