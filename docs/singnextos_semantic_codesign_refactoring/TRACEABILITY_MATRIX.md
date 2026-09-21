# Traceability Matrix

| Requirement / invariant | Phase(s) | Primary live anchor | Evidence target |
|---|---|---|---|
| C1/CD-001 independent authorization | P01,P03,P06 | `CapabilityAuthority`, `ResourceAdmissionProtocol` | revoke/admit race tests |
| C2/CD-002 typed refinement | P02,P04,P05,P06 | VERIFIED_GAP today | refinement property + negative tests |
| C3 resource bound | P09,P10 | `ExternalOperationResourceBinding`, `ResourceBudgetAuthority` | over-envelope/double-settlement tests |
| C4 resource != effect authority | P01,P06 | `OperationAuthority`, budget contracts | composition negative tests |
| C5 staged publication gate | P07,P17 | `ExternalOperationAuthority.Publish`, HybridCPU `ExternalOperationPublicationGate` | visible-without-decision blocked |
| C6 execute/retire/complete/visible/published separation | P03,P07,P09 | ExternalOperation lifecycle + HybridCPU retire | lifecycle state exploration |
| C7 generation drift | P04-P06 | `OperationDependencySnapshot`, `ExternalGenerationSet` | drift at each boundary |
| C8 evidence != authority | all | DTO comments/tests and owner APIs | mutation-negative tests |
| C9 replay != resubmit | P12 | HybridCPU replay policy | replay-after-revoke |
| C10 effect containment | P11,P13 | VERIFIED_GAP EffectEpoch | close-race TLA+/fault tests |
| C11 charging semantics | P09 | live Submitted->Consuming resource binding | chargeability matrix |
| C12 scheduler separation | P10,P12 | `ResourceScheduler` | stale hint/guarantee laundering |
| effect/publication algebra | P07 | existing effect policy/lifecycle | effect-class lifecycle suite |
| visibility/Region/coherence | P08 | `RegionAuthority`, compute planner | ABA/mutation/direct-coherent gates |
| multi-resource deadlock | P10 | existing typed envelope/budget owner | atomic vector/escrow model |
| liveness/quarantine | P13 | external/resource quarantine | bounded recovery tests |
| scalable logical owners | P14 | owner implementations | manycore contention/conservation |
| Managed runtime TCB | P15 | `AdmissionVerifier` | TCB assumption/evidence matrix |
| durable fresh authority | P15 | checkpoint + CapabilityAuthority | restart no-resurrection tests |
| SipJob oracle/refinement | P16 | SipJob admission/barrier verifier | authority-visible differential trace |
| MatrixMultiply Level 3 | P17 | compute/external/HybridCPU seams | exact E2E qualification |
| no ISA coupling | all | VNX-020 + source scans | forbidden-name/API/ISA scan |
