using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class MemorySemanticsV1Tests
{
    [Fact]
    public void SafeStagedContourRoundTripsAndGrantsNoAuthority()
    {
        var semantics = StagedOutput();
        var parsed = MemorySemanticsV1.ParseCanonical(semantics.SerializeCanonical());
        var clause = semantics.ToClause(SemanticExtensionRequirement.Mandatory);

        Assert.Equal(semantics, parsed);
        Assert.Equal(MemorySemanticsV1.ExtensionClassId, clause.ClassId);
        Assert.False(semantics.AuthorizesOwnership);
        Assert.False(semantics.AuthorizesExecution);
        Assert.False(semantics.AuthorizesPublication);
    }

    [Fact]
    public void CombinedReadOnlyInputAndExclusiveStagedOutputIsRepresentable()
    {
        var combined = StagedOperation();
        Assert.Equal(combined, MemorySemanticsV1.ParseCanonical(combined.SerializeCanonical()));
        Assert.False(MemorySemanticPartialOrderV1.Refines(StagedOutput(), combined));
        Assert.False(MemorySemanticPartialOrderV1.Refines(combined, StagedOutput()));
    }

    [Fact]
    public void StagedOutputCannotClaimSharedOwnershipOrSkipFenceVisibilityPublicationChain()
    {
        Assert.Throws<ArgumentException>(() => (StagedOutput() with
            { Ownership = MemoryOwnershipClassV1.SharedMutable }).Validate());
        Assert.Throws<ArgumentException>(() => (StagedOutput() with
            { Order = MemoryOrderClassV1.Unspecified }).Validate());
        Assert.Throws<ArgumentException>(() => (StagedOutput() with
            { Order = MemoryOrderClassV1.SequentiallyConsistent }).Validate());
        Assert.Throws<ArgumentException>(() => (StagedOutput() with
            { Visibility = MemoryVisibilityClassV1.DevicePrivateUntilCompletion }).Validate());
        Assert.Throws<ArgumentException>(() => (StagedOutput() with
            { PublicationMode = MemoryPublicationModeV1.DirectCoherent }).Validate());
    }

    [Fact]
    public void DirectCoherentMutableAndSharedMutableAreSeparateContours()
    {
        var direct = new MemorySemanticsV1(1, MemoryOwnershipClassV1.SharedMutable,
            MemoryAccessClassV1.DirectMutableOutput, MemoryOrderClassV1.SequentiallyConsistent,
            MemoryAtomicityClassV1.NaturallyAlignedScalar, MemoryCoherenceAssumptionV1.HardwareCoherent,
            MemoryVisibilityClassV1.ConsumerVisibleAfterFence, MemoryPublicationModeV1.DirectCoherent).Validate();

        Assert.False(MemorySemanticPartialOrderV1.Refines(direct, StagedOutput()));
        Assert.False(MemorySemanticPartialOrderV1.Refines(StagedOutput(), direct));
        Assert.Throws<ArgumentException>(() => (direct with
            { CoherenceAssumption = MemoryCoherenceAssumptionV1.ExplicitFenceOnly }).Validate());
    }

    [Fact]
    public void PartialOrderIsReflexiveAntisymmetricForDistinctComparableValuesAndTransitive()
    {
        var weak = ReadOnly(MemoryOrderClassV1.Unspecified, MemoryVisibilityClassV1.DevicePrivateUntilCompletion);
        var middle = ReadOnly(MemoryOrderClassV1.CompletionBeforeVisibilityFence,
            MemoryVisibilityClassV1.DevicePrivateUntilCompletion);
        var strong = ReadOnly(MemoryOrderClassV1.CompletionBeforeVisibilityFence,
            MemoryVisibilityClassV1.ConsumerVisibleAfterFence);
        var sc = ReadOnly(MemoryOrderClassV1.SequentiallyConsistent,
            MemoryVisibilityClassV1.ConsumerVisibleAfterFence);

        foreach (var item in new[] { weak, middle, strong, sc })
            Assert.True(MemorySemanticPartialOrderV1.Refines(item, item));
        Assert.True(MemorySemanticPartialOrderV1.Refines(middle, weak));
        Assert.False(MemorySemanticPartialOrderV1.Refines(weak, middle));
        Assert.True(MemorySemanticPartialOrderV1.Refines(strong, middle));
        Assert.True(MemorySemanticPartialOrderV1.Refines(strong, weak));
        Assert.True(MemorySemanticPartialOrderV1.Refines(sc, weak));
        Assert.False(MemorySemanticPartialOrderV1.Refines(sc, strong));
        Assert.False(MemorySemanticPartialOrderV1.Refines(strong, sc));
    }

    [Fact]
    public void MutationOfOneRequiredDimensionProducesAReproducibleRejection()
    {
        var required = StagedOutput();
        var mutations = new[]
        {
            required with { Atomicity = MemoryAtomicityClassV1.NaturallyAlignedScalar },
            required with { Order = MemoryOrderClassV1.SequentiallyConsistent },
            required with { CoherenceAssumption = MemoryCoherenceAssumptionV1.HardwareCoherent },
        };

        Assert.All(mutations, strongerRequirement =>
            Assert.False(MemorySemanticPartialOrderV1.Refines(required, strongerRequirement)));
    }

    private static MemorySemanticsV1 StagedOutput() => new(1, MemoryOwnershipClassV1.Exclusive,
        MemoryAccessClassV1.ExclusiveStagedOutput, MemoryOrderClassV1.CompletionBeforeVisibilityFence,
        MemoryAtomicityClassV1.None, MemoryCoherenceAssumptionV1.ExplicitFenceOnly,
        MemoryVisibilityClassV1.ConsumerVisibleAfterFence,
        MemoryPublicationModeV1.StagedWithheldUntilVisible);

    internal static MemorySemanticsV1 StagedOperation() => new MemorySemanticsV1(1,
        MemoryOwnershipClassV1.SharedReadOnlyInputAndExclusiveOutput,
        MemoryAccessClassV1.ReadOnlyInputAndExclusiveStagedOutput,
        MemoryOrderClassV1.CompletionBeforeVisibilityFence, MemoryAtomicityClassV1.None,
        MemoryCoherenceAssumptionV1.ExplicitFenceOnly,
        MemoryVisibilityClassV1.ConsumerVisibleAfterFence,
        MemoryPublicationModeV1.StagedWithheldUntilVisible).Validate();

    private static MemorySemanticsV1 ReadOnly(MemoryOrderClassV1 order, MemoryVisibilityClassV1 visibility) =>
        new MemorySemanticsV1(1, MemoryOwnershipClassV1.SharedReadOnly, MemoryAccessClassV1.ReadOnlyInput, order,
            MemoryAtomicityClassV1.None, MemoryCoherenceAssumptionV1.None, visibility,
            MemoryPublicationModeV1.StagedWithheldUntilVisible).Validate();
}
