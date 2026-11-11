using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("HybridCpu_ExecutableAdapter")]
namespace YAKSys_Hybrid_CPU.Core;
public sealed class NeutralOriginalVirtualIoTransferRequest(NeutralVirtualIoLease lease, ulong offset, bool write, int length, byte[]? data = null)
{
    public bool ValidInput { get; } = length is >0 and <=4096 && (write ? data is not null && data.Length==length : data is null);
    public Guid InvocationId { get; } = Guid.NewGuid();
    public NeutralVirtualIoLease Lease { get; } = lease;
    public ulong Offset { get; } = offset;
    public bool Write { get; } = write;
    public int Length { get; } = length;
    private byte[]? input = length is >0 and <=4096 && write && data is not null && data.Length==length ? data.ToArray() : null;
    internal byte[]? Input => input;
    internal void ReleaseInput() { if(input is { } bytes) Array.Clear(bytes); input = null; }
}
public sealed class NeutralOriginalVirtualIoTransferReceipt
{
    private readonly Func<byte[]> read;
    internal NeutralOriginalVirtualIoTransferReceipt(NeutralOriginalVirtualIoTransferRequest request,bool completed,bool noEffect,Func<byte[]> read)
    { Request = request; Completed = completed; NoEffect = noEffect; this.read = read; }
    public NeutralOriginalVirtualIoTransferRequest Request { get; }
    public bool Completed { get; }
    public bool NoEffect { get; }
    public byte[] CopyReadData() => read();
}
/// <summary>Bounded original software transfer. Caller reserves Length native read snapshot
/// bytes before admission, retained until exact IO closure; copies are data only.</summary>
public interface INeutralOriginalVirtualIoTransferProvider
{
    NeutralVirtualizationResult<NeutralOriginalVirtualIoTransferReceipt> TransferOriginalVirtualIo(NeutralOriginalVirtualIoTransferRequest request);
    NeutralVirtualizationResult<NeutralOriginalVirtualIoTransferReceipt> ResolveOriginalVirtualIoTransfer(NeutralOriginalVirtualIoTransferRequest request);
}
