# P06 — Integrate four-gate final admission sentry

    **Depends on:** P05  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Перед необратимой границей совместить independent SingNext authorization, provider admission, typed refinement и current HybridCPU runtime legality.

    ## Live anchors
    - `src/Runtime/SingPlus.Runtime/VNext/ResourceAdmissionProtocol.cs`
- `src/Runtime/SingPlus.Runtime/VNext/ComputePlanResourceAdmission.cs`
- `src/Runtime/SingPlus.Runtime/ExternalOperations/HybridCpuExternalOperationProvider.cs`
- `HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdmissionBindingContracts.cs`

    ## Existing symbols/semantics
    - `IComputeSubmissionLegalityGate`
- `PrepareResourceExternalAdmission`
- `SubmitResourceExternalAdmission`
- `ExternalOperationAdmissionBinding.Evaluate`

    ## Corrections vs previous roadmap
    - Admission rule is conjunction, not ownership merge.
- HybridCPU RuntimeLegality remains independently authoritative.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P06-01 | Extend existing prepare/revalidate/commit path | `PARTIALLY_EXISTING` |
| CD-P06-02 | Bind refinement result to exact binding generation | `NEW_PROPOSED` |
| CD-P06-03 | Guarantee one submit-start winner + pre-submit compensation | `PARTIALLY_EXISTING` |
| CD-P06-04 | Race tests revoke/session-close/provider-drift | `NEW_PROPOSED` |

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
