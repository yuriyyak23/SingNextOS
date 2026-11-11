using SingPlus.Contracts;

namespace SingPlus.Sip;

internal interface ITransferableOwnedPayload
{
    RegionHandle Handle { get; }
    OwnershipPayloadKind PayloadKind { get; }
    bool IsValidForRuntime { get; }
    bool IsOriginalForRuntime { get; }
    bool HasOriginalReleaseContinuityOf(ITransferableOwnedPayload original);
    void ValidateTransferForRuntime();
    object TransferForRuntime(RegionHandle newHandle);
    PreparedOwnedTransfer PrepareTransferForRuntime();
    object CreateBorrowLeaseForRuntime(BorrowLeaseHandle handle, BorrowLeaseLifetime lifetime);
    void InvalidateOwnerAccessForRuntime();
    void InvalidateForRuntime();
}

internal sealed class PreparedOwnedTransfer : IDisposable
{
    private readonly PreparedOwnedTransfer _original;
    private readonly Action<RegionHandle> commit;
    private readonly Action? cancel,validate;
    internal PreparedOwnedTransfer(ITransferableOwnedPayload source,object projection,object custodyGate,Action<RegionHandle> commit,Action? cancel=null,Action? validate=null)
    { _original=this;Source=source;SourceHandle=source.Handle;Projection=projection;CustodyGate=custodyGate;this.commit=commit;this.cancel=cancel;this.validate=validate; }
    internal object CustodyGate { get; }
    internal ITransferableOwnedPayload Source { get; }
    internal RegionHandle SourceHandle { get; }
    internal bool IsOriginal => ReferenceEquals(_original,this);
    internal object Projection { get; }
    internal void Commit(RegionHandle handle){Validate();commit(handle);}
    public void Dispose()
    {
        if(!ReferenceEquals(_original,this))throw new InvalidOperationException("Copied preparation cannot cancel original custody.");
        cancel?.Invoke();
    }
    internal void Validate()
    {
        if(!ReferenceEquals(_original,this))throw new InvalidOperationException("Copied prepared transfer denied.");
        validate?.Invoke();
    }
}

internal sealed class BorrowLeaseLifetime
{
    private readonly BorrowLeaseLifetime _original;
    internal object? OriginalRegionOwner { get; }
    internal bool IsOriginal => ReferenceEquals(_original,this);
    internal BorrowLeaseLifetime(object? originalRegionOwner=null)
    { _original=this; OriginalRegionOwner=originalRegionOwner; }
    private int _active = 1;
    private int _localAccessActive = 1;
    private object? _storageIdentity;

    internal bool IsOriginalForStorage(object identity) => IsOriginal && ReferenceEquals(Volatile.Read(ref _storageIdentity), identity);
    internal void BindOriginalStorage(object identity)
    {
        if (!IsOriginal || !IsActive) throw new InvalidOperationException("Only an original active borrow can bind byte storage.");
        object? previous = Interlocked.CompareExchange(ref _storageIdentity, identity, null);
        if (previous is not null && !ReferenceEquals(previous, identity))
            throw new InvalidOperationException("Original borrow lifetime belongs to different byte storage.");
    }

    internal bool IsActive => Volatile.Read(ref _active) != 0;
    internal bool IsLocalAccessActive => IsActive && Volatile.Read(ref _localAccessActive) != 0;

    internal void InvalidateLocalAccessForRuntime() => Interlocked.Exchange(ref _localAccessActive, 0);
    internal void InvalidateForRuntime()
    {
        Interlocked.Exchange(ref _localAccessActive, 0);
        Interlocked.Exchange(ref _active, 0);
    }
}

[Flags]
internal enum RuntimeBufferAccess
{
    Read = 1 << 0,
    Write = 1 << 1,
}

internal sealed class RuntimeBufferReservationLifetime
{
    private readonly RuntimeBufferReservationLifetime _original;
    private object? _storageIdentity;
    private int _active = 1;

    internal RuntimeBufferReservationLifetime() => _original = this;
    internal bool IsOriginalForStorage(object identity) => ReferenceEquals(_original, this) &&
        ReferenceEquals(Volatile.Read(ref _storageIdentity), identity);
    internal void BindOriginalStorage(object identity)
    {
        if (!ReferenceEquals(_original, this) || !IsActive)
            throw new InvalidOperationException("Only an original active reservation can bind byte storage.");
        object? previous = Interlocked.CompareExchange(ref _storageIdentity, identity, null);
        if (previous is not null && !ReferenceEquals(previous, identity))
            throw new InvalidOperationException("Original reservation belongs to different byte storage.");
    }

    internal bool IsActive => Volatile.Read(ref _active) != 0;

    internal void Invalidate() => Interlocked.Exchange(ref _active, 0);
}

public sealed class OwnedBuffer<T> : ITransferableOwnedPayload where T : unmanaged
{
    internal sealed class Storage(T[] data)
    {
        private readonly object _stateGate = new();
        private BorrowLeaseLifetime? _borrowLifetime;
        private BorrowLeaseLifetime? _originalBorrowLifetime;
        private bool _borrowLifetimeExpected;
        private readonly object _borrowStorageIdentity = new(); // Identity only; no array/Storage back-reference in lifetime history.
        private RuntimeBufferReservationLifetime? _runtimeReservation;
        private RuntimeBufferReservationLifetime? _originalRuntimeReservation;
        private readonly object _runtimeStorageIdentity = new(); // No backing reference retained by lifetime history.
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
                lock (_stateGate) return HasBorrowObligationLocked();
            }
        }

        private bool HasBorrowObligationLocked() => _borrowLifetimeExpected
            ? _originalBorrowLifetime is null || !_originalBorrowLifetime.IsOriginalForStorage(_borrowStorageIdentity) ||
                !ReferenceEquals(_borrowLifetime, _originalBorrowLifetime) || _originalBorrowLifetime.IsActive
            : _borrowLifetime is not null || _originalBorrowLifetime is not null;

        internal bool HasOriginalBorrowLifetime(BorrowLeaseLifetime lifetime)
        {
            lock (_stateGate)
                return _isAlive && _borrowLifetimeExpected && lifetime.IsOriginalForStorage(_borrowStorageIdentity) &&
                    ReferenceEquals(_borrowLifetime, lifetime) && ReferenceEquals(_originalBorrowLifetime, lifetime) &&
                    lifetime.IsLocalAccessActive;
        }

        public bool IsRuntimeReserved
        {
            get
            {
                lock (_stateGate) return _runtimeReservation is not null || _originalRuntimeReservation is not null;
            }
        }

        internal void BeginBorrow(BorrowLeaseLifetime lifetime)
        {
            lock (_stateGate)
            {
                if (!_isAlive) throw new InvalidOperationException("OwnedBuffer backing storage has been reclaimed.");
                if (HasBorrowObligationLocked()) throw new InvalidOperationException("OwnedBuffer already has an active or unresolved runtime borrow lease.");
                if (_runtimeReservation is not null || _originalRuntimeReservation is not null) throw new InvalidOperationException("OwnedBuffer runtime reservation is active or unresolved.");
                if (!lifetime.IsOriginal || !lifetime.IsActive) throw new InvalidOperationException("Original active borrow lifetime is required before registration.");
                lifetime.BindOriginalStorage(_borrowStorageIdentity);
                _borrowLifetime = lifetime;
                _originalBorrowLifetime = lifetime;
                _borrowLifetimeExpected = true;
            }
        }

        internal void BeginRuntimeReservation(RuntimeBufferReservationLifetime lifetime)
        {
            lock (_stateGate)
            {
                if (!_isAlive) throw new InvalidOperationException("OwnedBuffer backing storage has been reclaimed.");
                if (HasBorrowObligationLocked()) throw new InvalidOperationException("OwnedBuffer has an active or unresolved runtime borrow lease.");
                if (_runtimeReservation is not null || _originalRuntimeReservation is not null) throw new InvalidOperationException("OwnedBuffer runtime reservation is active or unresolved.");
                lifetime.BindOriginalStorage(_runtimeStorageIdentity);
                _runtimeReservation = lifetime;
                _originalRuntimeReservation = lifetime;
            }
        }

        internal void EndRuntimeReservation(RuntimeBufferReservationLifetime lifetime)
        {
            lock (_stateGate)
            {
                if (!lifetime.IsOriginalForStorage(_runtimeStorageIdentity) || !ReferenceEquals(_runtimeReservation, lifetime) || !ReferenceEquals(_originalRuntimeReservation,lifetime) || !lifetime.IsActive)
                    throw new InvalidOperationException("OwnedBuffer runtime reservation is stale or already released.");

                lifetime.Invalidate();
                _runtimeReservation = null;
                _originalRuntimeReservation = null;
            }
        }

        internal bool HasOriginalRuntimeReservation(RuntimeBufferReservationLifetime lifetime)
        {
            lock (_stateGate)
                return _isAlive && lifetime.IsOriginalForStorage(_runtimeStorageIdentity) && ReferenceEquals(_runtimeReservation,lifetime) && ReferenceEquals(_originalRuntimeReservation,lifetime) && lifetime.IsActive;
        }

        internal bool OwnsOriginalRuntimeLifetime(RuntimeBufferReservationLifetime lifetime) =>
            lifetime.IsOriginalForStorage(_runtimeStorageIdentity);

        internal void Invalidate()
        {
            lock (_stateGate)
            {
                _isAlive = false;
                if (_originalBorrowLifetime is { } borrow && borrow.IsOriginalForStorage(_borrowStorageIdentity))
                    borrow.InvalidateForRuntime(); // Never invalidate a substituted foreign original or route.
                if (_originalRuntimeReservation is { } reservation && reservation.IsOriginalForStorage(_runtimeStorageIdentity))
                    reservation.Invalidate(); // Never invalidate a substituted foreign original or route.
            }
        }
    }

    private sealed class LocalMoveOrigin
    {
        private readonly LocalMoveOrigin _original;
        private readonly Storage _storage;
        private readonly OwnedBuffer<T> _root;
        internal LocalMoveOrigin(Storage storage, OwnedBuffer<T> root)
        { _original = this; _storage = storage; _root = root; }
        private bool IsOriginalFor(Storage storage) => ReferenceEquals(_original, this) &&
            ReferenceEquals(_storage, storage) && ReferenceEquals(_root._storage, storage) &&
            ReferenceEquals(_root._originalOwner, _root);
        internal bool HasOriginalCustodyFor(Storage storage) => IsOriginalFor(storage) && ReferenceEquals(_root._localMoveOrigin, this);
        internal bool IsActiveFor(Storage storage) => HasOriginalCustodyFor(storage) && Volatile.Read(ref _root._localAccessActive) != 0;
        internal void RevokeFor(Storage storage)
        {
            if (IsOriginalFor(storage)) Interlocked.Exchange(ref _root._localAccessActive, 0);
        }
    }

    private readonly Storage _storage;
    private readonly object _ownerGate = new();
    private readonly OwnedBuffer<T> _originalOwner;
    private readonly LocalMoveOrigin _localMoveOrigin;
    private int _localAccessActive = 1;
    private bool _valid;
    private object? _preparedTransfer;

    internal OwnedBuffer(RegionHandle handle, T[] data) : this(handle, new Storage(data))
    {
    }

    private OwnedBuffer(RegionHandle handle, Storage storage, LocalMoveOrigin? localMoveOrigin = null)
    {
        Handle = handle;
        _storage = storage;
        _originalOwner = this;
        _localMoveOrigin = localMoveOrigin ?? new LocalMoveOrigin(storage, this);
        _valid = true;
    }

    public RegionHandle Handle { get; private set; }
    public bool IsValid
    {
        get
        {
            lock (_ownerGate) return ReferenceEquals(_originalOwner, this) && _valid && _storage.IsAlive && _localMoveOrigin.IsActiveFor(_storage);
        }
    }
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

    public ReadBorrow<T> BorrowRead(int offset, int length)
    {
        EnsureOwnerAccess();
        ValidateElementRange(offset, length);
        return new ReadBorrow<T>(this, offset, length);
    }

    public WriteBorrow<T> BorrowWrite(int offset, int length)
    {
        EnsureOwnerAccess();
        ValidateElementRange(offset, length);
        return new WriteBorrow<T>(this, offset, length);
    }

    public OwnedBuffer<T> Move()
    {
        lock (_ownerGate)
        {
            EnsureOwnerAccessCore();
            OwnedBuffer<T> moved = new OwnedBuffer<T>(Handle, _storage, _localMoveOrigin);
            _valid = false;
            return moved;
        }
    }

    internal Span<T> GetBorrowedSpan()
    {
        EnsureOwnerAccess();
        return _storage.Data.AsSpan();
    }

    internal Span<T> GetBorrowedSpan(int offset, int length)
    {
        EnsureOwnerAccess();
        return _storage.Data.AsSpan(offset, length);
    }

    internal RuntimeBufferLease<T> ReserveForRuntime(RuntimeBufferAccess access)
    {
        if (access is not RuntimeBufferAccess.Read and not RuntimeBufferAccess.Write)
            throw new ArgumentOutOfRangeException(nameof(access));

        lock (_ownerGate)
        {
            EnsureOwnerAccessCore();
            RuntimeBufferReservationLifetime lifetime = new RuntimeBufferReservationLifetime();
            _storage.BeginRuntimeReservation(lifetime);
            return new RuntimeBufferLease<T>(_storage, lifetime, access);
        }
    }

    OwnershipPayloadKind ITransferableOwnedPayload.PayloadKind => OwnershipPayloadKind.OwnedBuffer;
    bool ITransferableOwnedPayload.IsValidForRuntime => IsValid;
    bool ITransferableOwnedPayload.IsOriginalForRuntime => ReferenceEquals(_originalOwner, this) && _localMoveOrigin.HasOriginalCustodyFor(_storage);
    void ITransferableOwnedPayload.ValidateTransferForRuntime() => EnsureOwnerAccess();

    bool ITransferableOwnedPayload.HasOriginalReleaseContinuityOf(ITransferableOwnedPayload original)
    {
        lock (_ownerGate)
            return ReferenceEquals(_originalOwner, this) && _valid && _storage.IsAlive && _localMoveOrigin.IsActiveFor(_storage) &&
                original is OwnedBuffer<T> source && ReferenceEquals(source._originalOwner, source) &&
                ReferenceEquals(_localMoveOrigin, source._localMoveOrigin) && ReferenceEquals(_storage, source._storage) &&
                Handle == source.Handle;
    }

    object ITransferableOwnedPayload.TransferForRuntime(RegionHandle newHandle)
    {
        lock (_ownerGate)
        {
            EnsureOwnerAccessCore();
            OwnedBuffer<T> transferred = new OwnedBuffer<T>(newHandle, _storage);
            _valid = false;
            return transferred;
        }
    }

    PreparedOwnedTransfer ITransferableOwnedPayload.PrepareTransferForRuntime()
    {
        lock(_ownerGate)
        {
            EnsureOwnerAccessCore();
            RegionHandle originalHandle =Handle;
            OwnedBuffer<T> projection =new OwnedBuffer<T>(originalHandle,_storage){_valid=false};
            object token =new object();
            PreparedOwnedTransfer prepared =new PreparedOwnedTransfer(this,projection,_ownerGate,handle=>
            {
                lock(_ownerGate)
                {
                    EnsureValidCore();
                    if(!ReferenceEquals(_preparedTransfer,token) || Handle!=originalHandle || projection._valid || _storage.IsBorrowed || _storage.IsRuntimeReserved)
                        throw new InvalidOperationException("Original prepared ownership changed.");
                    projection.Handle=handle;_valid=false;projection._valid=true;_preparedTransfer=null;
                }
            },()=>{lock(_ownerGate){if(ReferenceEquals(_preparedTransfer,token))_preparedTransfer=null;}},()=>
            {
                lock(_ownerGate)
                {
                    EnsureValidCore();
                    if(!ReferenceEquals(_preparedTransfer,token) || Handle!=originalHandle || projection._valid || _storage.IsBorrowed || _storage.IsRuntimeReserved)
                        throw new InvalidOperationException("Original prepared ownership unavailable before Region mutation.");
                }
            });
            _preparedTransfer=token; // Local managed custody, not Region authority.
            return prepared;
        }
    }

    object ITransferableOwnedPayload.CreateBorrowLeaseForRuntime(BorrowLeaseHandle handle, BorrowLeaseLifetime lifetime)
    {
        lock (_ownerGate)
        {
            EnsureValidCore();
            _storage.BeginBorrow(lifetime);
            return new BorrowLease<T>(handle, _storage, lifetime);
        }
    }

    void ITransferableOwnedPayload.InvalidateForRuntime()
    {
        lock (_ownerGate)
        {
            _valid = false;
            _storage.Invalidate();
        }
    }

    void ITransferableOwnedPayload.InvalidateOwnerAccessForRuntime()
    {
        lock (_ownerGate)
        {
            _valid = false;
            Interlocked.Exchange(ref _localAccessActive, 0);
            _localMoveOrigin.RevokeFor(_storage);
        }
    }

    private void EnsureValid()
    {
        lock (_ownerGate) EnsureValidCore();
    }

    private void EnsureOwnerAccess()
    {
        lock (_ownerGate) EnsureOwnerAccessCore();
    }

    private void EnsureValidCore()
    {
        if (!ReferenceEquals(_originalOwner, this) || !_valid || !_storage.IsAlive || !_localMoveOrigin.IsActiveFor(_storage)) throw new InvalidOperationException("OwnedBuffer has been copied, moved, transferred, released, or reclaimed.");
    }

    private void EnsureOwnerAccessCore()
    {
        EnsureValidCore();
        if(_preparedTransfer is not null)throw new InvalidOperationException("OwnedBuffer is reserved for an original ownership transfer.");
        if (_storage.IsBorrowed) throw new InvalidOperationException("OwnedBuffer is temporarily inaccessible while a runtime borrow lease is active.");
        if (_storage.IsRuntimeReserved) throw new InvalidOperationException("OwnedBuffer is temporarily inaccessible while a runtime operation owns its CPU-access reservation.");
    }

    private void ValidateElementRange(int offset, int length)
    {
        if (offset < 0 || length <= 0 || offset > _storage.Data.Length - length)
            throw new ArgumentOutOfRangeException(nameof(length), "Borrow range must be a non-empty subrange of the buffer.");
    }
}

internal sealed class RuntimeBufferLease<T> : IDisposable where T : unmanaged
{
    private sealed class ByteMemory(RuntimeBufferLease<T> owner) : System.Buffers.MemoryManager<byte>
    {
        public override Span<byte> GetSpan()
        {
            owner.EnsureAccess(owner._access);
            return System.Runtime.InteropServices.MemoryMarshal.AsBytes(owner._storage.Data.AsSpan());
        }
        public override System.Buffers.MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException("Managed runtime backing does not expose native pointers.");
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    private readonly OwnedBuffer<T>.Storage _storage;
    private readonly RuntimeBufferReservationLifetime _lifetime;
    private readonly RuntimeBufferAccess _access;
    private readonly RuntimeBufferLease<T> _original;
    private readonly object _disposeGate = new();
    private int _disposed;

    internal RuntimeBufferLease(
        OwnedBuffer<T>.Storage storage,
        RuntimeBufferReservationLifetime lifetime,
        RuntimeBufferAccess access)
    {
        _storage = storage;
        _lifetime = lifetime;
        _access = access;
        _original = this;
    }

    internal bool IsActive => ReferenceEquals(_original,this) && _storage.HasOriginalRuntimeReservation(_lifetime);

    internal ReadOnlyMemory<byte> ReadOnlyBytes { get { EnsureAccess(RuntimeBufferAccess.Read); return new ByteMemory(this).Memory; } }
    internal Memory<byte> WritableBytes { get { EnsureAccess(RuntimeBufferAccess.Write); return new ByteMemory(this).Memory; } }
    internal T[] WritableArray { get { EnsureAccess(RuntimeBufferAccess.Write); return _storage.Data; } }

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
        if (!ReferenceEquals(_original,this)) throw new InvalidOperationException("Copied runtime buffer lease cannot end original reservation.");
        lock (_disposeGate)
        {
            if (!_storage.OwnsOriginalRuntimeLifetime(_lifetime))
                throw new InvalidOperationException("Original runtime reservation lifetime/storage custody changed.");
            if (_disposed != 0) return;
            // A lost registration denies views but does not prove this lifetime ended.
            if (_storage.IsAlive) _storage.EndRuntimeReservation(_lifetime);
            _disposed = 1; // Commit only after successful end; failed exact routing remains retryable.
        }
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


public readonly ref struct ReadBorrow<T> where T : unmanaged
{
    private readonly OwnedBuffer<T> _owner;
    private readonly int _offset;
    private readonly int _length;

    internal ReadBorrow(OwnedBuffer<T> owner, int offset, int length) =>
        (_owner, _offset, _length) = (owner, offset, length);

    public ReadOnlySpan<T> Span => _owner.GetBorrowedSpan(_offset, _length);
    public int Length => _length;
    public ref readonly T this[int index] => ref Span[index];
}

public readonly ref struct WriteBorrow<T> where T : unmanaged
{
    private readonly OwnedBuffer<T> _owner;
    private readonly int _offset;
    private readonly int _length;

    internal WriteBorrow(OwnedBuffer<T> owner, int offset, int length) =>
        (_owner, _offset, _length) = (owner, offset, length);

    public Span<T> Span => _owner.GetBorrowedSpan(_offset, _length);
    public int Length => _length;
    public ref T this[int index] => ref Span[index];
}
