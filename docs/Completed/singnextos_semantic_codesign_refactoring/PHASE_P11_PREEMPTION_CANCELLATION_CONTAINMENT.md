# P11 — Preemption, cancellation and provider-contour containment

    **Depends on:** P10  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Переиспользовать существующие cancellation modes; generic global EffectEpoch удалить. ISE/runtime hooks допустимы только как provider-local containment implementation без ISA changes.

    ## Live anchors
    - `contracts/SingPlus.Contracts/DeadlineCancellationContracts.cs`
- `contracts/SingPlus.Contracts/ExternalOperations.cs`
- `HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs`
- `HybridCPU_ExternalRuntime.Tests/ExternalOperationAdapterSessionTests.cs`
- `HybridCPU_ISE/CloseToHSL/Core/Execution/DmaStreamCompute/DmaStreamComputeRuntime.cs`
- `HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Fences/AcceleratorFenceModel.cs`

    ## Existing symbols/semantics
    - `CancellationDisposition`
- `ExternalCancellationSupport`
- `ExternalCancellationMode`
- `ExternalOperationCancellationReceipt`
- `AcceleratorFenceCoordinator`

    ## Corrections vs previous roadmap
    - ISA impact NONE.
- No CHERI/tagged pointers/new instructions/OS handles in ISA.
- Containment closure is provider-local proof, not OS authority.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P11-01 | Define semantic preemption/cancellation obligations with conservative mapping | `PARTIALLY_EXISTING` |
| CD-P11-02 | Extend guarantees only for executable cancellation/preemption classes | `NEW_PROPOSED` |
| CD-P11-03 | Replace global EffectEpoch with contour-scoped containment closure hook | `NEW_PROPOSED` |
| CD-P11-04 | Cancellation/retire/replay/containment fault tests | `NEW_PROPOSED` |

    ## Exit criteria
    - Every task has exact live-code anchors or an explicit VERIFIED_GAP/NEW_PROPOSED boundary.
    - No descriptor/evidence becomes authority and no existing owner is duplicated.
    - Unknown versions/classes fail closed for Mandatory semantics.
    - Tests are recorded as exists/static/run; only actual runs promote claim level.
    - HybridCPU changes stay contract/runtime/provider-specific as stated; **ISA impact NONE**.
    - Feature-gated rollout preserves baseline behavior while OFF.

    ## Formal/performance
    - Formal: As required by task cards.
    - Performance: No claim without measurement.
