# ТЗ P11 — Preemption, cancellation and provider-contour containment

    **Depends on:** P10  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`.

    ## CD-P11-01 — Define semantic preemption/cancellation obligations with conservative mapping

- **Phase:** P11
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** Both
- **Exact current paths:** `contracts/SingPlus.Contracts/DeadlineCancellationContracts.cs; contracts/SingPlus.Contracts/ExternalOperations.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs; HybridCPU_ExternalRuntime.Tests/ExternalOperationAdapterSessionTests.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/DmaStreamCompute/DmaStreamComputeRuntime.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Fences/AcceleratorFenceModel.cs`
- **Exact current symbols:** `CancellationDisposition; ExternalCancellationSupport; ExternalCancellationMode; ExternalOperationCancellationReceipt; AcceleratorFenceCoordinator`
- **Existing tests/evidence:** HybridCPU_ExternalRuntime.Tests/ExternalOperationAdapterSessionTests.cs; new containment race tests
- **Current live behavior:** SingNext has BeforeSubmissionOnly/ProviderCooperative plus rich CancellationDisposition; HybridCPU has Unsupported/BestEffort/ExactAcknowledgement.
- **Actual gap:** Need semantic classes such as NotCancellable/CancellableBeforeDispatch/Bounded/Checkpoint/DrainOnly without pretending provider support.
- **Required change:** Add obligation classes on SingNext side and mapping to existing provider modes; MaxNonPreemptibleInterval only when runtime can enforce/measure it.
- **Authoritative owner:** SingNext requirements + provider guarantee
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `CONTRACT_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Generic Cancel request never proves no effect; exact acknowledgement required for stronger class.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P10
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P11-02 — Extend guarantees only for executable cancellation/preemption classes

- **Phase:** P11
- **Classification:** `NEW_PROPOSED`
- **Repository:** HybridCPU-v2
- **Exact current paths:** `contracts/SingPlus.Contracts/DeadlineCancellationContracts.cs; contracts/SingPlus.Contracts/ExternalOperations.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs; HybridCPU_ExternalRuntime.Tests/ExternalOperationAdapterSessionTests.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/DmaStreamCompute/DmaStreamComputeRuntime.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Fences/AcceleratorFenceModel.cs`
- **Exact current symbols:** `CancellationDisposition; ExternalCancellationSupport; ExternalCancellationMode; ExternalOperationCancellationReceipt; AcceleratorFenceCoordinator`
- **Existing tests/evidence:** HybridCPU_ExternalRuntime.Tests/ExternalOperationAdapterSessionTests.cs; new containment race tests
- **Current live behavior:** Current HybridCPU adapter supports exact cancellation acknowledgement semantics but not a general bounded-preemption guarantee.
- **Actual gap:** No evidence for arbitrary bound.
- **Required change:** Provider advertises Unsupported unless a concrete runtime path enforces bound; ISE code may add runtime timers/safe points in existing execution engines without ISA change.
- **Authoritative owner:** HybridCPU runtime/provider
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `RUNTIME_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Bound measured under worst-case provider path; timeout demotes guarantee instead of pretending cancellation.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P10
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P11-03 — Replace global EffectEpoch with contour-scoped containment closure hook

- **Phase:** P11
- **Classification:** `NEW_PROPOSED`
- **Repository:** Both
- **Exact current paths:** `contracts/SingPlus.Contracts/DeadlineCancellationContracts.cs; contracts/SingPlus.Contracts/ExternalOperations.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs; HybridCPU_ExternalRuntime.Tests/ExternalOperationAdapterSessionTests.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/DmaStreamCompute/DmaStreamComputeRuntime.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Fences/AcceleratorFenceModel.cs`
- **Exact current symbols:** `CancellationDisposition; ExternalCancellationSupport; ExternalCancellationMode; ExternalOperationCancellationReceipt; AcceleratorFenceCoordinator`
- **Existing tests/evidence:** HybridCPU_ExternalRuntime.Tests/ExternalOperationAdapterSessionTests.cs; new containment race tests
- **Current live behavior:** Lane6 runtime reaches CommitPending; Lane7 has guarded token/fence/commit pipeline. There is no need for a CPU-global OS epoch/ISA object.
- **Actual gap:** Need proof that after closure no new external effects can emerge for selected contour.
- **Required change:** Define optional provider-neutral ContainmentClosureReceipt bound to operation/provider generation. Implement in Lane6/Lane7 runtime only where drain/cancel/fence + generation invalidation can prove closure. If proof unavailable => Unsupported and SingNext keeps quarantine/drain policy. No architectural register/instruction/OS handle.
- **Authoritative owner:** Provider/runtime containment mechanism; SingNext remains reclaim owner
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `PROVIDER_SPECIFIC`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** close-vs-retire, late effect after close, provider restart, duplicate closure, stale closure receipt.
- **Formal obligation:** TLA+/PlusCal for close/retire/cancel race; no bounded liveness under partition without assumption.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P10
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P11-04 — Cancellation/retire/replay/containment fault tests

- **Phase:** P11
- **Classification:** `NEW_PROPOSED`
- **Repository:** Both
- **Exact current paths:** `contracts/SingPlus.Contracts/DeadlineCancellationContracts.cs; contracts/SingPlus.Contracts/ExternalOperations.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs; HybridCPU_ExternalRuntime.Tests/ExternalOperationAdapterSessionTests.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/DmaStreamCompute/DmaStreamComputeRuntime.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Fences/AcceleratorFenceModel.cs`
- **Exact current symbols:** `CancellationDisposition; ExternalCancellationSupport; ExternalCancellationMode; ExternalOperationCancellationReceipt; AcceleratorFenceCoordinator`
- **Existing tests/evidence:** HybridCPU_ExternalRuntime.Tests/ExternalOperationAdapterSessionTests.cs; new containment race tests
- **Current live behavior:** Existing adapter tests cover exact cancellation acknowledgement and invalidation, not full cross-owner closure races.
- **Actual gap:** Need adversarial coverage.
- **Required change:** Add deterministic ISE/provider fake hooks and SingNext integration tests.
- **Authoritative owner:** CI/provider conformance
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `RUNTIME_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** cancel before submit; after possible submit; cancel/retire; closure/replay; late provider effect.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P10
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
