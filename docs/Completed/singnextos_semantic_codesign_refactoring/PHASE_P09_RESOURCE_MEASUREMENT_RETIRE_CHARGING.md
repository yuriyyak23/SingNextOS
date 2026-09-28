# P09 — Resource measurement, chargeability and settlement

    **Depends on:** P08  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Сделать charging explicit per resource class; HybridCPU telemetry is settlement evidence, not ResourceBudget authority.

    ## Live anchors
    - `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs`
- `src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs`
- `contracts/SingPlus.Contracts/ResourceControlModelContracts.cs`
- `HybridCPU_ISE/CloseToHSL/Core/Execution/DmaStreamCompute/DmaStreamComputeTelemetry.cs`
- `HybridCPU_ISE/CloseToHSL/Core/Pipeline/Retire/Evidence/CPU_Core.PipelineExecution.Retire.cs`

    ## Existing symbols/semantics
    - `SettleLease`
- `BudgetReservationState`
- `ResourceAssuranceV1`
- `DmaStreamComputeBackendTelemetry`

    ## Corrections vs previous roadmap
    - Consumption != retired work.
- Measurement != EnforcedUpperBound; upper bound != GuaranteedReservation.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P09-01 | Define chargeability matrix per ResourceClassV1 | `NEW_PROPOSED` |
| CD-P09-02 | Bind usage evidence to measurement contract identity | `NEW_PROPOSED` |
| CD-P09-03 | Reconcile replay/squash/retry/SMT/blocked time explicitly | `PARTIALLY_EXISTING` |
| CD-P09-04 | Enforce envelope and duplicate evidence invariants | `PARTIALLY_EXISTING` |

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
