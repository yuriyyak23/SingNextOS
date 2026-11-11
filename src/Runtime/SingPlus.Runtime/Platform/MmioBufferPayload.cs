using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Sip;

namespace SingPlus.Runtime;

// Bytes/use reservation only. Original MMIO invocation owns admission and closure.
internal sealed class MmioBufferPayload
{
    internal MmioBufferPayload(OwnedBuffer<byte> buffer,RegionOwner owner)
    { Original=this;_buffer=buffer;Region=buffer.Handle;Owner=owner; }
    internal MmioBufferPayload Original { get; }
    private OwnedBuffer<byte>? _buffer;
    internal OwnedBuffer<byte> Buffer => _buffer ?? throw new InvalidOperationException("MMIO backing bytes consumed; metadata only.");
    internal RegionHandle Region { get; }
    internal RegionOwner Owner { get; }
    internal RegionUseHandle? Use { get; set; }
    internal RegionAuthority? UseAuthority { get; set; }
    internal bool LocalRollbackPending { get; set; }
    internal RuntimeBufferLease<byte>? Reservation { get; private set; }
    internal PlatformMmioBacking? Backing { get; private set; }
    internal object? AdmissionOwner { get; set; }
    internal bool AdmissionStarted { get; private set; }
    internal void CaptureAdmission(object owner)
    {
        if (AdmissionStarted || AdmissionOwner is not null)
            throw new InvalidOperationException("Original MMIO payload admission was already captured.");
        AdmissionStarted = true;
        AdmissionOwner = owner;
    }
    internal void ReserveBytes()
    {
        Reservation=Buffer.ReserveForRuntime(RuntimeBufferAccess.Write);
        Backing=new PlatformMmioBacking(Reservation.WritableArray);
    }
    internal void ReleaseBytes() { Backing?.Release();Reservation?.Dispose();Reservation=null;_buffer=null; }
}
