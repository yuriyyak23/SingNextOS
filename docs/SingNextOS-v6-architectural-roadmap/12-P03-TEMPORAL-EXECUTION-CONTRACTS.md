# P03 — Temporal Execution Contracts

**Depends on:** existing ResourceBudgetAuthority; P05 for refinement.  
**Status:** proposed v6 implementation phase.  
**Baseline:** SingNextOS `b06ec5b5980acdad3143393a72a3b69c4edb5cfb`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

## 1. Objective

Turn budgets/deadlines into typed temporal obligations and provider guarantees while keeping quantitative truth in the single budget ledger.

## 2. Existing anchors to reuse

- `ResourceBudgetAuthority` atomic vector reservation
- EndpointSession donation
- deadlines/cancellation contracts
- `ResourceScheduler` and resource measurement

## 3. Target refactor

Define `TemporalObligationsV1` and `TemporalExecutionGuaranteesV1`: budget, period/window, deadline, max burst, max non-preemptible interval, interference class and donation ceiling. Reuse existing reservations; add new resource dimensions only where accounting/enforcement is real. Scheduler consumes guarantees but cannot create them.

## 4. Authority boundaries

`ResourceBudgetAuthority` owns reserved/consumed capacity. Session/invocation owners bind donation. HybridCPU/provider owns execution timing enforcement/evidence. Planner/scheduler remains policy only.

## 5. Mandatory invariants

- AccountingOnly != EnforcedUpperBound != GuaranteedReservation;
- deadline metadata without enforceable provider contract is a hint, not guarantee;
- SMT/shared-lane interference must be included in qualified bounds;
- blocked/waiting/retired work charging is defined per resource class;
- no new global TemporalAuthority.

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

budget conservation; donation laundering; priority-ceiling inversion; deadline miss; interference stress; replay/squash charging; provider stall; measurement duplicate/reorder; guaranteed vs non-guaranteed claim tests.

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

Gates `V6-TEMPORAL-LEASES` and separately `V6-GUARANTEED-DEADLINE`.
