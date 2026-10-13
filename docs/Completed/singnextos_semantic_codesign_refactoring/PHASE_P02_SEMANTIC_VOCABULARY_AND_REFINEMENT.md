# P02 — Define minimal typed semantic vocabulary and refinement algebra

    **Depends on:** P01  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Переиспользовать существующие typed resource subset semantics и добавить только отсутствующие dimensions; refines — типизированная relation, не equality.

    ## Live anchors
    - `contracts/SingPlus.Contracts/ResourceControlModelContracts.cs`
- `HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs`
- `HybridCPU_ISE/CloseToHSL/Core/Pipeline/Safety/SafetyVerifier.Types.cs`

    ## Existing symbols/semantics
    - `ResourceEnvelopeV1.IsSubset`
- `ResourceUseConstraintV1.IsSubset`
- `ExternalEffectClass`
- `ExternalVisibilityRequirement`
- `ExternalCancellationMode`
- `TypedSlotFactStaging`

    ## Corrections vs previous roadmap
    - Do not duplicate ResourceEnvelopeV1.
- Do not encode refinement as string/feature-bit coincidence.
- Compiler typed-slot facts remain validation metadata, not runtime legality.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P02-01 | Define dimension-specific partial orders | `PARTIALLY_EXISTING` |
| CD-P02-02 | Define Mandatory/Advisory/Unsupported semantics | `NEW_PROPOSED` |
| CD-P02-03 | Property-test refinement monotonicity and version failure | `NEW_PROPOSED` |

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
