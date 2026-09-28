# ТЗ P10 — Reuse existing atomic vector reservation; minimal QoS/multi-resource changes

    **Depends on:** P09  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`.

    ## CD-P10-01 — Verify/reuse atomic multi-dimensional budget reservation and add only missing envelope mapping

- **Phase:** P10
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; src/Runtime/SingPlus.Runtime/VNext/ResourceDonationProtocol.cs; contracts/SingPlus.Contracts/ResourceBudgetContracts.cs; contracts/SingPlus.Contracts/ResourceControlModelContracts.cs; src/Runtime/SingPlus.Runtime/VNext/ResourceScheduler.cs`
- **Exact current symbols:** `ResourceBudgetAuthority.Reserve; SplitLease; SettleLease; ResourceDonationBinding.PriorityCeiling; PriorityRank; ResourceScheduler`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/Phase05ResourceBudgetTests.cs; tests/SingPlus.Tests/Runtime/VNextPhase06ResourceDonationTests.cs; tests/SingPlus.Tests/Runtime/VNextPhase10ResourceSchedulerTests.cs
- **Current live behavior:** ResourceBudgetAuthority.Reserve already accepts IReadOnlyList<BudgetAmount> and mutates under one ledger gate; resource model already has ComputeTime/DMA/Network/Fabric/DeviceMemory/QueueSlot/Inflight classes.
- **Actual gap:** Mapping between semantic ResourceEnvelopeV1 collection and budget dimensions/provider-local resources is incomplete.
- **Required change:** Do not add a second vector-reservation owner. Add canonical envelope-set validation/mapping only as a value helper; reserve all SingNext-owned dimensions in one existing Reserve call.
- **Authoritative owner:** ResourceBudgetAuthority remains sole quantitative owner
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Atomic success/failure across multiple dimensions; no partial Used mutation on failure.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** Existing single gate is accepted for correctness; P14 measures contention before sharding.
- **Dependencies/blockers:** P09
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P10-02 — Remove standalone banker/escrow protocol from critical path

- **Phase:** P10
- **Classification:** `REMOVE_OR_MERGE`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; src/Runtime/SingPlus.Runtime/VNext/ResourceDonationProtocol.cs; contracts/SingPlus.Contracts/ResourceBudgetContracts.cs; contracts/SingPlus.Contracts/ResourceControlModelContracts.cs; src/Runtime/SingPlus.Runtime/VNext/ResourceScheduler.cs`
- **Exact current symbols:** `ResourceBudgetAuthority.Reserve; SplitLease; SettleLease; ResourceDonationBinding.PriorityCeiling; PriorityRank; ResourceScheduler`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/Phase05ResourceBudgetTests.cs; tests/SingPlus.Tests/Runtime/VNextPhase06ResourceDonationTests.cs; tests/SingPlus.Tests/Runtime/VNextPhase10ResourceSchedulerTests.cs
- **Current live behavior:** The original task proposed choosing up-front atomic/escrow machinery. Existing ledger already provides atomic vector reservation for OS-owned dimensions.
- **Actual gap:** Provider-local secondary resources can still create hold-and-wait if acquired later.
- **Required change:** Merge rule into P10-01/P06: OS-owned vector acquired atomically up front; provider-local admission must be all-or-nothing or release-before-wait. Escrow/canonical ordering only after a demonstrated provider contour gap.
- **Authoritative owner:** Existing budget/provider admission owners
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Partial provider-local acquisition fault injection proves no OS double-spend/hold-and-wait.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P09
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P10-03 — Preserve existing donation assurance/priority ceilings

- **Phase:** P10
- **Classification:** `VERIFIED_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; src/Runtime/SingPlus.Runtime/VNext/ResourceDonationProtocol.cs; contracts/SingPlus.Contracts/ResourceBudgetContracts.cs; contracts/SingPlus.Contracts/ResourceControlModelContracts.cs; src/Runtime/SingPlus.Runtime/VNext/ResourceScheduler.cs`
- **Exact current symbols:** `ResourceBudgetAuthority.Reserve; SplitLease; SettleLease; ResourceDonationBinding.PriorityCeiling; PriorityRank; ResourceScheduler`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/Phase05ResourceBudgetTests.cs; tests/SingPlus.Tests/Runtime/VNextPhase06ResourceDonationTests.cs; tests/SingPlus.Tests/Runtime/VNextPhase10ResourceSchedulerTests.cs
- **Current live behavior:** ResourceDonationProtocol narrows ResourceEnvelope, assurance and PriorityRank for nested donation and uses SplitLease conservation.
- **Actual gap:** Need anti-laundering regression coverage when new guarantee fields are added.
- **Required change:** Do not redesign donation. Extend tests so execution guarantee/QoS cannot exceed source grant/priority/assurance ceiling.
- **Authoritative owner:** CapabilityAuthority + ResourceBudgetAuthority + invocation donation owner
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Nested donation laundering/cycle/depth/priority ceiling negatives.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P09
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P10-04 — Scheduler uses evidence but cannot authorize/refine mismatch

- **Phase:** P10
- **Classification:** `VERIFIED_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; src/Runtime/SingPlus.Runtime/VNext/ResourceDonationProtocol.cs; contracts/SingPlus.Contracts/ResourceBudgetContracts.cs; contracts/SingPlus.Contracts/ResourceControlModelContracts.cs; src/Runtime/SingPlus.Runtime/VNext/ResourceScheduler.cs`
- **Exact current symbols:** `ResourceBudgetAuthority.Reserve; SplitLease; SettleLease; ResourceDonationBinding.PriorityCeiling; PriorityRank; ResourceScheduler`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/Phase05ResourceBudgetTests.cs; tests/SingPlus.Tests/Runtime/VNextPhase06ResourceDonationTests.cs; tests/SingPlus.Tests/Runtime/VNextPhase10ResourceSchedulerTests.cs
- **Current live behavior:** ResourceScheduler is policy/evidence layer; current owner map excludes authority.
- **Actual gap:** New guarantee data could tempt scheduler to override mismatch.
- **Required change:** Scheduler may filter/rank only. Final sentry recomputes/refuses mismatch independently.
- **Authoritative owner:** Planner/Scheduler policy only
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Stale topology and favorable score cannot legalize missing mandatory guarantee.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P09
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
