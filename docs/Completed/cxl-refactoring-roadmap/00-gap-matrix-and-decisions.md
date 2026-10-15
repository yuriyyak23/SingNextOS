# Phase 0 — Baseline, Gap Matrix and Architectural Decisions

## Goal

Freeze a code-grounded SingNextOS-only baseline before adding CXL types. The implementation work must start from executable code and tests, not from names present only in whitebooks or future-roadmap documents.

## Required repository audit

Re-check current code/tests and classify every relevant mechanism against these areas:

- `RegionAuthority`;
- `OwnedRegion` / `OwnedBuffer`;
- MOVE / borrow semantics;
- `PlatformAuthorityBridge`;
- platform domain and mapping generations;
- `DeviceLease`;
- MMIO / IRQ / DMA contracts;
- IOMMU-facing bindings;
- `PlatformMemoryVisibility`;
- `CoherentAccess`;
- publication/acquire and cache-maintenance contracts;
- completion/reclaim lifecycle;
- `DeviceResourceSet`;
- accelerator/compute service boundaries;
- virtualization and device-memory sharing;
- Evidence / SecureCompute separation;
- hardware/provider feature taxonomy.

Documentation to reconcile includes `docs/whitebook`, `docs/whitebook/hybridcpu-ise`, `docs/hybridcpu-v2-refactoring-roadmap`, `docs/next-refactoring-roadmap`, `docs/singularity-derived-refactoring-plan`, and `docs/external-requirements`.

## Gap matrix categories

### Already exists

A mechanism belongs here only when executable code plus tests prove the required invariant. Typical candidates from the previous architecture review are the region/capability ownership model, device-resource grouping, device leases, platform authority bridging, and parts of platform mapping/domain generation handling. Each item must be linked to concrete code/test locations in the implementation PR.

### Partially reusable

Likely candidates:

- DMA/IOMMU binding machinery that can materialize device access to an existing region but does not yet model CXL-specific fabric/window reconfiguration;
- memory visibility/coherent access abstractions that distinguish software visibility from cache maintenance but do not yet cover CXL providers;
- completion/reclaim logic that lacks the common seven-state lifecycle;
- SecureCompute evidence plumbing that can carry CXL security state but must not grant memory authority;
- virtualization resource contracts that are not yet sufficient for dynamic fabric binding/pooling.

### Needs extension

Expected extensions:

- per-region `MutationEpoch`;
- explicit `RegionUse` reservation/borrow state;
- provider-neutral operation lifecycle;
- CXL provider capability taxonomy;
- device/fabric/binding generation snapshots;
- CXL Type-3 placement/backing metadata hidden below `OwnedRegion`;
- staged Type-2 compute provider integration;
- dynamic reconfiguration/reclaim state machine.

### Missing

Expected missing mechanisms:

- CXL topology/device discovery and capability parsing;
- HDM decoder/window materialization backend;
- CXL Type-3 memory provider;
- CXL Type-2 coherent accelerator provider;
- Fabric Manager client/provider and reconfiguration events;
- CXL IDE/device-security evidence adapter;
- QEMU/real-hardware CXL integration tests;
- multi-host shared-memory/P2P policy.

### Should NOT be implemented

- a new CXL authority model parallel to `RegionAuthority`;
- a monolithic `ICxlProvider` owning discovery, memory, IOMMU, compute, fabric and security;
- CXL physical addresses/routes/HDM decoder IDs/DPA in normal `OwnedRegion` or application capability interfaces;
- `coherent == safe zero-copy` shortcuts;
- implicit ownership transfer on device completion;
- security evidence as permission;
- software emulation that claims to provide hardware CXL coherence, IDE, isolation or Fabric Manager enforcement that is absent on the platform;
- SingNextOS changes to HybridCPU ISA, lanes, compiler, replay engine, or legality engine in this roadmap.

## Architectural decisions to freeze

### AD-1: CXL is provider decomposition, not a new top-level authority

Use independent roles:

```text
CxlIoDeviceProvider
CxlMemoryProvider
CxlCoherentAccessProvider
CxlFabricProvider
CxlSecurityEvidenceProvider
```

Names are provisional; responsibilities are not.

### AD-2: Avoid a global fabric epoch when narrower generations suffice

Default generation set:

- region identity/generation from existing region authority;
- `MutationEpoch` per region;
- platform mapping/domain generation from existing platform bindings;
- `DeviceGeneration` per device/function/lease root;
- `FabricBindingGeneration` per affected CXL binding/window/topology object.

A broad fabric-wide generation may exist only if the backend cannot report a narrower fault/reconfiguration scope. Do not invalidate all CXL resources for a local port/window event by default.

### AD-3: CPU CXL.mem access is not an IOMMU abstraction

CXL.mem Type-3 memory is exposed to the host through host physical address windows/HDM decoder topology. The IOMMU is relevant when a device performs translated DMA/PASID/ATS-style access to memory, including CXL-backed host memory, but it is not the mechanism by which CPU load/store accesses CXL.mem.

### AD-4: CXL.cache is a coherence capability, not a universal IOMMU path

Do not claim that every CXL.cache transaction is authorized by an IOMMU. SingNextOS must represent the provider's actual hardware translation/protection mechanism and still perform software authority checks before materializing the device binding.

### AD-5: Staging is the default compute publication policy

Type-2 accelerator output is staged unless a direct coherent path proves all required region-use, mutation, visibility, publication and external replay constraints.

## Deliverables

- updated code-grounded gap matrix with file/test references;
- ADR-style record for AD-1 through AD-5;
- list of current types to extend vs new provider types;
- explicit list of documentation-only names that do not yet have executable support.

## Acceptance criteria

- every `Already exists` row points to executable code and at least one test;
- every proposed new type has a reason it cannot be represented by an existing contract;
- no implementation task modifies HybridCPU-v2 or compiler sources;
- reviewers can identify a single existing SingNextOS authority root for every future CXL hardware binding.
