# ТЗ P02 — Define minimal typed semantic vocabulary and refinement algebra

    **Depends on:** P01  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`.

    ## CD-P02-01 — Define dimension-specific partial orders

- **Phase:** P02
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** Both
- **Exact current paths:** `contracts/SingPlus.Contracts/ResourceControlModelContracts.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Types.cs`
- **Exact current symbols:** `ResourceEnvelopeV1.IsSubset; ResourceUseConstraintV1.IsSubset; ExternalEffectClass; ExternalVisibilityRequirement; ExternalCancellationMode; TypedSlotFactStaging`
- **Existing tests/evidence:** new property/state exploration required
- **Current live behavior:** ResourceEnvelopeV1/ResourceUseConstraintV1 already implement fail-closed subset relations. HybridCPU has effect/visibility/cancellation classes but no complete cross-domain lattice.
- **Actual gap:** Missing typed orders for isolation, publication enforcement, replay/determinism, cancellation/preemption, containment, locality and measurement/enforcement claims.
- **Required change:** Extend only needed dimensions with explicit comparison functions; do not collapse to enum ordinal unless semantically monotone.
- **Authoritative owner:** Pure contract/evaluator
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `CONTRACT_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Property tests reflexivity/transitivity where applicable; counterexamples for incomparable classes.
- **Formal obligation:** Property/state exploration; Alloy only for finite cross-dimension compatibility.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P01
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P02-02 — Define Mandatory/Advisory/Unsupported semantics

- **Phase:** P02
- **Classification:** `NEW_PROPOSED`
- **Repository:** Both
- **Exact current paths:** `contracts/SingPlus.Contracts/ResourceControlModelContracts.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Types.cs`
- **Exact current symbols:** `ResourceEnvelopeV1.IsSubset; ResourceUseConstraintV1.IsSubset; ExternalEffectClass; ExternalVisibilityRequirement; ExternalCancellationMode; TypedSlotFactStaging`
- **Existing tests/evidence:** new property/state exploration required
- **Current live behavior:** Current feature/capability surfaces do not uniformly say whether absence rejects admission.
- **Actual gap:** Unknown/unsupported guarantee can be laundered as weak success.
- **Required change:** Each obligation dimension carries requirement strength; Mandatory+Unsupported => reject; Advisory never grants authority; unknown => fail closed.
- **Authoritative owner:** Pure semantic contract
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `CONTRACT_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Unknown enum/version/class and Unsupported mandatory negatives.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P01
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P02-03 — Property-test refinement monotonicity and version failure

- **Phase:** P02
- **Classification:** `NEW_PROPOSED`
- **Repository:** Both
- **Exact current paths:** `contracts/SingPlus.Contracts/ResourceControlModelContracts.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Types.cs`
- **Exact current symbols:** `ResourceEnvelopeV1.IsSubset; ResourceUseConstraintV1.IsSubset; ExternalEffectClass; ExternalVisibilityRequirement; ExternalCancellationMode; TypedSlotFactStaging`
- **Existing tests/evidence:** new property/state exploration required
- **Current live behavior:** No end-to-end typed refinement evaluator exists yet.
- **Actual gap:** Core co-design thesis is unverified without executable relation tests.
- **Required change:** Create pure evaluator with canonical inputs and exhaustive small-state/property tests.
- **Authoritative owner:** Pure evaluator
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `CONTRACT_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Monotonicity, anti-laundering, unknown-version fail closed, no planner override.
- **Formal obligation:** Property/state exploration is mandatory; Lean/Coq not justified.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P01
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
