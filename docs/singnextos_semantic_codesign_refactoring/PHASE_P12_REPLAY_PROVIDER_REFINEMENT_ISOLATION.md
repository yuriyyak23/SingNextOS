# P12 — Replay, provider semantic refinement and isolation

    **Depends on:** P11  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Переиспользовать HybridCPU replay policy; fresh authorization/admission/legality остаются обязательными. ProviderBehavior refinement включает numeric/ordering/failure semantics.

    ## Live anchors
    - `HybridCPU_ExternalRuntime.Contracts/ExternalOperationReplayContracts.cs`
- `HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.RuntimeLegality.cs`
- `HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.TypedSlot.cs`
- `HybridCPU_ISE/CloseToHSL/Core/Execution/ExternalAccelerators/Capabilities/MatMulCapabilityProvider.cs`

    ## Existing symbols/semantics
    - `ExternalOperationReplayAction`
- `LegalityDecision`
- `TypedSlotFactStaging.CurrentMode`
- `MatMulCapabilityProvider`

    ## Corrections vs previous roadmap
    - Certificate/replay evidence != authority.
- Typed-slot metadata remains ValidationOnly until HybridCPU independently changes its runtime contract.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P12-01 | Define replay/determinism lattice by extending existing replay policy | `PARTIALLY_EXISTING` |
| CD-P12-02 | ProviderBehavior refines semantic operation contract including numeric semantics | `NEW_PROPOSED` |
| CD-P12-03 | Typed isolation subset checks without overclaim | `PARTIALLY_EXISTING` |
| CD-P12-04 | Guarantee laundering/provider-class switch tests | `NEW_PROPOSED` |

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
