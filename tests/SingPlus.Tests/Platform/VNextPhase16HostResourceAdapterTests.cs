using System.Collections;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class VNextPhase16HostResourceAdapterTests
{
    [Fact]
    public void ExactHostComputeContourExecutesPrepareBindPendingAndConservativeClosure()
    {
        Context context = Create(enabled: true);
        PlatformResourceReservation reservation = context.Bridge.PrepareResource(context.Domain, context.Subject,
            Correlation(), Envelope()).Value!;
        PlatformOperationIdentity operation = context.Provider.StageOperation(reservation.DomainLease).Value!;
        Assert.True(context.Provider.AdvanceOperation(operation, PlatformCompletionState.Pending).IsSuccess);
        PlatformResourceSubmissionBinding binding = context.Bridge.BindResourceSubmission(reservation, operation).Value!;

        PlatformResourceUsageEvidence pending = context.Bridge.ReconcileResourceUsage(binding).Value!;
        Assert.Equal(PlatformResourceReconciliationState.Pending, pending.State);
        Assert.False(pending.IsTerminal);
        Assert.Equal(0UL, pending.ConsumedAmount);

        Assert.True(context.Provider.AdvanceOperation(operation, PlatformCompletionState.Completed).IsSuccess);
        Assert.True(context.Provider.AdvanceOperation(operation, PlatformCompletionState.Closed).IsSuccess);
        PlatformResourceUsageEvidence terminal = context.Bridge.ReconcileResourceUsage(binding).Value!;
        Assert.Equal(PlatformResourceReconciliationState.ConservativeWorstCase, terminal.State);
        Assert.True(terminal.IsTerminal);
        Assert.Equal(Envelope().Amount, terminal.ConsumedAmount);
        Assert.Equal(terminal, context.Bridge.ReconcileResourceUsage(binding).Value);
        Assert.Equal(1, context.Provider.PrepareResourceCallCount);
        Assert.Equal(1, context.Provider.BindResourceSubmissionCallCount);
        Assert.Equal(3, context.Provider.ReconcileResourceUsageCallCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FinalResourceReceiptCannotWrapOrDiscardExactTerminalEvidence(bool terminal)
    {
        Context context = Create(enabled: true);
        PlatformResourceReservation reservation = context.Bridge.PrepareResource(context.Domain, context.Subject, Correlation(), Envelope()).Value!;
        PlatformOperationIdentity operation = context.Provider.StageOperation(reservation.DomainLease).Value!;
        Assert.True(context.Provider.AdvanceOperation(operation, PlatformCompletionState.Pending).IsSuccess);
        PlatformResourceSubmissionBinding binding = context.Bridge.BindResourceSubmission(reservation, operation).Value!;
        if (terminal)
        {
            Assert.True(context.Provider.AdvanceOperation(operation, PlatformCompletionState.Completed).IsSuccess);
            Assert.True(context.Provider.AdvanceOperation(operation, PlatformCompletionState.Closed).IsSuccess);
        }
        IDictionary records = (global::System.Collections.IDictionary)typeof(HostPlatformAuthorityProvider)
            .GetField("_resourceReservations", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!
            .GetValue(context.Provider)!;
        object record = records[reservation.ReservationId]!;
        PropertyInfo sequence = record.GetType().GetProperty("NextReceiptSequence")!;
        sequence.SetValue(record, ulong.MaxValue);

        KernelResult<PlatformResourceUsageEvidence> last = context.Bridge.ReconcileResourceUsage(binding);

        Assert.True(last.IsSuccess, last.Message);
        Assert.Equal(ulong.MaxValue, last.Value!.ReceiptSequence);
        Assert.Equal(terminal, last.Value.IsTerminal);
        Assert.Equal(0UL, (ulong)sequence.GetValue(record)!);
        for (int retry = 0; retry < 2; retry++)
        {
            KernelResult<PlatformResourceUsageEvidence> next = context.Bridge.ReconcileResourceUsage(binding);
            if (terminal) Assert.Equal(last.Value, next.Value);
            else Assert.Equal(KernelError.PlatformFaulted, next.Error);
            Assert.Equal(0UL, (ulong)sequence.GetValue(record)!);
        }
        Assert.Equal(PlatformAuthorityStatus.Denied, context.Provider.CancelPreparedResource(reservation).Status);
        Assert.Equal(binding, context.Provider.BindResourceSubmission(reservation, operation).Value);
    }

    [Fact]
    public void FeatureIsOptInAndUnsupportedOrWidenedDimensionsFailClosed()
    {
        Context disabled = Create(enabled: false);
        Assert.Equal(KernelError.PlatformUnsupported,
            disabled.Bridge.PrepareResource(disabled.Domain, disabled.Subject,
                Correlation(), Envelope()).Error);

        Context enabled = Create(enabled: true);
        ResourceEnvelopeV1 throughput = Envelope() with
        {
            Family = ResourceDimensionFamilyV1.Throughput,
            ResourceClass = ResourceClassV1.NetworkThroughput,
            Unit = ResourceUnitV1.BytesPerWindow,
            WindowNanoseconds = 1000
        };
        Assert.Equal(KernelError.PlatformUnsupported,
            enabled.Bridge.PrepareResource(enabled.Domain, enabled.Subject,
                Correlation(), throughput).Error);
    }

    [Fact]
    public void CancellationIsPreSubmitOnlyAndStaleGenerationsCannotBindOrReconcile()
    {
        Context context = Create(enabled: true);
        PlatformResourceReservation reservation = context.Bridge.PrepareResource(context.Domain, context.Subject,
            Correlation(), Envelope()).Value!;
        Assert.True(context.Bridge.CancelPreparedResource(reservation).IsSuccess);
        Assert.True(context.Bridge.CancelPreparedResource(reservation).IsSuccess);
        PlatformOperationIdentity operation = context.Provider.StageOperation(reservation.DomainLease).Value!;
        Assert.Equal(KernelError.InvalidTransition,
            context.Bridge.BindResourceSubmission(reservation, operation).Error);

        PlatformResourceReservation second = context.Bridge.PrepareResource(context.Domain, context.Subject,
            Correlation() with { CorrelationId = new PlatformResourceCorrelationId(102) }, Envelope()).Value!;
        PlatformOperationIdentity staleOperation = operation with { Generation = new PlatformOperationGeneration(2) };
        Assert.Equal(KernelError.StaleGeneration,
            context.Bridge.BindResourceSubmission(second, staleOperation).Error);
        Assert.Equal(KernelError.StaleGeneration,
            context.Bridge.CancelPreparedResource(second with
            {
                Generation = new PlatformResourceReservationGeneration(2)
            }).Error);
    }

    private static Context Create(bool enabled)
    {
        HostPlatformAuthorityProvider provider = new HostPlatformAuthorityProvider(enableResourceAccounting: enabled);
        PlatformAuthorityBridge bridge = new PlatformAuthorityBridge(provider);
        PlatformDomainIdentity subject = new PlatformDomainIdentity(new DomainId(71),
            new ProcessHandle(new ProcessId(73), 5));
        PlatformDomainBinding domain = bridge.BindDomain(subject).Value!;
        return new Context(provider, bridge, subject, domain);
    }

    private static PlatformResourceCorrelation Correlation() =>
        new(new PlatformResourceCorrelationId(101), new PlatformResourceCorrelationGeneration(1));

    private static ResourceEnvelopeV1 Envelope() => new(ResourceEnvelopeV1.CurrentVersion,
        ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
        ResourceUnitV1.Nanoseconds, 1000, 0, "host:compute-v1");

    private sealed record Context(
        HostPlatformAuthorityProvider Provider,
        PlatformAuthorityBridge Bridge,
        PlatformDomainIdentity Subject,
        PlatformDomainBinding Domain);
}
