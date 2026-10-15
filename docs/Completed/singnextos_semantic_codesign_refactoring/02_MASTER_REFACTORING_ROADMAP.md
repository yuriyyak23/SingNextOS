# Master refactoring roadmap

- **P00 — Freeze exact live baseline and evidence tuple** — verdict `CLOSED_WITH_CORRECTIONS`; depends: none; critical-path: yes
- **P01 — Reconstruct one-fact/one-owner authority map and existing machines** — verdict `CLOSED_WITH_CORRECTIONS`; depends: P00; critical-path: yes
- **P02 — Define minimal typed semantic vocabulary and refinement algebra** — verdict `CLOSED_WITH_CORRECTIONS`; depends: P01; critical-path: yes
- **P03 — Specify global operational semantics without a new runtime owner** — verdict `CLOSED_WITH_CORRECTIONS`; depends: P02; critical-path: yes
- **P04 — Implement OperationObligations as immutable non-authoritative snapshot** — verdict `CLOSED_WITH_CORRECTIONS`; depends: P03; critical-path: yes
- **P05 — Add provider/runtime guarantees and exact SemanticExecutionBinding** — verdict `CLOSED_WITH_CORRECTIONS`; depends: P04; critical-path: yes
- **P06 — Integrate four-gate final admission sentry** — verdict `CLOSED_WITH_CORRECTIONS`; depends: P05; critical-path: yes
- **P07 — Refine effect/publication algebra on existing lifecycle** — verdict `CLOSED_WITH_CORRECTIONS`; depends: P06; critical-path: yes
- **P08 — Visibility, Region integration and locality** — verdict `CLOSED_WITH_CORRECTIONS`; depends: P07; critical-path: yes
- **P09 — Resource measurement, chargeability and settlement** — verdict `CLOSED_WITH_CORRECTIONS`; depends: P08; critical-path: yes
- **P10 — Reuse existing atomic vector reservation; minimal QoS/multi-resource changes** — verdict `CLOSED_WITH_CORRECTIONS`; depends: P09; critical-path: yes
- **P11 — Preemption, cancellation and provider-contour containment** — verdict `CLOSED_WITH_CORRECTIONS`; depends: P10; critical-path: yes
- **P12 — Replay, provider semantic refinement and isolation** — verdict `CLOSED_WITH_CORRECTIONS`; depends: P11; critical-path: yes
- **P13 — Fault, liveness, quarantine and cold-restart reconciliation** — verdict `CLOSED_WITH_CORRECTIONS`; depends: P12; critical-path: yes
- **P14 — Scalability: measure before sharding** — verdict `DEFERRED`; depends: P10 (measurement can run in parallel with P11-P13); critical-path: no/conditional
- **P15 — Managed admission, roots, durable identity and IFC scope** — verdict `CLOSED_WITH_CORRECTIONS`; depends: P13 (can run in parallel with P14/P16); critical-path: no/conditional
- **P16 — SipJob semantic equivalence; declarative generation optional** — verdict `CLOSED_WITH_CORRECTIONS`; depends: P12 (parallel branch; not prerequisite for P17); critical-path: no/conditional
- **P17 — Qualify one MatrixMultiply staged-output vertical using existing HybridCPU execution substrate** — verdict `CLOSED_WITH_CORRECTIONS`; depends: P12 + P13 safety subset; independent of P14/P15/P16 optional branches; critical-path: yes
- **P18 — Qualification, promotion, evidence tuple and rollback** — verdict `CLOSED_WITH_CORRECTIONS`; depends: P17 + P13; include P14/P15/P16 only if those optional claims are enabled; critical-path: yes

## Dependency-correct implementation order

**Critical staged-Matrix path:** P00 → P01 → P02 → P03 → P04 → P05 → P06 → P07 → P08(staged subset) → P09 → P10 → P11(minimum cancellation/containment semantics) → P12 → P13(safety/reconciliation subset) → P17 → P18.

**Parallel/conditional branches:** P14 scalability (only after measurement), P15 managed/root/durable/IFC clarification, P16 SipJob equivalence and optional generator. None is allowed to block P17 unless its specific feature is enabled in the qualified contour.

This corrects the prior dependency in which P17 was downstream of the optional declarative OperationContract work in P16.
