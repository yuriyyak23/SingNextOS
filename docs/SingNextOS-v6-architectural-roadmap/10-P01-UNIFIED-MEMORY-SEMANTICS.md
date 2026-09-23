# P01 — Unified Heterogeneous Memory Semantics

**Depends on:** none.  
**Status:** proposed v6 implementation phase.  
**Baseline:** SingNextOS `b06ec5b5980acdad3143393a72a3b69c4edb5cfb`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

## 1. Objective

Make ownership, ordering, coherence, atomicity, visibility and sharing explicit cross-layer contracts without introducing a second memory owner.

## 2. Existing anchors to reuse

- `RegionAuthority`, `RegionUseRecord`, `MutationEpoch`
- `ExternalVisibilityRequirement`, `ComputePublicationPath`
- HybridCPU acquire/release and memory-consistency test surfaces
- MatrixTile/Lane6/Lane7 staged publication paths

## 3. Target refactor

Introduce provider-neutral `MemoryObligationsV1` and `MemoryExecutionGuaranteesV1`. Minimum vocabulary: access class (`Private`, `ReadShared`, `SingleWriter`, `SharedAtomic`, `StagedOutput`), ordering (`Relaxed`, `Acquire`, `Release`, `AcquireRelease`, `Sequential`), coherence class, atomicity unit, visibility scope and alias-exclusion requirement. Extend `RegionAuthority` only for qualified shared/atomic use modes; do not create a new shared-memory ledger.

## 4. Authority boundaries

SingNextOS owns Region access/ownership and publication. HybridCPU owns runtime memory-order legality and execution. Providers own coherence/atomic support. Compiler may describe required ordering/footprints but does not grant access.

## 5. Mandatory invariants

- shared mutable access requires exact RegionUse + provider guarantee + runtime legality;
- `Coherent` never implies ownership or alias exclusion;
- direct coherent output is derived only when alias, visibility and publication obligations refine;
- unknown atomic width/order fails closed;
- ordinary staged output remains the universal fallback.

## 6. SingNextOS work

- extend existing contracts under `contracts/SingPlus.Contracts` rather than creating a parallel semantic namespace;
- implement owner-side runtime transitions only in the current owning subsystem;
- add typed correlation/version/generation fields to `OperationObligationsV2` and/or `SemanticExecutionBindingV2` only when needed;
- keep planner, caches, telemetry and evidence non-authoritative;
- add feature gate default-OFF and mixed-version fallback;
- update repository architecture-policy tests when a new production layer/interface is introduced.

## 7. HybridCPU-v2 / provider work

- extend `HybridCPU_ExternalRuntime.Contracts` additively with provider-neutral guarantees/evidence only when the existing ABI cannot represent the semantic;
- preserve `LegalityDecision` / `IRuntimeLegalityService` / `GuardPlane` as runtime legality authority;
- use existing MatrixTile, lane6 DSC, lane7 external-accelerator, virtualization and SecureCompute seams independently; one contour's qualification does not promote another;
- compiler changes express semantic requirements/evidence but never mint authority;
- no provider-private CXL/IOMMU/lane/topology IDs in ordinary SingNext application/SIP contracts.

## 8. ISA impact

**Default: NONE.** Runtime/ISE/provider/compiler changes are permitted in existing legality, execution, measurement, fence, replay, retire and adapter seams. Any ISA extension requires a separate ADR proving an unavoidable enforcement gap.

## 9. Required tests

Memory litmus/property tests; CPU/device race tests; stale `MutationEpoch`; alias conflict; wrong ordering; shared-atomic negative cases; ordinary-vs-direct trace comparison; MatrixTile/DSC/L7 conformance where supported.

All tests record exact source/package/toolchain/provider tuple. Negative/race/fault cases are mandatory; type/DTO presence is not implementation evidence.

## 10. Exit criteria

- no authority owner is duplicated;
- every mutable generation/state transition has an identified owner and linearization point;
- unknown mandatory versions/classes fail closed;
- mixed-version fallback is explicit and does not weaken authority;
- executable evidence exists for the exact enabled contour;
- performance or security claims do not exceed evidence;
- no provider/user code runs under SingNext authority locks.

## 11. Feature gates

Gate `V6-MEMORY-SEMANTICS`; `V6-SHARED-ATOMIC-REGION` stays OFF until exact provider+ISE evidence exists.
