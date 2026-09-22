using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase07ExternalOperationResourceBindingTests
{
    [Fact]
    public void ExactCompletionReceiptSettlesQuantityWithoutPublishingOrReleasingRegion()
    {
        var setup = Create();
        using var commit = Prepare(setup, setup.Operation).Value!;
        var submitted = setup.Kernel.SubmitResourceExternalAdmission(commit, setup.Dependencies, KernelResult.Ok).Value!;
        Assert.True(setup.Kernel.RecordExternalOperationCompletion(setup.Process,
            new(submitted, ExternalOperationCompletionDisposition.Completed)).IsSuccess);

        var settled = setup.Kernel.SettleResourceExternalOperation(setup.Process,
            Evidence(submitted, consumed: 6, sequence: 1));

        Assert.True(settled.IsSuccess, settled.Message);
        Assert.Equal(BudgetReservationState.Released, settled.Value!.State);
        Assert.Equal(6UL, Used(setup));
        var operation = setup.Kernel.QueryExternalOperation(setup.Process, setup.Operation).Value!;
        Assert.Equal(ExternalOperationState.DeviceComplete, operation.State);
        Assert.Equal(KernelError.InvalidTransition,
            setup.Kernel.PublishExternalOperation(setup.Process, setup.Operation, setup.Dependencies,
                new(ExternalPublicationPolicy.Staged), () => { }).Error);
        Assert.Equal(KernelError.RegionUseConflict,
            setup.Kernel.Regions.Release(setup.Region, new(setup.Domain, setup.Process.Generation)).Error);
    }

    [Fact]
    public void DuplicateReceiptIsIdempotentButForgedCrossOperationAndProviderDriftFailClosed()
    {
        var setup = Create();
        using var first = Prepare(setup, setup.Operation).Value!;
        var firstBinding = setup.Kernel.SubmitResourceExternalAdmission(first, setup.Dependencies, KernelResult.Ok).Value!;
        Assert.True(setup.Kernel.RecordExternalOperationCompletion(setup.Process,
            new(firstBinding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        var evidence = Evidence(firstBinding, 7, 11);
        Assert.True(setup.Kernel.SettleResourceExternalOperation(setup.Process, evidence).IsSuccess);
        Assert.True(setup.Kernel.SettleResourceExternalOperation(setup.Process, evidence).IsSuccess);
        Assert.Equal(7UL, Used(setup));

        var secondRegion = setup.Kernel.AllocateBuffer<byte>(setup.Process, 8).Value!;
        var secondOperation = setup.Kernel.PrepareExternalOperation(setup.Process,
            [new(secondRegion.Handle, RegionUseMode.ReadOnly, new(0, 8))],
            ExternalVisibilityRequirement.None, ExternalPublicationPolicy.Staged).Value!.Operation;
        using var second = Prepare(setup, secondOperation).Value!;
        var secondBinding = setup.Kernel.SubmitResourceExternalAdmission(second, setup.Dependencies, KernelResult.Ok).Value!;
        Assert.True(setup.Kernel.RecordExternalOperationCompletion(setup.Process,
            new(secondBinding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);

        var crossOperation = Evidence(new(secondBinding.Operation, firstBinding.BindingId,
            firstBinding.Generation), 1, 12);
        Assert.Equal(KernelError.StaleGeneration,
            setup.Kernel.SettleResourceExternalOperation(setup.Process, crossOperation).Error);
        Assert.Equal(KernelError.StaleGeneration,
            setup.Kernel.SettleResourceExternalOperation(setup.Process,
                Evidence(secondBinding, 1, 13) with { ProviderGeneration = 2 }).Error);
        Assert.Equal(17UL, Used(setup));
    }

    [Fact]
    public void ReorderedOrDimensionChangingEvidenceCannotDriveSettlement()
    {
        var setup = Create();
        using var commit = Prepare(setup, setup.Operation).Value!;
        var binding = setup.Kernel.SubmitResourceExternalAdmission(commit, setup.Dependencies, KernelResult.Ok).Value!;

        Assert.Equal(KernelError.InvalidTransition,
            setup.Kernel.SettleResourceExternalOperation(setup.Process, Evidence(binding, 5, 1)).Error);
        Assert.True(setup.Kernel.RecordExternalOperationCompletion(setup.Process,
            new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.Equal(KernelError.InvalidMessage,
            setup.Kernel.SettleResourceExternalOperation(setup.Process,
                Evidence(binding, 5, 2) with { Unit = ResourceUnitV1.Bytes }).Error);
        Assert.Equal(10UL, Used(setup));
        Assert.Equal(BudgetReservationState.Consuming, setup.Kernel.QueryBudget(commit.Lease).Value!.State);
    }

    [Fact]
    public async Task ConcurrentDuplicateEvidenceHasOneSettlementAndNoDoubleRefund()
    {
        var setup = Create();
        using var commit = Prepare(setup, setup.Operation).Value!;
        var binding = setup.Kernel.SubmitResourceExternalAdmission(commit, setup.Dependencies, KernelResult.Ok).Value!;
        Assert.True(setup.Kernel.RecordExternalOperationCompletion(setup.Process,
            new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        var evidence = Evidence(binding, 3, 1);
        using var start = new ManualResetEventSlim(false);
        var attempts = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return setup.Kernel.SettleResourceExternalOperation(setup.Process, evidence);
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Contains(results, result => result.IsSuccess);
        Assert.All(results, result => Assert.True(result.IsSuccess || result.Error == KernelError.InvalidTransition));
        Assert.Equal(3UL, Used(setup));
        Assert.Equal(BudgetReservationState.Released, setup.Kernel.QueryBudget(commit.Lease).Value!.State);
    }

    [Fact]
    public void ProviderLossAfterPossibleSubmitQuarantinesAndNeverRefunds()
    {
        var setup = Create();
        using var commit = Prepare(setup, setup.Operation).Value!;
        var submitted = setup.Kernel.SubmitResourceExternalAdmission(commit, setup.Dependencies, KernelResult.Ok);
        Assert.True(submitted.IsSuccess, submitted.Message);

        Assert.True(setup.Kernel.RecordExternalOperationProviderLoss(setup.Process, setup.Operation).IsSuccess);

        Assert.Equal(10UL, Used(setup));
        Assert.Equal(BudgetReservationState.Quarantined,
            setup.Kernel.QueryBudget(commit.Lease).Value!.State);
        Assert.Equal(ExternalResourceBindingState.Quarantined,
            setup.Kernel.ExternalOperations.QueryResourceBinding(setup.Operation).Value!.State);
    }

    [Fact]
    public void PublicationFailureAfterSettlementDoesNotUndoChargeOrReleaseRegion()
    {
        var setup = Create();
        using var commit = Prepare(setup, setup.Operation).Value!;
        var binding = setup.Kernel.SubmitResourceExternalAdmission(commit, setup.Dependencies, KernelResult.Ok).Value!;
        Assert.True(setup.Kernel.RecordExternalOperationCompletion(setup.Process,
            new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(setup.Kernel.RecordExternalOperationVisibility(setup.Process,
            new(binding, ExternalVisibilityRequirement.None, true)).IsSuccess);
        Assert.True(setup.Kernel.SettleResourceExternalOperation(setup.Process,
            Evidence(binding, 4, 1)).IsSuccess);

        Assert.Equal(KernelError.PlatformFaulted,
            setup.Kernel.PublishExternalOperation(setup.Process, setup.Operation, setup.Dependencies,
                new(ExternalPublicationPolicy.Staged), () => throw new InvalidOperationException("publish" )).Error);

        Assert.Equal(4UL, Used(setup));
        Assert.Equal(BudgetReservationState.Released, setup.Kernel.QueryBudget(commit.Lease).Value!.State);
        Assert.Equal(KernelError.RegionUseConflict,
            setup.Kernel.Regions.Release(setup.Region, new(setup.Domain, setup.Process.Generation)).Error);
    }

    [Fact]
    public void PreSubmitDisposalCancelsExactBindingAndRefundsOnce()
    {
        var setup = Create();
        var commit = Prepare(setup, setup.Operation).Value!;

        commit.Dispose();
        commit.Dispose();

        Assert.Equal(0UL, Used(setup));
        Assert.Equal(BudgetReservationState.CancelledPreSubmit,
            setup.Kernel.QueryBudget(commit.Lease).Value!.State);
        Assert.Equal(ExternalResourceBindingState.CancelledPreSubmit,
            setup.Kernel.ExternalOperations.QueryResourceBinding(setup.Operation).Value!.State);
    }

    [Fact]
    public void ChargeabilityMatrixIsDimensionSpecificAndRetireNeverAuthorizesACharge()
    {
        var compute = ResourceChargeabilityMatrixV1.For(ResourceClassV1.ComputeTime);
        var usage = new ResourceUsageBreakdownV1(1, 101, 102, 2, 3, 5, 107, 7, 109, 113);
        Assert.Equal(ResourceUnitV1.Nanoseconds, compute.Unit);
        Assert.Equal(17UL, compute.Normalize(usage));
        Assert.False(compute.ChargedEvents.HasFlag(ResourceChargeEventV1.Retired));
        Assert.False(compute.ChargedEvents.HasFlag(ResourceChargeEventV1.StalledOrBlocked));
        Assert.False(compute.IsAuthority);
        Assert.False(compute.IsReservationGuarantee);

        Assert.Equal(7UL, ResourceChargeabilityMatrixV1.For(ResourceClassV1.DmaThroughput).Normalize(usage));
        Assert.Equal(7UL, ResourceChargeabilityMatrixV1.For(ResourceClassV1.NetworkThroughput).Normalize(usage));
        Assert.Equal(7UL, ResourceChargeabilityMatrixV1.For(ResourceClassV1.FabricThroughput).Normalize(usage));
        Assert.Equal(109UL, ResourceChargeabilityMatrixV1.For(ResourceClassV1.DeviceMemoryOccupancy).Normalize(usage));
        var queue = ResourceChargeabilityMatrixV1.For(ResourceClassV1.QueueSlotOccupancy);
        Assert.Equal(ResourceUnitV1.Slots, queue.Unit);
        Assert.Equal(109UL, queue.Normalize(usage));
        var inflight = ResourceChargeabilityMatrixV1.For(ResourceClassV1.InflightOperationOccupancy);
        Assert.Equal(ResourceUnitV1.Operations, inflight.Unit);
        Assert.Equal(109UL, inflight.Normalize(usage));
    }

    [Fact]
    public void ChargeabilityNormalizationRejectsUnknownVersionsClassesAndOverflow()
    {
        var policy = ResourceChargeabilityMatrixV1.For(ResourceClassV1.ComputeTime);
        Assert.Throws<NotSupportedException>(() => policy.Normalize(
            new(2, 0, 0, 1, 0, 0, 0, 0, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ResourceChargeabilityMatrixV1.For((ResourceClassV1)ushort.MaxValue));
        Assert.Throws<OverflowException>(() => policy.Normalize(
            new(1, 0, 0, ulong.MaxValue, 1, 0, 0, 0, 0, 0)));
    }

    [Fact]
    public void ExactMeasurementIdentityAndNormalizedBreakdownAreRequired()
    {
        var setup = Create();
        using var commit = Prepare(setup, setup.Operation).Value!;
        var binding = setup.Kernel.SubmitResourceExternalAdmission(commit, setup.Dependencies, KernelResult.Ok).Value!;
        Assert.True(setup.Kernel.RecordExternalOperationCompletion(setup.Process,
            new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        var evidence = Evidence(binding, 4, 21);

        Assert.Equal(KernelError.StaleGeneration,
            setup.Kernel.SettleResourceExternalOperation(setup.Process, evidence with
            {
                MeasurementContract = evidence.MeasurementContract with { ContractVersion = "2" }
            }).Error);
        Assert.Equal(KernelError.InvalidMessage,
            setup.Kernel.SettleResourceExternalOperation(setup.Process, evidence with
            {
                Usage = evidence.Usage with { Executed = 3 }
            }).Error);
        Assert.Equal(10UL, Used(setup));
        Assert.True(setup.Kernel.SettleResourceExternalOperation(setup.Process, evidence).IsSuccess);
        Assert.Equal(4UL, Used(setup));
    }

    [Fact]
    public void RetiredAndBlockedCountsDoNotIncreaseExecutedChargeAndConflictingDuplicateFailsClosed()
    {
        var setup = Create();
        using var commit = Prepare(setup, setup.Operation).Value!;
        var binding = setup.Kernel.SubmitResourceExternalAdmission(commit, setup.Dependencies, KernelResult.Ok).Value!;
        Assert.True(setup.Kernel.RecordExternalOperationCompletion(setup.Process,
            new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        var evidence = Evidence(binding, 1, 31) with
        {
            Usage = new(1, 0, 0, 1, 0, 0, 900, 0, 0, 500)
        };

        Assert.True(setup.Kernel.SettleResourceExternalOperation(setup.Process, evidence).IsSuccess);
        Assert.Equal(1UL, Used(setup));
        Assert.Equal(KernelError.InvalidTransition,
            setup.Kernel.SettleResourceExternalOperation(setup.Process,
                evidence with { EvidenceId = Guid.NewGuid() }).Error);
        Assert.Equal(1UL, Used(setup));
    }

    private static KernelResult<ResourceAdmissionCommit> Prepare(Setup setup, ExternalOperationHandle operation) =>
        setup.Kernel.PrepareResourceExternalAdmission(setup.Process, setup.EffectCapability,
            ResourceKind.Compute, "compute:effect", 1, setup.ResourceGrant, 1,
            Envelope(10), operation, setup.Dependencies);

    private static ExternalResourceUsageEvidence Evidence(OperationBinding binding, ulong consumed, ulong sequence) =>
        new(1, binding, "host:compute-v1", 1,
            new(1, "host:compute-v1:measurement", "1", ResourceClassV1.ComputeTime,
                ResourceUnitV1.Nanoseconds),
            EvidenceId(sequence), ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds,
            new(1, 0, 0, consumed, 0, 0, 0, 0, 0, 0), consumed, sequence);

    private static Guid EvidenceId(ulong sequence)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, sequence);
        bytes[15] = 0xA9;
        return new Guid(bytes);
    }

    private static Setup Create()
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 970, 1970).Handle;
        var process = TestFixtures.Create(kernel, 971, 1971).Handle;
        var administration = kernel.MintCapability(new(1970), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, process, "p07",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100),
             new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!.ProcessBudget;
        var effect = kernel.MintCapability(new(1971), process, ResourceKind.Compute,
            "compute:effect", CapabilityRights.Execute, resourceGeneration: 1).Value!.CapabilityId;
        var grant = kernel.CapabilityAuthority.Mint(new(1971), new(1971), ResourceKind.Compute,
            "resource-use:compute-time", CapabilityRights.Delegate, process.Generation, 1,
            range: null, session: null, quota: 100, delegationDepth: 4,
            resourceUse: new(1, Envelope(100), 0, long.MaxValue,
                ResourceAssuranceV1.RuntimeEnforced, 3)).Value!.CapabilityId;
        var region = kernel.AllocateBuffer<byte>(process, 16).Value!;
        var operation = kernel.PrepareExternalOperation(process,
            [new(region.Handle, RegionUseMode.ReadOnly, new(0, 16))],
            ExternalVisibilityRequirement.None, ExternalPublicationPolicy.Staged).Value!.Operation;
        return new(kernel, process, new(1971), budget, effect, grant, region.Handle,
            operation, new(1, 1));
    }

    private static ResourceEnvelopeV1 Envelope(ulong amount) => new(1,
        ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
        ResourceUnitV1.Nanoseconds, amount, 0, "host:compute-v1");

    private static ulong Used(Setup setup) => Assert.Single(
        setup.Kernel.QueryBudget(setup.Budget).Value!.Usage,
        usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used;

    private sealed record Setup(RuntimeKernel Kernel, ProcessHandle Process, DomainId Domain,
        BudgetAccountHandle Budget, CapabilityId EffectCapability, CapabilityId ResourceGrant,
        RegionHandle Region, ExternalOperationHandle Operation, OperationDependencySnapshot Dependencies);
}
