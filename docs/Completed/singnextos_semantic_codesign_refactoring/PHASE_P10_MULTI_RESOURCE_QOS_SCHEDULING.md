# P10 — Reuse existing atomic vector reservation; minimal QoS/multi-resource changes

    **Depends on:** P09  
    **Roadmap verdict:** `CLOSED_WITH_CORRECTIONS`  
    **Frozen baseline:** SingNextOS `cb6a94c314055e0712b8d9b8382ca1d146fe1c0e`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

    ## Objective
    Не добавлять banker/escrow/hierarchical machinery без доказанного gap: ResourceBudgetAuthority уже атомарно резервирует IReadOnlyList<BudgetAmount>.

    ## Live anchors
    - `src/Runtime/SingPlus.Runtime/Budgets/ResourceBudgetAuthority.cs`
- `src/Runtime/SingPlus.Runtime/VNext/ResourceDonationProtocol.cs`
- `contracts/SingPlus.Contracts/ResourceBudgetContracts.cs`
- `contracts/SingPlus.Contracts/ResourceControlModelContracts.cs`
- `src/Runtime/SingPlus.Runtime/VNext/ResourceScheduler.cs`

    ## Existing symbols/semantics
    - `ResourceBudgetAuthority.Reserve`
- `SplitLease`
- `SettleLease`
- `ResourceDonationBinding.PriorityCeiling`
- `PriorityRank`
- `ResourceScheduler`

    ## Corrections vs previous roadmap
    - No new banker by default.
- Reservation != scheduling eligibility != enforced upper bound != minimum/deadline guarantee.

    ## Tasks
    | Task | Work item | Classification |
    |---|---|---|
    | CD-P10-01 | Verify/reuse atomic multi-dimensional budget reservation and add only missing envelope mapping | `PARTIALLY_EXISTING` |
| CD-P10-02 | Remove standalone banker/escrow protocol from critical path | `REMOVE_OR_MERGE` |
| CD-P10-03 | Preserve existing donation assurance/priority ceilings | `VERIFIED_EXISTING` |
| CD-P10-04 | Scheduler uses evidence but cannot authorize/refine mismatch | `VERIFIED_EXISTING` |

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
