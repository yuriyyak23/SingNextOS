using System.Collections.Concurrent;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Tests.Capabilities;

public sealed class SingCapPhase02CapabilityLedgerTests
{
    private static readonly DomainId Issuer = new(10);
    private static readonly DomainId Subject = new(20);

    [Fact]
    public void OpaqueReferenceIsBoundToRuntimeRealmAndNonce()
    {
        CapabilityAuthority first = Authority(new AuthorityRealmId(Guid.Parse("10000000-0000-0000-0000-000000000001")));
        CapabilityDescriptorV1 minted = Mint(first);
        CapabilityAuthorityReference reference = first.GetReference(minted.CapabilityId).Value;
        CapabilityAuthority restarted = Authority(new AuthorityRealmId(Guid.Parse("20000000-0000-0000-0000-000000000002")));

        Assert.Equal(KernelError.WrongAuthorityRealm,
            restarted.Validate(reference, Subject, 7, CapabilityRights.Read).Error);
        Assert.Equal(KernelError.ForgedCapability,
            first.Validate(reference with { Nonce = Guid.NewGuid() }, Subject, 7, CapabilityRights.Read).Error);
    }

    [Fact]
    public void SubjectAndResourceGenerationsAreIndependentlyStale()
    {
        CapabilityAuthority authority = Authority();
        CapabilityDescriptorV1 minted = authority.Mint(Issuer, Subject, ResourceKind.Device, "device:0",
            CapabilityRights.Read, subjectGeneration: 7, resourceGeneration: 11).Value!;

        Assert.Equal(KernelError.StaleGeneration,
            authority.Validate(minted.CapabilityId, Subject, 8, CapabilityRights.Read, 11).Error);
        Assert.Equal(KernelError.StaleGeneration,
            authority.Validate(minted.CapabilityId, Subject, 7, CapabilityRights.Read, 12).Error);
        Assert.True(authority.Validate(minted.CapabilityId, Subject, 7, CapabilityRights.Read, 11).IsSuccess);
    }

    [Fact]
    public void RuntimeKernelConsumerEnforcesExactResourceGeneration()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        TestFixtures.Create(kernel, 1, 10);
        (Sip.SingProcess _, ProcessHandle subject) = TestFixtures.Create(kernel, 2, 20);
        CapabilityDescriptorV1 minted = kernel.MintCapability(Issuer, subject, ResourceKind.Device, "device:generation-bound",
            CapabilityRights.Read, resourceGeneration: 44).Value!;

        Assert.True(kernel.ValidateCapability(subject, minted.CapabilityId, CapabilityRights.Read, 44).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration,
            kernel.ValidateCapability(subject, minted.CapabilityId, CapabilityRights.Read, 45).Error);
    }

    [Fact]
    public void DescriptorAndInspectionProjectionDoNotCarryOpaqueAuthority()
    {
        CapabilityAuthority authority = Authority();
        CapabilityDescriptorV1 minted = Mint(authority);
        CapabilityAuthorityInspectionRecord inspection = Assert.Single(authority.InspectionSnapshot());

        Assert.Equal(minted, inspection.Descriptor);
        Assert.Equal(16, inspection.RealmFingerprint.Length);
        Assert.DoesNotContain(typeof(CapabilityAuthorityReference).GetProperties(),
            property => inspection.GetType().GetProperty(property.Name)?.PropertyType == property.PropertyType &&
                        property.Name == nameof(CapabilityAuthorityReference.Nonce));

        authority.Retire(minted.CapabilityId);
        Assert.Equal(KernelError.CapabilityRevoked,
            authority.Validate(inspection.Descriptor.CapabilityId, Subject, 7, CapabilityRights.Read).Error);
    }

    [Fact]
    public void NonceCollisionRetriesAreBoundedAndFailClosed()
    {
        Guid repeated = Guid.Parse("30000000-0000-0000-0000-000000000003");
        CapabilityAuthority authority = Authority(nonceFactory: () => repeated);
        Assert.True(authority.Mint(Issuer, Subject, ResourceKind.Device, "first", CapabilityRights.Read, 7).IsSuccess);

        KernelResult<CapabilityDescriptorV1> collision = authority.Mint(Issuer, Subject, ResourceKind.Device, "second", CapabilityRights.Read, 7);

        Assert.Equal(KernelError.CapacityExhausted, collision.Error);
        Assert.Single(authority.InspectionSnapshot());
    }

    [Fact]
    public void CapabilityIdentityDoesNotWrapOrReuseMaxValue()
    {
        CapabilityAuthority authority = Authority(initialCapabilityId: ulong.MaxValue);
        CapabilityDescriptorV1 final = Mint(authority);
        Assert.Equal(ulong.MaxValue, final.CapabilityId.Value);
        Assert.True(authority.Revoke(final.CapabilityId).IsSuccess);

        KernelResult<CapabilityDescriptorV1> exhausted = authority.Mint(Issuer, Subject, ResourceKind.Device, "next", CapabilityRights.Read, 7);

        Assert.Equal(KernelError.CapacityExhausted, exhausted.Error);
        Assert.Single(authority.InspectionSnapshot());
    }

    [Fact]
    public void RejectedMintDoesNotConsumeFinalCapabilityIdentity()
    {
        CapabilityAuthority authority = Authority(initialCapabilityId: ulong.MaxValue);

        KernelResult<CapabilityDescriptorV1> rejected = authority.Mint(Issuer, Subject, ResourceKind.Device, "invalid",
            CapabilityRights.Read, subjectGeneration: 7, resourceGeneration: 1,
            range: null, session: null, quota: 1, delegationDepth: 1,
            schema: (CapabilityConstraintSchema)ushort.MaxValue);
        CapabilityDescriptorV1 accepted = Mint(authority);

        Assert.Equal(KernelError.DelegationDenied, rejected.Error);
        Assert.True(accepted.CapabilityId.Value == ulong.MaxValue);
        Assert.Single(authority.InspectionSnapshot());
    }

    [Fact]
    public void RejectedDelegationDoesNotConsumeFinalCapabilityIdentity()
    {
        CapabilityAuthority authority = Authority(initialCapabilityId: ulong.MaxValue - 1);
        CapabilityDescriptorV1 source = authority.Mint(Issuer, Subject, ResourceKind.Device, "device:source",
            CapabilityRights.Read | CapabilityRights.Delegate, 7).Value!;

        KernelResult<CapabilityDescriptorV1> rejected = authority.Delegate(source.CapabilityId, Subject, new DomainId(21),
            CapabilityRights.Read, 8, child => child with
            {
                Resource = child.Resource with { ResourceId = "device:expanded" }
            });
        KernelResult<CapabilityDescriptorV1> accepted = authority.Delegate(source.CapabilityId, Subject, new DomainId(21),
            CapabilityRights.Read, 8);

        Assert.Equal(KernelError.DelegationDenied, rejected.Error);
        Assert.True(accepted.IsSuccess, accepted.Message);
        Assert.Equal(ulong.MaxValue, accepted.Value!.CapabilityId.Value);
        Assert.Equal(2, authority.InspectionSnapshot().Length);
    }

    [Fact]
    public void PerSubjectQuotaIsolationAndGlobalCapacityAreFailClosed()
    {
        CapabilityAuthority authority = Authority(limits: new(3, 1));
        Assert.True(authority.Mint(Issuer, Subject, ResourceKind.Device, "a", CapabilityRights.Read, 7).IsSuccess);
        Assert.Equal(KernelError.CapacityExhausted,
            authority.Mint(Issuer, Subject, ResourceKind.Device, "b", CapabilityRights.Read, 7).Error);
        Assert.True(authority.Mint(Issuer, new DomainId(21), ResourceKind.Device, "c", CapabilityRights.Read, 7).IsSuccess);
        Assert.True(authority.Mint(Issuer, new DomainId(22), ResourceKind.Device, "d", CapabilityRights.Read, 7).IsSuccess);
        Assert.Equal(KernelError.CapacityExhausted,
            authority.Mint(Issuer, new DomainId(23), ResourceKind.Device, "e", CapabilityRights.Read, 7).Error);
    }

    [Fact]
    public void ConcurrentMintCannotOvershootPerSubjectLiveQuota()
    {
        CapabilityAuthority authority = Authority(limits: new(128, 8));
        ConcurrentBag<KernelResult<CapabilityDescriptorV1>> results = new ConcurrentBag<KernelResult<CapabilityDescriptorV1>>();

        Parallel.For(0, 64, index => results.Add(authority.Mint(
            Issuer, Subject, ResourceKind.Device, $"device:{index}", CapabilityRights.Read, 7)));

        Assert.Equal(8, results.Count(result => result.IsSuccess));
        Assert.Equal(56, results.Count(result => result.Error == KernelError.CapacityExhausted));
        Assert.Equal(8, authority.InspectionSnapshot().Count(record => record.State == CapabilityRecordState.Active));
    }

    [Fact]
    public void RevocationFreesOnlyLiveQuotaAndNeverResidentTableCapacity()
    {
        CapabilityAuthority authority = Authority(limits: new(2, 1));
        CapabilityDescriptorV1 first = Mint(authority);
        authority.Revoke(first.CapabilityId);
        Assert.True(authority.Mint(Issuer, Subject, ResourceKind.Device, "replacement", CapabilityRights.Read, 7).IsSuccess);
        Assert.Equal(KernelError.CapacityExhausted,
            authority.Mint(Issuer, new DomainId(21), ResourceKind.Device, "global-full", CapabilityRights.Read, 7).Error);
    }

    private static CapabilityAuthority Authority(
        AuthorityRealmId? realm = null,
        CapabilityAuthorityLimits? limits = null,
        ulong initialCapabilityId = 1,
        Func<Guid>? nonceFactory = null) => new(
            realm ?? new AuthorityRealmId(Guid.Parse("40000000-0000-0000-0000-000000000004")),
            limits ?? new CapabilityAuthorityLimits(128, 32), initialCapabilityId, nonceFactory);

    private static CapabilityDescriptorV1 Mint(CapabilityAuthority authority) =>
        authority.Mint(Issuer, Subject, ResourceKind.Device, "device:0", CapabilityRights.Read, 7).Value!;
}
