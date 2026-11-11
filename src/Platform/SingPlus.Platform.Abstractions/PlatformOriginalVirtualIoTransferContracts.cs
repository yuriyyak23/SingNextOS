using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("SingPlus.Platform.HybridCpu")]
namespace SingPlus.Platform;
/// <summary>Original software transfer identity, never permission. Caller charges native
/// read snapshot Length before invocation and retains charge until exact original
/// transfer no-effect or IO closure; completion alone does not release a snapshot.</summary>
public sealed class PlatformOriginalVirtualIoTransferRequest(PlatformProviderVirtualIoLease lease,ulong offset,bool write,int length,byte[]? data=null)
{
    public bool ValidInput { get; } = length is >0 and <=4096 && (write ? data is not null && data.Length==length : data is null);
    public Guid InvocationId { get; } = Guid.NewGuid();
    public PlatformProviderVirtualIoLease Lease { get; } = lease;
    public ulong Offset { get; } = offset;
    public bool Write { get; } = write;
    public int Length { get; } = length;
    private byte[]? input=length is >0 and <=4096 && write && data is not null && data.Length==length ? data.ToArray() : null;
    internal byte[]? Input => input;
    internal void ReleaseInput() { if(input is { } bytes) Array.Clear(bytes);input=null; }
}
public sealed class PlatformOriginalVirtualIoTransferReceipt
{
    private readonly Func<byte[]> copy;
    internal PlatformOriginalVirtualIoTransferReceipt(PlatformOriginalVirtualIoTransferRequest request,bool completed,bool noEffect,Func<byte[]> copy)
    {Request=request;Completed=completed;NoEffect=noEffect;this.copy=copy;}
    public PlatformOriginalVirtualIoTransferRequest Request {get;}
    public bool Completed {get;}
    public bool NoEffect {get;}
    public byte[] CopyReadData()=>copy();
}
public interface IPlatformOriginalVirtualIoTransferProvider
{
    PlatformAuthorityResult<PlatformOriginalVirtualIoTransferReceipt> TransferOriginalVirtualIo(PlatformOriginalVirtualIoTransferRequest request);
    PlatformAuthorityResult<PlatformOriginalVirtualIoTransferReceipt> ResolveOriginalVirtualIoTransfer(PlatformOriginalVirtualIoTransferRequest request);
}
