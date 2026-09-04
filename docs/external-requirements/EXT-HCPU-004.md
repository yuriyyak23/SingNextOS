# EXT-HCPU-004

**Status:** External Blocked

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

## Minimal reproduction

1. Allocate an `OwnedRegion<byte>` or `OwnedBuffer<byte>` in SingNextOS.
2. Bind its exact range through an integration-only platform adapter.
3. Prove an out-of-range or wrong-direction request is denied without changing local ownership.
4. Revoke/unmap the external binding and prove stale use is denied.
5. Transfer the region to another domain and prove the old domain's external binding cannot remain usable.
6. Terminate the owner domain and prove external access is closed before local reclaim is treated as complete.

## SingNextOS component blocked

HybridCPU-backed direct owned-region device/compute mapping, DMA and platform zero-copy rebinding. Local ownership transfer, borrow, region validation and host tests remain unblocked.

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
has bounded host/model DMA lifecycle tests and separately pinned grant-scoped
HybridCPU visibility integration, but the neutral runtime still has no executable
submit/completion/cancel surface. The Phase-6 completion event is therefore a
local notification over validated model evidence; it does not satisfy this
external DMA requirement.
