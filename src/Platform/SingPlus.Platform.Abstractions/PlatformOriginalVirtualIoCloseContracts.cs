namespace SingPlus.Platform;

/// <summary>Retained before the first Close call. Reference identity is continuity;
/// copies/absence never authorize continuation or establish containment.</summary>
public sealed record PlatformOriginalVirtualIoCloseRequest(Guid InvocationId, PlatformProviderVirtualIoLease Lease);

/// <summary>Cleanup-only continuation of the same admitted composite lifetime.
/// Already-invoked native phases are queried, never repeated. Uninvoked cleanup
/// phases may finish under the original admission and current sources.</summary>
public interface IPlatformOriginalVirtualIoCloseProvider
{
    bool SupportsOriginalVirtualIoClose { get; }
    PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> CloseOriginalVirtualIo(PlatformOriginalVirtualIoCloseRequest request);
    PlatformAuthorityResult<PlatformVirtualIoClosureReceipt> ContinueOriginalVirtualIoClose(PlatformOriginalVirtualIoCloseRequest request);
}
