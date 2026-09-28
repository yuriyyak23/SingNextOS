using System.Collections.Immutable;
using SingPlus.Contracts;

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

internal readonly record struct SipJobProtectedTransportResult(
    SipJobResourceTransportResult Transport,
    ImmutableArray<SipJobProtectedLabelBinding> LabelBindings,
    SipJobPlanError PlanError,
    SipJobProtectedLabelError LabelError,
    string? Detail)
{
    internal bool IsSuccess => Transport.IsSuccess &&
        PlanError == SipJobPlanError.None && LabelError == SipJobProtectedLabelError.None;
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

    /// <summary>
    /// Named protected closed-value contour. Plan and label sidecars restrict the
    /// existing transport/owner boundary; neither can authorize transport or owner work.
    /// </summary>
    internal static SipJobProtectedTransportResult ExecuteProtected(
        VNextFeatureGateAuthority gates,
        VNextFeatureGateLease? fusedLease,
        bool possibleSubmit,
        SipJobPlanDescriptor plan,
        SipJobClosedCatalog catalog,
        IEnumerable<ProtectedValueLabelSidecarV1> sidecars,
        Func<KernelResult> ordinaryTransport,
        Func<KernelResult> liveOwnerBoundary)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(sidecars);
        ArgumentNullException.ThrowIfNull(ordinaryTransport);
        ArgumentNullException.ThrowIfNull(liveOwnerBoundary);

        ProtectedValueLabelSidecarV1[] exactSidecars = sidecars.ToArray();
        var verified = SipJobPlanVerifier.Verify(plan, catalog);
        if (!verified.IsSuccess)
            return Reject(verified.Failure?.Error ?? SipJobPlanError.Malformed,
                SipJobProtectedLabelError.None,
                verified.Failure?.Detail ?? "Protected SipJob plan verification failed.",
                KernelError.InvalidManifest);

        var labels = SipJobProtectedLabelFlow.Verify(
            SipJobProtectedLabelFlow.Version, plan, verified.Metadata!, exactSidecars);
        if (!labels.IsSuccess)
            return Reject(SipJobPlanError.None, labels.Error,
                labels.Detail ?? "Protected SipJob label admission failed.", KernelError.ProjectionDenied);

        SipJobProtectedLabelResult finalLabels = default;
        SipJobVerificationResult finalPlan = default;
        var transport = Execute(gates, fusedLease, possibleSubmit, ordinaryTransport, () =>
        {
            // Final restrictive revalidation is immediately adjacent to the existing
            // live owner commit callback. No authority lock is held across the callback.
            finalPlan = SipJobPlanVerifier.Verify(plan, catalog);
            if (!finalPlan.IsSuccess)
                return KernelResult.Fail(KernelError.InvalidManifest,
                    finalPlan.Failure?.Detail ?? "Protected SipJob plan changed before owner commit.");
            finalLabels = SipJobProtectedLabelFlow.Verify(
                SipJobProtectedLabelFlow.Version, plan, finalPlan.Metadata!, exactSidecars);
            if (!finalLabels.IsSuccess)
                return KernelResult.Fail(KernelError.ProjectionDenied,
                    finalLabels.Detail ?? "Protected SipJob labels changed before owner commit.");
            return liveOwnerBoundary();
        });

        if (!transport.IsSuccess && finalPlan.Failure is { } planFailure)
            return new(transport, [], planFailure.Error, SipJobProtectedLabelError.None,
                planFailure.Detail);
        if (!transport.IsSuccess && finalLabels.Error != SipJobProtectedLabelError.None)
            return new(transport, [], SipJobPlanError.None, finalLabels.Error, finalLabels.Detail);
        return new(transport, labels.Bindings, SipJobPlanError.None,
            SipJobProtectedLabelError.None, transport.Message);
    }

    private static SipJobProtectedTransportResult Reject(
        SipJobPlanError planError,
        SipJobProtectedLabelError labelError,
        string detail,
        KernelError error) =>
        new(new(SipJobResourceTransportMode.Quarantined, error, detail), [],
            planError, labelError, detail);

    private static KernelResult Invoke(Func<KernelResult> action)
    {
        try { return action(); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        { return KernelResult.Fail(KernelError.PlatformFaulted, exception.Message); }
    }
}
