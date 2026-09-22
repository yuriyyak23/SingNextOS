# ТЗ P14 — Scalability: measure before sharding

    **Depends on:** P10 (measurement can run in parallel with P11-P13)  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `DEFERRED`.

    ## CD-P14-01 — Measure contention before redesign

- **Phase:** P14
- **Classification:** `NEW_PROPOSED`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs; src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs`
- **Exact current symbols:** `ResourceBudgetAuthority._gate; RegionRecord.Gate`
- **Existing tests/evidence:** new performance/contention benchmarks only
- **Current live behavior:** ResourceBudgetAuthority currently has one ledger gate; RegionAuthority has per-region gates.
- **Actual gap:** Unknown whether budget gate is material on target manycore workload.
- **Required change:** Add contention/latency/throughput benchmark and lock-hold telemetry before any sharding task becomes enabled.
- **Authoritative owner:** Performance qualification
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Manycore/SMT/provider-contention benchmark with fixed source tuple.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** This task is measurement itself.
- **Dependencies/blockers:** P10 (measurement can run in parallel with P11-P13)
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P14-02 — Shard only if measured gate fails target

- **Phase:** P14
- **Classification:** `DEFERRED`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs; src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs`
- **Exact current symbols:** `ResourceBudgetAuthority._gate; RegionRecord.Gate`
- **Existing tests/evidence:** new performance/contention benchmarks only
- **Current live behavior:** No evidence yet that current owner implementation misses an agreed SLO.
- **Actual gap:** Premature sharding increases cross-shard correctness cost.
- **Required change:** Define candidate shard keys (stable owner/realm/domain) but do not implement until P14-01 exit criterion fails.
- **Authoritative owner:** Same logical authority owner
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** If enabled: cross-shard conservation/ABA/race tests.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P10 (measurement can run in parallel with P11-P13)
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P14-03 — Escrow/local reservation only if sharding requires it

- **Phase:** P14
- **Classification:** `DEFERRED`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs; src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs`
- **Exact current symbols:** `ResourceBudgetAuthority._gate; RegionRecord.Gate`
- **Existing tests/evidence:** new performance/contention benchmarks only
- **Current live behavior:** Current atomic vector reservation and SplitLease already support conservation for existing contour.
- **Actual gap:** No proven need for hierarchical escrow.
- **Required change:** Keep as optional optimization branch behind measured need.
- **Authoritative owner:** ResourceBudgetAuthority
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** If enabled: parent conservation and double-spend property tests.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P10 (measurement can run in parallel with P11-P13)
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P14-04 — Prove no cross-shard double-spend/ABA if optional branch lands

- **Phase:** P14
- **Classification:** `BLOCKED`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs; src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs`
- **Exact current symbols:** `ResourceBudgetAuthority._gate; RegionRecord.Gate`
- **Existing tests/evidence:** new performance/contention benchmarks only
- **Current live behavior:** There is no sharded implementation to prove.
- **Actual gap:** Depends on CD-P14-02/03 actually being enabled.
- **Required change:** Mandatory proof/test gate only for a future sharded implementation.
- **Authoritative owner:** ResourceBudgetAuthority
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Cross-shard linearizability/conservation/ABA.
- **Formal obligation:** TLA+/property-state exploration if sharding is implemented.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P10 (measurement can run in parallel with P11-P13)
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
