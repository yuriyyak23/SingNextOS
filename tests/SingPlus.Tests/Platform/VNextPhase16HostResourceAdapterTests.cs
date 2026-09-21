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
        var context = Create(enabled: true);
        var reservation = context.Bridge.PrepareResource(context.Domain, context.Subject,
            Correlation(), Envelope()).Value!;
        var operation = context.Provider.StageOperation(reservation.DomainLease).Value!;
        Assert.True(context.Provider.AdvanceOperation(operation, PlatformCompletionState.Pending).IsSuccess);
        var binding = context.Bridge.BindResourceSubmission(reservation, operation).Value!;

        var pending = context.Bridge.ReconcileResourceUsage(binding).Value!;
        Assert.Equal(PlatformResourceReconciliationState.Pending, pending.State);
        Assert.False(pending.IsTerminal);
        Assert.Equal(0UL, pending.ConsumedAmount);

        Assert.True(context.Provider.AdvanceOperation(operation, PlatformCompletionState.Completed).IsSuccess);
        Assert.True(context.Provider.AdvanceOperation(operation, PlatformCompletionState.Closed).IsSuccess);
        var terminal = context.Bridge.ReconcileResourceUsage(binding).Value!;
        Assert.Equal(PlatformResourceReconciliationState.ConservativeWorstCase, terminal.State);
        Assert.True(terminal.IsTerminal);
        Assert.Equal(Envelope().Amount, terminal.ConsumedAmount);
        Assert.Equal(terminal, context.Bridge.ReconcileResourceUsage(binding).Value);
        Assert.Equal(1, context.Provider.PrepareResourceCallCount);
        Assert.Equal(1, context.Provider.BindResourceSubmissionCallCount);
        Assert.Equal(3, context.Provider.ReconcileResourceUsageCallCount);
    }

    [Fact]
    public void FeatureIsOptInAndUnsupportedOrWidenedDimensionsFailClosed()
    {
        var disabled = Create(enabled: false);
        Assert.Equal(KernelError.PlatformUnsupported,
            disabled.Bridge.PrepareResource(disabled.Domain, disabled.Subject,
                Correlation(), Envelope()).Error);

        var enabled = Create(enabled: true);
        var throughput = Envelope() with
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
        var context = Create(enabled: true);
        var reservation = context.Bridge.PrepareResource(context.Domain, context.Subject,
            Correlation(), Envelope()).Value!;
        Assert.True(context.Bridge.CancelPreparedResource(reservation).IsSuccess);
        Assert.True(context.Bridge.CancelPreparedResource(reservation).IsSuccess);
        var operation = context.Provider.StageOperation(reservation.DomainLease).Value!;
        Assert.Equal(KernelError.InvalidTransition,
            context.Bridge.BindResourceSubmission(reservation, operation).Error);

        var second = context.Bridge.PrepareResource(context.Domain, context.Subject,
            Correlation() with { CorrelationId = new PlatformResourceCorrelationId(102) }, Envelope()).Value!;
        var staleOperation = operation with { Generation = new PlatformOperationGeneration(2) };
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
        var provider = new HostPlatformAuthorityProvider(enableResourceAccounting: enabled);
        var bridge = new PlatformAuthorityBridge(provider);
        var subject = new PlatformDomainIdentity(new DomainId(71),
            new ProcessHandle(new ProcessId(73), 5));
        var domain = bridge.BindDomain(subject).Value!;
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
