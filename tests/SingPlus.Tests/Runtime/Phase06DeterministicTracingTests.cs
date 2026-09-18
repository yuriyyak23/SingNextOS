using System.Text.Json;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Runtime;

public sealed class Phase06DeterministicTracingTests
{
    [Fact]
    public void PerProducerOrderingIsDeterministicAndStaleSessionFailsClosed()
    {
        var kernel = new RuntimeKernel();
        var owner = TestFixtures.Create(kernel, 601, 6001).Handle;
        var trace = kernel.StartTraceSession(owner, 8).Value!;

        Record(kernel, owner, "one", "accepted");
        Record(kernel, owner, "two", "closed");
        var snapshot = kernel.InspectTrace(owner, trace.Session).Value!;

        Assert.Equal([1UL, 2UL], snapshot.Events.Select(item => item.Sequence.Value));
        Assert.True(TraceReplayEngine.ReplayDiagnostic(snapshot).Deterministic);
        var stale = trace.Session with { Generation = new(trace.Session.Generation.Value + 1) };
        Assert.Equal(KernelError.StaleGeneration, kernel.InspectTrace(owner, stale).Error);
        Assert.True(kernel.StopTraceSession(owner, trace.Session).IsSuccess);
    }

    [Fact]
    public void OverflowIsExplicitAndBackpressureIsTestOnlyDisposition()
    {
        var kernel = new RuntimeKernel();
        var owner = TestFixtures.Create(kernel, 602, 6002).Handle;
        var trace = kernel.StartTraceSession(owner, 2, TraceOverflowPolicy.DropWithMarker).Value!;
        Record(kernel, owner, "one", "ok");
        Record(kernel, owner, "two", "ok");
        Record(kernel, owner, "three", "ok");

        var snapshot = kernel.InspectTrace(owner, trace.Session).Value!;
        Assert.False(snapshot.Complete);
        Assert.Equal(1UL, snapshot.DroppedEventCount);
        Assert.Contains(snapshot.Events, item => item.Kind == TraceEventKind.BufferOverflow && item.Incomplete);
        Assert.Contains(TraceReplayEngine.ReplayDiagnostic(snapshot).Divergences,
            item => item.Kind == TraceDivergenceKind.IncompleteTrace);

        var backpressure = kernel.StartTraceSession(owner, 1, TraceOverflowPolicy.BackpressureTestMode).Value!;
        Record(kernel, owner, "first", "ok");
        Assert.Equal(KernelError.TraceBackpressure,
            kernel.RecordTraceSemanticEvent(owner, TraceEventKind.SupervisorObservation,
                new(new("second")), new("service", "second", "ready", "ok")).Error);
        Assert.True(kernel.InspectTrace(owner, backpressure.Session).Value!.Complete);
    }

    [Fact]
    public void CrossProcessProjectionRequiresDedicatedReadCapability()
    {
        var kernel = new RuntimeKernel();
        var owner = TestFixtures.Create(kernel, 603, 6003).Handle;
        var inspector = TestFixtures.Create(kernel, 604, 6004).Handle;
        var trace = kernel.StartTraceSession(owner, 4).Value!;
        Record(kernel, owner, "self", "ok");

        Assert.Equal(KernelError.ProjectionDenied, kernel.InspectTrace(inspector, trace.Session).Error);
        var wrong = kernel.MintCapability(new(6004), inspector, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Read).Value!.CapabilityId;
        Assert.Equal(KernelError.ProjectionDenied, kernel.InspectTrace(inspector, trace.Session, wrong).Error);
        var allowed = kernel.MintCapability(new(6004), inspector, ResourceKind.KernelService,
            CapabilityResourceIds.TraceInspection, CapabilityRights.Read).Value!.CapabilityId;
        Assert.True(kernel.InspectTrace(inspector, trace.Session, allowed).IsSuccess);
    }

    [Fact]
    public void TraceDtosContainMetadataOnlyAndNeverAuthority()
    {
        var data = new TraceSemanticData("external-operation", "42:1", "submitted", "accepted", "sha256:abcd");
        var item = new SemanticTraceEvent(default, new(1), 1, TraceEventKind.ExternalOperationSubmitted,
            new("external:42"), null, default, data);
        var json = JsonSerializer.Serialize(item);

        Assert.False(item.AuthorizesEffect);
        Assert.False(item.AuthorizesReplayEffect);
        Assert.False(data.ContainsPayload);
        Assert.False(data.ContainsCapabilityMaterial);
        Assert.DoesNotContain("provider-token-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("capability-secret-value", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DeterministicModelReplayReportsTypedSemanticDivergence()
    {
        var kernel = new RuntimeKernel();
        var owner = TestFixtures.Create(kernel, 605, 6005).Handle;
        var trace = kernel.StartTraceSession(owner, 4).Value!;
        Record(kernel, owner, "model", "accepted");
        var snapshot = kernel.InspectTrace(owner, trace.Session).Value!;

        Assert.True(TraceReplayEngine.ReplayDeterministicModel(snapshot, item => item.Data.Outcome).Deterministic);
        var divergent = TraceReplayEngine.ReplayDeterministicModel(snapshot, _ => "rejected");
        Assert.False(divergent.Deterministic);
        Assert.Contains(divergent.Divergences, item => item.Kind == TraceDivergenceKind.ProviderSemanticMismatch);
        Assert.False(divergent.AuthorizesEffect);
    }

    [Fact]
    public void HybridCpuEvidenceCorrelationCannotRestoreRevokedCapability()
    {
        var kernel = new RuntimeKernel();
        var owner = TestFixtures.Create(kernel, 606, 6006).Handle;
        var trace = kernel.StartTraceSession(owner, 4).Value!;
        var correlation = new CausalCorrelationId("hybrid-request:7");
        Assert.True(kernel.RecordTraceSemanticEvent(owner, TraceEventKind.ExternalRuntimeReplayEvidence,
            new(correlation), new("hybridcpu-evidence", "request:7", "retired", "correlated", "sha256:evidence")).IsSuccess);
        var capability = kernel.MintCapability(new(6006), owner, ResourceKind.Compute, "model-compute",
            CapabilityRights.Execute).Value!.CapabilityId;
        Assert.True(kernel.RevokeCapability(capability).IsSuccess);

        var report = TraceReplayEngine.CorrelateExternalRuntimeEvidence(
            kernel.InspectTrace(owner, trace.Session).Value!, correlation, "sha256:certificate");

        Assert.True(report.Deterministic);
        Assert.False(report.AuthorizesEffect);
        Assert.False(report.AuthorizesProviderSubmission);
        Assert.Equal(KernelError.CapabilityRevoked,
            kernel.ValidateCapability(owner, capability, CapabilityRights.Execute).Error);
    }

    [Fact]
    public void RealIpcConsumerPreservesExplicitCausalParentWithoutTracingPayload()
    {
        var kernel = new RuntimeKernel();
        var sender = TestFixtures.Create(kernel, 607, 6007).Handle;
        var receiver = TestFixtures.Create(kernel, 608, 6008).Handle;
        var trace = kernel.StartTraceSession(sender, 8).Value!;
        var capabilityCorrelation = new CausalCorrelationId("capability:request");
        var ipcCorrelation = new CausalCorrelationId("ipc:request");
        _ = kernel.MintCapability(new(6007), sender, ResourceKind.KernelService, "semantic-service",
            CapabilityRights.Read, new(capabilityCorrelation));
        var protocol = new ProtocolDefinitionV1("TraceIpc", "trace-ipc", "Idle", null,
            [new(1, "Ping")], [new(1, "Idle", "Idle")]);
        var channel = kernel.CreateChannel(sender, receiver, protocol, 1).Value;

        Assert.True(kernel.Send(sender, receiver, channel.Left, 1,
            traceContext: new(ipcCorrelation, capabilityCorrelation)).IsSuccess);
        var snapshot = kernel.InspectTrace(sender, trace.Session).Value!;
        var sent = Assert.Single(snapshot.Events, item => item.Kind == TraceEventKind.IpcSent);

        Assert.Equal(ipcCorrelation, sent.Correlation);
        Assert.Equal(capabilityCorrelation, sent.ParentCorrelation);
        Assert.DoesNotContain("must-not-serialize", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public void OwningSubsystemsEmitLifecycleEventsOnlyAfterSuccessfulTransitions()
    {
        var kernel = new RuntimeKernel();
        var owner = TestFixtures.Create(kernel, 609, 6009).Handle;
        var trace = kernel.StartTraceSession(owner, 32).Value!;
        var capability = kernel.MintCapability(new(6009), owner, ResourceKind.File, "trace-owned-file",
            CapabilityRights.Read).Value!.CapabilityId;
        Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        var scope = kernel.CreateCancellationScope(owner).Value!.Scope;
        Assert.True(kernel.RequestCancellation(owner, scope).IsSuccess);
        var buffer = kernel.AllocateBuffer<byte>(owner, 8).Value!;
        var use = kernel.AcquireRegionUse(owner, buffer.Handle, RegionUseMode.ReadOnly, new(0, 8)).Value!;
        Assert.True(kernel.ReleaseRegionUse(owner, use.Handle).IsSuccess);
        var contract = new ServiceContractIdentity("TraceService", "1", "trace-service-digest");
        Assert.True(kernel.RegisterService(owner, "trace-service", contract,
            new(contract.Name, contract.Digest, "Idle", ["Done"], [new(1, "Invoke")], [new(1, "Idle", "Done")])).IsSuccess);

        var snapshot = kernel.InspectTrace(owner, trace.Session).Value!;

        Assert.Contains(snapshot.Events, item => item.Kind == TraceEventKind.CapabilityRevoked);
        Assert.Equal(2, snapshot.Events.Count(item => item.Kind == TraceEventKind.DeadlineCancellation));
        Assert.Equal(2, snapshot.Events.Count(item => item.Kind == TraceEventKind.RegionUse));
        Assert.Contains(snapshot.Events, item => item.Kind == TraceEventKind.ServiceLifecycle);
    }

    private static void Record(RuntimeKernel kernel, ProcessHandle owner, string correlation, string outcome) =>
        Assert.True(kernel.RecordTraceSemanticEvent(owner, TraceEventKind.SupervisorObservation,
            new(new(correlation)), new("service", correlation, "observed", outcome)).IsSuccess);
}
