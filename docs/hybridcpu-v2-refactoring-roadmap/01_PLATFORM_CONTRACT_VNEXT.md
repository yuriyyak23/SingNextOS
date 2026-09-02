# Phase 1 — Platform Contract vNext

## Status

**In progress.** Typed/versioned semantic feature discovery and the minimal host-backed completion-contract slice are implemented. Explicit memory-visibility vocabulary and any further semantic provider-family split remain future Phase-1 iterations. No working HybridCPU backend is required for the current host-backed work.

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

`PlatformAuthorityBridge` already keeps local binding IDs separate from provider lease IDs and tracks mapping `Active -> Draining -> Revoked`.

The audit conclusion is to **extend this seam**, not replace it with a universal HAL.

### Completed slice — typed/versioned semantic discovery

The host provider implements optional `IPlatformFeatureProvider` discovery through an immutable `PlatformFeatureManifest`.

Feature records carry only:

```text
semantic family
contract version
availability/evidence class
```

Current semantic families are named independently of HybridCPU implementation details, including `NeutralDomains`, `OwnedRegionMapping`, `IoDomainBinding`, `DmaMapping`, `ExplicitMemoryVisibility`, compute families, virtualization/nested domains, evidence, secure domains and surface presentation.

Availability classes are:

```text
Unavailable
ModelOnly
ProjectionOnly
RuntimeAdmission
Executable
ProductionSecure
```

These classes are **not a numeric privilege ladder**. A discovery request for one availability class requires that exact evidence class; for example, `ProjectionOnly` does not satisfy `Executable`, and `ModelOnly` does not satisfy `ProductionSecure`.

The current host-backed v1 domain/mapping operations are reported as `RuntimeAdmission` contract version 1. This means the configured provider can admit the semantic operation in the current runtime; it is not a claim of real HybridCPU hardware execution or production security.

Providers that still implement only v1 `PlatformAuthorityFeatures` receive a compatibility projection into the semantic manifest. This preserves existing providers while allowing new providers to implement `IPlatformFeatureProvider` independently of the operational authority interface.

`RuntimeKernel.QueryPlatformFeatures()` exposes only this semantic manifest. It exposes no provider lease IDs, capabilities, physical addresses, VMX/VMCS state, lanes or opcodes. Discovery is evidence only: current capability/ownership validation still runs before any provider authority call.

### Completed slice — minimal completion contract

The platform abstractions now define separate provider-side operation identity spaces:

```text
PlatformOperationId
PlatformOperationGeneration
PlatformOperationIdentity
```

`PlatformOperationIdentity` is bound to an already-live `PlatformProviderDomainLease`; it is not a Sing capability and cannot stage hardware-visible work by itself.

The common completion states are:

```text
Staged
Pending
Draining
Completed
Cancelled
Closed
Faulted
```

The lifecycle distinction is intentionally conservative:

- `Staged`, `Pending`, `Draining`, `Completed` and `Cancelled` are non-terminal for generic external-authority closure;
- `Closed` and `Faulted` are terminal observations;
- only `Closed` has `ProvesClosure = true`;
- `Faulted` is terminal for observation but **never** reclaim proof.

This prevents `Completed` or `Cancelled` from being mistaken for drain-before-reclaim proof before Phase 2 provides explicit teardown orchestration.

`IPlatformCompletionProvider.ObserveCompletion(...)` is an optional provider contract. The host provider contains a deterministic operation ledger used to stage model operations against a live provider domain lease, advance legal lifecycle states, observe receipts and validate receipt identity/current-state freshness.

The host ledger is **CurrentModelBound / ModelOnly evidence**, not an executable HybridCPU effect path. It is not wired into mapping revoke, process exit, reclaim or publication in this slice.

Receipt validation fails closed for:

- zero/malformed operation identities;
- undefined completion state;
- stale operation generation;
- stale provider-domain generation;
- wrong provider domain lease or subject;
- receipt state older than the provider's current operation state;
- operation observation after its provider domain lease is revoked.

No provider operation ID or receipt is exposed through SIP contracts or `RuntimeKernel` public authority APIs.

## Target contract shape

### 1. Replace boolean-ish feature discovery with typed, versioned discovery

**Implemented.** Keep current v1 bits for compatibility, while typed discovery is available as:

```text
IPlatformFeatureProvider.QueryFeatures()
RuntimeKernel.QueryPlatformFeatures()
```

A feature record identifies:

- semantic feature family;
- contract version;
- availability class.

Optional profile metadata remains intentionally deferred until a concrete safe use case requires it.

Feature families remain semantic, for example:

```text
NeutralDomains
OwnedRegionMapping
IoDomainBinding
DmaMapping
ExplicitMemoryVisibility
Dsc1BulkCompute
MatrixTileV1
ScopedAcceleratorV1
VirtualizationDomains
NestedDomains
PlatformEvidence
SecureDomains
SurfacePresentation
```

Discovery must never be treated as per-request authority.

### 2. Introduce a common opaque operation/completion model

**Minimal contract slice implemented; lifecycle integration deferred to Phase 2.** The current contract provides:

```text
PlatformOperationId
PlatformOperationGeneration
PlatformOperationIdentity

PlatformCompletionState:
  Staged
  Pending
  Draining
  Completed
  Cancelled
  Closed
  Faulted

PlatformCompletionReceipt:
  OperationId
  Generation
  provider-domain lease identity/generation
  state
  IsTerminal
  ProvesClosure
```

The receipt answers only what the provider can currently prove about the modelled operation. `Closed` is the only generic closure proof. Publication outcome, memory-visibility/fence outcome and typed operation-specific fault payloads remain deferred until the concrete semantic operation that needs them exists.

The receipt does not expose HybridCPU internal descriptor pointers, lane IDs, IOMMU table IDs or VMX fields.

### 3. Split optional provider capabilities by semantic family

**Started for discovery and completion observation.** `IPlatformFeatureProvider` and `IPlatformCompletionProvider` are optional and separate from the legacy authority interface. Do not grow one giant `IPlatformAuthorityProvider` method list. Add further semantic families only when a concrete cross-repository operation requires them, for example:

```text
IPlatformDomainProvider
IPlatformMemoryProvider
IPlatformIoProvider
IPlatformComputeProvider
IPlatformVirtualizationProvider
IPlatformEvidenceProvider
IPlatformSecureComputeProvider
```

The bridge remains the only component that may eventually store opaque leases/operation identities for kernel use.

This split is **not** a universal HAL: every interface exists only when a concrete cross-repository use case requires it.

### 4. Make memory visibility explicit in contract vocabulary

**Not implemented in these slices.** Add semantic input/output types instead of a global `FlushCaches` operation:

```text
PlatformMemoryConsumerClass
PlatformMemoryVisibilityRequirement
PlatformMemoryVisibilityOutcome
```

Required outcomes need to distinguish at least:

- coherent/no extra action required;
- explicit publication/fence required and satisfied;
- explicit cache maintenance required and satisfied;
- unsupported.

The provider decides how to implement the outcome. SingNextOS does not learn cache topology.

### 5. Preserve strict identity separation

Every vNext contract must preserve:

```text
Sing DomainId                  != provider domain lease
Sing CapabilityId              != HybridCPU CapabilityGrant/token
RegionHandle                   != IOMMU mapping identity
BorrowLeaseId                  != accelerator/device token
PlatformOperationId            != provider domain/mapping lease IDs
provider completion receipt    != process-visible capability
```

A provider feature manifest or completion receipt proves only provider-declared semantic/effect state. Only Sing kernel policy can mint/delegate a local capability or authorize a concrete effect.

## Code touched by completed slices

Feature-discovery slice:

- `src/Platform/SingPlus.Platform.Abstractions/PlatformFeatureContracts.cs`;
- `src/Platform/SingPlus.Platform.Host/HostPlatformAuthorityProvider.cs`;
- `src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.cs`;
- `src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.Platform.cs`;
- `tests/SingPlus.Tests/Platform/PlatformFeatureDiscoveryTests.cs`.

Minimal completion-contract slice:

- new `src/Platform/SingPlus.Platform.Abstractions/PlatformCompletionContracts.cs`;
- `src/Platform/SingPlus.Platform.Host/HostPlatformAuthorityProvider.cs`;
- new `tests/SingPlus.Tests/Platform/PlatformCompletionContractTests.cs`.

## Tests

Feature-discovery coverage includes:

- host provider exposes typed/versioned semantic features;
- an absent feature is `Unavailable`, distinct from an authority operation returning `Denied`;
- `ProjectionOnly` virtualization cannot satisfy an `Executable` query;
- `ModelOnly` SecureDomains cannot satisfy `ProductionSecure`;
- malformed zero-version, explicit-Unavailable and duplicate-family manifests fail closed;
- legacy v1 providers receive semantic discovery without invoking authority operations;
- feature presence never bypasses local capability validation;
- a kernel with no provider reports semantic features unavailable.

Completion-contract coverage includes:

- operation ID/generation are separate provider identity spaces;
- staged operation observation is deterministic;
- `Completed`/`Cancelled` cannot prove external closure;
- `Closed` proves closure;
- `Faulted` is terminal but does not prove closure and cannot transition to `Closed`;
- stale operation generation is rejected;
- wrong-domain operation identity is rejected;
- malformed completion state is rejected as `Faulted` evidence;
- wrong-domain receipt is rejected;
- an old receipt is stale after the provider advances operation state;
- revoked provider-domain lease invalidates later completion observation;
- completion provider contracts do not accept local `CapabilityId` authority.

Later Phase-1/Phase-2 work still requires bridge-integrated tests proving that a provider receipt cannot authorize local commit/reclaim unless the exact local binding/generation/ownership state is also current.

## Acceptance criteria

Phase 1 is **not yet complete**. Typed/versioned discovery and a minimal host-backed completion identity/state model are current. Phase 1 remains open until memory visibility is explicit and any provider-family split required by the first concrete external operation is defined, while current v1 domain/mapping behavior remains compatible and no raw HybridCPU type appears in SingNextOS public/SIP/kernel contracts.

Phase 2 will consume the completion contract to make local-revoked vs platform-draining vs platform-closed explicit. This Phase-1 slice does not claim that integration.

## Do not do

- no giant HAL;
- no raw `DomainRuntimeContext` or descriptor types in SingNextOS contracts;
- no VMCS fields;
- no physical addresses;
- no guarantee of global coherence;
- no feature discovery result or completion receipt treated as per-request authority;
- no reclaim based on `Completed`, `Cancelled` or `Faulted` alone.
