# Phase 1 — MutationEpoch and RegionUse Foundation

Status: complete for the provider-neutral, whole-region-conservative foundation.
See [01_PHASE1_IMPLEMENTATION_EVIDENCE.md](01_PHASE1_IMPLEMENTATION_EVIDENCE.md)
for the exact implementation boundary and qualification evidence.

## Goal

Introduce provider-neutral mutation and active-use tracking for memory regions before any CXL-specific code. This closes the most important safety gap for coherent/shared memory: a region can remain physically reachable while its logical ownership/use contract has changed.

## Key contracts

### `MutationEpoch`

A monotonically changing value scoped to one logical region (or the smallest existing region-authority object that can independently change). It records changes that make prior operation assumptions stale.

It changes on at least:

- ownership MOVE/rebind;
- mutable borrow grant/revoke;
- incompatible CPU writer activation;
- device-writable binding activation/revocation;
- memory replacement/remap that changes the backing identity relevant to the operation;
- forced reclaim/fault transition.

It should **not** change for unrelated region activity or read-only observations.

### `RegionUse`

A provider-neutral reservation describing how an operation may access a region. Minimum dimensions:

```text
ReadOnly
ExclusiveWrite
StagedOutput
DirectCoherentWrite
DevicePrivate
SharedReadMostly   // future-gated; not multi-host authority by itself
```

The exact enum/type may differ, but compatibility must be explicit rather than inferred from `CoherentAccess`.

A `RegionUse` binds:

- exact `RegionAuthority` identity/generation;
- access range;
- read/write direction;
- mutation snapshot;
- owner/principal;
- lifetime/cancellation handle;
- provider-visible binding only through an opaque platform handle.

## Required changes

1. Add a region mutation counter/generation to the region authority owner rather than to CXL code.
2. Make MOVE/borrow/reclaim transitions bump it where prior mutable-operation assumptions become stale.
3. Add an explicit region-use acquisition API.
4. Define a compatibility matrix between CPU use, DMA, coherent accelerator access and staged output.
5. Make release idempotent and ownership-safe.
6. Add diagnostics that expose logical region/use IDs but not CXL HDM/DPA/route identities to applications.

## Invariants

- `RegionUse` never grants authority beyond the supplied `RegionAuthority`.
- Hardware coherence cannot create or extend a `RegionUse`.
- A stale `MutationEpoch` cannot be refreshed in place; the operation must re-admit/rebind.
- Exclusive/direct device write excludes incompatible CPU/device writers for the same range.
- Read-only sharing does not imply permission to publish a later write.
- Releasing a use does not by itself return MOVE ownership; ownership transfer remains a separate authority operation.

## Negative tests

- acquire device write use with read-only `RegionAuthority` -> reject;
- MOVE region after use capture -> old use fails revalidation;
- grant incompatible CPU mutable borrow while device exclusive write use is active -> reject;
- revoke mutable borrow between submit and publish -> publish fails closed;
- double release -> no authority resurrection;
- stale use after reclaim -> reject before any new hardware submission;
- two disjoint ranges may coexist if the underlying authority model supports range-safe subdivision; overlapping incompatible ranges may not.

## Acceptance criteria

- existing non-CXL memory/DMA tests continue to pass;
- a fake external provider can acquire/release uses without CXL types;
- mutation invalidation is covered by deterministic tests;
- no application-visible type contains CXL-specific identities;
- `RegionUse` can be reused by later CXL Type-3, Type-2, ordinary DMA and future heterogeneous providers.

## External blockers

None. This phase is entirely inside SingNextOS and must be implementable without CXL hardware.
