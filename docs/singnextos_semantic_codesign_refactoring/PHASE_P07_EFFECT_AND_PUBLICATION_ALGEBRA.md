# P07 — Refine effect/publication algebra on existing lifecycle

    **Depends on:** P06  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Не заменять существующий ExternalOperation lifecycle; добавить только orthogonal effect semantics required for contours beyond staged memory.

    ## Live anchors
    - `contracts/SingPlus.Contracts/ExternalOperations.cs`
- `src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs`
- `HybridCPU_ExternalRuntime.Contracts/ExternalOperationPublicationContracts.cs`

    ## Existing symbols/semantics
    - `ExternalEffectPolicy`
- `ExternalEffectBoundaryState`
- `ExternalPublicationPolicy`
- `PublicationPlan`
- `ReleasePlan`
- `ExternalOperationPublicationGate`

    ## Corrections vs previous roadmap
    - Published may remain lifecycle state; its meaning is contour-qualified by effect traits.
- No universal rollback for externally observable effects.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P07-01 | Model orthogonal effect traits without parallel lifecycle | `PARTIALLY_EXISTING` |
| CD-P07-02 | Map current ExternalEffectPolicy conservatively | `PARTIALLY_EXISTING` |
| CD-P07-03 | Exact SingNext publication decision + provider action for staged contour | `PARTIALLY_EXISTING` |
| CD-P07-04 | Effect-class lifecycle tests | `NEW_PROPOSED` |

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
