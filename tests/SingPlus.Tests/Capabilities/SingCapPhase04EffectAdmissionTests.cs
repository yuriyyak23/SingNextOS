using System.Collections.Concurrent;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Capabilities;

public sealed class SingCapPhase04EffectAdmissionTests
{
    [Fact]
    public void RevokedAncestorInvalidatesDeepDescendantAndSnapshots()
    {
        var authority = Authority();
        var root = Mint(authority, new(10), CapabilityRights.Read | CapabilityRights.Delegate, depth: 4);
        var child = authority.Delegate(root.CapabilityId, new(10), new(20),
            CapabilityRights.Read | CapabilityRights.Delegate, 1).Value!;
        var grandchild = authority.Delegate(child.CapabilityId, new(20), new(30), CapabilityRights.Read, 1).Value!;

        Assert.True(authority.Revoke(root.CapabilityId).IsSuccess);

        Assert.Equal(KernelError.CapabilityRevoked,
            authority.Validate(grandchild.CapabilityId, new(30), 1, CapabilityRights.Read).Error);
        Assert.DoesNotContain(authority.SnapshotForDomain(new(30)), item => item.CapabilityId == grandchild.CapabilityId);
    }

    [Fact]
    public void ValidateThenRevokeCannotBeUsedToAcquireLaterEffectLease()
    {
        var authority = Authority();
        var capability = Mint(authority, new(10), CapabilityRights.Read);
        Assert.True(authority.Validate(capability.CapabilityId, new(10), 1, CapabilityRights.Read).IsSuccess);
        authority.Revoke(capability.CapabilityId);

        var lease = authority.AcquireOperationAuthority(capability.CapabilityId, new(10), 1,
            ResourceKind.File, "file:namespace", 1, CapabilityOperation.Read);

        Assert.Equal(KernelError.CapabilityRevoked, lease.Error);
    }

    [Fact]
    public void LeaseAdmittedBeforeRevokeFollowsGrandfatherPolicyButNewAdmissionStops()
    {
        var authority = Authority();
        var capability = Mint(authority, new(10), CapabilityRights.Read);
        using var lease = authority.AcquireOperationAuthority(capability.CapabilityId, new(10), 1,
            ResourceKind.File, "file:namespace", 1, CapabilityOperation.Read).Value!;

        authority.Revoke(capability.CapabilityId);

        Assert.Equal(EffectRevocationPolicy.GrandfatherAdmitted, lease.RevocationPolicy);
        Assert.Equal(1, authority.ActiveOperationLeaseCount);
        Assert.Equal(KernelError.CapabilityRevoked, authority.AcquireOperationAuthority(
            capability.CapabilityId, new(10), 1, ResourceKind.File, "file:namespace", 1,
            CapabilityOperation.Read).Error);
    }

    [Fact]
    public void OneShotAdmissionHasExactlyOneWinner()
    {
        var authority = Authority();
        var capability = Mint(authority, new(10), CapabilityRights.Execute);
        var results = new ConcurrentBag<KernelResult<OperationAuthorityLease>>();

        Parallel.For(0, 32, _ => results.Add(authority.AcquireOperationAuthority(
            capability.CapabilityId, new(10), 1, ResourceKind.File, "file:namespace", 1,
            CapabilityOperation.Execute, oneShot: true)));

        Assert.Single(results, static result => result.IsSuccess);
        Assert.Equal(31, results.Count(static result => result.Error == KernelError.CapabilityRevoked));
        foreach (var result in results.Where(static result => result.IsSuccess)) result.Value!.Dispose();
    }

    [Fact]
    public void ConcurrentDeriveAndRevokeNeverLeavesValidDescendant()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var authority = Authority();
            var root = Mint(authority, new(10), CapabilityRights.Read | CapabilityRights.Delegate);
            KernelResult<CapabilityDescriptorV1> derived = default;
            Parallel.Invoke(
                () => derived = authority.Delegate(root.CapabilityId, new(10), new(20), CapabilityRights.Read, 1),
                () => authority.Revoke(root.CapabilityId));

            if (derived.IsSuccess)
                Assert.Equal(KernelError.CapabilityRevoked,
                    authority.Validate(derived.Value!.CapabilityId, new(20), 1, CapabilityRights.Read).Error);
            else
                Assert.Contains(derived.Error, new[] { KernelError.CapabilityRevoked, KernelError.InsufficientRights });
        }
    }

    [Fact]
    public void SessionCloseBetweenPrepareAndCommitDeniesAndCompensatesInReverse()
    {
        var scenario = SessionScenario();
        KernelResult<EndpointSessionRegistry.Record> close = default;

        var admitted = scenario.Kernel.AdmitSessionCapabilityEffect(scenario.Caller, scenario.Service,
            scenario.Session, scenario.Capability, ResourceKind.File, "file:namespace", 1,
            CapabilityOperation.Read, afterSessionPin: () =>
                close = scenario.Kernel.EndpointSessions.Close(scenario.Session, scenario.Caller));

        Assert.Equal(KernelError.SessionDraining, close.Error);
        Assert.Equal(KernelError.SessionClosed, admitted.Error);
        Assert.Equal(0, scenario.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.Equal(EndpointSessionState.Closed,
            scenario.Kernel.EndpointSessions.SnapshotForProcess(scenario.Caller).Single().State);
    }

    [Fact]
    public void CapabilityPreparationFailureReleasesEarlierSessionPin()
    {
        var scenario = SessionScenario();

        var admitted = scenario.Kernel.AdmitSessionCapabilityEffect(scenario.Caller, scenario.Service,
            scenario.Session, scenario.Capability, ResourceKind.File, "file:other", 1,
            CapabilityOperation.Read);

        Assert.Equal(KernelError.WrongCapabilityResource, admitted.Error);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.Equal(0, scenario.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
    }

    [Fact]
    public void AdmittedPilotRunsEffectOutsideAuthorityRegistryLocks()
    {
        var scenario = SessionScenario();
        using var admitted = scenario.Kernel.AdmitSessionCapabilityEffect(scenario.Caller, scenario.Service,
            scenario.Session, scenario.Capability, ResourceKind.File, "file:namespace", 1,
            CapabilityOperation.Read).Value!;

        // Re-entry into both registries completes while the admitted effect lease is live.
        Assert.True(scenario.Kernel.RevokeCapability(scenario.Capability).IsSuccess);
        Assert.Equal(KernelError.SessionDraining,
            scenario.Kernel.EndpointSessions.Close(scenario.Session, scenario.Caller).Error);
        Assert.Equal(EffectRevocationPolicy.GrandfatherAdmitted, admitted.Capability.RevocationPolicy);
    }

    [Fact]
    public void ExhaustedAttemptIdentityCannotConsumeOneShotOrQuotaAuthority()
    {
        var scenario = SessionScenario(CapabilityRights.Execute, quota: 1);
        typeof(RuntimeKernel).GetField("_nextEffectAdmissionAttemptId",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(scenario.Kernel, 0UL);

        var denied = scenario.Kernel.AdmitSessionCapabilityEffect(scenario.Caller, scenario.Service,
            scenario.Session, scenario.Capability, ResourceKind.File, "file:namespace", 1,
            CapabilityOperation.Execute, quotaAmount: 1, oneShot: true);
        var process = scenario.Kernel.Processes.Resolve(scenario.Caller).Value!;

        Assert.Equal(KernelError.CapacityExhausted, denied.Error);
        Assert.True(scenario.Kernel.CapabilityAuthority.Validate(scenario.Capability,
            process.DomainId, scenario.Caller.Generation, CapabilityRights.Execute).IsSuccess);
        Assert.Equal(1UL, scenario.Kernel.CapabilityAuthority
            .InspectConstraints(scenario.Capability)!.Value.SharedRemaining);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.Equal(0, scenario.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
    }

    private static (RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service,
        EndpointSessionHandle Session, CapabilityId Capability) SessionScenario(
        CapabilityRights rights = CapabilityRights.Read, ulong quota = ulong.MaxValue)
    {
        var kernel = new RuntimeKernel();
        var (_, caller) = TestFixtures.Create(kernel, 1, 10);
        var (_, service) = TestFixtures.Create(kernel, 2, 20);
        var capability = kernel.CapabilityAuthority.Mint(new(20), new(10), ResourceKind.File,
            "file:namespace", rights, caller.Generation, resourceGeneration: 1,
            range: null, session: null, quota: quota, delegationDepth: 16).Value!.CapabilityId;
        var session = kernel.EndpointSessions.Add(caller, service, [capability], [],
            new(new ChannelId(1), new EndpointId(1), 1), null).Value!.Handle;
        return (kernel, caller, service, session, capability);
    }

    private static CapabilityAuthority Authority() => new(new AuthorityRealmId(Guid.NewGuid()), new(4096, 4096));

    private static CapabilityDescriptorV1 Mint(CapabilityAuthority authority, DomainId subject,
        CapabilityRights rights, ushort depth = 16) => authority.Mint(new(1), subject,
        ResourceKind.File, "file:namespace", rights, 1, 1, null, null, ulong.MaxValue, depth).Value!;
}
