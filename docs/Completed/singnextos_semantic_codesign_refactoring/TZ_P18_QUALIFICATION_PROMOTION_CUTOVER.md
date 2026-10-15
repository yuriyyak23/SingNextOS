# ТЗ P18 — Qualification, promotion, evidence tuple and rollback

    **Depends on:** P17 + P13; include P14/P15/P16 only if those optional claims are enabled  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`.

    ## CD-P18-01 — Run full adversarial matrix + selected model checking

- **Phase:** P18
- **Classification:** `NEW_PROPOSED`
- **Repository:** Both
- **Exact current paths:** `all touched contracts/runtime/tests; src/Runtime/SingPlus.Runtime/VNext/VNextFeatureGates.cs; HybridCPU_ExternalRuntime.Tests; HybridCPU_ISE.Tests`
- **Exact current symbols:** `VNextFeatureGates.IsEnabled currently always false; claim levels ModelOnly/StaticAdmission/RuntimeEnforced/ExecutableAdapter/EnforcedUpperBound/GuaranteedReservation/ProductionQualified`
- **Existing tests/evidence:** current executable result must be produced on exact tuple; this audit did not run tests
- **Current live behavior:** Scenarios are documented, but current executable results are not available from this audit.
- **Actual gap:** No promotion without exact run.
- **Required change:** Run unit/property/concurrency/fault/integration/provider conformance/differential/perf + TLA critical models. Record failures honestly.
- **Authoritative owner:** Qualification owner
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `RUNTIME_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** All scenarios in 09_ADVERSARIAL_MATRIX.md.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P17 + P13; include P14/P15/P16 only if those optional claims are enabled
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P18-02 — Record exact SHA/package/runtime/gate/test tuple

- **Phase:** P18
- **Classification:** `NEW_PROPOSED`
- **Repository:** SingNextOS
- **Exact current paths:** `all touched contracts/runtime/tests; src/Runtime/SingPlus.Runtime/VNext/VNextFeatureGates.cs; HybridCPU_ExternalRuntime.Tests; HybridCPU_ISE.Tests`
- **Exact current symbols:** `VNextFeatureGates.IsEnabled currently always false; claim levels ModelOnly/StaticAdmission/RuntimeEnforced/ExecutableAdapter/EnforcedUpperBound/GuaranteedReservation/ProductionQualified`
- **Existing tests/evidence:** current executable result must be produced on exact tuple; this audit did not run tests
- **Current live behavior:** Baseline tuple known; implementation will change source/package digests.
- **Actual gap:** Future source invalidates evidence.
- **Required change:** Generate machine-readable qualification tuple including SHAs, SDKs, package hashes, contract versions, gate states and test result artifacts.
- **Authoritative owner:** Qualification owner
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Tuple schema + locked restore.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P17 + P13; include P14/P15/P16 only if those optional claims are enabled
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P18-03 — Promote only exact Matrix/staged contour

- **Phase:** P18
- **Classification:** `NEW_PROPOSED`
- **Repository:** SingNextOS
- **Exact current paths:** `all touched contracts/runtime/tests; src/Runtime/SingPlus.Runtime/VNext/VNextFeatureGates.cs; HybridCPU_ExternalRuntime.Tests; HybridCPU_ISE.Tests`
- **Exact current symbols:** `VNextFeatureGates.IsEnabled currently always false; claim levels ModelOnly/StaticAdmission/RuntimeEnforced/ExecutableAdapter/EnforcedUpperBound/GuaranteedReservation/ProductionQualified`
- **Existing tests/evidence:** current executable result must be produced on exact tuple; this audit did not run tests
- **Current live behavior:** No semantic co-design implementation exists at audit baseline.
- **Actual gap:** Overbroad provider/ProductionQualified claim prohibited.
- **Required change:** Promote per dimension and provider only to demonstrated claim level; unsupported dimensions stay explicit.
- **Authoritative owner:** Qualification owner
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Claim matrix has evidence link per promotion.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P17 + P13; include P14/P15/P16 only if those optional claims are enabled
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P18-04 — Synchronize docs/traceability and retain rollback path

- **Phase:** P18
- **Classification:** `NEW_PROPOSED`
- **Repository:** SingNextOS
- **Exact current paths:** `all touched contracts/runtime/tests; src/Runtime/SingPlus.Runtime/VNext/VNextFeatureGates.cs; HybridCPU_ExternalRuntime.Tests; HybridCPU_ISE.Tests`
- **Exact current symbols:** `VNextFeatureGates.IsEnabled currently always false; claim levels ModelOnly/StaticAdmission/RuntimeEnforced/ExecutableAdapter/EnforcedUpperBound/GuaranteedReservation/ProductionQualified`
- **Existing tests/evidence:** current executable result must be produced on exact tuple; this audit did not run tests
- **Current live behavior:** Current VNext gates are all OFF, which is a safe migration baseline.
- **Actual gap:** New contract/gates need rollback semantics without reinterpreting in-flight work.
- **Required change:** Gate new admission only; rollback stops new binds, drains/reconciles old generation, never maps old binding to new version in place.
- **Authoritative owner:** Migration owner
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Gate OFF preserves baseline; rollback during in-flight staged operation.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P17 + P13; include P14/P15/P16 only if those optional claims are enabled
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
