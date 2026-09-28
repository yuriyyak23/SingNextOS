# Full Gap Matrix

Severity: `CRITICAL` blocks architecture closure; `HIGH` blocks a feature contour; `MEDIUM` bounds claims/operability; `LOW` is optional/performance scope.

| Requirement | Roadmap / desired claim | Live implementation status | Gap | Severity | Owner | Required action |
|---|---|---|---|---|---|---|
| one fact -> one owner | no duplicate authority | VERIFIED_EXISTING foundation | future phases could accidentally add ledgers | CRITICAL | architecture | CI/architecture guards + owner map |
| capability permission | single CapabilityAuthority | VERIFIED_EXISTING | none for v6 core | LOW | CapabilityAuthority | preserve; no new ledger |
| Region ownership/use | ownership distinct from mapping/coherence | VERIFIED_EXISTING / PARTIAL for new shared modes | shared mutable/atomic use semantics not qualified | HIGH | RegionAuthority | P01 explicit use modes + tests |
| resource quantitative truth | ResourceBudgetAuthority | VERIFIED_EXISTING | temporal/energy dimensions incomplete | MEDIUM | ResourceBudgetAuthority | P03/P12 additive dimensions |
| publication truth | separate from completion/visibility | VERIFIED_EXISTING foundation | heterogeneous visibility evidence not uniform | HIGH | publication owner | P01/QV1 explicit chain |
| V1 semantic binding | obligations/guarantees/binding exist | VERIFIED_EXISTING | insufficient vocabulary for v6 dimensions | HIGH | semantic contract layer | additive sidecars, not blanket V2 |
| contract versioning | fail closed on unknown mandatory semantics | PARTIAL | canonical sidecar negotiation absent | HIGH | contract/qualification | C0 canonicalization |
| HybridCPU runtime legality | independent gate | VERIFIED_EXISTING | must remain independent under v6 | CRITICAL | IRuntimeLegalityService/GuardPlane | integration tests/architecture guards |
| provider admission | independent provider gate | VERIFIED_EXISTING concept | risk of conflating with SingNext authority | CRITICAL | provider runtime | explicit independent result/generation |
| memory ordering | provider/CPU/device semantics refine obligations | PARTIAL | no single qualified CPU↔Matrix/DSC/L7 algebra | HIGH | provider + HybridCPU | P01 litmus + adapter |
| atomics/shared mutation | exact qualified semantics | MODEL_ONLY/partial local support | alias/atomicity/coherence cross-layer gap | HIGH | Region + provider | keep gate OFF |
| direct coherent output | safe without staging | UNSUPPORTED generally | completion/visibility/publication gap | HIGH | provider + publication | staged first; later evidence |
| address-space incarnation | generation-bound DMA correlation | PARTIAL | exact owner/generation binding not standardized | HIGH | process/VM owner | P04 bind generation |
| IOMMU mapping generation | stale mapping fails closed | PARTIAL/provider-specific | invalidation completion/reuse ABA | CRITICAL for zero-copy | IOMMU/provider | P04 adapter/evidence |
| PASID/SVA | correlation only | PARTIAL/model vocabulary | risk of authority laundering | HIGH | provider | non-authoritative binding + tests |
| DMA after revoke | no unauthorized new effects | PARTIAL | already-submitted effect closure ambiguous | CRITICAL | ExternalOperation + provider | effect-possible/quarantine protocol |
| page fault during DMA | revalidate live access | UNSUPPORTED generically | remap could violate RegionUse | HIGH | provider/address-space/Region | P04 fault hook |
| refinement partial order | machine-checkable relation | PARTIAL existing evaluator | missing new dimensions/trace projection | CRITICAL | spec/evaluator | P05 minimal core |
| provider trace projection | allowed SingNext semantic trace | NEW_PROPOSED | no common trace schema | HIGH | qualification | SemanticTraceEventV1 |
| durability chain | Complete→Visible→Published→Persisted→Durable | MODEL_ONLY/provider-specific | persistence domain/crash ordering | HIGH | storage/provider/publication | P02 staged named provider |
| reboot semantics | fresh authority after reboot | VERIFIED architecture principle | must be enforced in persistence paths | HIGH | boot/runtime owners | recovery freshness tests |
| temporal accounting | resource budget conservation | VERIFIED_EXISTING foundation | new units/donation/interference incomplete | MEDIUM | ResourceBudgetAuthority | P03 |
| hard deadline | guaranteed service | UNSUPPORTED | no generic schedulability/enforcement proof | HIGH | provider+scheduler analysis | FutureGated |
| WCET | hard upper bound | UNSUPPORTED | no executable proof | HIGH | provider/tooling | FutureGated |
| preemption | safe-point provider contract | PARTIAL/provider-specific | no uniform semantics | HIGH | provider/HybridCPU | P08 per provider |
| stateful resume | fresh-safe resume | MODEL_ONLY | stale captured state/reset/publication races | HIGH | provider + operation owner | separate gate |
| IFC | protected flow enforcement | MODEL_ONLY | generic subsystem unjustified | MEDIUM | capability + policy | de-scope P06 |
| locality planning | provider-neutral cost evidence | PARTIAL planner | stale/private topology leakage risk | LOW | planner/provider | P07 advisory |
| device trust | evidence predicates | PARTIAL models | production freshness/reset/assignment not qualified | HIGH | trust producer + device owner | P09 named adapter |
| RAS subrange damage | safe partial failure | PARTIAL | granular Region consequence model incomplete | HIGH | RegionAuthority | P10 subrange state |
| provider loss | no unsafe reclaim | PARTIAL quarantine concepts | closure may be ambiguous | CRITICAL | ExternalOperation + Region | P10 quarantine/reconcile |
| energy measurement | measured energy | PARTIAL telemetry | attribution/enforcement distinction | LOW | provider | P12 evidence semantics |
| energy budget | committed quantitative cap | NEW_PROPOSED | no generic enforcement | MEDIUM | ResourceBudgetAuthority/provider | only named enforceable contour |
| compiler proof | evidence-only lowering facts | PARTIAL proof culture | verifier language/cache invalidation absent | MEDIUM | compiler/verifier | PCL minimal language |
| compiler proof as permission | forbidden | architecture principle | risk if optimization bypasses checks | CRITICAL | architecture/runtime | negative gate tests |
| multi-host delegation | one owner + epoch lease | MODEL_ONLY | partition/ABA/expiry/reclaim protocol absent | HIGH | original owner | P11 FutureGated |
| distributed resource conservation | escrow/suballocation | MODEL_ONLY | no executable distributed ledger/escrow | HIGH | ResourceBudgetAuthority | formalize before code |
| real ISA extension | only if unavoidable | no proven gap | none demonstrated | LOW | ISA governance | default NONE; ADR if needed |
| emulator qualification | software semantics | PARTIAL/feasible | needs integrated vertical and trace evidence | HIGH | qualification | QV1 |
| hardware qualification | physical semantics | UNSUPPORTED generally | no named platform campaigns for new v6 claims | HIGH | qualification/platform | hardware matrix later |
| production security | exact tuple + supply chain | PARTIAL architecture | operational/hardware/security evidence incomplete | HIGH | qualification/security | production gate after hardware |
