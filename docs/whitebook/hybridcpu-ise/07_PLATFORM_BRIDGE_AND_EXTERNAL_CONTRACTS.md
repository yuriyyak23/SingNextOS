# 07. Platform Bridge And External Contracts

## Why a bridge is required

SingNextOS deliberately treats HybridCPU-v2 compiler/runtime/ISE/ISA as an external black box. This remains the correct boundary after the audit.

The OS should not reference internal HybridCPU types such as:

- `DomainRuntimeContext`;
- `CapabilityDescriptorSet`;
- `DmaStreamComputeMicroOp`;
- `MatrixTileMicroOp`;
- lane IDs;
- `TrapDecision`;
- `Vmcs*` projection types;
- accelerator native token stores.

Those are implementation/runtime authority details of the external platform.

Instead SingNextOS needs a small **typed Platform Authority Bridge** whose contracts are semantic and stable enough for OS policy.

## Placement

Recommended layering:

```text
public .NET-like API
 -> generated SIP client
 -> typed SIP service
 -> SingNextOS kernel authority
 -> SingPlus.Platform.Abstractions        [local contracts]
 -> HybridCPU platform binding            [external/integration assembly]
 -> existing HybridCPU runtime / ISE
```

Only the kernel/platform binding layer may hold external opaque handles.

SIP/driver code sees only local capabilities, owned regions and generated service contracts.

## Bridge design principles

### 1. Opaque handles

External platform identities must be represented as opaque values with no semantic interpretation in ordinary SingNextOS code.

Examples:

```text
PlatformExecutionLease
PlatformMemoryLease
PlatformIoLease
PlatformComputeLease
PlatformVirtualizationLease
PlatformEvidenceLease
PlatformSecureLease
```

These names are conceptual. Exact types depend on the actual external API.

### 2. Generation-bound

Every externally stateful binding must either expose or be wrapped by a local generation/epoch so stale leases can fail closed after:

- process/domain termination;
- mapping change;
- checkpoint/restore;
- device reset;
- capability revocation;
- external runtime restart.

### 3. No raw pointers as authority

Bridge methods receive local region handles/validated ranges and internally bind them to external memory mappings. They do not expose physical addresses as capabilities.

### 4. Semantic operations

Bridge operations express intent:

```text
BindExecutionDomain
BindMemoryDomain
BindIoDomain
MapOwnedRegion
SubmitBulkCompute
SubmitMatrixOperation
SubmitAcceleratorCommand
CreateChildDomain
ReadPlatformEvidence
```

They do not express lane/opcode selection.

### 5. Explicit stage/result vocabulary

Hardware-backed operations should return typed stage/result states rather than one ambiguous boolean:

```text
Unsupported
Denied
Admitted
Staged
Completed
Published
Cancelled
Stale
Faulted
```

Not every operation needs every state, but the contract must prevent `Admitted == Published` inference.

### 6. No external self-mint

An external platform result never directly becomes a process-visible SingNextOS capability. Kernel policy decides whether to mint/delegate a local capability after platform binding succeeds.

## Proposed local abstraction families

These are architecture targets, not a request to implement them in this audit PR.

### Execution domain bridge

Responsibilities:

- create/adopt process execution binding;
- configure semantic scheduling policy/budget;
- park/resume/terminate domain execution;
- observe domain generation/termination;
- optionally expose event/wait/barrier primitives.

Must not expose:

- physical lane assignment;
- scheduler internals;
- raw rename/commit state;
- replay certificate authority.

### Memory domain bridge

Responsibilities:

- bind an OS domain to external address-space authority;
- map/unmap exact owned-region ranges;
- protect/rebind mappings;
- expose invalidation/drain semantics;
- optionally classify private/shared/measured regions when externally supported.

Must not expose:

- page-table pointers as capabilities;
- VMCS EPT aliases as memory authority;
- host physical pointers to SIPs.

### I/O domain bridge

Responsibilities:

- bind device/IOMMU scope;
- map owned/shared regions for device access;
- issue/revoke DMA grants;
- signal/wait IRQ/event channels;
- drain/cancel before ownership transfer or domain termination.

Must keep MMIO/IRQ/DMA capabilities as local semantic permissions.

### Bulk compute bridge

Potential providers:

- ordinary CPU/vector path;
- DSC1;
- future platform provider.

The provider selection can be platform-owned or policy-owned, but the API is based on operation shape and owned-region access.

### Matrix bridge

Responsibilities:

- create a bounded matrix compute session;
- load from owned/borrowed region;
- execute supported operations;
- store to owned region;
- report supported numeric/layout profiles.

No architectural tile register handles escape to SIPs.

### Accelerator bridge

Responsibilities:

- query scoped accelerator capabilities;
- submit typed descriptor + owned memory footprints;
- poll/wait/cancel/fence;
- publish results only after platform commit.

No assumption of universal coherence or universal device ABI.

### Virtualization bridge

Responsibilities:

- create execution/memory/I/O child domain composition;
- map guest memory;
- attach bounded devices;
- receive neutral events/traps;
- optional compatibility projection.

VMX is downstream compatibility only.

### Evidence bridge

Responsibilities:

- return explicitly classified platform evidence;
- separate guest-visible/debug/host-only classes;
- expose deterministic versioned DTOs;
- never return authority tokens.

### Secure domain bridge

Future-gated responsibilities:

- feature discovery;
- bind secure domain;
- create private/shared/measured regions;
- measurement/evidence export;
- secure checkpoint contract.

Current expected production result may legitimately be `Unsupported` until HybridCPU closes the positive contour.

## Failure and rollback model

The bridge must preserve atomicity between local OS authority and external platform state.

### Example: map region for device

Wrong order:

```text
local capability published
 -> local owner state changed
 -> external mapping attempted
 -> mapping fails
```

Correct staged order:

```text
validate local capability + owner generation
 -> ask platform to stage binding
 -> validate returned binding identity/generation
 -> commit local mapping record
 -> publish operation capability/result
```

If local commit fails, platform stage must cancel/revoke.

### Example: ownership transfer with active compute

```text
close new hardware submissions for old owner
 -> drain/cancel outstanding operations
 -> revoke external mapping/grants
 -> external rebind to target if required
 -> increment local RegionGeneration
 -> publish receiver ownership
```

No partially transferred region is visible.

## Feature discovery

Platform capability discovery must be versioned and truth-preserving.

Suggested semantic capability set:

```text
ExecutionDomains
MemoryDomains
IoDomains
DirectOwnedRegionMapping
DmaMapping
BulkComputeDsc1
MatrixTileV1
AcceleratorCommandsV1
VirtualizationDomains
NestedDomains
PlatformEvidence
SecureDomains
SecureMigration
GlobalMemoryConflictCoherence
```

Discovery result only says that a provider claims the interface. A feature can still be denied for a particular domain/request.

This is analogous to HybridCPU's own rule that support metadata is not runtime authority.

## Local policy versus platform policy

The bridge must never silently widen policy.

Example:

```text
Platform says MatrixTile supported
SingNextOS process has no Matrix compute capability
=> denied locally; no call forwarded
```

and:

```text
SingNextOS process has DmaCapability
Platform I/O domain refuses range/direction
=> denied externally; local capability remains but no effect occurs
```

## Existing external requirements

Current `EXT-HCPU-001` and `EXT-HCPU-002` already correctly state two boundaries:

- external AOT/image/ISE qualification;
- external console/timer/MMIO/IRQ/DMA binding.

The audit identifies additional external contracts that should be tracked separately rather than hidden inside one generic HAL requirement.

## New requirement split recommended by this audit

### EXT-HCPU-003 — neutral domain binding

Existing external interface must provide a way to bind SingNextOS principals to neutral execution/memory/I/O domain authority without VMX/VMCS becoming the state owner.

### EXT-HCPU-004 — owned-region mapping and revocation

Existing external interface must allow exact range mapping/unmapping/rebinding for owned regions with domain/generation/lifetime semantics sufficient for DMA and zero-copy ownership transfer.

### EXT-HCPU-005 — scoped compute providers

Existing external interface must expose, where already available, semantic bindings for MatrixTile, DSC1 bulk compute and scoped L7 accelerator commands without requiring SingNextOS to emit raw physical lanes/opcodes as its authority API.

### EXT-HCPU-006 — virtualization/evidence/secure feature discovery

Existing external interface must expose neutral virtualization/nested/evidence capability discovery and must report SecureCompute positive availability truthfully. Unsupported/future-gated contours must be distinguishable from production-positive ones.

These requirements request **bindings to existing platform behavior**, not modifications to HybridCPU-v2.

## What SingNextOS must not require externally

This audit does not request:

- new ISA instructions;
- HybridCPU scheduler changes;
- VMX backend redesign;
- compiler/backend modifications;
- loader changes;
- SecureCompute activation;
- global coherence implementation;
- DSC2 implementation;
- new accelerator protocol;
- CHERI/tagged memory;
- Bartok/Sing# language changes.

If an external capability does not exist, SingNextOS remains correct by keeping that service unavailable.

## Bridge conformance tests

When a real binding appears, integration qualification should include at least:

- stale domain lease rejection;
- local capability denial before platform call;
- external denial does not mutate local ownership/state;
- region generation mismatch denied;
- active borrow blocks incompatible mapping/transfer;
- revocation closes new DMA/compute work;
- termination drains/cancels then reclaims;
- unsupported compute/secure feature reports unavailable;
- evidence cannot be used as authority;
- compatibility VMX projection cannot mutate neutral owner state;
- malformed external result fails closed;
- deterministic feature/ABI manifest digest.

## Decision

The Platform Authority Bridge is the architectural seam that lets SingNextOS use the full HybridCPU runtime without coupling its public/kernel object model to HybridCPU internals. It should be narrow, typed, opaque, generation-bound and testable; everything else stays behind it.