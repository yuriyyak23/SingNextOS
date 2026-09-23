# P08 — Preemptible and Resumable Heterogeneous Execution

**Depends on:** P03 + P05.  
**Status:** proposed v6 implementation phase.  
**Baseline:** SingNextOS `b06ec5b5980acdad3143393a72a3b69c4edb5cfb`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

## 1. Objective

Separate cancel, preempt, suspend, resume, restart and containment for CPU/MatrixTile/DSC/L7/provider work.

## 2. Existing anchors to reuse

- existing cancellation dispositions
- provider containment/fence hooks
- MatrixTile/DSC/L7 execution state
- external operation lifecycle

## 3. Target refactor

Define `PreemptionGuaranteeV1`: `NonPreemptible`, `RestartOnly`, `SafePointPreemptible`, `StateCapturePreemptible`, with max safe-point latency where enforceable. Add exact suspend/resume bindings that name operation/provider/runtime generations. Captured state is opaque evidence/state owned by the provider; resume always performs fresh SingNext and runtime legality checks.

## 4. Authority boundaries

Invocation/external-operation owners own logical lifecycle; provider/HybridCPU owns machine execution state and safe points; ResourceBudgetAuthority owns charge/settlement; no captured token grants authority.

## 5. Mandatory invariants

- cancel != preempt;
- preempt != closed;
- suspended state pins required Regions/resources or uses explicit durable migration semantics;
- stale resume generation fails closed;
- provider effect visibility/publication rules survive suspension.

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

preempt at every safe point; cancel-vs-preempt; resume after service/provider restart; stale captured state; resource accounting across suspension; multi-stage SipJob barrier behavior; replay interaction.

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

Gates `V6-PREEMPTION` and stronger `V6-STATEFUL-RESUME` separately.
