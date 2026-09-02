# Phase 1 — Platform Contract vNext

## Status

**Highest-priority SingNextOS refactoring.** This phase can be implemented with the host provider first and does not require a working HybridCPU backend.

## Current state

`src/Platform/SingPlus.Platform.Abstractions/PlatformAuthorityContracts.cs` currently exposes a deliberately narrow v1:

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

## Target contract shape

### 1. Replace boolean-ish feature discovery with typed, versioned discovery

Keep current v1 bits for compatibility if useful, but add a vNext query such as:

```text
PlatformFeatureManifest QueryFeatures()
```

A feature record should identify:

- semantic feature family;
- contract version;
- availability class;
- optional profile metadata that is safe to expose to the kernel.

Suggested availability classes:

```text
Unavailable
ModelOnly
ProjectionOnly
RuntimeAdmission
Executable
ProductionSecure
```

Do not assume these form a simple numeric privilege ladder for every feature. They describe evidence level/readiness, not grants to a caller.

Feature families should remain semantic, for example:

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

### 2. Introduce a common opaque operation/completion model

Add bridge-private identities and a neutral completion receipt usable by mapping, DMA, compute and virtualization:

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

Avoid growing one giant `IPlatformAuthorityProvider` method list. Prefer a root provider plus optional interfaces, for example:

```text
IPlatformFeatureProvider
IPlatformDomainProvider
IPlatformMemoryProvider
IPlatformIoProvider
IPlatformComputeProvider
IPlatformVirtualizationProvider
IPlatformEvidenceProvider
IPlatformSecureComputeProvider
```

The root provider owns identity/version and exposes supported extension interfaces. The bridge remains the only component that stores their opaque leases.

This split is **not** a universal HAL: every interface exists only when a concrete cross-repository use case requires it.

### 4. Make memory visibility explicit in contract vocabulary

Add semantic input/output types instead of a global `FlushCaches` operation:

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

A provider result may prove external state, but only Sing kernel policy can mint/delegate a local capability.

## Likely SingNextOS code touched

- `src/Platform/SingPlus.Platform.Abstractions/PlatformAuthorityContracts.cs`;
- new small files under `src/Platform/SingPlus.Platform.Abstractions/` for feature/completion/memory-visibility types;
- `src/Platform/SingPlus.Platform.Host/HostPlatformAuthorityProvider.cs`;
- `src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.cs`;
- `tests/SingPlus.Tests/Platform/PlatformAuthorityBridgeTests.cs` and new vNext contract tests.

Keep public contracts small enough that host tests can exhaustively fault-inject every state.

## Tests

At minimum:

- unsupported feature is distinguishable from denied;
- `ProjectionOnly` virtualization cannot be used as `Executable`;
- `ModelOnly` SecureCompute cannot satisfy a production-secure request;
- provider lease IDs never escape bridge-visible snapshots;
- malformed completion identity is rejected and provider state is closed/cancelled;
- completion for stale generation cannot authorize local commit;
- feature presence never bypasses local capability validation.

## Acceptance criteria

Phase 1 is done when the host provider can model all lifecycle/completion states needed by later phases, while current v1 domain/mapping behavior remains compatible and no raw HybridCPU type appears in SingNextOS public/SIP/kernel contracts.

## Do not do

- no giant HAL;
- no raw `DomainRuntimeContext` or descriptor types in SingNextOS contracts;
- no VMCS fields;
- no physical addresses;
- no guarantee of global coherence;
- no feature bit that is treated as per-request authority.
