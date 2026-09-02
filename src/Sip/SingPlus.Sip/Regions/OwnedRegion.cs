using SingPlus.Contracts;

namespace SingPlus.Sip;

public sealed class OwnedRegion<T> : ITransferableOwnedPayload where T : unmanaged
{
    private OwnedBuffer<T> _buffer;

    internal OwnedRegion(RegionHandle handle, T value)
    {
        _buffer = new OwnedBuffer<T>(handle, new[] { value });
    }

    private OwnedRegion(OwnedBuffer<T> buffer) => _buffer = buffer;

    public RegionHandle Handle => _buffer.Handle;
    public bool IsValid => _buffer.IsValid;

    public T Value
    {
        get => _buffer.Span[0];
        set => _buffer.Span[0] = value;
    }

    public BorrowedSpan<T> Borrow() => _buffer.Borrow();

    public OwnedRegion<T> Move() => new(_buffer.Move());

    bool ITransferableOwnedPayload.IsValidForRuntime => _buffer.IsValid;
    object ITransferableOwnedPayload.TransferForRuntime(RegionHandle newHandle) => new OwnedRegion<T>((OwnedBuffer<T>)((ITransferableOwnedPayload)_buffer).TransferForRuntime(newHandle));
    object ITransferableOwnedPayload.CreateBorrowLeaseForRuntime(BorrowLeaseHandle handle, BorrowLeaseLifetime lifetime) => ((ITransferableOwnedPayload)_buffer).CreateBorrowLeaseForRuntime(handle, lifetime);
    void ITransferableOwnedPayload.InvalidateForRuntime() => ((ITransferableOwnedPayload)_buffer).InvalidateForRuntime();
}
