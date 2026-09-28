using System.Security.Cryptography;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;

internal enum AbSlot { A, B }
internal enum RecoveryRoute { Trial, Confirmed, Replica, SignedLocalRecovery, Halt }
internal enum AbUpdateFailure { None, PowerLoss, ReadBackMismatch, InvalidManifest, RollbackRejected, WrongConfirmation, AttemptsExhausted }

internal sealed record AbImage(Guid ImageId, ulong Generation, byte[] Payload, byte[] ExpectedSha384, bool SignatureValid);
internal sealed record TrialRecord(AbSlot Slot, Guid RollbackDomain, Guid ImageId, ulong Generation, Guid Nonce, int AttemptsRemaining, bool BootAttempted, ulong Sequence);
internal sealed record AbUpdateResult(AbUpdateFailure Failure, int DurableBarrier, bool ConfirmedRouteAvailable, bool RecoveryRouteAvailable);
internal sealed record RecoveryDecision(RecoveryRoute Route, AbImage? Image);

internal sealed class AbRecoveryModel
{
    private readonly Dictionary<AbSlot, AbImage> _payloads = [];
    private readonly Dictionary<AbSlot, AbImage> _manifests = [];
    private readonly Dictionary<Guid, ulong> _rollbackFloors = [];
    private TrialRecord? _trial;
    private AbSlot _confirmedSlot;
    private readonly AbImage _localRecovery;
    private readonly Guid _rollbackDomain;

    public AbRecoveryModel(Guid rollbackDomain, AbSlot confirmedSlot, AbImage confirmed, AbImage signedLocalRecovery)
    {
        if (rollbackDomain == Guid.Empty || !Valid(confirmed) || !Valid(signedLocalRecovery)) throw new ArgumentException("Domain and initial images must be valid.");
        _rollbackDomain = rollbackDomain;
        _confirmedSlot = confirmedSlot;
        _payloads[confirmedSlot] = confirmed;
        _manifests[confirmedSlot] = confirmed;
        _localRecovery = signedLocalRecovery;
    }

    public ulong RollbackFloor(Guid domain) => _rollbackFloors.GetValueOrDefault(domain);
    public TrialRecord? Trial => _trial;

    public AbUpdateResult InstallInactive(AbImage image, Guid nonce, int attempts, int powerLossAfterBarrier = 0)
    {
        if (!image.SignatureValid || image.Payload.Length == 0 || image.ExpectedSha384.Length != 48 || attempts is <= 0 or > 16)
            return Result(AbUpdateFailure.InvalidManifest, 0);
        if (image.Generation < RollbackFloor(_rollbackDomain)) return Result(AbUpdateFailure.RollbackRejected, 0);
        var inactive = _confirmedSlot == AbSlot.A ? AbSlot.B : AbSlot.A;
        _payloads[inactive] = image; // barrier 1: payload first
        if (powerLossAfterBarrier == 1) return Result(AbUpdateFailure.PowerLoss, 1);
        if (!CryptographicOperations.FixedTimeEquals(SHA384.HashData(_payloads[inactive].Payload), image.ExpectedSha384))
            return Result(AbUpdateFailure.ReadBackMismatch, 2);
        if (powerLossAfterBarrier == 2) return Result(AbUpdateFailure.PowerLoss, 2);
        _manifests[inactive] = image; // barrier 3: manifest last
        if (powerLossAfterBarrier == 3) return Result(AbUpdateFailure.PowerLoss, 3);
        _trial = new(inactive, _rollbackDomain, image.ImageId, image.Generation, nonce, attempts, false, checked((_trial?.Sequence ?? 0) + 1)); // protected state
        if (powerLossAfterBarrier == 4) return Result(AbUpdateFailure.PowerLoss, 4);
        return Result(AbUpdateFailure.None, 4);
    }

    public RecoveryDecision Select(IReadOnlyList<AbImage> replicas)
    {
        if (_trial is { AttemptsRemaining: > 0 } trial && TrySlot(trial.Slot, out var trialImage) &&
            trialImage.ImageId == trial.ImageId && trialImage.Generation == trial.Generation)
        {
            _trial = trial with { AttemptsRemaining = trial.AttemptsRemaining - 1, BootAttempted = true, Sequence = checked(trial.Sequence + 1) };
            return new(RecoveryRoute.Trial, trialImage);
        }
        if (TrySlot(_confirmedSlot, out var confirmed)) return new(RecoveryRoute.Confirmed, confirmed);
        var floor = RollbackFloor(_rollbackDomain);
        var replica = replicas.Where(x => Valid(x) && x.Generation >= floor).OrderByDescending(static x => x.Generation).ThenBy(static x => x.ImageId).FirstOrDefault();
        if (replica is not null) return new(RecoveryRoute.Replica, replica);
        if (Valid(_localRecovery)) return new(RecoveryRoute.SignedLocalRecovery, _localRecovery);
        return new(RecoveryRoute.Halt, null);
    }

    public AbUpdateFailure Confirm(Guid rollbackDomain, Guid imageId, ulong generation, Guid nonce)
    {
        if (_trial is not { BootAttempted: true } trial || rollbackDomain != _rollbackDomain || trial.RollbackDomain != rollbackDomain ||
            trial.ImageId != imageId || trial.Generation != generation || trial.Nonce != nonce || !TrySlot(trial.Slot, out _))
            return AbUpdateFailure.WrongConfirmation;
        _confirmedSlot = trial.Slot;
        _rollbackFloors[rollbackDomain] = Math.Max(RollbackFloor(rollbackDomain), generation);
        _trial = null;
        return AbUpdateFailure.None;
    }

    internal void CorruptConfirmedForTest() => _manifests.Remove(_confirmedSlot);
    internal void CorruptLocalRecoveryForTest() => _localRecovery.Payload[0] ^= 0x80;

    private bool TrySlot(AbSlot slot, out AbImage image)
    {
        if (_manifests.TryGetValue(slot, out image!) && _payloads.TryGetValue(slot, out var payload) &&
            image == payload && Valid(image)) return true;
        image = null!;
        return false;
    }

    private static bool Valid(AbImage image) => image.SignatureValid && image.Payload.Length > 0 && image.ExpectedSha384.Length == 48 &&
        CryptographicOperations.FixedTimeEquals(SHA384.HashData(image.Payload), image.ExpectedSha384);

    private AbUpdateResult Result(AbUpdateFailure failure, int barrier) =>
        new(failure, barrier, TrySlot(_confirmedSlot, out _), Valid(_localRecovery));
}
