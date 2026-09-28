using System.Diagnostics;
using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using Xunit.Abstractions;

namespace SingPlus.Tests.Runtime;

public sealed class Phase11CrossCuttingIntegrationTests(ITestOutputHelper output)
{
    private static readonly byte[] Image = [0x11, 0xBB];
    private static readonly OperationDependencySnapshot Dependencies = new(17, 19);

    [Fact]
    public void ManagedLifecycleComposesDependenciesMoveExternalEffectObservabilityCheckpointAndReplacement()
    {
        var harness = CreateHarness();
        var providerContract = Contract("Phase11Provider");
        var provider = Definition("phase11-provider", 1101, 11101, providerContract);
        var worker = Definition("phase11-worker", 1102, 11102, Contract("Phase11Worker"),
            dependencies: [new(providerContract, ServiceDependencyKind.Hard)]);
        Assert.True(harness.Supervisor.Register(worker).IsSuccess);
        Assert.True(harness.Supervisor.Register(provider).IsSuccess);
        Assert.True(harness.Supervisor.StartAll().IsSuccess);
        var providerInstance = harness.Supervisor.Query(provider.Identity).Value!.Instance!.Value;
        var workerSnapshot = harness.Supervisor.Query(worker.Identity).Value!;
        var workerInstance = workerSnapshot.Instance!.Value;
        Assert.Equal(providerInstance, Assert.Single(workerSnapshot.Dependencies).Provider);

        var trace = harness.Kernel.StartTraceSession(workerInstance.Process, 64).Value!;
        var channel = MoveChannel(harness.Kernel, workerInstance.Process, providerInstance.Process, 4);
        var moved = harness.Kernel.AllocateBuffer<byte>(workerInstance.Process, 8).Value!;
        moved.Span.Fill(0x11);
        var move = harness.Kernel.SendMoveV2(workerInstance.Process, providerInstance.Process,
            channel.Left, 1, moved);
        Assert.True(move.IsSuccess, move.Message);
        Assert.False(moved.IsValid);
        Assert.True(Assert.IsType<OwnedBuffer<byte>>(move.Value!.Envelope.Payload).IsValid);

        var input = harness.Kernel.AllocateBuffer<byte>(workerInstance.Process, 8).Value!;
        var stagedOutput = harness.Kernel.AllocateBuffer<byte>(workerInstance.Process, 8).Value!;
        var prepared = harness.Kernel.PrepareExternalOperation(workerInstance.Process,
        [
            new(input.Handle, RegionUseMode.ReadOnly, new(0, 8)),
            new(stagedOutput.Handle, RegionUseMode.StagedOutput, new(0, 8)),
        ], ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
        Assert.True(harness.Kernel.AdmitExternalOperation(workerInstance.Process, prepared.Operation, Dependencies).IsSuccess);
        var binding = harness.Kernel.RecordExternalOperationSubmission(workerInstance.Process, prepared.Operation, Dependencies).Value!;
        Assert.True(harness.Kernel.RecordExternalOperationCompletion(workerInstance.Process,
            new(binding, ExternalOperationCompletionDisposition.Completed)).IsSuccess);
        Assert.True(harness.Kernel.RecordExternalOperationVisibility(workerInstance.Process,
            new(binding, ExternalVisibilityRequirement.PublicationFence, true)).IsSuccess);
        Assert.True(harness.Kernel.PublishExternalOperation(workerInstance.Process, prepared.Operation, Dependencies,
            new(ExternalPublicationPolicy.Staged), () => stagedOutput.Span[0] = 0x2A).IsSuccess);
        Assert.True(harness.Kernel.ReleaseExternalOperation(workerInstance.Process, prepared.Operation,
            new(true, false)).IsSuccess);
        Assert.Equal(0x2A, stagedOutput.Span[0]);

        var telemetry = harness.Supervisor.ProjectTelemetry(workerInstance).Value!;
        Assert.Equal(ServiceLifecycleState.Ready, telemetry.Service!.State);
        Assert.True(telemetry.Trace.BufferedEvents > 0);
        var traceSnapshot = harness.Kernel.InspectTrace(workerInstance.Process, trace.Session).Value!;
        Assert.Contains(traceSnapshot.Events, item => item.Kind == TraceEventKind.IpcSent);
        Assert.Contains(traceSnapshot.Events, item => item.Kind == TraceEventKind.ExternalOperationPublished);

        var checkpointComponent = harness.Kernel.AdmitComponent(
            CheckpointPlan("phase11-checkpoint", 1112, 11112, 1, 8192)).Value!;
        var checkpointBuffer = harness.Kernel.AllocateBuffer<byte>(checkpointComponent.Process, 8).Value!;
        var checkpoint = harness.Kernel.CreateOrdinaryCheckpoint(harness.Principal, harness.CheckpointCapability,
            checkpointComponent.Identity, new byte[] { 1, 1 }, [new(checkpointBuffer)]).Value!;
        Assert.True(checkpoint.Complete);
        Assert.True(harness.Kernel.DeleteOrdinaryCheckpoint(harness.Principal, harness.CheckpointCapability,
            checkpoint.Handle).IsSuccess);
        Assert.True(harness.Kernel.StopTraceSession(workerInstance.Process, trace.Session).IsSuccess);

        var replacement = harness.Supervisor.Replace(workerInstance,
            Definition("phase11-worker", 1102, 11102, Contract("Phase11Worker"), generation: 2,
                dependencies: [new(providerContract, ServiceDependencyKind.Hard)]));
        Assert.True(replacement.IsSuccess, replacement.Message);
        var fresh = replacement.Value!.Snapshot.Instance!.Value;
        Assert.Equal(workerInstance.Generation.Value + 1, fresh.Generation.Value);
        Assert.Equal(workerInstance.Process.Generation + 1, fresh.Process.Generation);
        Assert.Equal(KernelError.StaleHandle,
            harness.Kernel.ProjectTelemetry(workerInstance.Process, workerInstance.Process).Error);
        Assert.Equal(providerInstance, Assert.Single(replacement.Value.Snapshot.Dependencies).Provider);
    }

    [Fact]
    public void AmbiguousProviderLossIsExplainedObservedAndBlocksReplacementUntilContainment()
    {
        var harness = CreateHarness();
        var definition = Definition("phase11-fault", 1103, 11103, Contract("Phase11Fault"));
        Assert.True(harness.Supervisor.Register(definition).IsSuccess);
        var instance = harness.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        var trace = harness.Kernel.StartTraceSession(instance.Process, 32).Value!;
        var input = harness.Kernel.AllocateBuffer<byte>(instance.Process, 4).Value!;
        var result = harness.Kernel.AllocateBuffer<byte>(instance.Process, 4).Value!;
        var operation = harness.Kernel.PrepareExternalOperation(instance.Process,
        [
            new(input.Handle, RegionUseMode.ReadOnly, new(0, 4)),
            new(result.Handle, RegionUseMode.StagedOutput, new(0, 4)),
        ], ExternalVisibilityRequirement.None, ExternalPublicationPolicy.Staged).Value!;
        Assert.True(harness.Kernel.AdmitExternalOperation(instance.Process, operation.Operation, Dependencies).IsSuccess);
        Assert.True(harness.Kernel.RecordExternalOperationSubmission(instance.Process, operation.Operation, Dependencies).IsSuccess);
        Assert.True(harness.Kernel.RecordExternalOperationProviderLoss(instance.Process, operation.Operation).IsSuccess);

        Assert.True(harness.Supervisor.ReportHealth(instance, ServiceHealthState.Failed, "provider response lost").IsSuccess);
        Assert.Equal(ServiceLifecycleState.Quarantined, harness.Supervisor.Query(definition.Identity).Value!.State);
        var inspector = harness.Kernel.CreateAuthorityInspector(instance.Process).Value!;
        var why = inspector.WhyExternalOperationPinned(operation.Operation).Value!;
        Assert.Contains(why.Reasons, reason => reason.Kind == AuthorityBlockReasonKind.ProviderEffectUncontained);
        var telemetry = harness.Kernel.ProjectTelemetry(instance.Process, instance.Process).Value!;
        Assert.Equal(1, telemetry.Operations.FaultedOrUncontained);
        Assert.Contains(harness.Kernel.InspectTrace(instance.Process, trace.Session).Value!.Events,
            item => item.Kind == TraceEventKind.ExternalOperationFaulted);
        var replacement = harness.Supervisor.Replace(instance,
            Definition("phase11-fault", 1103, 11103, Contract("Phase11Fault"), generation: 2));
        Assert.Equal(KernelError.ReplacementBlocked, replacement.Error);

        Assert.Equal(KernelError.InvalidTransition,
            harness.Kernel.ReleaseExternalOperation(instance.Process, operation.Operation,
                new(false, true, true)).Error);
        Assert.Equal(KernelError.ReplacementBlocked,
            harness.Supervisor.Replace(instance,
                Definition("phase11-fault", 1103, 11103, Contract("Phase11Fault"), generation: 2)).Error);
        Assert.True(harness.Kernel.ReleaseExternalOperation(instance.Process, operation.Operation,
            new(true, true)).IsSuccess);
        Assert.True(harness.Kernel.ObserveComponentTeardown(definition.Identity).Value!.Reclaimable);
    }

    [Fact]
    public void FloodsOversizedInputsAndCheckpointStormStayBoundedAndFailClosed()
    {
        var harness = CreateHarness();
        var component = harness.Kernel.AdmitComponent(Plan("phase11-abuse", 1104, 11104,
            Contract("Phase11Abuse"), checkpointBytes: 64, traceBytes: 4096)).Value!;
        var trace = harness.Kernel.StartTraceSession(component.Process, 2, TraceOverflowPolicy.DropWithMarker).Value!;
        for (var index = 0; index < 32; index++)
            Assert.True(harness.Kernel.RecordTraceSemanticEvent(component.Process, TraceEventKind.SupervisorObservation,
                new(new($"flood:{index}")), new("abuse", index.ToString(), "observed", "bounded")).IsSuccess);
        var traceSnapshot = harness.Kernel.InspectTrace(component.Process, trace.Session).Value!;
        Assert.False(traceSnapshot.Complete);
        Assert.True(traceSnapshot.DroppedEventCount > 0);
        Assert.True(traceSnapshot.Events.Count <= 2);

        var subscription = harness.Kernel.StartTelemetrySubscription(component.Process, component.Process,
            TelemetryProjectionClass.SelfOperational, 1, TelemetrySubscriptionOverflowPolicy.DropOldestWithMarker).Value!;
        for (var index = 0; index < 16; index++)
            Assert.True(harness.Kernel.SampleTelemetrySubscription(component.Process, subscription.Subscription).IsSuccess);
        var batch = harness.Kernel.ReadTelemetrySubscription(component.Process, subscription.Subscription).Value!;
        Assert.Single(batch.Snapshots);
        Assert.Equal(15UL, batch.DroppedSnapshots);

        var receiver = TestFixtures.Create(harness.Kernel, 1199, 11999).Handle;
        var channel = CopyChannel(harness.Kernel, component.Process, receiver, 1);
        var source = harness.Kernel.AllocateBuffer<byte>(component.Process,
            IpcV2Contract.MaximumScatterGatherSegments + 1).Value!;
        var segments = Enumerable.Range(0, IpcV2Contract.MaximumScatterGatherSegments + 1)
            .Select(index => new IpcScatterGatherCopySegment(source, index, 1)).ToArray();
        Assert.Equal(KernelError.InvalidMessage,
            harness.Kernel.SendScatterGatherCopyV2(component.Process, receiver, channel.Left, 1, segments).Error);

        var checkpointKernel = new RuntimeKernel();
        var checkpointAdmin = TestFixtures.Create(checkpointKernel, 1188, 11188).Handle;
        var checkpointCapability = checkpointKernel.MintCapability(new(11188), checkpointAdmin,
            ResourceKind.KernelService, CapabilityResourceIds.CheckpointAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        var budgetCapability = checkpointKernel.MintCapability(new(11188), checkpointAdmin,
            ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration,
            CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(checkpointKernel.ConfigureSystemBudget(checkpointAdmin, budgetCapability,
        [
            new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
            new(ServiceBudgetDimension.ExternalOperations, 4),
            new(ServiceBudgetDimension.CheckpointStorageBytes, 64),
        ]).IsSuccess);
        var checkpointComponent = checkpointKernel.AdmitComponent(
            CheckpointPlan("phase11-storm", 1114, 11114, 1, 64)).Value!;
        Assert.True(checkpointKernel.CreateOrdinaryCheckpoint(checkpointAdmin, checkpointCapability,
            checkpointComponent.Identity, new byte[40]).IsSuccess);
        Assert.Equal(KernelError.BudgetExceeded,
            checkpointKernel.CreateOrdinaryCheckpoint(checkpointAdmin, checkpointCapability,
                checkpointComponent.Identity, new byte[40]).Error);

        var noAuthority = TestFixtures.Create(harness.Kernel, 1198, 11998).Handle;
        Assert.Equal(KernelError.ProjectionDenied,
            harness.Kernel.CreateAuthorityInspector(noAuthority).Value!.InspectOwner(source.Handle).Error);
        Assert.DoesNotContain(typeof(RuntimeKernel).Assembly.GetTypes(), type =>
            type.Name.Contains("FaultPlan", StringComparison.Ordinal));
    }

    [Fact]
    public void DeadlineAfterAcceptanceAndBudgetPressurePreservePinsAndAuthorityUntilExactClosure()
    {
        var clock = new MonotonicClock(100);
        var kernel = new RuntimeKernel(null, clock);
        var component = kernel.AdmitComponent(
            CheckpointPlan("phase11-deadline", 1116, 11116, 1, 4096, ownedMemoryBytes: 16)).Value!;
        var input = kernel.AllocateBuffer<byte>(component.Process, 8).Value!;
        var staged = kernel.AllocateBuffer<byte>(component.Process, 8).Value!;
        Assert.Equal(KernelError.BudgetExceeded, kernel.AllocateBuffer<byte>(component.Process, 1).Error);
        var scope = kernel.CreateCancellationScope(component.Process, new(101)).Value!;
        var operation = kernel.PrepareExternalOperation(component.Process,
        [
            new(input.Handle, RegionUseMode.ReadOnly, new(0, 8)),
            new(staged.Handle, RegionUseMode.StagedOutput, new(0, 8)),
        ], ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
        var admission = kernel.AdmitExternalOperation(component.Process, operation.Operation, Dependencies,
            cancellationSupport: ExternalCancellationSupport.ProviderCooperative,
            cancellationScope: scope.Scope).Value!;
        var binding = kernel.RecordExternalOperationSubmission(component.Process, operation.Operation, Dependencies).Value!;
        clock.AdvanceTo(101);

        var cancellation = kernel.RequestExternalOperationCancellation(component.Process, operation.Operation,
            scope.Scope, providerCancellationSupported: true).Value!;
        Assert.Equal(CancellationDisposition.ProviderClosurePending, cancellation.Disposition);
        Assert.All(admission.RegionUses, use =>
            Assert.Equal(RegionUseState.Active, kernel.ValidateRegionUse(component.Process, use.Handle).Value!.State));
        Assert.Equal(KernelError.InvalidTransition,
            kernel.ReleaseExternalOperation(component.Process, operation.Operation, new(false, false)).Error);

        Assert.True(kernel.RecordExternalOperationCompletion(component.Process,
            new(binding, ExternalOperationCompletionDisposition.Cancelled)).IsSuccess);
        Assert.True(kernel.ReleaseExternalOperation(component.Process, operation.Operation, new(true, false)).IsSuccess);
        Assert.Equal(CancellationDisposition.ProviderEffectContained,
            kernel.ObserveCancellation(component.Process, scope.Scope).Value!.Disposition);
        Assert.True(kernel.ReleaseRegion(component.Process, input).IsSuccess);
        Assert.True(kernel.AllocateBuffer<byte>(component.Process, 8).IsSuccess);
    }

    [Fact]
    public void CrossCuttingHotPathsHaveExecutableDiagnosticBaselines()
    {
        const int iterations = 64;
        var harness = CreateHarness();
        var definition = Definition("phase11-perf", 1105, 11105, Contract("Phase11Perf"));
        Assert.True(harness.Supervisor.Register(definition).IsSuccess);
        var timer = Stopwatch.StartNew();
        var started = harness.Supervisor.Start(definition.Identity).Value!.Snapshot.Instance!.Value;
        timer.Stop();
        var startTicks = timer.ElapsedTicks;

        timer.Restart();
        for (var index = 0; index < iterations; index++) Assert.True(harness.Supervisor.Query(definition.Identity).IsSuccess);
        timer.Stop();
        var supervisorIdleTicks = timer.ElapsedTicks;

        var receiver = TestFixtures.Create(harness.Kernel, 1197, 11997).Handle;
        var copyChannel = CopyChannel(harness.Kernel, started.Process, receiver, iterations);
        var payload = new IpcCopyPayload([1, 2, 3, 4], 64);
        timer.Restart();
        for (var index = 0; index < iterations; index++)
        {
            Assert.True(harness.Kernel.SendCopyV2(started.Process, receiver, copyChannel.Left, 1, payload).IsSuccess);
            Assert.True(harness.Kernel.Receive(receiver, copyChannel.Right).IsSuccess);
        }
        timer.Stop();
        var copyTicks = timer.ElapsedTicks;

        var moveChannel = MoveChannel(harness.Kernel, started.Process, receiver, iterations);
        timer.Restart();
        for (var index = 0; index < iterations; index++)
        {
            var buffer = harness.Kernel.AllocateBuffer<byte>(started.Process, 64).Value!;
            Assert.True(harness.Kernel.SendMoveV2(started.Process, receiver, moveChannel.Left, 1, buffer).IsSuccess);
            Assert.True(harness.Kernel.Receive(receiver, moveChannel.Right).IsSuccess);
        }
        timer.Stop();
        var moveTicks = timer.ElapsedTicks;

        var borrowChannel = BorrowChannel(harness.Kernel, started.Process, receiver, iterations);
        var borrowed = harness.Kernel.AllocateBuffer<byte>(started.Process, 64).Value!;
        timer.Restart();
        for (var index = 0; index < iterations; index++)
        {
            Assert.True(harness.Kernel.SendBorrowReadV2(started.Process, receiver, borrowChannel.Left, 1, borrowed).IsSuccess);
            var lease = Assert.IsType<BorrowLease<byte>>(harness.Kernel.Receive(receiver, borrowChannel.Right).Value!.Payload);
            Assert.True(harness.Kernel.ReturnBorrow(receiver, lease.Handle).IsSuccess);
        }
        timer.Stop();
        var borrowTicks = timer.ElapsedTicks;

        timer.Restart();
        for (var index = 0; index < iterations; index++)
        {
            var reserved = harness.Kernel.ReserveBudget(started.Process,
                [new(ServiceBudgetDimension.IpcMessages, 1)], BudgetReservationLifetime.LocalResource).Value!;
            Assert.True(harness.Kernel.ReleaseBudget(started.Process, reserved.Reservation).IsSuccess);
        }
        timer.Stop();
        var budgetTicks = timer.ElapsedTicks;

        timer.Restart();
        for (var index = 0; index < iterations; index++)
            harness.Kernel.RecordTraceSemanticEvent(started.Process, TraceEventKind.SupervisorObservation,
                new(new($"disabled:{index}")), new("perf", index.ToString(), "disabled", "no-session"));
        timer.Stop();
        var traceDisabledTicks = timer.ElapsedTicks;
        var trace = harness.Kernel.StartTraceSession(started.Process, iterations + 8).Value!;
        timer.Restart();
        for (var index = 0; index < iterations; index++)
            Assert.True(harness.Kernel.RecordTraceSemanticEvent(started.Process, TraceEventKind.SupervisorObservation,
                new(new($"enabled:{index}")), new("perf", index.ToString(), "enabled", "recorded")).IsSuccess);
        timer.Stop();
        var traceEnabledTicks = timer.ElapsedTicks;

        timer.Restart();
        for (var index = 0; index < iterations; index++) Assert.True(harness.Kernel.ProjectTelemetry(started.Process, started.Process).IsSuccess);
        timer.Stop();
        var telemetryTicks = timer.ElapsedTicks;
        var inspector = harness.Kernel.CreateAuthorityInspector(started.Process).Value!;
        timer.Restart();
        for (var index = 0; index < 8; index++) Assert.True(inspector.Capture().IsSuccess);
        timer.Stop();
        var inspectorTicks = timer.ElapsedTicks;

        timer.Restart();
        var checkpointComponent = harness.Kernel.AdmitComponent(
            CheckpointPlan("phase11-perf-checkpoint", 1115, 11115, 1, 8192)).Value!;
        var checkpoint = harness.Kernel.CreateOrdinaryCheckpoint(harness.Principal, harness.CheckpointCapability,
            checkpointComponent.Identity, new byte[256]).Value!;
        timer.Stop();
        var checkpointTicks = timer.ElapsedTicks;
        Assert.True(harness.Kernel.DeleteOrdinaryCheckpoint(harness.Principal, harness.CheckpointCapability, checkpoint.Handle).IsSuccess);
        Assert.True(harness.Kernel.StopTraceSession(started.Process, trace.Session).IsSuccess);

        timer.Restart();
        var replacement = harness.Supervisor.Replace(started,
            Definition("phase11-perf", 1105, 11105, Contract("Phase11Perf"), generation: 2));
        timer.Stop();
        Assert.True(replacement.IsSuccess, replacement.Message);
        var replaceTicks = timer.ElapsedTicks;

        output.WriteLine(
            "Phase11 diagnostic baseline; runtime={0}; os={1}; processor-count={2}; frequency={3}; iterations={4}; service-start={5}; service-replace={6}; supervisor-query={7}; small-copy-roundtrip={8}; allocate-move-roundtrip={9}; borrow-return={10}; budget-reserve-release={11}; trace-disabled={12}; trace-enabled={13}; telemetry={14}; inspector-8-captures={15}; checkpoint-256-bytes={16}",
            Environment.Version, Environment.OSVersion, Environment.ProcessorCount, Stopwatch.Frequency, iterations,
            startTicks, replaceTicks, supervisorIdleTicks, copyTicks, moveTicks, borrowTicks, budgetTicks,
            traceDisabledTicks, traceEnabledTicks, telemetryTicks, inspectorTicks, checkpointTicks);
    }

    private static Harness CreateHarness()
    {
        var kernel = new RuntimeKernel();
        var principal = TestFixtures.Create(kernel, 1190, 11190, identity: "phase11-control").Handle;
        var supervisorCapability = kernel.MintCapability(new(11190), principal, ResourceKind.KernelService,
            CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;
        var checkpointCapability = kernel.MintCapability(new(11190), principal, ResourceKind.KernelService,
            CapabilityResourceIds.CheckpointAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var supervisor = kernel.CreateServiceSupervisor(principal, supervisorCapability).Value!;
        return new(kernel, supervisor, principal, checkpointCapability);
    }

    private static ManagedServiceDefinition Definition(
        string name,
        ulong processId,
        ulong domainId,
        ServiceContractIdentity contract,
        ulong generation = 1,
        IEnumerable<ServiceDependencyRequirementV1>? dependencies = null) =>
        new(Plan(name, processId, domainId, contract, generation, dependencies), name);

    private static ComponentAdmissionPlan Plan(
        string name,
        ulong processId,
        ulong domainId,
        ServiceContractIdentity contract,
        ulong generation = 1,
        IEnumerable<ServiceDependencyRequirementV1>? dependencies = null,
        ulong checkpointBytes = 8192,
        ulong traceBytes = 65536)
    {
        var provided = new ProvidedServiceManifestV1(name, contract);
        var process = TestFixtures.Manifest(processId, domainId, generation, $"{name}-entry");
        var manifest = new ServiceManifestV1(new(name), new("1"), Digest(Image), process, [provided],
            dependencies: dependencies,
            budgetRequests:
            [
                new(ServiceBudgetDimension.OwnedMemoryBytes, 1_048_576),
                new(ServiceBudgetDimension.IpcMessages, 4096),
                new(ServiceBudgetDimension.IpcBytes, 1_048_576),
                new(ServiceBudgetDimension.ExternalOperations, 16),
                new(ServiceBudgetDimension.CheckpointStorageBytes, checkpointBytes),
                new(ServiceBudgetDimension.TraceTelemetryBufferBytes, traceBytes),
            ],
            checkpointPolicy: new(ServiceCheckpointMode.OperatorRequested),
            telemetryPolicy: new(ServiceTelemetryVisibility.ServiceAggregate, traceBytes));
        return new(manifest, Image, providedServices: [new(provided, Protocol(contract))]);
    }

    private static ComponentAdmissionPlan CheckpointPlan(
        string name,
        ulong processId,
        ulong domainId,
        ulong generation,
        ulong checkpointBytes,
        ulong ownedMemoryBytes = 4096)
    {
        var process = TestFixtures.Manifest(processId, domainId, generation, $"{name}-entry");
        var manifest = new ServiceManifestV1(new(name), new("1"), Digest(Image), process,
            budgetRequests:
            [
                new(ServiceBudgetDimension.OwnedMemoryBytes, ownedMemoryBytes),
                new(ServiceBudgetDimension.ExternalOperations, 4),
                new(ServiceBudgetDimension.CheckpointStorageBytes, checkpointBytes),
            ],
            checkpointPolicy: new(ServiceCheckpointMode.OperatorRequested));
        return new(manifest, Image);
    }

    private static (ChannelEndpointHandle Left, ChannelEndpointHandle Right) MoveChannel(
        RuntimeKernel kernel, ProcessHandle left, ProcessHandle right, int capacity)
    {
        var payload = new RequestPayloadDescriptorV1(RequestPayloadKind.Ownership, "payload",
            typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer);
        var message = new ProtocolMessageDescriptorV1(1, "Move", consumes: ["payload"], requestPayload: payload);
        var protocol = new ProtocolDefinitionV1("Phase11Move", Guid.NewGuid().ToString("N"), "Ready", null,
            [message], [new(1, "Ready", "Ready")]);
        return kernel.CreateChannel(left, right, protocol, capacity).Value;
    }

    private static (ChannelEndpointHandle Left, ChannelEndpointHandle Right) CopyChannel(
        RuntimeKernel kernel, ProcessHandle left, ProcessHandle right, int capacity)
    {
        var payload = new RequestPayloadDescriptorV1(RequestPayloadKind.Bounded, "payload",
            typeof(IpcCopyPayload).FullName, 64);
        var message = new ProtocolMessageDescriptorV1(1, "Copy", requestPayload: payload);
        var protocol = new ProtocolDefinitionV1("Phase11Copy", Guid.NewGuid().ToString("N"), "Ready", null,
            [message], [new(1, "Ready", "Ready")]);
        return kernel.CreateChannel(left, right, protocol, capacity).Value;
    }

    private static (ChannelEndpointHandle Left, ChannelEndpointHandle Right) BorrowChannel(
        RuntimeKernel kernel, ProcessHandle left, ProcessHandle right, int capacity)
    {
        var payload = new RequestPayloadDescriptorV1(RequestPayloadKind.Ownership, "payload",
            typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer", ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer);
        var message = new ProtocolMessageDescriptorV1(1, "Borrow", borrows: ["payload"], requestPayload: payload);
        var protocol = new ProtocolDefinitionV1("Phase11Borrow", Guid.NewGuid().ToString("N"), "Ready", null,
            [message], [new(1, "Ready", "Ready")]);
        return kernel.CreateChannel(left, right, protocol, capacity).Value;
    }

    private static ServiceContractIdentity Contract(string name) => new(name, "1", $"{name.ToLowerInvariant()}-digest");
    private static ProtocolDefinitionV1 Protocol(ServiceContractIdentity contract) =>
        new(contract.Name, contract.Digest, "Idle", ["Done"], [new(1, "Invoke")], [new(1, "Idle", "Done")]);
    private static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record Harness(RuntimeKernel Kernel, CapabilityAwareServiceSupervisor Supervisor,
        ProcessHandle Principal, CapabilityId CheckpointCapability);

    private sealed class MonotonicClock(long timestamp) : TimeProvider
    {
        private long _timestamp = timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void AdvanceTo(long timestampValue) => _timestamp = timestampValue;
    }
}
