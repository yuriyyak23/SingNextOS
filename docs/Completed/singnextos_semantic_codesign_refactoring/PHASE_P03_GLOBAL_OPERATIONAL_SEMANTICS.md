# P03 — Specify global operational semantics without a new runtime owner

    **Depends on:** P02  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Свести existing owner machines в формальную композицию; GlobalState — модель/спецификация, не runtime ledger.

    ## Live anchors
    - `src/Runtime/SingPlus.Runtime/VNext/ResourceAdmissionProtocol.cs`
- `src/Runtime/SingPlus.Runtime/VNext/ExternalOperationResourceBinding.cs`
- `src/Runtime/SingPlus.Runtime/SipJobs/SipJobSegmentAdmission.cs`
- `contracts/SingPlus.Contracts/ExternalOperations.cs`

    ## Existing symbols/semantics
    - `prepare/revalidate/commit`
- `BudgetReservationState`
- `ExternalOperationState`
- `SipJobAdmissionPhase`

    ## Corrections vs previous roadmap
    - No global lock implied by one logical owner.
- Unknown post-submit outcome is not rollback-safe.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P03-01 | Specify GlobalState as product of owner states | `NEW_PROPOSED` |
| CD-P03-02 | Define cross-owner commit points and compensation | `PARTIALLY_EXISTING` |
| CD-P03-03 | Model revoke/reserve/session/Region/provider races | `NEW_PROPOSED` |

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
