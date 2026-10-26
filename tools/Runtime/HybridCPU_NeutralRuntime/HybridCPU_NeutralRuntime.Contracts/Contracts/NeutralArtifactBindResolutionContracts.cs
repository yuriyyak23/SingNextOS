namespace YAKSys_Hybrid_CPU.Core;

/// <summary>Pure original Bind reconciliation for cleanup. Absence/failure proves
/// neither no-effect nor backing release. Refused publication cannot permit Start.</summary>
public interface INeutralOriginalArtifactBindProvider
{
    NeutralVirtualizationResult<NeutralExecutableArtifactReceipt> ResolveExecutableArtifactBind(NeutralExecutableArtifactRequest originalRequest);
}

/// <summary>Optional guarantee: NotInvoked from original Bind resolution proves
/// zero native backing effect for that retained invocation. No other failure
/// status or missing receipt establishes this guarantee.</summary>
public interface INeutralOriginalArtifactBindNoEffectProvider : INeutralOriginalArtifactBindProvider { }
