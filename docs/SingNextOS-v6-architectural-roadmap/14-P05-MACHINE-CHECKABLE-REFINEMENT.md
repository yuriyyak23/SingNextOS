# P05 — Machine-Checkable Cross-Layer Refinement

**Depends on:** P01 semantic vocabulary.  
**Status:** proposed v6 implementation phase.  
**Baseline:** SingNextOS `b06ec5b5980acdad3143393a72a3b69c4edb5cfb`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

## 1. Objective

Turn prose contracts into executable state/refinement models and trace conformance.

## 2. Existing anchors to reuse

- existing semantic-trace equivalence in SipJob
- `OperationObligationsV1` / `ExecutionGuaranteesV1`
- semantic co-design TLA+/Alloy plan
- provider conformance and fault-injection framework

## 3. Target refactor

Define a small canonical semantic state machine for admission, submit, effect, completion, visibility, publication, settlement, release, persistence and fault containment. Provide deterministic trace event schemas and a projection `ProviderTrace -> SingSemanticTrace`. Use TLA+/PlusCal for cross-owner concurrency, Alloy/property exploration for finite refinement/configuration and generated differential tests against runtime/ISE traces.

## 4. Authority boundaries

Formal model owns no runtime fact. It is specification/evidence. Live authority remains in existing owners; HybridCPU runtime remains independently authoritative for machine legality.

## 5. Mandatory invariants

- model and implementation share versioned event vocabulary but not mutable state;
- every provider-specific event must project or be declared unobservable stuttering;
- a provider trace that cannot be projected fails qualification;
- model checking results are bound to exact model/schema/source tuple.

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

state exploration of generation drift, cancel/retire, quarantine/reconcile, shared-memory races, persistence crash points and preemption; differential traces across ordinary SIP, SipJob, CPU, MatrixTile, DSC and L7 contours.

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

Gate `V6-FORMAL-REFINEMENT`; first promotion requires one end-to-end vertical with recorded model-check + executable differential evidence.
