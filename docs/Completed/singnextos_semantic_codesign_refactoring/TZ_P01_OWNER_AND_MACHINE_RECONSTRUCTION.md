# ТЗ P01 — Reconstruct one-fact/one-owner authority map and existing machines

    **Depends on:** P00  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`.

    ## CD-P01-01 — Build owner/fact matrix from live code

- **Phase:** P01
- **Classification:** `VERIFIED_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `anchors listed for P01`
- **Exact current symbols:** `CapabilityAuthority; ResourceBudgetAuthority; RegionAuthority; ExternalOperationAuthority`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/ExternalOperationLifecycleTests.cs; tests/SingPlus.Tests/Runtime/Phase05ResourceBudgetTests.cs
- **Current live behavior:** Live code already separates capability permission, quantitative budget, Region ownership/use, ExternalOperation lifecycle and publication/response truth.
- **Actual gap:** Roadmap must make negative ownership explicit for new concepts.
- **Required change:** Keep current owners; classify OperationObligations as requirement, ExecutionGuarantees as claim, SemanticExecutionBinding as correlation context.
- **Authoritative owner:** Existing per-fact owners
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Focused positive + negative + generation/race tests.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P00
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P01-02 — Trace submit/complete/visible/publish/settle transitions

- **Phase:** P01
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** SingNextOS
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs; src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs; src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs; src/Runtime/SingPlus.Runtime/Services/EndpointSessionInvocationRegistry.cs; src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.ExecutionPolicy.cs`
- **Exact current symbols:** `CapabilityAuthority; ResourceBudgetAuthority; RegionAuthority; ExternalOperationAuthority; EndpointSessionInvocationRegistry; PlatformAuthorityBridge`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/ExternalOperationLifecycleTests.cs; tests/SingPlus.Tests/Runtime/Phase05ResourceBudgetTests.cs
- **Current live behavior:** ExternalOperationState already separates Prepared/Admitted/Submitted/DeviceComplete/Visible/Published/Released; budget has Reserved/Bound/Consuming/Settling/Released/Quarantined/Reconciled.
- **Actual gap:** Cross-owner linearization and compensation need one normative map.
- **Required change:** Document exact transition owner, generation, linearization, evidence and negative authorization.
- **Authoritative owner:** Existing owners only
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** State-machine transition tests plus reordered/duplicate evidence negatives.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P00
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P01-03 — Architecture tests reject duplicate owners/forbidden imports

- **Phase:** P01
- **Classification:** `NEW_PROPOSED`
- **Repository:** Both
- **Exact current paths:** `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs; src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs; src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs; src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs; src/Runtime/SingPlus.Runtime/Services/EndpointSessionInvocationRegistry.cs; src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.ExecutionPolicy.cs`
- **Exact current symbols:** `CapabilityAuthority; ResourceBudgetAuthority; RegionAuthority; ExternalOperationAuthority; EndpointSessionInvocationRegistry; PlatformAuthorityBridge`
- **Existing tests/evidence:** tests/SingPlus.Tests/Runtime/ExternalOperationLifecycleTests.cs; tests/SingPlus.Tests/Runtime/Phase05ResourceBudgetTests.cs
- **Current live behavior:** Comments and APIs encode boundaries but there is no complete semantic co-design architecture guard.
- **Actual gap:** Future DTOs could accidentally gain mutation APIs/import OS identity into ISE.
- **Required change:** Add source/ABI architecture tests forbidding authority mutation from obligations/guarantees/binding and forbidding SingNext identities in HybridCPU public/ISA surfaces.
- **Authoritative owner:** Architecture/CI owner
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `CONTRACT_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Source/ABI guards; public API negative scan; no SingNext/RegionHandle/CapabilityId in HybridCPU ISA/core API.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P00
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
