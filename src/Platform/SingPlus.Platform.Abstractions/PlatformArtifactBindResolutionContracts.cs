namespace SingPlus.Platform;

/// <summary>Cleanup-only surviving original Bind lookup. It does not publish
/// admission, release backing, or infer no-effect from absence.</summary>
public interface IPlatformOriginalArtifactBindProvider
{
    PlatformAuthorityResult<PlatformExecutableArtifactReceipt> ResolveExecutableArtifactBind(PlatformExecutableArtifactRequest originalRequest);
}

/// <summary>Optional guarantee: NotAccepted from same-original Bind resolution
/// proves zero backing effect. Unfinished/foreign/unknown queries must not return
/// this status as no-effect; this is not artifact Release or active permission.</summary>
public interface IPlatformOriginalArtifactBindNoEffectProvider : IPlatformOriginalArtifactBindProvider { }
