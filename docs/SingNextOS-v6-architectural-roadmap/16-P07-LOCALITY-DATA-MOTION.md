# P07 — Semantic Locality and Data-Motion Planning

**Depends on:** P01; uses P03/P12 optionally.  
**Status:** proposed v6 implementation phase.  
**Baseline:** SingNextOS `b06ec5b5980acdad3143393a72a3b69c4edb5cfb`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

## 1. Objective

Make data placement and movement first-class planner inputs without exposing physical topology as authority.

## 2. Existing anchors to reuse

- ComputePlanning provider candidates
- CXL placement/pooling
- semantic locality item previously proposed in co-design
- resource scheduler evidence

## 3. Target refactor

Add `LocalityEvidenceV1` and `DataMovementCostV1` with semantic classes/ranges for latency, bandwidth, migration/copy cost, contention and optional energy. Planner evaluates `Cost(provider, inputs, outputs)` and may choose move-data, move-compute or staged execution. Exact BDF/port/decoder/NUMA IDs remain provider-private.

## 4. Authority boundaries

Provider/platform owns topology observations. Planner owns policy ranking only. RegionAuthority owns movement/ownership transitions. ResourceBudgetAuthority owns charged movement resources.

## 5. Mandatory invariants

- stale locality evidence never authorizes access;
- data movement is an explicit operation with ownership/budget/effect lifecycle;
- planner cannot assume coherent access from proximity;
- zero-copy is selected only after P01/P04 semantics refine.

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

topology churn; cost drift; remote-memory fallback; movement cancellation; migration vs Region revoke; planner decision reproducibility; no topology leakage in SIP/application ABI.

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

Gate `V6-LOCALITY-PLANNING`; policy starts advisory and becomes performance-qualified only after measurements.
