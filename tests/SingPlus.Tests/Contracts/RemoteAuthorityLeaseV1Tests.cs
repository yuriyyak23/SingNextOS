using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class RemoteAuthorityLeaseV1Tests
{
    private const string Resource = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly RemoteAuthorityLeaseV1 Lease = new(1,
        Guid.Parse("d62957c3-7c8d-4e4b-9c98-ef225133f731"),
        "host:owner", 3, "host:remote", 5, Resource,
        RemoteLeaseRightsV1.Read | RemoteLeaseRightsV1.Execute,
        7, 11, 13, 17);

    [Fact]
    public void ExactLogicalEpochLeaseIsEligibleButNeverParentOrTransferAuthority()
    {
        var code = RemoteAuthorityLeaseEvaluatorV1.Evaluate(Lease,
            new(3, 5, 7, 11, 17, false, false));

        Assert.Equal(RemoteLeaseAdmissionCodeV1.Eligible, code);
        Assert.False(Lease.IsParentAuthority);
        Assert.False(Lease.TransfersOwnership);
        Assert.False(Lease.UsesWallClockExpiry);
    }

    [Theory]
    [InlineData(4, 5, 7, 11, 13, false, false, RemoteLeaseAdmissionCodeV1.StaleOwnerIncarnation)]
    [InlineData(3, 6, 7, 11, 13, false, false, RemoteLeaseAdmissionCodeV1.StaleRemoteIncarnation)]
    [InlineData(3, 5, 8, 11, 13, false, false, RemoteLeaseAdmissionCodeV1.StaleOwnerEpoch)]
    [InlineData(3, 5, 7, 12, 13, false, false, RemoteLeaseAdmissionCodeV1.StaleLeaseGeneration)]
    [InlineData(3, 5, 7, 11, 18, false, false, RemoteLeaseAdmissionCodeV1.ExpiredOwnerSequence)]
    [InlineData(3, 5, 7, 11, 13, true, false, RemoteLeaseAdmissionCodeV1.Partitioned)]
    [InlineData(3, 5, 7, 11, 13, false, true, RemoteLeaseAdmissionCodeV1.Revoked)]
    public void RebootEpochExpiryPartitionAndRevocationAllDenyExecution(
        ulong ownerIncarnation, ulong remoteIncarnation, ulong epoch, ulong generation,
        ulong sequence, bool partitioned, bool revoked, RemoteLeaseAdmissionCodeV1 expected)
    {
        Assert.Equal(expected, RemoteAuthorityLeaseEvaluatorV1.Evaluate(Lease,
            new(ownerIncarnation, remoteIncarnation, epoch, generation,
                sequence, partitioned, revoked)));
    }

    [Fact]
    public void RenewalInvalidatesOldGenerationWithoutCreatingDuplicateEligibility()
    {
        var renewed = Lease with { LeaseGeneration = 12, IssuedAtOwnerSequence = 14, NotAfterOwnerSequence = 20 };
        var current = new RemoteLeaseCurrentStateV1(3, 5, 7, 12, 14, false, false);

        Assert.Equal(RemoteLeaseAdmissionCodeV1.StaleLeaseGeneration,
            RemoteAuthorityLeaseEvaluatorV1.Evaluate(Lease, current));
        Assert.Equal(RemoteLeaseAdmissionCodeV1.Eligible,
            RemoteAuthorityLeaseEvaluatorV1.Evaluate(renewed, current));
    }

    [Fact]
    public void ReclaimRequiresExactLeaseFenceAndExplicitEffectClosure()
    {
        var exact = new RemoteEffectClosureV1(1, Lease.LeaseId, Lease.OwnerEpoch,
            Lease.LeaseGeneration, 19, 23, true, true);

        Assert.True(RemoteReclaimPredicateV1.IsSatisfied(Lease, exact));
        Assert.False(exact.AuthorizesReclaim);
        Assert.False(RemoteReclaimPredicateV1.IsSatisfied(Lease, exact with { Fenced = false }));
        Assert.False(RemoteReclaimPredicateV1.IsSatisfied(Lease, exact with { EffectClosed = false }));
        Assert.False(RemoteReclaimPredicateV1.IsSatisfied(Lease, exact with { LeaseGeneration = 12 }));
    }

    [Fact]
    public void EscrowCannotOverAllocateOrDoubleReturnParentResources()
    {
        Assert.Equal(40UL, new RemoteResourceEscrowV1(1, 100, 60, 20, 40).Validate().ReturnedAllocation);
        Assert.Throws<ArgumentException>(() => new RemoteResourceEscrowV1(1, 100, 101, 0, 0).Validate());
        Assert.Throws<ArgumentException>(() => new RemoteResourceEscrowV1(1, 100, 60, 40, 21).Validate());
    }

    [Fact]
    public void LeaseShapeRejectsIdentityAndLogicalIntervalAbaInputs()
    {
        Assert.Throws<ArgumentException>(() => (Lease with { LeaseId = Guid.Empty }).Validate());
        Assert.Throws<ArgumentException>(() => (Lease with { RemoteHostIdentity = Lease.OwnerHostIdentity }).Validate());
        Assert.Throws<ArgumentException>(() => (Lease with { IssuedAtOwnerSequence = 18 }).Validate());
    }
}
