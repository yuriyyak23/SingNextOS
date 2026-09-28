using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class V6RasFailureConsequenceTests
{
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
