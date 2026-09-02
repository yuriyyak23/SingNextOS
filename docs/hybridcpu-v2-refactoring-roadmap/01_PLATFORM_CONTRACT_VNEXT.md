# Phase 1 — Platform Contract vNext

## Status

**Complete at the host-backed contract level.** Typed/versioned feature discovery, the minimal operation/completion model, and explicit semantic memory-visibility vocabulary are implemented. The phase does **not** claim real HybridCPU execution, DMA, cache-topology integration, or Phase-2 reclaim orchestration.

The next roadmap phase is Phase 2: explicit local-revoked / platform-draining / platform-closed lifecycle and reclaim proof.

## Current state

The legacy v1 authority operations remain deliberately narrow:

```text
Features:
  NeutralDomainBinding
  DirectOwnedRegionMapping

Operations:
  BindDomain
  RevokeDomain
  MapOwnedRegion
  RevokeRegionMapping
```

`PlatformAuthorityBridge` still owns local/provider identity separation and current mapping `Active -> Draining -> Revoked` behavior. Phase 1 extends the platform seam with small optional semantic contracts rather than replacing it with a universal HAL.

## Completed slice — typed/versioned semantic discovery

`IPlatformFeatureProvider` exposes an immutable `PlatformFeatureManifest` with:

```text
semantic feature family
contract version
availability/evidence class
```

Availability classes remain exact evidence classes, not a numeric privilege ladder:

```text
Unavailable
ModelOnly
ProjectionOnly
RuntimeAdmission
Executable
ProductionSecure
```

For example, `ProjectionOnly` cannot satisfy `Executable`, and `ModelOnly` cannot satisfy `ProductionSecure`.

The host-backed legacy domain/mapping features remain `RuntimeAdmission` contract version 1. `ExplicitMemoryVisibility` is now advertised by the host provider as contract version 1 `ModelOnly`: the host model can exercise the semantic contract, but this is not a claim of executable HybridCPU cache/fence behavior.

`RuntimeKernel.QueryPlatformFeatures()` remains discovery/evidence only. Feature presence never bypasses Sing capability/ownership validation.

## Completed slice — minimal operation/completion contract

Provider-side operation identities are separate from Sing and provider lease IDs:

```text
PlatformOperationId
PlatformOperationGeneration
PlatformOperationIdentity
```

`PlatformOperationIdentity` is bound to an already-live `PlatformProviderDomainLease`. It is not a capability and cannot authorize local or hardware-visible effects by itself.

Common completion states are:

```text
Staged
Pending
Draining
Completed
Cancelled
Closed
Faulted
```

Conservative lifecycle semantics remain:

- `Staged`, `Pending`, `Draining`, `Completed`, and `Cancelled` are non-terminal for generic external-authority closure;
- `Closed` and `Faulted` are terminal observations;
- only `Closed` has `ProvesClosure = true`;
- `Faulted` is terminal observation but never reclaim proof.

`IPlatformCompletionProvider` is optional and separate from `IPlatformAuthorityProvider`. The deterministic host operation ledger validates operation/receipt identity and freshness and remains model-only evidence. It is not wired into process exit, mapping reclaim, publication, or HybridCPU execution.

## Completed slice — explicit memory visibility

Phase 1 now has semantic vocabulary instead of a global cache-management primitive:

```text
PlatformMemoryConsumerClass
PlatformMemoryVisibilityRequirement
PlatformMemoryVisibilityOutcome
PlatformMemoryVisibilityRequest
PlatformMemoryVisibilityResult
IPlatformMemoryVisibilityProvider
```

Current semantic consumer classes are:

```text
CpuExecution
ExternalExecutionDomain
IoDevice
Accelerator
```

Current requirements are:

```text
CoherentAccess
PublicationFence
CacheMaintenance
```

Current outcomes are:

```text
Coherent
PublicationFenceSatisfied
CacheMaintenanceSatisfied
Unsupported
```

Requirement satisfaction is exact and fail-closed:

```text
CoherentAccess      -> Coherent
PublicationFence   -> PublicationFenceSatisfied
CacheMaintenance   -> CacheMaintenanceSatisfied
anything else      -> not satisfied
```

There is no numeric ordering where one visibility outcome silently substitutes for another.

### Operation-scoped semantics

`IPlatformMemoryVisibilityProvider.EnsureMemoryVisibility(...)` is scoped to an existing `PlatformOperationIdentity`. This deliberately prevents a global `FlushCaches`-style authority surface.

Before producing visibility evidence, the host provider revalidates the operation and therefore preserves existing fail-closed behavior for:

- malformed/zero operation identity;
- stale operation generation;
- stale provider-domain generation;
- wrong provider-domain lease or subject;
- revoked provider domain;
- terminal `Closed` or `Faulted` operations.

Undefined consumer/requirement enum values fail as `Faulted` contract input. A defined but unsupported consumer/requirement pair returns the explicit semantic outcome `Unsupported`; it is not confused with stale, denied, revoked, or malformed identity.

### Host model

The deterministic host model exercises all required outcome classes without claiming hardware behavior:

```text
CpuExecution + CoherentAccess
  -> Coherent

ExternalExecutionDomain + PublicationFence
  -> PublicationFenceSatisfied

IoDevice + CacheMaintenance
  -> CacheMaintenanceSatisfied

other defined pairs
  -> Unsupported
```

The visibility call does not advance completion state. Visibility evidence therefore cannot silently turn `Staged`, `Completed`, or another state into `Closed` and cannot authorize reclaim.

The result contains semantic consumer/requirement/outcome only. It contains no Sing capability, local domain ID, region handle, provider mapping ID, physical address, cache-line identity, lane ID, opcode, VMCS state, or HybridCPU descriptor.

## Optional provider families

Phase 1 now proves the split pattern with three independent optional contracts:

```text
IPlatformFeatureProvider
IPlatformCompletionProvider
IPlatformMemoryVisibilityProvider
```

Do not pre-create broad provider families. Add future `IPlatformDomainProvider`, `IPlatformIoProvider`, `IPlatformComputeProvider`, virtualization/evidence/secure interfaces only when a concrete cross-repository operation requires them.

This is not a universal HAL.

## Identity and authority invariants

Every vNext contract preserves:

```text
Sing DomainId                  != provider domain lease
Sing CapabilityId              != HybridCPU CapabilityGrant/token
RegionHandle                   != provider/IOMMU mapping identity
BorrowLeaseId                  != accelerator/device token
PlatformOperationId            != provider domain/mapping lease IDs
provider completion receipt    != process-visible capability
memory visibility result       != authority or reclaim proof
```

Feature manifests, completion receipts, and memory-visibility results are evidence. Only Sing kernel policy can mint/delegate local authority or advance ownership/reclaim state.

## Code touched by Phase 1

Typed feature discovery:

- `src/Platform/SingPlus.Platform.Abstractions/PlatformFeatureContracts.cs`;
- `src/Platform/SingPlus.Platform.Host/HostPlatformAuthorityProvider.cs`;
- `src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.cs`;
- `src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.Platform.cs`;
- `tests/SingPlus.Tests/Platform/PlatformFeatureDiscoveryTests.cs`.

Minimal completion contract:

- `src/Platform/SingPlus.Platform.Abstractions/PlatformCompletionContracts.cs`;
- `src/Platform/SingPlus.Platform.Host/HostPlatformAuthorityProvider.cs`;
- `tests/SingPlus.Tests/Platform/PlatformCompletionContractTests.cs`.

Explicit memory visibility:

- `src/Platform/SingPlus.Platform.Abstractions/PlatformMemoryVisibilityContracts.cs`;
- `src/Platform/SingPlus.Platform.Host/HostPlatformAuthorityProvider.cs`;
- `tests/SingPlus.Tests/Platform/PlatformMemoryVisibilityTests.cs`.

## Tests

Phase-1 coverage now includes:

- typed/versioned semantic feature discovery;
- exact availability/evidence classes;
- malformed feature manifest rejection;
- legacy-v1 discovery compatibility without authority calls;
- feature presence cannot bypass local capability validation;
- distinct operation ID/generation spaces;
- deterministic completion-state observation;
- stale/wrong-domain/revoked completion identity rejection;
- `Completed`/`Cancelled` are not closure proof;
- `Faulted` is not reclaim proof;
- host memory visibility is advertised only as `ModelOnly`;
- coherent, publication-fence, cache-maintenance, and unsupported outcomes are distinct;
- malformed visibility consumer/requirement fails closed;
- stale/wrong-domain/revoked operation cannot produce visibility evidence;
- terminal operation cannot produce new visibility evidence;
- visibility evidence does not advance completion lifecycle;
- visibility result carries no local/mapping authority;
- no `FlushCaches`/invalidate primitive appears in the visibility provider contract.

Phase 2 must add bridge-integrated tests proving that exact local capability, binding, region ownership, and provider completion/closure generations all remain current before local reclaim or authority advancement.

## Acceptance criteria

Phase 1 is complete when the host-backed contract can model the semantic discovery, completion lifecycle evidence, and explicit memory-visibility outcomes needed by later phases while preserving v1 domain/mapping compatibility and exposing no raw HybridCPU/hardware authority ABI.

That criterion is now met at the local/host-backed contract level.

This does **not** mean the platform is hardware-backed. Real HybridCPU binding, non-coherent region mapping, bounded DMA, and completion-driven reclaim remain later phases.

## Next phase

Phase 2 should consume these contracts to make teardown/reclaim states explicit:

```text
local authority revoked
-> platform draining
-> platform closed
-> exact local generations revalidated
-> reclaim allowed
```

Do not skip from local revoke directly to reclaim based on `Completed`, `Cancelled`, `Faulted`, feature presence, or a visibility result.

## Do not do

- no giant HAL;
- no raw `DomainRuntimeContext` or HybridCPU descriptor types in SingNextOS contracts;
- no VMCS fields;
- no physical addresses;
- no cache topology or cache-line IDs;
- no global `FlushCaches`/invalidate authority primitive;
- no guarantee of global coherence;
- no feature discovery result treated as per-request authority;
- no completion receipt treated as local capability;
- no memory-visibility result treated as capability, ownership, or reclaim proof;
- no reclaim based on `Completed`, `Cancelled`, `Faulted`, or visibility evidence alone;
- no DMA or HybridCPU binding in Phase 1.
