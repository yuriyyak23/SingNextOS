# Traceability matrix

| Invariant | Phase | Task | Code | Test | Formal evidence | Claim level |
|---|---|---|---|---|---|---|
| C1 | P01/P04/P06 | CD-P01-01, CD-P04-04, CD-P06-01 | CapabilityAuthority + final sentry | unauthorized valid-descriptor negatives | TLA sentry model | RuntimeEnforced after implementation |
| C2 | P02/P05/P06 | CD-P02-03, CD-P05-01, CD-P06-01 | typed refinement evaluator + binding | mismatch/unknown-version tests | property/state exploration | StaticAdmission -> RuntimeEnforced when wired |
| C3 | P09/P10 | CD-P09-02/04, CD-P10-01 | ResourceBudgetAuthority + measurement binding | over-envelope/double-evidence/vector atomicity | resource arithmetic property tests | EnforcedUpperBound only per qualified class |
| C4 | P01/P04 | CD-P01-01, CD-P04-04 | Budget snapshot AuthorizesEffect=false; CapabilityAuthority separate | lease cannot authorize effect | — | RuntimeEnforced existing boundary |
| C5 | P07/P17 | CD-P07-03, CD-P17-03 | ExternalOperationAuthority + adapter + PublicationGate | Visible-without-decision staged negative | TLA optional publication race | ExecutableAdapter only after end-to-end run |
| C6 | P01/P07/P08 | CD-P01-02, CD-P08-01 | existing separate lifecycle states | stage-order/reordered receipt tests | state exploration | RuntimeEnforced existing parts |
| C7 | P04/P05/P06 | CD-P04-03, CD-P05-02, CD-P06-02 | generation-bound owners/ExternalGenerationSet | ABA/restart/provider drift | state exploration | RuntimeEnforced after integrated binding |
| C8 | P01/P09/P12 | CD-P01-03, CD-P09-02, CD-P12-04 | evidence-only DTOs/certificates | evidence cannot mutate owner | — | Architecture + RuntimeEnforced owner APIs |
| C9 | P12 | CD-P12-01 | ExternalOperationReplayContracts + fresh admission | replay after revocation | — | RuntimeEnforced when integrated |
| C10 | P11 | CD-P11-03/04 | provider-contour closure hook | late effect after close | TLA close/retire/cancel | Unsupported unless provider proves RuntimeEnforced |
| C11 | P09 | CD-P09-01/03 | chargeability matrix + measurement contract | replay/squash/overhead/residency | property tests | ModelOnly until per-class runtime evidence |
| C12 | P10/P12 | CD-P10-04, CD-P12-04 | ResourceScheduler + final sentry | stale topology/guarantee laundering | — | RuntimeEnforced final sentry |
