# vNext traceability matrix

| Requirement | Primary phase | Existing owner preserved | Key evidence |
|---|---|---|---|
| accounting != authority | P02 | ResourceBudgetAuthority | compatibility + negative auth tests |
| monotonic resource derivation | P01/P03 | new narrow TemporalResourceAuthority | property + race tests |
| no double spend | P04 | TemporalResourceAuthority | concurrency suite |
| effect cap != resource cap | P03/P05 | CapabilityAuthority | negative admission tests |
| SIP donation | P06 | EndpointSessionRegistry + TemporalResourceAuthority | session ABA/cancel tests |
| external bind | P07 | ExternalOperationAuthority | provider-loss/quarantine tests |
| provider-neutral compute | P08 | Compute planner + existing owners | stale plan/live revalidation |
| HybridCPU double/triple admission | P09 | SingNext owners + HybridCPU provider/CPU guard | adapter conformance |
| no ISA/lane leakage | P00/P09 | provider boundary | reflection/dependency tests |
| scheduler policy != authority | P10 | authority core unchanged | cache/revalidation tests |
| temporal upper bound | P11 | TemporalResourceAuthority | replenishment/preemption tests |
| SipJob not authority | P12 | existing SipJob owners | differential traces |
| DMA/CXL resource classes | P13 | existing DMA/CXL/Region owners | contour-specific tests |
| ambiguous usage fails closed | P07/P14 | extop + resource owners | disconnect/restart tests |
| telemetry != authority | P15 | telemetry only | replay/no-mint tests |
| bounded claims | P16 | qualification framework | gate matrix |

## Pre-existing requirements reused

The vNext implementation must continue satisfying SingCap-M and SipJob requirements, especially:

- single capability ledger;
- exact authority realm/incarnation;
- monotonic capability constraints;
- operation authority leases;
- Region generation/borrow/mutation separation;
- generated SIP sentries;
- no ambient authority;
- SipJob semantic-trace equivalence;
- provider scheduling semantics remain non-authoritative;
- provider-private topology does not enter SIP/Job ABI.
