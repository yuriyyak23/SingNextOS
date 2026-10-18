using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class InformationFlowContractsV1Tests
{
    [Fact]
    public void JoinRaisesConfidentialityAndLowersIntegrity()
    {
        var joined = DataLabelLatticeV1.Join(Label(ConfidentialityClassV1.Protected, IntegrityClassV1.Trusted),
            Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Untrusted));
        Assert.Equal(Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Untrusted), joined);
        Assert.False(joined.AuthorizesRead);
        Assert.False(joined.AuthorizesWrite);
        Assert.False(joined.AuthorizesDeclassification);
    }

    [Fact]
    public void FlowRequiresBothConfidentialityAndIntegrityBounds()
    {
        var source = Label(ConfidentialityClassV1.Protected, IntegrityClassV1.Validated);
        Assert.True(DataLabelLatticeV1.CanFlowTo(source, new(1,
            Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Untrusted))));
        Assert.False(DataLabelLatticeV1.CanFlowTo(source, new(1,
            Label(ConfidentialityClassV1.Public, IntegrityClassV1.Untrusted))));
        Assert.False(DataLabelLatticeV1.CanFlowTo(source, new(1,
            Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Trusted))));
    }

    [Fact]
    public void DeclassifyAndEndorseChangeExactlyOneDimension()
    {
        var source = Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Untrusted);
        Assert.Equal(InformationFlowTransitionKindV1.Declassify,
            new InformationFlowTransitionV1(1, InformationFlowTransitionKindV1.Declassify, source,
                Label(ConfidentialityClassV1.Protected, IntegrityClassV1.Untrusted)).Validate().Kind);
        Assert.Equal(InformationFlowTransitionKindV1.Endorse,
            new InformationFlowTransitionV1(1, InformationFlowTransitionKindV1.Endorse, source,
                Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Validated)).Validate().Kind);
        Assert.Throws<ArgumentException>(() => new InformationFlowTransitionV1(1,
            InformationFlowTransitionKindV1.Declassify, source,
            Label(ConfidentialityClassV1.Public, IntegrityClassV1.Trusted)).Validate());
    }

    [Fact]
    public void UnknownVersionsAndClassesFailClosed()
    {
        Assert.Throws<NotSupportedException>(() => new DataLabelV1(2,
            ConfidentialityClassV1.Public, IntegrityClassV1.Trusted).Validate());
        Assert.False(DataLabelLatticeV1.CanFlowTo(new(1, (ConfidentialityClassV1)0,
            IntegrityClassV1.Trusted), new(1, Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Untrusted))));
    }

    private static DataLabelV1 Label(ConfidentialityClassV1 confidentiality, IntegrityClassV1 integrity) =>
        new(1, confidentiality, integrity);
}
