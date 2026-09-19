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
        var first = Authority(new AuthorityRealmId(Guid.Parse("10000000-0000-0000-0000-000000000001")));
        var minted = Mint(first);
        var reference = first.GetReference(minted.CapabilityId).Value;
        var restarted = Authority(new AuthorityRealmId(Guid.Parse("20000000-0000-0000-0000-000000000002")));

        Assert.Equal(KernelError.WrongAuthorityRealm,
            restarted.Validate(reference, Subject, 7, CapabilityRights.Read).Error);
        Assert.Equal(KernelError.ForgedCapability,
            first.Validate(reference with { Nonce = Guid.NewGuid() }, Subject, 7, CapabilityRights.Read).Error);
    }

    [Fact]
    public void SubjectAndResourceGenerationsAreIndependentlyStale()
    {
        var authority = Authority();
        var minted = authority.Mint(Issuer, Subject, ResourceKind.Device, "device:0",
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
        var kernel = new RuntimeKernel();
        TestFixtures.Create(kernel, 1, 10);
        var (_, subject) = TestFixtures.Create(kernel, 2, 20);
        var minted = kernel.MintCapability(Issuer, subject, ResourceKind.Device, "device:generation-bound",
            CapabilityRights.Read, resourceGeneration: 44).Value!;

        Assert.True(kernel.ValidateCapability(subject, minted.CapabilityId, CapabilityRights.Read, 44).IsSuccess);
        Assert.Equal(KernelError.StaleGeneration,
            kernel.ValidateCapability(subject, minted.CapabilityId, CapabilityRights.Read, 45).Error);
    }

    [Fact]
    public void DescriptorAndInspectionProjectionDoNotCarryOpaqueAuthority()
    {
        var authority = Authority();
        var minted = Mint(authority);
        var inspection = Assert.Single(authority.InspectionSnapshot());

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
        var repeated = Guid.Parse("30000000-0000-0000-0000-000000000003");
        var authority = Authority(nonceFactory: () => repeated);
        Assert.True(authority.Mint(Issuer, Subject, ResourceKind.Device, "first", CapabilityRights.Read, 7).IsSuccess);

        var collision = authority.Mint(Issuer, Subject, ResourceKind.Device, "second", CapabilityRights.Read, 7);

        Assert.Equal(KernelError.CapacityExhausted, collision.Error);
        Assert.Single(authority.InspectionSnapshot());
    }

    [Fact]
    public void CapabilityIdentityDoesNotWrapOrReuseMaxValue()
    {
        var authority = Authority(initialCapabilityId: ulong.MaxValue);
        var final = Mint(authority);
        Assert.Equal(ulong.MaxValue, final.CapabilityId.Value);
        Assert.True(authority.Revoke(final.CapabilityId).IsSuccess);

        var exhausted = authority.Mint(Issuer, Subject, ResourceKind.Device, "next", CapabilityRights.Read, 7);

        Assert.Equal(KernelError.CapacityExhausted, exhausted.Error);
        Assert.Single(authority.InspectionSnapshot());
    }

    [Fact]
    public void PerSubjectQuotaIsolationAndGlobalCapacityAreFailClosed()
    {
        var authority = Authority(limits: new(3, 1));
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
        var authority = Authority(limits: new(128, 8));
        var results = new ConcurrentBag<KernelResult<CapabilityDescriptorV1>>();

        Parallel.For(0, 64, index => results.Add(authority.Mint(
            Issuer, Subject, ResourceKind.Device, $"device:{index}", CapabilityRights.Read, 7)));

        Assert.Equal(8, results.Count(result => result.IsSuccess));
        Assert.Equal(56, results.Count(result => result.Error == KernelError.CapacityExhausted));
        Assert.Equal(8, authority.InspectionSnapshot().Count(record => record.State == CapabilityRecordState.Active));
    }

    [Fact]
    public void RevocationFreesOnlyLiveQuotaAndNeverResidentTableCapacity()
    {
        var authority = Authority(limits: new(2, 1));
        var first = Mint(authority);
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
