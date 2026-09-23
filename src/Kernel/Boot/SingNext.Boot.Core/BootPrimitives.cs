namespace SingNext.Boot.Core;

public enum BootFailure
{
    None = 0,
    Unsupported,
    Timeout,
    LinkLost,
    ResetObserved,
    Malformed,
    BoundsViolation,
    LimitExceeded,
    ReadbackMismatch,
    PartialCommit,
    AmbiguousState,
    SecurityPolicyDenied,
    HashMismatch,
    RollbackRejected,
    NotFound,
    Quarantined,
}

public readonly record struct BootResult<T>(T? Value, BootFailure Failure, string? Detail)
{
    public bool IsSuccess => Failure == BootFailure.None;

    public static BootResult<T> Success(T value) => new(value, BootFailure.None, null);

    public static BootResult<T> Fail(BootFailure failure, string detail)
    {
        if (failure == BootFailure.None) throw new ArgumentOutOfRangeException(nameof(failure));
        return new(default, failure, detail);
    }
}

public readonly record struct BootRange(ulong Start, ulong Length)
{
    public ulong EndExclusive => checked(Start + Length);

    public static bool TryCreate(ulong start, ulong length, ulong containerStart, ulong containerLength, out BootRange value)
    {
        value = default;
        if (length == 0 || containerLength == 0 || start < containerStart ||
            start > ulong.MaxValue - length || containerStart > ulong.MaxValue - containerLength)
            return false;
        var end = start + length;
        var containerEnd = containerStart + containerLength;
        if (end > containerEnd) return false;
        value = new BootRange(start, length);
        return true;
    }

    public bool Overlaps(BootRange other) => Start < other.EndExclusive && other.Start < EndExclusive;

    public bool Contains(ulong address) => address >= Start && address < EndExclusive;
}

public static class BootLimits
{
    public const int MaxPciFunctions = 4096;
    public const int MaxCapabilitiesPerFunction = 64;
    public const int MaxDiscoveryCandidates = 64;
    public const int MaxMailboxAttempts = 4;
    public const int MaxDecoderHops = 16;
    public const int MaxComponents = 64;
    public const int MaxComponentBytes = 64 * 1024 * 1024;
    public const int MaxTrialAttempts = 16;
}

public enum BootStateDomain : byte
{
    Capsule = 1,
    Image = 2,
}

public enum BootSlot : byte
{
    A = 1,
    B = 2,
}

public readonly record struct ProtectedBootEnvelope(
    uint Version,
    BootStateDomain Domain,
    BootSlot ConfirmedSlot,
    ulong ConfirmedGeneration,
    ulong RollbackFloor,
    BootSlot? TrialSlot,
    ulong? TrialGeneration,
    ulong PreviousConfirmedGeneration,
    Guid TrialNonce,
    byte TrialAttemptsRemaining,
    bool TrialAttempted,
    ulong DurableSequence,
    bool Committed,
    ReadOnlyMemory<byte> IntegrityTag);
