using System.Collections.Immutable;
using System.Reflection;
using SingPlus.Runtime;

namespace SingPlus.Tests.SipJobs;

public sealed class Phase146CacheIdentityTests
{
    [Fact]
    public void VerificationAndLiveBindingCachesAreSeparatedAndNonAuthoritative()
    {
        var verification = new SipJobVerificationCache();
        var verificationKey = new SipJobVerificationCacheKey("plan", "policy", "toolchain");
        verification.Put(verificationKey, new(["a", "b"], ["thunk:a", "thunk:b"], ["schema:a", "schema:b"]));
        Assert.True(verification.TryGet(verificationKey, out _));

        var binding = new SipJobLiveBindingCache();
        var key = Key();
        binding.Put(key, new(3, "owner-route:session", true));
        Assert.True(binding.TryGet(key, out var entry));
        Assert.True(entry.RequiresLiveRevalidation);
        Assert.Throws<ArgumentException>(() => binding.Put(key, entry with { RequiresLiveRevalidation = false }));
        Assert.False(SipJobFeatureGates.IsEnabled("FG-PLAN-CACHE"));
        Assert.False(SipJobFeatureGates.IsEnabled("FG-DYNAMIC-PLAN-BIND"));

        var forbiddenNames = new[] { "Authorized", "Allowed", "StillValid", "Implementation", "Delegate", "ServiceProvider" };
        var properties = typeof(SipJobLiveBindingCacheEntry).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(static property => property.Name != "EqualityContract").ToArray();
        Assert.DoesNotContain(properties, property => forbiddenNames.Any(name => property.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
        var forbiddenTypes = new[] { typeof(object), typeof(Type), typeof(Delegate), typeof(IServiceProvider), typeof(MethodInfo) };
        Assert.DoesNotContain(properties, property => forbiddenTypes.Contains(property.PropertyType));
        Assert.DoesNotContain(typeof(SipJobLiveBindingCacheKey).GetProperties().Where(static property => property.Name != "EqualityContract"),
            property => forbiddenTypes.Contains(property.PropertyType));
    }

    [Fact]
    public void VerificationCacheRejectsIncompleteOrNonCanonicalStaticFacts()
    {
        var cache = new SipJobVerificationCache();
        var key = new SipJobVerificationCacheKey("plan", "policy", "toolchain");

        Assert.Throws<ArgumentException>(() => cache.Put(key, new(default, ["thunk:a"], ["schema:a"])));
        Assert.Throws<ArgumentException>(() => cache.Put(key, new(["a", "b"], ["thunk:a"], ["schema:a", "schema:b"])));
        Assert.Throws<ArgumentException>(() => cache.Put(key, new(["a", "a"], ["thunk:a", "thunk:b"], ["schema:a", "schema:b"])));
        Assert.Throws<ArgumentException>(() => cache.Put(key, new(["a"], [" thunk:a"], ["schema:a"])));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void EveryAbaDigestReplayAndProviderMutationMissesExactBindingKey()
    {
        var cache = new SipJobLiveBindingCache();
        var original = Key();
        cache.Put(original, new(1, "owner-route:exact", true));

        foreach (var changed in Mutations(original))
            Assert.False(cache.TryGet(changed, out _));
        Assert.True(cache.TryGet(original, out _));
    }

    [Fact]
    public void DefaultOwnerIdentitiesAndGenerationsCannotEnterLiveBindingCache()
    {
        var cache = new SipJobLiveBindingCache();
        var entry = new SipJobLiveBindingCacheEntry(1, "owner-route:exact", true);

        foreach (var malformed in ZeroIdentityMutations(Key()))
            Assert.Throws<ArgumentException>(() => cache.Put(malformed, entry));

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void EvictionAndRestartClearOnlyLookupMetadata()
    {
        var cache = new SipJobLiveBindingCache();
        var first = Key();
        var second = first with { SessionGeneration = 8 };
        cache.Put(first, new(1, "route:one", true));
        cache.Put(second, new(2, "route:two", true));
        Assert.True(cache.Remove(first));
        Assert.False(cache.TryGet(first, out _));
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }

    private static SipJobLiveBindingCacheKey Key() => new(
        "realm", 1, 10, 2, 20, 3, 30, 4, "cap-lineage", 5, "seal", 6,
        "contract", "contract-digest", "request-digest", "response-digest", "thunk", "thunk-digest",
        "policy", "toolchain", "plan", "provider-neutral", 7);

    private static IEnumerable<SipJobLiveBindingCacheKey> Mutations(SipJobLiveBindingCacheKey key)
    {
        yield return key with { RuntimeRealmId = "realm-2" };
        yield return key with { RuntimeIncarnation = 2 };
        yield return key with { ProcessId = 11 };
        yield return key with { ProcessGeneration = 3 };
        yield return key with { ServiceId = 21 };
        yield return key with { ServiceGeneration = 4 };
        yield return key with { SessionId = 31 };
        yield return key with { SessionGeneration = 5 };
        yield return key with { CapabilityLineageId = "cap-new" };
        yield return key with { CapabilityGeneration = 6 };
        yield return key with { SealId = "seal-new" };
        yield return key with { SealGeneration = 7 };
        yield return key with { ContractId = "contract-new" };
        yield return key with { ContractDigest = "contract-mutated" };
        yield return key with { RequestSchemaDigest = "request-mutated" };
        yield return key with { ResponseSchemaDigest = "response-mutated" };
        yield return key with { ThunkId = "thunk-new" };
        yield return key with { ThunkDigest = "thunk-mutated" };
        yield return key with { AdmissionPolicyDigest = "policy-mutated" };
        yield return key with { GeneratorToolchainDigest = "toolchain-mutated" };
        yield return key with { PlanDigest = "plan-mutated" };
        yield return key with { ProviderIdentity = "provider-restarted" };
        yield return key with { ProviderGeneration = 8 };
    }

    private static IEnumerable<SipJobLiveBindingCacheKey> ZeroIdentityMutations(SipJobLiveBindingCacheKey key)
    {
        yield return key with { RuntimeIncarnation = 0 };
        yield return key with { ProcessId = 0 };
        yield return key with { ProcessGeneration = 0 };
        yield return key with { ServiceId = 0 };
        yield return key with { ServiceGeneration = 0 };
        yield return key with { SessionId = 0 };
        yield return key with { SessionGeneration = 0 };
        yield return key with { CapabilityGeneration = 0 };
        yield return key with { SealGeneration = 0 };
        yield return key with { ProviderGeneration = 0 };
    }
}
