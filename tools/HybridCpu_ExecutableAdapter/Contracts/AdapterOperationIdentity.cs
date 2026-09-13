namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

internal readonly record struct AdapterOperationHandle(ulong Value);
internal readonly record struct AdapterOperationGeneration(ulong Value);
internal readonly record struct AdapterOperationIdentity(
    AdapterOperationHandle Handle,
    AdapterOperationGeneration Generation);
