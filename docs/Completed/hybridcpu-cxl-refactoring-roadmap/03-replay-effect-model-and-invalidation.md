# Phase 3 — Replay Effect Model and Invalidation

## Goal

Connect SingNextOS external-operation lifecycle and effect classification to HybridCPU replay/deterministic execution without confusing replay evidence with authority to re-submit external side effects.

## Required effect classes

HybridCPU should consume provider-neutral external effect classes compatible with the SingNextOS contract:

```text
StagedReversibleUntilPublish
SnapshotOrIdempotenceRequired
IrreversibleBarrier
```

These are replay-policy inputs, not OS authority.

## ReplayToken relationship

`ReplayToken` remains the bounded rollback artifact for HybridCPU architectural state. It must not silently promise rollback of external device effects.

For an external operation, store only replay-relevant correlation such as:

- operation/descriptor identity;
- effect class;
- whether submit occurred;
- whether DeviceComplete/Visible/Published occurred;
- admission generation snapshot identity;
- optional provider idempotence/snapshot handle when the runtime contract explicitly supports one.

Do not store raw CXL topology.

## Replay policy by lifecycle stage

### Before submit

Replay/reschedule is allowed after normal CPU guards and fresh OS admission.

### Submitted but not complete

Replay must not duplicate the hardware effect. Policy must choose one of:

- await original operation;
- cancel under an explicitly supported cancellation contract and re-admit;
- treat as replay barrier.

### DeviceComplete but not Visible/Published

Do not re-submit. Continue visibility/publication handling for the original operation or fail according to stale/reset policy.

### Published

External architectural effect is committed. Replay may continue only from a checkpoint/model that includes that published effect, or the operation is an irreversible replay boundary.

## LoopBuffer / replay certificate integration

Replay-phase and structural certificates may prove that CPU-side scheduling/reuse conditions are unchanged. They do not prove:

- provider availability;
- region ownership;
- current SingNextOS generation;
- permission to execute again;
- idempotence of device side effects.

Any replay-cache key involving external operations must include a semantic external-effect identity/invalidation component, not a CXL port/device route.

## Invalidation events

Invalidate external replay reuse on:

- owner/domain guard change;
- region mutation generation change;
- device reset/generation change;
- mapping/domain generation change when used by the concrete operation;
- fabric binding generation change;
- descriptor identity change;
- provider capability change that changes visibility/publication/effect semantics;
- cancellation/fault that makes prior receipts non-reusable.

## Direct coherent writes

Treat direct coherent device writes as `IrreversibleBarrier` by default. Promote to a weaker class only after an explicit design proves snapshot/idempotence/rollback semantics across the actual provider path.

## Tests

Add duplicate-submit prevention, replay-after-submit, replay-after-DeviceComplete, stale-generation replay, cancellation/re-admission, and published-effect barrier tests.

## Exit criteria

No replay path can cause a second external hardware effect solely because a replay certificate or `ReplayToken` is valid.