# ТЗ P06 — Integrate four-gate final admission sentry

    **Depends on:** P05  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`.

    ## CD-P06-01 — Extend existing prepare/revalidate/commit path

- **Phase:** P06
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/VNext/ResourceAdmissionProtocol.cs; src/Runtime/SingPlus.Runtime/VNext/ComputePlanResourceAdmission.cs; src/Runtime/SingPlus.Runtime/ExternalOperations/HybridCpuExternalOperationProvider.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdmissionBindingContracts.cs`
- **Exact current symbols:** `IComputeSubmissionLegalityGate; PrepareResourceExternalAdmission; SubmitResourceExternalAdmission; ExternalOperationAdmissionBinding.Evaluate`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/VNextPhase07ExternalOperationResourceBindingTests.cs; HybridCPU_ExternalRuntime.Tests/ExternalOperationContractTests.cs
- **Current live behavior:** Existing SingNext admission already separates preparation and submit; HybridCPU binding already requires CPU guard + provider admission.
- **Actual gap:** Obligation/guarantee refinement is not yet one of final sentry gates.
- **Required change:** Insert pure refinement + exact SemanticExecutionBinding validation before provider submit commit; do not move provider callback under owner locks.
- **Authoritative owner:** SingNext coordination + existing owners
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `CONTRACT_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** All 4 gates independently false => no submit.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P05
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P06-02 — Bind refinement result to exact binding generation

- **Phase:** P06
- **Classification:** `NEW_PROPOSED`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/VNext/ResourceAdmissionProtocol.cs; src/Runtime/SingPlus.Runtime/VNext/ComputePlanResourceAdmission.cs; src/Runtime/SingPlus.Runtime/ExternalOperations/HybridCpuExternalOperationProvider.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdmissionBindingContracts.cs`
- **Exact current symbols:** `IComputeSubmissionLegalityGate; PrepareResourceExternalAdmission; SubmitResourceExternalAdmission; ExternalOperationAdmissionBinding.Evaluate`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/VNextPhase07ExternalOperationResourceBindingTests.cs; HybridCPU_ExternalRuntime.Tests/ExternalOperationContractTests.cs
- **Current live behavior:** No current refinement decision exists.
- **Actual gap:** Cached compatible result could survive provider/Region/lease drift.
- **Required change:** RefinementResult carries binding identity/version and is valid only for exact generations; any drift requires recompute.
- **Authoritative owner:** Pure evaluator/binding
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `CONTRACT_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Stale refinement decision cannot be reused after any generation change.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P05
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P06-03 — Guarantee one submit-start winner + pre-submit compensation

- **Phase:** P06
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/VNext/ResourceAdmissionProtocol.cs; src/Runtime/SingPlus.Runtime/VNext/ComputePlanResourceAdmission.cs; src/Runtime/SingPlus.Runtime/ExternalOperations/HybridCpuExternalOperationProvider.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdmissionBindingContracts.cs`
- **Exact current symbols:** `IComputeSubmissionLegalityGate; PrepareResourceExternalAdmission; SubmitResourceExternalAdmission; ExternalOperationAdmissionBinding.Evaluate`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/VNextPhase07ExternalOperationResourceBindingTests.cs; HybridCPU_ExternalRuntime.Tests/ExternalOperationContractTests.cs
- **Current live behavior:** HybridCPU adapter test rejects duplicate submit; SingNext resource protocol already compensates pre-submit failures.
- **Actual gap:** Need end-to-end single winner across OS binding and provider correlation.
- **Required change:** Use existing operation/binding identity and state transition CAS/owner lock; duplicate submit returns stable duplicate/stale status; compensate only before possible submit.
- **Authoritative owner:** ExternalOperationAuthority + admission coordinator
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Concurrent duplicate submit; transport throw before/after unknown submit.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P05
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P06-04 — Race tests revoke/session-close/provider-drift

- **Phase:** P06
- **Classification:** `NEW_PROPOSED`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/VNext/ResourceAdmissionProtocol.cs; src/Runtime/SingPlus.Runtime/VNext/ComputePlanResourceAdmission.cs; src/Runtime/SingPlus.Runtime/ExternalOperations/HybridCpuExternalOperationProvider.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdmissionBindingContracts.cs`
- **Exact current symbols:** `IComputeSubmissionLegalityGate; PrepareResourceExternalAdmission; SubmitResourceExternalAdmission; ExternalOperationAdmissionBinding.Evaluate`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/VNextPhase07ExternalOperationResourceBindingTests.cs; HybridCPU_ExternalRuntime.Tests/ExternalOperationContractTests.cs
- **Current live behavior:** Individual generation checks exist.
- **Actual gap:** Composition race coverage is incomplete.
- **Required change:** Add deterministic barriers/fault injection around final revalidation and submit callback.
- **Authoritative owner:** CI
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** revoke during admission; revoke after submit; session close during bind; provider generation drift; Region mutation.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P05
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
