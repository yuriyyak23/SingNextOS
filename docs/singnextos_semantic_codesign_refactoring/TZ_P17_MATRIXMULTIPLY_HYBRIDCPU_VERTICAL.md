# ТЗ P17 — Qualify one MatrixMultiply staged-output vertical using existing HybridCPU execution substrate

    **Depends on:** P12 + P13 safety subset; independent of P14/P15/P16 optional branches  
    **Baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`, HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`.

    ## CD-P17-01 — Add MatrixMultiply semantic operation and A/B/C Region operands in SingNext

- **Phase:** P17
- **Classification:** `VERIFIED_GAP`
- **Repository:** SingNextOS
- **Exact current paths:** `contracts/SingPlus.Contracts/ComputePlanning.cs; src/Runtime/SingPlus.Runtime/Compute/ComputePlanner.cs; HybridCPU_ISE/CloseToHSL/Core/ISA/Instructions/NonVmx/Lanes00_03Vector/MatrixTile/MtileMaccInstruction.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/MatrixTileFullPipelineHarness.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.MatrixTileRetireState.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Backends/ProviderNeutralExternalAcceleratorBackend.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Capabilities/MatMulCapabilityProvider.cs`
- **Exact current symbols:** `ComputeOperationKind (currently Copy/Transform/Reduce only); MatrixTile runtime/retire substrate; MatMulCapabilityProvider`
- **Existing tests/evidence:** HybridCPU_ISE.Tests/tests/Phase10MatrixTileRetirePublicationTests.cs; HybridCPU_ISE.Tests/tests/Phase11MatrixTileReplayRollbackConformanceTests.cs; HybridCPU_ISE.Tests/tests/L7SdcMatMulBackendTests.cs; new SingNext integration tests
- **Current live behavior:** ComputeOperationKind currently contains only Copy/Transform/Reduce. HybridCPU already has executable MatrixTile and L7 MatMul substrate.
- **Actual gap:** SingNext lacks semantic MatrixMultiply operation contract and 3-operand shape/Region uses.
- **Required change:** Add MatrixMultiply semantic descriptor in SingNext only; A/B read-only, C staged write with exact RegionUse/MutationEpoch. Do not add/change HybridCPU ISA instructions.
- **Authoritative owner:** SingNext semantic/effect + Region owners
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `NONE`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Shape/overflow/range/alias validation; C mutation epoch exact.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P12 + P13 safety subset; independent of P14/P15/P16 optional branches
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P17-02 — Map Matrix obligations to proven HybridCPU guarantees and exact binding

- **Phase:** P17
- **Classification:** `NEW_PROPOSED`
- **Repository:** Both
- **Exact current paths:** `contracts/SingPlus.Contracts/ComputePlanning.cs; src/Runtime/SingPlus.Runtime/Compute/ComputePlanner.cs; HybridCPU_ISE/CloseToHSL/Core/ISA/Instructions/NonVmx/Lanes00_03Vector/MatrixTile/MtileMaccInstruction.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/MatrixTileFullPipelineHarness.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.MatrixTileRetireState.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Backends/ProviderNeutralExternalAcceleratorBackend.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Capabilities/MatMulCapabilityProvider.cs`
- **Exact current symbols:** `ComputeOperationKind (currently Copy/Transform/Reduce only); MatrixTile runtime/retire substrate; MatMulCapabilityProvider`
- **Existing tests/evidence:** HybridCPU_ISE.Tests/tests/Phase10MatrixTileRetirePublicationTests.cs; HybridCPU_ISE.Tests/tests/Phase11MatrixTileReplayRollbackConformanceTests.cs; HybridCPU_ISE.Tests/tests/L7SdcMatMulBackendTests.cs; new SingNext integration tests
- **Current live behavior:** HybridCPU executable substrate exists, but semantic guarantee aggregate/refinement is new.
- **Actual gap:** Need no-stronger-than-evidence mapping.
- **Required change:** Prefer L7-SDC MatMul for the first staged contour because its live runtime has explicit staging/commit/fence surfaces. MatrixTile may be mapped only if its retire/publication behavior separately satisfies the same obligations. Resource dimensions may start AccountingOnly/Unsupported; do not require fake GuaranteedReservation.
- **Authoritative owner:** Provider guarantee + SingNext refinement
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `CONTRACT_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Guarantee mismatch, numeric mismatch, provider generation drift.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P12 + P13 safety subset; independent of P14/P15/P16 optional branches
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P17-03 — Execute via current runtime legality and staged provider publication path

- **Phase:** P17
- **Classification:** `PARTIALLY_EXISTING`
- **Repository:** Both
- **Exact current paths:** `contracts/SingPlus.Contracts/ComputePlanning.cs; src/Runtime/SingPlus.Runtime/Compute/ComputePlanner.cs; HybridCPU_ISE/CloseToHSL/Core/ISA/Instructions/NonVmx/Lanes00_03Vector/MatrixTile/MtileMaccInstruction.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/MatrixTileFullPipelineHarness.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.MatrixTileRetireState.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Backends/ProviderNeutralExternalAcceleratorBackend.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Capabilities/MatMulCapabilityProvider.cs`
- **Exact current symbols:** `ComputeOperationKind (currently Copy/Transform/Reduce only); MatrixTile runtime/retire substrate; MatMulCapabilityProvider`
- **Existing tests/evidence:** HybridCPU_ISE.Tests/tests/Phase10MatrixTileRetirePublicationTests.cs; HybridCPU_ISE.Tests/tests/Phase11MatrixTileReplayRollbackConformanceTests.cs; HybridCPU_ISE.Tests/tests/L7SdcMatMulBackendTests.cs; new SingNext integration tests
- **Current live behavior:** HybridCPU has runtime legality service, executable MatrixTile support, and Lane7 guarded staging/commit/fence plus ExternalOperation publication gate. SingNext provider adapter/lifecycle exist.
- **Actual gap:** No integrated semantic co-design sentry/binding route yet.
- **Required change:** Wire the first vertical through the L7-SDC MatMul staged backend/commit path so submit occurs only after four gates; ISE remains runtime legality authority. Staged C is withheld by the provider contour until the SingNext publication owner decides and the adapter invokes the exact staged action. MatrixTile is not assumed to provide withholding unless separately proven. Use existing runtime hooks; no ISA/core architecture change.
- **Authoritative owner:** Independent owners composed at adapter
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `RUNTIME_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** End-to-end staged happy path and every gate negative; Complete->Visible->Published distinct.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P12 + P13 safety subset; independent of P14/P15/P16 optional branches
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.

## CD-P17-04 — Collect bound usage/visibility/publication/settlement evidence

- **Phase:** P17
- **Classification:** `NEW_PROPOSED`
- **Repository:** Both
- **Exact current paths:** `contracts/SingPlus.Contracts/ComputePlanning.cs; src/Runtime/SingPlus.Runtime/Compute/ComputePlanner.cs; HybridCPU_ISE/CloseToHSL/Core/ISA/Instructions/NonVmx/Lanes00_03Vector/MatrixTile/MtileMaccInstruction.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/MatrixTileFullPipelineHarness.cs; HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.MatrixTileRetireState.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Backends/ProviderNeutralExternalAcceleratorBackend.cs; HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Capabilities/MatMulCapabilityProvider.cs`
- **Exact current symbols:** `ComputeOperationKind (currently Copy/Transform/Reduce only); MatrixTile runtime/retire substrate; MatMulCapabilityProvider`
- **Existing tests/evidence:** HybridCPU_ISE.Tests/tests/Phase10MatrixTileRetirePublicationTests.cs; HybridCPU_ISE.Tests/tests/Phase11MatrixTileReplayRollbackConformanceTests.cs; HybridCPU_ISE.Tests/tests/L7SdcMatMulBackendTests.cs; new SingNext integration tests
- **Current live behavior:** HybridCPU telemetry/retire evidence and SingNext settlement exist separately.
- **Actual gap:** Need exact correlation and claim-level promotion.
- **Required change:** Bind evidence to SemanticExecutionBinding; settle ResourceBudget only with accepted usage evidence; record visibility/publication receipts; evidence never authorizes.
- **Authoritative owner:** SingNext settlement/publication owners
- **Owners explicitly NOT changed:** CapabilityAuthority, ResourceBudgetAuthority, RegionAuthority, ExternalOperationAuthority, publication/response owner, HybridCPU runtime-legality owner, provider admission owner, planner/scheduler policy owner remain distinct unless explicitly named above.
- **State/generation impact:** New descriptors are immutable and generation-bound; no in-place upgrade. Relevant generation drift => stale/fail closed. Existing owner states remain authoritative.
- **Linearization point:** Final live-owner revalidation immediately before the relevant existing owner commit or provider submit/commit boundary. Provider callbacks must not execute while unrelated authority locks are held.
- **Concurrency races:** revoke, session close, Region mutation/ABA, provider restart/generation drift, duplicate submit/evidence, cancel/retire and publication/settlement races as applicable.
- **Failure/liveness behavior:** Pre-submit failure compensates only reversible reservations. After possible submit, uncertainty => quarantine/reconciliation; timeout is policy, not proof of no effect. Bounded liveness only under stated fault assumptions.
- **API/contract/versioning impact:** Additive/provider-neutral only; unknown version/class fails closed.
- **HybridCPU impact:** `RUNTIME_ONLY`
- **ISA impact:** `NONE` (mandatory). No new instruction, register, pointer width, tagged memory/pointer, OS handle or OS-specific VLIW encoding.
- **Tests required:** Cross-op evidence replay, duplicate completion, publication failure after consumption, provider loss.
- **Formal obligation:** None beyond executable/state tests unless noted.
- **Performance/scalability impact:** No new global lock; measure any hot-path regression.
- **Dependencies/blockers:** P12 + P13 safety subset; independent of P14/P15/P16 optional branches
- **Definition of Done:** code/spec change is anchored to exact source tuple; focused tests exist and are actually run before claim promotion; traceability/qualification updated; feature gate default remains safe; no authority duplication; no ISA change.
- **Prohibited shortcuts:** evidence=>authority; provider admission=>SingNext authorization; lease=>effect permission; completion=>visibility/publication; cancellation request=>closure; provider loss=>refund/reclaim; replay evidence=>fresh submit; planner hint=>admission; compiler metadata=>runtime legality; ISA/OS-handle coupling.
