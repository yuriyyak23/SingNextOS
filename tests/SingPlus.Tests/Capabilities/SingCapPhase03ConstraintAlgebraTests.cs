using System.Collections.Concurrent;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Capabilities;

public sealed class SingCapPhase03ConstraintAlgebraTests
{
    private static readonly DomainId Issuer = new(10);
    private static readonly DomainId ParentSubject = new(20);
    private static readonly DomainId ChildSubject = new(30);

    [Fact]
    public void CanonicalSerializationIsDeterministicAndOperationsAreSorted()
    {
        var authority = Authority();
        var parent = Mint(authority, operations: [CapabilityOperation.Write, CapabilityOperation.Read, CapabilityOperation.Write]);
        var constraints = authority.InspectConstraints(parent.CapabilityId)!.Value.Constraints;

        Assert.Equal([CapabilityOperation.Read, CapabilityOperation.Write], constraints.Operations.Operations.ToArray());
        Assert.Equal(constraints.SerializeCanonical(), constraints.SerializeCanonical());
    }

    [Fact]
    public void ExistingDelegationConsumerUsesAllDimensionSubsetGate()
    {
        var authority = Authority();
        var parent = Mint(authority);
        var child = authority.Delegate(parent.CapabilityId, ParentSubject, ChildSubject,
            CapabilityRights.Read, 9);

        Assert.True(child.IsSuccess, child.Message);
        var effective = authority.InspectConstraints(child.Value!.CapabilityId)!.Value.Constraints;
        Assert.Equal(new TargetSubjectConstraint(ChildSubject, 9), effective.TargetSubject);
        Assert.Equal((ushort)3, effective.DelegationDepth.RemainingDepth);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PropertyGeneratedValidChildrenRemainSubsets(int seed)
    {
        var random = new Random(seed);
        for (var index = 0; index < 100; index++)
        {
            var parent = Constraints(range: new(0, 4096, 4, 4), quota: (ulong)random.Next(2, 10_000), depth: 5);
            var offset = (ulong)random.Next(0, 512) * 4;
            var maxElements = (4096UL - offset) / 4;
            var length = (ulong)random.Next(1, (int)maxElements + 1) * 4;
            var child = parent with
            {
                Rights = new(CapabilityRights.Read), Operations = new([CapabilityOperation.Read]),
                Range = new(offset, length, 4, 4), Lifetime = new(10, 900),
                TargetSubject = new(ChildSubject, 9), DelegationDepth = new(4),
                Quota = new(parent.Quota.Account, parent.Quota.PerHandleCeiling - 1)
            };
            Assert.True(EffectiveCapabilityConstraints.IsSubset(child, parent));
        }
    }

    [Fact]
    public void WideningRightsResourceOperationLifetimeSubjectSessionDepthOrQuotaIsDenied()
    {
        var session = new EndpointSessionHandle(new(7), new(2));
        var authority = Authority();
        var parent = Mint(authority, session: session);

        AssertDenied(authority, parent, c => c with { Rights = new(CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Execute) });
        AssertDenied(authority, parent, c => c with { Resource = c.Resource with { ResourceId = "device:other" } });
        AssertDenied(authority, parent, c => c with { Operations = new([CapabilityOperation.Read, CapabilityOperation.Execute]) });
        AssertDenied(authority, parent, c => c with { Lifetime = new(0, long.MaxValue) });
        AssertDenied(authority, parent, c => c with { TargetSubject = new(new DomainId(999), 9) });
        AssertDenied(authority, parent, c => c with { Session = new(null) });
        AssertDenied(authority, parent, c => c with { DelegationDepth = new((ushort)(c.DelegationDepth.RemainingDepth + 1)) });
        AssertDenied(authority, parent, c => c with { Quota = new(c.Quota.Account, c.Quota.PerHandleCeiling + 1) });
    }

    [Fact]
    public void RangeExpansionOverflowAndElementOrAlignmentMismatchFailClosed()
    {
        var parent = Constraints(range: new(64, 1024, 4, 8));
        Assert.False(EffectiveCapabilityConstraints.IsSubset(parent with { Range = new(0, 2048, 4, 8), DelegationDepth = new(3) }, parent));
        Assert.False(EffectiveCapabilityConstraints.IsSubset(parent with { Range = new(64, ulong.MaxValue, 4, 8), DelegationDepth = new(3) }, parent));
        Assert.False(EffectiveCapabilityConstraints.IsSubset(parent with { Range = new(64, 512, 8, 8), DelegationDepth = new(3) }, parent));
        Assert.False(EffectiveCapabilityConstraints.IsSubset(parent with { Range = new(68, 512, 4, 8), DelegationDepth = new(3) }, parent));
    }

    [Fact]
    public void UnknownSchemaAndOperationFailClosed()
    {
        var parent = Constraints();
        Assert.False(EffectiveCapabilityConstraints.IsSubset(parent with { Schema = (CapabilityConstraintSchema)99 }, parent));
        Assert.Throws<ArgumentException>(() => new OperationSetConstraint([(CapabilityOperation)999]));
    }

    [Fact]
    public void SiblingsShareOneAtomicQuotaAndCannotAmplifyAggregate()
    {
        var authority = Authority();
        var parent = Mint(authority, quota: 1000);
        var children = Enumerable.Range(0, 8).Select(index => authority.Delegate(parent.CapabilityId,
            ParentSubject, new DomainId((ulong)(30 + index)), CapabilityRights.Read, 9,
            constraints => constraints with { Quota = new(constraints.Quota.Account, 1000) }).Value!).ToArray();
        var results = new ConcurrentBag<KernelResult>();

        Parallel.ForEach(children, child =>
        {
            for (var i = 0; i < 200; i++)
                results.Add(authority.ConsumeQuota(child.CapabilityId, child.SubjectDomainId, 9, 1));
        });

        Assert.Equal(1000, results.Count(static result => result.IsSuccess));
        Assert.Equal(600, results.Count(static result => result.Error == KernelError.BudgetExceeded));
        Assert.Equal(0UL, authority.InspectConstraints(parent.CapabilityId)!.Value.SharedRemaining);
        Assert.Single(children.Select(child => authority.InspectConstraints(child.CapabilityId)!.Value.Constraints.Quota.Account).Distinct());
    }

    [Fact]
    public void DescriptorProjectionCannotEstablishOrRestoreConstraints()
    {
        var authority = Authority();
        var parent = Mint(authority, quota: 1);
        var forged = parent with { Rights = CapabilityRights.Read | CapabilityRights.Execute, ResourceId = "forged" };
        Assert.True(authority.ConsumeQuota(parent.CapabilityId, ParentSubject, 7, 1).IsSuccess);

        Assert.Equal(KernelError.InsufficientRights,
            authority.Validate(forged.CapabilityId, ParentSubject, 7, CapabilityRights.Execute).Error);
        Assert.Equal(KernelError.BudgetExceeded,
            authority.ConsumeQuota(forged.CapabilityId, ParentSubject, 7, 1).Error);
    }

    private static void AssertDenied(CapabilityAuthority authority, CapabilityDescriptorV1 parent,
        Func<EffectiveCapabilityConstraints, EffectiveCapabilityConstraints> derive) =>
        Assert.Equal(KernelError.DelegationDenied, authority.Delegate(parent.CapabilityId, ParentSubject,
            ChildSubject, CapabilityRights.Read, 9, derive).Error);

    private static CapabilityAuthority Authority() => new(new AuthorityRealmId(Guid.NewGuid()), new(4096, 4096));

    private static CapabilityDescriptorV1 Mint(CapabilityAuthority authority, ulong quota = 100,
        EndpointSessionHandle? session = null, IEnumerable<CapabilityOperation>? operations = null) =>
        authority.Mint(Issuer, ParentSubject, ResourceKind.Device, "device:0",
            CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Delegate, 7, 11,
            new RangeConstraint(0, 4096, 4, 4), session, quota, 4,
            operations: operations, notBeforeUtcTicks: 0, expiresUtcTicks: 1000).Value!;

    private static EffectiveCapabilityConstraints Constraints(RangeConstraint? range = null, ulong quota = 100, ushort depth = 4) =>
        new EffectiveCapabilityConstraints(CapabilityConstraintSchema.V1,
            new(CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Delegate),
            new(ResourceKind.Device, "device:0", 11, null, null),
            new([CapabilityOperation.Read, CapabilityOperation.Write, CapabilityOperation.Delegate]),
            range, new(0, 1000), new(null, 0), new(null), new(depth), new(new(1), quota)).Canonicalize();
}
