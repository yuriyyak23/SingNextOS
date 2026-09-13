namespace YAKSys_Hybrid_CPU.ExecutableAdapter.Backend;

/// <summary>
/// Describes the external contract that must exist before an operational client
/// is implemented. It deliberately contains no HybridCPU internal identities.
/// </summary>
internal static class HybridCpuExternalRuntimePrerequisite
{
    public const ushort RequiredContractMajor = 1;
    public const ushort RequiredContractMinor = 3;
    public const string RequiredFacadeName = "IHybridCpuChildDomainRuntimeV3";
    public const string QualifiedFacade130Commit = "fd37b00a207a162baaa860f3f8b96c0c66d7e691";
    public const string ContractsPackage = "HybridCPU.ExternalRuntime.Contracts";
    public const string ContractsPackageVersion = "1.3.0";
    public const string ContractsPackageSha256 = "7956596E820F2536542A73171205ED0B7A3366996BBDB2B51D95C2AE1FE3FC92";
    public const string RuntimePackageSha256 = "191A1976DECAF607425B3F93378BA11446B32AF2EBFD09DFF45A26944E7E765F";
}
