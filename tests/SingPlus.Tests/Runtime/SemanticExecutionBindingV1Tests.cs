using Hc = HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Tests.Contracts;

namespace SingPlus.Tests.Runtime;

public sealed class SemanticExecutionBindingV1Tests
{
    public static IEnumerable<object[]> InvalidBindingDigests()
    {
        for (int field = 0; field < 2; field++)
            for (int fault = 0; fault < 5; fault++) yield return [field, fault];
    }

    [Theory]
    [MemberData(nameof(InvalidBindingDigests))]
    public void BindingTypedDigestRequiresExactLowercaseSha256(int field, int fault)
    {
        SemanticExecutionBindingV1 binding = Bind(Create()) with { Digest = default };
        string digest = fault switch
        {
            0 => new string('a', 63), 1 => new string('a', 65),
            2 => new string('g', 64), 3 => new string('A', 64), _ => "opaque-token",
        };
        binding = field == 0 ? binding with { ObligationsDigest = new(digest) }
            : binding with { GuaranteesDigest = new(digest) };
        Assert.Throws<ArgumentException>(() => binding.Canonicalize());
    }

    public static IEnumerable<object[]> InvalidBindingTokens()
    {
        for (int field = 0; field < 5; field++)
            for (int fault = 0; fault < 4; fault++) yield return [field, fault];
    }

    [Theory]
    [MemberData(nameof(InvalidBindingTokens))]
    public void BindingDigestRejectsNonScalarAndControlFramedFields(int field, int fault)
    {
        SemanticExecutionBindingV1 binding = Bind(Create()) with { Digest = default };
        string token = fault switch { 0 => "token\uD800", 1 => "token\uDC00", 2 => "token\ntail", _ => "token\0tail" };
        binding = field switch
        {
            0 => binding with { ProviderIdentity = token },
            1 => binding with { MeasurementContractIdentity = token },
            2 => binding with { ResourceEnvelope = binding.ResourceEnvelope with { SemanticScope = token } },
            3 => binding with { ObligationsDigest = new(token) },
            _ => binding with { GuaranteesDigest = new(token) },
        };
        Assert.Throws<ArgumentException>(() => binding.Canonicalize());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BindingConstructorRejectsMalformedIdentityWithoutReservationMutation(bool measurement)
    {
        Context c = Create();
        BudgetReservationSnapshot before = c.Kernel.QueryBudget(c.Lease).Value!;
        KernelResult<SemanticExecutionBindingV1> result = c.Kernel.CreateSemanticExecutionBindingV1(c.Obligations, c.Guarantees, c.Principal,
            c.Lease, OperationObligationsV1Tests.Envelope(10), measurement ? "hybridcpu:external-runtime" : "provider\uD800",
            ExecutionGuaranteesV1Tests.Request().Correlation, c.ProviderGenerations,
            measurement ? "measurement\uDC00" : "unsupported");
        Assert.Equal(KernelError.InvalidMessage, result.Error);
        Assert.Null(result.Value);
        BudgetSnapshotAssertions.Equal(before, c.Kernel.QueryBudget(c.Lease).Value!);
    }

    [Fact]
    public void ScalarBindingIdentitiesRetainV1DigestAndDistinctUnicodeWithoutNormalization()
    {
        SemanticExecutionBindingV1 binding = Bind(Create());
        Assert.Equal(binding.Digest, binding.Canonicalize().Digest);
        SemanticExecutionBindingV1 scalar = (binding with { Digest = default, ProviderIdentity = "provider:é🚀",
            MeasurementContractIdentity = "measurement:\uFFFD" }).Canonicalize();
        Assert.Equal(scalar.Digest, scalar.Canonicalize().Digest);
        Assert.NotEqual(scalar.Digest,
            (scalar with { Digest = default, ProviderIdentity = "provider:e\u0301🚀" }).Canonicalize().Digest);
    }

    [Fact]
    public void MalformedProviderGuaranteeTupleCannotPublishBindingOrMutateBudget()
    {
        Context c = Create();
        BudgetReservationSnapshot before = c.Kernel.QueryBudget(c.Lease).Value!;
        ExecutionGuaranteesV1 invalid = c.Guarantees with { Digest = default, ProviderExecutionClass = "provider\uD800" };
        KernelResult<SemanticExecutionBindingV1> result = c.Kernel.CreateSemanticExecutionBindingV1(c.Obligations, invalid, c.Principal,
            c.Lease, OperationObligationsV1Tests.Envelope(10), "hybridcpu:external-runtime",
            ExecutionGuaranteesV1Tests.Request().Correlation, c.ProviderGenerations);
        Assert.Equal(KernelError.InvalidMessage, result.Error);
        Assert.Null(result.Value);
        BudgetSnapshotAssertions.Equal(before, c.Kernel.QueryBudget(c.Lease).Value!);
        Assert.Equal(ExternalOperationState.Prepared,
            c.Kernel.ExternalOperations.Query(c.Obligations.Operation).Value!.State);
        Assert.Empty(c.Kernel.Regions.SnapshotUses());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void BindingConstructorClockMutationCannotPublishStaleCorrelationOrChangeBudget(int fault)
    {
        Context c = Create();
        BudgetReservationSnapshot before = c.Kernel.QueryBudget(c.Lease).Value!;
        bool clockCalled = false;
        c.Time.OnRead = () =>
        {
            clockCalled = true;
            if (fault == 1)
            {
                RegionOwner owner = new RegionOwner(new(2201), c.Principal.Generation);
                RegionUseDescriptor use = c.Kernel.Regions.AcquireUse(c.Region, owner, RegionUseMode.ExclusiveWrite, new(0, 16)).Value!;
                Assert.True(c.Kernel.Regions.ReleaseUse(use.Handle, owner).IsSuccess);
            }
            if (fault == 2)
            {
                Assert.True(c.Kernel.ExternalOperations.Admit(c.Obligations.Operation, new(1, 1)).IsSuccess);
                Assert.True(c.Kernel.ExternalOperations.RecordSubmission(c.Obligations.Operation, new(1, 1)).IsSuccess);
            }
        };
        KernelResult<SemanticExecutionBindingV1> result = c.Kernel.CreateSemanticExecutionBindingV1(c.Obligations, c.Guarantees, c.Principal,
            c.Lease, OperationObligationsV1Tests.Envelope(10), "hybridcpu:external-runtime",
            ExecutionGuaranteesV1Tests.Request().Correlation, c.ProviderGenerations);
        Assert.True(clockCalled);
        Assert.Equal(fault == 0, result.IsSuccess);
        if (fault != 0)
        {
            Assert.Equal(KernelError.StaleGeneration, result.Error);
            Assert.Null(result.Value);
        }
        BudgetSnapshotAssertions.Equal(before, c.Kernel.QueryBudget(c.Lease).Value!);
        if (fault == 2)
        {
            ExternalOperationSnapshot operation = c.Kernel.ExternalOperations.Query(c.Obligations.Operation).Value!;
            Assert.Equal(ExternalOperationState.Submitted, operation.State);
            foreach (RegionUseDescriptor use in operation.Admission!.RegionUses)
                Assert.Equal(KernelError.RegionUseConflict,
                    c.Kernel.Regions.ReleaseUse(use.Handle, operation.Principal).Error);
        }
    }

    [Fact]
    public void BindingCorrelatesExactOperationObligationsGuaranteesLeaseAndOpaqueProviderGeneration()
    {
        Context context = Create();

        KernelResult<SemanticExecutionBindingV1> binding = context.Kernel.CreateSemanticExecutionBindingV1(
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
        Context context = Create();
        SemanticExecutionBindingV1 binding = Bind(context);
        Hc.ExternalGenerationSet changed = Generations("4d08cf0f-fee3-44a7-b1c9-b7149067936e");

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
        Context context = Create();
        SemanticExecutionBindingV1 binding = Bind(context);
        OperationPreparation secondPreparation = context.Kernel.PrepareExternalOperation(context.Principal,
            [new(context.Region, RegionUseMode.ReadOnly, new(0, 16))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
        DateTimeOffset now = context.Time.GetUtcNow();
        OperationObligationsV1 secondObligations = context.Kernel.ConstructOperationObligationsV1(
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
        Context context = Create();
        SemanticExecutionBindingV1 binding = Bind(context);
        ExecutionGuaranteesV1 switched = (context.Guarantees with
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
        Context context = Create();
        SemanticExecutionBindingV1 binding = Bind(context);
        Assert.True(context.Kernel.Budgets.CancelLeasePreSubmit(context.Principal, context.Lease).IsSuccess);

        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.RevalidateSemanticExecutionBindingV1(binding,
                context.Obligations, context.Guarantees, "hybridcpu:external-runtime",
                context.ProviderGenerations).Error);

        Context fresh = Create();
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
        Hc.ExternalGenerationSet first = Generations("e913d943-f591-4b19-a2f1-d1d72910a63b");
        Hc.ExternalGenerationSet same = Generations("e913d943-f591-4b19-a2f1-d1d72910a63b");
        Hc.ExternalGenerationSet changed = Generations("88fb4103-d818-4365-a090-623cc6bd0de2");

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
        TestTimeProvider time = new TestTimeProvider(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        RuntimeKernel kernel = new RuntimeKernel(null, time);
        ProcessHandle admin = TestFixtures.Create(kernel, 1100, 2200).Handle;
        ProcessHandle principal = TestFixtures.Create(kernel, 1101, 2201).Handle;
        CapabilityId administration = kernel.MintCapability(new(2200), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, principal, "semantic-binding",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).IsSuccess);
        BudgetReservationHandle lease = kernel.Budgets.Reserve(principal,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 10)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        RegionHandle region = kernel.AllocateBuffer<byte>(principal, 16).Value!.Handle;
        ExternalOperationHandle operation = kernel.PrepareExternalOperation(principal,
            [new(region, RegionUseMode.ReadOnly, new(0, 16))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        DateTimeOffset now = time.GetUtcNow();
        OperationObligationsV1 obligations = kernel.ConstructOperationObligationsV1(principal, operation,
            [OperationObligationsV1Tests.Envelope(10)], OperationObligationsV1Tests.Requirements(),
            new(now.AddMinutes(-1).UtcTicks, now.AddHours(1).UtcTicks)).Value!;
        ExecutionGuaranteesV1 guarantees = HybridCpu114SemanticCompatibility.Map(ExecutionGuaranteesV1Tests.Request()).Value!;
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
        internal Action? OnRead { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            Action? callback = OnRead;
            OnRead = null;
            callback?.Invoke();
            return _now;
        }
    }
}
