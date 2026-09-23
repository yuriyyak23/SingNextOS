# P06 — Information-Flow Control

**Depends on:** P05; compatible with SingCap/ManagedCap.  
**Status:** proposed v6 implementation phase.  
**Baseline:** SingNextOS `b06ec5b5980acdad3143393a72a3b69c4edb5cfb`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

## 1. Objective

Constrain where legally readable information may flow without replacing capability authority.

## 2. Existing anchors to reuse

- ManagedCap closed-world admission
- software sealing
- SecureCompute policy/evidence
- telemetry projection/redaction

## 3. Target refactor

Add typed `DataLabelV1`/flow policy with confidentiality and integrity components, explicit join/meet where the chosen lattice requires it, and explicit `Declassify`/`Endorse` effects bound to existing capability authority. Labels travel with semantic Region/value projections, not raw pointers. Start with generated SIP/ManagedCap boundaries and staged provider outputs.

## 4. Authority boundaries

CapabilityAuthority still answers whether an operation is permitted. IFC policy answers whether a data-flow relation is legal. Declassification is an explicit effect permission, never an ambient label mutation.

## 5. Mandatory invariants

- labels are not capabilities;
- provider execution must not silently drop labels;
- unknown transform/provider isolation class denies protected flow;
- telemetry/debug projection obeys IFC;
- persistence/checkpoint preserve label policy identity but not live authority.

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

label laundering, implicit serialization paths, fused SipJob equivalence, provider staging leakage, telemetry leak, checkpoint/restore labels, declassification revoke race, secure-domain output downgrade.

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

Gate `V6-IFC`; begin with two-level confidentiality + integrity labels before richer lattices.
