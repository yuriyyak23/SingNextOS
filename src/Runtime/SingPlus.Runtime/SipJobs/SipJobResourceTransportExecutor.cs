namespace SingPlus.Runtime;

internal enum SipJobResourceTransportMode
{
    OrdinarySip = 0,
    FusedTransport,
    Quarantined,
}

internal readonly record struct SipJobResourceTransportResult(
    SipJobResourceTransportMode Mode,
    KernelError Error,
    string? Message)
{
    internal bool IsSuccess => Error == KernelError.None;
}

/// <summary>
/// Removes only the ordinary transport hop. The supplied live owner boundary is
/// invoked exactly once in both successful modes and remains responsible for all
/// capability, budget, Region, provider and publication transitions.
/// </summary>
internal static class SipJobResourceTransportExecutor
{
    internal const string GateName = "FG-VNX-SIPJOB-RESOURCE";

    internal static SipJobResourceTransportResult Execute(
        VNextFeatureGateAuthority gates,
        VNextFeatureGateLease? fusedLease,
        bool possibleSubmit,
        Func<KernelResult> ordinaryTransport,
        Func<KernelResult> liveOwnerBoundary)
    {
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(ordinaryTransport);
        ArgumentNullException.ThrowIfNull(liveOwnerBoundary);

        var disposition = fusedLease is { } lease
            ? gates.Evaluate(lease, possibleSubmit)
            : VNextGateUseDisposition.OrdinaryFallback;
        if (disposition == VNextGateUseDisposition.Quarantine)
            return new(SipJobResourceTransportMode.Quarantined, KernelError.Quarantined,
                "Stale fused work after possible submit requires owner reconciliation and cannot fall back or resubmit.");

        if (disposition == VNextGateUseDisposition.OrdinaryFallback)
        {
            var transport = Invoke(ordinaryTransport);
            if (!transport.IsSuccess)
                return new(SipJobResourceTransportMode.OrdinarySip, transport.Error, transport.Message);
        }

        var owner = Invoke(liveOwnerBoundary);
        return new(disposition == VNextGateUseDisposition.Enabled
                ? SipJobResourceTransportMode.FusedTransport
                : SipJobResourceTransportMode.OrdinarySip,
            owner.Error, owner.Message);
    }

    private static KernelResult Invoke(Func<KernelResult> action)
    {
        try { return action(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { return KernelResult.Fail(KernelError.PlatformFaulted, exception.Message); }
    }
}
