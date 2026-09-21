# ТЗ P09 — Define resource measurement and charging across execute/replay/retire/provider overhead

**Depends on:** P08.  **Baseline:** SingNextOS `1890a8e921cfe903b5b44857e4661168bf7bbceb`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

## CD-P09-01 — Define chargeability matrix per resource class

- **Classification:** VERIFIED_GAP + NEW_PROPOSED.
- **Repository:** SingNextOS unless an explicit HybridCPU contract/evidence hook is named below.
- **Verified current anchors:** `src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs; src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; HybridCPU_ISE/CloseToHSL/Core/Architecture/Registers/Retire/RetireCoordinator.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Types.cs`.
- **Proposed surface:** ResourceMeasurementContractV1, ResourceChargingPolicyV1, RetireEvidenceClassV1.
- **Problem / semantic requirement:** implement this task without changing the authoritative owner map; preserve independent OS authority, provider admission, runtime legality, resource truth, Region ownership and publication truth.
- **Proposed change:** make the minimal additive extension needed for the phase goal; reuse existing state machines and prepare/revalidate/commit seams rather than introducing a new manager/ledger.
- **Owner implications:** no new root authority. Pure descriptors/evaluators remain non-authoritative. Any owner mutation must occur only through the existing owner API.
- **Inputs:** exact operation/process/session/Region/resource/provider/contract generations required by the contour; no ambient cached authorization.
- **Outputs:** typed success/reject/stale result plus evidence where applicable. Output possession cannot authorize a later operation.
- **State/generation rules:** bind-once to exact generations; drift => stale/fail closed; no in-place generation upgrade.
- **Linearization/concurrency:** final exact revalidation immediately before owner commit; provider callbacks outside owner locks; duplicate commit has one winner.
- **Error/reject taxonomy:** distinguish invalid/malformed, unsupported, unauthorized, stale generation, provider unavailable/faulted, guarantee mismatch, runtime-illegal, ambiguous-post-submit, quarantine-required. Do not collapse them to generic false.
- **Compatibility/migration:** additive versioned change, gate OFF by default; conservative mapping of older contracts may only claim guarantees actually represented/enforced.
- **Required test assertions:**
  1. exact current input succeeds only when all independent gates succeed;
  2. one generation changed => no irreversible transition;
  3. duplicate/reordered evidence cannot skip state or double-settle;
  4. evidence/descriptor alone cannot mutate authority owner state;
  5. feature gate OFF preserves current qualified behavior;
  6. overflow/unknown enum/version fail closed where numeric/versioned input is present.
- **Definition of done:** source + focused tests + negative tests + traceability + exact evidence tuple; no new ISA dependency; full touched-project build/test clean relative to recorded baseline.
- **Dependencies/blockers:** P08; provider-specific enforcement remains BLOCKED if HybridCPU/provider cannot demonstrate the promised class.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>OS authorization; lease=>effect permission; completion=>visibility; visibility=>publication; cancellation request=>closure; provider loss=>refund; replay evidence=>resubmit; planner hint=>admission; compiler metadata=>runtime legality.

## CD-P09-02 — Extend exact usage evidence with measurement contract identity

- **Classification:** VERIFIED_GAP + NEW_PROPOSED.
- **Repository:** SingNextOS unless an explicit HybridCPU contract/evidence hook is named below.
- **Verified current anchors:** `src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs; src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; HybridCPU_ISE/CloseToHSL/Core/Architecture/Registers/Retire/RetireCoordinator.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Types.cs`.
- **Proposed surface:** ResourceMeasurementContractV1, ResourceChargingPolicyV1, RetireEvidenceClassV1.
- **Problem / semantic requirement:** implement this task without changing the authoritative owner map; preserve independent OS authority, provider admission, runtime legality, resource truth, Region ownership and publication truth.
- **Proposed change:** make the minimal additive extension needed for the phase goal; reuse existing state machines and prepare/revalidate/commit seams rather than introducing a new manager/ledger.
- **Owner implications:** no new root authority. Pure descriptors/evaluators remain non-authoritative. Any owner mutation must occur only through the existing owner API.
- **Inputs:** exact operation/process/session/Region/resource/provider/contract generations required by the contour; no ambient cached authorization.
- **Outputs:** typed success/reject/stale result plus evidence where applicable. Output possession cannot authorize a later operation.
- **State/generation rules:** bind-once to exact generations; drift => stale/fail closed; no in-place generation upgrade.
- **Linearization/concurrency:** final exact revalidation immediately before owner commit; provider callbacks outside owner locks; duplicate commit has one winner.
- **Error/reject taxonomy:** distinguish invalid/malformed, unsupported, unauthorized, stale generation, provider unavailable/faulted, guarantee mismatch, runtime-illegal, ambiguous-post-submit, quarantine-required. Do not collapse them to generic false.
- **Compatibility/migration:** additive versioned change, gate OFF by default; conservative mapping of older contracts may only claim guarantees actually represented/enforced.
- **Required test assertions:**
  1. exact current input succeeds only when all independent gates succeed;
  2. one generation changed => no irreversible transition;
  3. duplicate/reordered evidence cannot skip state or double-settle;
  4. evidence/descriptor alone cannot mutate authority owner state;
  5. feature gate OFF preserves current qualified behavior;
  6. overflow/unknown enum/version fail closed where numeric/versioned input is present.
- **Definition of done:** source + focused tests + negative tests + traceability + exact evidence tuple; no new ISA dependency; full touched-project build/test clean relative to recorded baseline.
- **Dependencies/blockers:** P08; provider-specific enforcement remains BLOCKED if HybridCPU/provider cannot demonstrate the promised class.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>OS authorization; lease=>effect permission; completion=>visibility; visibility=>publication; cancellation request=>closure; provider loss=>refund; replay evidence=>resubmit; planner hint=>admission; compiler metadata=>runtime legality.

## CD-P09-03 — Reconcile replay/squash/retry/SMT/blocked time

- **Classification:** VERIFIED_GAP + NEW_PROPOSED.
- **Repository:** SingNextOS unless an explicit HybridCPU contract/evidence hook is named below.
- **Verified current anchors:** `src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs; src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; HybridCPU_ISE/CloseToHSL/Core/Architecture/Registers/Retire/RetireCoordinator.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Types.cs`.
- **Proposed surface:** ResourceMeasurementContractV1, ResourceChargingPolicyV1, RetireEvidenceClassV1.
- **Problem / semantic requirement:** implement this task without changing the authoritative owner map; preserve independent OS authority, provider admission, runtime legality, resource truth, Region ownership and publication truth.
- **Proposed change:** make the minimal additive extension needed for the phase goal; reuse existing state machines and prepare/revalidate/commit seams rather than introducing a new manager/ledger.
- **Owner implications:** no new root authority. Pure descriptors/evaluators remain non-authoritative. Any owner mutation must occur only through the existing owner API.
- **Inputs:** exact operation/process/session/Region/resource/provider/contract generations required by the contour; no ambient cached authorization.
- **Outputs:** typed success/reject/stale result plus evidence where applicable. Output possession cannot authorize a later operation.
- **State/generation rules:** bind-once to exact generations; drift => stale/fail closed; no in-place generation upgrade.
- **Linearization/concurrency:** final exact revalidation immediately before owner commit; provider callbacks outside owner locks; duplicate commit has one winner.
- **Error/reject taxonomy:** distinguish invalid/malformed, unsupported, unauthorized, stale generation, provider unavailable/faulted, guarantee mismatch, runtime-illegal, ambiguous-post-submit, quarantine-required. Do not collapse them to generic false.
- **Compatibility/migration:** additive versioned change, gate OFF by default; conservative mapping of older contracts may only claim guarantees actually represented/enforced.
- **Required test assertions:**
  1. exact current input succeeds only when all independent gates succeed;
  2. one generation changed => no irreversible transition;
  3. duplicate/reordered evidence cannot skip state or double-settle;
  4. evidence/descriptor alone cannot mutate authority owner state;
  5. feature gate OFF preserves current qualified behavior;
  6. overflow/unknown enum/version fail closed where numeric/versioned input is present.
- **Definition of done:** source + focused tests + negative tests + traceability + exact evidence tuple; no new ISA dependency; full touched-project build/test clean relative to recorded baseline.
- **Dependencies/blockers:** P08; provider-specific enforcement remains BLOCKED if HybridCPU/provider cannot demonstrate the promised class.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>OS authorization; lease=>effect permission; completion=>visibility; visibility=>publication; cancellation request=>closure; provider loss=>refund; replay evidence=>resubmit; planner hint=>admission; compiler metadata=>runtime legality.

## CD-P09-04 — enforce over-envelope and duplicate evidence invariants

- **Classification:** VERIFIED_GAP + NEW_PROPOSED.
- **Repository:** SingNextOS unless an explicit HybridCPU contract/evidence hook is named below.
- **Verified current anchors:** `src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs; src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; HybridCPU_ISE/CloseToHSL/Core/Architecture/Registers/Retire/RetireCoordinator.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Types.cs`.
- **Proposed surface:** ResourceMeasurementContractV1, ResourceChargingPolicyV1, RetireEvidenceClassV1.
- **Problem / semantic requirement:** implement this task without changing the authoritative owner map; preserve independent OS authority, provider admission, runtime legality, resource truth, Region ownership and publication truth.
- **Proposed change:** make the minimal additive extension needed for the phase goal; reuse existing state machines and prepare/revalidate/commit seams rather than introducing a new manager/ledger.
- **Owner implications:** no new root authority. Pure descriptors/evaluators remain non-authoritative. Any owner mutation must occur only through the existing owner API.
- **Inputs:** exact operation/process/session/Region/resource/provider/contract generations required by the contour; no ambient cached authorization.
- **Outputs:** typed success/reject/stale result plus evidence where applicable. Output possession cannot authorize a later operation.
- **State/generation rules:** bind-once to exact generations; drift => stale/fail closed; no in-place generation upgrade.
- **Linearization/concurrency:** final exact revalidation immediately before owner commit; provider callbacks outside owner locks; duplicate commit has one winner.
- **Error/reject taxonomy:** distinguish invalid/malformed, unsupported, unauthorized, stale generation, provider unavailable/faulted, guarantee mismatch, runtime-illegal, ambiguous-post-submit, quarantine-required. Do not collapse them to generic false.
- **Compatibility/migration:** additive versioned change, gate OFF by default; conservative mapping of older contracts may only claim guarantees actually represented/enforced.
- **Required test assertions:**
  1. exact current input succeeds only when all independent gates succeed;
  2. one generation changed => no irreversible transition;
  3. duplicate/reordered evidence cannot skip state or double-settle;
  4. evidence/descriptor alone cannot mutate authority owner state;
  5. feature gate OFF preserves current qualified behavior;
  6. overflow/unknown enum/version fail closed where numeric/versioned input is present.
- **Definition of done:** source + focused tests + negative tests + traceability + exact evidence tuple; no new ISA dependency; full touched-project build/test clean relative to recorded baseline.
- **Dependencies/blockers:** P08; provider-specific enforcement remains BLOCKED if HybridCPU/provider cannot demonstrate the promised class.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>OS authorization; lease=>effect permission; completion=>visibility; visibility=>publication; cancellation request=>closure; provider loss=>refund; replay evidence=>resubmit; planner hint=>admission; compiler metadata=>runtime legality.
