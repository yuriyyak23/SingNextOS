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
    public const string ContractsPackageVersion = "1.34.0";
    public const string ContractsPackageSha256 = "DB84FC2396E809E63F078E6BEAF88633638DE71809EF5DA22C3AF07CB036A295";
    public const string RuntimePackageSha256 = "76B1918D075ADEFB50ACA35376FBCC58F9E1A911F3AA8DAA39B7F3388E43E9C4";
}











