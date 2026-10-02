using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase04CrossOwnerAdmissionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BlockedResourceClockDoesNotHoldOwnerGateAndRevokeRefusesLease(bool acquire)
    {
        var clock = new CallbackClock();
        var c = Create(clock);
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        clock.OnRead = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var admission = Task.Run(() => acquire
            ? c.Kernel.CapabilityAuthority.AcquireResourceUseAuthority(c.ResourceGrant, new(1901), 1, 1, Envelope(10)).Error
            : c.Kernel.CapabilityAuthority.ValidateResourceUse(c.ResourceGrant, new(1901), 1, 1, Envelope(10)).Error);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True((await Task.Run(() => c.Kernel.CapabilityAuthority.Revoke(c.ResourceGrant))
                .WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.CapabilityRevoked, await admission.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, c.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(100UL, c.Kernel.CapabilityAuthority.InspectConstraints(c.ResourceGrant)!.Value.SharedRemaining);
        Assert.Equal(0UL, Used(c));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ResourceClockFaultCannotAllocateLeaseOrInvokeConsumer(bool acquire)
    {
        var clock = new CallbackClock();
        var c = Create(clock);
        var called = false;
        clock.OnRead = () => throw new InvalidOperationException("injected resource clock");
        var error = acquire
            ? c.Kernel.CapabilityAuthority.AcquireResourceUseAuthority(c.ResourceGrant, new(1901), 1, 1, Envelope(10),
                () => { called = true; return KernelResult.Ok(); }).Error
            : c.Kernel.CapabilityAuthority.ValidateResourceUse(c.ResourceGrant, new(1901), 1, 1, Envelope(10)).Error;
        Assert.Equal(KernelError.PlatformFaulted, error);
        Assert.False(called);
        Assert.Equal(0, c.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(100UL, c.Kernel.CapabilityAuthority.InspectConstraints(c.ResourceGrant)!.Value.SharedRemaining);
        using var recovered = c.Kernel.CapabilityAuthority.AcquireResourceUseAuthority(c.ResourceGrant,
            new(1901), 1, 1, Envelope(10)).Value!;
        Assert.NotNull(recovered);
        Assert.Equal(0UL, Used(c));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ActualResourceConsumerClockFaultOrChangedPreparedStateCompensatesOnlyProvisionalCharge(bool changeOperation)
    {
        var clock = new CallbackClock();
        var c = Create(clock);
        c.Kernel.ResourceAdmissionQualificationHook = new Hook(point =>
        {
            if (point == ResourceAdmissionQualificationPoint.AfterFinalRevalidation)
                clock.OnRead = () =>
                {
                    if (!changeOperation) throw new InvalidOperationException("injected after budget preparation");
                    Assert.True(c.Kernel.ExternalOperations.Admit(c.Operation, c.Dependencies).IsSuccess);
                };
        });
        var refused = Prepare(c);
        Assert.Equal(changeOperation ? KernelError.StaleGeneration : KernelError.PlatformFaulted, refused.Error);
        Assert.Equal(0, c.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0UL, Used(c));
        Assert.Equal(changeOperation ? ExternalOperationState.Admitted : ExternalOperationState.Prepared,
            c.Kernel.ExternalOperations.Query(c.Operation).Value!.State);
        Assert.Equal(100UL, c.Kernel.CapabilityAuthority.InspectConstraints(c.ResourceGrant)!.Value.SharedRemaining);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClockCrossCapabilityRevocationCannotPublishOrDispatchAdmission(bool afterSubmit)
    {
        var clock = new CallbackClock();
        var c = Create(clock);
        ResourceAdmissionCommit? commit = afterSubmit ? Prepare(c).Value! : null;
        c.Kernel.ResourceAdmissionQualificationHook = new Hook(point =>
        {
            if (point == (afterSubmit ? ResourceAdmissionQualificationPoint.BeforeProviderCallback
                : ResourceAdmissionQualificationPoint.AfterFinalRevalidation))
                clock.OnRead = () => clock.OnRead = () => Assert.True(c.Kernel.CapabilityAuthority
                    .Revoke(afterSubmit ? c.EffectCapability : c.ResourceGrant).IsSuccess);
        });
        var callbacks = 0;
        var error = afterSubmit
            ? c.Kernel.SubmitResourceExternalAdmission(commit!, c.Dependencies,
                () => { callbacks++; return KernelResult.Ok(); }).Error
            : Prepare(c).Error;
        Assert.Equal(KernelError.CapabilityRevoked, error);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, c.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(afterSubmit ? 10UL : 0UL, Used(c));
        var owner = c.Kernel.ExternalOperations.Query(c.Operation).Value!;
        Assert.Equal(afterSubmit ? ExternalOperationState.Submitted : ExternalOperationState.Prepared, owner.State);
        if (afterSubmit)
        {
            Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.QueryBudget(commit!.Lease).Value!.State);
            foreach (var use in owner.Admission!.RegionUses)
                Assert.Equal(KernelError.RegionUseConflict, c.Kernel.Regions.ReleaseUse(use.Handle, owner.Principal).Error);
            commit.Dispose();
            Assert.Equal(10UL, Used(c));
        }
    }

    [Fact]
    public void TerminalCapabilityEpochDoesNotInterruptSafeProcessTeardown()
    {
        var kernel = new RuntimeKernel();
        var process = TestFixtures.Create(kernel, 8300, 8301).Handle;
        var capability = kernel.MintCapability(new(8301), process, ResourceKind.Compute,
            "terminal-teardown", CapabilityRights.Execute).Value!.CapabilityId;
        SeedDomainEpoch(kernel.CapabilityAuthority, new(8301), ulong.MaxValue);
        var result = kernel.TerminateProcess(process);
        Assert.True(result.IsSuccess, result.Message);
        Assert.False(kernel.Processes.Resolve(process).IsSuccess);
        Assert.Equal(CapabilityRecordState.Revoked, Assert.Single(kernel.CapabilityAuthority.InspectionSnapshot(),
            record => record.Descriptor.CapabilityId == capability).State);
        Assert.Equal(KernelError.CapabilityRevoked,
            kernel.CapabilityAuthority.Validate(capability, new(8301), process.Generation, CapabilityRights.Execute).Error);
        Assert.Empty(kernel.CapabilityAuthority.SnapshotForDomain(new(8301)));
    }

    [Fact]
    public void TerminalCapabilityEpochCannotCloseSubmittedEffectDuringTeardownRetry()
    {
        var c = Create();
        using var commit = Prepare(c).Value!;
        Assert.Equal(KernelError.PlatformFaulted, c.Kernel.SubmitResourceExternalAdmission(commit, c.Dependencies,
            () => KernelResult.Fail(KernelError.PlatformFaulted, "effect closure unknown")).Error);
        SeedDomainEpoch(c.Kernel.CapabilityAuthority, new(1901), ulong.MaxValue);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.False(c.Kernel.TerminateProcess(c.Process).IsSuccess);
            Assert.True(c.Kernel.Processes.Resolve(c.Process).IsSuccess);
            var observed = c.Kernel.ObserveProcessTeardown(c.Process);
            Assert.True(observed.IsSuccess);
            Assert.False(observed.Value!.LocalReclaimCompleted);
            Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.QueryBudget(commit.Lease).Value!.State);
            Assert.Equal(10UL, Used(c));
            var owner = c.Kernel.ExternalOperations.Query(c.Operation).Value!;
            Assert.Equal(ExternalOperationState.Submitted, owner.State);
            foreach (var use in owner.Admission!.RegionUses)
                Assert.Equal(KernelError.RegionUseConflict,
                    c.Kernel.Regions.ReleaseUse(use.Handle, owner.Principal).Error);
        }
        commit.Dispose();
        Assert.Equal(10UL, Used(c));
    }

    [Fact]
    public void TerminalDomainEpochDeniesNewMintAndDelegationWithoutWrapping()
    {
        var authority = new CapabilityAuthority(new(Guid.NewGuid()), new(16, 8));
        var parent = authority.Mint(new(8200), new(8200), ResourceKind.Compute, "epoch-parent",
            CapabilityRights.Execute | CapabilityRights.Delegate, 1, 1, null, null, 1, 1).Value!.CapabilityId;
        SeedDomainEpoch(authority, new(8201), ulong.MaxValue - 1);
        var child = authority.Delegate(parent, new(8200), new(8201), CapabilityRights.Execute, 1).Value!.CapabilityId;
        authority.RevokeAllForDomain(new(8201));
        Assert.Equal(KernelError.CapabilityRevoked, authority.Validate(child, new(8201), 1, CapabilityRights.Execute).Error);
        Assert.Equal(KernelError.CapabilityRevoked, authority.Mint(new(8201), new(8201), ResourceKind.Compute,
            "terminal", CapabilityRights.Execute, 1, 1, null, null, 1, 0).Error);
        Assert.Equal(KernelError.CapabilityRevoked,
            authority.Delegate(parent, new(8200), new(8201), CapabilityRights.Execute, 2).Error);
        Assert.Equal(2, authority.InspectionSnapshot().Length);
        Assert.Empty(authority.SnapshotForDomain(new(8201)));
    }

    [Fact]
    public void RepeatedTerminalDomainRevocationDoesNotInterruptOwnerCleanup()
    {
        var authority = new CapabilityAuthority(new(Guid.NewGuid()), new(16, 8));
        SeedDomainEpoch(authority, new(8250), ulong.MaxValue - 1);
        var parent = authority.Mint(new(8250), new(8250), ResourceKind.Compute, "terminal-lineage",
            CapabilityRights.Execute | CapabilityRights.Delegate, 1, 1, null, null, 1, 1).Value!.CapabilityId;
        var child = authority.Delegate(parent, new(8250), new(8251), CapabilityRights.Execute, 1).Value!.CapabilityId;
        authority.RevokeAllForDomain(new(8250));
        authority.RevokeAllForDomain(new(8250));
        Assert.Equal(KernelError.CapabilityRevoked, authority.Validate(child, new(8251), 1, CapabilityRights.Execute).Error);
        Assert.Empty(authority.SnapshotForDomain(new(8251)));
        Assert.Equal(0, authority.ActiveOperationLeaseCount);
    }

    private static void SeedDomainEpoch(CapabilityAuthority authority, DomainId domain, ulong epoch)
        => ((Dictionary<DomainId, ulong>)typeof(CapabilityAuthority).GetField("_domainEpochs",
            global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!
            .GetValue(authority)!)[domain] = epoch;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DelegationDeriveCannotLaunderTargetEpochAndFreshRetryRemainsValid(int boundary)
    {
        var authority = new CapabilityAuthority(new(Guid.NewGuid()), new(16, 8));
        var parent = authority.Mint(new(8100), new(8100), ResourceKind.Compute, "derive-epoch",
            CapabilityRights.Execute | CapabilityRights.Delegate, 1, 1, null, null, 7, 1).Value!.CapabilityId;
        var before = authority.InspectConstraints(parent)!.Value;
        var result = authority.Delegate(parent, new(8100), new(8101), CapabilityRights.Execute, 1, child =>
        {
            if (boundary == 1) authority.RevokeAllForDomain(new(8101));
            if (boundary == 2) authority.RevokeAllForDomain(new(8102));
            return child;
        });
        Assert.Equal(boundary != 1, result.IsSuccess);
        if (boundary == 1)
        {
            Assert.Equal(KernelError.CapabilityRevoked, result.Error);
            Assert.Single(authority.InspectionSnapshot());
            Assert.Empty(authority.SnapshotForDomain(new(8101)));
        }
        else Assert.True(authority.Validate(result.Value!.CapabilityId, new(8101), 1,
            CapabilityRights.Execute, 1).IsSuccess);
        var after = authority.InspectConstraints(parent)!.Value;
        Assert.Equal(before.SharedRemaining, after.SharedRemaining);
        Assert.Equal(before.HandleConsumed, after.HandleConsumed);
        Assert.Equal(0, authority.ActiveOperationLeaseCount);
        var retry = authority.Delegate(parent, new(8100), new(8101), CapabilityRights.Execute, 1);
        Assert.True(retry.IsSuccess);
        Assert.True(authority.Validate(retry.Value!.CapabilityId, new(8101), 1,
            CapabilityRights.Execute, 1).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MintOperationsCallbackCannotReenterIssuanceOrLaunderDomainEpoch(bool revoke)
    {
        var authority = new CapabilityAuthority(new(Guid.NewGuid()), new(16, 8));
        IEnumerable<CapabilityOperation> Operations()
        {
            if (revoke) authority.RevokeAllForDomain(new(8000));
            else Assert.Equal(KernelError.CapacityExhausted,
                authority.Mint(new(8000), new(8000), ResourceKind.Compute, "nested-operations",
                    CapabilityRights.Execute, 1, 1, null, null, 1, 0).Error);
            yield return CapabilityOperation.Execute;
        }
        var result = authority.Mint(new(8000), new(8000), ResourceKind.Compute, "operations",
            CapabilityRights.Execute, 1, 1, null, null, 1, 0, operations: Operations());
        Assert.Equal(!revoke, result.IsSuccess);
        if (revoke) Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(revoke ? 0 : 1, authority.InspectionSnapshot().Length);
        Assert.True(authority.Mint(new(8000), new(8000), ResourceKind.Compute, "retry-operations",
            CapabilityRights.Execute, 1, 1, null, null, 1, 0).IsSuccess);
    }

    [Fact]
    public void MintOperationsExceptionClearsIssuanceInterlockWithoutPublication()
    {
        var authority = new CapabilityAuthority(new(Guid.NewGuid()), new(16, 8));
        IEnumerable<CapabilityOperation> Operations()
        {
            yield return CapabilityOperation.Execute;
            throw new InvalidOperationException("enumeration failed");
        }
        Assert.Throws<InvalidOperationException>(() => authority.Mint(new(8050), new(8050),
            ResourceKind.Compute, "operations", CapabilityRights.Execute, 1, 1, null, null, 1, 0,
            operations: Operations()));
        Assert.Empty(authority.InspectionSnapshot());
        Assert.True(authority.Mint(new(8050), new(8050), ResourceKind.Compute, "retry",
            CapabilityRights.Execute, 1, 1, null, null, 1, 0).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonceCallbackMintFailureDoesNotPublishAndInterlockAllowsFreshRetry(bool throws)
    {
        Action? callback = null;
        var authority = new CapabilityAuthority(new(Guid.NewGuid()), new(16, 8), nonceFactory: () =>
        {
            var action = callback;
            callback = null;
            action?.Invoke();
            return Guid.NewGuid();
        });
        callback = () =>
        {
            if (throws) throw new InvalidOperationException("nonce unavailable");
            authority.RevokeAllForDomain(new(7950));
        };
        KernelResult<CapabilityDescriptorV1> Mint() => authority.Mint(new(7950), new(7950),
            ResourceKind.Compute, "mint-retry", CapabilityRights.Execute, 1, 1, null, null, 1, 0);
        if (throws) Assert.Throws<InvalidOperationException>(() => Mint());
        else Assert.Equal(KernelError.CapabilityRevoked, Mint().Error);
        Assert.Empty(authority.InspectionSnapshot());
        Assert.True(Mint().IsSuccess);
        Assert.Single(authority.InspectionSnapshot());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NonceCallbackRevocationCannotPublishDelegation(int fault)
    {
        Action? callback = null;
        var authority = new CapabilityAuthority(new(Guid.NewGuid()), new(16, 8), nonceFactory: () =>
        {
            var action = callback;
            callback = null;
            action?.Invoke();
            return Guid.NewGuid();
        });
        var parent = authority.Mint(new(7800), new(7800), ResourceKind.Compute, "nonce",
            CapabilityRights.Execute | CapabilityRights.Delegate, 1, 1, null, null, 1, 1).Value!.CapabilityId;
        callback = () =>
        {
            if (fault == 0) Assert.True(authority.Revoke(parent).IsSuccess);
            else authority.RevokeAllForDomain(new(7801));
        };
        var result = authority.Delegate(parent, new(7800), new(7801), CapabilityRights.Execute, 1);
        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Single(authority.InspectionSnapshot());
    }

    [Fact]
    public void NonceCallbackCannotRecursivelyIssueOverlappingIdentityOrQuotaAccount()
    {
        Action? callback = null;
        var authority = new CapabilityAuthority(new(Guid.NewGuid()), new(16, 8), nonceFactory: () =>
        {
            var action = callback;
            callback = null;
            action?.Invoke();
            return Guid.NewGuid();
        });
        callback = () => Assert.Equal(KernelError.CapacityExhausted,
            authority.Mint(new(7900), new(7900), ResourceKind.Compute, "nested",
                CapabilityRights.Execute, 1, 1, null, null, 1, 0).Error);
        Assert.True(authority.Mint(new(7900), new(7900), ResourceKind.Compute, "outer",
            CapabilityRights.Execute, 1, 1, null, null, 1, 0).IsSuccess);
        Assert.Single(authority.InspectionSnapshot());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void DelegationCallbackCannotPublishFromRevokedSourceOrOverCapacity(int fault)
    {
        var authority = new CapabilityAuthority(new(Guid.NewGuid()), new(3, 1));
        var parent = authority.Mint(new(7700), new(7700), ResourceKind.Compute, "derive",
            CapabilityRights.Execute | CapabilityRights.Delegate, 1, 1, null, null, 1, 1).Value!.CapabilityId;
        var result = authority.Delegate(parent, new(7700), new(7701), CapabilityRights.Execute, 1, child =>
        {
            if (fault == 0) Assert.True(authority.Revoke(parent).IsSuccess);
            else if (fault == 1) authority.RevokeAllForDomain(new(7700));
            else Assert.True(authority.Mint(new(7701), new(7701), ResourceKind.Compute, "occupant",
                CapabilityRights.Execute, 1, 1, null, null, 1, 0).IsSuccess);
            return child;
        });
        Assert.Equal(fault == 2 ? KernelError.CapacityExhausted : KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(fault == 2 ? 2 : 1, authority.InspectionSnapshot().Length);
        Assert.Equal(0, authority.ActiveOperationLeaseCount);
    }

    [Fact]
    public void ClockExceptionAfterSubmitCannotRefundOrReleasePinnedUses()
    {
        var clock = new CallbackClock();
        var c = Create(clock);
        using var commit = Prepare(c).Value!;
        c.Kernel.ResourceAdmissionQualificationHook = new Hook(point =>
        {
            if (point == ResourceAdmissionQualificationPoint.BeforeProviderCallback)
                clock.OnRead = () => throw new InvalidOperationException("clock unavailable after submit");
        });
        var callbacks = 0;
        var result = c.Kernel.SubmitResourceExternalAdmission(commit, c.Dependencies,
            () => { callbacks++; return KernelResult.Ok(); });
        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.QueryBudget(commit.Lease).Value!.State);
        Assert.Equal(10UL, Used(c));
        var owner = c.Kernel.QueryExternalOperation(c.Process, c.Operation).Value!;
        Assert.Equal(ExternalOperationState.Submitted, owner.State);
        Assert.Equal(ExternalOperationDisposition.ProviderLost, owner.Disposition);
        foreach (var use in owner.Admission!.RegionUses)
            Assert.Equal(KernelError.RegionUseConflict, c.Kernel.Regions.ReleaseUse(use.Handle, owner.Principal).Error);
        Assert.False(c.Kernel.ReleaseBudget(c.Process, commit.Lease).IsSuccess);
        commit.Dispose();
        Assert.Equal(10UL, Used(c));
    }

    [Fact]
    public void ResourceClockExceptionBeforeAdmissionCancelsOnlyReversibleReservation()
    {
        var clock = new CallbackClock();
        var c = Create(clock);
        c.Kernel.ResourceAdmissionQualificationHook = new Hook(point =>
        {
            if (point == ResourceAdmissionQualificationPoint.AfterFinalRevalidation)
                clock.OnRead = () => throw new InvalidOperationException("clock unavailable before admission");
        });
        var result = Prepare(c);
        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(0UL, Used(c));
        Assert.Equal(0, c.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(ExternalOperationState.Prepared, c.Kernel.ExternalOperations.Query(c.Operation).Value!.State);
        c.Kernel.ResourceAdmissionQualificationHook = null;
        using var retry = Prepare(c).Value!;
        Assert.NotNull(retry);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void ClockReadAncestorRevocationDeniesChildWithoutMutation(bool resource, bool acquire, bool domain)
    {
        var clock = new CallbackClock();
        var authority = new RuntimeKernel(null, clock).CapabilityAuthority;
        var parent = authority.Mint(new(7600), new(7600), ResourceKind.Compute, "clock-lineage",
            CapabilityRights.Execute | CapabilityRights.Delegate, 1, 1, null, null, 1, 1,
            resourceUse: new(1, Envelope(100), 0, long.MaxValue,
                ResourceAssuranceV1.RuntimeEnforced, 3)).Value!.CapabilityId;
        var child = authority.Delegate(parent, new(7600), new(7601),
            CapabilityRights.Execute, 1).Value!.CapabilityId;
        var before = authority.InspectConstraints(child)!.Value;
        clock.OnRead = () =>
        {
            if (domain) authority.RevokeAllForDomain(new(7600));
            else Assert.True(authority.Revoke(parent).IsSuccess);
        };
        var error = resource
            ? acquire
                ? authority.AcquireResourceUseAuthority(child, new(7601), 1, 1, Envelope(10)).Error
                : authority.ValidateResourceUse(child, new(7601), 1, 1, Envelope(10)).Error
            : acquire
                ? authority.AcquireOperationAuthority(child, new(7601), 1, ResourceKind.Compute,
                    "clock-lineage", 1, CapabilityOperation.Execute, quotaAmount: 1).Error
                : authority.ValidateOperationAdmission(child, new(7601), 1, ResourceKind.Compute,
                    "clock-lineage", 1, CapabilityOperation.Execute).Error;
        Assert.Equal(KernelError.CapabilityRevoked, error);
        Assert.Equal(0, authority.ActiveOperationLeaseCount);
        var after = authority.InspectConstraints(child)!.Value;
        Assert.Equal(before.SharedRemaining, after.SharedRemaining);
        Assert.Equal(before.HandleConsumed, after.HandleConsumed);
        Assert.Empty(authority.SnapshotForDomain(new(7601)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourceClockReadRevocationCannotReturnGrantOrAllocateLease(bool acquire)
    {
        var clock = new CallbackClock();
        var authority = new RuntimeKernel(null, clock).CapabilityAuthority;
        var id = authority.Mint(new(7500), new(7500), ResourceKind.Compute, "clock-grant",
            CapabilityRights.Delegate, 1, 1, null, null, 1, 0,
            resourceUse: new(1, Envelope(100), 0, long.MaxValue,
                ResourceAssuranceV1.RuntimeEnforced, 3)).Value!.CapabilityId;
        var before = authority.InspectConstraints(id)!.Value;
        clock.OnRead = () => Assert.True(authority.Revoke(id).IsSuccess);
        var error = acquire
            ? authority.AcquireResourceUseAuthority(id, new(7500), 1, 1, Envelope(10)).Error
            : authority.ValidateResourceUse(id, new(7500), 1, 1, Envelope(10)).Error;
        Assert.Equal(KernelError.CapabilityRevoked, error);
        Assert.Equal(0, authority.ActiveOperationLeaseCount);
        var after = authority.InspectConstraints(id)!.Value;
        Assert.Equal(before.SharedRemaining, after.SharedRemaining);
        Assert.Equal(before.HandleConsumed, after.HandleConsumed);
    }

    [Fact]
    public void ResourceClockRevokeDuringLeaseAcquisitionCannotPublishAdmission()
    {
        var clock = new CallbackClock();
        var c = Create(clock);
        c.Kernel.ResourceAdmissionQualificationHook = new Hook(point =>
        {
            if (point == ResourceAdmissionQualificationPoint.AfterFinalRevalidation)
                clock.OnRead = () => Assert.True(c.Kernel.CapabilityAuthority.Revoke(c.ResourceGrant).IsSuccess);
        });
        var result = Prepare(c);
        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(0UL, Used(c));
        Assert.Equal(0, c.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(ExternalOperationState.Prepared, c.Kernel.ExternalOperations.Query(c.Operation).Value!.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OperationQueryBoundaryDoesNotReserveOrResurrectAuthority(int boundary)
    {
        var authority = new RuntimeKernel(null, new MutableClock(DateTimeOffset.UnixEpoch)).CapabilityAuthority;
        var id = authority.Mint(new(7400), new(7400), ResourceKind.Compute, "query-boundary",
            CapabilityRights.Execute, 1, 1, null, null, 1, 0).Value!.CapabilityId;
        var operation = boundary == 0 ? (CapabilityOperation)ushort.MaxValue : CapabilityOperation.Execute;
        if (boundary == 1)
        {
            using var consumed = authority.AcquireOperationAuthority(id, new(7400), 1, ResourceKind.Compute,
                "query-boundary", 1, operation, oneShot: true).Value!;
        }
        if (boundary == 2)
            typeof(CapabilityAuthority).GetField("_nextOperationLeaseId",
                global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!
                .SetValue(authority, 0UL);
        var before = authority.InspectConstraints(id)!.Value;
        var query = authority.ValidateOperationAdmission(id, new(7400), 1, ResourceKind.Compute,
            "query-boundary", 1, operation);
        var acquire = authority.AcquireOperationAuthority(id, new(7400), 1, ResourceKind.Compute,
            "query-boundary", 1, operation, quotaAmount: 1);
        Assert.Equal(boundary == 2, query.IsSuccess);
        Assert.Equal(boundary switch { 0 => KernelError.InsufficientRights, 1 => KernelError.CapabilityRevoked,
            _ => KernelError.CapacityExhausted }, acquire.Error);
        Assert.Equal(0, authority.ActiveOperationLeaseCount);
        var after = authority.InspectConstraints(id)!.Value;
        Assert.Equal(before.SharedRemaining, after.SharedRemaining);
        Assert.Equal(before.HandleConsumed, after.HandleConsumed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClockReadRevocationCannotReturnOperationPermissionOrMutateQuota(bool acquire)
    {
        var clock = new CallbackClock();
        var authority = new RuntimeKernel(null, clock).CapabilityAuthority;
        var id = authority.Mint(new(7300), new(7300), ResourceKind.Compute, "clock-reentrant",
            CapabilityRights.Execute, 1, 1, null, null, 1, 0).Value!.CapabilityId;
        var before = authority.InspectConstraints(id)!.Value;
        clock.OnRead = () => Assert.True(authority.Revoke(id).IsSuccess);
        var result = acquire
            ? authority.AcquireOperationAuthority(id, new(7300), 1, ResourceKind.Compute,
                "clock-reentrant", 1, CapabilityOperation.Execute, quotaAmount: 1).Error
            : authority.ValidateOperationAdmission(id, new(7300), 1, ResourceKind.Compute,
                "clock-reentrant", 1, CapabilityOperation.Execute).Error;
        Assert.Equal(KernelError.CapabilityRevoked, result);
        Assert.Equal(0, authority.ActiveOperationLeaseCount);
        var after = authority.InspectConstraints(id)!.Value;
        Assert.Equal(before.SharedRemaining, after.SharedRemaining);
        Assert.Equal(before.HandleConsumed, after.HandleConsumed);
    }

    private sealed class CallbackClock : TimeProvider
    {
        internal Action? OnRead { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            var callback = OnRead;
            OnRead = null;
            callback?.Invoke();
            return DateTimeOffset.UnixEpoch;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void OperationAdmissionQueryMatchesAcquireWithoutQuotaOrLeaseMutation(int fault)
    {
        var origin = DateTimeOffset.UnixEpoch;
        var clock = new MutableClock(origin);
        var authority = new RuntimeKernel(null, clock).CapabilityAuthority;
        var session = new EndpointSessionHandle(new(17), new(1));
        var id = authority.Mint(new(7200), new(7200), ResourceKind.Compute, "query-effect",
            CapabilityRights.Execute, 1, 1, null, session, 1, 0,
            expiresUtcTicks: origin.UtcTicks + 10).Value!.CapabilityId;
        if (fault == 9) clock.Now = origin.AddTicks(10);
        if (fault == 10) Assert.True(authority.Revoke(id).IsSuccess);
        var exactId = fault == 11 ? new CapabilityId(id.Value + 1000) : id;
        var subject = new DomainId(fault == 1 ? 7201UL : 7200UL);
        var subjectGeneration = fault == 2 ? 2UL : 1UL;
        var kind = fault == 3 ? ResourceKind.KernelService : ResourceKind.Compute;
        var resource = fault == 4 ? "other-effect" : "query-effect";
        var generation = fault == 5 ? 2UL : 1UL;
        var operation = fault == 6 ? CapabilityOperation.Read : CapabilityOperation.Execute;
        EndpointSessionHandle? suppliedSession = fault == 7 ? null
            : fault == 8 ? session with { Generation = new(2) } : session;
        for (var repeat = 0; repeat < 64; repeat++)
        {
            var query = authority.ValidateOperationAdmission(exactId, subject, subjectGeneration,
                kind, resource, generation, operation, suppliedSession);
            Assert.Equal(fault == 0, query.IsSuccess);
            Assert.Equal(0, authority.ActiveOperationLeaseCount);
        }
        var decision = authority.ValidateOperationAdmission(exactId, subject, subjectGeneration,
            kind, resource, generation, operation, suppliedSession);
        var acquired = authority.AcquireOperationAuthority(exactId, subject, subjectGeneration,
            kind, resource, generation, operation, suppliedSession, quotaAmount: 1);
        Assert.Equal(decision.Error, acquired.Error);
        if (fault == 0)
        {
            Assert.Equal(1UL, acquired.Value!.Id.Value);
            acquired.Value.Dispose();
            Assert.Equal(KernelError.BudgetExceeded, authority.AcquireOperationAuthority(id, new(7200), 1,
                ResourceKind.Compute, "query-effect", 1, CapabilityOperation.Execute, session, quotaAmount: 1).Error);
        }
        Assert.Equal(0, authority.ActiveOperationLeaseCount);
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(10, false)]
    [InlineData(11, false)]
    public void EffectLifetimeIsFreshAtResourceCallbackBoundary(int offset, bool allowed)
    {
        var origin = DateTimeOffset.UnixEpoch;
        var clock = new MutableClock(origin);
        var c = Create(clock, origin.UtcTicks + 10);
        using var commit = Prepare(c).Value!;
        c.Kernel.ResourceAdmissionQualificationHook = new Hook(point =>
        {
            if (point == ResourceAdmissionQualificationPoint.BeforeProviderCallback)
                clock.Now = origin.AddTicks(offset);
        });
        var callbacks = 0;
        var submitted = c.Kernel.SubmitResourceExternalAdmission(commit, c.Dependencies,
            () => { callbacks++; return KernelResult.Ok(); });
        Assert.Equal(allowed, submitted.IsSuccess);
        Assert.Equal(allowed ? 1 : 0, callbacks);
        if (!allowed) Assert.Equal(KernelError.DeadlineExpired, submitted.Error);
        Assert.Equal(allowed ? BudgetReservationState.Consuming : BudgetReservationState.Quarantined,
            c.Kernel.QueryBudget(commit.Lease).Value!.State);
        Assert.Equal(10UL, Used(c));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(9, true)]
    [InlineData(10, false)]
    public void KernelCapabilityAdmissionUsesConfiguredOwnerClock(int offset, bool allowed)
    {
        var origin = DateTimeOffset.UnixEpoch;
        var kernel = new RuntimeKernel(null, new MutableClock(origin.AddTicks(offset)));
        var capability = kernel.CapabilityAuthority.Mint(new(7100), new(7100), ResourceKind.Compute,
            "clock-bound", CapabilityRights.Execute, 1, 1, null, null, 10, 0,
            notBeforeUtcTicks: origin.UtcTicks, expiresUtcTicks: origin.UtcTicks + 10).Value!.CapabilityId;
        var admitted = kernel.CapabilityAuthority.AcquireOperationAuthority(capability, new(7100), 1,
            ResourceKind.Compute, "clock-bound", 1, CapabilityOperation.Execute);
        Assert.Equal(allowed, admitted.IsSuccess);
        if (!allowed) Assert.Equal(KernelError.DeadlineExpired, admitted.Error);
        admitted.Value?.Dispose();
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RevocationAtDispatchBoundaryCannotUsePinnedAuthorityForNewCallback(bool effect, bool kernelConsumer)
    {
        var c = Create();
        using var commit = Prepare(c).Value!;
        c.Kernel.ResourceAdmissionQualificationHook = new Hook(point =>
        {
            if (point == ResourceAdmissionQualificationPoint.BeforeProviderCallback)
                Assert.True((kernelConsumer
                    ? c.Kernel.RevokeCapability(effect ? c.EffectCapability : c.ResourceGrant)
                    : c.Kernel.CapabilityAuthority.Revoke(effect ? c.EffectCapability : c.ResourceGrant)).IsSuccess);
        });
        var callbacks = 0;
        var result = c.Kernel.SubmitResourceExternalAdmission(commit, c.Dependencies,
            () => { callbacks++; return KernelResult.Ok(); });
        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.QueryBudget(commit.Lease).Value!.State);
        Assert.Equal(10UL, Used(c));
        var owner = c.Kernel.QueryExternalOperation(c.Process, c.Operation).Value!;
        Assert.Equal(ExternalOperationState.Submitted, owner.State);
        Assert.Equal(ExternalOperationDisposition.ProviderLost, owner.Disposition);
        foreach (var use in owner.Admission!.RegionUses)
            Assert.Equal(KernelError.RegionUseConflict, c.Kernel.Regions.ReleaseUse(use.Handle, owner.Principal).Error);
        Assert.False(c.Kernel.ReleaseBudget(c.Process, commit.Lease).IsSuccess);
        Assert.True(c.Kernel.RevokeCapability(effect ? c.EffectCapability : c.ResourceGrant).IsSuccess);
        Assert.False(c.Kernel.SubmitResourceExternalAdmission(commit, c.Dependencies,
            () => { callbacks++; return KernelResult.Ok(); }).IsSuccess);
        commit.Dispose();
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.Quarantined, c.Kernel.QueryBudget(commit.Lease).Value!.State);
        Assert.Equal(10UL, Used(c));
    }

    [Fact]
    public void RevokeBetweenReserveAndCommitCompensatesLeaseAndNeverAdmitsOperation()
    {
        var context = Create();
        context.Kernel.ResourceAdmissionQualificationHook = new Hook((point) =>
        {
            if (point == ResourceAdmissionQualificationPoint.AfterBudgetReservation)
                Assert.True(context.Kernel.CapabilityAuthority.Revoke(context.ResourceGrant).IsSuccess);
        });

        var result = Prepare(context);

        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(0UL, Used(context));
        Assert.Equal(ExternalOperationState.Prepared,
            context.Kernel.ExternalOperations.Query(context.Operation).Value!.State);
    }

    [Theory]
    [InlineData((int)ResourceAdmissionQualificationPoint.AfterInitialValidation)]
    [InlineData((int)ResourceAdmissionQualificationPoint.AfterBudgetReservation)]
    [InlineData((int)ResourceAdmissionQualificationPoint.AfterFinalRevalidation)]
    [InlineData((int)ResourceAdmissionQualificationPoint.AfterLocalCommit)]
    public void FaultAtEveryPreSubmitBoundaryCompensatesReversibleState(int faultValue)
    {
        var fault = (ResourceAdmissionQualificationPoint)faultValue;
        var context = Create();
        context.Kernel.ResourceAdmissionQualificationHook = new Hook(point =>
        {
            if (point == fault) throw new InvalidOperationException($"fault:{point}");
        });

        Assert.Equal(KernelError.PlatformFaulted, Prepare(context).Error);
        Assert.Equal(0UL, Used(context));
        Assert.NotEqual(ExternalOperationState.Submitted,
            context.Kernel.ExternalOperations.Query(context.Operation).Value!.State);
    }

    [Fact]
    public void RegionMutationBetweenReserveAndCommitFailsAndCompensates()
    {
        var context = Create();
        context.Kernel.ResourceAdmissionQualificationHook = new Hook(point =>
        {
            if (point == ResourceAdmissionQualificationPoint.AfterBudgetReservation)
                Assert.True(context.Kernel.Regions.Release(context.Region, new(new(1901), context.Process.Generation)).IsSuccess);
        });

        var result = Prepare(context);

        Assert.False(result.IsSuccess);
        Assert.Equal(0UL, Used(context));
    }

    [Fact]
    public void RevokeAtResourceAuthorityLinearizationLosesWithoutLocalCommit()
    {
        var context = Create();
        context.Kernel.ResourceAdmissionQualificationHook = new Hook(point =>
        {
            if (point == ResourceAdmissionQualificationPoint.AfterFinalRevalidation)
                Assert.True(context.Kernel.CapabilityAuthority.Revoke(context.ResourceGrant).IsSuccess);
        });

        var result = Prepare(context);

        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(0UL, Used(context));
        Assert.Equal(ExternalOperationState.Prepared,
            context.Kernel.ExternalOperations.Query(context.Operation).Value!.State);
    }

    [Fact]
    public void EffectAndResourceGenerationsAreValidatedIndependently()
    {
        var context = Create();

        var staleResource = context.Kernel.PrepareResourceExternalAdmission(
            context.Process, context.EffectCapability, ResourceKind.Compute, "compute:effect", 1,
            context.ResourceGrant, 2, Envelope(10), context.Operation, context.Dependencies);

        Assert.Equal(KernelError.StaleGeneration, staleResource.Error);
        Assert.Equal(0UL, Used(context));
        Assert.Equal(ExternalOperationState.Prepared,
            context.Kernel.ExternalOperations.Query(context.Operation).Value!.State);
    }

    [Fact]
    public void ProviderCallbackRunsWithoutOwnerLocksAndDuplicateSubmitIsDenied()
    {
        var context = Create();
        using var commit = Prepare(context).Value!;
        var callbacks = 0;

        var submitted = context.Kernel.SubmitResourceExternalAdmission(commit, context.Dependencies, () =>
        {
            Interlocked.Increment(ref callbacks);
            Assert.True(context.Kernel.QueryBudget(context.ProcessBudget).IsSuccess);
            Assert.True(context.Kernel.ExternalOperations.Query(context.Operation).IsSuccess);
            return KernelResult.Ok();
        });

        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.Equal(KernelError.InvalidTransition,
            context.Kernel.SubmitResourceExternalAdmission(commit, context.Dependencies, () => KernelResult.Ok()).Error);
        Assert.Equal(1, callbacks);
        Assert.Equal(BudgetReservationState.Consuming,
            context.Kernel.Budgets.Query(commit.Lease).Value!.State);
    }

    [Fact]
    public void RevokeAfterLocalCommitDoesNotRetroactivelyUndoAdmissionWinner()
    {
        var context = Create();
        using var commit = Prepare(context).Value!;
        Assert.True(context.Kernel.CapabilityAuthority.Revoke(context.ResourceGrant).IsSuccess);
        Assert.Equal(ExternalOperationState.Admitted,
            context.Kernel.ExternalOperations.Query(context.Operation).Value!.State);
        Assert.Equal(BudgetReservationState.Bound, context.Kernel.Budgets.Query(commit.Lease).Value!.State);
        var callbacks = 0;

        var result = context.Kernel.SubmitResourceExternalAdmission(commit, context.Dependencies, () =>
        {
            callbacks++;
            return KernelResult.Ok();
        });

        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(10UL, Used(context));
        Assert.Equal(BudgetReservationState.Quarantined,
            context.Kernel.Budgets.Query(commit.Lease).Value!.State);
    }

    [Fact]
    public void AbandonedLocalCommitIsCompensatedBeforeSubmit()
    {
        var context = Create();
        var commit = Prepare(context).Value!;

        commit.Dispose();

        Assert.Equal(0UL, Used(context));
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            context.Kernel.Budgets.Query(commit.Lease).Value!.State);
        Assert.NotEqual(ExternalOperationState.Submitted,
            context.Kernel.ExternalOperations.Query(context.Operation).Value!.State);
    }

    [Fact]
    public void ProviderFailureAfterPossibleSubmitQuarantinesWithoutRefund()
    {
        var context = Create();
        using var commit = Prepare(context).Value!;

        var result = context.Kernel.SubmitResourceExternalAdmission(commit, context.Dependencies,
            () => KernelResult.Fail(KernelError.PlatformFaulted, "provider disconnected"));

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(BudgetReservationState.Quarantined,
            context.Kernel.Budgets.Query(commit.Lease).Value!.State);
        Assert.Equal(10UL, Used(context));
    }

    [Theory]
    [InlineData((int)ResourceAdmissionQualificationPoint.BeforeProviderCallback, 0)]
    [InlineData((int)ResourceAdmissionQualificationPoint.AfterProviderCallback, 1)]
    public void FaultAtEveryPostPossibleSubmitBoundaryQuarantinesWithoutRefund(
        int faultValue, int expectedCallbacks)
    {
        var fault = (ResourceAdmissionQualificationPoint)faultValue;
        var context = Create();
        using var commit = Prepare(context).Value!;
        var callbacks = 0;
        context.Kernel.ResourceAdmissionQualificationHook = new Hook(point =>
        {
            if (point == fault) throw new InvalidOperationException($"fault:{point}");
        });

        var result = context.Kernel.SubmitResourceExternalAdmission(commit, context.Dependencies, () =>
        {
            callbacks++;
            return KernelResult.Ok();
        });

        Assert.Equal(KernelError.PlatformFaulted, result.Error);
        Assert.Equal(expectedCallbacks, callbacks);
        Assert.Equal(BudgetReservationState.Quarantined,
            context.Kernel.Budgets.Query(commit.Lease).Value!.State);
        Assert.Equal(10UL, Used(context));
    }

    [Fact]
    public async Task ConcurrentAdmissionsCompleteWithoutCrossOwnerDeadlock()
    {
        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            var context = Create();
            using var commit = Prepare(context).Value!;
            return context.Kernel.SubmitResourceExternalAdmission(commit, context.Dependencies, KernelResult.Ok);
        }));

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, result => Assert.True(result.IsSuccess, result.Message));
    }

    private static KernelResult<ResourceAdmissionCommit> Prepare(Context context) =>
        context.Kernel.PrepareResourceExternalAdmission(
            context.Process, context.EffectCapability, ResourceKind.Compute, "compute:effect", 1,
            context.ResourceGrant, 1, Envelope(10), context.Operation, context.Dependencies);

    private static Context Create(TimeProvider? clock = null, long effectExpiresUtcTicks = long.MaxValue)
    {
        var kernel = new RuntimeKernel(null, clock);
        var admin = TestFixtures.Create(kernel, 900, 1900).Handle;
        var target = TestFixtures.Create(kernel, 901, 1901).Handle;
        var administration = kernel.MintCapability(new(1900), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, target, "p04",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100), new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!;
        var effect = kernel.CapabilityAuthority.Mint(new(1901), new(1901), ResourceKind.Compute,
            "compute:effect", CapabilityRights.Execute, target.Generation, 1, null, null, ulong.MaxValue, 16,
            expiresUtcTicks: effectExpiresUtcTicks).Value!.CapabilityId;
        var grant = kernel.CapabilityAuthority.Mint(new(1901), new(1901), ResourceKind.Compute,
            "resource-use:compute-time", CapabilityRights.Delegate, target.Generation, 1,
            range: null, session: null, quota: 100, delegationDepth: 4,
            resourceUse: new(1, Envelope(100), 0, long.MaxValue,
                ResourceAssuranceV1.RuntimeEnforced, 3)).Value!.CapabilityId;
        var region = kernel.AllocateBuffer<byte>(target, 16).Value!;
        var operation = kernel.PrepareExternalOperation(target,
            [new(region.Handle, RegionUseMode.ReadOnly, new(0, 16))],
            ExternalVisibilityRequirement.None, ExternalPublicationPolicy.Staged).Value!.Operation;
        return new(kernel, target, budget.ProcessBudget, effect, grant, region.Handle, operation, new(1, 1));
    }

    private static ResourceEnvelopeV1 Envelope(ulong amount) => new(1,
        ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
        ResourceUnitV1.Nanoseconds, amount, 0, "host:compute-v1");

    private static ulong Used(Context context) => Assert.Single(
        context.Kernel.QueryBudget(context.ProcessBudget).Value!.Usage,
        usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used;

    private sealed record Context(
        RuntimeKernel Kernel, ProcessHandle Process, BudgetAccountHandle ProcessBudget,
        CapabilityId EffectCapability, CapabilityId ResourceGrant, RegionHandle Region,
        ExternalOperationHandle Operation, OperationDependencySnapshot Dependencies);

    private sealed class Hook(Action<ResourceAdmissionQualificationPoint> action) : IResourceAdmissionQualificationHook
    {
        public void At(ResourceAdmissionQualificationPoint point) => action(point);
    }
}
