# P01 — Reconstruct one-fact/one-owner authority map and existing machines

    **Depends on:** P00  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Закрепить фактических owners из live-кода и запретить semantic co-design слою создавать второй authority/ledger.

    ## Live anchors
    - `src/Runtime/SingPlus.Runtime/Capabilities/CapabilityAuthority.cs`
- `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs`
- `src/Runtime/SingPlus.Runtime/Regions/RegionAuthority.cs`
- `src/Runtime/SingPlus.Runtime/ExternalOperations/ExternalOperationAuthority.cs`
- `src/Runtime/SingPlus.Runtime/Services/EndpointSessionInvocationRegistry.cs`
- `src/Runtime/SingPlus.Runtime/Platform/PlatformAuthorityBridge.ExecutionPolicy.cs`

    ## Existing symbols/semantics
    - `CapabilityAuthority`
- `ResourceBudgetAuthority`
- `RegionAuthority`
- `ExternalOperationAuthority`
- `EndpointSessionInvocationRegistry`
- `PlatformAuthorityBridge`

    ## Corrections vs previous roadmap
    - No new AuthorityManager.
- PlatformAuthorityBridge remains binding/correlation, never capability minting.
- Planner/scheduler remain policy only.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P01-01 | Build owner/fact matrix from live code | `VERIFIED_EXISTING` |
| CD-P01-02 | Trace submit/complete/visible/publish/settle transitions | `PARTIALLY_EXISTING` |
| CD-P01-03 | Architecture tests reject duplicate owners/forbidden imports | `NEW_PROPOSED` |

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
