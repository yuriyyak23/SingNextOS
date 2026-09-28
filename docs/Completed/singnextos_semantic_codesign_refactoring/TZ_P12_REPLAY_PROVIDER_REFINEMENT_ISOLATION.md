# ТЗ P12 — Replay, provider semantic refinement and isolation

    **Depends on:** P11  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`.

    ## CD-P12-01 — Define replay/determinism lattice by extending existing replay policy

- **Phase:** P12
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** Both
- **Exact current paths:** `HybridCPU_ExternalRuntime.Contracts/ExternalOperationReplayContracts.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.RuntimeLegality.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.TypedSlot.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Capabilities/MatMulCapabilityProvider.cs`
- **Exact current symbols:** `ExternalOperationReplayAction; LegalityDecision; TypedSlotFactStaging.CurrentMode; MatMulCapabilityProvider`
- **Existing tests/evidence:** HybridCPU replay/certificate tests; L7SdcCapabilityIsNotAuthorityTests.cs; new provider-refinement tests
- **Current live behavior:** HybridCPU has provider-neutral replay policy/invalidation and runtime replay certificates; replay evidence is not submit authority.
- **Actual gap:** SingNext obligations need deterministic/replay semantics aligned to provider behavior.
- **Required change:** Map existing replay classes to SingNext requirements; every new provider submission still needs fresh SingNext authorization, provider admission, refinement and runtime legality.
- **Authoritative owner:** SingNext policy + HybridCPU runtime replay owner
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `CONTRACT_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Replay after revocation; cached certificate never allows new submit.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P11
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P12-02 — ProviderBehavior refines semantic operation contract including numeric semantics

- **Phase:** P12
- **Classification:** `NEW_PROPOSED`
- **Repository:** Both
- **Exact current paths:** `HybridCPU_ExternalRuntime.Contracts/ExternalOperationReplayContracts.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.RuntimeLegality.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.TypedSlot.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Capabilities/MatMulCapabilityProvider.cs`
- **Exact current symbols:** `ExternalOperationReplayAction; LegalityDecision; TypedSlotFactStaging.CurrentMode; MatMulCapabilityProvider`
- **Existing tests/evidence:** HybridCPU replay/certificate tests; L7SdcCapabilityIsNotAuthorityTests.cs; new provider-refinement tests
- **Current live behavior:** Executable MatrixTile/L7 MatMul paths exist, but same operation name does not prove precision/rounding/atomicity/order equivalence.
- **Actual gap:** MatrixMultiply vertical needs explicit provider semantics.
- **Required change:** Add provider conformance descriptor/evaluator for precision, rounding/overflow, atomicity, ordering, nondeterminism, partial progress, failure, visibility, cancellation, replay and measurement.
- **Authoritative owner:** Semantic contract + provider conformance
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `CONTRACT_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Host/model/MatrixTile/L7 fake differential cases; mismatched rounding/partial publication rejects.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P11
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P12-03 — Typed isolation subset checks without overclaim

- **Phase:** P12
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** HybridCPU-v2
- **Exact current paths:** `HybridCPU_ExternalRuntime.Contracts/ExternalOperationReplayContracts.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.RuntimeLegality.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.TypedSlot.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Capabilities/MatMulCapabilityProvider.cs`
- **Exact current symbols:** `ExternalOperationReplayAction; LegalityDecision; TypedSlotFactStaging.CurrentMode; MatMulCapabilityProvider`
- **Existing tests/evidence:** HybridCPU replay/certificate tests; L7SdcCapabilityIsNotAuthorityTests.cs; new provider-refinement tests
- **Current live behavior:** ISE has domain guard/typed-slot legality and isolation probes, but not every QoS/cache/bandwidth isolation class.
- **Actual gap:** Boolean Secure would overclaim.
- **Required change:** Expose only executable isolation guarantees; unsupported dimensions remain Unsupported. Compiler facts remain ValidationOnly and cannot replace runtime legality.
- **Authoritative owner:** HybridCPU runtime guarantee provider
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `RUNTIME_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Missing compiler facts still route runtime legality; domain guard negatives; guarantee subset.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P11
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P12-04 — Guarantee laundering/provider-class switch tests

- **Phase:** P12
- **Classification:** `NEW_PROPOSED`
- **Repository:** Both
- **Exact current paths:** `HybridCPU_ExternalRuntime.Contracts/ExternalOperationReplayContracts.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.RuntimeLegality.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.TypedSlot.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Capabilities/MatMulCapabilityProvider.cs`
- **Exact current symbols:** `ExternalOperationReplayAction; LegalityDecision; TypedSlotFactStaging.CurrentMode; MatMulCapabilityProvider`
- **Existing tests/evidence:** HybridCPU replay/certificate tests; L7SdcCapabilityIsNotAuthorityTests.cs; new provider-refinement tests
- **Current live behavior:** Planner/provider capabilities can change after planning.
- **Actual gap:** Stale stronger class might be reused.
- **Required change:** Bind guarantee class to provider generation/execution class; final sentry exact revalidation.
- **Authoritative owner:** Binding/refinement
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `CONTRACT_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Provider changes execution class after planning; guarantee bit/capability cannot upgrade enforcement class.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P11
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
