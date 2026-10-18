using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class Phase04DeadlineCancellationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DeadlineAdmissionClockCannotInventBeforeEffectAfterOriginalSubmission(bool published)
    {
        var clock = new CallbackScopeClock();
        var kernel = new RuntimeKernel(null, clock);
        var owner = TestFixtures.Create(kernel, 434, 4034).Handle;
        var input = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var output = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var operation = kernel.PrepareExternalOperation(owner,
            [new(input.Handle, RegionUseMode.ReadOnly, new(0, 8)), new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!.Operation;
        var scope = kernel.CreateCancellationScope(owner, new(200)).Value!.Scope;
        OperationBinding binding = default;
        var effects = 0;
        clock.Callback = () =>
        {
            Assert.True(kernel.AdmitExternalOperation(owner, operation, Dependencies, cancellationScope: scope).IsSuccess);
            binding = kernel.RecordExternalOperationSubmission(owner, operation, Dependencies).Value;
            if (published)
            {
                Assert.True(kernel.RecordExternalOperationCompletion(owner, new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
                Assert.True(kernel.RecordExternalOperationVisibility(owner, new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
                Assert.True(kernel.PublishExternalOperation(owner, operation, Dependencies, new(ExternalPublicationPolicy.Staged), () => effects++).IsSuccess);
            }
            Assert.True(kernel.RequestCancellation(owner, scope).IsSuccess);
        };
        Assert.Equal(KernelError.DeadlineExpired, kernel.AdmitExternalOperation(owner, operation, Dependencies, cancellationScope: scope).Error);
        var original = kernel.QueryExternalOperation(owner, operation).Value!;
        Assert.Equal(binding, original.Binding);
        Assert.Equal(published ? ExternalOperationState.Published : ExternalOperationState.Submitted, original.State);
        Assert.Equal(published ? CancellationDisposition.TooLateEffectMayExist : CancellationDisposition.ProviderClosurePending,
            kernel.ObserveCancellation(owner, scope).Value!.Disposition);
        Assert.False(kernel.ReleaseExternalOperation(owner, operation, new(false, false)).IsSuccess);
        Assert.False(kernel.ReleaseRegion(owner, input).IsSuccess);
        if (!published) Assert.True(kernel.RecordExternalOperationCompletion(owner, new(binding, ExternalOperationCompletionDisposition.Cancelled)).IsSuccess);
        Assert.True(kernel.ReleaseExternalOperation(owner, operation, new(true, false)).IsSuccess);
        Assert.True(kernel.ReleaseRegion(owner, input).IsSuccess);
        Assert.True(kernel.ReleaseRegion(owner, output).IsSuccess);
        Assert.Equal(published ? 1 : 0, effects);
    }

    private static readonly OperationDependencySnapshot Dependencies = new(7, 11);

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ScopeClockCallbackCanCommitOnAnotherThreadBeforeFreshScopeProjection(int mode)
    {
        var clock = new CallbackScopeClock();
        var kernel = new RuntimeKernel(null, clock);
        var owner = TestFixtures.Create(kernel, 431, 4031).Handle;
        var scope = kernel.CreateCancellationScope(owner, new(200)).Value!.Scope;
        clock.Callback = () =>
        {
            var worker = Task.Run(() => kernel.CancellationScopes.Request(owner, scope, 100));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Clock callback ran under scope commit lock.");
            Assert.True(worker.Result.IsSuccess);
        };
        CancellationObservation observed = mode switch
        {
            0 => kernel.CreateCancellationScope(owner, new(150), scope).Value!,
            1 => kernel.RequestCancellation(owner, scope).Value!,
            2 => kernel.ObserveCancellation(owner, scope).Value!,
            3 => kernel.CancellationScopes.RecordDisposition(owner, scope,
                CancellationDisposition.CancelledBeforeEffect).Value!,
            _ => Assert.Single(kernel.CancellationScopes.InspectionSnapshot(owner)),
        };
        Assert.True(observed.CancellationRequested);
        Assert.Equal(mode == 3 ? CancellationDisposition.CancelledBeforeEffect
            : CancellationDisposition.CancellationRequested, observed.Disposition);
        Assert.False(observed.AuthorizesEffect);
        Assert.False(observed.AuthorizesReclaim);
        Assert.Equal(1, clock.CallbackCount);
    }

    [Fact]
    public void ClockFailureBeforeScopeCreateDoesNotAllocateOrChangeParent()
    {
        var clock = new CallbackScopeClock();
        var kernel = new RuntimeKernel(null, clock);
        var owner = TestFixtures.Create(kernel, 432, 4032).Handle;
        var parent = kernel.CreateCancellationScope(owner, new(200)).Value!;
        clock.Callback = () => throw new InvalidOperationException("clock fault");
        Assert.Throws<InvalidOperationException>(() => kernel.CreateCancellationScope(owner, new(150), parent.Scope));
        Assert.Equal(parent, kernel.ObserveCancellation(owner, parent.Scope).Value);
        var child = kernel.CreateCancellationScope(owner, new(150), parent.Scope).Value!;
        Assert.Equal(parent.Scope.ScopeId.Value + 1, child.Scope.ScopeId.Value);
    }

    [Fact]
    public void ScopeCreateRechecksActualProcessAfterClockCallbackBeforeAllocation()
    {
        var clock = new CallbackScopeClock();
        var kernel = new RuntimeKernel(null, clock);
        var owner = TestFixtures.Create(kernel, 433, 4033).Handle;
        clock.Callback = () =>
        {
            var worker = Task.Run(() => kernel.TerminateProcess(owner));
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)), "Clock callback ran under process commit lock.");
            Assert.True(worker.Result.IsSuccess);
        };
        Assert.False(kernel.CreateCancellationScope(owner, new(200)).IsSuccess);
        Assert.Empty(kernel.CancellationScopes.InspectionSnapshot(owner));
    }

    private sealed class CallbackScopeClock : TimeProvider
    {
        public Action? Callback { get; set; }
        public int CallbackCount { get; private set; }
        public override long GetTimestamp()
        {
            var callback = Callback;
            Callback = null;
            if (callback is not null) { CallbackCount++; callback(); }
            return 100;
        }
    }


    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void LateCancellationAndRepeatedReleaseUseOriginalOwnerFactsWithoutInventingContainment(int mode)
    {
        var s = CreateExternalScenario();
        var prepared = Prepare(s);
        var scope = s.Kernel.CreateCancellationScope(s.Owner).Value!;
        Assert.True(s.Kernel.AdmitExternalOperation(s.Owner, prepared.Operation, Dependencies,
            cancellationScope: scope.Scope).IsSuccess);
        var effects = 0;
        if (mode is 0 or 3)
            Assert.True(s.Kernel.CancelExternalOperation(s.Owner, prepared.Operation, false).IsSuccess);
        else
        {
            var binding = s.Kernel.RecordExternalOperationSubmission(s.Owner, prepared.Operation, Dependencies).Value;
            Assert.True(s.Kernel.RecordExternalOperationCompletion(s.Owner,
                new(binding, mode == 1 ? ExternalOperationCompletionDisposition.Cancelled : ExternalOperationCompletionDisposition.Completed)).IsSuccess);
            if (mode == 2)
            {
                Assert.True(s.Kernel.RecordExternalOperationVisibility(s.Owner,
                    new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
                Assert.True(s.Kernel.PublishExternalOperation(s.Owner, prepared.Operation, Dependencies,
                    new(ExternalPublicationPolicy.Staged), () => effects++).IsSuccess);
            }
        }
        Assert.True(s.Kernel.ReleaseExternalOperation(s.Owner, prepared.Operation, new(mode is 1 or 2, false)).IsSuccess);
        Assert.Equal(CancellationDisposition.Active, s.Kernel.ObserveCancellation(s.Owner, scope.Scope).Value!.Disposition);
        if (mode == 3) Assert.True(s.Kernel.RequestCancellation(s.Owner, scope.Scope).IsSuccess);
        else Assert.True(s.Kernel.RequestExternalOperationCancellation(s.Owner, prepared.Operation, scope.Scope, false).IsSuccess);
        Assert.True(s.Kernel.ReleaseExternalOperation(s.Owner, prepared.Operation, new(false, false)).IsSuccess);
        var expected = mode is 0 or 3 ? CancellationDisposition.CancelledBeforeEffect
            : mode == 1 ? CancellationDisposition.ProviderEffectContained : CancellationDisposition.TooLateEffectMayExist;
        var observed = s.Kernel.ObserveCancellation(s.Owner, scope.Scope).Value!;
        Assert.Equal(expected, observed.Disposition);
        Assert.False(observed.AuthorizesReclaim);
        Assert.False(observed.AuthorizesEffect);
        Assert.True(s.Kernel.RequestExternalOperationCancellation(s.Owner, prepared.Operation, scope.Scope, false).IsSuccess);
        Assert.True(s.Kernel.ReleaseExternalOperation(s.Owner, prepared.Operation, new(false, false)).IsSuccess);
        Assert.Equal(observed.Sequence, s.Kernel.ObserveCancellation(s.Owner, scope.Scope).Value!.Sequence);
        var original = s.Kernel.QueryExternalOperation(s.Owner, prepared.Operation).Value!;
        Assert.Equal(ExternalOperationState.Released, original.State);
        Assert.Equal(mode == 2 ? 1 : 0, effects);
        Assert.Equal(mode is 0 or 3, original.Binding is null);
        Assert.Single(original.Transitions, item => item.Event == "Released");
        Assert.True(s.Kernel.ReleaseRegion(s.Owner, s.Input).IsSuccess);
        Assert.True(s.Kernel.ReleaseRegion(s.Owner, s.Output).IsSuccess);
    }

    [Fact]
    public void ParentCancellationPropagatesRequestWithoutFabricatingChildClosure()
    {
        var clock = new MonotonicTestClock(100);
        var kernel = new RuntimeKernel(null, clock);
        var owner = TestFixtures.Create(kernel, 401, 4001).Handle;
        var parent = kernel.CreateCancellationScope(owner, new(200)).Value!;
        var child = kernel.CreateCancellationScope(owner, new(150), parent.Scope).Value!;

        var requested = kernel.RequestCancellation(owner, parent.Scope);
        var observedChild = kernel.ObserveCancellation(owner, child.Scope);

        Assert.True(requested.IsSuccess, requested.Message);
        Assert.Equal(CancellationDisposition.CancellationRequested, observedChild.Value!.Disposition);
        Assert.True(observedChild.Value.CancellationRequested);
        Assert.False(observedChild.Value.AuthorizesReclaim);
        Assert.False(observedChild.Value.AuthorizesEffect);
        Assert.Equal(KernelError.InvalidMessage,
            kernel.CreateCancellationScope(owner, new(201), parent.Scope).Error);
    }

    [Fact]
    public void DeadlineBeforeExternalAdmissionCancelsLocallyWithoutRegionPins()
    {
        var scenario = CreateExternalScenario();
        var prepared = Prepare(scenario);
        var scope = scenario.Kernel.CreateCancellationScope(scenario.Owner, new(101)).Value!;
        scenario.Clock.AdvanceTo(101);

        var admission = scenario.Kernel.AdmitExternalOperation(
            scenario.Owner, prepared.Operation, Dependencies,
            cancellationScope: scope.Scope);

        Assert.Equal(KernelError.DeadlineExpired, admission.Error);
        Assert.DoesNotContain(scenario.Kernel.Regions.SnapshotUses(), use => use.State == RegionUseState.Active);
        Assert.Equal(ExternalOperationDisposition.Cancelled,
            scenario.Kernel.QueryExternalOperation(scenario.Owner, prepared.Operation).Value!.Disposition);
        Assert.Equal(CancellationDisposition.CancelledBeforeEffect,
            scenario.Kernel.ObserveCancellation(scenario.Owner, scope.Scope).Value!.Disposition);
    }

    [Fact]
    public void SubmittedCancellationKeepsPinsUntilExactProviderClosure()
    {
        var scenario = CreateExternalScenario();
        var prepared = Prepare(scenario);
        var scope = scenario.Kernel.CreateCancellationScope(scenario.Owner, new(200)).Value!;
        var admission = scenario.Kernel.AdmitExternalOperation(
            scenario.Owner, prepared.Operation, Dependencies,
            cancellationSupport: ExternalCancellationSupport.ProviderCooperative,
            cancellationScope: scope.Scope).Value!;
        var binding = scenario.Kernel.RecordExternalOperationSubmission(
            scenario.Owner, prepared.Operation, Dependencies).Value!;

        var requested = scenario.Kernel.RequestExternalOperationCancellation(
            scenario.Owner, prepared.Operation, scope.Scope, providerCancellationSupported: true);

        Assert.Equal(CancellationDisposition.ProviderClosurePending, requested.Value!.Disposition);
        Assert.All(admission.RegionUses, use =>
            Assert.Equal(RegionUseState.Active,
                scenario.Kernel.ValidateRegionUse(scenario.Owner, use.Handle).Value!.State));
        Assert.Equal(KernelError.InvalidTransition,
            scenario.Kernel.ReleaseExternalOperation(scenario.Owner, prepared.Operation, new(false, true)).Error);

        Assert.True(scenario.Kernel.RecordExternalOperationCompletion(
            scenario.Owner, new(binding, ExternalOperationCompletionDisposition.Cancelled)).IsSuccess);
        Assert.True(scenario.Kernel.ReleaseExternalOperation(
            scenario.Owner, prepared.Operation, new(ProviderResourcesClosed: true, ProviderUnavailable: false)).IsSuccess);
        Assert.Equal(CancellationDisposition.ProviderEffectContained,
            scenario.Kernel.ObserveCancellation(scenario.Owner, scope.Scope).Value!.Disposition);
        Assert.All(admission.RegionUses, use =>
            Assert.Equal(KernelError.InvalidRegionState,
                scenario.Kernel.ValidateRegionUse(scenario.Owner, use.Handle).Error));
    }

    [Fact]
    public void CancellationAfterDeviceCompleteSuppressesStagedPublicationButStillRequiresClosure()
    {
        var scenario = CreateExternalScenario();
        var prepared = Prepare(scenario);
        var scope = scenario.Kernel.CreateCancellationScope(scenario.Owner).Value!;
        Assert.True(scenario.Kernel.AdmitExternalOperation(
            scenario.Owner, prepared.Operation, Dependencies,
            cancellationScope: scope.Scope).IsSuccess);
        var binding = scenario.Kernel.RecordExternalOperationSubmission(
            scenario.Owner, prepared.Operation, Dependencies).Value!;
        Assert.True(scenario.Kernel.RecordExternalOperationCompletion(
            scenario.Owner, new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);

        var cancellation = scenario.Kernel.RequestExternalOperationCancellation(
            scenario.Owner, prepared.Operation, scope.Scope, providerCancellationSupported: false);

        Assert.Equal(CancellationDisposition.ProviderClosurePending, cancellation.Value!.Disposition);
        Assert.Equal(KernelError.InvalidTransition,
            scenario.Kernel.PublishExternalOperation(
                scenario.Owner, prepared.Operation, Dependencies,
                new(ExternalPublicationPolicy.Staged), () => { }).Error);
        Assert.True(scenario.Kernel.ReleaseExternalOperation(
            scenario.Owner, prepared.Operation, new(true, false)).IsSuccess);
        Assert.Equal(CancellationDisposition.ProviderEffectContained,
            scenario.Kernel.ObserveCancellation(scenario.Owner, scope.Scope).Value!.Disposition);
    }

    [Fact]
    public void CancellationAfterPublicationCannotUndoPublication()
    {
        var scenario = CreateExternalScenario();
        var prepared = Prepare(scenario);
        var scope = scenario.Kernel.CreateCancellationScope(scenario.Owner).Value!;
        Assert.True(scenario.Kernel.AdmitExternalOperation(
            scenario.Owner, prepared.Operation, Dependencies,
            cancellationScope: scope.Scope).IsSuccess);
        var binding = scenario.Kernel.RecordExternalOperationSubmission(
            scenario.Owner, prepared.Operation, Dependencies).Value!;
        Assert.True(scenario.Kernel.RecordExternalOperationCompletion(
            scenario.Owner, new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(scenario.Kernel.RecordExternalOperationVisibility(
            scenario.Owner, new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        Assert.True(scenario.Kernel.PublishExternalOperation(
            scenario.Owner, prepared.Operation, Dependencies,
            new(ExternalPublicationPolicy.Staged), () => { }).IsSuccess);

        var cancellation = scenario.Kernel.RequestExternalOperationCancellation(
            scenario.Owner, prepared.Operation, scope.Scope, providerCancellationSupported: true);

        Assert.Equal(CancellationDisposition.TooLateEffectMayExist, cancellation.Value!.Disposition);
        var operation = scenario.Kernel.QueryExternalOperation(scenario.Owner, prepared.Operation).Value!;
        Assert.Equal(ExternalOperationState.Published, operation.State);
        Assert.Equal(ExternalOperationDisposition.Published, operation.Disposition);
    }

    [Fact]
    public void StaleCancellationGenerationIsTypedAndCannotMutateLiveScope()
    {
        var scenario = CreateExternalScenario();
        var scope = scenario.Kernel.CreateCancellationScope(scenario.Owner).Value!;
        var stale = scope.Scope with
        {
            Generation = new CancellationScopeGeneration(scope.Scope.Generation.Value + 1)
        };

        var result = scenario.Kernel.RequestCancellation(scenario.Owner, stale);

        Assert.True(result.IsSuccess);
        Assert.Equal(CancellationDisposition.Stale, result.Value!.Disposition);
        Assert.Equal(CancellationDisposition.Active,
            scenario.Kernel.ObserveCancellation(scenario.Owner, scope.Scope).Value!.Disposition);
    }

    [Fact]
    public void CancellationScopeGenerationCannotBeReusedForAnotherExternalOperation()
    {
        var scenario = CreateExternalScenario();
        var first = Prepare(scenario);
        var second = Prepare(scenario);
        var scope = scenario.Kernel.CreateCancellationScope(scenario.Owner).Value!;
        Assert.True(scenario.Kernel.AdmitExternalOperation(
            scenario.Owner, first.Operation, Dependencies,
            cancellationScope: scope.Scope).IsSuccess);

        var reused = scenario.Kernel.AdmitExternalOperation(
            scenario.Owner, second.Operation, Dependencies,
            cancellationScope: scope.Scope);

        Assert.Equal(KernelError.StaleGeneration, reused.Error);
        Assert.Equal(ExternalOperationState.Prepared,
            scenario.Kernel.QueryExternalOperation(scenario.Owner, second.Operation).Value!.State);
    }

    [Fact]
    public async Task IpcDeadlineAfterMoveDoesNotReturnOrDuplicateOwnership()
    {
        var clock = new MonotonicTestClock(100);
        var kernel = new RuntimeKernel(null, clock);
        var caller = TestFixtures.Create(kernel, 410, 4010).Handle;
        var service = TestFixtures.Create(kernel, 411, 4011).Handle;
        var protocol = new ProtocolDefinitionV1(
            "DeadlineMove", "deadline-move-digest", "Idle", null,
            [new ProtocolMessageDescriptorV1(
                1, "Move", consumes: ["payload"],
                requestPayload: new(RequestPayloadKind.Ownership, "payload", "SingPlus.Sip.OwnedBuffer",
                    ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer))],
            [new ProtocolTransitionV1(1, "Idle", "Idle")]);
        var responseProtocol = new ResponseProtocolDefinitionV1(
            protocol.ContractName, "deadline-move-response", [new(1, "Move")]);
        var descriptor = kernel.RegisterService(service, "deadline-move",
            new(protocol.ContractName, "1", protocol.ContractDigest), protocol, responseProtocol).Value!;
        var session = kernel.OpenSession(caller, descriptor).Value;
        var buffer = kernel.AllocateBuffer<byte>(caller, 8).Value!;
        var oldHandle = buffer.Handle;
        var scope = kernel.CreateCancellationScope(caller, new(101)).Value!;
        using var stopWaiting = new CancellationTokenSource();
        var pending = kernel.InvokeSessionUntilCancellationAsync(
            caller, session, 1, buffer, scope.Scope, stopWaiting.Token).AsTask();
        var received = kernel.ReceiveSessionRequest(service, session).Value!;
        var moved = Assert.IsType<OwnedBuffer<byte>>(received.Request.Payload);
        clock.AdvanceTo(101);
        stopWaiting.Cancel();

        var stopped = await pending;
        var timeout = kernel.ObserveCancellation(caller, scope.Scope);

        Assert.Equal(KernelError.CancellationPending, stopped.Error);
        Assert.Equal(TimeoutDisposition.ExpiredWaitingMayStop, timeout.Value!.Timeout);
        Assert.Equal(CancellationDisposition.CancellationRequested, timeout.Value.Disposition);
        Assert.False(buffer.IsValid);
        Assert.True(moved.IsValid);
        Assert.Equal(oldHandle.RegionId, moved.Handle.RegionId);
        Assert.Equal(oldHandle.Generation.Value + 1, moved.Handle.Generation.Value);
        Assert.Equal(KernelError.InvalidTransition,
            kernel.AcceptSessionInvocation(service, received.Invocation, false).Error);
        Assert.True(kernel.AcceptSessionCancellation(service, received.Invocation).IsSuccess);
        Assert.True(kernel.CancelSessionResponse(service, received.Invocation).IsSuccess);
        Assert.Equal(CancellationDisposition.CancelledBeforeEffect,
            kernel.ObserveCancellation(caller, scope.Scope).Value!.Disposition);
        Assert.False(buffer.IsValid);
        Assert.True(moved.IsValid);
    }

    private static ExternalScenario CreateExternalScenario()
    {
        var clock = new MonotonicTestClock(100);
        var kernel = new RuntimeKernel(null, clock);
        var owner = TestFixtures.Create(kernel, 420, 4020).Handle;
        return new(kernel, owner, clock,
            kernel.AllocateBuffer<byte>(owner, 8).Value!,
            kernel.AllocateBuffer<byte>(owner, 8).Value!);
    }

    private static OperationPreparation Prepare(ExternalScenario scenario) =>
        scenario.Kernel.PrepareExternalOperation(
            scenario.Owner,
            [
                new(scenario.Input.Handle, RegionUseMode.ReadOnly, new(0, 8)),
                new(scenario.Output.Handle, RegionUseMode.StagedOutput, new(0, 8)),
            ],
            ExternalVisibilityRequirement.PublicationFence,
            ExternalPublicationPolicy.Staged).Value!;

    private sealed record ExternalScenario(
        RuntimeKernel Kernel,
        ProcessHandle Owner,
        MonotonicTestClock Clock,
        OwnedBuffer<byte> Input,
        OwnedBuffer<byte> Output);

    private sealed class MonotonicTestClock(long timestamp) : TimeProvider
    {
        private long _timestamp = timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void AdvanceTo(long timestamp) => _timestamp = timestamp;
    }
}
