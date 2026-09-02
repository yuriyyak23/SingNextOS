using SingPlus.Contracts;

namespace SingPlus.Sip;

internal interface ITransferableOwnedPayload
{
    RegionHandle Handle { get; }
    OwnershipPayloadKind PayloadKind { get; }
    bool IsValidForRuntime { get; }
    object TransferForRuntime(RegionHandle newHandle);
    object CreateBorrowLeaseForRuntime(BorrowLeaseHandle handle, BorrowLeaseLifetime lifetime);
    void InvalidateForRuntime();
}

internal sealed class BorrowLeaseLifetime
{
    private int _active = 1;

    internal bool IsActive => Volatile.Read(ref _active) != 0;

    internal void InvalidateForRuntime() => Interlocked.Exchange(ref _active, 0);
}

public sealed class OwnedBuffer<T> : ITransferableOwnedPayload where T : unmanaged
{
    internal sealed class Storage(T[] data)
    {
        private BorrowLeaseLifetime? _borrowLifetime;

        public T[] Data { get; } = data;
        public bool IsAlive { get; private set; } = true;
        public bool IsBorrowed => _borrowLifetime?.IsActive == true;

        internal void BeginBorrow(BorrowLeaseLifetime lifetime)
        {
            if (!IsAlive) throw new InvalidOperationException("OwnedBuffer backing storage has been reclaimed.");
            if (IsBorrowed) throw new InvalidOperationException("OwnedBuffer already has an active runtime borrow lease.");
            _borrowLifetime = lifetime;
        }

        internal void Invalidate()
        {
            IsAlive = false;
            _borrowLifetime?.InvalidateForRuntime();
        }
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
            EnsureOwnerAccess();
            return _storage.Data.AsSpan();
        }
    }

    public BorrowedSpan<T> Borrow()
    {
        EnsureOwnerAccess();
        return new BorrowedSpan<T>(this);
    }

    public OwnedBuffer<T> Move()
    {
        EnsureOwnerAccess();
        var moved = new OwnedBuffer<T>(Handle, _storage);
        _valid = false;
        return moved;
    }

    internal Span<T> GetBorrowedSpan()
    {
        EnsureOwnerAccess();
        return _storage.Data.AsSpan();
    }

    OwnershipPayloadKind ITransferableOwnedPayload.PayloadKind => OwnershipPayloadKind.OwnedBuffer;
    bool ITransferableOwnedPayload.IsValidForRuntime => IsValid;

    object ITransferableOwnedPayload.TransferForRuntime(RegionHandle newHandle)
    {
        EnsureOwnerAccess();
        var transferred = new OwnedBuffer<T>(newHandle, _storage);
        _valid = false;
        return transferred;
    }

    object ITransferableOwnedPayload.CreateBorrowLeaseForRuntime(BorrowLeaseHandle handle, BorrowLeaseLifetime lifetime)
    {
        EnsureValid();
        _storage.BeginBorrow(lifetime);
        return new BorrowLease<T>(handle, _storage, lifetime);
    }

    void ITransferableOwnedPayload.InvalidateForRuntime()
    {
        _valid = false;
        _storage.Invalidate();
    }

    private void EnsureValid()
    {
        if (!IsValid) throw new InvalidOperationException("OwnedBuffer has been moved, transferred, released, or reclaimed.");
    }

    private void EnsureOwnerAccess()
    {
        EnsureValid();
        if (_storage.IsBorrowed) throw new InvalidOperationException("OwnedBuffer is temporarily inaccessible while a runtime borrow lease is active.");
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
