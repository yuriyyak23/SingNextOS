# v6 Traceability Matrix

| Requirement | Owner | Phase | Primary gate | Key evidence |
|---|---|---|---|---|
| memory ordering/coherence separation | Region + provider + HybridCPU legality | P01 | V6-MEMORY-SEMANTICS | litmus + trace refinement |
| shared atomic Region use | RegionAuthority | P01 | V6-SHARED-ATOMIC-REGION | race/atomic tests |
| durable publication | publication + persistence provider | P02 | V6-DURABLE-OUTPUT | crash-point matrix |
| temporal reservation/guarantee separation | ResourceBudgetAuthority + provider | P03 | V6-TEMPORAL-LEASES | conservation + timing evidence |
| exact DMA mapping composition | Region/device/address-space/provider owners | P04 | V6-DMA-TRANSLATION-BINDING | reset/unmap/race tests |
| cross-layer trace refinement | specification/evidence only | P05 | V6-FORMAL-REFINEMENT | TLA+/property/differential traces |
| IFC flow constraints | capability + IFC policy | P06 | V6-IFC | laundering/declassification tests |
| topology-neutral locality planning | provider evidence + planner | P07 | V6-LOCALITY-PLANNING | topology churn/perf |
| preempt/resume semantics | invocation/external-op + provider | P08 | V6-PREEMPTION | safe-point/race tests |
| measured device trust predicate | security policy + evidence producer | P09 | V6-DEVICE-ATTESTATION | stale/replay/reset tests |
| partial failure/RAS | provider health + SingNext consequence owners | P10 | V6-RAS-PARTIAL-FAILURE | poison/reconfig tests |
| remote delegated authority | original logical owner | P11 | V6-MULTIHOST-LEASES | partition/expiry/conservation |
| energy/thermal budgets | budget + provider evidence | P12 | V6-ENERGY-BUDGETS | measurement/throttle tests |
| compiler semantic proof | compiler evidence + verifier | PCL | V6-PROOF-CARRYING-LOWERING | mutation/forgery/differential tests |
