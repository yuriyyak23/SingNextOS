using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class CancellationContainmentSemanticsV1Tests
{
    [Theory]
    [InlineData(CancellationClassV1.None, ExternalCancellationSupport.BeforeSubmissionOnly, false)]
    [InlineData(CancellationClassV1.BeforeDispatch, ExternalCancellationSupport.BeforeSubmissionOnly, false)]
    [InlineData(CancellationClassV1.ExactAcknowledgement, ExternalCancellationSupport.ProviderCooperative, true)]
    public void CancellationMappingIsCanonicalAndNeverAuthority(CancellationClassV1 value,
        ExternalCancellationSupport support, bool exactAcknowledgement)
    {
        var semantics = ExternalCancellationSemantics.ForClass(value);
        Assert.Equal(support, semantics.RequiredAdmissionSupport);
        Assert.Equal(exactAcknowledgement, semantics.RequiresExactProviderAcknowledgement);
        Assert.Equal(semantics, semantics.Canonicalize());
        Assert.False(semantics.AuthorizesCancellation);
        Assert.False(semantics.ProvesClosure);
    }

    [Fact]
    public void UnknownOrNoncanonicalCancellationSemanticsFailClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExternalCancellationSemantics.ForClass((CancellationClassV1)byte.MaxValue));
        Assert.Throws<NotSupportedException>(() =>
            (ExternalCancellationSemantics.ForClass(CancellationClassV1.None) with { Version = 2 }).Canonicalize());
        Assert.Throws<ArgumentException>(() =>
            (ExternalCancellationSemantics.ForClass(CancellationClassV1.BeforeDispatch) with
            { RequiredAdmissionSupport = ExternalCancellationSupport.ProviderCooperative }).Canonicalize());
    }

    [Fact]
    public void ContainmentReceiptIsExactEvidenceAndNeverReleaseOrReclaimAuthority()
    {
        var receipt = new ContainmentClosureReceiptV1(1,
            new(new(7), new(3)), "provider:one", 11, Guid.NewGuid());
        Assert.Equal(receipt, receipt.Canonicalize());
        Assert.False(receipt.AuthorizesRelease);
        Assert.False(receipt.AuthorizesReclaim);
        Assert.False(receipt.AuthorizesExecution);
        Assert.False(new ReleasePlan(false, true, true).IsAuthority);

        Assert.Throws<NotSupportedException>(() => (receipt with { Version = 2 }).Canonicalize());
        Assert.Throws<ArgumentException>(() => (receipt with { ProviderGeneration = 0 }).Canonicalize());
        Assert.Throws<ArgumentException>(() => (receipt with { ReceiptId = Guid.Empty }).Canonicalize());
        Assert.Throws<ArgumentException>(() => (receipt with { ProviderIdentity = " provider:one" }).Canonicalize());
    }
}
