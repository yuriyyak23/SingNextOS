namespace SingPlus.Runtime;

internal enum SipJobBarrierDisposition
{
    Fuse = 0,
    MaterializeOrdinarySip,
    RejectFutureGated,
}

internal readonly record struct SipJobBarrierDecision(
    SipJobBarrierDisposition Disposition,
    string LifecycleOwner,
    bool RequiresLiveRevalidation);

internal static class SipJobBarrierPlanner
{
    internal const uint Version = 1;

    internal static SipJobBarrierDecision Classify(uint version, SipJobBarrierClass barrier)
    {
        if (version != Version || !Enum.IsDefined(barrier))
            return Reject("UnknownBoundary");

        return barrier switch
        {
            SipJobBarrierClass.None => new(SipJobBarrierDisposition.Fuse, "GeneratedSentry", true),
            SipJobBarrierClass.ExternalEffect => Materialize("ExternalOperationAuthority"),
            SipJobBarrierClass.Publication => Materialize("ResponseRegistry"),
            SipJobBarrierClass.OwnershipSettlement => Materialize("RegionAuthority"),
            SipJobBarrierClass.AsyncWait => Materialize("EndpointSessionInvocationRegistry"),
            SipJobBarrierClass.CrossRuntime => Materialize("OrdinarySipTransport"),
            SipJobBarrierClass.IndependentCancellation => Materialize("EndpointSessionInvocationRegistry"),
            SipJobBarrierClass.ObservableInvocation => Materialize("EndpointSessionInvocationRegistry"),
            SipJobBarrierClass.NativeIsolated => Reject("NativeIsolatedTransition"),
            SipJobBarrierClass.ConfidentialDomain => Reject("SecureDomainTransition"),
            SipJobBarrierClass.IrreversiblePrivateMutation => Materialize("GeneratedSentry"),
            SipJobBarrierClass.UnsupportedAuthorityCommit => Materialize("CapabilityAuthority"),
            SipJobBarrierClass.ResourceConsumption => Materialize("ResourceBudgetAuthority"),
            _ => Reject("UnknownBoundary"),
        };
    }

    private static SipJobBarrierDecision Materialize(string owner) =>
        new(SipJobBarrierDisposition.MaterializeOrdinarySip, owner, true);

    private static SipJobBarrierDecision Reject(string owner) =>
        new(SipJobBarrierDisposition.RejectFutureGated, owner, true);
}
