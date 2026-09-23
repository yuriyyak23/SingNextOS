# P12 — Energy, Power and Thermal Resource Semantics

**Depends on:** P03; P07 optional.  
**Status:** proposed v6 implementation phase.  
**Baseline:** SingNextOS `b06ec5b5980acdad3143393a72a3b69c4edb5cfb`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

## 1. Objective

Extend resource/refinement algebra to energy and thermal constraints for heterogeneous planning and admission.

## 2. Existing anchors to reuse

- resource dimensional algebra
- provider measurement contracts
- resource scheduler
- locality/data-motion planning

## 3. Target refactor

Add resource classes/units only where the ledger can represent them safely: energy budget (joules or normalized unit), average/peak power envelope and optionally thermal/performance-state class. Provider guarantees may expose enforceable power/performance envelopes; telemetry-only values remain evidence. Planner may optimize energy-per-operation subject to semantic obligations.

## 4. Authority boundaries

ResourceBudgetAuthority owns local committed budget/accounting. Provider owns machine enforcement/measurement. Planner chooses policy. Thermal sensors/telemetry never become authority.

## 5. Mandatory invariants

- reservation != DVFS permission;
- energy telemetry cannot mint/refund budget by itself;
- thermal throttling that invalidates temporal guarantees forces revalidation/fallback;
- performance state identity stays provider-private.

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

energy accounting conservation; measurement loss; throttle during guaranteed job; provider switch; locality-vs-energy tradeoff; bogus telemetry; settlement bounds; cancellation energy charge.

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

Gate `V6-ENERGY-BUDGETS`; initial claim is accounting/measurement before enforcement.
