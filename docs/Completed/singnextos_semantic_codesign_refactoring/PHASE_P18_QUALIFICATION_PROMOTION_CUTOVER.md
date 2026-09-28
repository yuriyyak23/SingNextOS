# P18 — Qualification, promotion, evidence tuple and rollback

    **Depends on:** P17 + P13; include P14/P15/P16 only if those optional claims are enabled  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Закрыть adversarial matrix, execute exact test suite, promote only demonstrated contour/claim levels and retain rollback gate.

    ## Live anchors
    - `all touched contracts/runtime/tests`
- `src/Runtime/SingPlus.Runtime/VNext/VNextFeatureGates.cs`
- `HybridCPU_ExternalRuntime.Tests`
- `HybridCPU_ISE.Tests`

    ## Existing symbols/semantics
    - `VNextFeatureGates.IsEnabled currently always false`
- `claim levels ModelOnly/StaticAdmission/RuntimeEnforced/ExecutableAdapter/EnforcedUpperBound/GuaranteedReservation/ProductionQualified`

    ## Corrections vs previous roadmap
    - CLOSED here means roadmap specification complete, not implementation qualified.
- No ProductionQualified claim is made by this artifact.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P18-01 | Run full adversarial matrix + selected model checking | `NEW_PROPOSED` |
| CD-P18-02 | Record exact SHA/package/runtime/gate/test tuple | `NEW_PROPOSED` |
| CD-P18-03 | Promote only exact Matrix/staged contour | `NEW_PROPOSED` |
| CD-P18-04 | Synchronize docs/traceability and retain rollback path | `NEW_PROPOSED` |

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
