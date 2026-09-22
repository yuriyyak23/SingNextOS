using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip.Sdk;

namespace SingPlus.Tests.Runtime;

[SipContract]
internal interface IVNextP06DonatedResourceService
{
    [Message(1)]
    [RequiresResource(1, ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds, 10,
        "host:compute-v1", ResourceAssuranceV1.RuntimeEnforced,
        SipResourceDonationPolicyV1.AcceptNarrowed)]
    int Run();
}

public sealed class VNextPhase06ResourceDonationTests
{
    [Fact]
    public void GeneratedSentryConsumesExactInvocationDonationWithoutSecondCharge()
    {
        var setup = Create();
        var invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        var donation = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(10), Envelope(10),
            AdmissionQosHint.None).Value!;
        var effect = setup.Kernel.CapabilityAuthority.Mint(setup.ServiceDomain, setup.ServiceDomain,
            ResourceKind.Compute, "compute:effect", CapabilityRights.Execute, setup.Service.Generation,
            1, range: null, session: null, quota: 1, delegationDepth: 0).Value!.CapabilityId;
        var region = setup.Kernel.AllocateBuffer<byte>(setup.Service, 8).Value!;
        var operation = setup.Kernel.PrepareExternalOperation(setup.Service,
            [new(region.Handle, RegionUseMode.ReadOnly, new(0, 8))],
            ExternalVisibilityRequirement.None, ExternalPublicationPolicy.Staged).Value!.Operation;
        var binding = new SipResourceAdmissionBinding(effect, ResourceKind.Compute, "compute:effect", 1,
            default, 1, "host:compute-v1", operation, new(1, 1));
        var context = new TrustedSipInvocationContext(setup.Caller, setup.Service, setup.Session,
            invocation.Context.Invocation, requirement => setup.Kernel.EnterDonatedSipResourceAdmission(
                setup.Service, invocation.Context.Invocation, binding, requirement));
        var target = new DonatedTarget();

        var result = IVNextP06DonatedResourceServiceGeneratedOperationSentries.InvokeRuntime_Run(
            target, in context);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, target.ProviderCalls);
        Assert.Equal(10UL, Used(setup));
        Assert.Equal(BudgetReservationState.Consuming,
            setup.Kernel.QueryBudget(donation.Lease).Value!.State);
        Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Service, invocation, true).IsSuccess);
        Assert.Equal(10UL, Used(setup));
        Assert.Equal(BudgetReservationState.Quarantined,
            setup.Kernel.QueryBudget(donation.Lease).Value!.State);
    }

    [Fact]
    public void DonationIsInvocationScopedNarrowedAndChargedToCaller()
    {
        var setup = Create();
        var invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);

        var donation = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(80), Envelope(80),
            AdmissionQosHint.BoundedInteractive);

        Assert.True(donation.IsSuccess, donation.Message);
        Assert.Equal(setup.Caller, donation.Value!.ChargingOwner);
        Assert.Equal(80UL, Used(setup));
        Assert.True(setup.Kernel.CapabilityAuthority.ValidateResourceUse(donation.Value.DerivedGrant,
            setup.ServiceDomain, setup.Service.Generation, 1, Envelope(80)).IsSuccess);
        Assert.Equal(KernelError.InvalidTransition,
            setup.Kernel.BindResourceDonation(setup.Caller, setup.Service, invocation.Context.Invocation,
                setup.SourceGrant, 1, Requirement(10), Envelope(10), AdmissionQosHint.None).Error);
        Assert.Equal(80UL, Used(setup));

        Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Service, invocation, succeeded: false).IsSuccess);
        Assert.Equal(0UL, Used(setup));
        Assert.Equal(KernelError.CapabilityRevoked,
            setup.Kernel.CapabilityAuthority.ValidateResourceUse(donation.Value.DerivedGrant,
                setup.ServiceDomain, setup.Service.Generation, 1, Envelope(1)).Error);
    }

    [Fact]
    public void NestedDonationPreservesProvenanceAndConservesOneLeaseLineage()
    {
        var setup = Create(includeDownstream: true);
        var parentInvocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        var parent = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            parentInvocation.Context.Invocation, setup.SourceGrant, 1, Requirement(100), Envelope(100),
            AdmissionQosHint.LatencySensitive).Value!;
        var childInvocation = Begin(setup.Kernel, setup.Service, setup.Downstream, setup.DownstreamSession);

        var child = setup.Kernel.DeriveNestedResourceDonation(setup.Service, setup.Downstream,
            parentInvocation.Context.Invocation, childInvocation.Context.Invocation, Envelope(30),
            ResourceAssuranceV1.AccountingOnly, AdmissionQosHint.ThroughputOriented);

        Assert.True(child.IsSuccess, child.Message);
        Assert.Equal(setup.Caller, child.Value!.ChargingOwner);
        Assert.Equal(
            $"{parent.Provenance}/{setup.Service.ProcessId.Value}:{setup.Service.Generation}->{setup.Downstream.ProcessId.Value}:{setup.Downstream.Generation}@{childInvocation.Context.Invocation.Session.SessionId.Value}:{childInvocation.Context.Invocation.Session.Generation.Value}:{childInvocation.Context.Invocation.InvocationId.Value}:{childInvocation.Context.Invocation.Generation.Value}",
            child.Value.Provenance);
        Assert.Equal(100UL, Used(setup));
        Assert.Equal(70UL, Assert.Single(setup.Kernel.QueryBudget(parent.Lease).Value!.Amounts).Amount);
        Assert.Equal(30UL, Assert.Single(setup.Kernel.QueryBudget(child.Value.Lease).Value!.Amounts).Amount);
        Assert.True(setup.Kernel.CapabilityAuthority.ValidateResourceUse(child.Value.DerivedGrant,
            setup.DownstreamDomain, setup.Downstream.Generation, 1, Envelope(30)).IsSuccess);
        Assert.Equal(KernelError.DelegationDenied,
            setup.Kernel.DeriveNestedResourceDonation(setup.Service, setup.Downstream,
                parentInvocation.Context.Invocation, childInvocation.Context.Invocation, Envelope(31),
                ResourceAssuranceV1.GuaranteedReservation, AdmissionQosHint.LatencySensitive).Error);

        Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Downstream, childInvocation, false).IsSuccess);
        Assert.Equal(70UL, Used(setup));
        Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Service, parentInvocation, false).IsSuccess);
        Assert.Equal(0UL, Used(setup));
    }

    [Fact]
    public async Task ParallelNestedSplitsNeverAmplifyCallerCapacity()
    {
        var setup = Create(includeDownstream: true);
        var parentInvocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        var parent = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            parentInvocation.Context.Invocation, setup.SourceGrant, 1, Requirement(100), Envelope(100),
            AdmissionQosHint.LatencySensitive).Value!;
        var invocations = Enumerable.Range(0, 12)
            .Select(_ => Begin(setup.Kernel, setup.Service, setup.Downstream, setup.DownstreamSession)).ToArray();
        using var start = new ManualResetEventSlim(false);
        var attempts = invocations.Select(invocation => Task.Run(() =>
        {
            start.Wait();
            return setup.Kernel.DeriveNestedResourceDonation(setup.Service, setup.Downstream,
                parentInvocation.Context.Invocation, invocation.Context.Invocation, Envelope(10),
                ResourceAssuranceV1.AccountingOnly, AdmissionQosHint.Background);
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(9, results.Count(result => result.IsSuccess));
        Assert.All(results.Where(result => !result.IsSuccess),
            result => Assert.Equal(KernelError.BudgetExceeded, result.Error));
        Assert.Equal(100UL, Used(setup));
        Assert.Equal(10UL, Assert.Single(setup.Kernel.QueryBudget(parent.Lease).Value!.Amounts).Amount);

        foreach (var pair in invocations.Zip(results))
            Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Downstream, pair.First, false).IsSuccess);
        Assert.Equal(10UL, Used(setup));
        Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Service, parentInvocation, false).IsSuccess);
        Assert.Equal(0UL, Used(setup));
    }

    [Fact]
    public void PossibleSubmitAndSessionLossQuarantineInsteadOfRefund()
    {
        var setup = Create();
        var invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        var donation = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(40), Envelope(40),
            AdmissionQosHint.None).Value!;

        Assert.True(setup.Kernel.MarkResourceDonationPossibleSubmit(setup.Service,
            invocation.Context.Invocation).IsSuccess);
        Assert.Equal(KernelError.SessionDraining,
            setup.Kernel.CloseSession(setup.Caller, setup.Session).Error);
        Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Service, invocation, false).IsSuccess);

        Assert.Equal(40UL, Used(setup));
        Assert.Equal(BudgetReservationState.Quarantined,
            setup.Kernel.QueryBudget(donation.Lease).Value!.State);
        Assert.Equal(KernelError.CapabilityRevoked,
            setup.Kernel.CapabilityAuthority.ValidateResourceUse(donation.DerivedGrant,
                setup.ServiceDomain, setup.Service.Generation, 1, Envelope(1)).Error);
    }

    [Fact]
    public void StaleIdentityWideningAndAmbientReuseFailClosedBeforeCharging()
    {
        var setup = Create();
        var invocation = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        Assert.Equal(KernelError.StaleGeneration,
            setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
                invocation.Context.Invocation with { Generation = new(2) }, setup.SourceGrant, 1,
                Requirement(10), Envelope(10), AdmissionQosHint.None).Error);
        Assert.Equal(KernelError.InsufficientRights,
            setup.Kernel.BindResourceDonation(setup.Caller, setup.Service, invocation.Context.Invocation,
                setup.SourceGrant, 1, Requirement(101), Envelope(101), AdmissionQosHint.None).Error);
        Assert.Equal(0UL, Used(setup));

        var donated = setup.Kernel.BindResourceDonation(setup.Caller, setup.Service,
            invocation.Context.Invocation, setup.SourceGrant, 1, Requirement(10), Envelope(10),
            AdmissionQosHint.None).Value!;
        var second = Begin(setup.Kernel, setup.Caller, setup.Service, setup.Session);
        Assert.Equal(KernelError.InvalidTransition,
            setup.Kernel.CloseResourceDonation(setup.Service, second.Context.Invocation,
                submitMayHaveOccurred: false).Error);
        Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Service, second, false).IsSuccess);
        Assert.True(setup.Kernel.SettleInlineSessionInvocation(setup.Service, invocation, false).IsSuccess);
    }

    private static Setup Create(bool includeDownstream = false)
    {
        var kernel = new RuntimeKernel();
        var admin = TestFixtures.Create(kernel, 960, 1960).Handle;
        var caller = TestFixtures.Create(kernel, 961, 1961).Handle;
        var service = TestFixtures.Create(kernel, 962, 1962).Handle;
        var downstream = TestFixtures.Create(kernel, 963, 1963).Handle;
        var administration = kernel.MintCapability(new(1960), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        var budget = kernel.AdmitProcessBudget(admin, administration, caller, "p06",
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).Value!.ProcessBudget;
        Assert.True(kernel.AdmitProcessBudget(admin, administration, service, "p06-service",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).IsSuccess);
        var source = kernel.CapabilityAuthority.Mint(new(1961), new(1961), ResourceKind.Compute,
            "resource-use:compute-time", CapabilityRights.Delegate, caller.Generation, 1,
            range: null, session: null, quota: 100, delegationDepth: 4,
            resourceUse: new(1, Envelope(100), 0, long.MaxValue,
                ResourceAssuranceV1.RuntimeEnforced, 3)).Value!.CapabilityId;
        var session = Open(kernel, caller, service, "p06-main");
        var downstreamSession = includeDownstream
            ? Open(kernel, service, downstream, "p06-downstream")
            : default;
        return new(kernel, caller, service, downstream, new(1962), new(1963), session,
            downstreamSession, source, budget);
    }

    private static EndpointSessionHandle Open(RuntimeKernel kernel, ProcessHandle caller,
        ProcessHandle service, string name)
    {
        var protocol = new ProtocolDefinitionV1(name, name + "-digest", "Ready", terminalStates: null,
            messages: [new ProtocolMessageDescriptorV1(1, "Invoke",
                requestPayload: new RequestPayloadDescriptorV1(RequestPayloadKind.Primitive,
                    "value", typeof(int).FullName!))],
            transitions: [new ProtocolTransitionV1(1, "Ready", "Ready")]);
        var descriptor = kernel.RegisterService(service, name,
            new(protocol.ContractName, "1", protocol.ContractDigest), protocol).Value!;
        return kernel.OpenSession(caller, descriptor).Value;
    }

    private static InlineSipInvocationLease Begin(RuntimeKernel kernel, ProcessHandle caller,
        ProcessHandle service, EndpointSessionHandle session) =>
        kernel.BeginInlineSessionInvocation(caller, service, session, 1, 1).Value!;

    private static SipResourceRequirementV1 Requirement(ulong amount) => new(1,
        ResourceClassV1.ComputeTime, ResourceUnitV1.Nanoseconds, amount, "host:compute-v1",
        ResourceAssuranceV1.RuntimeEnforced, SipResourceDonationPolicyV1.AcceptNarrowed);

    private static ResourceEnvelopeV1 Envelope(ulong amount) => new(1,
        ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
        ResourceUnitV1.Nanoseconds, amount, 0, "host:compute-v1");

    private static ulong Used(Setup setup) => Assert.Single(
        setup.Kernel.QueryBudget(setup.Budget).Value!.Usage,
        usage => usage.Dimension == ServiceBudgetDimension.ComputeTimeNanoseconds).Used;

    private sealed record Setup(RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service,
        ProcessHandle Downstream, DomainId ServiceDomain, DomainId DownstreamDomain,
        EndpointSessionHandle Session, EndpointSessionHandle DownstreamSession,
        CapabilityId SourceGrant, BudgetAccountHandle Budget);

    private sealed class DonatedTarget : IIVNextP06DonatedResourceServiceGeneratedSentryTarget_Run
    {
        internal int ProviderCalls { get; private set; }

        public GeneratedSipSentryResult<int> Sentry_Run(in TrustedSipInvocationContext context,
            GeneratedSipResourceAdmission resourceAdmission)
        {
            var submitted = resourceAdmission.Submit(() =>
            {
                ProviderCalls++;
                return GeneratedSipSubmitResult.Ok();
            });
            return submitted.IsSuccess
                ? GeneratedSipSentryResult<int>.Success(1)
                : GeneratedSipSentryResult<int>.Failure(submitted.ErrorCode, submitted.Message!);
        }
    }
}
