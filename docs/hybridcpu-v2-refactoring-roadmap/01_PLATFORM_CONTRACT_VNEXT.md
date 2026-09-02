# Phase 1 — Platform Contract vNext

## Status

**In progress.** Typed/versioned semantic feature discovery is the first completed Phase-1 slice. Completion receipts, memory-visibility vocabulary and the broader semantic provider-family split remain future Phase-1 iterations. No working HybridCPU backend is required for the current host-backed work.

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

The host provider now implements optional `IPlatformFeatureProvider` discovery through an immutable `PlatformFeatureManifest`.

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

## Target contract shape

### 1. Replace boolean-ish feature discovery with typed, versioned discovery

**First slice implemented.** Keep current v1 bits for compatibility, while typed discovery is available as:

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

**Not implemented in this slice.** Add bridge-private identities and a neutral completion receipt usable by mapping, DMA, compute and virtualization:

```text
PlatformOperationId
PlatformOperationGeneration

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
  Subject/domain binding generation
  terminal state
  publication/visibility outcome
  fence/maintenance outcome
  typed status/fault
```

The receipt must answer a security question: **is external authority/effect definitely closed or published enough for local state to advance?**

It must not expose HybridCPU internal descriptor pointers, lane IDs, IOMMU table IDs or VMX fields.

### 3. Split optional provider capabilities by semantic family

**Started only for discovery.** `IPlatformFeatureProvider` is now optional and separate from the legacy authority interface. Do not grow one giant `IPlatformAuthorityProvider` method list. Add further semantic families only when a concrete cross-repository operation requires them, for example:

```text
IPlatformDomainProvider
IPlatformMemoryProvider
IPlatformIoProvider
IPlatformComputeProvider
IPlatformVirtualizationProvider
IPlatformEvidenceProvider
IPlatformSecureComputeProvider
```

The bridge remains the only component that stores opaque leases.

This split is **not** a universal HAL: every interface exists only when a concrete cross-repository use case requires it.

### 4. Make memory visibility explicit in contract vocabulary

**Not implemented in this slice.** Add semantic input/output types instead of a global `FlushCaches` operation:

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
provider completion receipt    != process-visible capability
```

A provider feature manifest proves only provider-declared semantic availability. Only Sing kernel policy can mint/delegate a local capability or authorize a concrete effect.

## Code touched by the first slice

- new `src/Platform/SingPlus.Platform.Abstractions/PlatformFeatureContracts.cs`;
- `src/Platform/SingPlus.Platform.Host/HostPlatformAuthorityProvider.cs`;
- `src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.cs`;
- `src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.Platform.cs`;
- new `tests/SingPlus.Tests/Platform/PlatformFeatureDiscoveryTests.cs`.

## Tests

First-slice coverage includes:

- host provider exposes typed/versioned semantic features;
- an absent feature is `Unavailable`, distinct from an authority operation returning `Denied`;
- `ProjectionOnly` virtualization cannot satisfy an `Executable` query;
- `ModelOnly` SecureDomains cannot satisfy `ProductionSecure`;
- malformed zero-version, explicit-Unavailable and duplicate-family manifests fail closed;
- legacy v1 providers receive semantic discovery without invoking authority operations;
- feature presence never bypasses local capability validation;
- a kernel with no provider reports semantic features unavailable.

Later Phase-1 tests still required with completion work:

- provider lease IDs never escape bridge-visible snapshots;
- malformed completion identity is rejected and provider state is closed/cancelled;
- completion for stale generation cannot authorize local commit.

## Acceptance criteria

Phase 1 is **not yet complete**. The typed/versioned discovery requirement is satisfied for host-backed/legacy-compatible providers, but Phase 1 remains open until the host provider can model the lifecycle/completion states needed by later phases and memory visibility is explicit, while current v1 domain/mapping behavior remains compatible and no raw HybridCPU type appears in SingNextOS public/SIP/kernel contracts.

## Do not do

- no giant HAL;
- no raw `DomainRuntimeContext` or descriptor types in SingNextOS contracts;
- no VMCS fields;
- no physical addresses;
- no guarantee of global coherence;
- no feature discovery result that is treated as per-request authority.
