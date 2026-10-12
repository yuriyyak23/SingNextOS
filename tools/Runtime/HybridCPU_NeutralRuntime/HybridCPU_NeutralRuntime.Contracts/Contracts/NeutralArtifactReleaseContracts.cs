namespace YAKSys_Hybrid_CPU.Core;

public readonly record struct NeutralArtifactReleaseRequest(Guid InvocationId, NeutralExecutableArtifactReceipt Artifact);
public readonly record struct NeutralArtifactReleaseReceipt(NeutralArtifactReleaseRequest Request, bool NoFurtherUse);

/// <summary>Optional exact artifact backing release; pure lookup cannot infer
/// release from absence. Failure and lost replies keep backing dependencies pinned.
/// NotInvoked proves no release invocation for this retained request.</summary>
public interface INeutralExecutableArtifactReleaseProvider
{
    NeutralVirtualizationResult<NeutralArtifactReleaseReceipt> ReleaseExecutableArtifact(NeutralArtifactReleaseRequest request);
    NeutralVirtualizationResult<NeutralArtifactReleaseReceipt> ResolveExecutableArtifactRelease(NeutralArtifactReleaseRequest request);
}
