# ТЗ P08 — Visibility, Region integration and locality

    **Depends on:** P07  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`.

    ## CD-P08-01 — Define visibility classes and Region transition mapping

- **Phase:** P08
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs; contracts/SingPlus.Contracts/ExternalOperations.cs; contracts/SingPlus.Contracts/ComputePlanning.cs`
- **Exact current symbols:** `RegionUseRecord; MutationEpoch; ExternalVisibilityRequirement; ComputePublicationPath`
- **Existing tests/evidence:** Region tests + external lifecycle tests; direct-coherent alias tests required before promotion
- **Current live behavior:** RegionUse includes exact MutationEpoch; ExternalOperation has DeviceComplete->Visible and visibility requirement.
- **Actual gap:** Need typed meaning for CoherentImmediate/AcquireRequired/StagedCopyBack/ProviderPrivateUntilCommit/DirectCoherentWrite.
- **Required change:** Add visibility descriptor that maps to existing RegionUse/MutationEpoch and ExternalOperation transition; no second Region state database.
- **Authoritative owner:** RegionAuthority + ExternalOperation lifecycle
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Complete != Visible; mutation epoch drift blocks publish/release.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P07
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P08-02 — Keep direct-coherent contour gated until alias-exclusion evidence

- **Phase:** P08
- **Classification:** `VERIFIED_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs; contracts/SingPlus.Contracts/ExternalOperations.cs; contracts/SingPlus.Contracts/ComputePlanning.cs`
- **Exact current symbols:** `RegionUseRecord; MutationEpoch; ExternalVisibilityRequirement; ComputePublicationPath`
- **Existing tests/evidence:** Region tests + external lifecycle tests; direct-coherent alias tests required before promotion
- **Current live behavior:** ComputePlanning already distinguishes staged/direct paths; current roadmap treats staged as first qualification contour.
- **Actual gap:** Direct coherent writes can be externally visible before OS publication decision.
- **Required change:** Retain feature-gated direct contour; require exact alias/mutation exclusion evidence and contour-specific publication semantics before promotion.
- **Authoritative owner:** RegionAuthority/planner policy
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Alias race and direct visibility tests; no staged-withholding claim.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P07
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P08-03 — Shared-mutable/atomic authority extension only if a real contour requires it

- **Phase:** P08
- **Classification:** `DEFERRED`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs; contracts/SingPlus.Contracts/ExternalOperations.cs; contracts/SingPlus.Contracts/ComputePlanning.cs`
- **Exact current symbols:** `RegionUseRecord; MutationEpoch; ExternalVisibilityRequirement; ComputePublicationPath`
- **Existing tests/evidence:** Region tests + external lifecycle tests; direct-coherent alias tests required before promotion
- **Current live behavior:** RegionAuthority already has ownership/use/mutation epochs; no proven critical-path need for a second shared-mutable ledger.
- **Actual gap:** Generic fractional/shared-write authority would be premature.
- **Required change:** Defer. If required later, extend RegionAuthority with owner-defined atomic mutation lease/range semantics.
- **Authoritative owner:** RegionAuthority only
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Not applicable until contour accepted.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** Avoid global Region lock; preserve per-region gates.
- **Dependencies/blockers:** P07
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P08-04 — Semantic locality model without hardware IDs in authority API

- **Phase:** P08
- **Classification:** `NEW_PROPOSED`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs; contracts/SingPlus.Contracts/ExternalOperations.cs; contracts/SingPlus.Contracts/ComputePlanning.cs`
- **Exact current symbols:** `RegionUseRecord; MutationEpoch; ExternalVisibilityRequirement; ComputePublicationPath`
- **Existing tests/evidence:** Region tests + external lifecycle tests; direct-coherent alias tests required before promotion
- **Current live behavior:** Compute candidate has latency/bandwidth classes but no normative semantic locality contract.
- **Actual gap:** Planner needs locality constraints without leaking lane/core/NUMA IDs into authority.
- **Required change:** Add optional LocalityRequirement/Guarantee classes owned by policy/provider; exact hardware topology remains provider-private evidence.
- **Authoritative owner:** Planner/provider policy
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `CONTRACT_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** No core/lane/BDF/physical address in portable contract.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P07
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
