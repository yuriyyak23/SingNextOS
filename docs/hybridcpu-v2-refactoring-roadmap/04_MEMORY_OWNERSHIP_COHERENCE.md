# Phase 4 — Memory, ownership, mapping and coherence

## Status

**Core integration phase.** Depends on Phases 1–3 and corresponds mainly to `EXT-HCPU-004`.

## Goal

Preserve SingNextOS ownership semantics while making regions safely visible across real HybridCPU memory domains and later devices/accelerators.

The semantic contract stays:

```text
small values          -> copy
large mutable payload -> MOVE exclusive authority
temporary access      -> revocable borrow/shared grant
device access         -> explicit bounded mapping/grant
completion            -> revoke/unmap/acquire/return authority
```

`MOVE` promises exclusive authority transfer. It must **not** promise atomic page-table remap or physical no-copy.

## Current state

SingNextOS already has:

- generation-bound `RegionHandle`;
- `OwnedRegion<T>` / `OwnedBuffer<T>`;
- MOVE transfer that invalidates old ownership views;
- borrow/loan generation and lifetime checks;
- `PlatformMappingReserved`, which blocks transfer/loan/release while a platform mapping is active;
- exact capability/resource checks before host-backed `MapOwnedRegion`.

HybridCPU-v2 has code-confirmed bounded memory/address-space authority, translation/invalidation machinery, domain generations/epochs and explicit non-coherent fence requirements in DMA authority paths. The audit found **no proof of a generic atomic ownership remap primitive** and no basis for assuming universal CPU/device coherence.

## Refactoring tasks

### 1. Map exact region slices, not only whole logical regions

Introduce a semantic slice type such as:

```text
PlatformRegionSlice
  RegionHandle          // includes region generation
  ExpectedOwner
  Offset
  Length
  Access
```

Validate overflow, region bounds, owner, generation and local capability rights before any provider call.

A provider lease must echo/commit the exact slice and access. Mismatched results are provider faults and must be closed before returning failure.

### 2. Separate ownership from mapping

Keep `RegionAuthority` as the OS owner of:

- exclusive owner;
- region generation;
- borrow state;
- payload lifetime.

The provider owns only external visibility/mapping authority. Do not create a second HybridCPU-backed `OwnedRegion` type that competes with Sing ownership.

### 3. Add explicit publish/acquire operations

For CPU → device/accelerator/display handoff, define a provider operation such as:

```text
PrepareForConsumer(mapping, consumerClass, visibilityRequirement)
```

For external producer → CPU reacquisition:

```text
AcquireFromConsumer(mapping, completionReceipt)
```

The outcome must say whether coherence was inherent, a fence was satisfied, explicit cache maintenance was performed, or the mode is unsupported.

Do not expose cache-line sizes/topology or a global `FlushAllCaches()` ABI unless a concrete platform contract genuinely requires it.

### 4. Model transfer with existing mappings conservatively

Default hardware-visible MOVE sequence:

```text
block new external submissions
  -> old mapping/grants Draining
  -> wait completion/publication
  -> revoke old mapping/IOMMU/device authority
  -> acquire/maintenance if required
  -> RegionAuthority.Transfer()
       owner := target
       region generation++
  -> optional map/grant for target
  -> publish receiver ownership
```

This sequence is semantically sufficient even if physical pages are copied or remapped non-atomically.

A future provider may advertise a faster rebind optimization, but the SIP/kernel contract must not depend on it.

### 5. Define borrow vs device grant explicitly

A CPU/SIP `BorrowLease<T>` is a local ownership/lifetime construct. A device or accelerator needs a **separate bridge-private external grant** derived from the borrow/region authority.

Never equate:

```text
BorrowLeaseId == provider mapping/token
```

For shared read access, materialize bounded read-only external authority and revoke it when the borrow ends. For mutable access, prefer exclusive MOVE or a protocol-specific single-writer lease rather than ambient shared mutable memory.

### 6. Keep copy fallback legal

If the provider cannot satisfy direct mapping/coherence for a request, valid outcomes include:

- bounded copy through a kernel/service-owned region;
- serialized access through a service;
- explicit `Unsupported`.

The high-level ownership contract remains unchanged.

## Zero-copy interpretation

Use these terms precisely:

- **logical zero-copy**: ownership wrapper/authority moves without duplicating the logical payload;
- **same-backing mapping**: two domains are mapped to the same physical backing under bounded authority;
- **physical zero-copy transfer**: ownership changes without moving bytes;
- **direct device access**: device/IOMMU mapping reaches the region backing.

Only claim the stronger form when the provider proves it.

## HybridCPU-v2 changes expected

Prefer an exported bounded memory integration facade over existing memory-domain/address-space/invalidation mechanisms. It may need to expose:

- exact map/unmap;
- access permissions;
- domain/mapping epochs;
- terminal revoke completion;
- memory-visibility/fence/cache-maintenance outcome.

Do not add atomic ownership remap or global coherence solely to satisfy SingNextOS.

## Tests

Required tests include:

- out-of-range slice rejected before provider call;
- wrong owner/generation rejected;
- mapped region cannot MOVE/loan/release;
- draining mapping rejects new external use;
- stale completion cannot release reservation;
- non-coherent requirement without satisfied fence fails closed;
- copy fallback preserves MOVE semantics;
- target receives a new region generation and old owner handle is stale;
- provider says direct access unsupported → no false zero-copy claim is published.

## Acceptance criteria

Phase 4 is complete when two real isolated domains can transfer or borrow an owned region while preserving Sing-local ownership and proving all external mappings are revoked/acquired before the old authority can be reused or reclaimed.

## Do not do

- no global shared-memory premise;
- no atomic-remap semantic requirement;
- no raw PTE/page-table/IOMMU identifiers in SIPs;
- no mutable shared region without an explicit single-writer/visibility protocol;
- no unconditional “zero-copy” API guarantee.
