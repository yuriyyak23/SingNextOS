# Technical specification — Singularity-derived additions to SingNextOS

## 1. Scope

This specification defines the required additions derived from the review of the authentic Microsoft Research Singularity `Interfaces`, `Kernel`, `Libraries` and `Drivers` structure.

It extends the existing HybridCPU-v2 roadmap. It does not replace SingNextOS capability/ownership/domain semantics and does not introduce a second kernel, HAL or compatibility-first authority model.

Normative terms **MUST**, **MUST NOT**, **SHOULD** and **MAY** are intentional.

## 2. Core invariants

Every implementation MUST preserve:

```text
local Sing capability
AND live local subject/resource generation
AND legal SIP/session protocol state
AND required ownership state
AND live opaque platform lease when external effects are involved
AND HybridCPU admission when applicable
  -> effect is allowed
```

The following namespaces MUST remain distinct:

- `CapabilityId`;
- `DomainId`;
- process handle/generation;
- region handle/ownership generation;
- endpoint session ID/generation;
- local operation ID/generation;
- platform provider lease/mapping IDs;
- HybridCPU execution/memory/I/O domain IDs/epochs;
- IOMMU/DMA/accelerator provider tokens.

No equality or implicit conversion between these namespaces may be used as authority proof.

## 3. Required architectural boundaries

The repository MUST enforce the conceptual layers:

```text
Contracts / public resource abstractions
Privileged runtime/kernel mechanisms
Platform bridge/provider adapters
Isolated services and drivers
Native libraries/SDK
Applications and compatibility personalities
```

Required restrictions:

1. contract assemblies MUST NOT reference privileged runtime implementation assemblies;
2. SIP DTOs MUST NOT contain provider leases, raw physical addresses, raw IOMMU identifiers, VMCS state, HybridCPU lanes or raw opcodes;
3. source-facing libraries MUST NOT call bridge/provider APIs directly;
4. drivers MUST NOT gain authority merely through assembly identity or privileged project references;
5. compatibility personalities MUST NOT own independent root hardware/platform authority;
6. provider adapters are the only layer allowed to translate Sing-local authority into provider-specific neutral HybridCPU operations.

Architecture tests MUST enforce these rules in CI.

## 4. Service contract model

### 4.1 Descriptor

Add or extend generated contract metadata to represent the semantic equivalent of:

```text
ServiceContractDescriptor
  ContractId
  Version
  Digest
  ProtocolStates
  MessageDescriptors
  AllowedTransitions
  TerminalStates
  CancellationTransitions
  OwnershipEffects
  CapabilityRequirements
```

Names may differ if existing types can be extended without churn.

### 4.2 Protocol state

Each endpoint session MUST have a protocol state and generation where the contract requires ordering.

The runtime/server boundary MUST reject a message before side effects if the transition is illegal.

Client-side generated guards MAY improve diagnostics but MUST NOT be the sole enforcement point.

### 4.3 Ownership effects

Contracts for large/mutable data SHOULD be able to declare semantic effects such as:

- no ownership change;
- MOVE to peer;
- temporary read-only borrow;
- temporary exclusive loan if supported;
- returned ownership on completion;
- external/device grant required before execution.

The generator/runtime MUST NOT silently infer a device grant from a local borrow.

### 4.4 Terminal behavior

Contracts MUST define behavior for close, cancellation, peer termination and already-published responses. A completed/published result remains committed after subsequent peer termination.

## 5. Ownership exchange model

### 5.1 Source of truth

SingNextOS `OwnedRegion`/`OwnedBuffer` state remains the logical ownership source of truth.

Provider mappings/grants MUST NOT become an alternate owner registry.

### 5.2 MOVE

A committed MOVE of mutable backing MUST:

1. validate owner and generation;
2. prevent new old-owner mutation;
3. drain or revoke incompatible external grants;
4. perform acquire/visibility operations when required;
5. assign the new owner;
6. increment ownership generation;
7. rebind externally only after the new authority is established.

An implementation MAY copy instead of remap. MOVE is an authority semantic, not a zero-copy guarantee.

### 5.3 External grants

Device/accelerator/display mappings MUST encode exact range, direction/access and lifecycle. They MUST be independently revocable from local borrows.

### 5.4 Coherence

No public contract may assume global hardware coherence. CPU-to-device and device-to-CPU transitions MUST include provider-declared visibility semantics when required.

## 6. Service manifests

Add a versioned `ServiceManifest` (or equivalent) that can represent:

```text
ComponentIdentity
ComponentVersion
Image/ExecutableDigest
Entrypoint
ProvidedContracts[]
RequiredContracts[]
ResourceRequirements[]
PlatformRequirements[]
MemoryProfile
Contract/ProtocolDigests[] as needed
```

A manifest is declarative metadata only.

**Manifest requirements MUST NOT be treated as granted authority.** Admission must separately select and grant exact local resources/capabilities.

Manifest parsing/validation MUST reject unknown mandatory requirement kinds and malformed resource bounds.

## 7. Service discovery and endpoint sessions

### 7.1 Discovery

Service discovery SHOULD return identity, compatible contract descriptors and endpoint discovery metadata.

Discovery MUST NOT by itself grant the right to invoke privileged operations.

### 7.2 Endpoint session

Introduce or formalize an `EndpointSession` concept containing the semantic equivalent of:

```text
service endpoint identity
caller domain/process generation
contract version/digest
session generation
current protocol state
granted local capability set/reference
```

Session admission MUST validate caller authority and compatibility before exposing an invokable endpoint.

A stale session generation MUST fail closed.

## 8. Component admission and lifecycle

A logical component instance MUST be traceable to:

- component/image identity;
- domain/process identity and generation;
- manifest;
- provided/required contracts;
- local capabilities;
- owned regions;
- open sessions;
- device resources;
- live platform domain/resources;
- pending external operations;
- lifecycle state.

Startup MUST be transactional enough that a failure unwinds already-created local/provider authority.

Termination MUST stop new effects immediately while preserving drain-before-reclaim for existing external effects.

Recommended semantic lifecycle:

```text
Declared -> Admitting -> Created -> Starting -> Running
Running/Faulted -> Draining -> Stopped -> Reclaimable
```

Existing process/domain state types may be reused; duplicating state machines is discouraged.

## 9. Driver and device resource model

### 9.1 Driver isolation

Drivers SHOULD run as isolated components/services when platform constraints allow.

A driver MUST receive exact resources through local capabilities and bridge-managed leases. It MUST NOT obtain ambient device access from a global singleton.

### 9.2 DeviceResourceSet

Add the semantic equivalent of a bounded resource bundle:

```text
DeviceLease
MmioMappingLease[]
IoPortLease[] where applicable
IrqBindingLease[]
Dma capability/policy
```

The Sing-visible objects remain local authority objects; provider lease IDs remain bridge-private.

### 9.3 MMIO

MMIO mappings MUST be exact to a device/resource/range/rights tuple and revocable by generation.

Applications MUST NOT receive raw physical addresses as the native hardware authority model.

### 9.4 IRQ

Interrupt binding MUST map a device/provider source into a kernel-owned event/completion route. Raw interrupt vectors/controller details SHOULD remain provider-private.

The semantic lifecycle MUST include bind, delivery/wait, acknowledgment when needed, drain and release.

A stale binding MUST NOT deliver into a recycled process generation.

### 9.5 DMA

Creating a DMA grant MUST require both:

```text
exact memory/region authority
AND exact device/service authority
```

The grant MUST include range and direction and MUST honor explicit non-coherent visibility rules.

No native driver API may accept an arbitrary address as sufficient DMA authority.

## 10. Unified events and completions

The runtime/kernel SHOULD converge on a small policy-neutral event/completion primitive reused across:

- timers;
- channel/request completion;
- IRQ;
- DMA;
- accelerator jobs;
- virtualization events/exits;
- display/surface release.

Externally backed operations MUST expose an opaque local operation identity/generation and terminal completion state.

A common state vocabulary SHOULD include the semantic distinctions:

```text
Staged
Pending
Draining
Completed
Cancelled
Closed
Faulted
```

Duplicate completion, stale completion and terminal-state resurrection MUST be rejected or handled idempotently without widening authority.

## 11. Revocation and teardown

The required causal ordering is:

```text
Active
-> local revoke / exit begins
-> no new effects admitted
-> outstanding external effects drain
-> provider mappings/leases close
-> visibility/acquire completes where needed
-> local resource reclaim becomes legal
```

Provider revoke failure MUST NOT restore a locally revoked capability.

When closure cannot be proven, the backing resource MUST stay pinned/quarantined rather than being unsafely recycled.

Process/component teardown MUST include sessions/channels, ownership transfers, DMA/IRQ, accelerator work, VM events and display grants where present.

## 12. Native libraries and system services

Rich source-facing APIs SHOULD live in libraries over generated SIP clients.

The privileged ABI SHOULD remain limited to neutral mechanisms such as:

- domain/process lifecycle;
- capability mint/delegate/revoke/check;
- owned-region lifecycle;
- typed channel/event transport;
- platform domain/mapping/grant materialization;
- completion/revocation;
- minimal trap/interrupt/event routing.

The kernel ABI MUST NOT grow native policy calls for:

- path/filesystem semantics;
- TCP/socket state;
- window placement/z-order;
- GPU command policy;
- VMCS fields;
- HybridCPU lanes/opcodes.

POSIX/Win32/Wine/VMX-compatible APIs remain downstream personalities over native contracts.

## 13. Required implementation work products

The refactoring is complete only when the repository contains working equivalents of:

1. dependency/architecture conformance tests;
2. versioned service contract descriptors with protocol-state metadata;
3. generator/runtime validation for legal transitions and terminal behavior;
4. ownership-effect metadata integrated with `OwnedRegion`/`OwnedBuffer` paths;
5. versioned `ServiceManifest` plus validation;
6. authority-neutral service discovery;
7. capability-scoped `EndpointSession` admission and session generations;
8. bounded driver `DeviceResourceSet`/resource leases;
9. IRQ-to-event binding without public raw-vector authority;
10. DMA grant creation from exact device + region authority;
11. shared event/completion lifecycle reused by multiple async subsystems;
12. component-level startup/teardown orchestration and state observability;
13. native library/service vertical slice proving rich API over SIP;
14. conformance tests proving compatibility layers do not widen authority.

Existing implementation types SHOULD be extended/reused when they already satisfy these semantics; creating duplicate abstractions merely to match these names is not required.

## 14. Test matrix

At minimum, automated tests MUST cover:

### Authority

- missing capability;
- wrong subject/domain;
- wrong owner;
- stale capability/resource/session/provider generation;
- device authority without region authority;
- region authority without device authority;
- range/direction widening;
- manifest requirement without grant;
- discovery without admitted session.

### Protocol

- valid transition;
- illegal transition;
- duplicate close/completion;
- request after terminal state;
- cancellation racing completion;
- peer death before commit;
- peer death after publication.

### Ownership

- MOVE invalidates old owner generation;
- local borrow is not usable as device authority;
- external grant drains before reclaim;
- non-coherent handoff requires prepare/acquire;
- copy fallback preserves semantics.

### Teardown

- process/service crash with pending calls;
- driver crash with in-flight DMA;
- stale IRQ/completion after process-generation reuse;
- provider revoke fault leaves resource unreclaimed;
- platform domain closure allows final reclaim only after child resources are closed.

### Boundary enforcement

- no provider IDs in SIP DTOs;
- no direct library/app dependency on provider implementation;
- no compatibility personality with additional root authority;
- no raw physical address/lane/opcode/VMCS authority in native public contracts.

## 15. Migration order

Implement in this order unless current code proves a smaller dependency-safe sequence:

1. dependency/conformance rules;
2. stateful SIP metadata and one pilot protocol;
3. ownership-effect integration;
4. manifests + discovery/session admission;
5. device/IRQ/DMA vertical slice;
6. common event/completion adoption;
7. component lifecycle observability/orchestration;
8. native library/service pilot;
9. expand to filesystem/network/virtualization/GUI as existing HybridCPU roadmap phases mature;
10. remove temporary exceptions and make all architectural checks CI-failing.

This sequence must remain subordinate to the existing HybridCPU roadmap's platform prerequisites: do not build a new service abstraction that bypasses unfinished authority/completion/memory-safety work.

## 16. Definition of Done

The Singularity-derived refactoring is complete when:

- contract, ownership, component and driver boundaries are machine-checkable;
- at least one service and one driver use the new model end-to-end;
- protocol-state errors fail before effects;
- manifest/discovery cannot mint authority;
- driver hardware access is exact and revocable;
- asynchronous external effects share trustworthy completion/drain semantics;
- process/component death cannot cause early region/resource reuse;
- rich native libraries require no expansion into a POSIX/Win32-style syscall surface;
- provider-specific HybridCPU identities remain bridge-private;
- tests prove stale/wrong-owner/wrong-domain/denied/revoked/faulted paths;
- documentation makes no stronger zero-copy/coherence/security claim than the provider and tests prove.

## 17. Non-goals

This work MUST NOT be used to justify:

- reimplementing historical Singularity wholesale;
- importing Bartok/Sing#/Spec# as a runtime dependency;
- rebuilding a global ExchangeHeap;
- creating a giant HAL or syscall ABI;
- exposing raw VMX/VMCS, IOMMU, physical-address, lane or opcode authority to applications;
- assuming universal memory coherence;
- claiming physical zero-copy merely because an ownership MOVE occurred;
- moving filesystem/network/GUI policy into the kernel;
- introducing a second runtime/kernel authority layer beneath SingNextOS.
