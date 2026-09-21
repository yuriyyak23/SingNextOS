# ТЗ P03 — Specify global operational semantics without a new runtime owner

    **Depends on:** P02  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`.

    ## CD-P03-01 — Specify GlobalState as product of owner states

- **Phase:** P03
- **Classification:** `NEW_PROPOSED`
- **Repository:** Docs/Formal
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/VNext/ResourceAdmissionProtocol.cs; src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs; src/Runtime/SingPlus.Runtime/SipJobs/SipJobSegmentAdmission.cs; contracts/SingPlus.Contracts/ExternalOperations.cs`
- **Exact current symbols:** `prepare/revalidate/commit; BudgetReservationState; ExternalOperationState; SipJobAdmissionPhase`
- **Existing tests/evidence:** existing transition tests + new race model
- **Current live behavior:** Independent owner machines exist; no single executable GlobalState owner is needed.
- **Actual gap:** Need a common semantics for proofs and traceability.
- **Required change:** Define GlobalState only in spec/model as tuple of owner states and immutable binding/evidence; never implement GlobalStateManager.
- **Authoritative owner:** Specification only
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Model consistency against concrete transition names.
- **Formal obligation:** TLA+/PlusCal model skeleton.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P02
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P03-02 — Define cross-owner commit points and compensation

- **Phase:** P03
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/VNext/ResourceAdmissionProtocol.cs; src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs; src/Runtime/SingPlus.Runtime/SipJobs/SipJobSegmentAdmission.cs; contracts/SingPlus.Contracts/ExternalOperations.cs`
- **Exact current symbols:** `prepare/revalidate/commit; BudgetReservationState; ExternalOperationState; SipJobAdmissionPhase`
- **Existing tests/evidence:** existing transition tests + new race model
- **Current live behavior:** ResourceAdmissionProtocol and SipJobSegmentAdmission already encode reversible prepare/final revalidate/commit/settlement patterns.
- **Actual gap:** The co-design sentry needs exact irreversible boundary semantics.
- **Required change:** Reuse prepare/revalidate/commit; provider callback outside authority locks; pre-submit compensation only; post-submit ambiguity => quarantine/reconcile.
- **Authoritative owner:** Existing owner protocols
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Race tests for revoke/session close/Region mutation/provider drift around final sentry.
- **Formal obligation:** TLA+/PlusCal for check-then-act races.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P02
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P03-03 — Model revoke/reserve/session/Region/provider races

- **Phase:** P03
- **Classification:** `NEW_PROPOSED`
- **Repository:** Docs/Formal
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/VNext/ResourceAdmissionProtocol.cs; src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs; src/Runtime/SingPlus.Runtime/SipJobs/SipJobSegmentAdmission.cs; contracts/SingPlus.Contracts/ExternalOperations.cs`
- **Exact current symbols:** `prepare/revalidate/commit; BudgetReservationState; ExternalOperationState; SipJobAdmissionPhase`
- **Existing tests/evidence:** existing transition tests + new race model
- **Current live behavior:** No complete model ties all relevant generations together.
- **Actual gap:** Safety and liveness claims otherwise exceed implementation evidence.
- **Required change:** Model only critical staged-output contour first; encode fault assumptions explicitly.
- **Authoritative owner:** Formal model
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Model-check counterexamples become executable regression tests.
- **Formal obligation:** TLA+/PlusCal mandatory for critical cross-owner transitions.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P02
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
