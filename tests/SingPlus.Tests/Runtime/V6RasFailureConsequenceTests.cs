using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class V6RasFailureConsequenceTests
{
    [Theory]
    [InlineData(false, 1UL)] [InlineData(true, 1UL)]
    [InlineData(false, 7UL)] [InlineData(true, 7UL)]
    public async Task SharedDomainOverlappingTeardownRetainsLastReclaimAndBothCharges(bool damageSecond, ulong secondGeneration)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle first = TestFixtures.Create(kernel, 8710, 8720).Handle;
        ProcessHandle second = TestFixtures.Create(kernel, 8711, 8720, secondGeneration).Handle;
        ProcessBudgetAdmission firstBudget = kernel.Budgets.CreateProcessHierarchy(first, "shared-first", "first",
            [new BudgetAmount(ServiceBudgetDimension.OwnedMemoryBytes, 128)]).Value;
        ProcessBudgetAdmission secondBudget = kernel.Budgets.CreateProcessHierarchy(second, "shared-second", "second",
            [new BudgetAmount(ServiceBudgetDimension.OwnedMemoryBytes, 128)]).Value;
        OwnedBuffer<byte> firstRegion = kernel.AllocateBuffer<byte>(first, 32).Value!;
        OwnedBuffer<byte> secondRegion = kernel.AllocateBuffer<byte>(second, 32).Value!;
        RegionOwner owner = new RegionOwner(new(8720), damageSecond ? secondGeneration : first.Generation);
        RegionDamageDescriptorV1 damage = kernel.Regions.QuarantineSubrange((damageSecond ? secondRegion : firstRegion).Handle,
            owner, Evidence(new(4, 4))).Value!;
        RegionDescriptor[] before = kernel.Regions.Snapshot().ToArray();
        TaskCompletionSource start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<KernelResult> a = Task.Run(async () => { await start.Task; return kernel.TerminateProcess(first); });
        Task<KernelResult> b = Task.Run(async () => { await start.Task; return kernel.TerminateProcess(second); });
        start.SetResult();
        KernelResult[] results = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(results, result => result.Error == KernelError.Quarantined);
        ProcessHandle pending = results[0].IsSuccess ? second : first;
        ProcessHandle retired = results[0].IsSuccess ? first : second;
        Assert.False(kernel.Processes.Resolve(retired).IsSuccess);
        Assert.True(kernel.Processes.Resolve(pending).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(pending).Value.LocalReclaimCompleted);
        Assert.Equal(before, kernel.Regions.Snapshot());
        foreach (BudgetAccountHandle account in new[] { firstBudget.ProcessBudget, secondBudget.ProcessBudget })
            Assert.Contains(kernel.QueryBudget(account).Value!.Usage, usage => usage.Dimension == ServiceBudgetDimension.OwnedMemoryBytes && usage.Used == 32);
        Assert.Equal(KernelError.PlatformBindingDraining, kernel.CreateProcess(TestFixtures.Manifest(8712, 8720)).Error);
        Assert.Equal(KernelError.Quarantined, kernel.TerminateProcess(pending).Error);
        RegionOwner wrongOwner = new RegionOwner(new(8720), damageSecond && secondGeneration != 1 ? 1UL : 7UL);
        Assert.False(kernel.Regions.RecordAuthoritativeBackingReplacement(wrongOwner, Replacement(damage)).IsSuccess);
        Assert.Equal(damage, Assert.Single(kernel.Regions.SnapshotDamage()));
        Assert.True(kernel.Regions.RecordAuthoritativeBackingReplacement(owner, Replacement(damage)).IsSuccess);
        Assert.True(kernel.TerminateProcess(pending).IsSuccess);
        Assert.All(kernel.Regions.Snapshot(), region => Assert.Equal(RegionState.Released, region.State));
        foreach (BudgetAccountHandle account in new[] { firstBudget.ProcessBudget, secondBudget.ProcessBudget })
            Assert.All(kernel.QueryBudget(account).Value!.Usage, usage => Assert.Equal(0UL, usage.Used));
        Assert.True(kernel.CreateProcess(TestFixtures.Manifest(8712, 8720)).IsSuccess);
        Assert.True(kernel.TerminateProcess(new(new(8712), 1)).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DomainRejoinCannotHidePendingDamagedRegionReclaim(bool reuseProcessId)
    {
        Context c = Create();
        RegionDamageDescriptorV1 damage = c.Kernel.Regions.QuarantineSubrange(c.Buffer.Handle, c.Owner, Evidence(new(4, 4))).Value!;
        Assert.Equal(KernelError.Quarantined, c.Kernel.TerminateProcess(c.Process).Error);
        Assert.False(c.Kernel.Domains.Contains(c.Owner.DomainId));
        SingProcessManifestV1 manifest = TestFixtures.Manifest(reuseProcessId ? c.Process.ProcessId.Value : 8611,
            c.Owner.DomainId.Value, reuseProcessId ? 2UL : 1UL);
        SingProcess[] before = c.Kernel.Processes.Snapshot().ToArray();
        Assert.Equal(KernelError.PlatformBindingDraining, c.Kernel.CreateProcess(manifest).Error);
        Assert.Equal(before, c.Kernel.Processes.Snapshot());
        Assert.False(c.Kernel.Domains.Contains(c.Owner.DomainId));
        ProcessHandle unrelated = TestFixtures.Create(c.Kernel, 8612, 8621).Handle;
        Assert.True(c.Kernel.TerminateProcess(unrelated).IsSuccess);
        Assert.Equal(KernelError.Quarantined, c.Kernel.TerminateProcess(c.Process).Error);
        Assert.False(c.Kernel.QueryProcessTeardown(c.Process).Value.LocalReclaimCompleted);
        Assert.Equal(damage, Assert.Single(c.Kernel.Regions.SnapshotDamage()));
        Assert.True(c.Kernel.Regions.RecordAuthoritativeBackingReplacement(c.Owner, Replacement(damage)).IsSuccess);
        Assert.True(c.Kernel.TerminateProcess(c.Process).IsSuccess);
        Assert.True(c.Kernel.CreateProcess(manifest).IsSuccess);
        ProcessHandle next = new ProcessHandle(manifest.ProcessId, manifest.Generation);
        Assert.True(c.Kernel.Domains.Contains(c.Owner.DomainId));
        Assert.False(c.Kernel.Processes.Resolve(c.Process).IsSuccess);
        Assert.True(c.Kernel.TerminateProcess(next).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DamagedDomainTeardownRefusesAtomicallyAndRetriesAfterExactReplacement(bool damageSecond)
    {
        Context c = Create();
        ProcessBudgetAdmission budget = c.Kernel.Budgets.CreateProcessHierarchy(c.Process, "ras-teardown", "ras-process",
            [new BudgetAmount(ServiceBudgetDimension.OwnedMemoryBytes, 128)]).Value;
        OwnedBuffer<byte> second = c.Kernel.AllocateBuffer<byte>(c.Process, 32).Value!;
        BudgetUsage[] charged = c.Kernel.QueryBudget(budget.ProcessBudget).Value!.Usage.ToArray();
        Assert.Contains(charged, usage => usage.Dimension == ServiceBudgetDimension.OwnedMemoryBytes && usage.Used == 32);
        OwnedBuffer<byte> target = damageSecond ? second : c.Buffer;
        RegionDamageDescriptorV1 damage = c.Kernel.Regions.QuarantineSubrange(target.Handle, c.Owner, Evidence(new(4, 4))).Value!;
        RegionDescriptor[] before = c.Kernel.Regions.Snapshot().ToArray();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Assert.Equal(KernelError.Quarantined, c.Kernel.TerminateProcess(c.Process).Error);
            ProcessTeardownSnapshot teardown = c.Kernel.QueryProcessTeardown(c.Process).Value;
            Assert.False(teardown.LocalReclaimCompleted);
            Assert.True(teardown.PlatformDomainClosed);
            Assert.Equal(KernelError.Quarantined, teardown.BlockingError);
            Assert.Equal(before, c.Kernel.Regions.Snapshot());
            Assert.Equal(damage, Assert.Single(c.Kernel.Regions.SnapshotDamage()));
            Assert.True(c.Kernel.Processes.Resolve(c.Process).IsSuccess);
            Assert.Equal(charged, c.Kernel.QueryBudget(budget.ProcessBudget).Value!.Usage);
        }
        Assert.True(c.Kernel.Regions.RecordAuthoritativeBackingReplacement(c.Owner, Replacement(damage)).IsSuccess);
        Assert.True(c.Kernel.TerminateProcess(c.Process).IsSuccess);
        Assert.Empty(c.Kernel.Regions.SnapshotDamage());
        Assert.All(c.Kernel.Regions.Snapshot(), region => Assert.Equal(RegionState.Released, region.State));
        Assert.False(c.Kernel.Processes.Resolve(c.Process).IsSuccess);
        Assert.All(c.Kernel.QueryBudget(budget.ProcessBudget).Value!.Usage, usage => Assert.Equal(0UL, usage.Used));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerationAdvanceCannotResetObservationSequenceOrClearDamage(bool exhausted)
    {
        Context context = Create();
        RegionUseDescriptor unaffected = context.Kernel.AcquireRegionUse(context.Process, context.Buffer.Handle,
            RegionUseMode.ReadOnly, new(32, 8)).Value!;
        ulong sequence = exhausted ? ulong.MaxValue : 10UL;
        ProviderHealthEvidenceV1 original = Evidence(new(8, 8)) with { ObservationSequence = sequence };
        RegionDamageDescriptorV1 damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner, original).Value!;
        FailureDomainScopeV1 next = original.Scope with { ProviderGeneration = 2, FailureDomainGeneration = 2 };
        Assert.Equal(KernelError.StaleGeneration, context.Kernel.Regions.QuarantineSubrange(
            context.Buffer.Handle, context.Owner, original with { Scope = next, ObservationSequence = 1 }).Error);
        Assert.Equal(KernelError.StaleGeneration, context.Kernel.Regions.RecordFailureDomainReconfiguration(
            damage.Handle, next, 1).Error);
        RegionBackingReplacementEvidenceV1 replacement = new RegionBackingReplacementEvidenceV1(1, damage.Handle, next, 1, 2,
            damage.Range, new('a', 64), new('b', 64));
        Assert.Equal(KernelError.StaleGeneration, context.Kernel.Regions.RecordAuthoritativeBackingReplacement(
            context.Owner, replacement).Error);
        Assert.Equal(damage, Assert.Single(context.Kernel.Regions.SnapshotDamage()));
        Assert.True(context.Kernel.ValidateRegionUse(context.Process, unaffected.Handle).IsSuccess);
        Assert.Equal(KernelError.Quarantined,
            context.Kernel.Regions.ReservePlatformMapping(context.Buffer.Handle, context.Owner).Error);
        if (!exhausted)
        {
            KernelResult<RegionDamageDescriptorV1> rebound = context.Kernel.Regions.RecordFailureDomainReconfiguration(damage.Handle, next, sequence + 1);
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
        Context context = Create();
        ProviderHealthEvidenceV1 original = Evidence(new(8, 8));
        FailureDomainScopeV1 scope = provider ? original.Scope with { ProviderGeneration = ulong.MaxValue } :
            original.Scope with { FailureDomainGeneration = ulong.MaxValue };
        RegionDamageDescriptorV1 damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle,
            context.Owner, original with { Scope = scope }).Value!;
        FailureDomainScopeV1 wrapped = provider ? scope with { ProviderGeneration = 0, FailureDomainGeneration = 2 } :
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
        Context context = Create();
        ProviderHealthEvidenceV1 original = Evidence(new(8, 8));
        ProviderHealthEvidenceV1 evidence = original with { Scope = original.Scope with
            { ProviderId = "провайдер-😀", FailureDomainId = "bank-零" } };
        KernelResult<RegionDamageDescriptorV1> result = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner, evidence);
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
        Context context = Create();
        RegionUseDescriptor use = context.Kernel.AcquireRegionUse(context.Process, context.Buffer.Handle,
            RegionUseMode.ReadOnly, new(0, 64)).Value!;
        ProviderHealthEvidenceV1 original = Evidence(new(8, 8));
        FailureDomainScopeV1 scope = mutation switch
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
        Context context = Create();
        RegionDamageDescriptorV1 damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle,
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
        Context context = Create();
        RegionBackingLeaseDescriptor backing = context.Kernel.Regions.ReserveBacking(context.Buffer.Handle, context.Owner).Value!;
        RegionDamageDescriptorV1 damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle,
            context.Owner, Evidence(new(8, 8))).Value!;
        RegionBackingReplacementEvidenceV1 replacement = Replacement(damage);

        Assert.Equal(KernelError.PlatformBindingActive,
            context.Kernel.Regions.RecordAuthoritativeBackingReplacement(context.Owner, replacement).Error);
        Assert.Equal(damage, Assert.Single(context.Kernel.Regions.SnapshotDamage()));
        Assert.Equal(KernelError.StaleGeneration, context.Kernel.Regions.ReleaseBacking(
            backing.Handle with { Generation = backing.Handle.Generation + 1 }, context.Owner).Error);
        Assert.Equal(KernelError.PlatformBindingActive,
            context.Kernel.Regions.RecordAuthoritativeBackingReplacement(context.Owner, replacement).Error);
        Assert.Equal(damage, Assert.Single(context.Kernel.Regions.SnapshotDamage()));

        Assert.True(context.Kernel.Regions.ReleaseBacking(backing.Handle, context.Owner).IsSuccess);
        KernelResult<RegionBackingReplacementReceiptV1> cleared = context.Kernel.Regions.RecordAuthoritativeBackingReplacement(context.Owner, replacement);
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
        Context context = Create();
        RegionUseDescriptor use = context.Kernel.AcquireRegionUse(context.Process, context.Buffer.Handle,
            RegionUseMode.ReadOnly, new(0, 64)).Value!;
        ProviderHealthEvidenceV1 rejected = Evidence(new(offset, length)) with { ObservationSequence = 100 };

        Assert.Equal(KernelError.InvalidRegionState, context.Kernel.Regions.QuarantineSubrange(
            context.Buffer.Handle, context.Owner, rejected).Error);
        Assert.Empty(context.Kernel.Regions.SnapshotDamage());
        Assert.True(context.Kernel.ValidateRegionUse(context.Process, use.Handle).IsSuccess);

        KernelResult<RegionDamageDescriptorV1> accepted = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle,
            context.Owner, Evidence(new(63, 1)));
        Assert.True(accepted.IsSuccess, accepted.Message);
        Assert.Single(context.Kernel.Regions.SnapshotDamage());
        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.ValidateRegionUse(context.Process, use.Handle).Error);
    }

    [Fact]
    public void RegionAuthorityQuarantinesOnlyTheDamagedSubrange()
    {
        Context context = Create();
        RegionUseDescriptor damaged = context.Kernel.AcquireRegionUse(context.Process, context.Buffer.Handle,
            RegionUseMode.ReadOnly, new(0, 16)).Value!;
        RegionUseDescriptor unaffected = context.Kernel.AcquireRegionUse(context.Process, context.Buffer.Handle,
            RegionUseMode.ReadOnly, new(32, 16)).Value!;

        KernelResult<RegionDamageDescriptorV1> quarantine = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
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
        Context context = Create();
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
        Context context = Create();
        RegionDamageDescriptorV1 damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle,
            context.Owner, Evidence(new(8, 8))).Value!;

        Assert.Equal(KernelError.Quarantined,
            context.Kernel.Regions.ReserveBacking(context.Buffer.Handle, context.Owner).Error);
        Assert.True(context.Kernel.Regions.RecordAuthoritativeBackingReplacement(
            context.Owner, Replacement(damage)).IsSuccess);

        KernelResult<RegionBackingLeaseDescriptor> backing = context.Kernel.Regions.ReserveBacking(context.Buffer.Handle, context.Owner);
        Assert.True(backing.IsSuccess, backing.Message);
        Assert.True(context.Kernel.Regions.ReleaseBacking(backing.Value!.Handle, context.Owner).IsSuccess);
    }

    [Fact]
    public void ReplayedHealthEvidenceCannotCreateASecondDamageIdentity()
    {
        Context context = Create();
        ProviderHealthEvidenceV1 evidence = Evidence(new(4, 4));
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
        Context context = Create();
        RegionDamageDescriptorV1 damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
            Evidence(new(8, 8))).Value!;
        FailureDomainScopeV1 nextScope = damage.FailureDomain with
        {
            ProviderGeneration = damage.FailureDomain.ProviderGeneration + 1,
            FailureDomainGeneration = damage.FailureDomain.FailureDomainGeneration + 1,
        };

        KernelResult<RegionDamageDescriptorV1> rebound = context.Kernel.Regions.RecordFailureDomainReconfiguration(
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
        Context context = Create();
        RegionUseDescriptor use = context.Kernel.AcquireRegionUse(context.Process, context.Buffer.Handle,
            RegionUseMode.DevicePrivate, new(8, 8)).Value!;
        RegionDamageDescriptorV1 damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
            Evidence(new(8, 8))).Value!;
        RegionBackingReplacementEvidenceV1 replacement = Replacement(damage);

        Assert.Equal(KernelError.ExternalEffectUncontained,
            context.Kernel.Regions.RecordAuthoritativeBackingReplacement(context.Owner, replacement).Error);
        Assert.True(context.Kernel.Regions.ReleaseUse(use.Handle, context.Owner).IsSuccess);

        KernelResult<RegionBackingReplacementReceiptV1> cleared = context.Kernel.Regions.RecordAuthoritativeBackingReplacement(context.Owner, replacement);

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
        Context context = Create();
        RegionUseDescriptor disjoint = context.Kernel.AcquireRegionUse(context.Process, context.Buffer.Handle,
            RegionUseMode.ReadOnly, new(48, 8)).Value!;
        RegionDamageDescriptorV1 first = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
            Evidence(new(8, 8))).Value!;
        ProviderHealthEvidenceV1 secondEvidence = Evidence(new(24, 8)) with
        {
            Scope = new("test-provider", "bank-1", 1, 1),
            ObservationSequence = 2,
        };
        RegionDamageDescriptorV1 second = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
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
        Context context = Create();
        RegionDamageDescriptorV1 damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
            Evidence(new(8, 8))).Value!;
        RegionBackingReplacementEvidenceV1 exact = Replacement(damage);

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
        Context context = Create();
        ProviderHealthEvidenceV1 original = Evidence(new(8, 8));
        RegionDamageDescriptorV1 damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
            original).Value!;
        Assert.True(context.Kernel.Regions.RecordAuthoritativeBackingReplacement(
            context.Owner, Replacement(damage)).IsSuccess);

        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner, original).Error);
    }

    [Fact]
    public void ClearedDamageRejectsOldProviderGenerationEvenWithNewerObservationSequence()
    {
        Context context = Create();
        ProviderHealthEvidenceV1 original = Evidence(new(8, 8));
        RegionDamageDescriptorV1 damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle,
            context.Owner, original).Value!;
        RegionBackingReplacementEvidenceV1 replacement = Replacement(damage);
        Assert.True(context.Kernel.Regions.RecordAuthoritativeBackingReplacement(
            context.Owner, replacement).IsSuccess);

        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
                original with { ObservationSequence = replacement.EvidenceObservationSequence + 1 }).Error);

        ProviderHealthEvidenceV1 current = original with
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
        Context context = Create();
        ProviderHealthEvidenceV1 original = Evidence(new(8, 8));
        RegionDamageDescriptorV1 damage = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle,
            context.Owner, original).Value!;
        FailureDomainScopeV1 nextScope = damage.FailureDomain with
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
        Context context = Create();
        ProviderHealthEvidenceV1 firstEvidence = Evidence(new(8, 8));
        RegionDamageDescriptorV1 first = context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
            firstEvidence).Value!;
        ProviderHealthEvidenceV1 newerEvidence = Evidence(new(24, 8)) with { ObservationSequence = 3 };
        Assert.True(context.Kernel.Regions.QuarantineSubrange(context.Buffer.Handle, context.Owner,
            newerEvidence).IsSuccess);

        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.Regions.RecordAuthoritativeBackingReplacement(
                context.Owner, Replacement(first)).Error);
        RegionBackingReplacementEvidenceV1 freshReplacement = Replacement(first) with { EvidenceObservationSequence = 4 };
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
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle process = TestFixtures.Create(kernel, 8610, 8620).Handle;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(process, 64).Value!;
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
