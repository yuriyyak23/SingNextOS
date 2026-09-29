using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class V6TemporalCapacityReservationTests
{
    [Fact]
    public void StaleReleaseCannotTriggerInjectedResetOfLiveReservation()
    {
        var provider = new V6ManagedTemporalCapacityProvider(50);
        var reserved = provider.Reserve("operation:capacity:exact-release", Temporal(40).ComputeEnvelope).Value!;
        Assert.True(provider.BeginUse(reserved.Handle).IsSuccess);
        provider.InjectResetBeforeNextRelease();

        Assert.Equal(KernelError.StaleGeneration,
            provider.Release(reserved.Handle with { Generation = reserved.Handle.Generation + 1 }).Error);
        Assert.Equal(1UL, provider.ProviderGeneration);
        Assert.Equal(V6TemporalProviderReservationState.InUse,
            provider.Query(reserved.Handle).Value!.State);
        Assert.Equal(40UL, provider.ReservedNanoseconds);

        Assert.Equal(KernelError.InvalidTransition, provider.Release(reserved.Handle).Error);
        Assert.Equal(2UL, provider.ProviderGeneration);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            provider.Query(reserved.Handle).Value!.State);
        Assert.Equal(40UL, provider.ReservedNanoseconds);
    }

    [Fact]
    public void RepeatedOrQuarantinedReleaseCannotTriggerInjectedReset()
    {
        var provider = new V6ManagedTemporalCapacityProvider(50);
        var released = provider.Reserve("operation:capacity:released", Temporal(10).ComputeEnvelope).Value!;
        Assert.True(provider.Release(released.Handle).IsSuccess);
        var live = provider.Reserve("operation:capacity:live", Temporal(40).ComputeEnvelope).Value!;
        Assert.True(provider.BeginUse(live.Handle).IsSuccess);
        provider.InjectResetBeforeNextRelease();

        Assert.True(provider.Release(released.Handle).IsSuccess);
        Assert.Equal(1UL, provider.ProviderGeneration);
        Assert.Equal(V6TemporalProviderReservationState.InUse, provider.Query(live.Handle).Value!.State);
        Assert.Equal(40UL, provider.ReservedNanoseconds);

        Assert.Equal(KernelError.InvalidTransition, provider.Release(live.Handle).Error);
        Assert.Equal(2UL, provider.ProviderGeneration);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined, provider.Query(live.Handle).Value!.State);
        provider.InjectResetBeforeNextRelease();
        Assert.Equal(KernelError.InvalidTransition, provider.Release(live.Handle).Error);
        Assert.Equal(2UL, provider.ProviderGeneration);
    }

    [Fact]
    public void AdmissionBindsExistingBudgetAndNamedProviderWithoutDeadlinePromotion()
    {
        var context = Create(100, 100, 60);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:capacity:1", Temporal(60)).Value!;

        Assert.Equal(60UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(V6TemporalCapacityOperationState.Admitted, binding.State);
        Assert.Equal(V6ManagedTemporalCapacityProvider.Identity, binding.ProviderReservation.ProviderIdentity);
        Assert.False(binding.AuthorizesExecution);
        Assert.False(binding.GuaranteesDeadline);
        Assert.False(binding.GuaranteesMinimumService);

        var cancelled = context.Coordinator.CancelAdmitted(binding);
        Assert.True(cancelled.IsSuccess, cancelled.Message);
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(0UL, Used(context));
    }

    [Fact]
    public void SuccessfulSubmitConsumesBudgetThenExactSettlementReleasesCapacity()
    {
        var context = Create(100, 100, 80);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:capacity:submit", Temporal(80)).Value!;
        var callbacks = 0;
        var submitted = context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () =>
            {
                callbacks++;
                Assert.Equal(BudgetReservationState.Consuming,
                    context.Budgets.Query(context.Budget).Value!.State);
                Assert.Equal(V6TemporalProviderReservationState.InUse,
                    context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
                return KernelResult.Ok();
            });

        Assert.True(submitted.IsSuccess, submitted.Message);
        Assert.Equal(1, callbacks);
        var settled = context.Coordinator.Settle(submitted.Value!, 45);
        Assert.True(settled.IsSuccess, settled.Message);
        Assert.Equal(V6TemporalCapacityOperationState.Settled, settled.Value!.State);
        Assert.Equal(45UL, Used(context));
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void FreshAdmissionDenialLeavesBothReservationsReusable()
    {
        var context = Create(100, 100, 50);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:capacity:denied", Temporal(50)).Value!;
        var callbacks = 0;

        var result = context.Coordinator.Submit(binding,
            () => KernelResult.Fail(KernelError.CapabilityRevoked, "revoked"),
            KernelResult.Ok, KernelResult.Ok,
            () => { callbacks++; return KernelResult.Ok(); });

        Assert.Equal(KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.Bound, context.Budgets.Query(context.Budget).Value!.State);
        Assert.Equal(V6TemporalProviderReservationState.Reserved,
            context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
        Assert.True(context.Coordinator.CancelAdmitted(binding).IsSuccess);
    }

    [Fact]
    public void BoundTemporalQuarantineRequiresExactExternalOperationOwnerRelease()
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 9910, 9911).Handle;
        var owner = TestFixtures.Create(kernel, 9912, 9913).Handle;
        var administration = kernel.MintCapability(new(9911), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, owner, "temporal-bound",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        var budget = kernel.Budgets.Reserve(owner,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 50)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(kernel.Budgets.BindLease(owner, budget).IsSuccess);
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var operation = kernel.PrepareExternalOperation(owner,
            [new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        var dependencies = new OperationDependencySnapshot(1, 1);
        Assert.True(kernel.AdmitExternalOperation(owner, operation, dependencies,
            new("temporal-bound-provider")).IsSuccess);
        var provider = new V6ManagedTemporalCapacityProvider(50);
        var coordinator = new V6TemporalCapacityCoordinator(kernel, provider);
        var bound = coordinator.AdmitBoundToExternalOperation(owner, budget, operation, Temporal(50)).Value!;
        var unboundCallbacks = 0;
        Assert.Equal(KernelError.PlatformDenied,
            coordinator.Submit(bound, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
                () => { unboundCallbacks++; return KernelResult.Ok(); }).Error);
        Assert.Equal(0, unboundCallbacks);

        var ambiguous = coordinator.SubmitBoundExternalOperation(bound, dependencies,
            KernelResult.Ok, KernelResult.Ok,
            KernelResult.Ok, () =>
            {
                Assert.Equal(ExternalOperationState.Submitted,
                    kernel.QueryExternalOperation(owner, operation).Value!.State);
                return KernelResult.Fail(KernelError.PlatformUnavailable, "provider outcome unknown");
            });
        Assert.Equal(KernelError.PlatformUnavailable, ambiguous.Error);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            coordinator.ReconcileQuarantinedBoundExternalOperation(bound).Error);
        Assert.Equal(KernelError.PlatformDenied,
            coordinator.ReconcileQuarantined(bound, KernelResult.Ok).Error);
        Assert.Equal(KernelError.InvalidTransition,
            coordinator.ReconcileQuarantined(bound with { ExternalOperation = null }, KernelResult.Ok).Error);
        Assert.Equal(BudgetReservationState.Quarantined, kernel.Budgets.Query(budget).Value!.State);
        Assert.Equal(50UL, provider.ReservedNanoseconds);

        Assert.True(kernel.RecordExternalOperationProviderLoss(owner, operation).IsSuccess);
        Assert.True(kernel.ReleaseExternalOperation(owner, operation,
            new(ProviderResourcesClosed: true, ProviderUnavailable: true)).IsSuccess);
        var reconciled = coordinator.ReconcileQuarantinedBoundExternalOperation(bound);
        Assert.True(reconciled.IsSuccess, reconciled.Message);
        Assert.Equal(BudgetReservationState.Released, kernel.Budgets.Query(budget).Value!.State);
        Assert.Equal(0UL, provider.ReservedNanoseconds);
    }

    [Fact]
    public void BoundTemporalSettlementWaitsForOwnerPublication()
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 9920, 9921).Handle;
        var owner = TestFixtures.Create(kernel, 9922, 9923).Handle;
        var administration = kernel.MintCapability(new(9921), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, owner, "temporal-publication",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        var budget = kernel.Budgets.Reserve(owner,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 50)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(kernel.Budgets.BindLease(owner, budget).IsSuccess);
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var operation = kernel.PrepareExternalOperation(owner,
            [new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        var dependencies = new OperationDependencySnapshot(1, 1);
        Assert.True(kernel.AdmitExternalOperation(owner, operation, dependencies,
            new("temporal-publication-provider")).IsSuccess);
        var provider = new V6ManagedTemporalCapacityProvider(50);
        var coordinator = new V6TemporalCapacityCoordinator(kernel, provider);
        var bound = coordinator.AdmitBoundToExternalOperation(owner, budget, operation, Temporal(50)).Value!;
        OperationBinding? ownerBinding = null;
        var submitted = coordinator.SubmitBoundExternalOperation(bound, dependencies,
            KernelResult.Ok, KernelResult.Ok,
            KernelResult.Ok, () =>
            {
                var effect = kernel.QueryExternalOperation(owner, operation);
                Assert.Equal(ExternalOperationState.Submitted, effect.Value!.State);
                ownerBinding = effect.Value.Binding;
                return KernelResult.Ok();
            }).Value!;

        Assert.Equal(KernelError.InvalidTransition,
            coordinator.Settle(submitted with { ExternalOperation = null }, 20).Error);
        Assert.Equal(KernelError.ExternalEffectUncontained, coordinator.Settle(submitted, 20).Error);
        Assert.Equal(BudgetReservationState.Consuming, kernel.Budgets.Query(budget).Value!.State);
        Assert.True(kernel.RecordExternalOperationCompletion(owner,
            new(ownerBinding!.Value, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(kernel.RecordExternalOperationVisibility(owner,
            new(ownerBinding.Value, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        Assert.True(kernel.PublishExternalOperation(owner, operation, dependencies,
            new(ExternalPublicationPolicy.Staged), static () => { }).IsSuccess);
        var settled = coordinator.Settle(submitted, 20);

        Assert.True(settled.IsSuccess, settled.Message);
        Assert.Equal(0UL, provider.ReservedNanoseconds);
        Assert.Equal(BudgetReservationState.Released, kernel.Budgets.Query(budget).Value!.State);
    }

    [Fact]
    public void BoundOwnerSubmitDenialClosesCapacityBeforeProviderCallback()
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 9930, 9931).Handle;
        var owner = TestFixtures.Create(kernel, 9932, 9933).Handle;
        var administration = kernel.MintCapability(new(9931), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, owner, "temporal-owner-denial",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        var budget = kernel.Budgets.Reserve(owner,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 50)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(kernel.Budgets.BindLease(owner, budget).IsSuccess);
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var operation = kernel.PrepareExternalOperation(owner,
            [new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        Assert.True(kernel.AdmitExternalOperation(owner, operation,
            new OperationDependencySnapshot(1, 1), new("temporal-denial-provider")).IsSuccess);
        var provider = new V6ManagedTemporalCapacityProvider(50);
        var coordinator = new V6TemporalCapacityCoordinator(kernel, provider);
        var bound = coordinator.AdmitBoundToExternalOperation(owner, budget, operation, Temporal(50)).Value!;
        var callbacks = 0;

        var denied = coordinator.SubmitBoundExternalOperation(bound,
            new OperationDependencySnapshot(2, 2), KernelResult.Ok, KernelResult.Ok,
            KernelResult.Ok, () => { callbacks++; return KernelResult.Ok(); });

        Assert.False(denied.IsSuccess);
        Assert.Equal(0, callbacks);
        Assert.Equal(0UL, provider.ReservedNanoseconds);
        Assert.Equal(BudgetReservationState.Released, kernel.Budgets.Query(budget).Value!.State);
        Assert.Equal(ExternalOperationState.Admitted,
            kernel.QueryExternalOperation(owner, operation).Value!.State);
        Assert.Equal(KernelError.InvalidTransition,
            coordinator.ReconcileQuarantinedBoundExternalOperation(bound).Error);
    }

    [Fact]
    public async Task ConcurrentBoundSubmitHasOneOwnerTransitionAndOneProviderCallback()
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 9940, 9941).Handle;
        var owner = TestFixtures.Create(kernel, 9942, 9943).Handle;
        var administration = kernel.MintCapability(new(9941), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, owner, "temporal-race",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        var budget = kernel.Budgets.Reserve(owner,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 50)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(kernel.Budgets.BindLease(owner, budget).IsSuccess);
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var operation = kernel.PrepareExternalOperation(owner,
            [new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        var dependencies = new OperationDependencySnapshot(1, 1);
        Assert.True(kernel.AdmitExternalOperation(owner, operation, dependencies,
            new("temporal-race-provider")).IsSuccess);
        var provider = new V6ManagedTemporalCapacityProvider(50);
        var coordinator = new V6TemporalCapacityCoordinator(kernel, provider);
        var bound = coordinator.AdmitBoundToExternalOperation(owner, budget, operation, Temporal(50)).Value!;
        using var barrier = new Barrier(2);
        var callbacks = 0;
        KernelResult<V6TemporalCapacityBinding> Invoke() => coordinator.SubmitBoundExternalOperation(
            bound, dependencies, KernelResult.Ok, KernelResult.Ok,
            () => { barrier.SignalAndWait(TimeSpan.FromSeconds(5)); return KernelResult.Ok(); },
            () => { Interlocked.Increment(ref callbacks); return KernelResult.Ok(); });

        var results = await Task.WhenAll(Task.Run(Invoke), Task.Run(Invoke)).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(results, result => result.Error == KernelError.StaleGeneration);
        Assert.Equal(1, callbacks);
        Assert.Equal(1, kernel.QueryExternalOperation(owner, operation).Value!.Transitions.Count(
            transition => transition.Event == "Submitted"));
        Assert.Equal(BudgetReservationState.Consuming, kernel.Budgets.Query(budget).Value!.State);
    }

    [Fact]
    public void BoundOwnerLossDuringSuccessfulProviderCallbackStillQuarantinesCapacity()
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 9950, 9951).Handle;
        var owner = TestFixtures.Create(kernel, 9952, 9953).Handle;
        var administration = kernel.MintCapability(new(9951), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, owner, "temporal-owner-loss",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        var budget = kernel.Budgets.Reserve(owner,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 50)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(kernel.Budgets.BindLease(owner, budget).IsSuccess);
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var operation = kernel.PrepareExternalOperation(owner,
            [new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        var dependencies = new OperationDependencySnapshot(1, 1);
        Assert.True(kernel.AdmitExternalOperation(owner, operation, dependencies,
            new("temporal-loss-provider")).IsSuccess);
        var provider = new V6ManagedTemporalCapacityProvider(50);
        var coordinator = new V6TemporalCapacityCoordinator(kernel, provider);
        var bound = coordinator.AdmitBoundToExternalOperation(owner, budget, operation, Temporal(50)).Value!;

        var result = coordinator.SubmitBoundExternalOperation(bound, dependencies,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, () =>
            {
                Assert.True(kernel.RecordExternalOperationProviderLoss(owner, operation).IsSuccess);
                return KernelResult.Ok();
            });

        Assert.Equal(KernelError.StaleGeneration, result.Error);
        Assert.Equal(BudgetReservationState.Quarantined, kernel.Budgets.Query(budget).Value!.State);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            provider.Query(bound.ProviderReservation.Handle).Value!.State);
        Assert.Equal(KernelError.ExternalEffectUncontained,
            coordinator.ReconcileQuarantinedBoundExternalOperation(bound).Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegalityTriggeredAdmissionRevocationIsCaughtBeforeCapacityUse(bool revokeSingNext)
    {
        var context = Create(100, 100, 50);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:capacity:legality-revoke", Temporal(50)).Value!;
        var revoked = false;
        var singNextReads = 0;
        var providerReads = 0;
        var effects = 0;

        var result = context.Coordinator.Submit(binding,
            () =>
            {
                singNextReads++;
                return revoked && revokeSingNext
                    ? KernelResult.Fail(KernelError.CapabilityRevoked, "SingNext admission revoked")
                    : KernelResult.Ok();
            },
            () =>
            {
                providerReads++;
                return revoked && !revokeSingNext
                    ? KernelResult.Fail(KernelError.PlatformDenied, "provider admission revoked")
                    : KernelResult.Ok();
            },
            () => { revoked = true; return KernelResult.Ok(); },
            () => { effects++; return KernelResult.Ok(); });

        Assert.Equal(revokeSingNext ? KernelError.CapabilityRevoked : KernelError.PlatformDenied,
            result.Error);
        Assert.Equal(2, singNextReads);
        Assert.Equal(revokeSingNext ? 1 : 2, providerReads);
        Assert.Equal(0, effects);
        Assert.Equal(BudgetReservationState.Bound, context.Budgets.Query(context.Budget).Value!.State);
        Assert.Equal(V6TemporalProviderReservationState.Reserved,
            context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
    }

    [Fact]
    public void BudgetCancelledAfterAdmissionClosesUnusedProviderCapacityBeforeSubmit()
    {
        var context = Create(100, 100, 50);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:capacity:budget-cancelled", Temporal(50)).Value!;
        Assert.True(context.Budgets.CancelLeasePreSubmit(context.Owner, context.Budget).IsSuccess);
        var effects = 0;

        var stale = context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => { effects++; return KernelResult.Ok(); });

        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.Equal(0, effects);
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(V6TemporalProviderReservationState.Released,
            context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
        Assert.Equal(KernelError.StaleGeneration, context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => { effects++; return KernelResult.Ok(); }).Error);
        Assert.Equal(0, effects);
    }

    [Fact]
    public void ProviderFailureQuarantinesBothOwnersUntilExplicitReconciliation()
    {
        var context = Create(100, 100, 50);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:capacity:failure", Temporal(50)).Value!;
        var result = context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => KernelResult.Fail(KernelError.PlatformUnavailable, "lost"));

        Assert.Equal(KernelError.PlatformUnavailable, result.Error);
        Assert.Equal(BudgetReservationState.Quarantined, context.Budgets.Query(context.Budget).Value!.State);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
        Assert.Equal(50UL, Used(context));
        var reconciled = context.Coordinator.ReconcileQuarantined(
            binding with { State = V6TemporalCapacityOperationState.Quarantined }, KernelResult.Ok);
        Assert.True(reconciled.IsSuccess, reconciled.Message);
        Assert.Equal(0UL, Used(context));
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void DeniedEffectClosureCannotReleaseQuarantinedCapacityOrBudget()
    {
        var context = Create(100, 100, 50);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:closure:denied", Temporal(50)).Value!;
        Assert.Equal(KernelError.PlatformUnavailable, context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => KernelResult.Fail(KernelError.PlatformUnavailable, "ambiguous effect")).Error);

        var denied = context.Coordinator.ReconcileQuarantined(binding,
            () => KernelResult.Fail(KernelError.PlatformDenied, "effect not closed"));

        Assert.Equal(KernelError.PlatformDenied, denied.Error);
        Assert.Equal(BudgetReservationState.Quarantined,
            context.Budgets.Query(context.Budget).Value!.State);
        Assert.Equal(50UL, Used(context));
        Assert.Equal(50UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
    }

    [Fact]
    public void EffectClosureCallbackRunsOutsideCoordinatorLock()
    {
        var context = Create(100, 100, 50);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:closure:outside-lock", Temporal(50)).Value!;
        Assert.Equal(KernelError.PlatformUnavailable, context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => KernelResult.Fail(KernelError.PlatformUnavailable, "ambiguous effect")).Error);

        var reconciled = context.Coordinator.ReconcileQuarantined(binding, () =>
        {
            var concurrent = Task.Run(() => context.Coordinator.CancelAdmitted(binding));
            Assert.True(concurrent.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(KernelError.InvalidTransition, concurrent.Result.Error);
            return KernelResult.Ok();
        });

        Assert.True(reconciled.IsSuccess, reconciled.Message);
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void ConcurrentReconciliationConsumesQuarantineOnce()
    {
        var context = Create(100, 100, 50);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:closure:one-winner", Temporal(50)).Value!;
        Assert.Equal(KernelError.PlatformUnavailable, context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => KernelResult.Fail(KernelError.PlatformUnavailable, "ambiguous effect")).Error);

        var outer = context.Coordinator.ReconcileQuarantined(binding, () =>
        {
            var inner = context.Coordinator.ReconcileQuarantined(binding, KernelResult.Ok);
            Assert.True(inner.IsSuccess, inner.Message);
            return KernelResult.Ok();
        });

        Assert.Equal(KernelError.StaleGeneration, outer.Error);
        Assert.Equal(0UL, Used(context));
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void ProviderResetBeforeOrDuringSubmitNeverRunsOrPublishesStaleCapacity()
    {
        var before = Create(100, 100, 40);
        var beforeBinding = before.Coordinator.Admit(before.Owner, before.Budget,
            "operation:capacity:reset-before", Temporal(40)).Value!;
        Assert.True(before.Provider.Reset().IsSuccess);
        var callbacks = 0;
        var denied = before.Coordinator.Submit(beforeBinding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => { callbacks++; return KernelResult.Ok(); });
        Assert.Equal(KernelError.StaleGeneration, denied.Error);
        Assert.Equal(0, callbacks);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            before.Budgets.Query(before.Budget).Value!.State);
        Assert.Equal(0UL, before.Provider.ReservedNanoseconds);

        var during = Create(100, 100, 40);
        var duringBinding = during.Coordinator.Admit(during.Owner, during.Budget,
            "operation:capacity:reset-during", Temporal(40)).Value!;
        var ambiguous = during.Coordinator.Submit(duringBinding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok,
            () => { Assert.True(during.Provider.Reset().IsSuccess); return KernelResult.Ok(); });
        Assert.Equal(KernelError.StaleGeneration, ambiguous.Error);
        Assert.Equal(BudgetReservationState.Quarantined,
            during.Budgets.Query(during.Budget).Value!.State);
        Assert.Equal(40UL, during.Provider.ReservedNanoseconds);
        var reconciled = during.Coordinator.ReconcileQuarantined(
            duringBinding with { State = V6TemporalCapacityOperationState.Quarantined }, KernelResult.Ok);
        Assert.True(reconciled.IsSuccess, reconciled.Message);
        Assert.Equal(0UL, Used(during));
        Assert.Equal(0UL, during.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void ResetRetainsAmbiguousCapacityAndCorrelationUntilReconciliation()
    {
        var provider = new V6ManagedTemporalCapacityProvider(100);
        var first = provider.Reserve("operation:reset:ambiguous", Temporal(80).ComputeEnvelope).Value!;
        Assert.True(provider.BeginUse(first.Handle).IsSuccess);
        Assert.True(provider.Reset().IsSuccess);

        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            provider.Query(first.Handle).Value!.State);
        Assert.Equal(80UL, provider.ReservedNanoseconds);
        Assert.Equal(KernelError.DuplicateIdentity,
            provider.Reserve("operation:reset:ambiguous", Temporal(1).ComputeEnvelope).Error);
        Assert.Equal(KernelError.BudgetExceeded,
            provider.Reserve("operation:reset:new", Temporal(21).ComputeEnvelope).Error);

        Assert.True(provider.ReconcileAndRelease(first.Handle).IsSuccess);
        Assert.Equal(0UL, provider.ReservedNanoseconds);
        Assert.True(provider.Reserve("operation:reset:ambiguous", Temporal(80).ComputeEnvelope).IsSuccess);
    }

    [Fact]
    public void ResetBeforeSubmitAllowsExactCancellationOfStaleReservation()
    {
        var context = Create(100, 100, 40);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:reset:pre-submit", Temporal(40)).Value!;
        Assert.True(context.Provider.Reset().IsSuccess);

        var cancelled = context.Coordinator.CancelAdmitted(binding);

        Assert.True(cancelled.IsSuccess, cancelled.Message);
        Assert.Equal(V6TemporalCapacityOperationState.Cancelled, cancelled.Value!.State);
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(0UL, Used(context));
    }

    [Fact]
    public void ResetAfterSubmitQuarantinesBudgetBeforeSettlement()
    {
        var context = Create(100, 100, 40);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:reset:after-submit", Temporal(40)).Value!;
        var submitted = context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok).Value!;
        Assert.True(context.Provider.Reset().IsSuccess);

        var stale = context.Coordinator.Settle(submitted, 20);

        Assert.Equal(KernelError.StaleGeneration, stale.Error);
        Assert.Equal(BudgetReservationState.Quarantined,
            context.Budgets.Query(context.Budget).Value!.State);
        Assert.Equal(40UL, Used(context));
        Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
        Assert.True(context.Coordinator.ReconcileQuarantined(submitted, KernelResult.Ok).IsSuccess);
        Assert.Equal(0UL, Used(context));
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void SettlementFailureQuarantinesProviderAndKeepsReconciliationReachable()
    {
        var context = Create(100, 100, 40);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:settlement:failure", Temporal(40)).Value!;
        var submitted = context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok).Value!;
        Assert.True(context.Budgets.QuarantineLease(context.Owner, context.Budget).IsSuccess);

        var failed = context.Coordinator.Settle(submitted, 20);

        Assert.Equal(KernelError.InvalidTransition, failed.Error);
        Assert.Equal(BudgetReservationState.Quarantined,
            context.Budgets.Query(context.Budget).Value!.State);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
        Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
        Assert.True(context.Coordinator.ReconcileQuarantined(submitted, KernelResult.Ok).IsSuccess);
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void ResetBetweenBudgetSettlementAndProviderReleaseRetainsCapacityForReconciliation()
    {
        var context = Create(100, 100, 40);
        var binding = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:settlement:reset-race", Temporal(40)).Value!;
        var submitted = context.Coordinator.Submit(binding,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok).Value!;
        context.Provider.InjectResetBeforeNextRelease();

        var interrupted = context.Coordinator.Settle(submitted, 20);

        Assert.Equal(KernelError.PlatformFaulted, interrupted.Error);
        Assert.Equal(BudgetReservationState.Released,
            context.Budgets.Query(context.Budget).Value!.State);
        Assert.Equal(20UL, Used(context));
        Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            context.Provider.Query(binding.ProviderReservation.Handle).Value!.State);
        Assert.True(context.Coordinator.ReconcileQuarantined(submitted, KernelResult.Ok).IsSuccess);
        Assert.Equal(20UL, Used(context));
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public async Task LastProtectedCapacityUnitHasExactlyOneWinner()
    {
        var budgets = BudgetAuthority(200, out var owner, out var account);
        var firstBudget = BoundBudget(budgets, owner, 60);
        var secondBudget = BoundBudget(budgets, owner, 60);
        var provider = new V6ManagedTemporalCapacityProvider(60);
        var coordinator = new V6TemporalCapacityCoordinator(budgets, provider);
        using var barrier = new Barrier(2);
        Task<KernelResult<V6TemporalCapacityBinding>> Admit(string correlation, BudgetReservationHandle budget) =>
            Task.Run(() =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(5));
                return coordinator.Admit(owner, budget, correlation, Temporal(60));
            });
        var results = await Task.WhenAll(Admit("operation:capacity:a", firstBudget),
            Admit("operation:capacity:b", secondBudget));

        var winner = Assert.Single(results, result => result.IsSuccess).Value!;
        Assert.Equal(KernelError.BudgetExceeded, Assert.Single(results, result => !result.IsSuccess).Error);
        Assert.Equal(60UL, provider.ReservedNanoseconds);
        Assert.Equal(120UL, Used(budgets.Query(account).Value!));
        Assert.True(coordinator.CancelAdmitted(winner).IsSuccess);
    }

    [Fact]
    public void GuaranteedCompletionDeadlineIsStillRejectedByCapacityContour()
    {
        var context = Create(100, 100, 40);
        var deadline = Temporal(40) with
        {
            DeadlineSemantics = TemporalDeadlineSemanticsV1.GuaranteedCompletion,
            DeadlineTimestamp = 1000,
        };

        Assert.Equal(KernelError.PlatformUnsupported,
            context.Coordinator.Admit(context.Owner, context.Budget,
                "operation:capacity:deadline", deadline).Error);
        Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void MeasuredOverrunQuarantinesBothOwnersAndCannotBeRewrittenByLowerSettlement()
    {
        var context = Create(100, 50, 40);
        var admitted = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:capacity:overrun", Temporal(40)).Value!;
        var submitted = context.Coordinator.Submit(admitted, KernelResult.Ok, KernelResult.Ok,
            KernelResult.Ok, KernelResult.Ok).Value!;

        Assert.Equal(KernelError.BudgetExceeded, context.Coordinator.Settle(submitted, 41).Error);
        Assert.Equal(BudgetReservationState.Quarantined,
            context.Budgets.Query(context.Budget).Value!.State);
        Assert.Equal(V6TemporalProviderReservationState.Quarantined,
            context.Provider.Query(submitted.ProviderReservation.Handle).Value!.State);
        Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
        Assert.Equal(KernelError.InvalidTransition, context.Coordinator.Settle(submitted, 20).Error);
        var closureCalls = 0;
        var reconciliation = context.Coordinator.ReconcileQuarantined(submitted, () =>
        {
            closureCalls++;
            return KernelResult.Ok();
        });
        Assert.Equal(KernelError.BudgetExceeded, reconciliation.Error);
        Assert.Equal(0, closureCalls);
        var budget = context.Budgets.Query(context.Budget).Value!;
        Assert.Equal(BudgetReservationState.Quarantined, budget.State);
        Assert.Empty(budget.ChargedAmounts ?? []);
        Assert.Equal(40UL, Used(context));
        Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
    }

    [Theory]
    [InlineData(100UL, true)]
    [InlineData(40UL, false)]
    public void CorrectiveOverrunSettlementUsesBudgetOwnerCapacityOrRetainsQuarantine(
        ulong budgetLimit, bool chargeFits)
    {
        var context = Create(budgetLimit, 40, 40);
        var admitted = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:capacity:corrective", Temporal(40)).Value!;
        var submitted = context.Coordinator.Submit(admitted, KernelResult.Ok, KernelResult.Ok,
            KernelResult.Ok, KernelResult.Ok).Value!;
        Assert.Equal(KernelError.BudgetExceeded, context.Coordinator.Settle(submitted, 41).Error);
        var closureCalls = 0;
        var corrected = context.Coordinator.ReconcileReportedOverrun(submitted, () =>
        {
            closureCalls++;
            return KernelResult.Ok();
        });

        Assert.Equal(1, closureCalls);
        if (chargeFits)
        {
            Assert.True(corrected.IsSuccess, corrected.Message);
            Assert.Equal(BudgetReservationState.Released, context.Budgets.Query(context.Budget).Value!.State);
            Assert.Equal(41UL, Assert.Single(context.Budgets.Query(context.Budget).Value!.ChargedAmounts!).Amount);
            Assert.Equal(41UL, Used(context));
            Assert.Equal(0UL, context.Provider.ReservedNanoseconds);
            Assert.Equal(KernelError.InvalidTransition,
                context.Coordinator.ReconcileReportedOverrun(submitted, KernelResult.Ok).Error);
        }
        else
        {
            Assert.Equal(KernelError.BudgetExceeded, corrected.Error);
            Assert.Equal(BudgetReservationState.Quarantined, context.Budgets.Query(context.Budget).Value!.State);
            Assert.Equal(40UL, Used(context));
            Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
        }
    }

    [Fact]
    public void CorrectiveOverrunChargeRequiresEffectClosureBeforeBudgetMutation()
    {
        var context = Create(100, 40, 40);
        var admitted = context.Coordinator.Admit(context.Owner, context.Budget,
            "operation:capacity:closure-before-charge", Temporal(40)).Value!;
        var submitted = context.Coordinator.Submit(admitted, KernelResult.Ok, KernelResult.Ok,
            KernelResult.Ok, KernelResult.Ok).Value!;
        Assert.Equal(KernelError.BudgetExceeded, context.Coordinator.Settle(submitted, 41).Error);

        var denied = context.Coordinator.ReconcileReportedOverrun(submitted,
            () => KernelResult.Fail(KernelError.ExternalEffectUncontained, "effect remains open"));

        Assert.Equal(KernelError.ExternalEffectUncontained, denied.Error);
        Assert.Equal(BudgetReservationState.Quarantined, context.Budgets.Query(context.Budget).Value!.State);
        Assert.Empty(context.Budgets.Query(context.Budget).Value!.ChargedAmounts ?? []);
        Assert.Equal(40UL, Used(context));
        Assert.Equal(40UL, context.Provider.ReservedNanoseconds);
    }

    [Fact]
    public void BoundCorrectiveOverrunWaitsForExactExternalOwnerRelease()
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 9960, 9961).Handle;
        var owner = TestFixtures.Create(kernel, 9962, 9963).Handle;
        var administration = kernel.MintCapability(new(9961), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, owner, "temporal-bound-overrun",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        var budget = kernel.Budgets.Reserve(owner,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 40)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(kernel.Budgets.BindLease(owner, budget).IsSuccess);
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var operation = kernel.PrepareExternalOperation(owner,
            [new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        var dependencies = new OperationDependencySnapshot(1, 1);
        Assert.True(kernel.AdmitExternalOperation(owner, operation, dependencies,
            new("temporal-bound-overrun-provider")).IsSuccess);
        var provider = new V6ManagedTemporalCapacityProvider(40);
        var coordinator = new V6TemporalCapacityCoordinator(kernel, provider);
        var admitted = coordinator.AdmitBoundToExternalOperation(owner, budget, operation, Temporal(40)).Value!;
        var submitted = coordinator.SubmitBoundExternalOperation(admitted, dependencies,
            KernelResult.Ok, KernelResult.Ok, KernelResult.Ok, KernelResult.Ok).Value!;
        var ownerBinding = kernel.QueryExternalOperation(owner, operation).Value!.Binding!.Value;
        Assert.True(kernel.RecordExternalOperationCompletion(owner,
            new(ownerBinding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(kernel.RecordExternalOperationVisibility(owner,
            new(ownerBinding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        Assert.True(kernel.PublishExternalOperation(owner, operation, dependencies,
            new(ExternalPublicationPolicy.Staged), static () => { }).IsSuccess);
        Assert.Equal(KernelError.BudgetExceeded, coordinator.Settle(submitted, 41).Error);

        Assert.Equal(KernelError.ExternalEffectUncontained,
            coordinator.ReconcileReportedOverrunBoundExternalOperation(submitted).Error);
        Assert.Equal(KernelError.PlatformDenied,
            coordinator.ReconcileReportedOverrun(submitted, KernelResult.Ok).Error);
        Assert.Equal(BudgetReservationState.Quarantined, kernel.Budgets.Query(budget).Value!.State);
        Assert.True(kernel.ReleaseExternalOperation(owner, operation,
            new(ProviderResourcesClosed: true, ProviderUnavailable: false)).IsSuccess);

        var corrected = coordinator.ReconcileReportedOverrunBoundExternalOperation(submitted);
        Assert.True(corrected.IsSuccess, corrected.Message);
        Assert.Equal(41UL, Assert.Single(kernel.Budgets.Query(budget).Value!.ChargedAmounts!).Amount);
        Assert.Equal(0UL, provider.ReservedNanoseconds);
    }

    private static Context Create(ulong budgetLimit, ulong providerCapacity, ulong reservation)
    {
        var budgets = BudgetAuthority(budgetLimit, out var owner, out var account);
        var lease = BoundBudget(budgets, owner, reservation);
        var provider = new V6ManagedTemporalCapacityProvider(providerCapacity);
        return new(budgets, owner, account, lease, provider,
            new V6TemporalCapacityCoordinator(budgets, provider));
    }

    private static ResourceBudgetAuthority BudgetAuthority(
        ulong limit, out ProcessHandle owner, out BudgetAccountHandle account)
    {
        var budgets = new ResourceBudgetAuthority();
        Assert.True(budgets.ConfigureSystem([Amount(limit)]).IsSuccess);
        var service = budgets.CreateChild(budgets.SystemBudget, BudgetAccountLevel.Service,
            "temporal-service", [Amount(limit)]).Value!.Account;
        account = budgets.CreateChild(service, BudgetAccountLevel.ProcessDomain,
            "temporal-process", [Amount(limit)]).Value!.Account;
        owner = new ProcessHandle(new(990), 5);
        Assert.True(budgets.AttachProcess(owner, account).IsSuccess);
        return budgets;
    }

    private static BudgetReservationHandle BoundBudget(
        ResourceBudgetAuthority budgets, ProcessHandle owner, ulong amount)
    {
        var reservation = budgets.Reserve(owner, [Amount(amount)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.BoundedInteractive).Value!.Reservation;
        Assert.True(budgets.BindLease(owner, reservation).IsSuccess);
        return reservation;
    }

    private static TemporalSemanticsV1 Temporal(ulong amount) => new(1,
        new ResourceEnvelopeV1(1, ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
            ResourceUnitV1.Nanoseconds, amount, 0, "managed:protected-capacity"),
        ResourceAssuranceV1.GuaranteedReservation, TemporalDeadlineSemanticsV1.None,
        DeadlineClockClass.MonotonicRuntime, 0);

    private static BudgetAmount Amount(ulong amount) =>
        new(ServiceBudgetDimension.ComputeTimeNanoseconds, amount);

    private static ulong Used(Context context) => Used(context.Budgets.Query(context.Account).Value!);
    private static ulong Used(BudgetAccountSnapshot snapshot) =>
        Assert.Single(snapshot.Usage,
            usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used;

    private sealed record Context(ResourceBudgetAuthority Budgets, ProcessHandle Owner,
        BudgetAccountHandle Account, BudgetReservationHandle Budget,
        V6ManagedTemporalCapacityProvider Provider, V6TemporalCapacityCoordinator Coordinator);
}
