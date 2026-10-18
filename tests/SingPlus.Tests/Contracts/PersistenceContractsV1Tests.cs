using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class PersistenceContractsV1Tests
{
    private static readonly PersistenceSemanticsV1 Semantics = new(1,
        "singnext:managed-durable-output-model", "media:model:1",
        PersistenceDomainClassV1.NamedManagedModel,
        PersistenceOrderingV1.DataThenMetadataThenCommitRecord, 7, 11, true);
    private static readonly DurableOutputBindingV1 Binding = new(1, "operation:42", 13,
        new('a', 64), new('b', 64), 17, Semantics);
    private static readonly ProviderPersistEvidenceV1 Evidence = new(1,
        Semantics.ProviderIdentity, Semantics.MediaIdentity, Binding.OperationCorrelation,
        Binding.ContentDigest, Binding.MetadataDigest, Semantics.DomainClass,
        PersistEvidenceAssuranceV1.ModelOnly, 7, 11, 13, 17, 1, 2, 3, 4, 5);

    [Fact]
    public void ExactTupleMatchesWithoutGrantingPublicationOrRegionAuthority()
    {
        Assert.Equal(PersistEvidenceMatchCodeV1.Exact, PersistEvidenceMatcherV1.Match(Binding, Evidence));
        Assert.False(Semantics.GrantsRegionAuthority);
        Assert.False(Semantics.AuthorizesPublication);
        Assert.False(Binding.AuthorizesPublication);
        Assert.False(Evidence.AuthorizesPublication);
        Assert.False(Evidence.RestoresAuthority);
    }

    [Fact]
    public void ProviderMediaOperationAndRecoveryGenerationsAreExact()
    {
        Assert.Equal(PersistEvidenceMatchCodeV1.StaleProviderGeneration,
            PersistEvidenceMatcherV1.Match(Binding, Evidence with { ProviderGeneration = 8 }));
        Assert.Equal(PersistEvidenceMatchCodeV1.StaleMediaGeneration,
            PersistEvidenceMatcherV1.Match(Binding, Evidence with { MediaGeneration = 12 }));
        Assert.Equal(PersistEvidenceMatchCodeV1.StaleOperationGeneration,
            PersistEvidenceMatcherV1.Match(Binding, Evidence with { OperationGeneration = 14 }));
        Assert.Equal(PersistEvidenceMatchCodeV1.StaleRecoveryGeneration,
            PersistEvidenceMatcherV1.Match(Binding, Evidence with { RecoveryGeneration = 18 }));
    }

    [Fact]
    public void ContentReplayAndNamedIdentityMismatchAreRejected()
    {
        Assert.Equal(PersistEvidenceMatchCodeV1.ContentMismatch,
            PersistEvidenceMatcherV1.Match(Binding, Evidence with { ContentDigest = new('c', 64) }));
        Assert.Equal(PersistEvidenceMatchCodeV1.WrongProvider,
            PersistEvidenceMatcherV1.Match(Binding, Evidence with { ProviderIdentity = "provider:other" }));
        Assert.Equal(PersistEvidenceMatchCodeV1.WrongMedia,
            PersistEvidenceMatcherV1.Match(Binding, Evidence with { MediaIdentity = "media:other" }));
    }

    [Fact]
    public void PersistOrderRequiresDurableBeforePublished()
    {
        Assert.Throws<ArgumentException>(() => (Evidence with { DataPersistedSequence = 1 }).Validate());
        Assert.Throws<ArgumentException>(() => (Evidence with { PublishedSequence = 4 }).Validate());
        Assert.Throws<ArgumentException>(() => (Semantics with { DurableBeforePublication = false }).Validate());
    }

    [Fact]
    public void ModelEvidenceCannotUpgradeAPhysicalPersistenceDomain()
    {
        Assert.Throws<ArgumentException>(() => (Evidence with
            { DomainClass = PersistenceDomainClassV1.Eadr }).Validate());
    }

    [Fact]
    public void RecoveryRecordRequiresFreshAdmissionAndRestoresNoAuthority()
    {
        var record = new RecoveryFreshnessRecord(1, "operation:42", new('d', 64), 3, 5, 7, 17).Validate();

        Assert.False(record.HasFreshAdmission(3, 6, 8));
        Assert.False(record.HasFreshAdmission(4, 5, 8));
        Assert.False(record.HasFreshAdmission(4, 6, 7));
        Assert.True(record.HasFreshAdmission(4, 6, 8));
        Assert.False(record.PreservesCapability);
        Assert.False(record.PreservesSession);
        Assert.False(record.PreservesProviderAuthority);
        Assert.False(record.GrantsRegionAuthority);
    }
}
