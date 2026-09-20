# vNext traceability matrix — corrected

| Requirement / invariant | Primary phase(s) | Authoritative owner(s) | Minimum executable evidence |
|---|---|---|---|
| no second ledger | P00/P02 | `CapabilityAuthority`, `ResourceBudgetAuthority` | dependency/reflection + owner-map tests |
| accounting != permission | P01/P02 | both, orthogonal | negative admission tests |
| monotonic resource grant | P02 | `CapabilityAuthority` | property + revoke/derive race tests |
| conservation/no double spend | P03 | `ResourceBudgetAuthority` | concurrent split/reserve/settle tests |
| cross-owner atomic admission | P04 | existing owners | fault-injection at every boundary |
| SIP resource sentry | P05 | generated sentry + existing owners | bypass/reflection negatives |
| donation/no laundering | P06 | capability + session + budget owners | nested/ABA/cancel/property tests |
| provider ambiguity | P07/P14 | ExternalOperation + budget owners | disconnect/restart/quarantine tests |
| publication independence | P07/P12 | ExternalOperation/publication owners | completion/visibility/publication trace tests |
| ComputePlan not authority | P08 | planner non-authoritative | stale cache/revalidation tests |
| HybridCPU independent legality | P09 | HybridCPU runtime/provider + SingNext owners | adapter conformance |
| scheduler policy != authority | P10 | scheduler none | cache poisoning/revalidation tests |
| temporal upper bound | P11 | budget owner + qualified runtime/provider enforcement | overrun/preemption/replenishment tests |
| SipJob equivalence | P12 | existing ordinary owners | differential transition traces |
| dimensional resource families | P13 | budget owner + resource-specific effect owners | unit/dimension property tests |
| no restart resurrection | P14 | all exact owners | restart/ABA/checkpoint tests |
| telemetry != authority | P15 | telemetry none | replay/no-mint/no-release tests |
| bounded claims | P16 | qualification framework | exact evidence manifest |
| mixed-version safe cutover | P17 | compatibility owners | dual-stack/mixed-provider tests |

Every `VNX-*` invariant must map to at least one phase, one implementation owner and one executable negative test before P16 qualification.
