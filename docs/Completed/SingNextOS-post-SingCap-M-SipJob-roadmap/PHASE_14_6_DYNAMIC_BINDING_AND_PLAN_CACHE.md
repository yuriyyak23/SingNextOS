# P14-6 — Dynamic Binding, Precompiled Thunk Selection, and Non-Authoritative Caches

## 1. Goal

Allow runtime selection among precompiled qualified stages and cache expensive static/binding work without caching authorization or creating an ambient service locator.

Dynamic binding means **selection from a closed admitted catalog**, not dynamic code generation.

## 2. Two cache classes

The implementation SHOULD separate two conceptual caches.

### 2.1 Verification cache

Caches static facts derived from immutable plan/manifests:

```text
PlanDigest -> verified descriptor offsets
contract/schema compatibility
required gate set
thunk IDs/digests
static graph/lifetime/barrier analysis
```

It contains no live subject/session/capability authorization.

### 2.2 Live binding cache

Caches where/how to find current runtime objects:

```text
opaque process/service/session references
TCB-private thunk binding
lookup handles for authority owners
provider-neutral contract binding where applicable
```

A live binding is a performance hint. Every run performs owner-required revalidation.

## 3. Full binding identity

The cache key must prevent ABA/restart aliasing.

It MUST bind, directly or through an existing non-reusable opaque handle, every relevant identity:

```text
RuntimeRealm / AuthorityRealm
RuntimeIncarnation
ProcessHandle + generation/incarnation
ServiceHandle + generation/incarnation
SessionHandle + generation
Capability authority reference/lineage identity
Capability resource generation/revocation epoch where exposed by existing model
Seal identity + generation
ContractId + digest
Request/response schema digests
ThunkId + digest
AdmissionPolicyDigest
Generator/toolchain tuple where required for compatibility
PlanDigest
Provider contract/generation only for provider-dependent stages
```

A numeric ID alone is not sufficient if it can be reused after restart.

## 4. No cached authorization

Forbidden cache fields/semantics:

```text
Authorized = true
CapabilityStillValid = true
SessionStillActive = true
RegionOwner = X as execution permission
SealStillLive = true
ProviderAllowed = true
```

Allowed examples:

```text
CapabilityOwnerLookupRef
SessionRegistryLookupRef
RegionHandle for live validation
ThunkCatalogIndex
ManifestOffset
ServiceBindingKey
```

## 5. Dynamic stage selection

A dynamic plan may select a stage only when:

1. the operation exists in the closed thunk catalog;
2. contract/schema/thunk digests exactly match;
3. required gates are enabled for the exact contour;
4. the selected binding belongs to the expected runtime/authority realm;
5. live execution revalidates all owners.

Caller-supplied delegate, reflection type name, assembly path, service locator key, or arbitrary method token is not a valid selection mechanism.

## 6. Restart/ABA handling

Required scenarios:

### Runtime recreation

Old Job handle/cache entry must not execute against a recreated runtime with reused local IDs.

### Service restart

Same logical service name with a new incarnation requires rebinding and live session/capability checks.

### Session ID reuse

New generation must not be accepted by a cache entry bound to an older generation.

### Capability rederive/revoke

A new lineage/reference does not inherit cached validation from the old capability.

### Seal recreation

Same logical object key with new generation invalidates the old binding.

### Plan/thunk/schema change

Any digest change creates a different verification/binding identity.

### Provider restart

Only provider-dependent entries include provider generation/contract identity. Provider evidence never grants local authority.

## 7. Stale cache behavior

On stale/missing live state:

```text
cache miss / stale detection
    -> attempt normal trusted rebind
    -> perform live validation
    -> execute only if current owners admit
```

If safe rebinding is impossible, route to ordinary SIP or reject. Never "refresh" a stale generation by guessing the current value.

## 8. Job handle semantics

A reusable `SipJobHandle` identifies:

```text
verified plan identity
optional cache key/lookup accelerator
```

It does not contain permission. The following must still fail after handle creation:

- revoked capability;
- closed session;
- restarted service;
- reclaimed Region;
- closed seal;
- disabled gate;
- incompatible runtime/toolchain/provider tuple.

## 9. Cache lifetime and memory safety

TCB-private cache entries may be reference-bearing internally, but:

- no entry is exposed to ManagedCap code;
- eviction/restart releases TCB-private references;
- weak/strong rooting strategy is documented to avoid pinning retired service incarnations forever;
- cache eviction cannot trigger user/provider callbacks under authority locks;
- cached service implementation references, if required internally, are scoped to exact incarnation and never used as authority evidence.

## 10. Plan replay

A serialized/stored plan may be replayed only as static metadata.

Replay MUST rebind current runtime identities and live authority. A plan generated in realm/incarnation A must not carry executable authority into realm/incarnation B.

If plan format or policy tuple is incompatible, replay fails closed.

## 11. Executable proof set

### ABA suite

Create resource/service/session/capability/seal identities, cache binding, tear them down, recreate semantically similar objects with reused numeric/local IDs where possible, then execute old handle.

Expected: stale deny/rebind; never accidental execution.

### Cache vs revoke

Warm cache, revoke capability, run repeatedly. Every run denied despite cache hit.

### Cache vs restart

Warm cache, restart service/runtime, run old handle. Old implementation reference must not be entered.

### Digest mutation

Change one contract/schema/thunk/policy/plan semantic field. Cache entry must not match.

### No dynamic codegen

Instrument/scan runtime path for prohibited dynamic IL/reflection dispatch. Unknown selection must reject, not generate code.

## 12. Performance requirements

Measure separately:

```text
cold verification
warm verification-cache hit
cold live binding
warm live-binding hit
live authority revalidation cost
stale-entry rebind cost
```

A warm cache speedup must not be attributed to eliminated security checks if live admission still occurs.

## 13. PR decomposition

1. verification-cache identity and canonical keys;
2. live-binding cache with gate OFF;
3. restart/ABA invalidation tests;
4. dynamic precompiled thunk selection;
5. replay/stale-handle tests;
6. performance counters and qualification.

## 14. Exit criteria

- cache cannot grant authority;
- complete realm/incarnation identity is represented;
- ABA/restart/replay tests pass;
- dynamic composition selects only precompiled admitted thunks;
- no service-locator/dynamic-IL path exists;
- fallback remains ordinary SIP or rejection.
