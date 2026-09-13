namespace YAKSys_Hybrid_CPU.ExecutableAdapter.Reconciliation;

internal sealed record AdapterOperationReservation(
    AdapterOperationIdentity Identity,
    string Operation,
    DateTimeOffset CreatedAtUtc);
