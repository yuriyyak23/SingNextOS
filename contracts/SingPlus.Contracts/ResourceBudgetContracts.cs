namespace SingPlus.Contracts;

public static class ResourceBudgetContract
{
    public const uint Version = 1;
}

public readonly record struct BudgetAccountId(ulong Value);
public readonly record struct BudgetGeneration(ulong Value);
public readonly record struct BudgetAccountHandle(BudgetAccountId AccountId, BudgetGeneration Generation);
public readonly record struct BudgetReservationId(ulong Value);
public readonly record struct BudgetReservationHandle(BudgetReservationId ReservationId, BudgetGeneration Generation);

public enum BudgetAccountLevel
{
    System = 0,
    Service,
    ProcessDomain,
}

public enum BudgetReservationLifetime
{
    LocalResource = 0,
    IpcQueued,
    PlatformMapping,
    ExternalEffect,
    DeviceDma,
    GuestMemory,
    CheckpointImage,
    TraceTelemetryBuffer,
}

public enum AdmissionQosHint
{
    None = 0,
    LatencySensitive,
    ThroughputOriented,
    Background,
    BoundedInteractive,
}

public enum BudgetPressureState
{
    Normal = 0,
    SoftLimit,
    HardLimit,
    ReclaimPending,
    PinnedByExternalEffect,
}

public enum BudgetReservationState
{
    Active = 0,
    Reserved = Active,
    Bound = 1,
    Consuming = 2,
    Settling = 3,
    Released = 4,
    CancelledPreSubmit = 5,
    Quarantined = 6,
    Reconciled = 7,
    Stale = 8,
}

public readonly record struct BudgetAmount(ServiceBudgetDimension Dimension, ulong Amount);
public readonly record struct BudgetUsage(ServiceBudgetDimension Dimension, ulong Limit, ulong Used);
public readonly record struct ProcessBudgetAdmission(
    ProcessHandle Process,
    BudgetAccountHandle ServiceBudget,
    BudgetAccountHandle ProcessBudget);

public sealed record BudgetAccountSnapshot(
    BudgetAccountHandle Account,
    BudgetAccountLevel Level,
    BudgetAccountHandle? Parent,
    string OwnerCorrelation,
    IReadOnlyList<BudgetUsage> Usage,
    BudgetPressureState Pressure,
    uint ContractVersion = ResourceBudgetContract.Version)
{
    public bool MaterializesAuthority => false;
}

public sealed record BudgetReservationSnapshot(
    BudgetReservationHandle Reservation,
    BudgetAccountHandle Account,
    ProcessHandle Owner,
    IReadOnlyList<BudgetAmount> Amounts,
    BudgetReservationLifetime Lifetime,
    AdmissionQosHint QosHint,
    BudgetReservationState State,
    uint ContractVersion = ResourceBudgetContract.Version,
    IReadOnlyList<BudgetAmount>? ChargedAmounts = null)
{
    public bool AuthorizesEffect => false;
    public bool AuthorizesReclaim => false;
}
