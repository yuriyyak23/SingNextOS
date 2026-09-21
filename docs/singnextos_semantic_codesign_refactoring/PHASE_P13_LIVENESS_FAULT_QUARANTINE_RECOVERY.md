# P13 — Fault, liveness, quarantine and cold-restart reconciliation

    **Depends on:** P12  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Определить closure/reclaim policy для uncertain states, не обещая bounded liveness при partition/malicious provider.

    ## Live anchors
    - `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs`
- `src/Runtime/SingPlus.Runtime/Checkpointing/RuntimeKernel.Checkpointing.cs`
- `src/Runtime/SingPlus.Runtime/Tracing/TraceReplayEngine.cs`
- `contracts/SingPlus.Contracts/DeadlineCancellationContracts.cs`

    ## Existing symbols/semantics
    - `QuarantineLease`
- `ReconcileLease`
- `RestoreOrdinaryCheckpoint`
- `TraceReplayEngine.CorrelateExternalRuntimeEvidence`
- `CancellationDisposition.ProviderClosurePending`

    ## Corrections vs previous roadmap
    - unknown -> quarantine is only safety start, not liveness solution.
- Fresh admission after restore is mandatory.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P13-01 | Classify fault/trust models | `NEW_PROPOSED` |
| CD-P13-02 | Define eventual settlement/containment/reclaim policy | `PARTIALLY_EXISTING` |
| CD-P13-03 | Bound retries only under explicit fault assumptions | `NEW_PROPOSED` |
| CD-P13-04 | Cold restart without authority resurrection | `PARTIALLY_EXISTING` |

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
