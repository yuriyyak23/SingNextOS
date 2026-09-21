# P16 — SipJob semantic equivalence; declarative generation optional

    **Depends on:** P12 (parallel branch; not prerequisite for P17)  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Обычный SIP остаётся semantic oracle; SipJob — оптимизация. Declarative OperationContract generator не блокирует co-design vertical.

    ## Live anchors
    - `src/Runtime/SingPlus.Runtime/SipJobs/SipJobBarrierPlanner.cs`
- `src/Runtime/SingPlus.Runtime/SipJobs/SipJobSegmentAdmission.cs`
- `src/Runtime/SingPlus.Runtime/SipJobs/SipJobStageExecutionEligibility.cs`
- `tests/SingPlus.Tests/SipJobs/SipJobSemanticTraceVocabulary.cs`

    ## Existing symbols/semantics
    - `SipJobSegmentAdmissionVerifier`
- `SipJobStageExecutionEligibilityVerifier`
- `SipJobAdmissionParticipantKind`

    ## Corrections vs previous roadmap
    - P17 no longer depends on P16.
- SipJob provider execution remains future-gated until provider-neutral contract exists.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P16-01 | Define authority-visible trace vocabulary | `PARTIALLY_EXISTING` |
| CD-P16-02 | Differential/weak-bisimulation ordinary SIP vs fused path | `NEW_PROPOSED` |
| CD-P16-03 | Defer declarative OperationContract code generator until protocols stabilize | `DEFERRED` |
| CD-P16-04 | Generated metadata cannot authorize | `DEFERRED` |

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
