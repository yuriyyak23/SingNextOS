using System.Text.Json;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Runtime;

public sealed class VNextPhase15TelemetryAuthorityBoundaryTests
{
    [Fact]
    public void AuditOnlyGateRemainsDefaultOff()
    {
        Assert.True(VNextFeatureGates.TryResolve("FG-VNX-AUDIT-ONLY", out VNextFeatureGate gate));
        Assert.Equal(VNextFeatureGate.AuditOnly, gate);
        Assert.False(VNextFeatureGates.IsEnabled("FG-VNX-AUDIT-ONLY"));
    }

    [Fact]
    public void DroppedDuplicatedAndReorderedEvidenceCannotChangeBudgetAuthority()
    {
        ResourceBudgetAuthority authority = CreateBudgetAuthority(out ProcessHandle process, out BudgetAccountHandle account);
        BudgetReservationHandle lease = authority.Reserve(process, [Amount(10)], BudgetReservationLifetime.LocalResource,
            AdmissionQosHint.None).Value!.Reservation;
        Assert.True(authority.BindLease(process, lease).IsSuccess);
        Assert.True(authority.BeginConsumption(process, lease).IsSuccess);
        Assert.True(authority.QuarantineLease(process, lease).IsSuccess);
        BudgetReservationSnapshot before = authority.Query(lease).Value!;
        BudgetAccountSnapshot beforeAccount = authority.Query(account).Value!;

        TraceSessionHandle session = new TraceSessionHandle(new(17), new(3));
        TraceProducerHandle producer = new TraceProducerHandle(session, new(5), new(2));
        TraceSessionAdmission admission = new TraceSessionAdmission(session, producer, process, TraceVisibilityClass.Self,
            TraceOverflowPolicy.DropWithMarker, 2, TraceSessionState.Active);
        SemanticTraceEvent first = Event(producer, process, 2, "quarantined");
        SemanticTraceEvent second = Event(producer, process, 1, "consuming");
        TraceSnapshot reorderedAndDuplicated = new TraceSnapshot(admission, [first, second, first], 4, false);

        TraceReplayReport diagnostic = TraceReplayEngine.ReplayDiagnostic(reorderedAndDuplicated);
        TraceReplayReport model = TraceReplayEngine.ReplayDeterministicModel(reorderedAndDuplicated, item => item.Data.Outcome);
        TraceReplayReport correlation = TraceReplayEngine.CorrelateExternalRuntimeEvidence(
            reorderedAndDuplicated, first.Correlation, "sha256:diagnostic-only");

        Assert.False(diagnostic.Deterministic);
        Assert.False(model.Deterministic);
        Assert.False(correlation.AuthorizesEffect);
        Assert.False(correlation.AuthorizesProviderSubmission);
        BudgetSnapshotAssertions.Equal(before, authority.Query(lease).Value!);
        Assert.Equal(beforeAccount.Usage, authority.Query(account).Value!.Usage);
    }

    [Fact]
    public void BoundedTraceOverflowDoesNotBackpressureAuthorityTransition()
    {
        ResourceBudgetAuthority authority = CreateBudgetAuthority(out ProcessHandle process, out BudgetAccountHandle account);
        DeterministicTraceAuthority traces = new DeterministicTraceAuthority(TimeProvider.System);
        TraceSessionAdmission trace = traces.Start(process, 1, TraceOverflowPolicy.BackpressureTestMode,
            TraceVisibilityClass.Self).Value!;
        Assert.True(traces.Record(process, TraceEventKind.SupervisorObservation, new("first"), null,
            new("budget", "opaque:1", "observed", "ok")).IsSuccess);
        Assert.Equal(KernelError.TraceBackpressure,
            traces.Record(process, TraceEventKind.SupervisorObservation, new("second"), null,
                new("budget", "opaque:2", "observed", "ok")).Error);

        KernelResult<BudgetReservationSnapshot> lease = authority.Reserve(process, [Amount(10)], BudgetReservationLifetime.LocalResource,
            AdmissionQosHint.None);

        Assert.True(lease.IsSuccess);
        Assert.Equal(10UL, Assert.Single(authority.Query(account).Value!.Usage,
            usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used);
        Assert.True(traces.Snapshot(trace.Session).Value!.Complete);
    }

    [Fact]
    public void PublicObservabilityContractsExposeEvidenceButNoAuthorityOrProviderPrivateAbi()
    {
        Type[] contractTypes = new[]
        {
            typeof(SemanticTraceEvent), typeof(TraceSnapshot), typeof(TraceReplayReport),
            typeof(StructuredTelemetrySnapshot), typeof(TelemetrySubscriptionBatch),
        };
        string surface = string.Join('|', contractTypes.SelectMany(type => type.GetProperties())
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}:{property.PropertyType.Name}"));
        foreach (string? forbidden in new[]
                 {
                     "CapabilityId", "BudgetReservationHandle", "ExternalOperationHandle", "ProviderHandle",
                     "Opcode", "Lane", "Slot", "VMCS", "IOMMU", "CXL", "PhysicalAddress", "Topology",
                 })
            Assert.DoesNotContain(forbidden, surface, StringComparison.OrdinalIgnoreCase);

        string[] reportMethods = typeof(TraceReplayEngine).GetMethods()
            .Where(method => method.DeclaringType == typeof(TraceReplayEngine))
            .Select(method => method.Name).ToArray();
        Assert.DoesNotContain(reportMethods, name =>
            name.Contains("Submit", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Settle", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Mint", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Release", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Publish", StringComparison.OrdinalIgnoreCase));

        string json = JsonSerializer.Serialize(Event(
            new(new(new(1), new(1)), new(1), new(1)), new(new(7), 3), 1, "observed"));
        Assert.DoesNotContain("provider-private", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("capability-secret", json, StringComparison.OrdinalIgnoreCase);
    }

    private static ResourceBudgetAuthority CreateBudgetAuthority(
        out ProcessHandle process,
        out BudgetAccountHandle account)
    {
        ResourceBudgetAuthority authority = new ResourceBudgetAuthority();
        Assert.True(authority.ConfigureSystem([Amount(10)]).IsSuccess);
        BudgetAccountHandle service = authority.CreateChild(authority.SystemBudget, BudgetAccountLevel.Service, "p15-service",
            [Amount(10)]).Value!.Account;
        account = authority.CreateChild(service, BudgetAccountLevel.ProcessDomain, "p15-process",
            [Amount(10)]).Value!.Account;
        process = new(new(15), 4);
        Assert.True(authority.AttachProcess(process, account).IsSuccess);
        return authority;
    }

    private static SemanticTraceEvent Event(
        TraceProducerHandle producer,
        ProcessHandle process,
        ulong sequence,
        string state) =>
        new(producer, new(sequence), checked((long)sequence), TraceEventKind.SupervisorObservation,
            new($"opaque:{sequence}"), null, process,
            new("resource-observation", $"resource:{sequence}", state, "diagnostic"));

    private static BudgetAmount Amount(ulong amount) =>
        new(ServiceBudgetDimension.ComputeTimeNanoseconds, amount);
}
