# ТЗ P04 — Implement OperationObligations as immutable non-authoritative snapshot

    **Depends on:** P03  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`.

    ## CD-P04-01 — Add versioned OperationObligationsV1 vocabulary

- **Phase:** P04
- **Classification:** `NEW_PROPOSED`
- **Repository:** SingNextOS
- **Exact current paths:** `contracts/SingPlus.Contracts/ComputePlanning.cs; contracts/SingPlus.Contracts/ExternalOperations.cs; contracts/SingPlus.Contracts/ResourceControlModelContracts.cs; src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs`
- **Exact current symbols:** `ComputeIntent; OperationPreparation; OperationAdmissionSnapshot; ResourceEnvelopeV1; RegionUseDescriptor; MutationEpoch`
- **Existing tests/evidence:** new obligation contract tests required
- **Current live behavior:** No aggregate OperationObligations symbol exists, but most constituent semantics already exist in Compute/ExternalOperation/Region/resource contracts.
- **Actual gap:** Need one immutable requirement snapshot for refinement without creating authority.
- **Required change:** Add thin aggregate referencing/reusing SemanticEffect, DataUse/Region use, ResourceEnvelopeV1 list, temporal, isolation, preemption/cancellation, visibility/publication, replay/determinism, failure/containment and locality. IFC excluded from v1.
- **Authoritative owner:** SingNext semantic contract owner (requirements only)
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** DTO validation; canonical unknown/version failure; possession cannot mutate owners.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P03
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P04-02 — Compose obligations from exact live owner snapshots

- **Phase:** P04
- **Classification:** `NEW_PROPOSED`
- **Repository:** SingNextOS
- **Exact current paths:** `contracts/SingPlus.Contracts/ComputePlanning.cs; contracts/SingPlus.Contracts/ExternalOperations.cs; contracts/SingPlus.Contracts/ResourceControlModelContracts.cs; src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs`
- **Exact current symbols:** `ComputeIntent; OperationPreparation; OperationAdmissionSnapshot; ResourceEnvelopeV1; RegionUseDescriptor; MutationEpoch`
- **Existing tests/evidence:** new obligation contract tests required
- **Current live behavior:** Current contracts are separate and generation-bound in their own owners.
- **Actual gap:** Aggregate can become stale between planning and submit.
- **Required change:** Composer captures immutable identities/generations only; it does not cache authority truth beyond final revalidation.
- **Authoritative owner:** SingNext coordination layer
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Compose then revoke/mutate/session-close => final sentry rejects.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P03
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P04-03 — Revalidate obligation identity at final sentry

- **Phase:** P04
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `contracts/SingPlus.Contracts/ComputePlanning.cs; contracts/SingPlus.Contracts/ExternalOperations.cs; contracts/SingPlus.Contracts/ResourceControlModelContracts.cs; src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs`
- **Exact current symbols:** `ComputeIntent; OperationPreparation; OperationAdmissionSnapshot; ResourceEnvelopeV1; RegionUseDescriptor; MutationEpoch`
- **Existing tests/evidence:** new obligation contract tests required
- **Current live behavior:** Existing ResourceAdmissionProtocol and Region/session owners already provide revalidation seams; obligation aggregate is missing.
- **Actual gap:** Need exact binding from snapshot to sentry inputs.
- **Required change:** Add obligation digest/id/version to sentry candidate and re-resolve live owners immediately before irreversible submit.
- **Authoritative owner:** Existing owners + coordination
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** ABA/generation drift across operation, Region, session, budget, provider.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P03
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P04-04 — Negative tests: descriptor possession never authorizes

- **Phase:** P04
- **Classification:** `NEW_PROPOSED`
- **Repository:** SingNextOS
- **Exact current paths:** `contracts/SingPlus.Contracts/ComputePlanning.cs; contracts/SingPlus.Contracts/ExternalOperations.cs; contracts/SingPlus.Contracts/ResourceControlModelContracts.cs; src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs`
- **Exact current symbols:** `ComputeIntent; OperationPreparation; OperationAdmissionSnapshot; ResourceEnvelopeV1; RegionUseDescriptor; MutationEpoch`
- **Existing tests/evidence:** new obligation contract tests required
- **Current live behavior:** Existing individual DTOs often expose AuthorizesEffect=false, but new aggregate needs explicit regression guard.
- **Actual gap:** Future callers may treat valid DTO as permit.
- **Required change:** No mutation methods on obligations; tests prove capability/resource/publication checks are still independently required.
- **Authoritative owner:** CI/architecture
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Unauthorized operation with syntactically valid obligations must fail.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P03
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
