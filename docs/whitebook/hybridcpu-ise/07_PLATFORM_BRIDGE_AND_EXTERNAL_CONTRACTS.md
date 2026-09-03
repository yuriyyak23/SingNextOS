# 07. Platform Bridge And External Contracts

> Status note: this chapter's “current” inventory is the historical
> `af791aba...` Phase-1 snapshot named below. The implementation-ordered roadmap
> is authoritative for later delivery status. Phase 3 added the real neutral
> HybridCPU provider, Phases 4–5 added bounded memory/I/O lifecycles, and Phase 6
> versions the SingPlus `NeutralDomains` contract at v2 with an exact
> `(DomainId, ProcessHandle)` subject. The current scheduler slice adds
> `ExecutionPolicy` v1 as a binding-scoped host `ModelOnly` contract while the
> HybridCPU provider reports that family unavailable. This proves no HybridCPU
> placement, budget enforcement, scheduler quality, hard real-time, boot or
> security property beyond its tested evidence.

## Current status at SingNextOS `af791aba...`

Platform Authority Bridge больше не является только proposed abstraction. В текущем SingNextOS реализован **local/host-backed v1 contour**:

```text
src/Platform/SingPlus.Platform.Abstractions/PlatformAuthorityContracts.cs
src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.cs
src/Runtime/SingPlus.Runtime/Platform/RuntimeKernel.Platform.cs
src/Platform/SingPlus.Platform.Host/HostPlatformAuthorityProvider.cs
```

Этот contour подтверждает архитектурную границу между local OS authority и external provider authority, но **не является HybridCPU hardware integration**.

Current feature discovery содержит только:

```text
NeutralDomainBinding
DirectOwnedRegionMapping
```

Current provider operations:

```text
BindDomain
RevokeDomain
MapOwnedRegion
RevokeRegionMapping
```

Current statuses:

```text
Success
Unavailable
Unsupported
Denied
Stale
Revoked
WrongDomain
Faulted
```

Следовательно, claims о DMA/IOMMU, compute, virtualization, evidence, SecureCompute, GPU/display и coherence по-прежнему требуют отдельного external provider contract.

## Why a bridge is required

SingNextOS treats HybridCPU compiler/runtime/ISE/ISA as an external black box. OS code must not depend on internal HybridCPU types, physical lane IDs, raw MicroOps, VMCS state stores, host pointers or backend-specific tokens.

Correct layering:

```text
.NET-like public API
 -> generated typed SIP client
 -> typed service SIP
 -> capability + ownership IPC
 -> privileged SingNextOS kernel authority
 -> SingPlus.Platform abstractions
 -> provider binding
 -> existing HybridCPU runtime / ISE when such provider exists
```

Only privileged kernel/platform integration may correlate local OS principals/regions with external opaque leases.

## Local authority and provider authority remain separate

Current bridge deliberately preserves two namespaces of identity:

```text
local:
  PlatformDomainBindingId
  PlatformDomainBindingGeneration
  PlatformRegionMappingId
  PlatformRegionMappingGeneration

provider:
  PlatformProviderDomainLeaseId
  PlatformProviderRegionMappingId
  PlatformProviderLeaseGeneration
```

This is normative. A local `CapabilityId` is not passed into `IPlatformAuthorityProvider`, and a provider lease is not published as a process-visible Sing+ capability.

Hardware-backed effect follows:

```text
local SingNextOS capability/ownership validation
        AND
live provider/platform grant
        -> effect may proceed
```

Neither half substitutes for the other.

## Phase-1 snapshot domain binding semantics

At the `af791aba...` snapshot,
`RuntimeKernel.BindPlatformDomain(ProcessHandle)` resolved the live process and
bound the provider subject as:

```text
PlatformDomainIdentity(
    DomainId,
    ProcessGeneration)
```

That historical bridge rejected duplicate active bindings, stale generations,
wrong subjects and revoked bindings. Provider-returned subject identity was
checked before the local binding was published. Current v2 instead carries the
exact `ProcessHandle`, as stated in the status note above.

The v1 snapshot did **not** split execution/memory/I/O leases into three
separately implemented local types. `NeutralDomainBinding` was a deliberately
narrow generic local seam. Later delivery remains described by the roadmap.

## Phase-1 snapshot direct owned-region mapping semantics

`RuntimeKernel.MapPlatformOwnedRegion` enforces the local side before calling the provider:

1. resolve the live owner process;
2. validate platform domain binding against `DomainId + ProcessGeneration`;
3. derive required local rights from requested `PlatformMemoryAccess`;
4. validate a live capability for the caller generation;
5. require that the capability has `ResourceKind.MemoryRegion` and identifies the exact `RegionId`;
6. validate current region owner/generation;
7. reserve the region against incompatible ownership operations;
8. call the provider;
9. release the reservation if provider mapping fails.

For a read mapping, local capability requires `Map | Read`; for write, `Map | Write`; read/write requires all relevant bits.

The bridge also validates that the returned provider mapping refers to the same provider domain lease, exact region identity and requested access.

## Current region/platform interlock

`RegionAuthority` contains `PlatformMappingReserved`. While set, the current runtime blocks:

- ownership transfer;
- borrow/loan;
- release;
- domain reclaim.

Process termination/fault also fails while active platform authority exists. The provider mapping must first be revoked and the local reservation released.

This is an important current property: local ownership cannot silently move while an external mapping is still considered active.

What it does **not** prove is how a future HybridCPU provider drains hardware queues, revokes an IOMMU mapping, performs TLB/cache maintenance or waits for DMA/GPU fences. Those are provider-specific external semantics.

## Host provider status

`HostPlatformAuthorityProvider` is a deterministic reference/test provider. It implements the two current feature bits and rejects wrong-domain, stale/revoked and duplicate active mappings.

Its purpose is to prove SingNextOS-owned semantics:

- local validation happens before provider call;
- provider identity is opaque;
- stale/revoked state fails closed;
- external unsupported/denied result does not publish a successful local ownership transition;
- active mapping blocks incompatible region lifecycle.

It must never be presented as HybridCPU IOMMU/DMA or hardware zero-copy evidence.

## What is still external-blocked

### HybridCPU provider binding

No current repository code proves an `IPlatformAuthorityProvider` implementation backed by real HybridCPU neutral runtime owners.

`EXT-HCPU-003` and `EXT-HCPU-004` therefore remain valid: SingNextOS still needs a stable external binding for real domain and owned-region mapping authority.

### DMA and I/O domains

Current `DmaCapability` is a local semantic capability, not an external DMA grant. Current v1 platform provider has no DMA submit/drain/fence API.

Future flow must remain:

```text
Device/Dma capability
+ live owned region generation
+ live platform domain/mapping
+ exact direction/range
+ provider operation grant
+ completion/publication
```

### Coherence

`DirectOwnedRegionMapping` does not mean universal coherent shared memory. A provider must truthfully state any memory-ordering/cache/coherence requirements. When such a contract is absent, SingNextOS must copy, serialize access, perform an explicitly supported maintenance protocol, or fail closed.

### Compute

MatrixTile, DSC1 and L7-SDC are integration candidates described in chapters 04–05 and `EXT-HCPU-005`. No current `IPlatformAuthorityProvider` method exposes them.

### Virtualization, evidence and SecureCompute

These remain future/external provider families under `EXT-HCPU-006`. VMX compatibility cannot become the bridge authority model, and SecureCompute remains feature-gated until production-positive externally.

### GUI/display/GPU

The native UI architecture is defined in [`12_NATIVE_API_AND_UI_CONTRACTS.md`](12_NATIVE_API_AND_UI_CONTRACTS.md). There is no current display/compositor/GPU provider path in the bridge. Future surface presentation must reuse the same capability/ownership/provider principles rather than inventing a privileged shared framebuffer API.

## Target bridge evolution

New feature families should be added only when a concrete use case and external interface exist. They must remain semantic; no raw lane/opcode topology belongs in public/SIP APIs.

Potential target families:

### Execution-domain service

- execution lifecycle/budget intent;
- park/resume/terminate or provider equivalents;
- stale/revoked domain detection;
- event/wait integration when available.

### Memory-domain service

- exact owned-region mapping;
- explicit access direction;
- rebind/revoke/drain semantics;
- optional region protection classes only when enforceable.

### I/O-domain and DMA service

- device/IOMMU scope;
- exact region/range mapping;
- submit/revoke/drain/completion;
- no raw physical addresses in SIP messages.

### Bulk/Matrix/Accelerator providers

- semantic operations over owned/borrowed regions;
- explicit capability and provider feature discovery;
- staged result/publication;
- no raw lane allocation.

### Virtualization/evidence/secure providers

- neutral child-domain composition;
- classified read-only evidence;
- optional secure-domain support only when production-positive;
- compatibility projections downstream.

## Provider contract rules

Every future provider extension must preserve the following invariants.

### Opaque external identities

External IDs/tokens are stored behind the privileged bridge and have independent generation/lifetime. Ordinary SIP code never treats them as authority.

### Semantic operations

Good:

```text
MapOwnedRegion
BindIoDomain
SubmitBulkTransform
PresentSurface
CreateChildDomain
ReadPlatformEvidence
```

Bad:

```text
SelectLane6
VMWRITE(field)
UsePhysicalAddressAsCapability
SubmitRawHostPointer
```

### Explicit failure vocabulary

Unsupported, denied, stale, revoked and faulted conditions must stay distinguishable. A future operation may additionally need staged/completed/published/cancelled states, but `provider returned success` must never be silently widened into a stronger publication guarantee than the contract defines.

### No self-mint

A provider result cannot mint a process-visible local capability by itself. Kernel policy decides which local authority exists.

### Rollback before publication

If provider staging succeeds but local commit fails, external state must be revoked/cancelled before caller-visible success. Conversely provider failure must not leave local ownership pretending that the external transition completed.

## Ownership transfer with an active platform mapping

Current v1 takes the conservative path: transfer is denied while a mapping reservation is active.

A future hardware-backed zero-copy rebind may support:

```text
close new external use
 -> drain/cancel outstanding device/compute work
 -> revoke old mapping/grant
 -> stage mapping for target domain
 -> advance local region generation/owner
 -> publish receiver ownership
```

This is **target semantics**, not current implementation. Until an external provider proves atomic/recoverable rebind, conservative revoke-before-transfer remains correct.

## Feature discovery

Current feature bits are intentionally small. Future discovery may add semantic features such as:

```text
IoDomainBinding
DmaMapping
BulkComputeDsc1
MatrixTileV1
AcceleratorCommandsV1
VirtualizationDomains
NestedDomains
PlatformEvidence
SecureDomains
SurfacePresentation
```

A feature bit only advertises provider support for a contract family. It is never a grant for a particular domain/request.

## Existing external requirements

The current external requirement split remains authoritative:

- `EXT-HCPU-001` — AOT/image/ISE qualification;
- `EXT-HCPU-002` — console/timer/MMIO/IRQ/DMA HAL bindings;
- `EXT-HCPU-003` — neutral domain binding;
- `EXT-HCPU-004` — owned-region mapping/revocation/direct access;
- `EXT-HCPU-005` — scoped compute providers;
- `EXT-HCPU-006` — virtualization/nested/evidence/SecureCompute discovery.

The presence of local v1 implementations does not close external requirements 003–006. It closes only the SingNextOS-owned **abstraction and host conformance** portion.

## Non-goals

This bridge does not request or imply:

- new ISA instructions;
- HybridCPU compiler/backend changes;
- a VMCS manager;
- SecureCompute activation;
- global coherence implementation;
- DSC2;
- raw physical addresses in public APIs;
- a universal GPU ABI;
- a special GUI authority system.

## Conformance direction

A real provider integration must preserve current negative properties and add hardware-backed evidence for:

- stale domain/mapping denial;
- exact owner/range/access checks;
- local capability denial before external effect;
- external denial without local state publication;
- drain/revoke before transfer/reclaim;
- unsupported feature truthfulness;
- completion/publication semantics;
- no authority leakage through provider tokens/evidence.

## Decision

At the Phase-1 snapshot, the repository proved the **local shape** of the
Platform Authority Bridge: narrow semantic contracts, opaque provider leases,
generation checks, local capability validation and owned-region mapping
interlocks.

At that closure point, the next claim boundary was external. The later real
provider still does not prove HybridCPU DMA/remap/coherent zero-copy; those
claims require their own current code and tests.
