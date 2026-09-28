# Audit Correction Log

This file records how the adversarial audit findings were incorporated. A recommendation is not accepted blindly when it conflicts with the source-of-truth hierarchy or the one-fact/one-owner rule.

| Audit finding/recommendation | Disposition | Correction in this package |
|---|---|---|
| Preserve one-fact/one-owner separation | ACCEPT | made normative across all files |
| Avoid TemporalAuthority / PowerAuthority | ACCEPT | temporal/energy use ResourceBudgetAuthority + provider enforcement/evidence |
| IFC may be over-broad | ACCEPT | P06 becomes optional protected-contour feature |
| PCL must remain evidence-only | ACCEPT | proof scope reduced; runtime fallback mandatory |
| Hard RT/determinism claims too strong | ACCEPT | hard RT/WCET remain unsupported/future-gated |
| DMA revoke/translation race is high risk | ACCEPT | P04 centers generation-bound mapping and effect-ambiguity closure |
| RAS needs subrange damage/quarantine | ACCEPT | P10 extends Region consequences, not a new ownership ledger |
| Multi-host should be late/future-gated | ACCEPT | removed from critical path |
| P01 and P04 should be merged | REJECT AS OWNER-CONFLATING | they remain separate: Region semantics vs platform/IOMMU translation state |
| P07 and P08 should be merged | REJECT AS SEMANTICALLY ORTHOGONAL | locality policy and preemption lifecycle remain separate; overlap removed |
| Provider should be modeled as ProviderAuthority translating rights | REJECT | provider admission is an independent provider-local gate, not a SingNext authority root |
| New HybridCPU/ISA fences may be required | MODIFIED | no ISA change is assumed; runtime/sideband/fence reuse first, separate ADR if a real gap appears |
| Add AddressSpaceGeneration | ACCEPT WITH BOUNDARY | process/address-space incarnation is owner-specific; correlation object is non-authoritative |
| Compiler should emit semantic proofs | ACCEPT WITH SCOPE CUT | only bounded immutable facts; no live authority/generation claims |
| P05 can be finalizing phase | MODIFIED | P05 becomes cross-cutting semantic spine with an early minimal core |
| First vertical should use heterogeneous workload | ACCEPT | staged-output MatrixTile/DSC-capable contour, but no shared mutable/persistence/RT claims |
