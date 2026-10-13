# P00 — Freeze exact live baseline and evidence tuple

    **Depends on:** none  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Зафиксировать источники истины до любых semantic-contract изменений; отделить статически просмотренные тесты от реально выполненных.

    ## Live anchors
    - `global.json`
- `Directory.Build.props`
- `.packages/HybridCPU.ExternalRuntime.Contracts.1.14.0.nupkg`
- `HybridCPU_ExternalRuntime.Contracts/HybridCPU_ExternalRuntime.Contracts.csproj`
- `HybridCPU_ExternalRuntime.Contracts/ExternalOperationContracts.cs`

    ## Existing symbols/semantics
    - `SingNext SDK 11.0.100-rc.1.26425.128`
- `HybridCPU SDK 10.0.201`
- `HybridCPU.ExternalRuntime.Contracts 1.14.0`
- `ExternalOperationContract.Version 1.4.0`

    ## Corrections vs previous roadmap
    - Update baseline SHA everywhere.
- Do not claim tests passed.
- Retain package SHA-256 only as RECORDED_NOT_RECOMPUTED until bytes are rehashed.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P00-01 | Refresh exact SHAs/tree SHAs | `STALE_OR_INCORRECT` |
| CD-P00-02 | Recompute package/DLL hashes and lockfile identity at implementation start | `PARTIALLY_EXISTING` |
| CD-P00-03 | Inventory tests/contracts and record test-evidence level | `PARTIALLY_EXISTING` |

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
