# Phase 5 — CXL Type-3 Memory Provider

Status: **Complete** for the single-host model backend. See
[05_PHASE5_IMPLEMENTATION_EVIDENCE.md](05_PHASE5_IMPLEMENTATION_EVIDENCE.md).

## Goal

Add a SingNextOS CXL.mem Type-3 memory provider while keeping normal users on `OwnedRegion` / `OwnedBuffer` and existing region authority. CXL placement/backing is a provider detail, not a new application memory object.

## Target path

```text
allocation / placement intent
  -> memory placement policy
  -> ICxlMemoryProvider
  -> CXL.mem backing/window binding
  -> OwnedRegion
  -> normal CPU/device consumers
```

## Public model

A CXL-backed allocation remains an ordinary owned region with optional semantic placement metadata such as:

- volatile vs persistent class where supported;
- NUMA/proximity class;
- capacity/latency/bandwidth policy;
- migration/reclaim policy;
- shareability policy;
- device-coherent accessibility as a queried capability.

It must not expose DPA, decoder indices or fabric route identity as ordinary region authority.

## Provider responsibilities

The Type-3 provider owns:

- discovery of usable CXL memory capacity;
- translation from semantic placement request to platform HPA/HDM/fabric binding;
- provider-private backing identity;
- health/fault/reconfiguration events;
- hot-add/hot-remove/rebind lifecycle;
- visibility/cache-maintenance metadata required by the platform;
- generation invalidation on backing/window changes.

The normal region layer owns:

- allocation identity;
- region authority;
- MOVE/borrow semantics;
- `MutationEpoch`;
- `RegionUse`;
- user-facing lifetime and reclaim state.

## CPU access semantics

CPU load/store access to CXL.mem is modeled through the platform memory map and normal memory semantics. Do **not** introduce an IOMMU mapping simply to represent CPU access to a Type-3 region.

IOMMU/domain mappings are added only when a device DMA path actually requires translated access to that region.

## Shared/coherent use

CXL.mem availability does not automatically make a region safe for simultaneous host/device writers. `RegionUse` remains the compatibility authority.

Examples:

- CPU read-only + device read-only may share when the provider supports the required visibility semantics;
- CPU writer + device writer requires an explicit stronger sharing contract, not merely CXL coherence;
- direct device output into a region requires the Phase 7 publication/effect policy;
- cross-host shared memory is out of scope until Phase 10.

## Reclaim/hot-remove

A provider event that invalidates the backing must:

1. stop new admissions to the affected backing;
2. invalidate the affected fabric/binding generation;
3. mark dependent operations for fail-closed recovery;
4. drain/cancel/quarantine in-flight operations according to lifecycle state;
5. migrate or revoke regions only through the normal region authority/lifetime machinery;
6. never silently replace backing while preserving stale provider bindings.

## Negative tests

- allocator requests CXL placement when no provider capacity -> explicit fallback or failure per policy;
- backing generation changes before device submit -> operation rebind/reject;
- hot-remove while CPU-owned region exists -> region enters controlled reclaim/migration state, not dangling access;
- Type-3 provider returns raw DPA to application API -> contract test failure;
- CXL-backed region passed to ordinary DMA path -> must use existing DMA/IOMMU binding rules, not bypass them;
- coherent capability absent -> no direct coherent access optimization.

## Acceptance criteria

- fake/model Type-3 provider allocates an `OwnedRegion` without introducing a public `CxlRegion` authority type;
- CPU-facing tests use normal region APIs;
- DMA to CXL-backed memory continues through existing device binding contracts;
- hot-remove/rebind invalidates only affected regions/bindings where possible;
- placement policy can fall back to non-CXL memory without changing application ownership semantics.

## External blockers

Real hardware support requires platform firmware/ACPI CXL discovery, HDM decoder programming policy and hardware/firmware ownership rules. QEMU can be used for the initial model backend before physical hardware.
