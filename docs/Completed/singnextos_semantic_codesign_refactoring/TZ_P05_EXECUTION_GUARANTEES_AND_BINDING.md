# ТЗ P05 — Add provider/runtime guarantees and exact SemanticExecutionBinding

    **Depends on:** P04  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`.

    ## CD-P05-01 — Add provider-neutral ExecutionGuaranteesV1 descriptor

- **Phase:** P05
- **Classification:** `NEW_PROPOSED`
- **Repository:** HybridCPU Contracts + SingNext adapter
- **Exact current paths:** `HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdmissionBindingContracts.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationPublicationContracts.cs; HybridCPU_ExternalRuntime/ExternalOperationAdapterSession.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Types.cs`
- **Exact current symbols:** `ExternalGenerationSet; ExternalOperationAdmissionBinding; ExternalOperationPublicationGate; LegalityDecision; LegalityAuthoritySource`
- **Existing tests/evidence:** HybridCPU_ExternalRuntime.Tests/ExternalOperationContractTests.cs; HybridCPU_ExternalRuntime.Tests/ExternalOperationAdapterSessionTests.cs
- **Current live behavior:** HybridCPU already exposes exact lifecycle, cancellation, visibility, generation and CPU guard semantics, but no full guarantee descriptor.
- **Actual gap:** SingNext cannot distinguish measured/model-only/enforced guarantees dimension-by-dimension.
- **Required change:** Add additive contract with claim level + execution/measurement/enforcement/preemption/isolation/contention/visibility/publication/replay/determinism/containment/failure/locality/retire-evidence classes. Unsupported is explicit.
- **Authoritative owner:** Provider/runtime describes guarantees; no OS authority
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `CONTRACT_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Public ABI excludes SingNext/private topology; every non-Unsupported guarantee maps to enforcement/evidence source.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P04
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P05-02 — Bind exact OS operation/provider/runtime contract context

- **Phase:** P05
- **Classification:** `NEW_PROPOSED`
- **Repository:** SingNextOS
- **Exact current paths:** `HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdmissionBindingContracts.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationPublicationContracts.cs; HybridCPU_ExternalRuntime/ExternalOperationAdapterSession.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Types.cs`
- **Exact current symbols:** `ExternalGenerationSet; ExternalOperationAdmissionBinding; ExternalOperationPublicationGate; LegalityDecision; LegalityAuthoritySource`
- **Existing tests/evidence:** HybridCPU_ExternalRuntime.Tests/ExternalOperationContractTests.cs; HybridCPU_ExternalRuntime.Tests/ExternalOperationAdapterSessionTests.cs
- **Current live behavior:** HybridCPU ExternalOperationAdmissionBinding already combines CPU guard + provider admission, but it is not an OS semantic binding and must not be duplicated.
- **Actual gap:** Need OS-side exact correlation between obligations, guarantees, lease, provider generation and execution contract.
- **Required change:** Add SemanticExecutionBinding in SingNext coordination layer referencing ExternalOperation generation, provider generation set, execution-contract version, obligation/guarantee ids, resource envelope identity, measurement, visibility/publication, replay/determinism, containment epoch if present. Immutable; no mutation/authority methods.
- **Authoritative owner:** SingNext coordination/binding owner
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `CONTRACT_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Cross-operation receipt replay, provider restart, binding ABA, contract-version mismatch.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P04
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P05-03 — Conservative compatibility mapping for Contracts 1.14.0

- **Phase:** P05
- **Classification:** `NEW_PROPOSED`
- **Repository:** SingNextOS
- **Exact current paths:** `HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdmissionBindingContracts.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationPublicationContracts.cs; HybridCPU_ExternalRuntime/ExternalOperationAdapterSession.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Types.cs`
- **Exact current symbols:** `ExternalGenerationSet; ExternalOperationAdmissionBinding; ExternalOperationPublicationGate; LegalityDecision; LegalityAuthoritySource`
- **Existing tests/evidence:** HybridCPU_ExternalRuntime.Tests/ExternalOperationContractTests.cs; HybridCPU_ExternalRuntime.Tests/ExternalOperationAdapterSessionTests.cs
- **Current live behavior:** Current 1.14.0 proves exact generation/lifecycle, independent CPU/provider admission, staged publication precondition, cancellation modes and replay no-direct-submit semantics. It does not automatically prove arbitrary resource QoS/preemption/containment.
- **Actual gap:** Existing package must not be overclaimed.
- **Required change:** Map only proven dimensions; mark absent dimensions Unsupported/ModelOnly. No synthetic guarantee from capability bit alone.
- **Authoritative owner:** Compatibility adapter
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Mapping tests against 1.14.0 public contract; unknown/new fields reject until explicit adapter.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P04
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P05-04 — Cross-repo ABI/conformance tests

- **Phase:** P05
- **Classification:** `NEW_PROPOSED`
- **Repository:** Both
- **Exact current paths:** `HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdmissionBindingContracts.cs; HybridCPU_ExternalRuntime.Contracts/ExternalOperationPublicationContracts.cs; HybridCPU_ExternalRuntime/ExternalOperationAdapterSession.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Types.cs`
- **Exact current symbols:** `ExternalGenerationSet; ExternalOperationAdmissionBinding; ExternalOperationPublicationGate; LegalityDecision; LegalityAuthoritySource`
- **Existing tests/evidence:** HybridCPU_ExternalRuntime.Tests/ExternalOperationContractTests.cs; HybridCPU_ExternalRuntime.Tests/ExternalOperationAdapterSessionTests.cs
- **Current live behavior:** HybridCPU already has ABI exclusion tests and exact adapter tests; SingNext semantic layer is new.
- **Actual gap:** Need two-repo compatibility gate.
- **Required change:** Add version tuple tests and provider conformance fixtures; no direct project reference from SingNext into HybridCPU ISE.
- **Authoritative owner:** CI/qualification
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `CONTRACT_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Package/API baseline, exact request/receipt, stale generation, no private topology leakage.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P04
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
