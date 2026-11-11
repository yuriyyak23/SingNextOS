namespace SingPlus.Platform;
public sealed class PlatformOriginalVirtualIoSnapshotRelease
{
    internal PlatformOriginalVirtualIoSnapshotRelease(PlatformOriginalVirtualIoTransferReceipt transfer){Transfer=transfer;}
    public PlatformOriginalVirtualIoTransferReceipt Transfer {get;}
    public bool NoFurtherSnapshotRead=>true;
}
public interface IPlatformOriginalVirtualIoSnapshotReleaseProvider
{
    bool SupportsOriginalVirtualIoSnapshotRelease {get;}
    PlatformAuthorityResult<PlatformOriginalVirtualIoSnapshotRelease> ReleaseOriginalVirtualIoSnapshot(PlatformOriginalVirtualIoTransferRequest request,PlatformOriginalVirtualIoTransferReceipt transfer);
    PlatformAuthorityResult<PlatformOriginalVirtualIoSnapshotRelease> ResolveOriginalVirtualIoSnapshotRelease(PlatformOriginalVirtualIoTransferRequest request,PlatformOriginalVirtualIoTransferReceipt transfer);
}
// Continues retained cleanup; does not admit a new transfer or reconstruct intent.
public interface IPlatformOriginalVirtualIoSnapshotReleaseContinuationProvider
{
    PlatformAuthorityResult<PlatformOriginalVirtualIoSnapshotRelease> ContinueOriginalVirtualIoSnapshotRelease(PlatformOriginalVirtualIoTransferRequest request,PlatformOriginalVirtualIoTransferReceipt transfer);
}
