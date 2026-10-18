using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Tests.Contracts;

namespace SingPlus.Tests.Runtime;

public sealed class SemanticOperationObligationsRuntimeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ObligationsClockCallbackCannotReturnStaleOwnerValidation(int fault)
    {
        var c = Create(RegionUseMode.StagedOutput, withSession: true);
        var obligations = Compose(c).Value!;
        if (fault == 3)
        {
            Assert.True(c.Kernel.ExternalOperations.Admit(c.Operation, new(1, 1)).IsSuccess);
            obligations = Compose(c).Value!;
        }
        var clockCalled = false;
        c.Time.OnRead = () =>
        {
            clockCalled = true;
            if (fault == 1)
            {
                var owner = new RegionOwner(new(2101), c.Principal.Generation);
                var use = c.Kernel.Regions.AcquireUse(c.Region, owner, RegionUseMode.ExclusiveWrite, new(0, 16)).Value!;
                Assert.True(c.Kernel.Regions.ReleaseUse(use.Handle, owner).IsSuccess);
            }
            if (fault == 2) Assert.True(c.Kernel.CloseSession(c.Principal, c.Session!.Value).IsSuccess);
            if (fault == 3) Assert.True(c.Kernel.ExternalOperations.RecordSubmission(c.Operation, new(1, 1)).IsSuccess);
        };
        var result = c.Kernel.RevalidateOperationObligationsV1(obligations);
        Assert.True(clockCalled);
        Assert.Equal(fault == 0, result.IsSuccess);
        if (fault != 0) Assert.Equal(fault == 2 ? KernelError.SessionClosed : KernelError.StaleGeneration, result.Error);
        Assert.Empty(c.Kernel.Budgets.InspectionSnapshot());
        if (fault == 3)
        {
            var operation = c.Kernel.ExternalOperations.Query(c.Operation).Value!;
            Assert.Equal(ExternalOperationState.Submitted, operation.State);
            foreach (var use in operation.Admission!.RegionUses)
                Assert.Equal(KernelError.RegionUseConflict,
                    c.Kernel.Regions.ReleaseUse(use.Handle, operation.Principal).Error);
        }
        else Assert.All(c.Kernel.Regions.SnapshotUses(), use => Assert.Equal(RegionUseState.Released, use.State));
    }

    [Fact]
    public void ComposerCapturesExactLiveRegionEpochAndRevalidatesWithoutMutation()
    {
        var context = Create();
        var obligations = Compose(context);

        Assert.True(obligations.IsSuccess, obligations.Message);
        Assert.Equal(context.Region, Assert.Single(obligations.Value!.RegionUses).Region);
        Assert.True(context.Kernel.RevalidateOperationObligationsV1(obligations.Value).IsSuccess);
        Assert.Equal(ExternalOperationState.Prepared,
            context.Kernel.ExternalOperations.Query(context.Operation).Value!.State);
        Assert.Empty(context.Kernel.Regions.SnapshotUses());
    }

    [Fact]
    public void RegionMutationAfterCompositionFailsClosed()
    {
        var context = Create(RegionUseMode.StagedOutput);
        var obligations = Compose(context).Value!;
        var owner = new RegionOwner(new(2101), context.Principal.Generation);
        var write = context.Kernel.Regions.AcquireUse(context.Region, owner,
            RegionUseMode.ExclusiveWrite, new(0, 16)).Value!;
        Assert.True(context.Kernel.Regions.ReleaseUse(write.Handle, owner).IsSuccess);

        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.RevalidateOperationObligationsV1(obligations).Error);
    }

    [Fact]
    public void SessionCloseAfterCompositionFailsClosed()
    {
        var context = Create(withSession: true);
        var obligations = Compose(context).Value!;
        Assert.True(context.Kernel.CloseSession(context.Principal, context.Session!.Value).IsSuccess);

        Assert.Equal(KernelError.SessionClosed,
            context.Kernel.RevalidateOperationObligationsV1(obligations).Error);
    }

    [Fact]
    public void ExactAdmissionRemainsRevalidatableButSubmittedStateFailsClosed()
    {
        var context = Create();
        var obligations = Compose(context).Value!;
        Assert.True(context.Kernel.ExternalOperations.Admit(context.Operation, new(1, 1)).IsSuccess);

        Assert.True(context.Kernel.RevalidateOperationObligationsV1(obligations).IsSuccess);
        Assert.True(context.Kernel.ExternalOperations.RecordSubmission(context.Operation, new(1, 1)).IsSuccess);

        Assert.Equal(KernelError.StaleGeneration,
            context.Kernel.RevalidateOperationObligationsV1(obligations).Error);
    }

    [Fact]
    public void ExpiredTemporalSnapshotFailsClosed()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        var context = Create(timeProvider: time);
        var obligations = Compose(context).Value!;
        time.Advance(TimeSpan.FromHours(2));

        Assert.Equal(KernelError.DeadlineExpired,
            context.Kernel.RevalidateOperationObligationsV1(obligations).Error);
    }

    [Fact]
    public void SyntacticallyValidObligationsDoNotAuthorizeExistingAdmission()
    {
        var context = Create();
        var obligations = Compose(context).Value!;
        Assert.True(context.Kernel.RevalidateOperationObligationsV1(obligations).IsSuccess);

        var denied = context.Kernel.PrepareResourceExternalAdmission(
            context.Principal,
            new CapabilityId(999_001),
            ResourceKind.Compute,
            "compute:effect",
            1,
            new CapabilityId(999_002),
            1,
            OperationObligationsV1Tests.Envelope(10),
            context.Operation,
            new(1, 1));

        Assert.Equal(KernelError.CapabilityNotFound, denied.Error);
        Assert.Equal(ExternalOperationState.Prepared,
            context.Kernel.ExternalOperations.Query(context.Operation).Value!.State);
        Assert.Empty(context.Kernel.Budgets.InspectionSnapshot());
    }

    private static KernelResult<OperationObligationsV1> Compose(Context context)
    {
        var now = context.Time.GetUtcNow();
        return context.Kernel.ConstructOperationObligationsV1(
            context.Principal,
            context.Operation,
            [OperationObligationsV1Tests.Envelope(10)],
            OperationObligationsV1Tests.Requirements(),
            new(now.AddMinutes(-1).UtcTicks, now.AddHours(1).UtcTicks),
            context.Session);
    }

    private static Context Create(
        RegionUseMode mode = RegionUseMode.ReadOnly,
        bool withSession = false,
        TestTimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? new TestTimeProvider(
            new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        var kernel = new RuntimeKernel(null, time);
        var principal = TestFixtures.Create(kernel, 1001, 2101).Handle;
        var region = kernel.AllocateBuffer<byte>(principal, 16).Value!.Handle;
        var operation = kernel.PrepareExternalOperation(principal,
            [new(region, mode, new(0, 16))],
            ExternalVisibilityRequirement.PublicationFence,
            ExternalPublicationPolicy.Staged).Value!.Operation;

        EndpointSessionHandle? session = null;
        if (withSession)
        {
            var provider = TestFixtures.Create(kernel, 1002, 2102).Handle;
            var protocol = new ProtocolDefinitionV1(
                "ObligationSession", "obligation-session-v1", "Ready", ["Done"],
                [new ProtocolMessageDescriptorV1(1, "Run")],
                [new ProtocolTransitionV1(1, "Ready", "Done")]);
            var service = kernel.RegisterService(provider, "obligation-session",
                new("ObligationSession", "1", "obligation-session-v1"), protocol).Value!;
            session = kernel.OpenSession(principal, service).Value;
        }

        return new(kernel, time, principal, region, operation, session);
    }

    private sealed record Context(
        RuntimeKernel Kernel,
        TestTimeProvider Time,
        ProcessHandle Principal,
        RegionHandle Region,
        ExternalOperationHandle Operation,
        EndpointSessionHandle? Session);

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        internal Action? OnRead { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            var callback = OnRead;
            OnRead = null;
            callback?.Invoke();
            return _now;
        }
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
