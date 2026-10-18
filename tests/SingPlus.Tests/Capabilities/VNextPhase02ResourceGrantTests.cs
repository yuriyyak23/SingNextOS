using System.Text.Json;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Capabilities;

public sealed class VNextPhase02ResourceGrantTests
{
    [Fact]
    public void ResourceGrantIsLiveLedgerBasedAndIndependentFromEffectRights()
    {
        var authority = new CapabilityAuthority();
        var grant = Mint(authority, new(10), 7, Constraint(100));

        Assert.True(authority.ValidateResourceUse(grant.CapabilityId, new(10), 7, 3, Envelope(50)).IsSuccess);
        Assert.Equal(KernelError.InsufficientRights,
            authority.Validate(grant.CapabilityId, new(10), 7, CapabilityRights.Execute, 3).Error);

        var effectOnly = authority.Mint(new(1), new(10), ResourceKind.Compute, "compute:effect",
            CapabilityRights.Execute, 7, 3).Value!;
        Assert.Equal(KernelError.InsufficientRights,
            authority.ValidateResourceUse(effectOnly.CapabilityId, new(10), 7, 3, Envelope(1)).Error);
    }

    [Fact]
    public void DerivationCanOnlyNarrowResourceEnvelopeAssuranceScopeAndDepth()
    {
        var authority = new CapabilityAuthority();
        var parent = Mint(authority, new(10), 7, Constraint(100));
        var child = authority.Delegate(parent.CapabilityId, new(10), new(11), CapabilityRights.Delegate, 8,
            constraints => constraints with { ResourceUse = Constraint(50, "host:compute-v1", ResourceAssuranceV1.AccountingOnly, 2) });

        Assert.True(child.IsSuccess, child.Message);
        Assert.True(authority.ValidateResourceUse(child.Value!.CapabilityId, new(11), 8, 3, Envelope(50)).IsSuccess);
        Assert.Equal(KernelError.InsufficientRights,
            authority.ValidateResourceUse(child.Value.CapabilityId, new(11), 8, 3, Envelope(51)).Error);

        Assert.Equal(KernelError.DelegationDenied,
            authority.Delegate(parent.CapabilityId, new(10), new(11), CapabilityRights.Delegate, 8,
                constraints => constraints with { ResourceUse = Constraint(101) }).Error);
        Assert.Equal(KernelError.DelegationDenied,
            authority.Delegate(parent.CapabilityId, new(10), new(11), CapabilityRights.Delegate, 8,
                constraints => constraints with { ResourceUse = Constraint(50, "gpu:compute-v1") }).Error);
        Assert.Equal(KernelError.DelegationDenied,
            authority.Delegate(parent.CapabilityId, new(10), new(11), CapabilityRights.Delegate, 8,
                constraints => constraints with { ResourceUse = Constraint(50, assurance: ResourceAssuranceV1.GuaranteedReservation) }).Error);
    }

    [Fact]
    public void StaleSubjectResourceAndRevokedGrantFailClosed()
    {
        var authority = new CapabilityAuthority();
        var grant = Mint(authority, new(10), 7, Constraint(100));

        Assert.Equal(KernelError.StaleGeneration,
            authority.ValidateResourceUse(grant.CapabilityId, new(10), 8, 3, Envelope(1)).Error);
        Assert.Equal(KernelError.StaleGeneration,
            authority.ValidateResourceUse(grant.CapabilityId, new(10), 7, 4, Envelope(1)).Error);
        Assert.True(authority.Revoke(grant.CapabilityId).IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked,
            authority.ValidateResourceUse(grant.CapabilityId, new(10), 7, 3, Envelope(1)).Error);
    }

    [Fact]
    public void ResourceGrantValidityIsCheckedByLiveAuthority()
    {
        var authority = new CapabilityAuthority();
        var future = Constraint(100) with { NotBeforeUtcTicks = long.MaxValue - 1, ExpiresUtcTicks = long.MaxValue };
        var grant = Mint(authority, new(10), 7, future);

        Assert.Equal(KernelError.DeadlineExpired,
            authority.ValidateResourceUse(grant.CapabilityId, new(10), 7, 3, Envelope(1)).Error);
    }

    [Fact]
    public async Task DeriveVersusRevokeHasNoLiveChildAfterParentRevocation()
    {
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var authority = new CapabilityAuthority();
            var parent = Mint(authority, new(10), 7, Constraint(100));
            using var start = new ManualResetEventSlim(false);
            var derive = Task.Run(() =>
            {
                start.Wait();
                return authority.Delegate(parent.CapabilityId, new(10), new(11), CapabilityRights.Delegate, 8,
                    constraints => constraints with { ResourceUse = Constraint(50, assurance: ResourceAssuranceV1.AccountingOnly, depth: 2) });
            });
            var revoke = Task.Run(() => { start.Wait(); return authority.Revoke(parent.CapabilityId); });
            start.Set();
            var child = await derive;
            Assert.True((await revoke).IsSuccess);
            if (child.IsSuccess)
                Assert.Equal(KernelError.CapabilityRevoked,
                    authority.ValidateResourceUse(child.Value!.CapabilityId, new(11), 8, 3, Envelope(1)).Error);
            else
                Assert.Equal(KernelError.CapabilityRevoked, child.Error);
        }
    }

    [Fact]
    public void SerializedDescriptorCannotValidateWithoutExactLedgerRecord()
    {
        var authority = new CapabilityAuthority();
        var descriptor = Constraint(100);
        var copy = JsonSerializer.Deserialize<ResourceUseConstraintV1>(JsonSerializer.Serialize(descriptor));

        Assert.Equal(descriptor, copy);
        Assert.Equal(KernelError.CapabilityNotFound,
            authority.ValidateResourceUse(new(999), new(10), 7, 3, copy.Envelope).Error);
    }

    private static CapabilityDescriptorV1 Mint(
        CapabilityAuthority authority, DomainId subject, ulong generation, ResourceUseConstraintV1 constraint) =>
        authority.Mint(new(1), subject, ResourceKind.Compute, "resource-use:compute-time",
            CapabilityRights.Delegate, generation, 3, range: null, session: null, quota: 1000, delegationDepth: 4,
            resourceUse: constraint).Value!;

    private static ResourceUseConstraintV1 Constraint(
        ulong amount,
        string scope = "host:compute-v1",
        ResourceAssuranceV1 assurance = ResourceAssuranceV1.RuntimeEnforced,
        ushort depth = 3) =>
        new(1, Envelope(amount, scope), 0, long.MaxValue, assurance, depth);

    private static ResourceEnvelopeV1 Envelope(ulong amount, string scope = "host:compute-v1") =>
        new(1, ResourceDimensionFamilyV1.Time, ResourceClassV1.ComputeTime,
            ResourceUnitV1.Nanoseconds, amount, 0, scope);
}
