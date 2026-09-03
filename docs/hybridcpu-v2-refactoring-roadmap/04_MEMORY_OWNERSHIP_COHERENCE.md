# Phase 4 — Memory, ownership, mapping and coherence

## Status

**Complete — four vertical slices implemented.**

Phase 4 now has:

1. exact non-coherent owned-region mapping, publication evidence and completion-backed revoke;
2. a two-domain `OwnedBuffer<T>` MOVE handoff with post-close acquire for writable external mappings;
3. a CPU `BorrowLease<T>` -> bounded external shared-read grant lifecycle with verified closure before borrow completion or process reclaim;
4. an explicit true bounded-copy fallback when a requested target exact mapping cannot be materialized.

The cross-repository integration gate remains pinned to HybridCPU neutral acquire commit `4dce496d072b56efae61dfa9d99058eaa782fea3`. The final bounded-copy slice requires no HybridCPU-v2 export or source change: it is a Sing runtime fallback after the external direct-mapping attempt fails.

DMA/IOMMU remain outside Phase 4 and outside this iteration.

## Goal

Preserve SingNextOS ownership semantics while making owned memory safely visible across HybridCPU execution domains without assuming universal coherence or physical zero-copy.

The semantic contract is:

```text
small values          -> copy
large mutable payload -> MOVE exclusive authority
temporary access      -> revocable borrow/shared grant
external access       -> exact bounded mapping/grant
completion            -> close/acquire/return authority
direct-map failure    -> explicit bounded-copy fallback or local-only fallback
```

`MOVE` is an authority transfer. It does **not** promise atomic page-table remap, same physical backing, or physical zero-copy.

## Slice 1 — exact non-coherent mapping

Implemented guarantees:

- exact `PlatformRegionSlice` with region generation, owner, byte length, offset, length and access;
- local capability/owner/generation/bounds checks before provider admission;
- real neutral HybridCPU exact mapping with independent mapping identity;
- explicit non-coherent `PublicationFence` semantics;
- completion-backed exact revoke;
- local `PlatformMappingReserved` release only after valid `Closed` evidence and local generation/owner revalidation.

The whole Sing region remains conservatively reserved while one ordinary exact external slice mapping exists, so MOVE, borrow and release are blocked until closure.

## Slice 2 — two-domain MOVE handoff

`RuntimeKernel.MovePlatformOwnedBuffer<T>(...)` composes existing primitives rather than creating a second ownership state machine:

```text
validate source + target process generations
  -> validate exact source mapping identity
  -> prevalidate optional target binding/capability
  -> source PublicationFence for ExternalExecutionDomain
  -> BeginRegionMappingRevocation
  -> observe until Closed
  -> if source mapping was writable:
       post-close AcquisitionFence
  -> FinalizePlatformRegionMappingClosure
       exact local owner/generation revalidation
       release PlatformMappingReserved
  -> RegionAuthority.Transfer()
       owner := target
       region generation++
  -> optional exact target mapping for the new generation
  -> target PublicationFence
```

No local owner/generation mutation occurs while the source mapping is `Active`, `Draining`, `Faulted`, missing publication proof, or missing required acquire proof.

Writable external mappings require mapping-bound post-close acquire evidence. Read-only source mappings have no external writer and therefore need exact `Closed` plus source publication but no acquire fence. Evidence remains evidence; `RegionAuthority` remains ownership authority.

A target mapping request is prevalidated before source drain, but actual materialization occurs only after `RegionAuthority.Transfer()` commits the new owner and generation.

## Slice 3 — CPU borrow + external shared-read grant

`BorrowLease<T>` remains the CPU-local read-only lifetime object and never becomes a provider token. The bridge derives a separate grant identity:

```text
BorrowLeaseHandle / BorrowLeaseGeneration
  != PlatformBorrowReadGrantId / PlatformBorrowReadGrantGeneration
  != PlatformRegionMappingId / PlatformRegionMappingGeneration
  != PlatformProviderRegionMappingId / PlatformProviderLeaseGeneration
  != NeutralOwnedRegionMappingHandle / epoch
```

The real HybridCPU provider requires:

```text
PlatformRegionSlice.Region.Owner
  == PlatformProviderDomainLease.Subject
```

Therefore the external shared-read grant is bound to the exact owner platform domain while the CPU borrower remains a separate local read-only lifetime.

Grant admission requires exact owner/borrower process generations, exact borrow and region generations, the exact live `BorrowLeaseLifetime`, exact owner-bound platform binding generation, exact bounded range and `Read` only access.

Completion remains:

```text
external read grant Active
  -> request completion
  -> Draining / no new external effects
  -> exact Closed completion
  -> exact local borrow + region + owner + binding + hidden mapping revalidation
  -> reclaim bridge-private grant metadata
  -> only then ReturnLoan / RevokeLoan
```

`Faulted` is not closure. Existing process teardown closes/drains these grants before borrower loan return or owner region reclaim.

## Slice 4 — true bounded-copy MOVE fallback

### 1. Explicit opt-in and exact bound

The fallback is requested with a separate policy:

```text
PlatformBoundedCopyFallbackPolicy(MaxBytes)
```

It is legal only together with an explicit target mapping request. The full authoritative moved region byte length, not merely the requested target slice length, must fit inside `MaxBytes`.

The bound is validated before source publication/revoke begins. A too-small or malformed bound therefore cannot consume source authority or start external drain.

### 2. Trigger boundary

The true copy path is used only when the post-transfer **target exact mapping cannot be materialized**.

It is not used when a target mapping was successfully created and later publication fails. Once a target mapping identity exists, its own revoke/closure lifecycle must remain authoritative; bounded copy must never rematerialize backing underneath a live or potentially live external mapping.

Without an explicit copy policy, the existing result remains:

```text
PlatformMoveTargetExposureState.LocalOwnershipFallback
```

With a valid policy and a failed target mapping admission/materialization, the result is:

```text
PlatformMoveTargetExposureState.BoundedCopyFallback
```

The original target mapping error is retained as `TargetExposureError`; no target external mapping or publication evidence is claimed.

### 3. Ordering and authority

The bounded-copy path runs only after the normal source handoff has completed:

```text
source PublicationFence
  -> source drain / Closed
  -> source AcquisitionFence when writable
  -> release source mapping reservation
  -> RegionAuthority.Transfer()
       owner := target
       region generation++ exactly once
  -> target exact mapping attempt
  -> mapping unavailable
  -> exact target RegionAuthority owner/generation revalidation
  -> prove no target platform mapping is active
  -> kernel-private exact-size staging region
       target backing -> staging
       staging -> fresh target backing
  -> RegionAuthority payload replacement for the same target RegionHandle
  -> invalidate old moved backing
  -> clear/reclaim staging region
  -> return only the fresh target-owned buffer
```

The copy is therefore a physical/backing rematerialization **after** authority already belongs exclusively to the target. It never creates a second Sing owner and never rolls ownership back to the source.

The staging region is kernel-private data storage only. It has no `RegionHandle`, provider lease, HybridCPU token or independent Sing authority. This is deliberate: `RegionAuthority` remains the only source of Sing ownership authority for the transferred region.

### 4. Whole-buffer copy, not mapped-slice copy

MOVE transfers the whole `OwnedBuffer<T>` authority. Therefore bounded-copy rematerialization copies the full authoritative region byte length even if the failed target mapping request covered only one exact slice.

`PlatformBoundedCopyEvidence` records only:

```text
RegionHandle
ByteLength
MaxBytes
```

It is evidence that an exact bounded copy occurred; it is not ownership or mapping authority.

### 5. No new HybridCPU capability

No new HybridCPU-v2 primitive is required. The fallback specifically covers the case where direct target mapping is unavailable and performs the rematerialization entirely inside Sing after the existing source mapping lifecycle has been safely closed.

The existing pinned HybridCPU gate remains unchanged and continues to prove the direct mapping/publication/revoke/acquire paths used before fallback selection.

## Identity and authority invariants

The following remain distinct namespaces:

```text
CapabilityId
DomainId / process generation
RegionHandle / RegionGeneration
BorrowLeaseHandle / BorrowLeaseGeneration
PlatformBorrowReadGrantId / generation
PlatformDomainBindingId / generation
PlatformRegionMappingId / generation
PlatformProviderDomainLeaseId / generation
PlatformProviderRegionMappingId / generation
PlatformOperationId / generation
NeutralDomainBindingHandle / epoch
NeutralOwnedRegionMappingHandle / epoch
```

`PlatformBoundedCopyFallbackPolicy` and `PlatformBoundedCopyEvidence` introduce no new authority identity namespace.

None of the authority identities above are interchangeable. Visibility/acquire/copy evidence, completion receipts and feature discovery remain evidence only. `RegionAuthority` remains the sole Sing ownership authority.

Compatibility projections remain downstream. No provider/HybridCPU mapping ID or hardware-shaped identity is introduced into the SIP/kernel ownership authority ABI.

## Negative guarantees

Tests across the four slices prove:

- source publication failure happens before revoke or ownership mutation;
- `Draining` keeps owner/generation/reservation unchanged;
- stale/faulted acquire or completion evidence cannot transfer/reclaim authority;
- `Faulted` never counts as closure;
- successful writable MOVE orders publication -> close -> acquire -> generation-changing transfer;
- target capability/binding failure is rejected before source drain;
- unsupported target direct mapping without copy policy remains local-only fallback and makes no zero-copy claim;
- bounded-copy limits are checked before source drain;
- bounded-copy policy cannot exist without an explicit target mapping request;
- bounded-copy fallback changes backing identity while preserving all bytes in the whole owned buffer;
- bounded-copy fallback changes `RegionGeneration` only through the single MOVE transfer;
- source and intermediate moved backing are invalid after successful rematerialization;
- target mapping publication failure does not masquerade as bounded-copy fallback;
- bounded-copy public evidence contains no provider/HybridCPU or hardware authority identity;
- external shared-read grants remain exactly read-only and cannot outlive verified closure;
- process teardown cannot bypass active/draining external grant authority.

## Phase-4 acceptance boundary

Phase 4 is complete when normal Sing guarantees plus the unchanged pinned HybridCPU gate prove all of:

```text
exact non-coherent mapping + publication + verified revoke
writable MOVE: publication -> Closed -> acquire -> RegionAuthority.Transfer generation++
read-only CPU borrow + external grant -> drain -> Closed -> exact revalidation -> borrow completion
failed target direct mapping + explicit bound -> whole-buffer bounded copy into fresh target backing
```

The bounded-copy fallback is an optimization/safety fallback, not a promise of universal copy, universal direct mapping or physical zero-copy.

## Remaining Phase-4 work

None. Phase 4 acceptance requirements are closed by the four slices above.

Further device/MMIO/IRQ/DMA work belongs to later roadmap phases. DMA/IOMMU were not introduced by this phase.

## Do not do

- no global shared-memory premise;
- no global coherence premise;
- no atomic-remap semantic requirement;
- no raw physical/PTE/page-table/IOMMU identifiers in SIP/kernel authority ABI;
- no mutable shared region without explicit single-writer/visibility protocol;
- no unconditional physical zero-copy guarantee;
- no DMA/IOMMU work in Phase 4.
