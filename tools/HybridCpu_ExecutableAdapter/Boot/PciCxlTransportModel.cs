using YAKSys_Hybrid_CPU.Boot.Contracts;
using System.Buffers.Binary;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;

internal readonly record struct PciExtendedCapability(ushort Id, ushort Offset, ushort NextOffset, bool RequiredUnique);
internal enum TransportFailure { None, MalformedCapabilityChain, DuplicateCapability, BudgetExceeded, Timeout, OversizedReply, Unsupported, InvalidLocator }
internal readonly record struct TransportResult<T>(T? Value, TransportFailure Failure) where T : class;

internal sealed class BoundedPciCapabilityIterator
{
    public TransportResult<IReadOnlyList<PciExtendedCapability>> Walk(IReadOnlyDictionary<ushort, PciExtendedCapability> space, ushort first, int maximum = 64)
    {
        var result = new List<PciExtendedCapability>(); var seenOffsets = new HashSet<ushort>(); var seenIds = new HashSet<ushort>(); var uniqueIds = new HashSet<ushort>(); var current = first;
        while (current != 0)
        {
            if (result.Count >= maximum) return new(null, TransportFailure.BudgetExceeded);
            if (current < 0x100 || (current & 3) != 0 || !seenOffsets.Add(current) || !space.TryGetValue(current, out var cap)) return new(null, TransportFailure.MalformedCapabilityChain);
            if (cap.Offset != current) return new(null, TransportFailure.DuplicateCapability);
            if (seenIds.Contains(cap.Id) && (cap.RequiredUnique || uniqueIds.Contains(cap.Id))) return new(null, TransportFailure.DuplicateCapability);
            seenIds.Add(cap.Id);
            if (cap.RequiredUnique) uniqueIds.Add(cap.Id);
            result.Add(cap); current = cap.NextOffset;
        }
        return new(result, TransportFailure.None);
    }
}

internal sealed class DeterministicMailboxModel(byte[]? lsa, int completionOperation, int maxPayload)
{
    private int _operation;
    public TransportResult<byte[]> ReadLsa(int offset, int length, int deadlineOperation)
    {
        _operation++;
        if (lsa is null) return new(null, TransportFailure.Unsupported);
        if (length < 0 || length > maxPayload) return new(null, TransportFailure.OversizedReply);
        if (completionOperation > deadlineOperation || _operation > deadlineOperation) return new(null, TransportFailure.Timeout);
        if (offset < 0 || offset > lsa.Length - length) return new(null, TransportFailure.InvalidLocator);
        return new(lsa.AsSpan(offset, length).ToArray(), TransportFailure.None);
    }
}

internal sealed record HybridBootLocator(Guid BootVolumeId, Guid ReplicaId, ulong LocatorGeneration, ulong AnchorOffset, ulong AnchorLength);
internal static class HybridBootLocatorParser
{
    internal const uint Magic = 0x314C4248; internal const int Size = 72;
    public static TransportResult<HybridBootLocator> Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Size || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic || BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) != Size) return new(null, TransportFailure.InvalidLocator);
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[68..]) != BootWire.Crc32C(bytes[..68])) return new(null, TransportFailure.InvalidLocator);
        var length = BinaryPrimitives.ReadUInt64LittleEndian(bytes[56..]); var offset = BinaryPrimitives.ReadUInt64LittleEndian(bytes[48..]);
        if (length == 0 || offset > ulong.MaxValue - length) return new(null, TransportFailure.InvalidLocator);
        return new(new(BootWire.ReadUuid(bytes[8..]), BootWire.ReadUuid(bytes[24..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]), offset, length), TransportFailure.None);
    }
}
