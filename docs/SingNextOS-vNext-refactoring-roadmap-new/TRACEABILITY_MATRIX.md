# vNext traceability matrix — qualified contours

`VNEXT_TRACEABILITY.json` is the synchronized machine-readable source. Every row names an owner, live implementation contour, executable test lane, evidence, tuple, exact claim and exclusions.

| Invariant | Phase | Owner / disposition | Claim |
|---|---|---|---|
| VNX-001 | P00 | capability and budget owners; no duplicate ledger | StaticAdmission |
| VNX-002 | P01 | budget owner; accounting is not permission | RuntimeEnforced |
| VNX-003 | P02 | capability owner; narrow resource permission | RuntimeEnforced |
| VNX-004 | P03 | single quantitative budget owner | RuntimeEnforced |
| VNX-005 | P02 | monotonic capability derivation | RuntimeEnforced |
| VNX-006 | P02 | no sibling-union amplification | RuntimeEnforced |
| VNX-007 | P03 | linear lease generation | RuntimeEnforced |
| VNX-008 | P02/P03/P06/P07/P14 | exact boundary generations | RuntimeEnforced |
| VNX-009 | P06 | provenance-preserving donation | RuntimeEnforced |
| VNX-010 | P03/P06 | one charging lineage | RuntimeEnforced |
| VNX-011 | P07/P08/P10/P15 | evidence never authority | RuntimeEnforced |
| VNX-012 | P07/P14 | provider ambiguity quarantines | RuntimeEnforced |
| VNX-013 | P02/P04/P07 | resource and effect authority independent | RuntimeEnforced |
| VNX-014 | P04/P07 | Region safety independent | RuntimeEnforced |
| VNX-015 | P07/P12 | completion/visibility/publication/release separate | RuntimeEnforced |
| VNX-016 | P12 | SipJob materializes ordinary boundary | RuntimeEnforced |
| VNX-017 | P08/P10 | planner and scheduler are policy only | RuntimeEnforced |
| VNX-018 | P08/P09 | CPU/provider legality independent | StaticAdmission |
| VNX-019 | P00/P09/P15 | no provider-private public ABI | StaticAdmission |
| VNX-020 | P00/P09 | ISA-change category empty | ModelOnly |
| VNX-021 | P04 | prepare/revalidate/commit/compensate | RuntimeEnforced |
| VNX-022 | P07 | settlement independent of publication | RuntimeEnforced |
| VNX-023 | P11/P16 | claims separated; production withheld | StaticAdmission |
| VNX-024 | P01/P03/P13 | typed checked dimensional arithmetic | RuntimeEnforced |
| VNX-025 | P14 | no authority resurrection | RuntimeEnforced |
| VNX-026 | P15 | replay cannot resubmit or charge | RuntimeEnforced |
| VNX-027 | P07/P15 | exact correlation is evidence only | RuntimeEnforced |
| VNX-028 | P03/P06/P07/P14 | terminal policies for enabled contour | RuntimeEnforced |

All `FG-VNX-*` gates remain OFF. Runtime claims describe directly exercised owner behavior, not enabled rollout status. Host/JIT evidence does not transfer to HybridCPU, NativeAOT, other providers or resource families. `ProductionQualified`, temporal guarantees, hardware and cold-process durable recovery remain excluded.
