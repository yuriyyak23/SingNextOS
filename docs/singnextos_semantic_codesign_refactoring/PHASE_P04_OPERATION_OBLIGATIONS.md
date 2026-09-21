# P04 — Implement OperationObligations as immutable non-authoritative snapshot

    **Depends on:** P03  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Собрать минимальный semantic requirement snapshot из уже существующих owner facts без передачи authority.

    ## Live anchors
    - `contracts/SingPlus.Contracts/ComputePlanning.cs`
- `contracts/SingPlus.Contracts/ExternalOperations.cs`
- `contracts/SingPlus.Contracts/ResourceControlModelContracts.cs`
- `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs`

    ## Existing symbols/semantics
    - `ComputeIntent`
- `OperationPreparation`
- `OperationAdmissionSnapshot`
- `ResourceEnvelopeV1`
- `RegionUseDescriptor`
- `MutationEpoch`

    ## Corrections vs previous roadmap
    - Reuse ResourceEnvelopeV1; do not create second resource descriptor.
- IFC stays out of critical path.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P04-01 | Add versioned OperationObligationsV1 vocabulary | `NEW_PROPOSED` |
| CD-P04-02 | Compose obligations from exact live owner snapshots | `NEW_PROPOSED` |
| CD-P04-03 | Revalidate obligation identity at final sentry | `PARTIALLY_EXISTING` |
| CD-P04-04 | Negative tests: descriptor possession never authorizes | `NEW_PROPOSED` |

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
