using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase16DurableResourceAdmissionTests
{
    private static readonly byte[] Key = Enumerable.Range(33, 32).Select(static value => (byte)value).ToArray();

    [Fact]
    public void PossibleSubmitIsDurableBeforeCallbackAndColdRestartDoesNotResurrectLease()
    {
        WithJournal((path, context) =>
        {
            using var commit = Prepare(context).Value!;
            var callbackObserved = false;
            var submitted = context.Kernel.SubmitResourceExternalAdmission(commit, context.Dependencies, () =>
            {
                callbackObserved = true;
                var pending = Assert.Single(context.Kernel.ResourceBudgetRecovery!.ReconciliationBacklog);
                Assert.Equal(ResourceBudgetRecoveryTransition.PossibleSubmit, pending.LastPayload.Transition);
                return KernelResult.Fail(KernelError.PlatformFaulted, "ambiguous provider disconnect");
            });

            Assert.True(callbackObserved);
            Assert.Equal(KernelError.PlatformFaulted, submitted.Error);
            Assert.Equal(BudgetReservationState.Quarantined,
                context.Kernel.Budgets.Query(commit.Lease).Value!.State);

            var restarted = Kernel(path);
            var backlog = Assert.Single(restarted.ResourceBudgetRecovery!.ReconciliationBacklog);
            Assert.Equal(ResourceBudgetRecoveryTransition.Quarantined, backlog.LastPayload.Transition);
            Assert.Equal(KernelError.BudgetReservationNotFound, restarted.Budgets.Query(commit.Lease).Error);
            Assert.True(restarted.ResourceBudgetRecovery.BlocksFreshAdmission);
            Assert.False(restarted.Budgets.RecoveryImportComplete);
            Assert.True(restarted.Budgets.ConfigureSystem([
                new BudgetAmount(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
            Assert.True(restarted.Budgets.RecoveryImportComplete);
            Assert.Equal(10UL, restarted.Budgets.Query(restarted.Budgets.SystemBudget).Value!.Usage
                .Single(usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used);
        });
    }

    [Fact]
    public void ConservativeColdChargeCannotBeRefundedOrMultipliedByOldLeaseCopies()
    {
        WithJournal((path, context) =>
        {
            using var commit = Prepare(context).Value!;
            Assert.Equal(KernelError.PlatformFaulted,
                context.Kernel.SubmitResourceExternalAdmission(commit, context.Dependencies,
                    () => KernelResult.Fail(KernelError.PlatformFaulted, "ambiguous")).Error);

            foreach (var restart in Enumerable.Range(0, 2))
            {
                var kernel = Kernel(path);
                Assert.True(kernel.Budgets.ConfigureSystem([
                    new BudgetAmount(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
                var root = kernel.Budgets.Query(kernel.Budgets.SystemBudget).Value!;
                Assert.Equal(10UL, root.Usage.Single(usage =>
                    usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used);
                Assert.Equal(KernelError.BudgetReservationNotFound, kernel.Budgets.Query(commit.Lease).Error);
            }

            var insufficient = Kernel(path);
            Assert.Equal(KernelError.BudgetExceeded, insufficient.Budgets.ConfigureSystem([
                new BudgetAmount(ServiceBudgetDimension.ComputeTimeNanoseconds, 9)]).Error);
            Assert.False(insufficient.Budgets.RecoveryImportComplete);
        });
    }

    [Fact]
    public void AbandonedPreparedAdmissionPersistsPreSubmitCancellationWithoutRecoveryBacklog()
    {
        WithJournal((path, context) =>
        {
            var commit = Prepare(context).Value!;
            commit.Dispose();

            var restarted = Kernel(path);
            var item = Assert.Single(restarted.ResourceBudgetRecovery!.Items);
            Assert.Equal(ResourceBudgetRecoveryTransition.CancelledPreSubmit, item.LastPayload.Transition);
            Assert.True(item.IsTerminal);
            Assert.Empty(restarted.ResourceBudgetRecovery.ReconciliationBacklog);
            Assert.False(restarted.ResourceBudgetRecovery.BlocksFreshAdmission);
        });
    }

    [Fact]
    public void CorruptColdJournalFailsKernelConstructionClosed()
    {
        WithJournal((path, context) =>
        {
            var commit = Prepare(context).Value!;
            commit.Dispose();
            var bytes = File.ReadAllBytes(path);
            bytes[^1] ^= 0x80;
            File.WriteAllBytes(path, bytes);
            Assert.Throws<InvalidDataException>(() => Kernel(path));
        });
    }

    private static void WithJournal(Action<string, Context> action)
    {
        var root = Path.Combine(Path.GetTempPath(), $"singnext-p16-integration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "budget.recovery");
            action(path, Create(Kernel(path)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static RuntimeKernel Kernel(string path) =>
        new(null, null, new RuntimeKernelRecoveryOptions(path, Key));

    private static KernelResult<ResourceAdmissionCommit> Prepare(Context context) =>
        context.Kernel.PrepareResourceExternalAdmission(
            context.Process, context.EffectCapability, ResourceKind.Compute, "compute:effect", 1,
            context.ResourceGrant, 1, Envelope(10), context.Operation, context.Dependencies);

    private static Context Create(RuntimeKernel kernel)
    {
        var admin = TestFixtures.Create(kernel, 2900, 3900).Handle;
        var target = TestFixtures.Create(kernel, 2901, 3901).Handle;
        var administration = kernel.MintCapability(new(3900), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, target, "p16-durable",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100),
             new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).Value!;
        var effect = kernel.MintCapability(new(3901), target, ResourceKind.Compute,
            "compute:effect", CapabilityRights.Execute, resourceGeneration: 1).Value!.CapabilityId;
        var grant = kernel.CapabilityAuthority.Mint(new(3901), new(3901), ResourceKind.Compute,
            "resource-use:compute-time", CapabilityRights.Delegate, target.Generation, 1,
            range: null, session: null, quota: 100, delegationDepth: 4,
            resourceUse: new(1, Envelope(100), 0, long.MaxValue,
                ResourceAssuranceV1.RuntimeEnforced, 3)).Value!.CapabilityId;
        var region = kernel.AllocateBuffer<byte>(target, 16).Value!;
        var operation = kernel.PrepareExternalOperation(target,
            [new(region.Handle, RegionUseMode.ReadOnly, new(0, 16))],
            ExternalVisibilityRequirement.None, ExternalPublicationPolicy.Staged).Value!.Operation;
        return new(kernel, target, budget.ProcessBudget, effect, grant, operation, new(1, 1));
    }

    private static ResourceEnvelopeV1 Envelope(ulong amount) => new(1,
        ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
        ResourceUnitV1.Nanoseconds, amount, 0, "host:compute-v1");

    private sealed record Context(
        RuntimeKernel Kernel,
        ProcessHandle Process,
        BudgetAccountHandle ProcessBudget,
        CapabilityId EffectCapability,
        CapabilityId ResourceGrant,
        ExternalOperationHandle Operation,
        OperationDependencySnapshot Dependencies);
}
