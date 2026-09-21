# Master Refactoring Roadmap

## Dependency graph

```text
P00 Source freeze + executable inventory
 -> P01 Owner/state reconstruction + traceability
 -> P02 Semantic vocabulary + typed refinement algebra
 -> P03 Global operational semantics + irreversible boundary
 -> P04 SingNext OperationObligations
 -> P05 Provider ExecutionGuarantees + SemanticExecutionBinding
 -> P06 Generation-exact admission sentry
 -> P07 Effect/publication algebra
 -> P08 Visibility/memory/shared mutation/locality
 -> P09 Resource measurement/retire/charging
 -> P10 Multi-resource/QoS/donation/scheduling
 -> P11 Preemption/cancellation/effect containment
 -> P12 Replay/determinism/provider refinement/isolation/contention
 -> P13 Liveness/quarantine/recovery/fault model
 -> P14 Scalable authority ownership
 -> P15 Managed runtime/root/durable authority/IFC decision
 -> P16 SipJob refinement + declarative OperationContract
 -> P17 MatrixMultiply end-to-end HybridCPU contour
 -> P18 Formal/adversarial qualification + migration/cutover
```

## Maturity transition

- **Level 1 — Adapter integration:** already present for several external-operation/provider contours.
- **Level 2 — Semantic contract co-design:** target of P02-P06; obligations, guarantees, typed refinement and exact binding.
- **Level 3 — Enforcement co-design:** promoted only per contour after P07-P18 evidence.

### Plausible Level 3 without ISA changes
- staged memory output with exact Region generations, explicit visibility, OS publication decision, provider publication fence and exact resource upper-bound enforcement;
- bounded local HybridCPU compute where runtime legality, provider admission, upper-bound measurement/enforcement and cancellation/containment are executable;
- replay barriers and generation invalidation.

### Remain Level 2 unless provider proves enforcement
- direct-coherent write while alias exclusion remains future-gated;
- non-compensatable external network/MMIO/storage/service effects;
- strict cache-contention/memory-bandwidth isolation;
- guaranteed minimum capacity/deadline service;
- byzantine remote provider without a suitable trusted enforcement/attestation boundary.

## Phase gating
Every phase has one of the architectural verdicts `CLOSED`, `CLOSED_WITH_CORRECTIONS`, `BLOCKED`, `REDESIGN_REQUIRED`, `REMOVE_OR_MERGE`. In implementation, a dependent phase MUST NOT enable its feature contour while a prerequisite is `BLOCKED`.
