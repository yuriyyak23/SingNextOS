# Phase 4 — Memory, ownership, mapping and coherence

## Status

**In progress — three real vertical slices implemented.**

The phase now has:

1. exact non-coherent owned-region mapping, publication evidence and completion-backed revoke;
2. a two-domain `OwnedBuffer<T>` MOVE handoff with post-close acquire for writable external mappings;
3. a CPU `BorrowLease<T>` -> bounded external shared-read grant lifecycle with verified closure before borrow completion.

The current cross-repository integration gate remains pinned to HybridCPU neutral acquire commit `4dce496d072b56efae61dfa9d99058eaa782fea3`, stacked on the exact mapping owner from PR #7. The borrow/shared-read slice needs no new HybridCPU export: it reuses the existing exact read-only mapping, `PublicationFence`, revoke and completion primitives.

Phase 4 is **not** complete. A true bounded-copy fallback remains separate work. DMA is still out of scope.

## Goal

Preserve SingNextOS ownership semantics while making owned memory safely visible across HybridCPU execution domains without assuming universal coherence or physical zero-copy.

The semantic contract remains:

```text
small values          -> copy
large mutable payload -> MOVE exclusive authority
temporary access      -> revocable borrow/shared grant
external access       -> exact bounded mapping/grant
completion            -> close/acquire/return authority
```

`MOVE` is an authority transfer. It does **not** promise atomic page-table remap, same physical backing, or physical zero-copy.

## Slice 1 — exact non-coherent mapping

Implemented before the later handoff slices:

- exact `PlatformRegionSlice` with region generation, owner, byte length, offset, length and access;
- local capability/owner/generation/bounds checks before provider admission;
- real neutral HybridCPU exact mapping with independent mapping identity;
- explicit non-coherent `PublicationFence` semantics;
- completion-backed exact revoke;
- local `PlatformMappingReserved` release only after valid `Closed` evidence and local generation/owner revalidation.

The whole Sing region remains conservatively reserved while one ordinary exact external slice mapping exists, so MOVE, borrow and release are blocked until closure.

## Slice 2 — two-domain MOVE handoff

### 1. Handoff order

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

### 2. Publication and acquire are different evidence classes

Producer publication remains the existing mapping visibility surface.

For writable external mappings, closure alone is not enough to let CPU ownership move. The MOVE slice adds a separate mapping-bound acquire contract:

```text
PlatformMemoryAcquireRequirement.AcquisitionFence
PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied
PlatformRegionAcquireRequest / Result
IPlatformRegionAcquireProvider
```

The `ExplicitMemoryVisibility` feature family is contract v3. Publication request/result shapes remain compatible; v3 additionally means the provider can expose the sibling post-close acquire contract.

Acquire evidence is valid only for the exact provider mapping ID/generation, exact Sing region slice and exact producer class. It is evidence, not authority.

### 3. Neutral HybridCPU acquire owner

The narrow `HybridCPU_NeutralRuntime` dependency provides the post-close acquire operation used by writable MOVE:

```text
exact non-coherent mapping live
  -> exact close
  -> mapping revoked
  -> domain still live
  -> AcquisitionFence
  -> AcquisitionFenceSatisfied
```

Acquire before close is rejected. Stale mapping epochs and revoked domains cannot manufacture acquire evidence. Duplicate acquire is idempotent evidence and does not create new mapping or ownership authority.

No physical address, page-table/PTE, cache-line, IOMMU/DMA, VMX/VMCS, lane or opcode identity is exported.

### 4. Draining retry semantics

If provider completion remains non-terminal:

```text
MOVE -> PlatformBindingDraining
```

The old `OwnedBuffer<T>` stays valid, region generation does not change, and `PlatformMappingReserved` remains pinned. A later retry may continue the same handoff only when the drain was started by that MOVE path.

A drain started by unrelated teardown/revocation is not silently adopted as MOVE proof.

### 5. Writable vs read-only source mappings

Read-only external mappings cannot have external writes, so exact `Closed` plus source publication is enough before local MOVE.

Writable external mappings additionally require post-close acquire evidence. If acquire is unsupported, stale, wrong-domain, malformed or faulted:

- MOVE fails closed;
- source owner/generation remain unchanged;
- reservation remains pinned;
- direct `RegionAuthority.Transfer()` remains blocked.

`Faulted` or `Closed` by itself never becomes acquire authority.

### 6. Optional target external exposure

A MOVE may optionally request an exact target mapping for the **new** region generation.

Target binding/capability rights are checked before source drain begins. The actual target map can only be materialized after `RegionAuthority.Transfer()` because provider mapping authority must commit the new owner and new `RegionGeneration`.

If target exact mapping or target publication is unsupported after the local MOVE has completed, the result reports:

```text
PlatformMoveTargetExposureState.LocalOwnershipFallback
```

The MOVE remains successful because local ownership already belongs exclusively to the target. No direct external access or physical zero-copy claim is published. This is a legal local-ownership fallback, not a claim that bytes were copied.

A true bounded-copy fallback is still separate work.

## Slice 3 — CPU borrow + external shared-read grant

### 1. Separate lifetime and identity spaces

`BorrowLease<T>` remains the CPU-local read-only lifetime object. It is never passed to the provider and never becomes a provider mapping token.

The bridge derives a separate local grant identity:

```text
BorrowLeaseHandle / BorrowLeaseGeneration
  != PlatformBorrowReadGrantId / PlatformBorrowReadGrantGeneration
  != PlatformRegionMappingId / PlatformRegionMappingGeneration
  != PlatformProviderRegionMappingId / PlatformProviderLeaseGeneration
  != NeutralOwnedRegionMappingHandle / epoch
```

The public grant surface carries only Sing/bridge-local identities, the exact platform domain binding, the CPU borrow identity, and the exact byte range. Provider mapping leases and completion operations remain bridge-private evidence.

### 2. Admission contract

A grant can be created only from an exact live CPU borrow:

```text
exact owner process generation
  + exact borrower process generation
  + exact BorrowLeaseHandle generation
  + exact RegionHandle / RegionGeneration
  + exact RegionOwner
  + live BorrowLeaseLifetime
  + exact PlatformDomainBinding / generation for the borrower
  + exact bounded byte range
  -> hidden provider mapping with Read access only
  -> separate PlatformBorrowReadGrant identity
```

The external provider mapping is always `PlatformMemoryAccess.Read`. There is no grant API that accepts `Write`, no MOVE/release authority is carried by the grant, and a normal owned-region mapping cannot be admitted while the region is in `Loaned` state.

The CPU borrower and external reader may therefore coexist only as readers. Owner mutable access is already suppressed by the existing CPU borrow lifetime.

### 3. Publication before external effects

External use requires mapping-bound publication:

```text
PlatformMemoryConsumerClass.ExternalExecutionDomain
  + PlatformMemoryVisibilityRequirement.PublicationFence
  -> PublicationFenceSatisfied
```

The resulting `PlatformBorrowReadGrantEvidence` contains only the local grant identity plus the semantic visibility outcome. It contains no provider mapping ID, HybridCPU token, operation ID, physical address or other authority-shaped evidence.

Once revoke starts, the hidden mapping is `Draining`; the existing mapping validator rejects new visibility/effects before another provider call.

### 4. Completion / revoke ordering

Borrow completion is explicit and fail-closed:

```text
local owner + CPU BorrowLease live
  -> external read grant Active
  -> RequestPlatformBorrowCompletion
  -> hidden exact mapping Draining
  -> no new external effects
  -> exact completion observation
  -> Closed required
  -> exact local revalidation
       BorrowLease identity + generation + exact lifetime object
       RegionHandle + RegionGeneration
       owner DomainId + owner process generation
       borrower DomainId + borrower process generation
       external grant id + generation + exact range
       platform binding id + generation + subject
       hidden mapping id + generation + exact read-only slice
  -> release external-borrow reservation
  -> reclaim bridge-private mapping/grant metadata
  -> only now RegionAuthority.ReturnLoan()
       invalidate BorrowLeaseLifetime
       region Loaned -> Owned
```

A non-terminal completion leaves the grant `Draining` and the CPU borrow live. `Faulted` is never closure and never permits borrow completion or metadata reclaim.

### 5. RegionAuthority interlock

`RegionAuthority` owns the local interlock for this authority relationship. While the external read grant is active or draining:

- `ReturnLoan` is blocked;
- owner `RevokeLoan` is blocked;
- domain-level loan/region reclaim refuses to bypass the grant;
- ownership transfer/release cannot become legal through early borrow completion;
- a second external grant or normal platform mapping reservation cannot reuse the same borrow lifetime.

The bridge can release the reservation only after verified `Closed` plus exact local borrow/region/owner/binding revalidation. Evidence never becomes ownership authority.

### 6. No new HybridCPU capability required

The existing neutral runtime already has the minimal primitives needed for this read-only slice:

- exact bounded read mapping;
- explicit non-coherent `PublicationFence`;
- exact mapping close;
- completion-backed `Closed` evidence through the Sing provider bridge.

Because the external grant is read-only, there is no external producer and no post-close `AcquisitionFence` requirement for this lifecycle. The existing acquire primitive remains required for writable MOVE, not for shared-read grant closure.

No HybridCPU-v2 source change is required by this slice.

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

None are interchangeable.

`PlatformRegionVisibilityEvidence`, `PlatformRegionAcquireEvidence`, `PlatformBorrowReadGrantEvidence`, completion receipts and feature discovery are evidence only. `RegionAuthority` remains the sole Sing ownership authority.

## Negative guarantees

The combined Phase-4 tests must prove, at minimum:

### MOVE

- target capability/binding denial happens before source revoke;
- source publication failure happens before source revoke;
- `Draining` keeps owner/generation/reservation unchanged;
- stale acquire evidence cannot release reservation;
- unsupported acquire on writable mapping cannot MOVE;
- read-only external mapping does not require acquire;
- successful writable path orders publication -> close -> `Closed` -> acquire -> generation-changing transfer;
- old source `RegionHandle` is stale after MOVE;
- target mapping is bound to the new region generation;
- unsupported target mapping yields local-ownership fallback without rollback or zero-copy claim;
- real neutral HybridCPU acquire requires exact close and rejects stale generations.

### Borrow/shared-read

- external grant cannot be created without a live exact CPU borrow;
- stale region, borrow, process or platform-binding generation fails before external admission;
- grant identity is separate from borrow, region and provider mapping identity spaces;
- grant access is always exactly `Read` and the range is exact/bounded;
- MOVE/release/borrow return/owner revoke cannot bypass an active or draining grant;
- completion request starts drain without completing the CPU borrow;
- new external visibility/effects are rejected after drain starts;
- every non-terminal completion remains insufficient;
- stale/wrong-domain/wrong-operation/malformed closure evidence fails closed;
- `Faulted` remains non-reclaimable;
- only valid `Closed` plus exact local revalidation allows grant metadata reclaim and CPU borrow completion;
- a reclaimed grant cannot create later evidence/effects;
- public SIP/kernel-facing grant surfaces expose no provider/HybridCPU mapping ID or hardware-shaped authority identity.

## Remaining Phase-4 work

### True bounded-copy fallback

When direct target mapping cannot be materialized, a future fallback may copy through a kernel/service-owned bounded region. That path must preserve the same high-level MOVE exclusivity and never allow both old and new owners to remain live.

This fallback is intentionally **not** implemented by the borrow/shared-read PR.

## Acceptance boundary

The borrow/shared-read iteration is complete when the normal Sing guarantees prove:

```text
live CPU borrow
  -> exact read-only external grant
  -> PublicationFence evidence
  -> drain / no new effects
  -> exact Closed completion
  -> exact local borrow + region + owner + binding revalidation
  -> bridge-private grant metadata reclaimed
  -> CPU borrow returned
```

The existing pinned HybridCPU gate remains relevant because the provider exact mapping/publication/revoke primitives are exercised against the pinned neutral runtime; this iteration adds no new external commit dependency.

Full Phase 4 remains **In progress** until the true bounded-copy fallback is implemented or explicitly dispositioned by the roadmap.

## Do not do

- no global shared-memory premise;
- no global coherence premise;
- no atomic-remap semantic requirement;
- no raw physical/PTE/page-table/IOMMU identifiers in SIP/kernel authority ABI;
- no mutable shared region without explicit single-writer/visibility protocol;
- no unconditional physical zero-copy guarantee;
- no DMA in this iteration.
