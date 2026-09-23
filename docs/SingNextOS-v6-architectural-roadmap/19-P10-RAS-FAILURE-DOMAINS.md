# P10 — RAS and Failure-Domain Algebra

**Depends on:** P01 + P04 + P05.  
**Status:** proposed v6 implementation phase.  
**Baseline:** SingNextOS `b06ec5b5980acdad3143393a72a3b69c4edb5cfb`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

## 1. Objective

Represent partial memory/device/fabric degradation and exact containment/reconfiguration instead of binary provider availability.

## 2. Existing anchors to reuse

- CXL reconfiguration/reclaim
- provider closure/containment distinctions
- Region quarantine
- fault injection framework

## 3. Target refactor

Add provider-neutral health/failure projections: corrected error, poison, degraded bandwidth, failed range, failed device/function, resetting, draining, contained, reconfigured. Add `FailureDomainId` as opaque evidence identity and optional `RegionDamageMap` projections. SingNext translates evidence into explicit Region/provider lifecycle transitions.

## 4. Authority boundaries

Provider owns health evidence. RegionAuthority owns memory consequences; ExternalOperationAuthority owns in-flight effect consequences; Fabric/device owner owns rebinding; ResourceBudgetAuthority owns accounting corrections only after closure rules permit them.

## 5. Mandatory invariants

- degraded != closed;
- poisoned subrange does not automatically destroy unrelated Region ranges;
- uncertain in-flight writer blocks unsafe reclaim;
- reconfiguration increments exact generations;
- health telemetry cannot directly mutate capability/ownership state.

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

ECC/poison injection; partial-range quarantine; link degradation; device reset during DMA; rebind after failure; duplicate RAS events; stale damage map; recovery without authority resurrection.

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

Gate `V6-RAS-PARTIAL-FAILURE`; initial implementation may remain software/model only until named hardware exists.
