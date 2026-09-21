# ТЗ P16 — SipJob semantic equivalence; declarative generation optional

    **Depends on:** P12 (parallel branch; not prerequisite for P17)  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`.

    ## CD-P16-01 — Define authority-visible trace vocabulary

- **Phase:** P16
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/SipJobs/SipJobBarrierPlanner.cs; src/Runtime/SingPlus.Runtime/SipJobs/SipJobSegmentAdmission.cs; src/Runtime/SingPlus.Runtime/SipJobs/SipJobStageExecutionEligibility.cs; tests/SingPlus.Tests/SipJobs/SipJobSemanticTraceVocabulary.cs`
- **Exact current symbols:** `SipJobSegmentAdmissionVerifier; SipJobStageExecutionEligibilityVerifier; SipJobAdmissionParticipantKind`
- **Existing tests/evidence:** tests/SingPlus.Tests/SipJobs/Phase145ABarrierPlannerTests.cs; tests/SingPlus.Tests/SipJobs/Phase144ComposedAdmissionTests.cs
- **Current live behavior:** SipJob tests already have semantic trace vocabulary; live barrier/segment code names existing owners explicitly.
- **Actual gap:** Need co-design events for provider submit/effects/publication/settlement.
- **Required change:** Extend trace vocabulary with exact external operation/binding IDs; keep transport/serialization/queue/fusion internal-only.
- **Authoritative owner:** Trace/qualification
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Trace schema tests; no provider-private payload leakage.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P12 (parallel branch; not prerequisite for P17)
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P16-02 — Differential/weak-bisimulation ordinary SIP vs fused path

- **Phase:** P16
- **Classification:** `NEW_PROPOSED`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/SipJobs/SipJobBarrierPlanner.cs; src/Runtime/SingPlus.Runtime/SipJobs/SipJobSegmentAdmission.cs; src/Runtime/SingPlus.Runtime/SipJobs/SipJobStageExecutionEligibility.cs; tests/SingPlus.Tests/SipJobs/SipJobSemanticTraceVocabulary.cs`
- **Exact current symbols:** `SipJobSegmentAdmissionVerifier; SipJobStageExecutionEligibilityVerifier; SipJobAdmissionParticipantKind`
- **Existing tests/evidence:** tests/SingPlus.Tests/SipJobs/Phase145ABarrierPlannerTests.cs; tests/SingPlus.Tests/SipJobs/Phase144ComposedAdmissionTests.cs
- **Current live behavior:** Current SipJob qualification covers many barriers but not new semantic execution binding.
- **Actual gap:** Optimization must not alter authority-visible behavior.
- **Required change:** Run same semantic operation through ordinary SIP and eligible SipJob path; compare visible trace up to allowed stuttering/internal events.
- **Authoritative owner:** SipJob optimizer only
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** cap/Region/resource/provider/external-effect/publication/settlement equivalence.
- **Formal obligation:** Trace/differential state exploration; full theorem prover not required initially.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P12 (parallel branch; not prerequisite for P17)
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P16-03 — Defer declarative OperationContract code generator until protocols stabilize

- **Phase:** P16
- **Classification:** `DEFERRED`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/SipJobs/SipJobBarrierPlanner.cs; src/Runtime/SingPlus.Runtime/SipJobs/SipJobSegmentAdmission.cs; src/Runtime/SingPlus.Runtime/SipJobs/SipJobStageExecutionEligibility.cs; tests/SingPlus.Tests/SipJobs/SipJobSemanticTraceVocabulary.cs`
- **Exact current symbols:** `SipJobSegmentAdmissionVerifier; SipJobStageExecutionEligibilityVerifier; SipJobAdmissionParticipantKind`
- **Existing tests/evidence:** tests/SingPlus.Tests/SipJobs/Phase145ABarrierPlannerTests.cs; tests/SingPlus.Tests/SipJobs/Phase144ComposedAdmissionTests.cs
- **Current live behavior:** Generated sentry framework exists conceptually, but semantic co-design can be implemented with explicit contracts first.
- **Actual gap:** Early generator would freeze wrong boundaries and add complexity.
- **Required change:** Do not block P17. If later enabled, generator may emit sentry/test/quarantine skeletons only.
- **Authoritative owner:** Build tooling, never authority
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** If enabled: generated output cannot mutate/mint authority without existing owner calls.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P12 (parallel branch; not prerequisite for P17)
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P16-04 — Generated metadata cannot authorize

- **Phase:** P16
- **Classification:** `DEFERRED`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/SipJobs/SipJobBarrierPlanner.cs; src/Runtime/SingPlus.Runtime/SipJobs/SipJobSegmentAdmission.cs; src/Runtime/SingPlus.Runtime/SipJobs/SipJobStageExecutionEligibility.cs; tests/SingPlus.Tests/SipJobs/SipJobSemanticTraceVocabulary.cs`
- **Exact current symbols:** `SipJobSegmentAdmissionVerifier; SipJobStageExecutionEligibilityVerifier; SipJobAdmissionParticipantKind`
- **Existing tests/evidence:** tests/SingPlus.Tests/SipJobs/Phase145ABarrierPlannerTests.cs; tests/SingPlus.Tests/SipJobs/Phase144ComposedAdmissionTests.cs
- **Current live behavior:** Depends on optional CD-P16-03.
- **Actual gap:** No generated artifact exists yet for new contract.
- **Required change:** Mandatory negative architecture tests only when generator lands.
- **Authoritative owner:** CI
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Generated code never caches authority truth or replaces runtime legality.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P12 (parallel branch; not prerequisite for P17)
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
