namespace YAKSys_Hybrid_CPU.ExecutableAdapter;

internal enum AdapterResourceLifecycle
{
    Opening,
    Active,
    Draining,
    Closed,
    Revoked,
    Faulted,
    Quarantined,
}
