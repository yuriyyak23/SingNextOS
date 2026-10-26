using SingPlus.Contracts;

namespace SingPlus.Sip;

public sealed class OwnedRegion<T> : ITransferableOwnedPayload where T : unmanaged
{
    private OwnedBuffer<T> _buffer;
    private readonly OwnedRegion<T> _originalOwner;

    internal OwnedRegion(RegionHandle handle, T value)
    {
        _buffer = new OwnedBuffer<T>(handle, new[] { value });
        _originalOwner = this;
    }

    private OwnedRegion(OwnedBuffer<T> buffer) { _buffer = buffer; _originalOwner = this; }

    public RegionHandle Handle => _buffer.Handle;
    public bool IsValid => ReferenceEquals(_originalOwner, this) && _buffer.IsValid;

    private OwnedBuffer<T> OriginalBuffer => ReferenceEquals(_originalOwner, this) ? _buffer :
        throw new InvalidOperationException("Copied OwnedRegion cannot replace its original ownership token.");

    public T Value
    {
        get => OriginalBuffer.Span[0];
        set => OriginalBuffer.Span[0] = value;
    }

    public BorrowedSpan<T> Borrow() => OriginalBuffer.Borrow();

    public OwnedRegion<T> Move() => new(OriginalBuffer.Move());

    OwnershipPayloadKind ITransferableOwnedPayload.PayloadKind => OwnershipPayloadKind.OwnedRegion;
    bool ITransferableOwnedPayload.IsValidForRuntime => IsValid;
    bool ITransferableOwnedPayload.IsOriginalForRuntime => ReferenceEquals(_originalOwner, this) && ((ITransferableOwnedPayload)_buffer).IsOriginalForRuntime;
    bool ITransferableOwnedPayload.HasOriginalReleaseContinuityOf(ITransferableOwnedPayload original) =>
        ReferenceEquals(_originalOwner, this) && original is OwnedRegion<T> source && ReferenceEquals(source._originalOwner, source) &&
        ((ITransferableOwnedPayload)_buffer).HasOriginalReleaseContinuityOf(source._buffer);
    void ITransferableOwnedPayload.ValidateTransferForRuntime() => ((ITransferableOwnedPayload)OriginalBuffer).ValidateTransferForRuntime();
    object ITransferableOwnedPayload.TransferForRuntime(RegionHandle newHandle) => new OwnedRegion<T>((OwnedBuffer<T>)((ITransferableOwnedPayload)OriginalBuffer).TransferForRuntime(newHandle));
    PreparedOwnedTransfer ITransferableOwnedPayload.PrepareTransferForRuntime()
    {
        PreparedOwnedTransfer prepared =((ITransferableOwnedPayload)OriginalBuffer).PrepareTransferForRuntime();
        try
        {
            OwnedRegion<T> projection =new OwnedRegion<T>((OwnedBuffer<T>)prepared.Projection);
            return new PreparedOwnedTransfer(this,projection,prepared.CustodyGate,prepared.Commit,prepared.Dispose,prepared.Validate);
        }
        catch { prepared.Dispose();throw; } // No Region effect occurred during wrapper allocation.
    }
    object ITransferableOwnedPayload.CreateBorrowLeaseForRuntime(BorrowLeaseHandle handle, BorrowLeaseLifetime lifetime) => ((ITransferableOwnedPayload)OriginalBuffer).CreateBorrowLeaseForRuntime(handle, lifetime);
    void ITransferableOwnedPayload.InvalidateOwnerAccessForRuntime() => ((ITransferableOwnedPayload)_buffer).InvalidateOwnerAccessForRuntime();
    void ITransferableOwnedPayload.InvalidateForRuntime() => ((ITransferableOwnedPayload)_buffer).InvalidateForRuntime();
}
