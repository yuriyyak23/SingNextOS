using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class V6RasFailureConsequenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerationAdvanceCannotResetObservationSequenceOrClearDamage(bool exhausted)
    {
        var context = Create();
        var unaffected = context.Kernel.AcquireRegionUse(context.Process, context.Buffer.Handle,
            RegionUseMode.ReadOnly, new(32, 8)).Value!;
        var sequence = exhausted ? ulong.MaxValue : 10UL;
        var original = Evidence(new(8, 8)) with { ObservationSequence = sequence };
        var damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner, original).Value!;
        var next = original.Scope with { ProviderGeneration = 2, FailureDomainGeneration = 2 };
        Assert.Equal(KernelError.StaleGeneration, context.Kernel.Regions.QuarantineSubrange(
            context.Buffer.Handle, context.Owner, original with { Scope = next, ObservationSequence = 1 }).Error);
        Assert.Equal(KernelError.StaleGeneration, context.Kernel.Regions.RecordFailureDomainReconfiguration(
            damage.Handle, next, 1).Error);
        var replacement = new RegionBackingReplacementEvidenceV1(1, damage.Handle, next, 1, 2,
            damage.Range, new('a', 64), new('b', 64));
        Assert.Equal(KernelError.StaleGeneration, context.Kernel.Regions.RecordAuthoritativeBackingReplacement(
            context.Owner, replacement).Error);
        Assert.Equal(damage, Assert.Single(context.Kernel.Regions.SnapshotDamage()));
        Assert.True(context.Kernel.ValidateRegionUse(context.Process, unaffected.Handle).IsSuccess);
        Assert.Equal(KernelError.Quarantined,
            context.Kernel.Regions.ReservePlatformMapping(context.Buffer.Handle, context.Owner).Error);
        if (!exhausted)
        {
            var rebound = context.Kernel.Regions.RecordFailureDomainReconfiguration(damage.Handle, next, sequence + 1);
            Assert.True(rebound.IsSuccess, rebound.Message);
            Assert.Equal(sequence + 1, rebound.Value!.EvidenceObservationSequence);
            Assert.Equal(damage.Handle.Generation + 1, rebound.Value.Handle.Generation);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureGenerationExhaustionCannotWrapOrPartiallyReconfigure(bool provider)
    {
        var context = Create();
        var original = Evidence(new(8, 8));
        var scope = provider ? original.Scope with { ProviderGeneration = ulong.MaxValue } :
            original.Scope with { FailureDomainGeneration = ulong.MaxValue };
        var damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle,
            context.Owner, original with { Scope = scope }).Value!;
        var wrapped = provider ? scope with { ProviderGeneration = 0, FailureDomainGeneration = 2 } :
            scope with { ProviderGeneration = 2, FailureDomainGeneration = 0 };
        Assert.Equal(KernelError.InvalidMessage, context.Kernel.Regions.RecordFailureDomainReconfiguration(
            damage.Handle, wrapped, 2).Error);
        Assert.Equal(KernelError.StaleGeneration, context.Kernel.Regions.RecordFailureDomainReconfiguration(
            damage.Handle, scope, 2).Error);
        Assert.Equal(damage, Assert.Single(context.Kernel.Regions.SnapshotDamage()));
        Assert.Equal(KernelError.Quarantined,
            context.Kernel.Regions.ReservePlatformMapping(context.Buffer.Handle, context.Owner).Error);
    }
    [Fact]
    public void CanonicalUnicodeFailureDomainRemainsAccepted()
    {
        var context = Create();
        var original = Evidence(new(8, 8));
        var evidence = original with { Scope = original.Scope with
            { ProviderId = "провайдер-😀", FailureDomainId = "bank-零" } };
        var result = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner, evidence);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(evidence.Scope, result.Value!.FailureDomain);
        Assert.False(evidence.AuthorizesRegionMutation);
        Assert.False(evidence.AuthorizesReclaim);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void NoncanonicalFailureDomainCannotMutateRegionOrConsumeEvidenceSequence(int mutation)
    {
        var context = Create();
        var use = context.Kernel.AcquireRegionUse(context.Process, context.Buffer.Handle,
            RegionUseMode.ReadOnly, new(0, 64)).Value!;
        var original = Evidence(new(8, 8));
        var scope = mutation switch
        {
            0 => original.Scope with { ProviderId = original.Scope.ProviderId + " " },
            1 => original.Scope with { FailureDomainId = " " + original.Scope.FailureDomainId },
            2 => original.Scope with { ProviderId = original.Scope.ProviderId + (char)1 },
            3 => original.Scope with { FailureDomainId = original.Scope.FailureDomainId + (char)1 },
            4 => original.Scope with { ProviderId = original.Scope.ProviderId + (char)0xD800 },
            _ => original.Scope with { FailureDomainId = original.Scope.FailureDomainId + (char)0xDC00 }
        };
        Assert.Equal(KernelError.InvalidMessage, context.Kernel.Regions.QuarantineSubrange(
            context.Buffer.Handle, context.Owner, original with { Scope = scope, ObservationSequence = 100 }).Error);
        Assert.Empty(context.Kernel.Regions.SnapshotDamage());
        Assert.True(context.Kernel.ValidateRegionUse(context.Process, use.Handle).IsSuccess);
        Assert.True(context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle,
            context.Owner, original).IsSuccess);
        Assert.Single(context.Kernel.Regions.SnapshotDamage());
        Assert.Equal(KernelError.StaleGeneration, context.Kernel.ValidateRegionUse(context.Process, use.Handle).Error);
    }
    [Fact]
    public void DamagedRegionCannotAcquirePlatformMappingBeforeAuthoritativeReplacement()
    {
        var context = Create();
        var damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle,
            context.Owner, Evidence(new(8, 8))).Value!;
        Assert.Equal(KernelError.Quarantined,
            context.Kernel.Regions.ReservePlatformMapping(context.Buffer.Handle, context.Owner).Error);
        Assert.False(context.Kernel.Regions.HasPlatformMappingReservation(context.Buffer.Handle, context.Owner));
        Assert.True(context.Kernel.Regions.RecordAuthoritativeBackingReplacement(
            context.Owner, Replacement(damage)).IsSuccess);
        Assert.True(context.Kernel.Regions.ReservePlatformMapping(context.Buffer.Handle, context.Owner).IsSuccess);
        Assert.True(context.Kernel.Regions.ReleasePlatformMappingReservation(context.Buffer.Handle, context.Owner).IsSuccess);
    }

    [Fact]
    public void ReplacementWaitsForExactBackingLeaseClosureAndCanRetryUnchangedEvidence()
    {
        var context = Create();
        var backing = context.Kernel.Regions.ReserveBacking(context.Buffer.Handle, context.Owner).Value!;
        var damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle,
            context.Owner, Evidence(new(8, 8))).Value!;
        var replacement = Replacement(damage);

        Assert.Equal(KernelError.PlatformBindingActive,
            context.Kernel.Regions.RecordAuthoritativeBackingReplacement(context.Owner, replacement).Error);
        Assert.Equal(damage, Assert.Single(context.Kernel.Regions.SnapshotDamage()));
        Assert.Equal(KernelError.StaleGeneration, context.Kernel.Regions.ReleaseBacking(
            backing.Handle with { Generation = backing.Handle.Generation + 1 }, context.Owner).Error);
        Assert.Equal(KernelError.PlatformBindingActive,
            context.Kernel.Regions.RecordAuthoritativeBackingReplacement(context.Owner, replacement).Error);
        Assert.Equal(damage, Assert.Single(context.Kernel.Regions.SnapshotDamage()));

        Assert.True(context.Kernel.Regions.ReleaseBacking(backing.Handle, context.Owner).IsSuccess);
        var cleared = context.Kernel.Regions.RecordAuthoritativeBackingReplacement(context.Owner, replacement);
        Assert.True(cleared.IsSuccess, cleared.Message);
        Assert.Empty(context.Kernel.Regions.SnapshotDamage());
        Assert.False(cleared.Value!.GrantsRegionAuthority);
        Assert.False(cleared.Value.AuthorizesReclaim);
    }

    [Theory]
    [InlineData(64L, 1L)]
    [InlineData(63L, 2L)]
    public void OutOfBoundsDamageCannotMutateUsesOrEvidenceHighWatermark(long offset, long length)
    {
        var context = Create();
        var use = context.Kernel.AcquireRegionUse(context.Process, context.Buffer.Handle,
            RegionUseMode.ReadOnly, new(0, 64)).Value!;
        var rejected = Evidence(new(offset, length)) with { ObservationSequence = 100 };

        Assert.Equal(KernelError.InvalidRegionState, context.Kernel.Regions.QuarantineSubrange(
            context.Buffer.Handle, context.Owner, rejected).Error);
        Assert.Empty(context.Kernel.Regions.SnapshotDamage());
        Assert.True(context.Kernel.ValidateRegionUse(context.Process, use.Handle).IsSuccess);

        var accepted = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle,
            context.Owner, Evidence(new(63, 1)));
        Assert.True(accepted.IsSuccess, accepted.Message);
        Assert.Single(context.Kernel.Regions.SnapshotDamage());
        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.ValidateRegionUse(context.Process, use.Handle).Error);
    }

    [Fact]
    public void RegionAuthorityQuarantinesOnlyTheDamagedSubrange()
    {
        var context = Create();
        var damaged = context.Kernel.AcquireRegionUse(context.Process, context.Buffer.Handle,
            RegionUseMode.ReadOnly, new(0, 16)).Value!;
        var unaffected = context.Kernel.AcquireRegionUse(context.Process, context.Buffer.Handle,
            RegionUseMode.ReadOnly, new(32, 16)).Value!;

        var quarantine = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
            Evidence(new(8, 8)));

        Assert.True(quarantine.IsSuccess, quarantine.Message);
        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.ValidateRegionUse(context.Process, damaged.Handle).Error);
        Assert.True(context.Kernel.ValidateRegionUse(context.Process, unaffected.Handle).IsSuccess);
        Assert.Equal(KernelError.Quarantined, context.Kernel.AcquireRegionUse(context.Process,
            context.Buffer.Handle, RegionUseMode.ReadOnly, new(12, 8)).Error);
        Assert.True(context.Kernel.AcquireRegionUse(context.Process, context.Buffer.Handle,
            RegionUseMode.ReadOnly, new(48, 8)).IsSuccess);
    }

    [Fact]
    public void QuarantinedDamagePinsOwnershipAndBlocksReclaim()
    {
        var context = Create();
        Assert.True(context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
            Evidence(new(4, 4))).IsSuccess);

        Assert.Equal(KernelError.Quarantined,
            context.Kernel.ReleaseRegion(context.Process, context.Buffer).Error);
        Assert.Throws<InvalidOperationException>(() =>
            context.Kernel.Regions.ReclaimAllForDomain(context.Owner.DomainId));
    }

    [Fact]
    public void DamagedRegionCannotAcquireNewBackingBeforeAuthoritativeReplacement()
    {
        var context = Create();
        var damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle,
            context.Owner, Evidence(new(8, 8))).Value!;

        Assert.Equal(KernelError.Quarantined,
            context.Kernel.Regions.ReserveBacking(context.Buffer.Handle, context.Owner).Error);
        Assert.True(context.Kernel.Regions.RecordAuthoritativeBackingReplacement(
            context.Owner, Replacement(damage)).IsSuccess);

        var backing = context.Kernel.Regions.ReserveBacking(context.Buffer.Handle, context.Owner);
        Assert.True(backing.IsSuccess, backing.Message);
        Assert.True(context.Kernel.Regions.ReleaseBacking(backing.Value!.Handle, context.Owner).IsSuccess);
    }

    [Fact]
    public void ReplayedHealthEvidenceCannotCreateASecondDamageIdentity()
    {
        var context = Create();
        var evidence = Evidence(new(4, 4));
        Assert.True(context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
            evidence).IsSuccess);

        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
                evidence).Error);
        Assert.Single(context.Kernel.Regions.SnapshotDamage());
    }

    [Fact]
    public void ReconfigurationAdvancesExactGenerationAndRejectsAba()
    {
        var context = Create();
        var damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
            Evidence(new(8, 8))).Value!;
        var nextScope = damage.FailureDomain with
        {
            ProviderGeneration = damage.FailureDomain.ProviderGeneration + 1,
            FailureDomainGeneration = damage.FailureDomain.FailureDomainGeneration + 1,
        };

        var rebound = context.Kernel.Regions.RecordFailureDomainReconfiguration(
            damage.Handle, nextScope, damage.EvidenceObservationSequence + 1);

        Assert.True(rebound.IsSuccess, rebound.Message);
        Assert.Equal(damage.Handle.Generation + 1, rebound.Value!.Handle.Generation);
        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.Regions.RecordFailureDomainReconfiguration(damage.Handle,
                nextScope with { ProviderGeneration = nextScope.ProviderGeneration + 1,
                    FailureDomainGeneration = nextScope.FailureDomainGeneration + 1 },
                rebound.Value.EvidenceObservationSequence + 1).Error);
        Assert.Equal(KernelError.Quarantined, context.Kernel.AcquireRegionUse(context.Process,
            context.Buffer.Handle, RegionUseMode.ReadOnly, new(8, 1)).Error);
    }

    [Fact]
    public void AuthoritativeReplacementRequiresExplicitOverlappingUseClosure()
    {
        var context = Create();
        var use = context.Kernel.AcquireRegionUse(context.Process, context.Buffer.Handle,
            RegionUseMode.DevicePrivate, new(8, 8)).Value!;
        var damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
            Evidence(new(8, 8))).Value!;
        var replacement = Replacement(damage);

        Assert.Equal(KernelError.ExternalEffectUncontained,
            context.Kernel.Regions.RecordAuthoritativeBackingReplacement(context.Owner, replacement).Error);
        Assert.True(context.Kernel.Regions.ReleaseUse(use.Handle, context.Owner).IsSuccess);

        var cleared = context.Kernel.Regions.RecordAuthoritativeBackingReplacement(context.Owner, replacement);

        Assert.True(cleared.IsSuccess, cleared.Message);
        Assert.Empty(context.Kernel.Regions.SnapshotDamage());
        Assert.True(context.Kernel.AcquireRegionUse(context.Process, context.Buffer.Handle,
            RegionUseMode.ReadOnly, new(8, 8)).IsSuccess);
        Assert.False(cleared.Value!.GrantsRegionAuthority);
        Assert.False(cleared.Value.AuthorizesReclaim);
    }

    [Fact]
    public void ReplacementClearsOnlyExactDamageAndPreservesDisjointUse()
    {
        var context = Create();
        var disjoint = context.Kernel.AcquireRegionUse(context.Process, context.Buffer.Handle,
            RegionUseMode.ReadOnly, new(48, 8)).Value!;
        var first = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
            Evidence(new(8, 8))).Value!;
        var secondEvidence = Evidence(new(24, 8)) with
        {
            Scope = new("test-provider", "bank-1", 1, 1),
            ObservationSequence = 2,
        };
        var second = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
            secondEvidence).Value!;

        Assert.True(context.Kernel.Regions.RecordAuthoritativeBackingReplacement(
            context.Owner, Replacement(first)).IsSuccess);

        Assert.Equal(second.Handle, Assert.Single(context.Kernel.Regions.SnapshotDamage()).Handle);
        Assert.True(context.Kernel.ValidateRegionUse(context.Process, disjoint.Handle).IsSuccess);
        Assert.True(context.Kernel.AcquireRegionUse(context.Process, context.Buffer.Handle,
            RegionUseMode.ReadOnly, new(8, 8)).IsSuccess);
        Assert.Equal(KernelError.Quarantined, context.Kernel.AcquireRegionUse(context.Process,
            context.Buffer.Handle, RegionUseMode.ReadOnly, new(24, 1)).Error);
    }

    [Fact]
    public void ReplacementRejectsDamageScopeRangeAndGenerationAba()
    {
        var context = Create();
        var damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
            Evidence(new(8, 8))).Value!;
        var exact = Replacement(damage);

        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.Regions.RecordAuthoritativeBackingReplacement(context.Owner,
                exact with { Damage = exact.Damage with { Generation = exact.Damage.Generation + 1 } }).Error);
        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.Regions.RecordAuthoritativeBackingReplacement(context.Owner,
                exact with { Range = new(9, 7) }).Error);
        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.Regions.RecordAuthoritativeBackingReplacement(context.Owner,
                exact with { NextFailureDomain = exact.NextFailureDomain with
                    { ProviderGeneration = exact.NextFailureDomain.ProviderGeneration + 1 } }).Error);
    }

    [Fact]
    public void ClearedDamageRetainsEvidenceHighWatermarkAgainstReplay()
    {
        var context = Create();
        var original = Evidence(new(8, 8));
        var damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
            original).Value!;
        Assert.True(context.Kernel.Regions.RecordAuthoritativeBackingReplacement(
            context.Owner, Replacement(damage)).IsSuccess);

        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner, original).Error);
    }

    [Fact]
    public void ClearedDamageRejectsOldProviderGenerationEvenWithNewerObservationSequence()
    {
        var context = Create();
        var original = Evidence(new(8, 8));
        var damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle,
            context.Owner, original).Value!;
        var replacement = Replacement(damage);
        Assert.True(context.Kernel.Regions.RecordAuthoritativeBackingReplacement(
            context.Owner, replacement).IsSuccess);

        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
                original with { ObservationSequence = replacement.EvidenceObservationSequence + 1 }).Error);

        var current = original with
        {
            Scope = replacement.NextFailureDomain,
            ObservationSequence = replacement.EvidenceObservationSequence + 1,
        };
        Assert.True(context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle,
            context.Owner, current).IsSuccess);
    }

    [Fact]
    public void ReconfiguredDamageRejectsOldProviderGenerationWithNewerSequence()
    {
        var context = Create();
        var original = Evidence(new(8, 8));
        var damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle,
            context.Owner, original).Value!;
        var nextScope = damage.FailureDomain with
        {
            ProviderGeneration = damage.FailureDomain.ProviderGeneration + 1,
            FailureDomainGeneration = damage.FailureDomain.FailureDomainGeneration + 1,
        };
        Assert.True(context.Kernel.Regions.RecordFailureDomainReconfiguration(
            damage.Handle, nextScope, damage.EvidenceObservationSequence + 1).IsSuccess);

        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
                original with { ObservationSequence = damage.EvidenceObservationSequence + 2 }).Error);
    }

    [Fact]
    public void OlderDamageCannotRegressFailureDomainHighWatermark()
    {
        var context = Create();
        var firstEvidence = Evidence(new(8, 8));
        var first = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
            firstEvidence).Value!;
        var newerEvidence = Evidence(new(24, 8)) with { ObservationSequence = 3 };
        Assert.True(context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
            newerEvidence).IsSuccess);

        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.Regions.RecordAuthoritativeBackingReplacement(
                context.Owner, Replacement(first)).Error);
        var freshReplacement = Replacement(first) with { EvidenceObservationSequence = 4 };
        Assert.True(context.Kernel.Regions.RecordAuthoritativeBackingReplacement(
            context.Owner, freshReplacement).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
                newerEvidence).Error);
    }

    [Fact]
    public void GranularRasGateRemainsOff()
    {
        Assert.False(V6FeatureGates.IsEnabled("V6-RAS-PARTIAL-FAILURE"));
    }

    private static Context Create()
    {
        var kernel = new RuntimeKernel();
        var process = TestFixtures.Create(kernel, 8610, 8620).Handle;
        var buffer = kernel.AllocateBuffer<byte>(process, 64).Value!;
        return new(kernel, process, new RegionOwner(new(8620), process.Generation), buffer);
    }

    private static ProviderHealthEvidenceV1 Evidence(RegionUseRange range) => new(1,
        new("test-provider", "bank-0", 1, 1), 1, ProviderHealthStateV1.Degraded,
        ProviderFaultClassV1.Omission, range);

    private static RegionBackingReplacementEvidenceV1 Replacement(RegionDamageDescriptorV1 damage) => new(1,
        damage.Handle, damage.FailureDomain with
        {
            ProviderGeneration = damage.FailureDomain.ProviderGeneration + 1,
            FailureDomainGeneration = damage.FailureDomain.FailureDomainGeneration + 1,
        }, damage.EvidenceObservationSequence + 1, 2, damage.Range, new('a', 64), new('b', 64));

    private sealed record Context(RuntimeKernel Kernel, ProcessHandle Process, RegionOwner Owner,
        OwnedBuffer<byte> Buffer);
}
