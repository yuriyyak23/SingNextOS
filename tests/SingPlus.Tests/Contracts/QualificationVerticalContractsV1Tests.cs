using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class QualificationVerticalContractsV1Tests
{
    [Fact]
    public void ExactVerticalRecordsEveryRequiredAuditFieldWithoutGrantingAuthority()
    {
        var evidence = FirstQualificationVerticalEvidenceV1.Create(Events());

        Assert.Equal(Enum.GetValues<QualificationVerticalStepV1>(), evidence.Transitions.Select(item => item.Step));
        Assert.All(evidence.Transitions, item =>
        {
            Assert.False(item.AuthorizesExecution);
            Assert.False(item.AuthorizesPublication);
        });
        Assert.False(evidence.AuthorizesExecution);
        Assert.False(evidence.ReplacesOwnerState);
    }

    [Fact]
    public void MissingReorderedOrDuplicateTransitionFailsClosed()
    {
        var events = Events();
        Assert.Throws<ArgumentException>(() => FirstQualificationVerticalEvidenceV1.Create(events.Skip(1)));
        Assert.Throws<ArgumentException>(() => FirstQualificationVerticalEvidenceV1.Create(events.Reverse()));
        Assert.Throws<ArgumentException>(() => FirstQualificationVerticalEvidenceV1.Create(events.Append(events[^1])));
    }

    [Fact]
    public void MalformedOwnerGenerationAndRollbackEvidenceIsRejected()
    {
        var events = Events();
        Assert.Throws<ArgumentException>(() => (events[0] with { Owner = " " }).Validate());
        Assert.Throws<ArgumentException>(() => (events[0] with { GenerationVectorDigest = "bad" }).Validate());
        Assert.Throws<ArgumentException>(() => (events[0] with { RollbackOrCompensationRule = "" }).Validate());
    }

    private static QualificationTransitionEvidenceV1[] Events() =>
        Enum.GetValues<QualificationVerticalStepV1>().Select((step, index) => new QualificationTransitionEvidenceV1(
            1, (ulong)index + 1, step, "owner:test", new('a', 64), new('b', 64),
            QualificationFailureStateV1.None, "linearization:test", "rollback:test")).ToArray();
}
