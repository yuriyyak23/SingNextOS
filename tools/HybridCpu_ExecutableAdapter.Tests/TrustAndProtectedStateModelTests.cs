using YAKSys_Hybrid_CPU.ExecutableAdapter.Boot;
using Xunit;

namespace HybridCpu_ExecutableAdapter.Tests;

public sealed class TrustAndProtectedStateModelTests
{
    private static readonly byte[] Payload = "signed manifest bytes"u8.ToArray();
    private static readonly ModelTrustKey Key = new("prod", Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), 7, false, BootPolicyMode.Production);

    [Fact]
    public void Model_signature_requires_current_nonrevoked_role_key_and_floor()
    {
        var trust = new BootTrustModel(new DeterministicModelSignatureVerifier()); var signature = DeterministicModelSignatureVerifier.Sign(Payload, Key);
        Assert.Equal(TrustDecision.Accepted, trust.Verify(Payload, signature, "prod", 7, 7, 9, 8, BootPolicyMode.Production, false, [Key]));
        Assert.Equal(TrustDecision.BadSignature, trust.Verify(Payload, new byte[48], "prod", 7, 7, 9, 8, BootPolicyMode.Production, false, [Key]));
        Assert.Equal(TrustDecision.UnknownKey, trust.Verify(Payload, signature, "missing", 7, 7, 9, 8, BootPolicyMode.Production, false, [Key]));
        Assert.Equal(TrustDecision.AmbiguousKey, trust.Verify(Payload, signature, "prod", 7, 7, 9, 8, BootPolicyMode.Production, false, [Key, Key]));
        Assert.Equal(TrustDecision.RevokedKey, trust.Verify(Payload, signature, "prod", 7, 7, 9, 8, BootPolicyMode.Production, false, [Key with { Revoked = true }]));
        Assert.Equal(TrustDecision.StaleTrustEpoch, trust.Verify(Payload, signature, "prod", 6, 7, 9, 8, BootPolicyMode.Production, false, [Key]));
        Assert.Equal(TrustDecision.RollbackRejected, trust.Verify(Payload, signature, "prod", 7, 7, 7, 8, BootPolicyMode.Production, false, [Key]));
    }

    [Fact]
    public void Unsigned_image_needs_both_development_policy_and_hardware_enable()
    {
        var dev = Key with { KeyId = "dev", Role = BootPolicyMode.Development };
        var trust = new BootTrustModel(new DeterministicModelSignatureVerifier());
        Assert.Equal(TrustDecision.UnknownKey, trust.Verify(Payload, [], "dev", 7, 7, 9, 0, BootPolicyMode.Production, false, [dev]));
        Assert.Equal(TrustDecision.UnsignedDenied, trust.Verify(Payload, [], "dev", 7, 7, 9, 0, BootPolicyMode.Development, false, [dev]));
        Assert.Equal(TrustDecision.Accepted, trust.Verify(Payload, [], "dev", 7, 7, 9, 0, BootPolicyMode.Development, true, [dev]));
        Assert.Equal(TrustDecision.UnknownKey, trust.Verify(Payload, [], "prod", 7, 7, 9, 0, BootPolicyMode.Development, true, [Key]));
    }

    [Fact]
    public void Power_loss_selects_only_complete_old_or_new_state()
    {
        var store = new AtomicProtectedStateModel(); var old = new ProtectedBootState(Guid.NewGuid(), Guid.NewGuid(), 4, Guid.NewGuid(), true, 1); store.Prepare(old); store.Commit();
        var next = new ProtectedBootState(Guid.NewGuid(), Guid.NewGuid(), 5, Guid.NewGuid(), false, 2); store.Prepare(next); store.SimulatePowerLoss(); Assert.Equal(old, store.Read());
        store.Prepare(next); store.Commit(); Assert.Equal(next, store.Read());
    }

    [Fact]
    public void Rollback_floor_advances_only_after_bound_confirmation()
    {
        var domain = Guid.NewGuid(); var state = new ProtectedBootState(domain, Guid.NewGuid(), 12, Guid.NewGuid(), false, 1); var store = new AtomicProtectedStateModel(); store.Prepare(state); store.Commit();
        Assert.Equal(0UL, store.Floor(domain)); Assert.Equal(TrustDecision.WrongConfirmation, store.Confirm(domain, state.ImageId, state.Generation, Guid.NewGuid())); Assert.Equal(0UL, store.Floor(domain));
        Assert.Equal(TrustDecision.WrongConfirmation, store.Confirm(Guid.NewGuid(), state.ImageId, state.Generation, state.BootNonce));
        Assert.Equal(TrustDecision.Accepted, store.Confirm(domain, state.ImageId, state.Generation, state.BootNonce)); Assert.Equal(12UL, store.Floor(domain)); Assert.True(store.Read()!.Confirmed);
        Assert.Equal(TrustDecision.WrongConfirmation, store.Confirm(domain, state.ImageId, state.Generation, state.BootNonce));
    }
}
