using SingPlus.Admission;
using SingPlus.HybridCpuQualification;

namespace SingPlus.Tests.Qualification;

public sealed class FrontendCapabilityTests
{
    [Fact]
    public void AsyncDescriptorSnapshotsOperationsBeforeQualification()
    {
        var operations = new[] { ManagedAsyncSemanticOperation.Suspend,
            ManagedAsyncSemanticOperation.ContinuationReference, ManagedAsyncSemanticOperation.Resume };
        var descriptor = new ManagedAsyncFrontendContractV1(1, ManagedAsyncAbi.RuntimeAsyncV2, operations);
        operations[0] = (ManagedAsyncSemanticOperation)99;
        Assert.Equal(ManagedAsyncAdmissionDisposition.RecognizedButLoweringUnavailable,
            ManagedAsyncFrontendQualification.Validate(descriptor));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ManagedAsyncSemanticOperation>)descriptor.Operations)[0] = (ManagedAsyncSemanticOperation)99);

        var replacement = new[] { ManagedAsyncSemanticOperation.Suspend,
            ManagedAsyncSemanticOperation.ContinuationReference, ManagedAsyncSemanticOperation.Resume };
        var updated = descriptor with { Operations = replacement };
        replacement[2] = (ManagedAsyncSemanticOperation)99;
        Assert.Equal(ManagedAsyncAdmissionDisposition.RecognizedButLoweringUnavailable,
            ManagedAsyncFrontendQualification.Validate(updated));
        Assert.Equal(ManagedAsyncAdmissionDisposition.Rejected,
            ManagedAsyncFrontendQualification.Validate(default));
    }

    [Fact]
    public void LegacyAsyncPathIsUnchangedAndNewAbiIsRecognitionOnly()
    {
        var legacy = new ManagedAsyncFrontendContractV1(1, ManagedAsyncAbi.LegacyCilStateMachine, []);
        var runtimeV2 = new ManagedAsyncFrontendContractV1(1, ManagedAsyncAbi.RuntimeAsyncV2,
            [ManagedAsyncSemanticOperation.Suspend, ManagedAsyncSemanticOperation.ContinuationReference, ManagedAsyncSemanticOperation.Resume]);
        Assert.Equal(ManagedAsyncAdmissionDisposition.LegacyCompatible, ManagedAsyncFrontendQualification.Validate(legacy));
        Assert.Equal(ManagedAsyncAdmissionDisposition.RecognizedButLoweringUnavailable, ManagedAsyncFrontendQualification.Validate(runtimeV2));
        Assert.Equal(ManagedAsyncAdmissionDisposition.Rejected, ManagedAsyncFrontendQualification.Validate(runtimeV2 with { Version = 2 }));
        Assert.Equal(ManagedAsyncAdmissionDisposition.Rejected, ManagedAsyncFrontendQualification.Validate(runtimeV2 with { Abi = (ManagedAsyncAbi)99 }));
        Assert.Equal(ManagedAsyncAdmissionDisposition.Rejected, ManagedAsyncFrontendQualification.Validate(runtimeV2 with { Operations = [ManagedAsyncSemanticOperation.Suspend] }));
    }

    [Fact]
    public void EveryTargetCapabilityRequiresExplicitProviderConfirmation()
    {
        var all = Enum.GetValues<TargetNumericCapability>();
        Assert.Equal(9, all.Length);
        var none = new TargetCapabilityDescription("provider:neutral", []);
        var confirmed = new TargetCapabilityDescription("provider:qualified", all);
        foreach (var capability in all)
        {
            foreach (var fallback in Enum.GetValues<UnsupportedCapabilityDisposition>())
            {
                var expectedFallback = fallback switch
                {
                    UnsupportedCapabilityDisposition.SoftwareLowering => TargetLoweringChoice.SoftwareLowering,
                    UnsupportedCapabilityDisposition.LibraryCall => TargetLoweringChoice.LibraryCall,
                    _ => TargetLoweringChoice.CompileTimeUnsupported
                };
                Assert.Equal(expectedFallback, none.Decide(capability, fallback));
                Assert.Equal(TargetLoweringChoice.NativeCapabilityConfirmed, confirmed.Decide(capability, fallback));
            }
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => none.Decide((TargetNumericCapability)99, UnsupportedCapabilityDisposition.CompileTimeUnsupported));
        Assert.Throws<ArgumentOutOfRangeException>(() => none.Decide(TargetNumericCapability.Vector64, (UnsupportedCapabilityDisposition)99));
    }

    [Fact]
    public void BclNumericAvailabilityDoesNotClaimTargetCapability()
    {
        _ = Half.One;
        var target = new TargetCapabilityDescription("provider:no-fp16", [TargetNumericCapability.Vector128]);
        Assert.Equal(TargetLoweringChoice.SoftwareLowering, target.Decide(TargetNumericCapability.FP16Arithmetic, UnsupportedCapabilityDisposition.SoftwareLowering));
        Assert.Equal(TargetLoweringChoice.CompileTimeUnsupported, target.Decide(TargetNumericCapability.BFloat16Arithmetic, UnsupportedCapabilityDisposition.CompileTimeUnsupported));
        Assert.Equal(TargetLoweringChoice.NativeCapabilityConfirmed, target.Decide(TargetNumericCapability.Vector128, UnsupportedCapabilityDisposition.CompileTimeUnsupported));
    }

    [Fact]
    public void ProviderClaimsHaveCanonicalVersionedIdentity()
    {
        var first = new TargetCapabilityDescription("provider:qualified",
            [TargetNumericCapability.Vector256, TargetNumericCapability.FP16Arithmetic, TargetNumericCapability.CrossLaneZip]);
        var permuted = new TargetCapabilityDescription("provider:qualified",
            [TargetNumericCapability.CrossLaneZip, TargetNumericCapability.Vector256, TargetNumericCapability.FP16Arithmetic]);
        var otherProvider = new TargetCapabilityDescription("provider:other",
            [TargetNumericCapability.CrossLaneZip, TargetNumericCapability.Vector256, TargetNumericCapability.FP16Arithmetic]);
        Assert.Equal(1, TargetCapabilityDescription.SchemaVersion);
        Assert.Equal(first.SerializeCanonical(), permuted.SerializeCanonical());
        Assert.Equal(first.CanonicalDigest, permuted.CanonicalDigest);
        var reloaded = TargetCapabilityDescription.DeserializeCanonical(first.SerializeCanonical());
        Assert.Equal(first.CanonicalDigest, reloaded.CanonicalDigest);
        Assert.Equal(TargetLoweringChoice.NativeCapabilityConfirmed,
            reloaded.Decide(TargetNumericCapability.CrossLaneZip, UnsupportedCapabilityDisposition.CompileTimeUnsupported));
        Assert.Equal(Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(first.SerializeCanonical())).ToLowerInvariant(), first.CanonicalDigest);
        Assert.NotEqual(first.CanonicalDigest, otherProvider.CanonicalDigest);
        Assert.Throws<ArgumentException>(() => new TargetCapabilityDescription("provider:qualified",
            [TargetNumericCapability.Vector64, TargetNumericCapability.Vector64]));
        Assert.Throws<ArgumentException>(() => new TargetCapabilityDescription("provider\nunstable", []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TargetCapabilityDescription("provider:qualified", [(TargetNumericCapability)99]));
        foreach (var invalid in new[]
        {
            "{\"SchemaVersion\":2,\"ProviderId\":\"provider:qualified\",\"ConfirmedCapabilities\":[0]}",
            "{\"SchemaVersion\":1,\"ProviderId\":\"provider:qualified\",\"ConfirmedCapabilities\":[3,0]}",
            "{\"SchemaVersion\":1,\"ProviderId\":\"provider:qualified\",\"ConfirmedCapabilities\":[0,0]}",
            "{\"SchemaVersion\":1,\"ProviderId\":\"provider:qualified\",\"ConfirmedCapabilities\":[99]}",
            "{\"ProviderId\":\"provider:qualified\",\"SchemaVersion\":1,\"ConfirmedCapabilities\":[0]}",
            "{ \"SchemaVersion\":1,\"ProviderId\":\"provider:qualified\",\"ConfirmedCapabilities\":[0]}"
        })
            Assert.Throws<InvalidDataException>(() => TargetCapabilityDescription.DeserializeCanonical(global::System.Text.Encoding.UTF8.GetBytes(invalid)));
    }
}
