namespace SingPlus.Contracts;

public static class IpcV2Contract
{
    public const uint Version = 2;
    public const int MaximumScatterGatherSegments = 64;
    public const int MaximumScatterGatherBytes = 4 * 1024 * 1024;
}

public enum IpcTransferSemantic
{
    Copy = 0,
    Move,
    BorrowRead,
    ScatterGatherCopy,
    RequestReply,
}

public enum IpcOwnershipDisposition
{
    NotApplicable = 0,
    SenderRetained,
    ReceiverOwned,
    BorrowActive,
    Quarantined,
}

public enum IpcTransportPath
{
    SmallCopy = 0,
    BoundedCopy,
    OwnershipTransfer,
    ReadOnlyBorrow,
    ScatterGatherCopy,
}

public enum IpcScatterGatherOverlapPolicy
{
    RejectOverlap = 0,
    AllowExactDuplicateRead,
}

public enum IpcSegmentTransferMode
{
    CopyRead = 0,
    Move,
    BorrowRead,
}

public readonly record struct IpcScatterGatherSegmentDescriptor(
    int Index,
    long Offset,
    int Length,
    IpcSegmentTransferMode Mode);

public sealed class IpcCopyPayload : IBoundedPayload
{
    private readonly byte[] _bytes;

    public IpcCopyPayload(ReadOnlySpan<byte> bytes, int maxPayloadSize)
    {
        if (maxPayloadSize <= 0 || bytes.Length > maxPayloadSize)
            throw new ArgumentOutOfRangeException(nameof(maxPayloadSize));
        _bytes = bytes.ToArray();
        MaxPayloadSize = maxPayloadSize;
    }

    public int PayloadSize => _bytes.Length;
    public int MaxPayloadSize { get; }
    public ReadOnlyMemory<byte> Bytes => _bytes;
    public IpcCopyPayload Copy() => new(_bytes, MaxPayloadSize);
}

public sealed class IpcScatterGatherPayload : IBoundedPayload
{
    private readonly byte[][] _segments;

    public IpcScatterGatherPayload(IEnumerable<ReadOnlyMemory<byte>> segments)
    {
        _segments = segments.Select(static segment => segment.ToArray()).ToArray();
        if (_segments.Length == 0 || _segments.Length > IpcV2Contract.MaximumScatterGatherSegments)
            throw new ArgumentOutOfRangeException(nameof(segments));
        PayloadSize = checked(_segments.Sum(static segment => segment.Length));
        if (PayloadSize > IpcV2Contract.MaximumScatterGatherBytes)
            throw new ArgumentOutOfRangeException(nameof(segments));
    }

    public int PayloadSize { get; }
    public int MaxPayloadSize => IpcV2Contract.MaximumScatterGatherBytes;
    public IReadOnlyList<ReadOnlyMemory<byte>> Segments => _segments.Select(static segment => (ReadOnlyMemory<byte>)segment).ToArray();
}

public sealed record IpcV2SendReceipt(
    IpcTransferSemantic Semantic,
    IpcOwnershipDisposition Ownership,
    IpcTransportPath PathEvidence,
    ChannelEnvelope Envelope,
    CausalCorrelationId? Correlation = null,
    uint ContractVersion = IpcV2Contract.Version)
{
    public bool AuthorizesTransfer => false;
    public bool GuaranteesZeroCopy => false;
}
