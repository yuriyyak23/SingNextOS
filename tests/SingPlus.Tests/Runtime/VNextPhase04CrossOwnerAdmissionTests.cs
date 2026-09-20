using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase04CrossOwnerAdmissionTests
{
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
        var callbacks = 0;

        var result = context.Kernel.SubmitResourceExternalAdmission(commit, context.Dependencies, () =>
        {
            callbacks++;
            return KernelResult.Ok();
        });

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, callbacks);
        Assert.Equal(10UL, Used(context));
        Assert.Equal(BudgetReservationState.Consuming,
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

    private static Context Create()
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 900, 1900).Handle;
        var target = TestFixtures.Create(kernel, 901, 1901).Handle;
        var administration = kernel.MintCapability(new(1900), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, target, "p04",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100), new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!;
        var effect = kernel.MintCapability(new(1901), target, ResourceKind.Compute,
            "compute:effect", CapabilityRights.Execute, resourceGeneration: 1).Value!.CapabilityId;
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
