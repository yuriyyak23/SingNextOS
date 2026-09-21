# P05 — Add provider/runtime guarantees and exact SemanticExecutionBinding

    **Depends on:** P04  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Расширить ExternalRuntime provider-neutral contract только там, где enforcement/evidence реально принадлежит runtime/provider; связать exact generations без нового authority ledger.

    ## Live anchors
    - `HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs`
- `HybridCPU_ExternalRuntime.Contracts/ExternalOperationAdmissionBindingContracts.cs`
- `HybridCPU_ExternalRuntime.Contracts/ExternalOperationPublicationContracts.cs`
- `HybridCPU_ExternalRuntime/ExternalOperationAdapterSession.cs`
- `HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Types.cs`

    ## Existing symbols/semantics
    - `ExternalGenerationSet`
- `ExternalOperationAdmissionBinding`
- `ExternalOperationPublicationGate`
- `LegalityDecision`
- `LegalityAuthoritySource`

    ## Corrections vs previous roadmap
    - SemanticExecutionBinding is distinct from SecureExecutionBinding and ExternalOperationAdmissionBinding.
- No CPU-owned OS publication authority.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P05-01 | Add provider-neutral ExecutionGuaranteesV1 descriptor | `NEW_PROPOSED` |
| CD-P05-02 | Bind exact OS operation/provider/runtime contract context | `NEW_PROPOSED` |
| CD-P05-03 | Conservative compatibility mapping for Contracts 1.14.0 | `NEW_PROPOSED` |
| CD-P05-04 | Cross-repo ABI/conformance tests | `NEW_PROPOSED` |

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
