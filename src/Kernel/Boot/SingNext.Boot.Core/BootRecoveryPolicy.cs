namespace SingNext.Boot.Core;

public enum BootRecoveryRoute
{
    Trial = 0,
    Confirmed,
    Replica,
    SignedLocalRecovery,
    Halt,
}

public readonly record struct BootRecoveryCandidate(bool Present, bool Authenticated, ulong Generation);

public static class BootRecoveryPolicy
{
    public static BootRecoveryRoute Select(
        BootRecoveryCandidate trial,
        byte trialAttemptsRemaining,
        BootRecoveryCandidate confirmed,
        BootRecoveryCandidate replica,
        BootRecoveryCandidate localRecovery,
        ulong rollbackFloor)
    {
        if (trialAttemptsRemaining != 0 && Eligible(trial, rollbackFloor)) return BootRecoveryRoute.Trial;
        if (Eligible(confirmed, rollbackFloor)) return BootRecoveryRoute.Confirmed;
        if (Eligible(replica, rollbackFloor)) return BootRecoveryRoute.Replica;
        return localRecovery.Present && localRecovery.Authenticated
            ? BootRecoveryRoute.SignedLocalRecovery
            : BootRecoveryRoute.Halt;
    }

    private static bool Eligible(BootRecoveryCandidate candidate, ulong floor) =>
        candidate.Present && candidate.Authenticated && candidate.Generation >= floor;
}
