using System.Collections.Concurrent;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.SipJobs;

public sealed class Phase144ComposedAdmissionTests
{
    [Fact]
    public void CanonicalSegmentClassifiesOwnerSemanticsWithoutAuthorizationResult()
    {
        SipJobSegmentAdmissionDescriptor descriptor = Segment(
            ProcessProbe(),
            SessionPrepare(),
            OperationCommit(),
            InvocationSettlement());

        SipJobSegmentAdmissionResult result = SipJobSegmentAdmissionVerifier.Verify(descriptor);

        Assert.True(result.IsSuccess);
        Assert.False(SipJobFeatureGates.IsEnabled(SipJobSegmentAdmissionVerifier.MultiSessionGate));
        Assert.DoesNotContain(result.Metadata!.GetType().GetProperties(), property =>
            property.Name.Contains("Authoriz", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Allowed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConsumptiveOperationAuthorityCannotMasqueradeAsReversiblePrepare()
    {
        SipJobAdmissionParticipantDescriptor disguised = OperationCommit() with
        {
            Phase = SipJobAdmissionPhase.ReversiblePrepare,
            PrepareOperationId = "AcquireOperationAuthority",
            CommitOperationId = "None",
            ReleaseBeforeCommitOperationId = "DisposeLease"
        };

        SipJobSegmentAdmissionResult result = SipJobSegmentAdmissionVerifier.Verify(Segment(ProcessProbe(), disguised));

        Assert.Equal(SipJobSegmentAdmissionError.InvalidOwnerProtocol, result.Failure!.Value.Error);
    }

    [Fact]
    public void TwoIndependentConsumptiveCommitsAreRejectedInsteadOfPseudoTransaction()
    {
        SipJobAdmissionParticipantDescriptor second = OperationCommit() with { ParticipantId = "capability-commit-2" };

        SipJobSegmentAdmissionResult result = SipJobSegmentAdmissionVerifier.Verify(
            Segment(ProcessProbe(), SessionPrepare(), OperationCommit(), second));

        Assert.Equal(SipJobSegmentAdmissionError.UnsafeMultipleConsumptiveCommits,
            result.Failure!.Value.Error);
    }

    [Fact]
    public void PhaseReorderingAndUnknownGateFailClosed()
    {
        SipJobSegmentAdmissionResult reordered = SipJobSegmentAdmissionVerifier.Verify(
            Segment(ProcessProbe(), OperationCommit(), SessionPrepare()));
        Assert.Equal(SipJobSegmentAdmissionError.InvalidPhase, reordered.Failure!.Value.Error);

        SipJobSegmentAdmissionDescriptor wrongGate = new SipJobSegmentAdmissionDescriptor(
            1, "segment-1", [ProcessProbe()], [SipJobSegmentAdmissionVerifier.LinearGate]);
        Assert.Equal(SipJobSegmentAdmissionError.UnsupportedGate,
            SipJobSegmentAdmissionVerifier.Verify(wrongGate).Failure!.Value.Error);
    }

    [Fact]
    public void NullParticipantFailsClosedWithoutOwnerProtocolEvaluation()
    {
        SipJobSegmentAdmissionDescriptor descriptor = new SipJobSegmentAdmissionDescriptor(
            1,
            "segment-1",
            [null!],
            [SipJobSegmentAdmissionVerifier.LinearGate, SipJobSegmentAdmissionVerifier.MultiSessionGate]);

        SipJobSegmentAdmissionResult result = SipJobSegmentAdmissionVerifier.Verify(descriptor);

        Assert.Equal(SipJobSegmentAdmissionError.Malformed, result.Failure!.Value.Error);
    }

    [Fact]
    public void CloseAfterSessionPrepareDoesNotConsumeQuotaOrOneShot()
    {
        Scenario scenario = CreateScenario(quota: 1, rights: CapabilityRights.Execute);
        KernelResult close = default;
        scenario.Kernel.EffectAdmissionQualificationHook = new AdmissionHook(
            EffectAdmissionQualificationPoint.AfterSessionPin,
            () => close = scenario.Kernel.CloseSession(scenario.Caller, scenario.Session));

        KernelResult<EffectAdmissionLease> admitted = scenario.Kernel.AdmitSessionCapabilityEffect(
            scenario.Caller,
            scenario.Service,
            scenario.Session,
            scenario.Capability,
            ResourceKind.File,
            "file:p14-segment",
            1,
            CapabilityOperation.Execute,
            quotaAmount: 1,
            oneShot: true);

        Assert.Equal(KernelError.SessionDraining, close.Error);
        Assert.Equal(KernelError.SessionClosed, admitted.Error);
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Caller).Value!;
        Assert.True(scenario.Kernel.CapabilityAuthority.Validate(
            scenario.Capability, process.DomainId, scenario.Caller.Generation,
            CapabilityRights.Execute).IsSuccess);
        (EffectiveCapabilityConstraints Constraints, ulong SharedRemaining, ulong HandleConsumed) quota = scenario.Kernel.CapabilityAuthority.InspectConstraints(scenario.Capability)!.Value;
        Assert.Equal(1UL, quota.SharedRemaining);
        Assert.Equal(0UL, quota.HandleConsumed);
        Assert.Equal(0, scenario.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Kernel.Channels.GetEndpoint(scenario.CallerEndpoint).Error);
    }

    [Fact]
    public void QuotaLossAfterFinalRevalidationReleasesPreparedSessionAndNeverCreatesLease()
    {
        Scenario scenario = CreateScenario(quota: 1, rights: CapabilityRights.Execute);
        scenario.Kernel.EffectAdmissionQualificationHook = new AdmissionHook(
            EffectAdmissionQualificationPoint.AfterFinalSessionRevalidation,
            () =>
            {
                SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Caller).Value!;
                Assert.True(scenario.Kernel.CapabilityAuthority.ConsumeQuota(
                    scenario.Capability, process.DomainId, scenario.Caller.Generation, 1).IsSuccess);
            });

        KernelResult<EffectAdmissionLease> admitted = scenario.Kernel.AdmitSessionCapabilityEffect(
            scenario.Caller, scenario.Service, scenario.Session, scenario.Capability,
            ResourceKind.File, "file:p14-segment", 1, CapabilityOperation.Execute,
            quotaAmount: 1);

        Assert.Equal(KernelError.BudgetExceeded, admitted.Error);
        Assert.Equal(0, scenario.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
    }

    [Fact]
    public void RevokeAfterFinalRevalidationFailsCommitWithoutLeakingPreparedSession()
    {
        Scenario scenario = CreateScenario(quota: 1, rights: CapabilityRights.Execute);
        scenario.Kernel.EffectAdmissionQualificationHook = new AdmissionHook(
            EffectAdmissionQualificationPoint.AfterFinalSessionRevalidation,
            () => Assert.True(scenario.Kernel.CapabilityAuthority.Revoke(scenario.Capability).IsSuccess));

        KernelResult<EffectAdmissionLease> admitted = scenario.Kernel.AdmitSessionCapabilityEffect(
            scenario.Caller, scenario.Service, scenario.Session, scenario.Capability,
            ResourceKind.File, "file:p14-segment", 1, CapabilityOperation.Execute,
            quotaAmount: 1, oneShot: true);

        Assert.Equal(KernelError.CapabilityRevoked, admitted.Error);
        (EffectiveCapabilityConstraints Constraints, ulong SharedRemaining, ulong HandleConsumed) quota = scenario.Kernel.CapabilityAuthority.InspectConstraints(scenario.Capability)!.Value;
        Assert.Equal(1UL, quota.SharedRemaining);
        Assert.Equal(0UL, quota.HandleConsumed);
        Assert.Equal(0, scenario.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
    }

    [Fact]
    public void CloseAfterPreparedRevalidationDeniesCommitAndCleanupIsOwnerDefined()
    {
        Scenario scenario = CreateScenario(quota: 1, rights: CapabilityRights.Execute);
        KernelResult close = default;
        scenario.Kernel.EffectAdmissionQualificationHook = new AdmissionHook(
            EffectAdmissionQualificationPoint.AfterFinalSessionRevalidation,
            () => close = scenario.Kernel.CloseSession(scenario.Caller, scenario.Session));

        KernelResult<EffectAdmissionLease> admitted = scenario.Kernel.AdmitSessionCapabilityEffect(
            scenario.Caller, scenario.Service, scenario.Session, scenario.Capability,
            ResourceKind.File, "file:p14-segment", 1, CapabilityOperation.Execute,
            quotaAmount: 1, oneShot: true);

        Assert.Equal(KernelError.SessionDraining, close.Error);
        Assert.Equal(KernelError.SessionClosed, admitted.Error);
        Assert.Equal(0, scenario.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Kernel.Channels.GetEndpoint(scenario.CallerEndpoint).Error);
        (EffectiveCapabilityConstraints Constraints, ulong SharedRemaining, ulong HandleConsumed) quota = scenario.Kernel.CapabilityAuthority.InspectConstraints(scenario.Capability)!.Value;
        Assert.Equal(1UL, quota.SharedRemaining);
        Assert.Equal(0UL, quota.HandleConsumed);
    }

    [Fact]
    public void ConcurrentOneShotSegmentCommitHasExactlyOneWinner()
    {
        Scenario scenario = CreateScenario(quota: 1, rights: CapabilityRights.Execute);
        ConcurrentBag<KernelResult<EffectAdmissionLease>> results = new ConcurrentBag<KernelResult<EffectAdmissionLease>>();

        Parallel.For(0, 16, _ => results.Add(scenario.Kernel.AdmitSessionCapabilityEffect(
            scenario.Caller, scenario.Service, scenario.Session, scenario.Capability,
            ResourceKind.File, "file:p14-segment", 1, CapabilityOperation.Execute,
            quotaAmount: 1, oneShot: true)));

        Assert.Single(results, static result => result.IsSuccess);
        Assert.All(results.Where(static result => !result.IsSuccess), result =>
            Assert.Contains(result.Error, new[] { KernelError.CapabilityRevoked, KernelError.BudgetExceeded }));
        foreach (KernelResult<EffectAdmissionLease> result in results.Where(static result => result.IsSuccess)) result.Value!.Dispose();
        Assert.Equal(0, scenario.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
    }

    [Fact]
    public void DisposingCommittedLeaseDoesNotRefundQuotaOrOneShot()
    {
        Scenario scenario = CreateScenario(quota: 1, rights: CapabilityRights.Execute);
        EffectAdmissionLease admitted = scenario.Kernel.AdmitSessionCapabilityEffect(
            scenario.Caller, scenario.Service, scenario.Session, scenario.Capability,
            ResourceKind.File, "file:p14-segment", 1, CapabilityOperation.Execute,
            quotaAmount: 1, oneShot: true).Value!;

        admitted.Dispose();

        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Caller).Value!;
        Assert.Equal(KernelError.CapabilityRevoked, scenario.Kernel.CapabilityAuthority.Validate(
            scenario.Capability, process.DomainId, scenario.Caller.Generation,
            CapabilityRights.Execute).Error);
        (EffectiveCapabilityConstraints Constraints, ulong SharedRemaining, ulong HandleConsumed) quota = scenario.Kernel.CapabilityAuthority.InspectConstraints(scenario.Capability)!.Value;
        Assert.Equal(0UL, quota.SharedRemaining);
        Assert.Equal(1UL, quota.HandleConsumed);
    }

    private static SipJobSegmentAdmissionDescriptor Segment(
        params SipJobAdmissionParticipantDescriptor[] participants) =>
        new(1, "segment-1", participants,
            [SipJobSegmentAdmissionVerifier.LinearGate, SipJobSegmentAdmissionVerifier.MultiSessionGate]);

    private static SipJobAdmissionParticipantDescriptor ProcessProbe() =>
        new(1, "process", SipJobAdmissionParticipantKind.ProcessIdentity, SipJobAdmissionPhase.ProbeOnly,
            "ProcessRegistry", "Resolve", "Resolve", "None", "None", "None");

    private static SipJobAdmissionParticipantDescriptor SessionPrepare() =>
        new(1, "session", SipJobAdmissionParticipantKind.SessionPin, SipJobAdmissionPhase.ReversiblePrepare,
            "EndpointSessionRegistry", "AcquirePin", "RevalidatePin", "None", "ReleasePin", "ReleasePin");

    private static SipJobAdmissionParticipantDescriptor OperationCommit() =>
        new(1, "capability-commit", SipJobAdmissionParticipantKind.OperationAuthority, SipJobAdmissionPhase.CommitConsumptive,
            "CapabilityAuthority", "None", "Validate", "AcquireOperationAuthority", "None", "ReleaseLeaseNoRefund");

    private static SipJobAdmissionParticipantDescriptor InvocationSettlement() =>
        new(1, "invocation-settlement", SipJobAdmissionParticipantKind.InvocationSettlement,
            SipJobAdmissionPhase.PostCommitSettlement, "EndpointSessionInvocationRegistry", "None", "Resolve",
            "CompleteInline", "None", "OwnerTerminal");

    private static Scenario CreateScenario(ulong quota, CapabilityRights rights)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle caller = TestFixtures.Create(kernel, 8161, 816_100).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 8162, 816_200).Handle;
        SingProcess callerProcess = kernel.Processes.Resolve(caller).Value!;
        CapabilityId capability = kernel.CapabilityAuthority.Mint(
            new(816_200), callerProcess.DomainId, ResourceKind.File, "file:p14-segment", rights,
            caller.Generation, 1, null, null, quota, 16).Value!.CapabilityId;
        ProtocolDefinitionV1 protocol = ISipJobClosedValueQualificationServiceProtocol.CreateDefinition();
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(
            service, "p14-composed-admission",
            new(protocol.ContractName, "1", protocol.ContractDigest), protocol,
            ISipJobClosedValueQualificationServiceResponseProtocol.Definition).Value!;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor).Value;
        ChannelEndpointHandle endpoint = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        return new(kernel, caller, service, session, endpoint, capability);
    }

    private sealed class AdmissionHook(
        EffectAdmissionQualificationPoint selected,
        Action action) : IEffectAdmissionQualificationHook
    {
        private int _fired;
        public void At(EffectAdmissionQualificationPoint point)
        {
            if (point == selected && Interlocked.Exchange(ref _fired, 1) == 0)
                action();
        }
    }

    private sealed record Scenario(
        RuntimeKernel Kernel,
        ProcessHandle Caller,
        ProcessHandle Service,
        EndpointSessionHandle Session,
        ChannelEndpointHandle CallerEndpoint,
        CapabilityId Capability);
}
