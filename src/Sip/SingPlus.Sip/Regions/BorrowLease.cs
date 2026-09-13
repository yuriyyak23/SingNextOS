using SingPlus.Contracts;

namespace SingPlus.Sip;

public sealed class BorrowLease<T> where T : unmanaged
{
    private readonly OwnedBuffer<T>.Storage _storage;
    private readonly BorrowLeaseLifetime _lifetime;

    internal BorrowLease(BorrowLeaseHandle handle, OwnedBuffer<T>.Storage storage, BorrowLeaseLifetime lifetime)
    {
        Handle = handle;
        _storage = storage;
        _lifetime = lifetime;
    }

    public BorrowLeaseHandle Handle { get; }
    public bool IsValid => _lifetime.IsActive && _storage.IsAlive;

    public int Length
    {
        get
        {
            EnsureValid();
            return _storage.Data.Length;
        }
    }

    public ReadOnlySpan<T> Span
    {
        get
        {
            EnsureValid();
            return _storage.Data.AsSpan();
        }
    }

    public ref readonly T this[int index] => ref Span[index];

    private void EnsureValid()
    {
        if (!IsValid) throw new InvalidOperationException("BorrowLease has been returned, revoked, or reclaimed.");
    }
}
