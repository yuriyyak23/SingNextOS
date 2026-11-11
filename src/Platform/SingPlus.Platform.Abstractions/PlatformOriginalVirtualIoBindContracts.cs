namespace SingPlus.Platform;

public sealed record PlatformOriginalVirtualIoBindRequest(Guid InvocationId, PlatformVirtualIoRequest Request);
public sealed record PlatformOriginalVirtualIoBindCleanupClosure(PlatformOriginalVirtualIoBindRequest Admission, bool NoFurtherUse, bool NoEffect = false);
/// <summary>Original invocation identity precedes Bind. Cleanup fences only the
/// backing admitted through that invocation. Missing/failed queries are unknown.
/// Cleanup continuation never repeats an invoked native phase.</summary>
public interface IPlatformOriginalVirtualIoBindProvider
{
    bool SupportsOriginalVirtualIoBindCleanup { get; }
    PlatformAuthorityResult<PlatformProviderVirtualIoLease> BindOriginalVirtualIo(PlatformOriginalVirtualIoBindRequest request);
    PlatformAuthorityResult<PlatformOriginalVirtualIoBindCleanupClosure> CleanupOriginalVirtualIoBind(PlatformOriginalVirtualIoBindRequest request);
    PlatformOriginalVirtualIoBindCleanupClosure? ResolveOriginalVirtualIoBindCleanup(PlatformOriginalVirtualIoBindRequest request);
}
