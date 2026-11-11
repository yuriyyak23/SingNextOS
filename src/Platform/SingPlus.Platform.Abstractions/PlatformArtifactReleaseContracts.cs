namespace SingPlus.Platform;

public readonly record struct PlatformArtifactReleaseRequest(Guid InvocationId, PlatformExecutableArtifactReceipt Artifact);
public readonly record struct PlatformArtifactReleaseReceipt(PlatformArtifactReleaseRequest Request, bool NoFurtherUse);

/// <summary>Optional surviving-original artifact release. No permission, mapping
/// closure or Region reclaim is implied until existing local owners consume it.
/// NotAccepted proves no release effect for the exact retained request only.</summary>
public interface IPlatformExecutableArtifactReleaseProvider
{
    PlatformAuthorityResult<PlatformArtifactReleaseReceipt> ReleaseExecutableArtifact(PlatformArtifactReleaseRequest request);
    PlatformAuthorityResult<PlatformArtifactReleaseReceipt> ResolveExecutableArtifactRelease(PlatformArtifactReleaseRequest request);
}
