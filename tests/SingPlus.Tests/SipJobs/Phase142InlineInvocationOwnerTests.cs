using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.SipJobs;

public sealed class Phase142InlineInvocationOwnerTests
{
    [Fact]
    public void InlineCopiedInvocationUsesExistingProtocolAndInvocationOwnersWithoutQueueMaterialization()
    {
        Scenario scenario = CreateScenario();
        ChannelEndpoint before = scenario.Kernel.Channels.GetEndpoint(scenario.CallerEndpoint).Value!;

        KernelResult<InlineSipInvocationLease> begun = scenario.Kernel.BeginInlineSessionInvocation(
            scenario.Caller, scenario.Service, scenario.Session, 1, 41);

        Assert.True(begun.IsSuccess, begun.Message);
        ChannelEndpoint after = scenario.Kernel.Channels.GetEndpoint(scenario.CallerEndpoint).Value!;
        Assert.Equal(before.Sequence + 1, after.Sequence);
        Assert.Equal("Used", after.ProtocolState);
        Assert.Equal(0, scenario.Kernel.Channels.InspectionSummary(scenario.Caller).QueuedMessages);
        InlineSipInvocationLease lease = begun.Value!;
        Assert.True(scenario.Kernel.RevalidateInlineSessionInvocation(lease).IsSuccess);
        Assert.True(scenario.Kernel.SettleInlineSessionInvocation(
            scenario.Service, lease, succeeded: true).IsSuccess);
        Assert.Equal(
            0,
            scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.Equal(
            KernelError.ResponseNotPending,
            scenario.Kernel.SettleInlineSessionInvocation(
                scenario.Service, lease, succeeded: true).Error);
    }

    [Fact]
    public void InlineCopiedInvocationRejectsMalformedStaleAndIllegalProtocolBeforeCreatingInvocation()
    {
        Scenario malformed = CreateScenario();
        Assert.Equal(
            KernelError.UnsupportedPayload,
            malformed.Kernel.BeginInlineSessionInvocation(
                malformed.Caller, malformed.Service, malformed.Session, 1, "not-an-int").Error);
        Assert.Equal(0UL, malformed.Kernel.Channels.GetEndpoint(malformed.CallerEndpoint).Value!.Sequence);

        Scenario stale = CreateScenario();
        ProcessHandle staleCaller = stale.Caller with { Generation = stale.Caller.Generation + 1 };
        Assert.Equal(
            KernelError.StaleHandle,
            stale.Kernel.BeginInlineSessionInvocation(
                staleCaller, stale.Service, stale.Session, 1, 1).Error);

        Scenario protocol = CreateScenario();
        KernelResult<InlineSipInvocationLease> first = protocol.Kernel.BeginInlineSessionInvocation(
            protocol.Caller, protocol.Service, protocol.Session, 1, 1);
        Assert.True(first.IsSuccess, first.Message);
        Assert.True(protocol.Kernel.SettleInlineSessionInvocation(
            protocol.Service, first.Value!, succeeded: false).IsSuccess);
        Assert.Equal(
            KernelError.InvalidProtocolTransition,
            protocol.Kernel.BeginInlineSessionInvocation(
                protocol.Caller, protocol.Service, protocol.Session, 1, 2).Error);
    }

    [Fact]
    public void InlineCopiedInvocationRejectsMessageAttachedAuthorityAndClosedSession()
    {
        Scenario authority = CreateScenario(requiresCapability: true);
        Assert.Equal(
            KernelError.MissingCapability,
            authority.Kernel.BeginInlineSessionInvocation(
                authority.Caller, authority.Service, authority.Session, 1, 1).Error);

        Scenario closed = CreateScenario();
        Assert.True(closed.Kernel.CloseSession(closed.Caller, closed.Session).IsSuccess);
        Assert.Equal(
            KernelError.SessionClosed,
            closed.Kernel.BeginInlineSessionInvocation(
                closed.Caller, closed.Service, closed.Session, 1, 1).Error);
    }

    private static Scenario CreateScenario(bool requiresCapability = false)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle caller = TestFixtures.Create(kernel, 62_001, 620_010).Handle;
        ProcessHandle service = TestFixtures.Create(kernel, 62_002, 620_020).Handle;
        CapabilityRequirementV1[] requirements = requiresCapability
            ? new[] { new CapabilityRequirementV1(ResourceKind.Process, "inline-authority", CapabilityRights.Read) }
            : [];
        ProtocolDefinitionV1 protocol = new ProtocolDefinitionV1(
            "P14InlineOwner",
            requiresCapability ? "p14-inline-owner-capability" : "p14-inline-owner",
            "Ready",
            terminalStates: ["Used"],
            messages:
            [
                new ProtocolMessageDescriptorV1(
                    1,
                    "Transform",
                    requiredCapabilities: requirements,
                    requestPayload: new RequestPayloadDescriptorV1(
                        RequestPayloadKind.Primitive,
                        "value",
                        typeof(int).FullName!))
            ],
            transitions: [new ProtocolTransitionV1(1, "Ready", "Used")]);
        ServiceEndpointDescriptor descriptor = kernel.RegisterService(
            service,
            "p14-inline-owner",
            new(protocol.ContractName, "1", protocol.ContractDigest),
            protocol).Value!;
        EndpointSessionHandle session = kernel.OpenSession(caller, descriptor).Value;
        ChannelEndpointHandle callerEndpoint = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        return new(kernel, caller, service, session, callerEndpoint);
    }

    private sealed record Scenario(
        RuntimeKernel Kernel,
        ProcessHandle Caller,
        ProcessHandle Service,
        EndpointSessionHandle Session,
        ChannelEndpointHandle CallerEndpoint);
}
