# Phase 4 — Memory, ownership, mapping and coherence

## Status

**In progress.** Phases 1–3 are complete in SingNextOS. This iteration implements the first real Phase-4 vertical slice: one exact Sing-owned region slice can be mapped into the neutral HybridCPU runtime, prepared with explicit non-coherent visibility semantics, and revoked through the existing completion-gated Phase-2 reclaim path.

Full Phase 4 is **not** complete yet. Two-domain MOVE/borrow handoff, external-producer acquire semantics, and copy fallback remain later Phase-4 work.

The cross-repository integration gate pins the exact HybridCPU neutral mapping dependency commit `79131c89d686a03636f17cd27fdecf818b12c8c0`.

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

`MOVE` promises exclusive authority transfer. It does **not** promise atomic page-table remap or physical no-copy.

## Current implemented slice

### 1. Exact region-slice identity

SingNextOS now has a versioned semantic v2 mapping contract:

```text
PlatformRegionSlice
  PlatformRegionIdentity
    RegionHandle         // includes exact region generation
    RegionOwner          // DomainId + process generation
    ByteLength
  Offset
  Length
  Access
```

Before any exact-provider call, `RuntimeKernel.MapPlatformOwnedRegionSlice(...)` validates:

- live process generation;
- exact platform-domain binding;
- local memory-region capability and required `Map` / `Read` / `Write` rights;
- exact capability resource identity;
- exact `RegionAuthority` owner and region generation;
- non-negative offset;
- positive length;
- overflow-free containment within the region;
- `Read`, `Write`, or `Read|Write` access only.

Invalid bounds, owner, generation, capability, or access fail before external mapping materialization.

The provider returns `PlatformProviderOwnedRegionMapping`, which combines the existing opaque provider mapping lease with the exact requested `PlatformRegionSlice`. The bridge treats mismatched domain lease, generation, region, range, or access as malformed provider evidence and fails closed.

### 2. Ownership remains exclusively in Sing

`RegionAuthority` remains authoritative for:

- owner identity;
- region generation;
- borrow state;
- payload lifetime;
- MOVE/release legality.

The HybridCPU provider owns only external mapping lifetime. It never creates or replaces a Sing `OwnedRegion<T>` / `OwnedBuffer<T>`.

The existing conservative `PlatformMappingReserved` interlock remains whole-region scoped in this slice. While any exact external slice mapping exists, Sing blocks:

- MOVE/ownership transfer;
- CPU borrow/loan;
- local region release.

This is intentionally conservative. Multiple independently reservable subranges are not claimed yet.

### 3. Real neutral HybridCPU mapping owner

The narrow `HybridCPU_NeutralRuntime` dependency adds an opaque mapping owner with independent identity spaces:

```text
NeutralDomainBindingHandle / Epoch
  != NeutralOwnedRegionMappingHandle / Epoch
  != PlatformProviderDomainLeaseId / generation
  != PlatformProviderRegionMappingId / generation
  != RegionHandle / generation
```

The external neutral mapping commits only:

- exact offset;
- exact length;
- exact read/write access;
- exact neutral-domain lease;
- explicit `NonCoherent` coherence model.

No physical address, PTE/page-table identity, cache-line identity, DMA/IOMMU token, VMX/VMCS state, lane ID, or opcode crosses this provider-facing surface.

### 4. Explicit mapping-bound memory visibility

`PlatformRegionVisibilityRequest` binds visibility evidence to the exact provider mapping ID/generation and exact region slice.

The real neutral mapping in this slice is explicitly **non-coherent**. It supports:

```text
ExternalExecutionDomain + PublicationFence
  -> PublicationFenceSatisfied
```

It deliberately does **not** claim:

```text
CoherentAccess
CacheMaintenance
```

Those requirements return semantic `Unsupported`; the Sing bridge returns `PlatformUnsupported` instead of publishing false ready/coherent evidence.

There is no global `FlushCaches`, no cache-line topology ABI, and no ambient coherence assumption.

### 5. Completion-backed exact revoke and reclaim

The existing Phase-2 lifecycle is reused rather than duplicated:

```text
local mapping authority live
  -> BeginRegionMappingRevocation
  -> HybridCPU exact mapping close
  -> provider PlatformOperationIdentity
  -> exact Closed completion receipt
  -> bridge validates operation/domain/mapping generations
  -> local RegionAuthority owner/generation revalidated
  -> PlatformMappingReserved released
  -> mapping metadata forgotten
  -> MOVE/loan/release may proceed
```

The neutral owner can close this mapping synchronously, so the provider emits a `Closed` receipt only after exact external close succeeds. The Sing lifecycle nevertheless treats the receipt as evidence, not authority, and still performs all local generation/ownership revalidation before reclaim.

A stale receipt, wrong operation/domain, malformed mapping result, faulted closure, or non-terminal completion cannot release the local reservation.

Once revocation begins and the bridge is `Draining`, the mapping cannot produce new visibility evidence.

### 6. Compatibility boundary

The legacy whole-region provider lease remains available as a compatibility projection. Exact v2 mappings carry offset/length in a separate semantic wrapper while reusing the already-proven Phase-2 base mapping lifecycle.

This avoids rewriting the legacy host mapping path and does not weaken its existing reclaim guarantees.

## What this slice does not prove

This iteration does **not** claim:

- physical zero-copy transfer;
- atomic page-table remap;
- universal CPU/HybridCPU coherence;
- multi-domain mapping handoff;
- external-producer → CPU acquire/cache-maintenance semantics;
- device grants;
- DMA/IOMMU authority;
- multiple simultaneous independently reservable slices from one Sing region.

`OwnedRegionMapping v2 = Executable` means this concrete exact mapping operation is real. It does not mean every Phase-4 transfer/coherence mode exists.

## Remaining Phase-4 work

### Two-domain MOVE / borrow orchestration

The next slice should prove at least one cross-domain ownership handoff using the completed primitives:

```text
old owner blocks new external use
  -> old exact mappings drain and close
  -> required producer-side publication/acquire evidence
  -> RegionAuthority.Transfer()
       owner := target
       region generation++
  -> old RegionHandle becomes stale
  -> optional exact mapping for target generation
  -> receiver publication
```

CPU `BorrowLease<T>` remains distinct from an external mapping/grant. A later external shared-read grant must be bridge-private and explicitly revoked before the borrow ends.

### Acquire and copy fallback

A future external-producer path must model `AcquireFromConsumer` or equivalent semantic evidence when the CPU regains ownership. If direct mapping/visibility is unsupported, bounded copy or serialized service access remains legal.

The high-level MOVE contract must remain unchanged regardless of whether bytes were copied or backing was remapped.

## Zero-copy interpretation

Use these terms precisely:

- **logical zero-copy**: ownership wrapper/authority moves without duplicating the logical payload;
- **same-backing mapping**: two domains are mapped to the same physical backing under bounded authority;
- **physical zero-copy transfer**: ownership changes without moving bytes;
- **direct device access**: device/IOMMU mapping reaches the region backing.

Only claim the stronger form when a provider proves it.

## Tests for this slice

Sing-focused coverage proves:

- out-of-range slice rejected before provider call;
- wrong owner and stale region generation rejected before provider call;
- exact offset/length/access/owner are committed by the provider result;
- mapped region cannot MOVE/loan/release;
- `CoherentAccess` on the non-coherent mapping fails closed;
- exact `PublicationFence` succeeds;
- draining mapping rejects new visibility work before provider call;
- stale completion cannot release the region reservation;
- forged exact offset/length is rejected;
- after exact `Closed` + local revalidation, MOVE succeeds with a new region generation;
- public v2 surfaces contain no hardware-shaped authority identifiers.

Cross-repository coverage additionally proves real HybridCPU mapping materialization, independent identity spaces, explicit non-coherent fence behavior, exact revoke, process-teardown ordering, and zero active external mappings before domain close/local exit.

## Acceptance criteria

This first Phase-4 slice is complete when the pinned cross-repository gate proves exact map → publication fence → exact close/Closed receipt → Sing reservation release on the real neutral HybridCPU mapping owner.

**Full Phase 4** remains complete only when two real isolated domains can transfer or borrow an owned region while preserving Sing-local ownership and proving all external mappings are revoked/acquired before old authority can be reused or reclaimed.

## Do not do

- no global shared-memory premise;
- no atomic-remap semantic requirement;
- no raw physical/PTE/page-table/IOMMU identifiers in SIPs;
- no mutable shared region without explicit single-writer/visibility protocol;
- no unconditional “zero-copy” API guarantee;
- no DMA implementation in this slice.
