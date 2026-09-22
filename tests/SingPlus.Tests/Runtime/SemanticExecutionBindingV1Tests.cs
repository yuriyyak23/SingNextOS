using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Tests.Contracts;

namespace SingPlus.Tests.Runtime;

public sealed class SemanticExecutionBindingV1Tests
{
    [Fact]
    public void BindingCorrelatesExactOperationObligationsGuaranteesLeaseAndOpaqueProviderGeneration()
    {
        var context = Create();

        var binding = context.Kernel.CreateSemanticExecutionBindingV1(
            context.Obligations, context.Guarantees, context.Principal, context.Lease,
            OperationObligationsV1Tests.Envelope(10), "hybridcpu:external-runtime",
            ExecutionGuaranteesV1Tests.Request().Correlation,
            context.ProviderGenerations);

        Assert.True(binding.IsSuccess, binding.Message);
        Assert.True(context.Kernel.RevalidateSemanticExecutionBindingV1(binding.Value!,
            context.Obligations, context.Guarantees, "hybridcpu:external-runtime",
            context.ProviderGenerations).IsSuccess);
        Assert.False(binding.Value!.AuthorizesExecution);
        Assert.False(binding.Value.AuthorizesEffect);
        Assert.False(binding.Value.AuthorizesPublication);
        Assert.False(binding.Value.IsProviderAdmission);
    }

    [Fact]
    public void ProviderGenerationOrIdentityDriftFailsClosed()
    {
        var context = Create();
        var binding = Bind(context);
        var changed = Generations("4d08cf0f-fee3-44a7-b1c9-b7149067936e");

        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.RevalidateSemanticExecutionBindingV1(binding,
                context.Obligations, context.Guarantees, "hybridcpu:external-runtime", changed).Error);
        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.RevalidateSemanticExecutionBindingV1(binding,
                context.Obligations, context.Guarantees, "hybridcpu:other", context.ProviderGenerations).Error);
    }

    [Fact]
    public void CrossOperationReplayAndBindingTamperFailClosed()
    {
        var context = Create();
        var binding = Bind(context);
        var secondPreparation = context.Kernel.PrepareExternalOperation(context.Principal,
            [new(context.Region, RegionUseMode.ReadOnly, new(0, 16))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
        var now = context.Time.GetUtcNow();
        var secondObligations = context.Kernel.ConstructOperationObligationsV1(
            context.Principal, secondPreparation.Operation, [OperationObligationsV1Tests.Envelope(10)],
            OperationObligationsV1Tests.Requirements(),
            new(now.AddMinutes(-1).UtcTicks, now.AddHours(1).UtcTicks)).Value!;

        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.RevalidateSemanticExecutionBindingV1(binding,
                secondObligations, context.Guarantees, "hybridcpu:external-runtime",
                context.ProviderGenerations).Error);
        Assert.Equal(KernelError.InvalidMessage,
            context.Kernel.RevalidateSemanticExecutionBindingV1(binding with
                {
                    ProviderIdentity = "hybridcpu:tampered",
                }, context.Obligations, context.Guarantees, "hybridcpu:tampered",
                context.ProviderGenerations).Error);
    }

    [Fact]
    public void ProviderExecutionClassSwitchCannotReuseBindingOrGuaranteeDigest()
    {
        var context = Create();
        var binding = Bind(context);
        var switched = (context.Guarantees with
        {
            ProviderExecutionClass = "matrix-tile/binary32",
            Digest = default,
        }).Canonicalize();

        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.RevalidateSemanticExecutionBindingV1(binding,
                context.Obligations, switched, "hybridcpu:external-runtime",
                context.ProviderGenerations).Error);
    }

    [Fact]
    public void LeaseCancellationOrWrongEnvelopeCannotRemainBound()
    {
        var context = Create();
        var binding = Bind(context);
        Assert.True(context.Kernel.Budgets.CancelLeasePreSubmit(context.Principal, context.Lease).IsSuccess);

        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.RevalidateSemanticExecutionBindingV1(binding,
                context.Obligations, context.Guarantees, "hybridcpu:external-runtime",
                context.ProviderGenerations).Error);

        var fresh = Create();
        Assert.Equal(KernelError.InvalidMessage,
            fresh.Kernel.CreateSemanticExecutionBindingV1(
                fresh.Obligations, fresh.Guarantees, fresh.Principal, fresh.Lease,
                OperationObligationsV1Tests.Envelope(11), "hybridcpu:external-runtime",
                ExecutionGuaranteesV1Tests.Request().Correlation,
                fresh.ProviderGenerations).Error);
    }

    [Fact]
    public void OpaqueGenerationDigestIsStableAndChangesWithAnyToken()
    {
        var first = Generations("e913d943-f591-4b19-a2f1-d1d72910a63b");
        var same = Generations("e913d943-f591-4b19-a2f1-d1d72910a63b");
        var changed = Generations("88fb4103-d818-4365-a090-623cc6bd0de2");

        Assert.Equal(HybridCpu114SemanticCompatibility.GenerationDigest(first),
            HybridCpu114SemanticCompatibility.GenerationDigest(same));
        Assert.NotEqual(HybridCpu114SemanticCompatibility.GenerationDigest(first),
            HybridCpu114SemanticCompatibility.GenerationDigest(changed));
    }

    private static SemanticExecutionBindingV1 Bind(Context context) =>
        context.Kernel.CreateSemanticExecutionBindingV1(
            context.Obligations, context.Guarantees, context.Principal, context.Lease,
            OperationObligationsV1Tests.Envelope(10), "hybridcpu:external-runtime",
            ExecutionGuaranteesV1Tests.Request().Correlation,
            context.ProviderGenerations).Value!;

    private static Context Create()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        var kernel = new RuntimeKernel(null, time);
        var admin = TestFixtures.Create(kernel, 1100, 2200).Handle;
        var principal = TestFixtures.Create(kernel, 1101, 2201).Handle;
        var administration = kernel.MintCapability(new(2200), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, principal, "semantic-binding",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        var lease = kernel.Budgets.Reserve(principal,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 10)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        var region = kernel.AllocateBuffer<byte>(principal, 16).Value!.Handle;
        var operation = kernel.PrepareExternalOperation(principal,
            [new(region, RegionUseMode.ReadOnly, new(0, 16))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        var now = time.GetUtcNow();
        var obligations = kernel.ConstructOperationObligationsV1(principal, operation,
            [OperationObligationsV1Tests.Envelope(10)], OperationObligationsV1Tests.Requirements(),
            new(now.AddMinutes(-1).UtcTicks, now.AddHours(1).UtcTicks)).Value!;
        var guarantees = HybridCpu114SemanticCompatibility.Map(ExecutionGuaranteesV1Tests.Request()).Value!;
        return new(kernel, time, principal, region, lease, obligations, guarantees,
            Generations("e913d943-f591-4b19-a2f1-d1d72910a63b"));
    }

    private static Hc.ExternalGenerationSet Generations(string token) =>
        new(Hc.ExternalOperationContract.Version, [Guid.Parse(token)]);

    private sealed record Context(
        RuntimeKernel Kernel,
        TestTimeProvider Time,
        ProcessHandle Principal,
        RegionHandle Region,
        BudgetReservationHandle Lease,
        OperationObligationsV1 Obligations,
        ExecutionGuaranteesV1 Guarantees,
        Hc.ExternalGenerationSet ProviderGenerations);

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
