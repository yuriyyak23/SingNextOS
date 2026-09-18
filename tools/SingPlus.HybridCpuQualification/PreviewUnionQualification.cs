#if SINGPLUS_PREVIEW_LANGUAGE
namespace SingPlus.HybridCpuQualification;

// N4 experiment only: a cold qualification route, never an IR/authority/SIP representation.
internal sealed record LocalCorpus(string ArtifactIdentity);
internal sealed record ExternalCompilerNeeded(string ContractVersion);
internal union QualificationRoute(LocalCorpus, ExternalCompilerNeeded);

internal static class PreviewUnionQualification
{
    internal static string Describe(QualificationRoute route) => route switch
    {
        LocalCorpus local => "local:" + local.ArtifactIdentity,
        ExternalCompilerNeeded external => "external:" + external.ContractVersion
    };
}
#endif
