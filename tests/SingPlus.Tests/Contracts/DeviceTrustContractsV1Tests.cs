using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class DeviceTrustContractsV1Tests
{
    private const string Firmware = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly TrustObligationV1 Obligation = new(1, "device:accelerator-7", Firmware,
        DeviceTrustAssuranceV1.ModelOnly, 11, 13, 17, 19, 23);
    private static readonly TrustEvidenceV1 Evidence = new(1, "device:accelerator-7", Firmware,
        DeviceTrustAssuranceV1.ModelOnly, 11, 13, 17, 19, 23, 29);

    [Fact]
    public void ExactGenerationBoundEvidenceSatisfiesPredicateWithoutGrantingAuthority()
    {
        var decision = DeviceTrustPredicateEvaluatorV1.Evaluate(Obligation, Evidence);

        Assert.True(decision.IsSatisfied);
        Assert.False(decision.GrantsAuthority);
        Assert.False(Obligation.GrantsAuthority);
        Assert.False(Evidence.GrantsAuthority);
        Assert.False(Evidence.AuthorizesExecution);
        Assert.False(Evidence.AuthorizesRegionAccess);
    }

    [Theory]
    [InlineData(nameof(TrustEvidenceV1.ProviderTrustGeneration), DeviceTrustDecisionCodeV1.StaleProviderTrustGeneration)]
    [InlineData(nameof(TrustEvidenceV1.DeviceGeneration), DeviceTrustDecisionCodeV1.StaleDeviceGeneration)]
    [InlineData(nameof(TrustEvidenceV1.AssignmentGeneration), DeviceTrustDecisionCodeV1.StaleAssignmentGeneration)]
    [InlineData(nameof(TrustEvidenceV1.ResetGeneration), DeviceTrustDecisionCodeV1.StaleResetGeneration)]
    [InlineData(nameof(TrustEvidenceV1.PolicyGeneration), DeviceTrustDecisionCodeV1.StalePolicyGeneration)]
    public void AnyMutableGenerationDriftRejectsReplayedEvidence(string generation, DeviceTrustDecisionCodeV1 expected)
    {
        var stale = generation switch
        {
            nameof(TrustEvidenceV1.ProviderTrustGeneration) => Evidence with { ProviderTrustGeneration = 12 },
            nameof(TrustEvidenceV1.DeviceGeneration) => Evidence with { DeviceGeneration = 14 },
            nameof(TrustEvidenceV1.AssignmentGeneration) => Evidence with { AssignmentGeneration = 18 },
            nameof(TrustEvidenceV1.ResetGeneration) => Evidence with { ResetGeneration = 20 },
            nameof(TrustEvidenceV1.PolicyGeneration) => Evidence with { PolicyGeneration = 24 },
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };

        Assert.Equal(expected, DeviceTrustPredicateEvaluatorV1.Evaluate(Obligation, stale).Code);
    }

    [Fact]
    public void WrongDeviceFirmwareAndAssuranceAreDistinctDenials()
    {
        Assert.Equal(DeviceTrustDecisionCodeV1.WrongDevice,
            DeviceTrustPredicateEvaluatorV1.Evaluate(Obligation, Evidence with { DeviceIdentity = "device:other" }).Code);
        Assert.Equal(DeviceTrustDecisionCodeV1.WrongFirmware,
            DeviceTrustPredicateEvaluatorV1.Evaluate(Obligation, Evidence with { FirmwareMeasurementDigest = new('1', 64) }).Code);
        Assert.Equal(DeviceTrustDecisionCodeV1.InsufficientAssurance,
            DeviceTrustPredicateEvaluatorV1.Evaluate(
                Obligation with { MinimumAssurance = DeviceTrustAssuranceV1.HardwareAttested }, Evidence).Code);
    }

    [Fact]
    public void UnknownMandatoryAssuranceAndNoncanonicalEvidenceFailClosed()
    {
        Assert.Throws<NotSupportedException>(() =>
            (Obligation with { MinimumAssurance = (DeviceTrustAssuranceV1)255 }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (Evidence with { FirmwareMeasurementDigest = Firmware.ToUpperInvariant() }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (Evidence with { EvidenceSequence = 0 }).Validate());
    }
}
