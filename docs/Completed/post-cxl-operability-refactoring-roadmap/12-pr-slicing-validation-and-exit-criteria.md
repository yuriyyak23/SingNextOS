# Phase 12 — PR Slicing, Validation, and Final Exit Criteria

Status: implemented and qualified. The executable negative/end-to-end mapping is recorded in `12_FINAL_QUALIFICATION_MATRIX.md`; command evidence is in `12_PHASE_12_IMPLEMENTATION_EVIDENCE.md`.

## Goal

Define implementation order, keep reviews bounded, and establish the final negative-test matrix required to declare the post-CXL operability roadmap complete.

## Recommended implementation order

### Wave A — operational control

1. Phase 1: service definitions/manifests and shared contracts.
2. Phase 2: supervisor.
3. Phase 3: authority inspector.
4. Phase 4: deadlines/cancellation.
5. Phase 5: budgets.

This wave makes the system manageable and safe before performance/lifecycle conveniences are added.

### Wave B — performance and diagnosis

6. Phase 6: deterministic tracing/replay.
7. Phase 7: IPC v2.
8. Phase 9: telemetry/evidence projection.
10. Phase 10: provider conformance/fault injection may begin in parallel once shared lifecycle contracts stabilize.

### Wave C — lifecycle continuity and qualification

9. Phase 8: ordinary checkpoint/restore after supervisor, cancellation and budgets are stable.
11. Phase 11: integration/performance/hardening.
12. Phase 12: final qualification.

## PR slicing guidance

Prefer small contract-first PRs. Suggested slices:

- contract types + validation;
- model authority/state implementation;
- one integration consumer;
- negative tests;
- compatibility adapter/migration;
- performance benchmark changes.

Do not combine unrelated supervisor, IPC and checkpoint implementations into one large PR merely because they belong to the same roadmap.

## Mandatory migration rule

Existing code may temporarily coexist with new contracts behind adapters, but there must be one authoritative source per semantic fact.

Examples:

- one service generation source;
- one budget ledger;
- one IPC ownership transfer state;
- one checkpoint classification for a resource;
- one provider-effect closure truth.

Adapters may project state; they may not maintain a second mutable authority store.

## Full negative-test matrix

### Service/supervisor

- stale service generation rejected;
- hard dependency loss handled by policy;
- restart does not inherit old capabilities;
- crash loop bounded;
- ambiguous external effect blocks unsafe replacement.

### Inspector

- unauthorized cross-service inspection denied;
- sensitive host data redacted;
- stale/current nodes not conflated;
- projection cannot authorize mutation.

### Deadline/cancellation

- pre-effect cancellation closes cleanly;
- submit race deterministic;
- post-effect timeout does not release authority;
- stale cancellation scope rejected;
- published effect cannot be undone by cancellation.

### Budgets

- parent/child limit enforcement;
- concurrent reservation no overcommit;
- stale release no capacity corruption;
- live external effect keeps charge after crash;
- fresh replacement accounting.

### Trace/replay

- trace not authority;
- missing/incomplete trace marked explicitly;
- replay divergence detected;
- HybridCPU replay evidence cannot submit live provider effect.

### IPC v2

- exactly one owner after MOVE;
- borrow lifetime exact;
- timeout/crash race safe;
- scatter-gather bounds exact;
- optimized/copy path semantic equivalence.

### Checkpoint

- unsupported live resource blocks checkpoint;
- partial image invalid;
- restored generation fresh;
- old handles remain stale;
- manifest/schema incompatibility rejected.

### Telemetry

- self projection scoped;
- cross-tenant denied;
- host topology hidden by default;
- evidence/telemetry not interchangeable;
- stale subscription rejected.

### Provider conformance

- NotAccepted only for proven pre-effect rejection;
- ambiguous acceptance retains local pins/accounting;
- malformed receipt compensated or quarantined;
- stale closure cannot release current generation;
- exception after effect boundary fail-closed;
- reset/reconfiguration active-work behavior exact.

## End-to-end qualification scenarios

1. Normal service startup -> IPC -> external effect -> publication -> shutdown.
2. Dependency restart with exact rebinding.
3. Crash after external submission with successful cancellation/closure.
4. Crash after ambiguous acceptance -> quarantine and blocked unsafe replacement.
5. Deadline expiry at every ExternalOperation lifecycle stage.
6. Budget exhaustion and recovery after exact closure.
7. MOVE IPC under deadline and service crash races.
8. Trace + inspector explanation of blocked region reclaim.
9. Planned checkpoint-based replacement under new generation.
10. Provider reset while service running, with telemetry/trace and supervisor state remaining truthful.

## Documentation requirements

Every completed phase must update:

- public/contract docs;
- invariants and non-goals;
- lifecycle state diagrams;
- provider/conformance expectations where relevant;
- known FutureGated items;
- test evidence/counts only after tests actually run.

Do not state a test count or hardware qualification that was not executed.

## Final Definition of Done

The roadmap is complete when:

- all ten directions have executable implementations and negative tests;
- supervisor generation-safe replacement is production path for managed services;
- inspector/provenance diagnostics cover major authority and reclaim objects;
- common deadline/cancellation semantics are used by IPC and external effects;
- hierarchical budgets cover memory, IPC and external operations;
- tracing/replay gives causal reproducibility without granting authority;
- IPC v2 is ownership-correct and performance-measured;
- manifests drive requests/admission without becoming authority;
- ordinary checkpoint/restore creates fresh authority generations;
- telemetry/evidence projection enforces visibility boundaries;
- provider conformance framework qualifies at least two provider families;
- integrated fault/teardown/restart scenarios pass;
- no CXL/Virtualization/SecureCompute/HybridCPU invariant is weakened;
- remaining unsupported features are explicitly documented as FutureGated rather than implicitly emulated.
