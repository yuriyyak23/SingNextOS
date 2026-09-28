# P08 — Visibility, Region integration and locality

    **Depends on:** P07  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Расширять RegionAuthority, а не создавать второй memory/ownership ledger; staged Matrix contour first.

    ## Live anchors
    - `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs`
- `contracts/SingPlus.Contracts/ExternalOperations.cs`
- `contracts/SingPlus.Contracts/ComputePlanning.cs`

    ## Existing symbols/semantics
    - `RegionUseRecord`
- `MutationEpoch`
- `ExternalVisibilityRequirement`
- `ComputePublicationPath`

    ## Corrections vs previous roadmap
    - mapping != coherence != ownership != visibility != publication.
- Staged output is critical-path contour.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P08-01 | Define visibility classes and Region transition mapping | `PARTIALLY_EXISTING` |
| CD-P08-02 | Keep direct-coherent contour gated until alias-exclusion evidence | `VERIFIED_EXISTING` |
| CD-P08-03 | Shared-mutable/atomic authority extension only if a real contour requires it | `DEFERRED` |
| CD-P08-04 | Semantic locality model without hardware IDs in authority API | `NEW_PROPOSED` |

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
