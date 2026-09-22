# ТЗ P07 — Refine effect/publication algebra on existing lifecycle

    **Depends on:** P06  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`.

    ## CD-P07-01 — Model orthogonal effect traits without parallel lifecycle

- **Phase:** P07
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `contracts/SingPlus.Contracts/ExternalOperations.cs; src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationPublicationContracts.cs`
- **Exact current symbols:** `ExternalEffectPolicy; ExternalEffectBoundaryState; ExternalPublicationPolicy; PublicationPlan; ReleasePlan; ExternalOperationPublicationGate`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/ExternalOperationLifecycleTests.cs; HybridCPU_ExternalRuntime.Tests/ExternalOperationContractTests.cs
- **Current live behavior:** SingNext already models StagedReversibleUntilPublish / SnapshotOrIdempotenceRequired / IrreversibleBarrier and NotCrossed/StagedPending/ExternallyVisible/Irreversible.
- **Actual gap:** Network/MMIO/durable/compensatable contours need traits not safely inferred from 3 classes.
- **Required change:** Add compact EffectSemanticsV1 traits only when a supported contour needs them: Staged, Reversible, LocallyIrreversible, ExternallyObservable, Durable, Compensatable, Idempotent, Commutative. Keep current state machine authoritative.
- **Authoritative owner:** ExternalOperation semantic contract
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Invalid trait combinations fail; state transition remains existing owner.
- **Formal obligation:** Alloy optional for valid trait/configuration combinations.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P06
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P07-02 — Map current ExternalEffectPolicy conservatively

- **Phase:** P07
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `contracts/SingPlus.Contracts/ExternalOperations.cs; src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationPublicationContracts.cs`
- **Exact current symbols:** `ExternalEffectPolicy; ExternalEffectBoundaryState; ExternalPublicationPolicy; PublicationPlan; ReleasePlan; ExternalOperationPublicationGate`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/ExternalOperationLifecycleTests.cs; HybridCPU_ExternalRuntime.Tests/ExternalOperationContractTests.cs
- **Current live behavior:** Current policy already separates staged/idempotence-required/irreversible.
- **Actual gap:** Mapping to new traits could claim more than existing implementation.
- **Required change:** Define one-way conservative mapping; no reverse inference to stronger semantics.
- **Authoritative owner:** Pure mapper
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Every existing class maps to no-stronger trait set.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P06
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P07-03 — Exact SingNext publication decision + provider action for staged contour

- **Phase:** P07
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** Both
- **Exact current paths:** `contracts/SingPlus.Contracts/ExternalOperations.cs; src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationPublicationContracts.cs`
- **Exact current symbols:** `ExternalEffectPolicy; ExternalEffectBoundaryState; ExternalPublicationPolicy; PublicationPlan; ReleasePlan; ExternalOperationPublicationGate`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/ExternalOperationLifecycleTests.cs; HybridCPU_ExternalRuntime.Tests/ExternalOperationContractTests.cs
- **Current live behavior:** SingNext owns publication state; HybridCPU has a staged publication eligibility gate requiring completion+visibility.
- **Actual gap:** Cross-repo action must not become CPU-owned PublishPermit authority.
- **Required change:** Record exact SingNext publication decision in existing publication owner; adapter invokes provider publish action only if contour can withhold; provider receipt returns; ExternalOperationAuthority records result. Direct/coherent effects bypass staged gate and use contour-specific semantics.
- **Authoritative owner:** SingNext publication owner + provider mechanism
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `RUNTIME_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Visible without decision cannot advance Published for staged contour; direct contour never pretends withholding.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P06
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P07-04 — Effect-class lifecycle tests

- **Phase:** P07
- **Classification:** `NEW_PROPOSED`
- **Repository:** SingNextOS
- **Exact current paths:** `contracts/SingPlus.Contracts/ExternalOperations.cs; src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationPublicationContracts.cs`
- **Exact current symbols:** `ExternalEffectPolicy; ExternalEffectBoundaryState; ExternalPublicationPolicy; PublicationPlan; ReleasePlan; ExternalOperationPublicationGate`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/ExternalOperationLifecycleTests.cs; HybridCPU_ExternalRuntime.Tests/ExternalOperationContractTests.cs
- **Current live behavior:** Current tests focus existing staged/direct lifecycle.
- **Actual gap:** Need contour-specific negative semantics.
- **Required change:** Add staged/direct/network-like fake/MMIO-like fake/durable fake tests without claiming unsupported production providers.
- **Authoritative owner:** CI/provider conformance
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** commit/observability/visibility/publication/rollback/compensation/replay/settlement/reclaim matrix.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P06
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
