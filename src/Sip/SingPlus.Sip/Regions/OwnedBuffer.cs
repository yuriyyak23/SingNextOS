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

[Flags]
internal enum RuntimeBufferAccess
{
    Read = 1 << 0,
    Write = 1 << 1,
}

internal sealed class RuntimeBufferReservationLifetime
{
    private int _active = 1;

    internal bool IsActive => Volatile.Read(ref _active) != 0;

    internal void Invalidate() => Interlocked.Exchange(ref _active, 0);
}

public sealed class OwnedBuffer<T> : ITransferableOwnedPayload where T : unmanaged
{
    internal sealed class Storage(T[] data)
    {
        private readonly object _stateGate = new();
        private BorrowLeaseLifetime? _borrowLifetime;
        private RuntimeBufferReservationLifetime? _runtimeReservation;
        private bool _isAlive = true;

        public T[] Data { get; } = data;
        public bool IsAlive
        {
            get
            {
                lock (_stateGate) return _isAlive;
            }
        }

        public bool IsBorrowed
        {
            get
            {
                lock (_stateGate) return _borrowLifetime?.IsActive == true;
            }
        }

        public bool IsRuntimeReserved
        {
            get
            {
                lock (_stateGate) return _runtimeReservation?.IsActive == true;
            }
        }

        internal void BeginBorrow(BorrowLeaseLifetime lifetime)
        {
            lock (_stateGate)
            {
                if (!_isAlive) throw new InvalidOperationException("OwnedBuffer backing storage has been reclaimed.");
                if (_borrowLifetime?.IsActive == true) throw new InvalidOperationException("OwnedBuffer already has an active runtime borrow lease.");
                if (_runtimeReservation?.IsActive == true) throw new InvalidOperationException("OwnedBuffer is reserved by an active runtime operation.");
                _borrowLifetime = lifetime;
            }
        }

        internal void BeginRuntimeReservation(RuntimeBufferReservationLifetime lifetime)
        {
            lock (_stateGate)
            {
                if (!_isAlive) throw new InvalidOperationException("OwnedBuffer backing storage has been reclaimed.");
                if (_borrowLifetime?.IsActive == true) throw new InvalidOperationException("OwnedBuffer has an active runtime borrow lease.");
                if (_runtimeReservation?.IsActive == true) throw new InvalidOperationException("OwnedBuffer is already reserved by a runtime operation.");
                _runtimeReservation = lifetime;
            }
        }

        internal void EndRuntimeReservation(RuntimeBufferReservationLifetime lifetime)
        {
            lock (_stateGate)
            {
                if (!ReferenceEquals(_runtimeReservation, lifetime) || !lifetime.IsActive)
                    throw new InvalidOperationException("OwnedBuffer runtime reservation is stale or already released.");

                lifetime.Invalidate();
                _runtimeReservation = null;
            }
        }

        internal void Invalidate()
        {
            lock (_stateGate)
            {
                _isAlive = false;
                _borrowLifetime?.InvalidateForRuntime();
                _runtimeReservation?.Invalidate();
            }
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

    internal RuntimeBufferLease<T> ReserveForRuntime(RuntimeBufferAccess access)
    {
        if (access is not RuntimeBufferAccess.Read and not RuntimeBufferAccess.Write)
            throw new ArgumentOutOfRangeException(nameof(access));

        EnsureOwnerAccess();
        var lifetime = new RuntimeBufferReservationLifetime();
        _storage.BeginRuntimeReservation(lifetime);
        return new RuntimeBufferLease<T>(_storage, lifetime, access);
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
        if (_storage.IsRuntimeReserved) throw new InvalidOperationException("OwnedBuffer is temporarily inaccessible while a runtime operation owns its CPU-access reservation.");
    }
}

internal sealed class RuntimeBufferLease<T> : IDisposable where T : unmanaged
{
    private readonly OwnedBuffer<T>.Storage _storage;
    private readonly RuntimeBufferReservationLifetime _lifetime;
    private readonly RuntimeBufferAccess _access;
    private int _disposed;

    internal RuntimeBufferLease(
        OwnedBuffer<T>.Storage storage,
        RuntimeBufferReservationLifetime lifetime,
        RuntimeBufferAccess access)
    {
        _storage = storage;
        _lifetime = lifetime;
        _access = access;
    }

    internal bool IsActive => _lifetime.IsActive && _storage.IsAlive;

    internal ReadOnlySpan<T> ReadOnlySpan
    {
        get
        {
            EnsureAccess(RuntimeBufferAccess.Read);
            return _storage.Data.AsSpan();
        }
    }

    internal Span<T> WritableSpan
    {
        get
        {
            EnsureAccess(RuntimeBufferAccess.Write);
            return _storage.Data.AsSpan();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!IsActive) return;
        _storage.EndRuntimeReservation(_lifetime);
    }

    private void EnsureAccess(RuntimeBufferAccess required)
    {
        if (!IsActive)
            throw new InvalidOperationException("The runtime buffer reservation is no longer active.");
        if ((_access & required) != required)
            throw new InvalidOperationException("The runtime buffer reservation does not grant the requested access.");
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
