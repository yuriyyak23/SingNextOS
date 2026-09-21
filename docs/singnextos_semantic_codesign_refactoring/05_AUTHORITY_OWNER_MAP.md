# Authority Owner Map

| Fact | Owner before | Owner after | Change? |
|---|---|---|---|
| semantic/effect permission | `CapabilityAuthority` | same | no |
| resource-use permission | `CapabilityAuthority` constraint algebra | same | extend vocabulary only |
| quantitative reservation/settlement | `ResourceBudgetAuthority` | same | extend resource dimensions/escrow |
| Region ownership/use/mutation | `RegionAuthority` | same | optional mutation authority extension |
| session/invocation | existing registries | same | exact binding integration |
| external-operation identity/lifecycle | `ExternalOperationAuthority` | same | extend semantic binding/effect taxonomy |
| OS publication decision | existing publication/ExternalOperation owner | same | explicit decision record for staged contours |
| provider admission/resources | provider | same | richer guarantee evidence |
| HybridCPU runtime legality | HybridCPU legality service | same | optional exact semantic binding input/evidence |
| execute/retire/replay | HybridCPU runtime | same | evidence projection only |
| policy/placement | planner/scheduler | same | consumes semantic locality/guarantee observations |
| typed refinement | none | pure evaluator | **no owner/ledger** |
| obligations | none | immutable descriptor | **no owner/ledger** |
| guarantees | partial feature/receipt surfaces | immutable descriptor | **no new authority owner** |
| semantic execution binding | none | existing ExternalOperation/provider coordination context | **no new root authority** |

## Locking rule
No user/provider/service callback while capability, budget, Region, session, publication or ExternalOperation owner locks are held. Cross-owner work follows prepare/revalidate/commit with explicit compensation.
