using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Sdk;

namespace SingPlus.Tests.Runtime;

[SipContract]
internal interface IVNextP06DonatedResourceService
{
    [Message(1)]
    [RequiresResource(1, ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds, 10,
        "host:compute-v1", ResourceAssuranceV1.RuntimeEnforced,
        SipResourceDonationPolicyV1.AcceptNarrowed)]
    int Run();
}

public sealed class VNextPhase06ResourceDonationTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void PossibleSubmitClockOwnerLossDoesNotMutatePostCallbackFacts(int fault)
    {
        DonationClock clock = new DonationClock();
        Setup setup = Create(timeProvider: clock, resourceGeneration: 7);
        InlineSipInvocationLease invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        ResourceDonationBinding donation = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, 7, Requirement(40), Envelope(40), AdmissionQosHint.None).Value!;
        BudgetReservationState observed = BudgetReservationState.Reserved;
        ulong used = 40;
        Action faultAction = () =>
        {
            if (fault is 0 or 6) Assert.True(setup.Kernel.CapabilityAuthority.Revoke(donation.DerivedGrant).IsSuccess);
            if (fault == 1)
            {
                KernelResult stopped = setup.Kernel.TerminateProcess(setup.Service);
                Assert.True(stopped.IsSuccess || stopped.Error == KernelError.PlatformBindingDraining, stopped.Message);
            }
            if (fault == 2) Assert.Equal(KernelError.SessionDraining, setup.Kernel.CloseSession(setup.Caller, setup.Session).Error);
            if (fault == 4) Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Service, invocation, false).IsSuccess);
            if (fault == 5) Assert.True(setup.Kernel.Budgets.CancelLeasePreSubmit(setup.Caller, donation.Lease).IsSuccess);
            observed = setup.Kernel.QueryBudget(donation.Lease).Value!.State;
            used = Used(setup);
            if (fault == 3) throw new InvalidOperationException("Clock failure before consumption.");
        };
        clock.Callback = fault == 6 ? () => clock.Callback = faultAction : faultAction;
        Assert.False(setup.Kernel.MarkResourceDonationPossibleSubmit(setup.Service, invocation.Context.Invocation).IsSuccess);
        Assert.Equal(observed, setup.Kernel.QueryBudget(donation.Lease).Value!.State);
        Assert.Equal(used, Used(setup));
        if (fault == 3)
        {
            Assert.True(setup.Kernel.MarkResourceDonationPossibleSubmit(setup.Service, invocation.Context.Invocation).IsSuccess);
            Assert.Equal(BudgetReservationState.Consuming, setup.Kernel.QueryBudget(donation.Lease).Value!.State);
            Assert.Equal(40UL, Used(setup));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PossibleSubmitClockWorkerReentryHasOneWinnerWithoutCorrelationDeadlock(bool returnDonation)
    {
        DonationClock clock = new DonationClock();
        Setup setup = Create(timeProvider: clock);
        InlineSipInvocationLease invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        ResourceDonationBinding donation = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(40), Envelope(40), AdmissionQosHint.None).Value!;
        clock.Callback = () =>
        {
            Task<KernelResult> worker = Task.Run(() => returnDonation
                ? setup.Kernel.CloseResourceDonation(setup.Service, invocation.Context.Invocation, submitMayHaveOccurred: false)
                : setup.Kernel.MarkResourceDonationPossibleSubmit(setup.Service, invocation.Context.Invocation));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Clock callback held correlation or owner commit lock.");
            Assert.True(worker.Result.IsSuccess, worker.Result.Message);
        };
        Assert.False(setup.Kernel.MarkResourceDonationPossibleSubmit(setup.Service, invocation.Context.Invocation).IsSuccess);
        Assert.Equal(returnDonation ? BudgetReservationState.CancelledPreSubmit : BudgetReservationState.Consuming,
            setup.Kernel.QueryBudget(donation.Lease).Value!.State);
        Assert.Equal(returnDonation ? 0UL : 40UL, Used(setup));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NonDefaultDonationGenerationReachesPossibleSubmitAndRetainsUnknownEffect(bool nested)
    {
        Setup setup = Create(includeDownstream: nested, resourceGeneration: 7);
        InlineSipInvocationLease parentInvocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        ResourceDonationBinding parent = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            parentInvocation.Context.Invocation, setup.SourceGrant, 7, Requirement(100), Envelope(100),
            AdmissionQosHint.LatencySensitive).Value!;
        ResourceDonationBinding donation = parent;
        InlineSipInvocationLease invocation = parentInvocation;
        ProcessHandle service = setup.Service;
        if (nested)
        {
            invocation = Begin(setup.Kernel, setup.Service, setup.Downstream, setup.DownstreamSession);
            donation = setup.Kernel.DeriveNestedResourceDonation(setup.Service, setup.Downstream,
                parent.Invocation, invocation.Context.Invocation, Envelope(30), ResourceAssuranceV1.AccountingOnly,
                AdmissionQosHint.Background).Value!;
            service = setup.Downstream;
        }
        Assert.Equal(7UL, donation.ResourceGrantGeneration);
        Assert.True(setup.Kernel.MarkResourceDonationPossibleSubmit(service, invocation.Context.Invocation).IsSuccess);
        Assert.Equal(BudgetReservationState.Consuming, setup.Kernel.QueryBudget(donation.Lease).Value!.State);
        Assert.Equal(100UL, Used(setup));
        Assert.True(setup.Kernel.CloseResourceDonation(service, invocation.Context.Invocation, submitMayHaveOccurred: true).IsSuccess);
        Assert.Equal(BudgetReservationState.Quarantined, setup.Kernel.QueryBudget(donation.Lease).Value!.State);
        Assert.Equal(100UL, Used(setup));
    }

    [Theory]
    [InlineData(0UL)] [InlineData(1UL)] [InlineData(6UL)]
    public void WrongDonationSourceGenerationCannotAllocateOrConsumeAndExactRetryWorks(ulong wrongGeneration)
    {
        Setup setup = Create(resourceGeneration: 7);
        InlineSipInvocationLease invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        int records = setup.Kernel.CapabilityAuthority.InspectionSnapshot().Length;
        Assert.Equal(KernelError.StaleGeneration, setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, wrongGeneration, Requirement(10), Envelope(10),
            AdmissionQosHint.None).Error);
        Assert.Equal(records, setup.Kernel.CapabilityAuthority.InspectionSnapshot().Length);
        Assert.Equal(0UL, Used(setup));
        Assert.False(setup.Kernel.MarkResourceDonationPossibleSubmit(setup.Service, invocation.Context.Invocation).IsSuccess);
        Assert.True(setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, 7, Requirement(10), Envelope(10), AdmissionQosHint.None).IsSuccess);
        Assert.True(setup.Kernel.MarkResourceDonationPossibleSubmit(setup.Service, invocation.Context.Invocation).IsSuccess);
        Assert.Equal(10UL, Used(setup));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void NestedFinalOwnerLossCannotDebitParentOrPublishChild(int fault)
    {
        Setup setup = Create(includeDownstream: true);
        InlineSipInvocationLease parentInvocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        ResourceDonationBinding parent = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            parentInvocation.Context.Invocation, setup.SourceGrant, 1, Requirement(100), Envelope(100),
            AdmissionQosHint.LatencySensitive).Value!;
        InlineSipInvocationLease childInvocation = Begin(setup.Kernel, setup.Service, setup.Downstream, setup.DownstreamSession);
        CapabilityId prepared = default;
        setup.Kernel.BeforeNestedDonationPublicationQualificationHook = grant =>
        {
            prepared = grant;
            Assert.Equal(100UL, Assert.Single(setup.Kernel.QueryBudget(parent.Lease).Value!.Amounts).Amount);
            if (fault == 0) Assert.True(setup.Kernel.CapabilityAuthority.Revoke(parent.DerivedGrant).IsSuccess);
            if (fault == 1) Assert.True(setup.Kernel.CapabilityAuthority.Revoke(grant).IsSuccess);
            if (fault == 2) Assert.True(setup.Kernel.TerminateProcess(setup.Downstream).IsSuccess);
            if (fault == 3) throw new InvalidOperationException("Nested preparation interrupted.");
            if (fault == 4)
            {
                childInvocation.Dispose();
                Assert.True(setup.Kernel.CloseSession(setup.Service, setup.DownstreamSession).IsSuccess);
            }
            if (fault == 5)
                Assert.True(setup.Kernel.MarkResourceDonationPossibleSubmit(setup.Service, parent.Invocation).IsSuccess);
            if (fault == 6)
                Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Downstream, childInvocation, false).IsSuccess);
            if (fault == 7) Assert.True(setup.Kernel.TerminateProcess(setup.Service).IsSuccess);
            if (fault == 8) Assert.True(setup.Kernel.Budgets.CancelLeasePreSubmit(setup.Caller, parent.Lease).IsSuccess);
        };
        Assert.False(setup.Kernel.DeriveNestedResourceDonation(setup.Service, setup.Downstream,
            parent.Invocation, childInvocation.Context.Invocation, Envelope(30), ResourceAssuranceV1.AccountingOnly,
            AdmissionQosHint.Background).IsSuccess);
        Assert.Equal(100UL, Assert.Single(setup.Kernel.QueryBudget(parent.Lease).Value!.Amounts).Amount);
        Assert.Equal(fault == 8 ? 0UL : 100UL, Used(setup));
        Assert.False(setup.Kernel.CapabilityAuthority.ValidateResourceUse(prepared, setup.DownstreamDomain,
            setup.Downstream.Generation, 1, Envelope(30)).IsSuccess);
        Assert.False(setup.Kernel.MarkResourceDonationPossibleSubmit(setup.Downstream, childInvocation.Context.Invocation).IsSuccess);
        if (fault == 3)
        {
            setup.Kernel.BeforeNestedDonationPublicationQualificationHook = null;
            Assert.True(setup.Kernel.DeriveNestedResourceDonation(setup.Service, setup.Downstream,
                parent.Invocation, childInvocation.Context.Invocation, Envelope(30), ResourceAssuranceV1.AccountingOnly,
                AdmissionQosHint.Background).IsSuccess);
            Assert.Equal(70UL, Assert.Single(setup.Kernel.QueryBudget(parent.Lease).Value!.Amounts).Amount);
            Assert.Equal(100UL, Used(setup));
        }
    }

    [Fact]
    public void NestedPreparationAllowsWorkerReentryWithoutSecondSplit()
    {
        Setup setup = Create(includeDownstream: true);
        InlineSipInvocationLease parentInvocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        ResourceDonationBinding parent = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            parentInvocation.Context.Invocation, setup.SourceGrant, 1, Requirement(100), Envelope(100),
            AdmissionQosHint.LatencySensitive).Value!;
        InlineSipInvocationLease child = Begin(setup.Kernel, setup.Service, setup.Downstream, setup.DownstreamSession);
        setup.Kernel.BeforeNestedDonationPublicationQualificationHook = _ =>
        {
            Task<KernelResult<ResourceDonationBinding>> reentry = Task.Run(() => setup.Kernel.DeriveNestedResourceDonation(setup.Service, setup.Downstream,
                parent.Invocation, child.Context.Invocation, Envelope(30), ResourceAssuranceV1.AccountingOnly,
                AdmissionQosHint.Background));
            Assert.True(reentry.Wait(TimeSpan.FromSeconds(5)), "Nested preparation held correlation gate.");
            Assert.Equal(KernelError.InvalidTransition, reentry.Result.Error);
        };
        Assert.True(setup.Kernel.DeriveNestedResourceDonation(setup.Service, setup.Downstream,
            parent.Invocation, child.Context.Invocation, Envelope(30), ResourceAssuranceV1.AccountingOnly,
            AdmissionQosHint.Background).IsSuccess);
        Assert.Equal(70UL, Assert.Single(setup.Kernel.QueryBudget(parent.Lease).Value!.Amounts).Amount);
        Assert.Equal(100UL, Used(setup));
    }

    [Fact]
    public void FinalDonationClockCallbackReentryAndSourceRevocationCannotPublish()
    {
        DonationClock clock = new DonationClock();
        Setup setup = Create(timeProvider: clock);
        InlineSipInvocationLease invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        setup.Kernel.BeforeDonationPublicationQualificationHook = binding => clock.Callback = () =>
        {
            Task<KernelResult<ResourceDonationBinding>> reentry = Task.Run(() => setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
                invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(10), Envelope(10), AdmissionQosHint.None));
            Assert.True(reentry.Wait(TimeSpan.FromSeconds(5)), "Actual resource clock callback held a correlation lock.");
            Assert.Equal(KernelError.InvalidTransition, reentry.Result.Error);
            Assert.True(setup.Kernel.CapabilityAuthority.Revoke(setup.SourceGrant).IsSuccess);
        };
        Assert.Equal(KernelError.CapabilityRevoked, setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(10), Envelope(10), AdmissionQosHint.None).Error);
        Assert.Equal(0UL, Used(setup));
        Assert.False(setup.Kernel.MarkResourceDonationPossibleSubmit(setup.Service, invocation.Context.Invocation).IsSuccess);
    }

    private sealed class DonationClock : TimeProvider
    {
        internal Action? Callback;
        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Exchange(ref Callback, null)?.Invoke();
            return DateTimeOffset.UnixEpoch;
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void InitialDonationFinalOwnerLossRefusesBindingAndCancelsExactLocalFacts(int fault)
    {
        Setup setup = Create();
        InlineSipInvocationLease invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        ResourceDonationBinding? prepared = null;
        setup.Kernel.BeforeDonationPublicationQualificationHook = binding =>
        {
            prepared = binding;
            if (fault == 0) Assert.True(setup.Kernel.CapabilityAuthority.Revoke(setup.SourceGrant).IsSuccess);
            if (fault == 1) Assert.True(setup.Kernel.CapabilityAuthority.Revoke(binding.DerivedGrant).IsSuccess);
            if (fault == 2) Assert.True(setup.Kernel.TerminateProcess(setup.Caller).IsSuccess);
            if (fault == 3) Assert.True(setup.Kernel.TerminateProcess(setup.Service).IsSuccess);
            if (fault == 4) Assert.True(setup.Kernel.Budgets.CancelLeasePreSubmit(setup.Caller, binding.Lease).IsSuccess);
            if (fault == 5) throw new InvalidOperationException("Interrupted publication.");
            if (fault == 6)
            {
                invocation.Dispose();
                Assert.True(setup.Kernel.CloseSession(setup.Caller, setup.Session).IsSuccess);
            }
        };
        KernelResult<ResourceDonationBinding> result = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(10), Envelope(10), AdmissionQosHint.None);
        Assert.False(result.IsSuccess);
        Assert.NotNull(prepared);
        Assert.False(setup.Kernel.MarkResourceDonationPossibleSubmit(setup.Service, invocation.Context.Invocation).IsSuccess);
        Assert.Equal(BudgetReservationState.CancelledPreSubmit, setup.Kernel.QueryBudget(prepared.Lease).Value!.State);
        Assert.Equal(0UL, Used(setup));
        Assert.False(setup.Kernel.CapabilityAuthority.ValidateResourceUse(prepared.DerivedGrant, setup.ServiceDomain,
            setup.Service.Generation, 1, Envelope(10)).IsSuccess);
        if (fault is 4 or 5)
        {
            setup.Kernel.BeforeDonationPublicationQualificationHook = null;
            Assert.True(setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
                invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(10), Envelope(10), AdmissionQosHint.None).IsSuccess);
            Assert.Equal(10UL, Used(setup));
        }
    }

    [Fact]
    public void InitialDonationPreparationCallbackRunsOutsideCorrelationLockAndRefusesReentry()
    {
        Setup setup = Create();
        InlineSipInvocationLease invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        setup.Kernel.BeforeDonationPublicationQualificationHook = binding =>
        {
            Task<KernelResult<ResourceDonationBinding>> reentry = Task.Run(() => setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
                invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(10), Envelope(10), AdmissionQosHint.None));
            Assert.True(reentry.Wait(TimeSpan.FromSeconds(5)), "Donation callback held correlation owner lock.");
            Assert.Equal(KernelError.InvalidTransition, reentry.Result.Error);
            Assert.Equal(10UL, Used(setup));
        };
        Assert.True(setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(10), Envelope(10), AdmissionQosHint.None).IsSuccess);
        Assert.Equal(10UL, Used(setup));
    }

    [Fact]
    public void RevokedDonationCannotBeginConsumptionAndBudgetRemainsReserved()
    {
        Setup setup = Create();
        InlineSipInvocationLease invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        ResourceDonationBinding donation = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(10), Envelope(10), AdmissionQosHint.None).Value!;
        Assert.True(setup.Kernel.CapabilityAuthority.Revoke(donation.DerivedGrant).IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, setup.Kernel.MarkResourceDonationPossibleSubmit(
            setup.Service, invocation.Context.Invocation).Error);
        Assert.Equal(BudgetReservationState.Reserved, setup.Kernel.QueryBudget(donation.Lease).Value!.State);
    }

    [Fact]
    public async Task ConcurrentInitialDonationBindingHasOneReservationAndOneDerivedRecord()
    {
        Setup setup = Create();
        InlineSipInvocationLease invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        int records = setup.Kernel.CapabilityAuthority.InspectionSnapshot().Length;
        TaskCompletionSource start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<KernelResult<ResourceDonationBinding>> Bind() => Task.Run(async () =>
        {
            await start.Task;
            return setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
                invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(10), Envelope(10), AdmissionQosHint.None);
        });
        Task<KernelResult<ResourceDonationBinding>> first = Bind();
        Task<KernelResult<ResourceDonationBinding>> second = Bind();
        start.SetResult();
        KernelResult<ResourceDonationBinding>[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal(KernelError.InvalidTransition, Assert.Single(results, result => !result.IsSuccess).Error);
        Assert.Equal(records + 1, setup.Kernel.CapabilityAuthority.InspectionSnapshot().Length);
        Assert.Equal(10UL, Used(setup));
    }

    [Fact]
    public async Task ConcurrentDuplicateNestedDonationHasOneSplitAndPreservesHeldCapacity()
    {
        for (int iteration = 0; iteration < 16; iteration++)
        {
            Setup setup = Create(includeDownstream: true);
            InlineSipInvocationLease parentInvocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
            ResourceDonationBinding parent = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
                parentInvocation.Context.Invocation, setup.SourceGrant, 1, Requirement(100), Envelope(100),
                AdmissionQosHint.LatencySensitive).Value!;
            InlineSipInvocationLease childInvocation = Begin(setup.Kernel, setup.Service, setup.Downstream, setup.DownstreamSession);
            TaskCompletionSource start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<KernelResult<ResourceDonationBinding>> Split() => Task.Run(async () =>
            {
                await start.Task;
                return setup.Kernel.DeriveNestedResourceDonation(setup.Service, setup.Downstream,
                    parentInvocation.Context.Invocation, childInvocation.Context.Invocation, Envelope(30),
                    ResourceAssuranceV1.AccountingOnly, AdmissionQosHint.ThroughputOriented);
            });
            Task<KernelResult<ResourceDonationBinding>> first = Split();
            Task<KernelResult<ResourceDonationBinding>> second = Split();
            start.SetResult();
            KernelResult<ResourceDonationBinding>[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Single(results.Where(result => result.IsSuccess));
            Assert.Equal(KernelError.InvalidTransition, Assert.Single(results.Where(result => !result.IsSuccess)).Error);
            Assert.Equal(100UL, Used(setup));
            Assert.Equal(70UL, Assert.Single(setup.Kernel.QueryBudget(parent.Lease).Value!.Amounts).Amount);
        }
    }

    [Theory]
    [InlineData(0, 1UL)] [InlineData(1, 1UL)] [InlineData(2, 1UL)] [InlineData(3, 1UL)] [InlineData(4, 1UL)]
    [InlineData(0, 7UL)] [InlineData(1, 7UL)] [InlineData(2, 7UL)] [InlineData(3, 7UL)] [InlineData(4, 7UL)]
    public void GeneratedSentryConsumesExactInvocationDonationWithoutSecondCharge(int revokePoint, ulong resourceGeneration)
    {
        Setup setup = Create(resourceGeneration: resourceGeneration);
        InlineSipInvocationLease invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        ResourceDonationBinding donation = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, resourceGeneration, Requirement(10), Envelope(10),
            AdmissionQosHint.None).Value!;
        CapabilityId effect = setup.Kernel.CapabilityAuthority.Mint(setup.ServiceDomain, setup.ServiceDomain,
            ResourceKind.Compute, "compute:effect", CapabilityRights.Execute, setup.Service.Generation,
            1, range: null, session: null, quota: 1, delegationDepth: 0).Value!.CapabilityId;
        OwnedBuffer<byte> region = setup.Kernel.AllocateBuffer<byte>(setup.Service, 8).Value!;
        ExternalOperationHandle operation = setup.Kernel.PrepareExternalOperation(setup.Service,
            [new(region.Handle, RegionUseMode.ReadOnly, new(0, 8))],
            ExternalVisibilityRequirement.None, ExternalPublicationPolicy.Staged).Value!.Operation;
        SipResourceAdmissionBinding binding = new SipResourceAdmissionBinding(effect, ResourceKind.Compute, "compute:effect", 1,
            default, 1, "host:compute-v1", operation, new(1, 1));
        TrustedSipInvocationContext context = new TrustedSipInvocationContext(setup.Caller, setup.Service, setup.Session,
            invocation.Context.Invocation, requirement => setup.Kernel.EnterDonatedSipResourceAdmission(
                setup.Service, invocation.Context.Invocation, binding, requirement));
        DonatedTarget target = new DonatedTarget();
        if (revokePoint != 0)
            setup.Kernel.ResourceAdmissionQualificationHook = new Hook(point =>
            {
                if (point == (revokePoint is 1 or 4 ? ResourceAdmissionQualificationPoint.AfterLocalCommit : ResourceAdmissionQualificationPoint.BeforeProviderCallback))
                {
                    if (revokePoint >= 3)
                        Assert.True(setup.Kernel.CancelExternalOperation(setup.Service, operation,
                            providerCancellationSupported: false).IsSuccess);
                    else Assert.True(setup.Kernel.CapabilityAuthority.Revoke(donation.DerivedGrant).IsSuccess);
                }
            });

        GeneratedSipSentryResult<int> result = IVNextP06DonatedResourceServiceGeneratedOperationSentries.InvokeRuntime_Run(
            target, in context);

        if (revokePoint != 0)
        {
            Assert.False(result.IsSuccess);
            Assert.Equal((int)(revokePoint >= 3 ? KernelError.StaleGeneration : KernelError.CapabilityRevoked), result.ErrorCode);
            Assert.Equal(0, target.ProviderCalls);
            Assert.Equal(revokePoint is 1 or 4 ? BudgetReservationState.CancelledPreSubmit : BudgetReservationState.Quarantined,
                setup.Kernel.QueryBudget(donation.Lease).Value!.State);
            if (revokePoint == 3)
            {
                Assert.NotNull(setup.Kernel.QueryExternalOperation(setup.Service, operation).Value!.Binding);
                Assert.Equal(ExternalResourceBindingState.Quarantined,
                    setup.Kernel.ExternalOperations.QueryResourceBinding(operation).Value!.State);
                Assert.Equal(10UL, Used(setup));
            }
            if (revokePoint == 4)
                Assert.Null(setup.Kernel.QueryExternalOperation(setup.Service, operation).Value!.Binding);
            return;
        }
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, target.ProviderCalls);
        Assert.Equal(10UL, Used(setup));
        Assert.Equal(BudgetReservationState.Consuming,
            setup.Kernel.QueryBudget(donation.Lease).Value!.State);
        Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Service, invocation, true).IsSuccess);
        Assert.Equal(10UL, Used(setup));
        Assert.Equal(BudgetReservationState.Quarantined,
            setup.Kernel.QueryBudget(donation.Lease).Value!.State);
    }

    [Fact]
    public void GeneratedSubmitBlocksLatePreSubmitReturnDuringSessionDrain()
    {
        Setup setup = Create();
        InlineSipInvocationLease invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        ResourceDonationBinding donation = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(10), Envelope(10),
            AdmissionQosHint.None).Value!;
        CapabilityId effect = setup.Kernel.CapabilityAuthority.Mint(setup.ServiceDomain, setup.ServiceDomain,
            ResourceKind.Compute, "compute:effect", CapabilityRights.Execute, setup.Service.Generation,
            1, range: null, session: null, quota: 1, delegationDepth: 0).Value!.CapabilityId;
        OwnedBuffer<byte> region = setup.Kernel.AllocateBuffer<byte>(setup.Service, 8).Value!;
        ExternalOperationHandle operation = setup.Kernel.PrepareExternalOperation(setup.Service,
            [new(region.Handle, RegionUseMode.ReadOnly, new(0, 8))],
            ExternalVisibilityRequirement.None, ExternalPublicationPolicy.Staged).Value!.Operation;
        SipResourceAdmissionBinding binding = new SipResourceAdmissionBinding(effect, ResourceKind.Compute, "compute:effect", 1,
            default, 1, "host:compute-v1", operation, new(1, 1));
        TrustedSipInvocationContext context = new TrustedSipInvocationContext(setup.Caller, setup.Service, setup.Session,
            invocation.Context.Invocation, requirement => setup.Kernel.EnterDonatedSipResourceAdmission(
                setup.Service, invocation.Context.Invocation, binding, requirement));
        KernelResult drain = default;
        KernelResult lateReturn = default;
        setup.Kernel.ResourceAdmissionQualificationHook = new Hook(point =>
        {
            if (point != ResourceAdmissionQualificationPoint.BeforeProviderCallback) return;
            drain = setup.Kernel.CloseSession(setup.Caller, setup.Session);
            lateReturn = setup.Kernel.CloseResourceDonation(
                setup.Service, invocation.Context.Invocation, submitMayHaveOccurred: false);
        });

        DonatedTarget target = new DonatedTarget();
        GeneratedSipSentryResult<int> result = IVNextP06DonatedResourceServiceGeneratedOperationSentries.InvokeRuntime_Run(
            target, in context);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(KernelError.SessionDraining, drain.Error);
        Assert.Equal(KernelError.InvalidTransition, lateReturn.Error);
        Assert.Equal(1, target.ProviderCalls);
        Assert.Equal(BudgetReservationState.Consuming,
            setup.Kernel.QueryBudget(donation.Lease).Value!.State);
        Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Service, invocation, false).IsSuccess);
        Assert.Equal(BudgetReservationState.Quarantined,
            setup.Kernel.QueryBudget(donation.Lease).Value!.State);
        Assert.Equal(KernelError.PlatformBindingDraining,
            setup.Kernel.TerminateProcess(setup.Service).Error);
    }

    [Fact]
    public void SessionCloseWithoutLivePinQuarantinesBoundDonationInsteadOfAssumingPreSubmitClosure()
    {
        Setup setup = Create();
        InlineSipInvocationLease invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        ResourceDonationBinding donation = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(10), Envelope(10),
            AdmissionQosHint.None).Value!;
        invocation.Dispose();

        Assert.True(setup.Kernel.CloseSession(setup.Caller, setup.Session).IsSuccess);
        Assert.Equal(BudgetReservationState.Quarantined,
            setup.Kernel.QueryBudget(donation.Lease).Value!.State);
        Assert.Equal(KernelError.InvalidTransition,
            setup.Kernel.CloseResourceDonation(setup.Service, invocation.Context.Invocation,
                submitMayHaveOccurred: false).Error);
        Assert.Equal(KernelError.PlatformBindingDraining,
            setup.Kernel.TerminateProcess(setup.Service).Error);
    }

    [Fact]
    public void GeneratedSubmitAfterSessionCloseWithoutPinCannotRefundOrCallProvider()
    {
        Setup setup = Create();
        InlineSipInvocationLease invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        ResourceDonationBinding donation = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(10), Envelope(10),
            AdmissionQosHint.None).Value!;
        CapabilityId effect = setup.Kernel.CapabilityAuthority.Mint(setup.ServiceDomain, setup.ServiceDomain,
            ResourceKind.Compute, "compute:effect", CapabilityRights.Execute, setup.Service.Generation,
            1, range: null, session: null, quota: 1, delegationDepth: 0).Value!.CapabilityId;
        OwnedBuffer<byte> region = setup.Kernel.AllocateBuffer<byte>(setup.Service, 8).Value!;
        ExternalOperationHandle operation = setup.Kernel.PrepareExternalOperation(setup.Service,
            [new(region.Handle, RegionUseMode.ReadOnly, new(0, 8))],
            ExternalVisibilityRequirement.None, ExternalPublicationPolicy.Staged).Value!.Operation;
        SipResourceAdmissionBinding binding = new SipResourceAdmissionBinding(effect, ResourceKind.Compute, "compute:effect", 1,
            default, 1, "host:compute-v1", operation, new(1, 1));
        using GeneratedSipResourceAdmission admission = setup.Kernel.EnterDonatedSipResourceAdmission(
            setup.Service, invocation.Context.Invocation, binding, Requirement(10));
        Assert.True(admission.IsSuccess, admission.Message);
        invocation.Dispose();
        int providerCalls = 0;

        Assert.True(setup.Kernel.CloseSession(setup.Caller, setup.Session).IsSuccess);
        GeneratedSipSubmitResult submitted = admission.Submit(() =>
        {
            providerCalls++;
            return GeneratedSipSubmitResult.Ok();
        });

        Assert.False(submitted.IsSuccess);
        Assert.Equal(0, providerCalls);
        Assert.Equal(BudgetReservationState.Quarantined,
            setup.Kernel.QueryBudget(donation.Lease).Value!.State);
        Assert.Equal(KernelError.InvalidTransition,
            setup.Kernel.CloseResourceDonation(setup.Service, invocation.Context.Invocation,
                submitMayHaveOccurred: false).Error);
        Assert.Equal(KernelError.PlatformBindingDraining,
            setup.Kernel.TerminateProcess(setup.Service).Error);
    }

    [Fact]
    public void DonationIsInvocationScopedNarrowedAndChargedToCaller()
    {
        Setup setup = Create();
        InlineSipInvocationLease invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);

        KernelResult<ResourceDonationBinding> donation = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(80), Envelope(80),
            AdmissionQosHint.BoundedInteractive);

        Assert.True(donation.IsSuccess, donation.Message);
        Assert.Equal(setup.Caller, donation.Value!.ChargingOwner);
        Assert.Equal(80UL, Used(setup));
        Assert.True(setup.Kernel.CapabilityAuthority.ValidateResourceUse(donation.Value.DerivedGrant,
            setup.ServiceDomain, setup.Service.Generation, 1, Envelope(80)).IsSuccess);
        Assert.Equal(KernelError.InvalidTransition,
            setup.Kernel.BindResourceDonation(setup.Caller, setup.Service, invocation.Context.Invocation,
                setup.SourceGrant, 1, Requirement(10), Envelope(10), AdmissionQosHint.None).Error);
        Assert.Equal(80UL, Used(setup));

        Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Service, invocation, succeeded: false).IsSuccess);
        Assert.Equal(0UL, Used(setup));
        Assert.Equal(KernelError.CapabilityRevoked,
            setup.Kernel.CapabilityAuthority.ValidateResourceUse(donation.Value.DerivedGrant,
                setup.ServiceDomain, setup.Service.Generation, 1, Envelope(1)).Error);
    }

    [Fact]
    public void NestedDonationPreservesProvenanceAndConservesOneLeaseLineage()
    {
        Setup setup = Create(includeDownstream: true);
        InlineSipInvocationLease parentInvocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        ResourceDonationBinding parent = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            parentInvocation.Context.Invocation, setup.SourceGrant, 1, Requirement(100), Envelope(100),
            AdmissionQosHint.LatencySensitive).Value!;
        InlineSipInvocationLease childInvocation = Begin(setup.Kernel, setup.Service, setup.Downstream, setup.DownstreamSession);

        KernelResult<ResourceDonationBinding> child = setup.Kernel.DeriveNestedResourceDonation(setup.Service, setup.Downstream,
            parentInvocation.Context.Invocation, childInvocation.Context.Invocation, Envelope(30),
            ResourceAssuranceV1.AccountingOnly, AdmissionQosHint.ThroughputOriented);

        Assert.True(child.IsSuccess, child.Message);
        Assert.Equal(setup.Caller, child.Value!.ChargingOwner);
        Assert.Equal(
            $"{parent.Provenance}/{setup.Service.ProcessId.Value}:{setup.Service.Generation}->{setup.Downstream.ProcessId.Value}:{setup.Downstream.Generation}@{childInvocation.Context.Invocation.Session.SessionId.Value}:{childInvocation.Context.Invocation.Session.Generation.Value}:{childInvocation.Context.Invocation.InvocationId.Value}:{childInvocation.Context.Invocation.Generation.Value}",
            child.Value.Provenance);
        Assert.Equal(100UL, Used(setup));
        Assert.Equal(70UL, Assert.Single(setup.Kernel.QueryBudget(parent.Lease).Value!.Amounts).Amount);
        Assert.Equal(30UL, Assert.Single(setup.Kernel.QueryBudget(child.Value.Lease).Value!.Amounts).Amount);
        ulong beforeDuplicate = Used(setup);
        Assert.Equal(KernelError.InvalidTransition,
            setup.Kernel.DeriveNestedResourceDonation(setup.Service, setup.Downstream,
                parentInvocation.Context.Invocation, childInvocation.Context.Invocation, Envelope(10),
                ResourceAssuranceV1.AccountingOnly, AdmissionQosHint.ThroughputOriented).Error);
        Assert.Equal(beforeDuplicate, Used(setup));
        Assert.Equal(70UL, Assert.Single(setup.Kernel.QueryBudget(parent.Lease).Value!.Amounts).Amount);
        Assert.True(setup.Kernel.CapabilityAuthority.ValidateResourceUse(child.Value.DerivedGrant,
            setup.DownstreamDomain, setup.Downstream.Generation, 1, Envelope(30)).IsSuccess);
        Assert.Equal(KernelError.DelegationDenied,
            setup.Kernel.DeriveNestedResourceDonation(setup.Service, setup.Downstream,
                parentInvocation.Context.Invocation, childInvocation.Context.Invocation, Envelope(31),
                ResourceAssuranceV1.GuaranteedReservation, AdmissionQosHint.LatencySensitive).Error);

        Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Downstream, childInvocation, false).IsSuccess);
        Assert.Equal(70UL, Used(setup));
        Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Service, parentInvocation, false).IsSuccess);
        Assert.Equal(0UL, Used(setup));
    }

    [Fact]
    public async Task ParallelNestedSplitsNeverAmplifyCallerCapacity()
    {
        Setup setup = Create(includeDownstream: true);
        InlineSipInvocationLease parentInvocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        ResourceDonationBinding parent = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            parentInvocation.Context.Invocation, setup.SourceGrant, 1, Requirement(100), Envelope(100),
            AdmissionQosHint.LatencySensitive).Value!;
        InlineSipInvocationLease[] invocations = Enumerable.Range(0, 12)
            .Select(_ => Begin(setup.Kernel, setup.Service, setup.Downstream, setup.DownstreamSession)).ToArray();
        using ManualResetEventSlim start = new ManualResetEventSlim(false);
        Task<KernelResult<ResourceDonationBinding>>[] attempts = invocations.Select(invocation => Task.Run(() =>
        {
            start.Wait();
            return setup.Kernel.DeriveNestedResourceDonation(setup.Service, setup.Downstream,
                parentInvocation.Context.Invocation, invocation.Context.Invocation, Envelope(10),
                ResourceAssuranceV1.AccountingOnly, AdmissionQosHint.Background);
        })).ToArray();
        start.Set();
        KernelResult<ResourceDonationBinding>[] results = await Task.WhenAll(attempts);

        Assert.Equal(9, results.Count(result => result.IsSuccess));
        Assert.All(results.Where(result => !result.IsSuccess),
            result => Assert.Equal(KernelError.BudgetExceeded, result.Error));
        Assert.Equal(100UL, Used(setup));
        Assert.Equal(10UL, Assert.Single(setup.Kernel.QueryBudget(parent.Lease).Value!.Amounts).Amount);

        foreach ((InlineSipInvocationLease First, KernelResult<ResourceDonationBinding> Second) pair in invocations.Zip(results))
            Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Downstream, pair.First, false).IsSuccess);
        Assert.Equal(10UL, Used(setup));
        Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Service, parentInvocation, false).IsSuccess);
        Assert.Equal(0UL, Used(setup));
    }

    [Fact]
    public void PossibleSubmitAndSessionLossQuarantineInsteadOfRefund()
    {
        Setup setup = Create();
        InlineSipInvocationLease invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        ResourceDonationBinding donation = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(40), Envelope(40),
            AdmissionQosHint.None).Value!;

        Assert.True(setup.Kernel.MarkResourceDonationPossibleSubmit(setup.Service,
            invocation.Context.Invocation).IsSuccess);
        Assert.Equal(KernelError.SessionDraining,
            setup.Kernel.CloseSession(setup.Caller, setup.Session).Error);
        Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Service, invocation, false).IsSuccess);

        Assert.Equal(40UL, Used(setup));
        Assert.Equal(BudgetReservationState.Quarantined,
            setup.Kernel.QueryBudget(donation.Lease).Value!.State);
        Assert.Equal(KernelError.CapabilityRevoked,
            setup.Kernel.CapabilityAuthority.ValidateResourceUse(donation.DerivedGrant,
                setup.ServiceDomain, setup.Service.Generation, 1, Envelope(1)).Error);
    }

    [Fact]
    public void PossibleSubmitCannotDowngradeDonationToPreSubmitReturn()
    {
        Setup setup = Create();
        InlineSipInvocationLease invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        ResourceDonationBinding donation = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(40), Envelope(40),
            AdmissionQosHint.None).Value!;
        Assert.True(setup.Kernel.MarkResourceDonationPossibleSubmit(
            setup.Service, invocation.Context.Invocation).IsSuccess);

        Assert.Equal(KernelError.InvalidTransition,
            setup.Kernel.CloseResourceDonation(setup.Service, invocation.Context.Invocation,
                submitMayHaveOccurred: false).Error);
        Assert.Equal(BudgetReservationState.Consuming,
            setup.Kernel.QueryBudget(donation.Lease).Value!.State);
        Assert.True(setup.Kernel.CloseResourceDonation(setup.Service, invocation.Context.Invocation,
            submitMayHaveOccurred: true).IsSuccess);
        Assert.Equal(KernelError.InvalidTransition,
            setup.Kernel.CloseResourceDonation(setup.Service, invocation.Context.Invocation,
                submitMayHaveOccurred: false).Error);
        Assert.Equal(BudgetReservationState.Quarantined,
            setup.Kernel.QueryBudget(donation.Lease).Value!.State);

        Assert.Equal(KernelError.SessionDraining,
            setup.Kernel.CloseSession(setup.Caller, setup.Session).Error);
        Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Service, invocation, false).IsSuccess);
        Assert.Equal(40UL, Used(setup));
        Assert.Equal(KernelError.PlatformBindingDraining,
            setup.Kernel.TerminateProcess(setup.Service).Error);
    }

    [Fact]
    public async Task PossibleSubmitAndPreSubmitReturnHaveOneDonationWinner()
    {
        for (int iteration = 0; iteration < 32; iteration++)
        {
            Setup setup = Create();
            InlineSipInvocationLease invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
            ResourceDonationBinding donation = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
                invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(40), Envelope(40),
                AdmissionQosHint.None).Value!;
            TaskCompletionSource<bool> start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<KernelResult> submit = Task.Run(async () =>
            {
                await start.Task;
                return setup.Kernel.MarkResourceDonationPossibleSubmit(
                    setup.Service, invocation.Context.Invocation);
            });
            Task<KernelResult> close = Task.Run(async () =>
            {
                await start.Task;
                return setup.Kernel.CloseResourceDonation(
                    setup.Service, invocation.Context.Invocation, submitMayHaveOccurred: false);
            });
            start.SetResult(true);
            await Task.WhenAll(submit, close).WaitAsync(TimeSpan.FromSeconds(5));

            KernelResult submitted = await submit;
            KernelResult closed = await close;
            Assert.NotEqual(submitted.IsSuccess, closed.IsSuccess);
            BudgetReservationState state = setup.Kernel.QueryBudget(donation.Lease).Value!.State;
            Assert.Equal(submitted.IsSuccess
                ? BudgetReservationState.Consuming
                : BudgetReservationState.CancelledPreSubmit, state);
            Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Service, invocation, false).IsSuccess);
        }
    }

    [Fact]
    public void StaleIdentityWideningAndAmbientReuseFailClosedBeforeCharging()
    {
        Setup setup = Create();
        InlineSipInvocationLease invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        int capabilityRecords = setup.Kernel.CapabilityAuthority.InspectionSnapshot().Length;
        Assert.Equal(KernelError.StaleGeneration,
            setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
                invocation.Context.Invocation with { Generation = new(2) }, setup.SourceGrant, 1,
                Requirement(10), Envelope(10), AdmissionQosHint.None).Error);
        Assert.Equal(capabilityRecords, setup.Kernel.CapabilityAuthority.InspectionSnapshot().Length);
        Assert.Equal(KernelError.InsufficientRights,
            setup.Kernel.BindResourceDonation(setup.Caller, setup.Service, invocation.Context.Invocation,
                setup.SourceGrant, 1, Requirement(101), Envelope(101), AdmissionQosHint.None).Error);
        Assert.Equal(0UL, Used(setup));

        ResourceDonationBinding donated = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(10), Envelope(10),
            AdmissionQosHint.None).Value!;
        InlineSipInvocationLease second = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        Assert.Equal(KernelError.InvalidTransition,
            setup.Kernel.CloseResourceDonation(setup.Service, second.Context.Invocation,
                submitMayHaveOccurred: false).Error);
        Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Service, second, false).IsSuccess);
        Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Service, invocation, false).IsSuccess);
    }

    private static Setup Create(bool includeDownstream = false, TimeProvider? timeProvider = null, ulong resourceGeneration = 1)
    {
        RuntimeKernel kernel = new RuntimeKernel(null, timeProvider);
        ProcessHandle admin = TestFixtures.Create(kernel, 960, 1960).Handle;
        ProcessHandle caller = TestFixtures.Create(kernel, 961, 1961).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 962, 1962).Handle;
        ProcessHandle downstream = TestFixtures.Create(kernel, 963, 1963).Handle;
        CapabilityId administration = kernel.MintCapability(new(1960), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, administration, caller, "p06",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).Value!.ProcessBudget;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, service, "p06-service",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).IsSuccess);
        CapabilityId source = kernel.CapabilityAuthority.Mint(new(1961), new(1961), ResourceKind.Compute,
            "resource-use:compute-time", CapabilityRights.Delegate, caller.Generation, resourceGeneration,
            range: null, session: null, quota: 100, delegationDepth: 4,
            resourceUse: new(1, Envelope(100), 0, long.MaxValue,
                ResourceAssuranceV1.RuntimeEnforced, 3)).Value!.CapabilityId;
        EndpointSessionHandle session = Open(kernel, caller, service, "p06-main");
        EndpointSessionHandle downstreamSession = includeDownstream
            ? Open(kernel, service, downstream, "p06-downstream")
            : default;
        return new(kernel, caller, service, downstream, new(1962), new(1963), session,
            downstreamSession, source, budget);
    }

    private static EndpointSessionHandle Open(RuntimeKernel kernel, ProcessHandle caller,
        ProcessHandle service, string name)
    {
        ProtocolDefinitionV1 protocol = new ProtocolDefinitionV1(name, name + "-digest", "Ready", terminalStates: null,
            messages: [new ProtocolMessageDescriptorV1(1, "Invoke",
                requestPayload: new RequestPayloadDescriptorV1(RequestPayloadKind.Primitive,
                    "value", typeof(int).FullName!))],
            transitions: [new ProtocolTransitionV1(1, "Ready", "Ready")]);
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(service, name,
            new(protocol.ContractName, "1", protocol.ContractDigest), protocol).Value!;
        return kernel.OpenSession(caller, descriptor).Value;
    }

    private static InlineSipInvocationLease Begin(RuntimeKernel kernel, ProcessHandle caller,
        ProcessHandle service, EndpointSessionHandle session) =>
        kernel.BeginInlineSessionInvocation(caller, service, session, 1, 1).Value!;

    private static SipResourceRequirementV1 Requirement(ulong amount) => new(1,
        ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds, amount, "host:compute-v1",
        ResourceAssuranceV1.RuntimeEnforced, SipResourceDonationPolicyV1.AcceptNarrowed);

    private static ResourceEnvelopeV1 Envelope(ulong amount) => new(1,
        ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
        ResourceUnitV1.Nanoseconds, amount, 0, "host:compute-v1");

    private static ulong Used(Setup setup) => Assert.Single(
        setup.Kernel.QueryBudget(setup.Budget).Value!.Usage,
        usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used;

    private sealed record Setup(RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service,
        ProcessHandle Downstream, DomainId ServiceDomain, DomainId DownstreamDomain,
        EndpointSessionHandle Session, EndpointSessionHandle DownstreamSession,
        CapabilityId SourceGrant, BudgetAccountHandle Budget);

    private sealed class DonatedTarget : IIVNextP06DonatedResourceServiceGeneratedSentryTarget_Run
    {
        internal int ProviderCalls { get; private set; }

        public GeneratedSipSentryResult<int> Sentry_Run(in TrustedSipInvocationContext context,
            GeneratedSipResourceAdmission resourceAdmission)
        {
            GeneratedSipSubmitResult submitted = resourceAdmission.Submit(() =>
            {
                ProviderCalls++;
                return GeneratedSipSubmitResult.Ok();
            });
            return submitted.IsSuccess
                ? GeneratedSipSentryResult<int>.Success(1)
                : GeneratedSipSentryResult<int>.Failure(submitted.ErrorCode, submitted.Message!);
        }
    }

    private sealed class Hook(Action<ResourceAdmissionQualificationPoint> action) : IResourceAdmissionQualificationHook
    {
        public void At(ResourceAdmissionQualificationPoint point) => action(point);
    }
}
