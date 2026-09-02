using SingPlus.Contracts;

namespace SingPlus.Sip;

internal interface ITransferableOwnedPayload
{
    RegionHandle Handle { get; }
    bool IsValidForRuntime { get; }
    object TransferForRuntime(RegionHandle newHandle);
    void InvalidateForRuntime();
}

public sealed class OwnedBuffer<T> : ITransferableOwnedPayload where T : unmanaged
{
    private sealed class Storage(T[] data)
    {
        public T[] Data { get; } = data;
        public bool IsAlive { get; set; } = true;
    }

    private readonly Storage _storage;
    private bool _valid;

    internal OwnedBuffer(RegionHandle handle, T[] data) : this(handle, new Storage(data))
    {
    }

    private OwnedBuffer(RegionHandle handle, Storage storage)
    {
        Handle = handle;
        _storage = storage;
        _valid = true;
    }

    public RegionHandle Handle { get; private set; }
    public bool IsValid => _valid && _storage.IsAlive;
    public int Length
    {
        get
        {
            EnsureValid();
            return _storage.Data.Length;
        }
    }

    public Span<T> Span
    {
        get
        {
            EnsureValid();
            return _storage.Data.AsSpan();
        }
    }

    public BorrowedSpan<T> Borrow()
    {
        EnsureValid();
        return new BorrowedSpan<T>(this);
    }

    public OwnedBuffer<T> Move()
    {
        EnsureValid();
        var moved = new OwnedBuffer<T>(Handle, _storage);
        _valid = false;
        return moved;
    }

    internal Span<T> GetBorrowedSpan()
    {
        EnsureValid();
        return _storage.Data.AsSpan();
    }

    bool ITransferableOwnedPayload.IsValidForRuntime => IsValid;

    object ITransferableOwnedPayload.TransferForRuntime(RegionHandle newHandle)
    {
        EnsureValid();
        var transferred = new OwnedBuffer<T>(newHandle, _storage);
        _valid = false;
        return transferred;
    }

    void ITransferableOwnedPayload.InvalidateForRuntime()
    {
        _valid = false;
        _storage.IsAlive = false;
    }

    private void EnsureValid()
    {
        if (!IsValid) throw new InvalidOperationException("OwnedBuffer has been moved, transferred, released, or reclaimed.");
    }
}

public readonly ref struct BorrowedSpan<T> where T : unmanaged
{
    private readonly OwnedBuffer<T> _owner;

    internal BorrowedSpan(OwnedBuffer<T> owner) => _owner = owner;

    public Span<T> Span => _owner.GetBorrowedSpan();
    public int Length => Span.Length;
    public ref T this[int index] => ref Span[index];
}
