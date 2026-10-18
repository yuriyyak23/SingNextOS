namespace YAKSys_Hybrid_CPU.Core;
public sealed class NeutralOriginalVirtualIoSnapshotRelease
{
    internal NeutralOriginalVirtualIoSnapshotRelease(NeutralOriginalVirtualIoTransferReceipt transfer){Transfer=transfer;}
    public NeutralOriginalVirtualIoTransferReceipt Transfer {get;}
    public bool NoFurtherSnapshotRead=>true;
}
public interface INeutralOriginalVirtualIoSnapshotReleaseProvider
{
    bool SupportsOriginalVirtualIoSnapshotRelease {get;}
    NeutralVirtualizationResult<NeutralOriginalVirtualIoSnapshotRelease> ReleaseOriginalVirtualIoSnapshot(NeutralOriginalVirtualIoTransferRequest request,NeutralOriginalVirtualIoTransferReceipt transfer);
    NeutralVirtualizationResult<NeutralOriginalVirtualIoSnapshotRelease> ResolveOriginalVirtualIoSnapshotRelease(NeutralOriginalVirtualIoTransferRequest request,NeutralOriginalVirtualIoTransferReceipt transfer);
}
// Cleanup-only continuation of an already retained original. May deliver its
// first release only when the producing owner proves it never invoked native.
// Unknown invocation permits same-original query only, never another effect.
public interface INeutralOriginalVirtualIoSnapshotReleaseContinuationProvider
{
    NeutralVirtualizationResult<NeutralOriginalVirtualIoSnapshotRelease> ContinueOriginalVirtualIoSnapshotRelease(NeutralOriginalVirtualIoTransferRequest request,NeutralOriginalVirtualIoTransferReceipt transfer);
}
