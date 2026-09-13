# EXT-HCPU-004

**Status:** External Blocked

## Local boundary now implemented

SingNextOS locally serializes bounded DMA and DSC1 admission/release for the
same exact `PlatformRegionMapping` identity. `RuntimeKernel` supplies a private
coarse platform-memory-use gate, while the bridge derives active conflict state
from its existing DMA/DSC1 lifecycle ledgers under their private gates. There is
no parallel authority registry. DMA operation identity, DSC1 submission
identity, mapping generation and provider continuations remain separate, and
the interlock adds neither a public token nor a provider ABI.

The policy is deliberately whole-mapping exclusive across mechanisms: any
active, ambiguously accepted or submit-path invariant-faulted DMA use conflicts
with DSC1 source or destination use of that same mapping, including read/read
and non-overlapping byte subranges. Accepted lifetimes on distinct independently
authorized mappings may overlap, although their admission calls pass through
the coarse local gate. Current `RegionAuthority` permits one live platform
mapping per owned region, so this disjoint case does not authorize aliases of
the same region.

An ordinary pre-accept submit denial leaves no persistent lifecycle pin: DSC1
removes its provisional record, while DMA exits the serialized admission
window without publishing one. DMA retains its pin through completion until
exact direction-aware post-completion visibility finishes. DSC1 retains both
pins through exact completed/cancelled settlement,
local publication or discard, buffer-lease release and local release commit.
Pending or denied terminal observation retains the existing pin; malformed,
faulted, thrown or ambiguously accepted provider state retains the exact use
where it remains identifiable, or quarantines the containing platform domain
when exact identity cannot be retained. These are local admission/reclaim
rules, not evidence that an external IOMMU, DMA engine or accelerator enforces
the same lifetime.

The local model/test contour assumes provider calls made while the coarse
private gates are held are bounded and do not wait on cross-thread re-entry.
Host supplies DSC1 `ModelOnly`; combined DMA↔DSC1 behavior is exercised by a
faithful test provider and is not Host executable-DMA evidence. A stalled or
re-entrant executable provider could delay disjoint admission, so a future
executable binding needs a bounded provisional per-operation
reservation/reconciliation protocol rather than this locking shape.

## Required external capability

The existing HybridCPU platform integration must provide exact, revocable mapping/binding of a SingNextOS owned region into the platform memory/I/O authority needed for direct device access, DMA, compute and zero-copy ownership rebinding.

## Why SingNextOS needs it

SingNextOS already has generation-bound `RegionHandle`, ownership transfer and revocable borrow semantics. To make those regions hardware-visible without weakening security, the kernel needs an external mapping contract that preserves owner/domain/range/lifetime constraints.

A local `DmaCapability` or `RegionHandle` is not by itself an IOMMU/platform grant. Conversely, an external mapping token must not become a process-visible SingNextOS capability.

## Existing interface expected

An already existing or externally supplied HybridCPU platform/runtime interface that can:

- map an exact region/range into a neutral memory/I/O domain;
- constrain direction/access rights;
- revoke/unmap the binding;
- detect stale domain/mapping generation;
- drain or reject outstanding device/compute work before rebind/reclaim;
- support rebinding required by ownership transfer when the platform permits it;
- report unsupported coherency/direct-access modes explicitly.
- independently reject or drain conflicting DMA/compute use for the exact
  external mapping lifetime before it is rebound or reclaimed.

## Minimal reproduction

1. Allocate an `OwnedRegion<byte>` or `OwnedBuffer<byte>` in SingNextOS.
2. Bind its exact range through an integration-only platform adapter.
3. Prove an out-of-range or wrong-direction request is denied without changing local ownership.
4. Revoke/unmap the external binding and prove stale use is denied.
5. Transfer the region to another domain and prove the old domain's external binding cannot remain usable.
6. Terminate the owner domain and prove external access is closed before local reclaim is treated as complete.

## SingNextOS component blocked

HybridCPU-backed direct owned-region device/compute mapping, DMA and platform
zero-copy rebinding. Local ownership transfer, borrow, region validation,
conservative cross-mechanism admission and host tests remain unblocked.

## Explicit non-request

This requirement does **not** ask for:

- universal coherent shared memory;
- a new IOMMU implementation;
- DSC2/queue support;
- new DMA instructions;
- raw physical addresses in public/SIP APIs.

If the external platform cannot provide direct mapping for a case, SingNextOS may copy through a bounded kernel/service region or fail closed.

## Fallback/mock used

Current `RegionAuthority`, `OwnedRegion<T>`, `OwnedBuffer<T>` and
`BorrowLease<T>` semantics remain authoritative for local ownership. SingNextOS
has bounded host/model DMA lifecycle tests and grant-scoped HybridCPU visibility
integration merged at `9e001bf...`, but the neutral runtime still has no executable
submit/completion/cancel surface. The Phase-6 completion event is therefore a
local notification over validated model evidence; it does not satisfy this
external DMA requirement. Cancelling its local waiter does not cancel or close
DMA authority and does not permit ownership return or reclaim.

Phase-7 Slice 3 additionally rejects simultaneous local DMA and DSC1 use of one
complete mapping identity and pins ambiguous/faulted lifetimes. It deliberately
does not reason about range or cache-line overlap and proves no cross-engine
coherence. DMA `Prepare` alone is not an active mapping use. The current model
also has no CPU/managed-alias mutation epoch that invalidates prepared DMA
evidence after an intervening CPU or DSC1 write but before submit. An executable
path must bind prepare evidence to a current mutation/visibility epoch or require
a fresh prepare; the existing local interlock must not be cited as that proof.
