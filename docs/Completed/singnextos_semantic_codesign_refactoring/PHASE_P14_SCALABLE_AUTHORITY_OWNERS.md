# P14 — Scalability: measure before sharding

    **Depends on:** P10 (measurement can run in parallel with P11-P13)  
    **Roadmap verdict:** `DEFERRED`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Сохранить one logical fact -> one owner, но не делать преждевременный distributed authority redesign.

    ## Live anchors
    - `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs`
- `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs`
- `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs`

    ## Existing symbols/semantics
    - `ResourceBudgetAuthority._gate`
- `RegionRecord.Gate`

    ## Corrections vs previous roadmap
    - P14 is not a blocker for staged MatrixMultiply unless benchmark fails agreed SLO.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P14-01 | Measure contention before redesign | `NEW_PROPOSED` |
| CD-P14-02 | Shard only if measured gate fails target | `DEFERRED` |
| CD-P14-03 | Escrow/local reservation only if sharding requires it | `DEFERRED` |
| CD-P14-04 | Prove no cross-shard double-spend/ABA if optional branch lands | `BLOCKED` |

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
