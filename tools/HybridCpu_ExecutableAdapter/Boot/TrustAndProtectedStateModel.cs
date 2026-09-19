using System.Security.Cryptography;

namespace YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;

internal enum BootPolicyMode { Production, Development, Recovery }
internal enum TrustDecision { Accepted, UnknownKey, AmbiguousKey, RevokedKey, StaleTrustEpoch, BadSignature, RollbackRejected, UnsignedDenied, WrongConfirmation }
internal sealed record ModelTrustKey(string KeyId, byte[] Secret, ulong TrustEpoch, bool Revoked, BootPolicyMode Role);
internal sealed record ProtectedBootState(Guid RollbackDomainId, Guid ImageId, ulong Generation, Guid BootNonce, bool Confirmed, ulong Sequence);

internal interface IModelSignatureVerifier
{
    bool Verify(ReadOnlySpan<byte> signedBytes, ReadOnlySpan<byte> signature, ModelTrustKey key);
}

internal sealed class DeterministicModelSignatureVerifier : IModelSignatureVerifier
{
    public bool Verify(ReadOnlySpan<byte> signedBytes, ReadOnlySpan<byte> signature, ModelTrustKey key) =>
        signature.Length == 48 && CryptographicOperations.FixedTimeEquals(HMACSHA384.HashData(key.Secret, signedBytes), signature);
    public static byte[] Sign(ReadOnlySpan<byte> signedBytes, ModelTrustKey key) => HMACSHA384.HashData(key.Secret, signedBytes);
}

internal sealed class AtomicProtectedStateModel
{
    private ProtectedBootState? _active;
    private ProtectedBootState? _prepared;
    private readonly Dictionary<Guid, ulong> _floors = [];

    public ProtectedBootState? Read() => _active;
    public void Prepare(ProtectedBootState state) => _prepared = state;
    public void Commit() { if (_prepared is null) throw new InvalidOperationException("No prepared record."); _active = _prepared; _prepared = null; }
    public void SimulatePowerLoss() => _prepared = null;
    public ulong Floor(Guid domain) => _floors.GetValueOrDefault(domain);

    public TrustDecision Confirm(Guid domain, Guid imageId, ulong generation, Guid nonce)
    {
        if (_active is null || _active.Confirmed || _active.RollbackDomainId != domain || _active.ImageId != imageId ||
            _active.Generation != generation || _active.BootNonce != nonce)
            return TrustDecision.WrongConfirmation;
        _active = _active with { Confirmed = true, Sequence = checked(_active.Sequence + 1) };
        _floors[domain] = Math.Max(Floor(domain), generation);
        return TrustDecision.Accepted;
    }
}

internal sealed class BootTrustModel(IModelSignatureVerifier verifier)
{
    public TrustDecision Verify(ReadOnlySpan<byte> signedBytes, ReadOnlySpan<byte> signature, string keyId,
        ulong manifestTrustEpoch, ulong activeTrustEpoch, ulong generation, ulong rollbackFloor,
        BootPolicyMode policyMode, bool hardwareDevelopmentEnabled, IReadOnlyList<ModelTrustKey> keys)
    {
        var matches = keys.Where(x => StringComparer.Ordinal.Equals(x.KeyId, keyId)).Take(2).ToArray();
        if (matches.Length == 0) return TrustDecision.UnknownKey;
        if (matches.Length != 1) return TrustDecision.AmbiguousKey;
        var key = matches[0];
        if (key.Revoked) return TrustDecision.RevokedKey;
        if (manifestTrustEpoch != activeTrustEpoch || key.TrustEpoch != activeTrustEpoch) return TrustDecision.StaleTrustEpoch;
        if (generation < rollbackFloor) return TrustDecision.RollbackRejected;
        if (key.Role != policyMode) return TrustDecision.UnknownKey;
        if (signature.IsEmpty)
            return policyMode == BootPolicyMode.Development && hardwareDevelopmentEnabled ? TrustDecision.Accepted : TrustDecision.UnsignedDenied;
        return verifier.Verify(signedBytes, signature, key) ? TrustDecision.Accepted : TrustDecision.BadSignature;
    }
}
