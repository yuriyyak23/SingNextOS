using SingPlus.Contracts;

namespace SingPlus.Tests.Contracts;

public sealed class DmaExecutionBindingV1Tests
{
    [Fact]
    public void ExactBindingRoundTripsAndContainsNoAuthority()
    {
        var binding = Binding();
        var parsed = DmaExecutionBindingV1.ParseCanonical(binding.SerializeCanonical());

        Assert.Equal(binding, parsed);
        Assert.Equal(DmaExecutionBindingV1.ExtensionClassId,
            binding.ToClause(SemanticExtensionRequirement.Mandatory).ClassId);
        Assert.False(binding.AuthorizesDma);
        Assert.False(binding.AuthorizesExecution);
        Assert.False(binding.AuthorizesEffect);
    }

    [Fact]
    public void DriftInEveryMutableOwnerGenerationFailsExactRevalidation()
    {
        var admitted = Binding();
        DmaExecutionBindingV1[] stale =
        [
            admitted with { RegionGeneration = 2 }, admitted with { MutationGeneration = 3 },
            admitted with { ProcessIncarnation = 4 }, admitted with { AddressSpaceGeneration = 5 },
            admitted with { TranslationGeneration = 6 }, admitted with { DeviceLeaseGeneration = 7 },
            admitted with { ProviderGeneration = 8 }, admitted with { SessionGeneration = 9 },
            admitted with { ExternalOperationGeneration = 10 },
            admitted with { RegionUseDigest = new string('c', 64) },
            admitted with { SecurityDomainDigest = new string('d', 64) },
        ];

        Assert.True(admitted.MatchesCurrent(admitted));
        Assert.All(stale, current => Assert.False(admitted.MatchesCurrent(current)));
    }

    [Fact]
    public void ZeroGenerationUnknownStateAndMalformedDigestFailClosed()
    {
        Assert.Throws<ArgumentException>(() => (Binding() with { TranslationGeneration = 0 }).Validate());
        Assert.Throws<NotSupportedException>(() => (Binding() with { EffectState = (DmaEffectStateV1)0 }).Validate());
        Assert.Throws<ArgumentException>(() => (Binding() with { RegionUseDigest = "region-handle" }).Validate());
        Assert.Throws<FormatException>(() => DmaExecutionBindingV1.ParseCanonical(new byte[10]));
    }

    [Fact]
    public void EffectPossibleCannotBeReinterpretedAsClosedOrAdmitted()
    {
        var possible = Binding() with { EffectState = DmaEffectStateV1.EffectPossible };
        Assert.False(possible.MatchesCurrent(possible with { EffectState = DmaEffectStateV1.Admitted }));
        Assert.False(possible.MatchesCurrent(possible with { EffectState = DmaEffectStateV1.Closed }));
        Assert.False(possible.MatchesCurrent(possible with { EffectState = DmaEffectStateV1.Quarantined }));
    }

    [Fact]
    public void DmaBindingPublicSurfaceContainsNoMachinePermissionOrTopologyIdentifiers()
    {
        string[] forbidden = ["Capability", "RegionHandle", "Pasid", "Iova", "PhysicalAddress", "Topology", "Permission"];
        var names = typeof(DmaExecutionBindingV1).GetProperties().Select(property =>
            property.Name + ":" + property.PropertyType.Name).ToArray();
        Assert.All(forbidden, fragment => Assert.DoesNotContain(names,
            name => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    private static DmaExecutionBindingV1 Binding() => new(1, new string('a', 64),
        RegionGeneration: 1, MutationGeneration: 2, ProcessIncarnation: 3,
        AddressSpaceGeneration: 4, TranslationGeneration: 5, DeviceLeaseGeneration: 6,
        ProviderGeneration: 7, SessionGeneration: 8, ExternalOperationGeneration: 9,
        DmaEffectStateV1.Admitted, new string('b', 64));
}
