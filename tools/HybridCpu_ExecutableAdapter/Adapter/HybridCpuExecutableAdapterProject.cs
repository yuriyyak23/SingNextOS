namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

/// <summary>
/// Compile-time marker for the separately qualified executable child adapter.
/// </summary>
public static class HybridCpuExecutableAdapterProject
{
    public const string ProjectName = "HybridCpu_ExecutableAdapter";
    public const uint AdapterContractVersion = 2;
    public static bool IsOperational => true;
}
