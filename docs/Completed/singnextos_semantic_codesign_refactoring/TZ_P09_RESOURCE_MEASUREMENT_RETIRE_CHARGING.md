# ТЗ P09 — Resource measurement, chargeability and settlement

    **Depends on:** P08  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`.

    ## CD-P09-01 — Define chargeability matrix per ResourceClassV1

- **Phase:** P09
- **Classification:** `NEW_PROPOSED`
- **Repository:** Both
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs; contracts/SingPlus.Contracts/ResourceControlModelContracts.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/DmaStreamCompute/DmaStreamComputeTelemetry.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.PipelineExecution.Retire.cs`
- **Exact current symbols:** `SettleLease; BudgetReservationState; ResourceAssuranceV1; DmaStreamComputeBackendTelemetry`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/VNextPhase07ExternalOperationResourceBindingTests.cs; HybridCPU_ISE.Tests/tests/DmaStreamComputeTelemetryTests.cs
- **Current live behavior:** Budget settlement already accepts actual usage <= reserved amounts; it does not define semantic source for every resource class.
- **Actual gap:** Retired work cannot be universal proxy for execution/occupancy/throughput/provider overhead.
- **Required change:** For ComputeTime, DMA throughput, device memory occupancy, queue slots and inflight occupancy define admitted/issued/executed/squashed/replayed/retired/overhead/residency/stalled charge policy.
- **Authoritative owner:** ResourceBudget policy; provider supplies evidence only
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `RUNTIME_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Each class has positive, replay, cancel, provider loss, overhead/residency cases.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P08
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P09-02 — Bind usage evidence to measurement contract identity

- **Phase:** P09
- **Classification:** `NEW_PROPOSED`
- **Repository:** Both
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs; contracts/SingPlus.Contracts/ResourceControlModelContracts.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/DmaStreamCompute/DmaStreamComputeTelemetry.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.PipelineExecution.Retire.cs`
- **Exact current symbols:** `SettleLease; BudgetReservationState; ResourceAssuranceV1; DmaStreamComputeBackendTelemetry`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/VNextPhase07ExternalOperationResourceBindingTests.cs; HybridCPU_ISE.Tests/tests/DmaStreamComputeTelemetryTests.cs
- **Current live behavior:** Current settlement validates values, but provider measurement contract identity is not part of a semantic binding.
- **Actual gap:** Evidence from another provider/version could be replayed.
- **Required change:** Add MeasurementContractId/version and operation/binding/provider generations to usage evidence; exact-match at settlement.
- **Authoritative owner:** SingNext settlement coordinator + provider evidence
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `CONTRACT_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Cross-operation/cross-generation usage receipt replay rejected.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P08
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P09-03 — Reconcile replay/squash/retry/SMT/blocked time explicitly

- **Phase:** P09
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** Both
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs; contracts/SingPlus.Contracts/ResourceControlModelContracts.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/DmaStreamCompute/DmaStreamComputeTelemetry.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.PipelineExecution.Retire.cs`
- **Exact current symbols:** `SettleLease; BudgetReservationState; ResourceAssuranceV1; DmaStreamComputeBackendTelemetry`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/VNextPhase07ExternalOperationResourceBindingTests.cs; HybridCPU_ISE.Tests/tests/DmaStreamComputeTelemetryTests.cs
- **Current live behavior:** HybridCPU exposes replay/retire/telemetry; SingNext has settlement/quarantine.
- **Actual gap:** No universal charging semantics connects them.
- **Required change:** Provider implementation emits resource-class-specific counters; SingNext normalizes only according to bound measurement contract. Never infer consumption solely from retire count.
- **Authoritative owner:** Provider measurement + SingNext settlement
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `RUNTIME_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Replay and squash accounting; SMT contention not misreported as reservation guarantee.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P08
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P09-04 — Enforce envelope and duplicate evidence invariants

- **Phase:** P09
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs; contracts/SingPlus.Contracts/ResourceControlModelContracts.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/DmaStreamCompute/DmaStreamComputeTelemetry.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.PipelineExecution.Retire.cs`
- **Exact current symbols:** `SettleLease; BudgetReservationState; ResourceAssuranceV1; DmaStreamComputeBackendTelemetry`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/VNextPhase07ExternalOperationResourceBindingTests.cs; HybridCPU_ISE.Tests/tests/DmaStreamComputeTelemetryTests.cs
- **Current live behavior:** SettleLease already rejects actualUsage > reserved and terminal settlement is idempotent.
- **Actual gap:** Need exact-evidence identity and duplicate/cross-operation protection.
- **Required change:** Keep budget check; add evidence nonce/identity in binding; duplicate exact evidence is idempotent, mismatched duplicate is fault/quarantine.
- **Authoritative owner:** ResourceBudgetAuthority + coordinator
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** double charge, double refund, overflow, cross-op evidence replay.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P08
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
