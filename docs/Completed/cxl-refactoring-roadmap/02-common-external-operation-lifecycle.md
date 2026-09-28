# Phase 2 — Common External Operation Lifecycle

Status: complete for the provider-neutral lifecycle and mock non-CXL vertical
slice. See
[02_PHASE2_IMPLEMENTATION_EVIDENCE.md](02_PHASE2_IMPLEMENTATION_EVIDENCE.md)
for the exact boundary and qualification evidence.

## Goal

Create one SingNextOS lifecycle for asynchronous DMA/accelerator/device-backed operations:

```text
Prepared
-> Admitted
-> Submitted
-> DeviceComplete
-> Visible
-> Published
-> Released
```

This lifecycle is the OS/runtime boundary. It must not depend on CXL and must not duplicate HybridCPU replay internals.

## State semantics

### `Prepared`

Descriptor/intent exists. No hardware authority has been materialized and no hardware effect is permitted.

### `Admitted`

SingNextOS has validated software authority and acquired all `RegionUse` objects. Required generation snapshots are captured. This state still does not mean a device has accepted work.

### `Submitted`

The provider has materialized/bound required hardware resources and accepted the request. Any hardware effect after this point is attributed to this operation generation.

### `DeviceComplete`

The device/provider reports completion. This is **not** equivalent to memory visibility, publication, or ownership return.

### `Visible`

Required provider-specific visibility/acquire/cache-maintenance rules have completed for the intended consumer domain. Data may still be private/staged and not logically published.

### `Published`

SingNextOS has revalidated authority/generations and performed the publication action. Consumers may now observe the result according to the normal memory/API contract.

### `Released`

Operation-specific platform bindings, leases, temporary mappings and region uses are released. This does not imply global device lease release unless the operation owns it.

## Key types

Provisional, provider-neutral contracts:

```text
ExternalOperationId
OperationGeneration
ExternalOperationState
OperationAdmissionSnapshot
OperationBinding
OperationCompletion
VisibilityRequirement
PublicationPlan
ReleasePlan
```

`OperationGeneration` is local identity for deduplication/cancellation/completion matching. It is not a new authority epoch.

## Required changes

1. Introduce a state machine with fail-closed transitions.
2. Capture region/device/platform generations at `Admitted` or immediately before `Submitted`, whichever is the last no-hardware-effect boundary.
3. Make provider submission return an opaque binding/receipt.
4. Separate completion observation from visibility processing.
5. Require authority/generation revalidation before `Published`.
6. Make cancellation semantics explicit per state.
7. Make release idempotent and resilient to device/fabric loss.

## Cancellation rules

- before `Submitted`: cancel with no hardware effect;
- after `Submitted`: request provider cancellation if supported; otherwise drain/quarantine result;
- after `DeviceComplete` but before `Published`: discard/quarantine staged result if revalidation fails;
- after direct coherent hardware writes: cancellation cannot pretend the write never occurred; this path requires a stronger publication/replay policy in Phase 7.

## Invariants

- no transition skips `Admitted`;
- `DeviceComplete` does not mutate logical ownership;
- `Visible` is provider/memory-domain visibility, not application publication;
- `Published` requires fresh authority/generation validation;
- `Released` cannot publish a result;
- duplicate or stale completions are matched by operation identity/generation and ignored/rejected safely.

## Negative tests

- submit without admission -> reject;
- completion for stale/wrong operation generation -> reject/quarantine;
- publish before device complete -> reject;
- publish after mutation epoch changed -> reject;
- visibility failure -> never publish;
- device reset after submit -> operation transitions to failed/reclaim path, not success;
- release after provider disappeared -> local authority/use cleanup still completes safely.

## Acceptance criteria

- at least one existing/mock non-CXL external operation is ported to this lifecycle;
- state transition tests cover every invalid edge;
- logs clearly distinguish `DeviceComplete`, `Visible`, `Published`, and `Released`;
- lifecycle code has no CXL-specific fields.

## External blockers

None.
