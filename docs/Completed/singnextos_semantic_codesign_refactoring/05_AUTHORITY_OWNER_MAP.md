# Authority owner map

| Fact | Authoritative owner | Not owned by |
|---|---|---|
| semantic/effect permission | CapabilityAuthority / existing effect authority path | obligations DTO, provider, CPU, planner |
| quantitative budget/reservation/settlement | ResourceBudgetAuthority | provider telemetry, lease descriptor, scheduler |
| Region ownership/use/mutation epoch | RegionAuthority | coherence mapping, provider, binding |
| session/invocation identity/lifecycle | EndpointSessionRegistry / EndpointSessionInvocationRegistry | SipJob optimizer, provider |
| external-operation identity/lifecycle | ExternalOperationAuthority | provider receipt, binding, planner |
| publication decision/system truth | existing response/publication owners + ExternalOperationAuthority transition | HybridCPU gate, provider completion |
| provider admission/local resources | provider | SingNext planner, CPU legality service |
| runtime legality/typed-slot execution/retire/replay legality | HybridCPU runtime | compiler metadata, SingNext obligation, provider capability bit |
| planner/scheduler policy | ComputePlanner/ResourceScheduler | all authority facts |
| OperationObligationsV1 | requirement snapshot; no authority | — |
| ExecutionGuaranteesV1 | provider/runtime claim; no OS authority | — |
| SemanticExecutionBinding | immutable exact correlation/context; no authority ledger | — |
| evidence/telemetry/certificates | evidence producer | all authority owners |
