using SingPlus.Contracts;

namespace SingPlus.Sip;

internal interface ITransferableOwnedPayload
{
    RegionHandle Handle { get; }
    OwnershipPayloadKind PayloadKind { get; }
    bool IsValidForRuntime { get; }
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
    private readonly object _ownerGate = new();
    private bool _valid;
    private object? _preparedTransfer;

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
    public bool IsValid
    {
        get
        {
            lock (_ownerGate) return _valid && _storage.IsAlive;
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
            var moved = new OwnedBuffer<T>(Handle, _storage);
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
            var lifetime = new RuntimeBufferReservationLifetime();
            _storage.BeginRuntimeReservation(lifetime);
            return new RuntimeBufferLease<T>(_storage, lifetime, access);
        }
    }

    OwnershipPayloadKind ITransferableOwnedPayload.PayloadKind => OwnershipPayloadKind.OwnedBuffer;
    bool ITransferableOwnedPayload.IsValidForRuntime => IsValid;
    void ITransferableOwnedPayload.ValidateTransferForRuntime() => EnsureOwnerAccess();

    object ITransferableOwnedPayload.TransferForRuntime(RegionHandle newHandle)
    {
        lock (_ownerGate)
        {
            EnsureOwnerAccessCore();
            var transferred = new OwnedBuffer<T>(newHandle, _storage);
            _valid = false;
            return transferred;
        }
    }

    PreparedOwnedTransfer ITransferableOwnedPayload.PrepareTransferForRuntime()
    {
        lock(_ownerGate)
        {
            EnsureOwnerAccessCore();
            var originalHandle=Handle;
            var projection=new OwnedBuffer<T>(originalHandle,_storage){_valid=false};
            var token=new object();
            var prepared=new PreparedOwnedTransfer(this,projection,_ownerGate,handle=>
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
        lock (_ownerGate) _valid = false;
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
        if (!_valid || !_storage.IsAlive) throw new InvalidOperationException("OwnedBuffer has been moved, transferred, released, or reclaimed.");
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
