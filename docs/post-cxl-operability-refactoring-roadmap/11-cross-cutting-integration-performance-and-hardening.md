# Phase 11 — Cross-cutting Integration, Performance, and Hardening

## Goal

Integrate the ten directions into one coherent operational model, remove duplicate lifecycle logic, establish performance baselines, and harden abuse/failure boundaries before final qualification.

## Integrated service lifecycle

Validate the complete path:

```text
manifest
 -> supervisor admission
 -> budget reservation
 -> process/capability materialization
 -> dependency binding
 -> IPC traffic
 -> external operations
 -> trace + telemetry
 -> health transition
 -> cancellation/drain
 -> optional checkpoint
 -> exact teardown
 -> replacement generation
```

Every stage must preserve independent authority, accounting and observation semantics.

## Lifecycle deduplication

Search for subsystem-local concepts that can now delegate to shared contracts:

- bespoke timeout booleans -> Phase 4 dispositions;
- local quota counters -> Phase 5 reservations;
- ad-hoc debug dumps -> Inspector/telemetry projections;
- provider-specific fault toggles -> Phase 10 fault plans;
- IPC-local ownership diagnostics -> provenance graph/trace correlation.

Do not refactor merely for naming uniformity if semantics differ; preserve exact resource-specific closure rules.

## Performance baselines

Measure at least:

- service start/replace latency;
- supervisor idle overhead;
- small-message IPC latency;
- MOVE/borrow throughput;
- budget check overhead;
- tracing disabled/enabled overhead;
- telemetry collection overhead;
- inspector snapshot cost;
- checkpoint throughput and pause/quiescence time;
- provider conformance suite runtime.

Record baseline environment and methodology. Avoid hard performance promises until measurements exist.

## Hot-path requirements

- supervisor absent from normal per-message fast path;
- inspector on-demand only;
- budget admission bounded by shallow hierarchy;
- tracing disabled path avoids global lock;
- telemetry aggregation avoids stop-the-world;
- IPC small-copy fast path remains simple;
- checkpoint work not present on ordinary execution path.

## Abuse resistance

Test and bound:

- restart storms;
- dependency flapping;
- trace/telemetry flood;
- oversized IPC queues/scatter lists;
- cancellation storms;
- checkpoint request storms;
- budget reservation leaks;
- inspector expensive graph queries;
- fault-injection access controls.

## Security review gates

Verify:

- no manifest/configuration authority confusion;
- no supervisor root authority leakage;
- no stale authority across replacement;
- no capability material persisted to trace/checkpoint/telemetry;
- no cross-tenant diagnostic leakage;
- no timeout/cancellation reclaim bug;
- no budget bypass via nested/service delegation;
- no replay-to-live-effect bypass.

## Compatibility review

Existing applications/services should continue to work through compatibility adapters where reasonable. New IPC/manifest/supervisor functionality should be opt-in until migration is proven.

CXL/Virtualization/SecureCompute contracts are treated as stable prerequisites and must not be weakened to simplify integration.

## Required integrated scenarios

- supervisor-managed service performs MOVE IPC, external compute, publication, telemetry and clean restart;
- same scenario with provider fault after acceptance quarantines correctly;
- same scenario with deadline expiry drains correctly;
- budget pressure degrades admission without corrupting authority;
- trace + inspector explain a blocked reclaim;
- planned replacement with ordinary checkpoint restores logical state under new generation;
- telemetry remains scoped throughout restart.

## Exit criteria

No unresolved cross-subsystem authority ambiguity remains; hot paths have measured baselines; abuse cases are bounded; integration scenarios pass under normal and injected-fault conditions.