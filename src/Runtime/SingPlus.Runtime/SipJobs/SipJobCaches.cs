using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace SingPlus.Runtime;

internal readonly record struct SipJobVerificationCacheKey(
    string PlanDigest,
    string AdmissionPolicyDigest,
    string GeneratorToolchainDigest);

internal sealed record SipJobVerificationCacheEntry(
    ImmutableArray<string> OrderedStageIds,
    ImmutableArray<string> ThunkIds,
    ImmutableArray<string> SchemaDigests);

internal readonly record struct SipJobLiveBindingCacheKey(
    string RuntimeRealmId,
    ulong RuntimeIncarnation,
    ulong ProcessId,
    ulong ProcessGeneration,
    ulong ServiceId,
    ulong ServiceGeneration,
    ulong SessionId,
    ulong SessionGeneration,
    string CapabilityLineageId,
    ulong CapabilityGeneration,
    string SealId,
    ulong SealGeneration,
    string ContractId,
    string ContractDigest,
    string RequestSchemaDigest,
    string ResponseSchemaDigest,
    string ThunkId,
    string ThunkDigest,
    string AdmissionPolicyDigest,
    string GeneratorToolchainDigest,
    string PlanDigest,
    string ProviderIdentity,
    ulong ProviderGeneration);

internal sealed record SipJobLiveBindingCacheEntry(
    int ThunkCatalogIndex,
    string OwnerLookupRouteId,
    bool RequiresLiveRevalidation);

internal sealed class SipJobVerificationCache
{
    private readonly ConcurrentDictionary<SipJobVerificationCacheKey, SipJobVerificationCacheEntry> _entries = new();
    internal bool TryGet(SipJobVerificationCacheKey key, out SipJobVerificationCacheEntry entry) => _entries.TryGetValue(key, out entry!);
    internal void Put(SipJobVerificationCacheKey key, SipJobVerificationCacheEntry entry)
    {
        Validate(key.PlanDigest, key.AdmissionPolicyDigest, key.GeneratorToolchainDigest);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.OrderedStageIds.IsDefaultOrEmpty || entry.ThunkIds.IsDefaultOrEmpty || entry.SchemaDigests.IsDefaultOrEmpty ||
            entry.OrderedStageIds.Length != entry.ThunkIds.Length || entry.OrderedStageIds.Length != entry.SchemaDigests.Length ||
            entry.OrderedStageIds.Distinct(StringComparer.Ordinal).Count() != entry.OrderedStageIds.Length)
            throw new ArgumentException("Verified cache metadata must contain one finite thunk and schema digest per unique stage.");
        Validate([.. entry.OrderedStageIds, .. entry.ThunkIds, .. entry.SchemaDigests]);
        _entries[key] = entry;
    }
    internal int Count => _entries.Count;
    private static void Validate(params string[] values)
    {
        if (values.Any(static value => string.IsNullOrWhiteSpace(value) || value != value.Trim()))
            throw new ArgumentException("Cache identity values must be canonical.");
    }
}

internal sealed class SipJobLiveBindingCache
{
    private readonly ConcurrentDictionary<SipJobLiveBindingCacheKey, SipJobLiveBindingCacheEntry> _entries = new();
    internal bool TryGet(SipJobLiveBindingCacheKey key, out SipJobLiveBindingCacheEntry entry) => _entries.TryGetValue(key, out entry!);
    internal void Put(SipJobLiveBindingCacheKey key, SipJobLiveBindingCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var identity = new[]
        {
            key.RuntimeRealmId, key.CapabilityLineageId, key.SealId, key.ContractId, key.ContractDigest,
            key.RequestSchemaDigest, key.ResponseSchemaDigest, key.ThunkId, key.ThunkDigest,
            key.AdmissionPolicyDigest, key.GeneratorToolchainDigest, key.PlanDigest, key.ProviderIdentity
        };
        if (identity.Any(static value => string.IsNullOrWhiteSpace(value) || value != value.Trim()))
            throw new ArgumentException("Live binding identity values must be canonical.");
        if (key.RuntimeIncarnation == 0 || key.ProcessId == 0 || key.ProcessGeneration == 0 ||
            key.ServiceId == 0 || key.ServiceGeneration == 0 || key.SessionId == 0 ||
            key.SessionGeneration == 0 || key.CapabilityGeneration == 0 || key.SealGeneration == 0 ||
            key.ProviderGeneration == 0)
            throw new ArgumentException("Live binding owner identities and generations must be nonzero.");
        if (!entry.RequiresLiveRevalidation)
            throw new ArgumentException("A live binding cache entry can only be stored when every run revalidates current owners.");
        if (entry.ThunkCatalogIndex < 0 || string.IsNullOrWhiteSpace(entry.OwnerLookupRouteId) || entry.OwnerLookupRouteId != entry.OwnerLookupRouteId.Trim())
            throw new ArgumentException("Live binding metadata must name a finite thunk index and canonical owner route.");
        _entries[key] = entry;
    }
    internal bool Remove(SipJobLiveBindingCacheKey key) => _entries.TryRemove(key, out _);
    internal void Clear() => _entries.Clear();
    internal int Count => _entries.Count;
}
