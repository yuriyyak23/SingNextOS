# Cross-Project Delta

## SingNextOS changes

Required core changes:

1. Additive semantic extension envelope/canonicalization and digest binding around V1 contracts.
2. P01 memory sidecar and staged reference semantics.
3. P04 generation-bound DMA correlation and final revalidation.
4. P05 trace/refinement evaluator and differential harness.
5. Qualification/claim guards and architecture tests preventing duplicate owners/authority leakage.
6. QV1 end-to-end staged heterogeneous contour.

Later/optional changes:

- P02 persistence sidecars/recovery freshness;
- P03 temporal resource dimensions;
- P08 preemption lifecycle;
- P06 protected IFC;
- P07 locality evidence/planning;
- P09 trust predicates;
- P10 RAS subrange consequences;
- P12 energy dimensions;
- P11 lease protocol after single-host closure.

## HybridCPU ExternalRuntime changes

Prefer additive changes only:

- expose exact provider/session/manifest generation needed by v6 binding;
- expose translation invalidation/reset/containment evidence for selected DMA provider;
- expose visibility/publication-relevant provider evidence without claiming SingNext publication;
- optionally expose memory/temporal/preemption/trust/RAS guarantee sidecars for concrete adapters;
- preserve `CPU guard != provider admission != SingNext authority`.

No SingNext capability objects enter ExternalRuntime authority decisions.

## HybridCPU ISE/runtime changes

Core expectation: small `RUNTIME_ONLY`/instrumentation changes, not authority changes.

- retain `LegalityDecision` and `IRuntimeLegalityService` as top-level machine legality seam;
- add semantic trace event correlation if not already observable;
- expose existing retire/fence/visibility facts needed by the selected QV1 contour;
- add provider-specific safe-point/capture hooks only for P08;
- do not turn OS deadlines, labels, proofs or capabilities into GuardPlane authority.

## Compiler changes

Not required for QV1 correctness.

Optional later work:

- PCL evidence sidecar generation;
- footprint/alias/order/safe-point facts;
- compiler contract v6 compatibility preserved until an actual breaking compiler/runtime change is required.

## ISA changes

**None required by the corrected roadmap at this time.**

Per-phase classification:

| Phase | Impact |
|---|---|
| P01 | RUNTIME_ONLY / SIDEBAND_CONTRACT |
| P02 | SIDEBAND_CONTRACT |
| P03 | RUNTIME_ONLY / SIDEBAND_CONTRACT |
| P04 | SIDEBAND_CONTRACT / RUNTIME_ONLY |
| P05 | RUNTIME_ONLY instrumentation |
| P06 | SIDEBAND_CONTRACT optional |
| P07 | SIDEBAND_CONTRACT optional |
| P08 | RUNTIME_ONLY / SIDEBAND_CONTRACT; COMPILER_CONTRACT optional safe points |
| P09 | SIDEBAND_CONTRACT |
| P10 | SIDEBAND_CONTRACT |
| P11 | SIDEBAND_CONTRACT |
| P12 | SIDEBAND_CONTRACT |
| PCL | COMPILER_CONTRACT |

No row is `ISA_EXTENSION_REQUIRED`. Any future proposal requires a separate ADR showing why runtime legality, existing fences, sideband, provider contracts and generation checks are insufficient.
