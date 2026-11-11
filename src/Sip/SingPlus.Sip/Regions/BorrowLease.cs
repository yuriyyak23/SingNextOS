using SingPlus.Contracts;

namespace SingPlus.Sip;

internal interface IOriginalBorrowPayload
{
    BorrowLeaseHandle RuntimeHandle { get; }
    BorrowLeaseLifetime RuntimeLifetime { get; }
    bool RuntimeIsOriginal { get; }
}

public sealed class BorrowLease<T> : IOriginalBorrowPayload where T : unmanaged
{
    private readonly OwnedBuffer<T>.Storage _storage;
    private readonly BorrowLeaseLifetime _lifetime;
    private readonly BorrowLease<T> _original;

    internal BorrowLease(BorrowLeaseHandle handle, OwnedBuffer<T>.Storage storage, BorrowLeaseLifetime lifetime)
    {
        Handle = handle;
        _storage = storage;
        _lifetime = lifetime;
        _original = this;
    }

    public BorrowLeaseHandle Handle { get; }
    BorrowLeaseHandle IOriginalBorrowPayload.RuntimeHandle => Handle;
    BorrowLeaseLifetime IOriginalBorrowPayload.RuntimeLifetime => _lifetime;
    bool IOriginalBorrowPayload.RuntimeIsOriginal => ReferenceEquals(_original,this);
    public bool IsValid => ReferenceEquals(_original, this) && _storage.HasOriginalBorrowLifetime(_lifetime);

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
