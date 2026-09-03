# Phase 4 — Memory, ownership, mapping and coherence

## Status

**In progress — three real vertical slices implemented.**

The phase now has:

1. exact non-coherent owned-region mapping, publication evidence and completion-backed revoke;
2. a two-domain `OwnedBuffer<T>` MOVE handoff with post-close acquire for writable external mappings;
3. a CPU `BorrowLease<T>` -> bounded external shared-read grant lifecycle with verified closure before borrow completion or process reclaim.

The cross-repository integration gate remains pinned to HybridCPU neutral acquire commit `4dce496d072b56efae61dfa9d99058eaa782fea3`, stacked on the exact mapping owner from PR #7. The borrow/shared-read slice needs no new HybridCPU export: it reuses the existing exact read-only mapping, `PublicationFence`, revoke and completion primitives through the current Sing provider.

Phase 4 is **not** complete. A true bounded-copy fallback remains separate work. DMA/IOMMU remain out of scope.

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

### Handoff order

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

### Publication and acquire are different evidence classes

Producer publication remains the existing mapping visibility surface. Writable external mappings additionally require a separate mapping-bound post-close acquire contract:

```text
PlatformMemoryAcquireRequirement.AcquisitionFence
PlatformMemoryAcquireOutcome.AcquisitionFenceSatisfied
PlatformRegionAcquireRequest / Result
IPlatformRegionAcquireProvider
```

Acquire evidence is valid only for the exact provider mapping ID/generation, exact Sing region slice and exact producer class. It is evidence, not authority.

The narrow `HybridCPU_NeutralRuntime` dependency provides the matching post-close acquire operation. Acquire before close is rejected; stale mapping epochs and revoked domains cannot manufacture acquire evidence. Duplicate acquire is idempotent evidence and does not create ownership authority.

Read-only source mappings have no external writer and therefore need exact `Closed` plus source publication but no acquire fence. Writable mappings fail closed if acquire is unsupported, stale, wrong-domain, malformed or faulted.

### Optional target exposure and fallback

A MOVE may optionally request an exact target mapping for the **new** region generation. Target binding/capability rights are checked before source drain begins; materialization occurs only after `RegionAuthority.Transfer()` commits the new owner/generation.

If target mapping/publication cannot be materialized after the local transfer, the result is:

```text
PlatformMoveTargetExposureState.LocalOwnershipFallback
```

This means the target owns the region locally. It is not a claim that the bytes were copied and not a physical zero-copy guarantee. A true bounded-copy fallback remains separate work.

## Slice 3 — CPU borrow + external shared-read grant

### 1. Separate lifetime and identity spaces

`BorrowLease<T>` remains the CPU-local read-only lifetime object. It is never passed to the provider and never becomes a provider mapping token.

The bridge derives a separate grant identity:

```text
BorrowLeaseHandle / BorrowLeaseGeneration
  != PlatformBorrowReadGrantId / PlatformBorrowReadGrantGeneration
  != PlatformRegionMappingId / PlatformRegionMappingGeneration
  != PlatformProviderRegionMappingId / PlatformProviderLeaseGeneration
  != NeutralOwnedRegionMappingHandle / epoch
```

The grant surface carries only Sing/bridge-local identities, the exact platform domain binding, the CPU borrow identity and exact byte range. Provider mapping leases, provider operations and HybridCPU mapping handles remain bridge/provider-private evidence.

### 2. Owner-bound external execution domain

The real HybridCPU provider already enforces an important neutral invariant:

```text
PlatformRegionSlice.Region.Owner
  == PlatformProviderDomainLease.Subject
```

Therefore the minimal reusable shared-read slice binds the external execution domain to the **exact Sing region owner**, not to the CPU borrower. No cross-owner provider grant/export is introduced.

This yields three deliberately distinct roles:

```text
Sing owner
  -> remains RegionAuthority owner, but mutable CPU access is suppressed by BorrowLease lifetime
CPU borrower
  -> local read-only BorrowLease<T>
owner-bound external execution domain
  -> separate exact bounded read-only grant
```

The CPU borrower and external reader may coexist only as readers. The platform binding is revalidated against the exact owner `DomainId + process generation` and binding generation.

### 3. Admission contract

A grant can be created only from an exact live CPU borrow:

```text
exact owner process generation
  + exact borrower process generation
  + exact BorrowLeaseHandle / BorrowLeaseGeneration
  + exact RegionHandle / RegionGeneration
  + exact RegionOwner
  + exact live BorrowLeaseLifetime object
  + exact owner-bound PlatformDomainBinding / generation
  + exact bounded byte range
  -> hidden provider mapping with Read access only
  -> separate PlatformBorrowReadGrant identity
```

Local stale/wrong-owner/wrong-domain/range failures happen before provider admission. Provider denied/revoked/faulted/malformed mapping results fail closed and roll back the local grant reservation.

The provider mapping is always `PlatformMemoryAccess.Read`. There is no grant API accepting `Write`; the grant carries no MOVE/release authority. A normal owned-region writable mapping cannot be admitted while the region is in `Loaned` state.

### 4. Publication before external use

External reader admission requires mapping-bound visibility evidence:

```text
PlatformMemoryConsumerClass.ExternalExecutionDomain
  + PlatformMemoryVisibilityRequirement.PublicationFence
  -> PublicationFenceSatisfied
```

`PlatformBorrowReadGrantEvidence` exposes the local grant plus semantic visibility outcome only. It contains no provider mapping ID, provider operation ID, HybridCPU token or hardware-shaped authority identity.

Denied, revoked, faulted or malformed publication evidence fails closed. Once revoke begins, the hidden mapping is `Draining`; existing mapping validation rejects new visibility/effects before another provider call.

### 5. Completion / revoke ordering

Normal borrow completion is explicit and fail-closed:

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
       owner-bound platform binding id + generation + subject
       hidden mapping id + generation + exact read-only slice
  -> release external-borrow reservation
  -> reclaim bridge-private exact mapping/grant metadata
  -> only now RegionAuthority.ReturnLoan()
       invalidate BorrowLeaseLifetime
       region Loaned -> Owned
```

A non-terminal completion leaves the grant `Draining` and the CPU borrow live. Stale/wrong-domain/wrong-operation/malformed closure evidence cannot release the reservation. `Faulted` is never closure and never permits borrow completion or grant reclaim.

### 6. RegionAuthority interlock

`RegionAuthority` remains the sole Sing ownership authority and owns the local interlock for this relationship. While the external read grant is active or draining:

- `ReturnLoan` is blocked;
- owner `RevokeLoan` is blocked;
- ownership transfer/release cannot become legal through early borrow completion;
- a second grant cannot reuse the same borrow lifetime;
- normal platform mapping reservation cannot reuse the borrowed region;
- aggregate domain loan/region reclaim refuses to bypass the grant.

The bridge can release the reservation only after verified `Closed` plus exact local borrow/region/owner/binding/mapping revalidation. Completion and visibility evidence never become ownership authority.

### 7. Existing process teardown owns closure too

The grant is registered with the existing process teardown orchestrator so a new authority class cannot bypass Phase-2 closure rules.

For borrower exit:

```text
Exiting
  -> close/drain external read grant using owner-bound platform identity
  -> Closed + exact revalidation
  -> reclaim grant metadata
  -> ReturnLoan
  -> borrower local reclaim
```

For owner exit:

```text
Exiting
  -> close/drain external read grant
  -> Closed + exact revalidation
  -> reclaim grant metadata
  -> RevokeLoan
  -> revoke owner platform domain
  -> owner region reclaim
```

If closure is still non-terminal, process teardown remains `PlatformDraining`. If grant closure faults, process teardown becomes fault-contained and local reclaim remains forbidden.

### 8. No new HybridCPU capability required

The existing neutral runtime already has the minimal primitives needed for this read-only slice:

- exact bounded read mapping;
- explicit non-coherent `PublicationFence`;
- exact mapping close;
- completion-backed `Closed` evidence through the Sing provider bridge.

Because the external grant is read-only, there is no external producer and no post-close `AcquisitionFence` requirement for this lifecycle. The existing acquire primitive remains required for writable MOVE, not for shared-read grant closure.

No HybridCPU-v2 source change is required. The existing pinned cross-repository workflow adds a real provider/runtime integration test for the new borrow/read-grant path.

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

Compatibility projections remain downstream. No provider/HybridCPU mapping ID or hardware-shaped identity is introduced into the SIP/kernel ownership authority ABI.

## Negative guarantees

### MOVE slice

Tests continue to prove:

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

### Borrow/shared-read slice

Focused tests prove:

- external grant cannot be created without a live exact CPU borrow;
- stale region, borrow, owner/borrower process or platform-binding generation fails before provider admission;
- wrong owner-bound platform domain fails before provider admission;
- grant identity is separate from borrow, region, bridge mapping and provider mapping identity spaces;
- grant access is always exactly `Read` and the range is exact/bounded;
- denied/revoked/faulted/malformed mapping or publication evidence fails closed;
- MOVE/release/borrow return/owner revoke cannot bypass an active or draining grant;
- completion request starts drain without completing the CPU borrow;
- new external visibility/effects are rejected after drain starts;
- every non-terminal completion remains insufficient;
- stale/wrong-domain/wrong-operation/malformed closure evidence fails closed;
- `Faulted` remains non-reclaimable;
- only valid `Closed` plus exact local revalidation allows grant metadata reclaim and CPU borrow completion;
- reclaimed grants cannot create later evidence/effects;
- borrower and owner process teardown both close the grant before local borrow/region reclaim, including a draining retry case;
- the pinned real HybridCPU provider/runtime completes an actual CPU borrow + exact read grant + publication + close + return path;
- public grant surfaces expose no provider/HybridCPU mapping ID or hardware-shaped authority identity.

## Remaining Phase-4 work

### True bounded-copy fallback

When direct target mapping cannot be materialized, a future fallback may copy through a kernel/service-owned bounded region. That path must preserve the same high-level MOVE exclusivity and never allow both old and new owners to remain live.

This fallback is intentionally **not** implemented by the borrow/shared-read PR.

## Acceptance boundary

The borrow/shared-read iteration is complete when normal Sing guarantees plus the existing pinned HybridCPU gate prove:

```text
live CPU borrow
  -> exact owner-bound read-only external grant
  -> PublicationFence evidence
  -> drain / no new effects
  -> exact Closed completion
  -> exact local borrow + region + owner + binding revalidation
  -> bridge-private grant metadata reclaimed
  -> CPU borrow returned/revoked before local reclaim
```

Full Phase 4 remains **In progress** until the true bounded-copy fallback is implemented or explicitly dispositioned by the roadmap.

## Do not do

- no global shared-memory premise;
- no global coherence premise;
- no atomic-remap semantic requirement;
- no raw physical/PTE/page-table/IOMMU identifiers in SIP/kernel authority ABI;
- no mutable shared region without explicit single-writer/visibility protocol;
- no unconditional physical zero-copy guarantee;
- no DMA/IOMMU work in this iteration.
