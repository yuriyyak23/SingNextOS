using System.Security.Cryptography;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class Phase09StructuredTelemetryTests
{
    private static readonly byte[] Image = [0x09, 0x99];

    [Fact]
    public void SelfProjectionReflectsAuthoritativeBudgetMemoryDeadlineTraceAndCheckpointState()
    {
        var kernel = new RuntimeKernel();
        var admin = Admin(kernel, 991, 9901);
        var component = kernel.AdmitComponent(Plan("telemetry-self", 901, 9001, 1)).Value!;
        var buffer = kernel.AllocateBuffer<byte>(component.Process, 16).Value!;
        _ = kernel.CreateCancellationScope(component.Process).Value!;
        var trace = kernel.StartTraceSession(component.Process, 4).Value!;
        Assert.True(kernel.RecordTraceSemanticEvent(component.Process, TraceEventKind.ProcessLifecycle,
            new(new("telemetry-test")), new("process", "self", "running", "sample")).IsSuccess);
        var image = kernel.CreateOrdinaryCheckpoint(admin.Process, admin.CheckpointCapability,
            component.Identity, new byte[] { 1, 2, 3 }, [new(buffer)]).Value!;

        var snapshot = kernel.ProjectTelemetry(component.Process, component.Process).Value!;

        Assert.False(snapshot.AuthorizesEffect);
        Assert.False(snapshot.SatisfiesSecurityEvidence);
        Assert.Equal(1, snapshot.Resources.OwnedRegionCount);
        Assert.Equal(16UL, snapshot.Resources.OwnedMemoryBytes);
        Assert.Equal(1, snapshot.Trace.Sessions);
        Assert.True(snapshot.Trace.BufferedEvents >= 1);
        Assert.Equal(1, snapshot.Checkpoints.Committed);
        Assert.Equal(19UL, snapshot.Checkpoints.StoredBytes);
        Assert.Contains(snapshot.BudgetUsage, usage => usage.Dimension == ServiceBudgetDimension.OwnedMemoryBytes && usage.Used == 16);
        Assert.True(kernel.StopTraceSession(component.Process, trace.Session).IsSuccess);
        Assert.True(kernel.DeleteOrdinaryCheckpoint(admin.Process, admin.CheckpointCapability, image.Handle).IsSuccess);
    }

    [Fact]
    public void CrossServiceProjectionRequiresExactDedicatedCapabilityAndLeaksNoHostTopology()
    {
        var kernel = new RuntimeKernel();
        var target = kernel.AdmitComponent(Plan("target", 902, 9002, 1)).Value!;
        var observer = kernel.AdmitComponent(Plan("observer", 903, 9003, 1)).Value!;

        Assert.Equal(KernelError.ProjectionDenied,
            kernel.ProjectTelemetry(observer.Process, target.Process).Error);
        var wrong = kernel.MintCapability(new(9003), observer.Process, ResourceKind.KernelService,
            CapabilityResourceIds.TraceInspection, CapabilityRights.Read).Value!.CapabilityId;
        Assert.Equal(KernelError.ProjectionDenied,
            kernel.ProjectTelemetry(observer.Process, target.Process, TelemetryProjectionClass.PrivilegedSystemDiagnostics, wrong).Error);
        var exact = kernel.MintCapability(new(9003), observer.Process, ResourceKind.KernelService,
            CapabilityResourceIds.TelemetryInspection, CapabilityRights.Read).Value!.CapabilityId;

        var privileged = kernel.ProjectTelemetry(observer.Process, target.Process,
            TelemetryProjectionClass.PrivilegedSystemDiagnostics, exact).Value!;

        var publicSurface = string.Join('|', privileged.GetType().GetProperties().Select(property => property.Name));
        Assert.DoesNotContain("Cxl", publicSurface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Topology", publicSurface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RecoveryToken", publicSurface, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Accelerator", publicSurface, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BoundedSubscriptionReportsOverflowAndOldGenerationCannotReadAfterRestart()
    {
        var kernel = new RuntimeKernel();
        var original = kernel.AdmitComponent(Plan("subscription", 904, 9004, 1)).Value!;
        var subscription = kernel.StartTelemetrySubscription(original.Process, original.Process,
            TelemetryProjectionClass.SelfOperational, 1, TelemetrySubscriptionOverflowPolicy.DropOldestWithMarker).Value!;
        Assert.True(kernel.SampleTelemetrySubscription(original.Process, subscription.Subscription).IsSuccess);
        Assert.True(kernel.SampleTelemetrySubscription(original.Process, subscription.Subscription).IsSuccess);
        var batch = kernel.ReadTelemetrySubscription(original.Process, subscription.Subscription).Value!;
        Assert.Single(batch.Snapshots);
        Assert.Equal(1UL, batch.DroppedSnapshots);
        Assert.False(batch.Complete);

        Assert.True(kernel.DrainComponent(original.Identity).Value!.Reclaimable);
        Assert.True(kernel.RetireReclaimableComponent(original.Identity, original.Process).IsSuccess);
        var replacement = kernel.AdmitComponent(Plan("subscription", 904, 9004, 2)).Value!;

        Assert.Equal(KernelError.StaleHandle,
            kernel.ReadTelemetrySubscription(original.Process, subscription.Subscription).Error);
        Assert.Equal(KernelError.ProjectionDenied,
            kernel.ReadTelemetrySubscription(replacement.Process, subscription.Subscription).Error);
        Assert.True(kernel.StartTelemetrySubscription(replacement.Process, replacement.Process,
            TelemetryProjectionClass.SelfOperational, 1, TelemetrySubscriptionOverflowPolicy.RejectSample).IsSuccess);
    }

    [Fact]
    public void SecurityEvidenceProjectionRemainsTypedDataAndCannotBeReplacedByTelemetry()
    {
        var kernel = new RuntimeKernel();
        var component = kernel.AdmitComponent(Plan("evidence", 905, 9005, 1)).Value!;
        var evidence = new EvidenceRecord(new("test-measurement", 1),
            new(new(9005), new(905), 1, null, null), new("test-producer"),
            EvidenceVisibilityClass.SecurityMeasurement, "ok", "digest-only", new(7, 11), false);

        var projected = new SecurityEvidenceProjection(evidence, 13);
        var telemetry = kernel.ProjectTelemetry(component.Process, component.Process).Value!;

        Assert.Equal(new EvidenceFreshness(7, 11), projected.Freshness);
        Assert.False(projected.AuthorizesEffect);
        Assert.False(telemetry.SatisfiesSecurityEvidence);
        Assert.NotEqual(typeof(SecurityEvidenceProjection), telemetry.GetType());
    }

    [Fact]
    public void ManifestPolicyAndBudgetBoundTelemetrySubscriptions()
    {
        var kernel = new RuntimeKernel();
        var disabled = kernel.AdmitComponent(Plan("disabled", 906, 9006, 1,
            telemetry: ServiceTelemetryPolicyV1.None)).Value!;
        Assert.Equal(KernelError.ProjectionDenied,
            kernel.ProjectTelemetry(disabled.Process, disabled.Process).Error);

        var bounded = kernel.AdmitComponent(Plan("bounded", 907, 9007, 1,
            telemetry: new(ServiceTelemetryVisibility.Self, StructuredTelemetryContract.EstimatedSnapshotBytes))).Value!;
        Assert.Equal(KernelError.BudgetExceeded,
            kernel.StartTelemetrySubscription(bounded.Process, bounded.Process,
                TelemetryProjectionClass.SelfOperational, 2, TelemetrySubscriptionOverflowPolicy.RejectSample).Error);
        var admitted = kernel.StartTelemetrySubscription(bounded.Process, bounded.Process,
            TelemetryProjectionClass.SelfOperational, 1, TelemetrySubscriptionOverflowPolicy.RejectSample).Value!;
        Assert.Equal(StructuredTelemetryContract.EstimatedSnapshotBytes,
            Usage(kernel.QueryBudget(bounded.ProcessBudget).Value!, ServiceBudgetDimension.TraceTelemetryBufferBytes).Used);
        Assert.True(kernel.CloseTelemetrySubscription(bounded.Process, admitted.Subscription).IsSuccess);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(bounded.ProcessBudget).Value!, ServiceBudgetDimension.TraceTelemetryBufferBytes).Used);
    }

    [Fact]
    public void IpcQueueDepthIsProjectedFromTheExistingChannelRegistry()
    {
        var kernel = new RuntimeKernel();
        var sender = kernel.AdmitComponent(Plan("ipc-metrics", 908, 9008, 1)).Value!;
        var receiver = TestFixtures.Create(kernel, 909, 9009).Handle;
        var protocol = new ProtocolDefinitionV1("TelemetryIpc", "telemetry-ipc", "Idle", null,
            [new(1, "Ping")], [new(1, "Idle", "Idle")]);
        var endpoints = kernel.CreateChannel(sender.Process, receiver, protocol, 4).Value;
        Assert.True(kernel.Send(sender.Process, receiver, endpoints.Left, 1).IsSuccess);

        var projected = kernel.ProjectTelemetry(sender.Process, sender.Process).Value!;

        Assert.Equal(1, projected.Resources.IpcChannelCount);
        Assert.Equal(1, projected.Resources.IpcQueuedMessages);
    }

    [Fact]
    public void SupervisorConsumerProjectsHealthAndRestartStateWithoutAuthority()
    {
        var kernel = new RuntimeKernel();
        var principal = TestFixtures.Create(kernel, 910, 9010, identity: "telemetry-supervisor").Handle;
        var control = kernel.MintCapability(new(9010), principal, ResourceKind.KernelService,
            CapabilityResourceIds.ServiceSupervisor, CapabilityRights.Configure | CapabilityRights.Execute).Value!.CapabilityId;
        var supervisor = kernel.CreateServiceSupervisor(principal, control).Value!;
        var contract = new ServiceContractIdentity("ManagedTelemetry", "1", "managed-telemetry-digest");
        var provided = new ProvidedServiceManifestV1("managed-telemetry", contract);
        var process = TestFixtures.Manifest(911, 9011, 1, "managed-telemetry-entry");
        var manifest = new ServiceManifestV1(new("managed-telemetry"), new("1"), Digest(Image), process,
            [provided], telemetryPolicy: new(ServiceTelemetryVisibility.ServiceAggregate, 4096));
        var definition = new ManagedServiceDefinition(new(manifest, Image,
            providedServices: [new(provided, new(contract.Name, contract.Digest, "Idle", ["Done"], [new(1, "Invoke")], [new(1, "Idle", "Done")]))]),
            "managed-telemetry");
        Assert.True(supervisor.Register(definition).IsSuccess);
        var started = supervisor.Start(definition.Identity).Value!.Snapshot;

        var projected = supervisor.ProjectTelemetry(started.Instance!.Value).Value!;

        Assert.Equal(TelemetryProjectionClass.ServiceAggregate, projected.Projection);
        Assert.Equal(ServiceLifecycleState.Ready, projected.Service!.State);
        Assert.Equal(ServiceHealthState.Ready, projected.Service.Health);
        Assert.False(projected.AuthorizesEffect);
    }

    private static ComponentAdmissionPlan Plan(
        string name,
        ulong processId,
        ulong domainId,
        ulong generation,
        ServiceTelemetryPolicyV1? telemetry = null)
    {
        var process = TestFixtures.Manifest(processId, domainId, generation, $"{name}-entry");
        var manifest = new ServiceManifestV1(new(name), new("1"), Digest(Image), process,
            budgetRequests:
            [
                new(ServiceBudgetDimension.OwnedMemoryBytes, 4096),
                new(ServiceBudgetDimension.TraceTelemetryBufferBytes, 4096),
                new(ServiceBudgetDimension.CheckpointStorageBytes, 4096),
            ],
            checkpointPolicy: new(ServiceCheckpointMode.OperatorRequested),
            telemetryPolicy: telemetry ?? new(ServiceTelemetryVisibility.Self, 4096));
        return new(manifest, Image);
    }

    private static (ProcessHandle Process, CapabilityId CheckpointCapability) Admin(
        RuntimeKernel kernel,
        ulong processId,
        ulong domainId)
    {
        var process = TestFixtures.Create(kernel, processId, domainId).Handle;
        var capability = kernel.MintCapability(new(domainId), process, ResourceKind.KernelService,
            CapabilityResourceIds.CheckpointAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        return (process, capability);
    }

    private static BudgetUsage Usage(BudgetAccountSnapshot account, ServiceBudgetDimension dimension) =>
        Assert.Single(account.Usage, usage => usage.Dimension == dimension);

    private static string Digest(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
