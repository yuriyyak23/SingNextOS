# HybridCPU-v2 + Compiler CXL 3.x/4.x Refactoring Roadmap

Status: proposed cross-project implementation roadmap. Baseline refreshed after the Virtualization + SecureCompute + CXL end-to-end audit on 2026-09-13.

## Scope

This roadmap covers future changes owned by `yuriyyak23/HybridCPU-v2`, including `HybridCPU-v2/Compilers`, and the external contract that SingNextOS expects from that project. SingNextOS implementation work is split into `../virtualization-securecompute-cxl-refactoring-roadmap/`.

CXL remains a fabric/memory/provider substrate below existing authority. It is not a new CPU lane, ISA authority class, replay authority, or application-visible identity.

## Target dependency

```text
HybridCPU compiler semantic intent
  -> HybridCPU ISA / descriptor carriers
  -> HybridCPU runtime legality + replay model
  -> versioned ExternalRuntime child + secure contracts
  -> SingNextOS platform authority / external-operation lifecycle
  -> SingNextOS CXL providers
  -> CXL.io / CXL.cache / CXL.mem
```

## Mandatory invariants

- CXL must not become a new `LegalityAuthoritySource`.
- evidence != authority; replay certificate != runtime permission.
- coherence != ownership; completion != visibility; visibility != publication; publication != ownership return.
- stale domain/mapping/security/provider generation fails closed before the next provider effect or publication.
- `ProviderUnavailable != ProviderClosed != ProviderEffectContained`; reclaim requires Closed or Contained.
- direct coherent output and zero-copy remain proven optimizations, not ABI promises.
- Virtualization and SecureCompute remain separate authorities and compose through exact bindings rather than a monolithic secure-VM authority root.
- compiler IR may express semantic requirements but may not mint runtime authority or expose CXL topology.

## Existing mechanisms to preserve

- `LegalityDecision` / `LegalityAuthoritySource` and `GuardPlane`;
- structural/replay certificates and rollback contours;
- lane6 `DmaStreamCompute` and lane7 L7-SDC without merging their semantics;
- external-operation descriptors, fences and staged commit/publication;
- mapping/domain epoch checks;
- retire/publication separation;
- `HybridCPU_ExternalRuntime` and V3 child-domain contracts;
- neutral SecureCompute admission in ISE;
- compiler typed-slot/resource admission and bundle construction.

## Phase index

1. `00-baseline-gap-matrix-and-decisions.md`
2. `01-cross-project-runtime-contract.md`
3. `02-legality-guardplane-and-authority.md`
4. `03-replay-effect-model-and-invalidation.md`
5. `04-lane7-l7-sdc-external-operation-bridge.md`
6. `05-lane6-dmastreamcompute-integration.md`
7. `06-commit-visibility-publication-and-fences.md`
8. `07-compiler-ir-semantic-intent.md`
9. `08-compiler-lowering-admission-and-bundling.md`
10. `09-externalruntime-singnextos-adapter.md`
11. `10-cxl-memory-and-coherent-access-semantics.md`
12. `11-fault-reset-reconfiguration-and-cancellation.md`
13. `12-validation-negative-tests-and-conformance.md`
14. `13-migration-order-pr-slicing-and-exit-criteria.md`
15. `14-securecompute-externalruntime-contract.md` — ProductionSecure external ABI and exact closure.
16. `15-secure-virtualization-composition-and-events.md` — exact child/secure composition and event publication.
17. `16-compiler-secure-virtual-intent-and-cxl-boundary.md` — compiler semantic modernization without topology leakage.
18. `17-cross-project-validation-and-exit-criteria.md` — new end-to-end negative matrix and completion gates.

## New closure condition

The roadmap is not complete when only child virtualization is executable. Completion additionally requires a versioned owner-bound SecureCompute external ABI, exact secure+virtualized resource composition, exhaustive secure-operation policy dispatch, and tests proving CXL-backed secure virtual execution fails closed on stale generations, reconfiguration and ambiguous closure.

## Implementation evidence

- `09_10_12_SINGNEXTOS_INTEGRATION_EVIDENCE.md` records the executable
  SingNextOS adapter, same-image provider matrix and repository deployment
  conformance.
- `00_17_COMPLETENESS_CORRECTNESS_AUDIT.md` records the current H00-H17 audit,
  the repaired external-operation disposition mapping and the completed H17
  repository-model exit matrix. No physical-provider or `ProductionSecure`
  claim is made.
