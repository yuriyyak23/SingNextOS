# ТЗ P15 — Managed admission, roots, durable identity and IFC scope

    **Depends on:** P13 (can run in parallel with P14/P16)  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`.

    ## CD-P15-01 — Specify managed execution assumptions only from live AdmissionVerifier/evidence

- **Phase:** P15
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `tools/SingPlus.Admission/AdmissionVerifier.cs; tests/SingPlus.Tests/Admission/AdmissionVerifierTests.cs; src/Runtime/SingPlus.Runtime/Checkpointing/RuntimeKernel.Checkpointing.cs; docs/Completed/SingCap-Refactoring/roadmap/PHASE_09_MANAGEDCAP_ADMISSION_AND_CLOSED_WORLD.md`
- **Exact current symbols:** `AdmissionVerifier; RestoreOrdinaryCheckpoint; CapabilityAuthority`
- **Existing tests/evidence:** AdmissionVerifierTests.cs; ManagedCap NativeAOT fixture exists; no live ManagedCap runtime type was found in current tree inventory
- **Current live behavior:** AdmissionVerifier is live; ManagedCap appears in completed roadmap/fixtures, but current tree inventory did not expose a live runtime type named ManagedCap.
- **Actual gap:** Do not build a new contract on a stale symbol assumption.
- **Required change:** Document actual JIT/NativeAOT/admission assumptions per claim; if a live ManagedCap implementation is needed, first locate/prove it or treat as new work.
- **Authoritative owner:** Admission/qualification
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** JIT evidence != NativeAOT evidence; exact profile/source tuple.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P13 (can run in parallel with P14/P16)
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P15-02 — Root/service mint policy uses existing CapabilityAuthority

- **Phase:** P15
- **Classification:** `NEW_PROPOSED`
- **Repository:** SingNextOS
- **Exact current paths:** `tools/SingPlus.Admission/AdmissionVerifier.cs; tests/SingPlus.Tests/Admission/AdmissionVerifierTests.cs; src/Runtime/SingPlus.Runtime/Checkpointing/RuntimeKernel.Checkpointing.cs; docs/Completed/SingCap-Refactoring/roadmap/PHASE_09_MANAGEDCAP_ADMISSION_AND_CLOSED_WORLD.md`
- **Exact current symbols:** `AdmissionVerifier; RestoreOrdinaryCheckpoint; CapabilityAuthority`
- **Existing tests/evidence:** AdmissionVerifierTests.cs; ManagedCap NativeAOT fixture exists; no live ManagedCap runtime type was found in current tree inventory
- **Current live behavior:** CapabilityAuthority is the semantic permission owner; no need for a second RootAuthority ledger.
- **Actual gap:** Bootstrap mint rules should be explicit if semantic co-design requires them.
- **Required change:** Specify trusted bootstrap policy that calls existing capability mint APIs; discovery/manifest is intent, not authority.
- **Authoritative owner:** CapabilityAuthority
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Untrusted manifest/provider discovery cannot mint authority.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P13 (can run in parallel with P14/P16)
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P15-03 — Durable identity+policy+principal -> fresh ephemeral authority

- **Phase:** P15
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `tools/SingPlus.Admission/AdmissionVerifier.cs; tests/SingPlus.Tests/Admission/AdmissionVerifierTests.cs; src/Runtime/SingPlus.Runtime/Checkpointing/RuntimeKernel.Checkpointing.cs; docs/Completed/SingCap-Refactoring/roadmap/PHASE_09_MANAGEDCAP_ADMISSION_AND_CLOSED_WORLD.md`
- **Exact current symbols:** `AdmissionVerifier; RestoreOrdinaryCheckpoint; CapabilityAuthority`
- **Existing tests/evidence:** AdmissionVerifierTests.cs; ManagedCap NativeAOT fixture exists; no live ManagedCap runtime type was found in current tree inventory
- **Current live behavior:** Checkpoint restore already requires fresh process generation and fresh admission.
- **Actual gap:** Persistent policy/principal authentication is not a generic execution binding feature.
- **Required change:** Codify rule and reuse fresh admission. Persistent records never deserialize directly into live capability/lease/binding.
- **Authoritative owner:** Identity/admission/capability owners
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Restore/restart stale capability and stale binding rejection.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P13 (can run in parallel with P14/P16)
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P15-04 — Keep IFC out of critical path

- **Phase:** P15
- **Classification:** `DEFERRED`
- **Repository:** SingNextOS
- **Exact current paths:** `tools/SingPlus.Admission/AdmissionVerifier.cs; tests/SingPlus.Tests/Admission/AdmissionVerifierTests.cs; src/Runtime/SingPlus.Runtime/Checkpointing/RuntimeKernel.Checkpointing.cs; docs/Completed/SingCap-Refactoring/roadmap/PHASE_09_MANAGEDCAP_ADMISSION_AND_CLOSED_WORLD.md`
- **Exact current symbols:** `AdmissionVerifier; RestoreOrdinaryCheckpoint; CapabilityAuthority`
- **Existing tests/evidence:** AdmissionVerifierTests.cs; ManagedCap NativeAOT fixture exists; no live ManagedCap runtime type was found in current tree inventory
- **Current live behavior:** Capabilities constrain access but current co-design thesis does not require general noninterference label propagation.
- **Actual gap:** Generic IFC would be major new authority/policy subsystem.
- **Required change:** Defer unless a concrete cross-domain confidentiality requirement cannot be met by existing authority/isolation.
- **Authoritative owner:** N/A
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** None until a concrete IFC requirement is accepted.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P13 (can run in parallel with P14/P16)
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
