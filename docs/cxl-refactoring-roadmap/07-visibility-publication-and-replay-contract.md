# Phase 7 — Visibility, Publication and Replay Contract

Status: **Complete** for the SingNextOS effect/publication contract. See
[07_PHASE7_IMPLEMENTATION_EVIDENCE.md](07_PHASE7_IMPLEMENTATION_EVIDENCE.md).

## Goal

Define the SingNextOS side of replay-safe external execution without duplicating HybridCPU-v2 replay internals. The OS must classify external effects and guarantee correct staged publication boundaries for CXL-backed memory/accelerators.

## Core rule

CXL coherence is **not** rollback, replay, idempotence or publication. Coherence only answers a memory-consistency/coherence question for supported agents and scopes; it does not restore an earlier architectural state after an external effect.

## Provider-neutral effect classes

Prefer semantic effect classes rather than CXL-specific replay flags:

```text
StagedReversibleUntilPublish
SnapshotOrIdempotenceRequired
IrreversibleBarrier
```

Possible mapping examples:

### `StagedReversibleUntilPublish`

Device effects remain in private/staging state until SingNextOS performs publication. A stale authority/generation before publication can discard or quarantine the result.

### `SnapshotOrIdempotenceRequired`

The provider may have touched externally visible state, but retry is legal only with an explicit snapshot/restore, operation-idempotence or deduplication contract.

### `IrreversibleBarrier`

The operation can produce effects that cannot be undone/replayed safely by SingNextOS. Submission or the first irreversible effect becomes an external replay barrier for any higher-level runtime that requires deterministic replay.

The mapping from these classes to HybridCPU replay tokens/certificates is an external contract for the later HybridCPU-v2 roadmap.

## Lifecycle requirements

### `DeviceComplete`

Only states that the device/provider reports completion for the current operation generation.

### `Visible`

Provider-specific memory visibility/acquire/cache-maintenance obligations have completed for the intended consumer. For staged output, visibility may refer only to staging memory.

### `Published`

SingNextOS has:

1. revalidated region authority and `MutationEpoch`;
2. revalidated required device/fabric/platform generations;
3. confirmed the current operation generation and completion;
4. completed visibility actions;
5. performed the publication/copy/ownership transition required by the API.

### `Released`

Temporary resources are reclaimed. It does not erase previously published/irreversible effects.

## Direct coherent write policy

Direct coherent writes into final application-visible memory should normally be classified at least as `SnapshotOrIdempotenceRequired`, and as `IrreversibleBarrier` when the runtime cannot prove replay-safe recovery.

They may avoid staged copy only if:

- `RegionUse` excludes incompatible mutation;
- publication semantics allow data to become visible directly;
- completion/visibility rules are precise enough for the consumer;
- any replay consumer understands that the write may already have happened;
- reset/fabric failure recovery does not report a false rollback.

Do **not** automatically mark every coherent read or read-only shared mapping as a replay barrier. The effect classification follows externally visible side effects, not the CXL protocol name.

## Stale generation handling

### Before submit

Any stale required generation -> fail closed before new hardware effect; re-admit/rebind if policy permits.

### After submit, before staged publication

Stale required generation -> drain/cancel/quarantine output; do not publish.

### After direct/irreversible effect

Stale state -> fault/recovery path. The system must not claim the effect was rolled back. A higher-level replay engine must be told the operation crossed the applicable external-effect boundary.

## Publication fence/visibility integration

Extend existing `PlatformMemoryVisibility`, `CoherentAccess`, `PublicationFence`, and `CacheMaintenance` concepts only as necessary to represent actual CXL-backed provider requirements. Avoid introducing `CxlFence` if an existing provider-neutral visibility/fence contract can express the needed semantics.

## Negative tests

- CXL-coherent capability present but no effect classification -> submission rejected for replay-sensitive context;
- direct write followed by device reset -> never transition through fake staged rollback;
- staged result completes after output region mutation -> no publication;
- visibility operation fails -> no publication;
- duplicate completion after publication -> no second publication;
- read-only coherent operation is not forced into irreversible barrier class without an external side effect.

## Acceptance criteria

- effect classification is provider-neutral;
- staged Type-2 fake provider can demonstrate fail-closed stale publication;
- direct coherent tests explicitly assert irreversible/already-visible behavior;
- SingNextOS exposes enough lifecycle/effect information for later HybridCPU integration without importing HybridCPU replay tokens or certificates.

## External blockers

Final mapping into HybridCPU replay behavior is outside this repository and must be completed in the separate HybridCPU-v2 roadmap.
