# P02 — Durability and Persistent-Memory Semantics

**Depends on:** P01 + P05 core trace model.  
**Status:** proposed v6 implementation phase.  
**Baseline:** SingNextOS `b06ec5b5980acdad3143393a72a3b69c4edb5cfb`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

## 1. Objective

Add persistence/crash-consistency as a semantic dimension distinct from visibility/publication.

## 2. Existing anchors to reuse

- CXL Type-3 model and Direct Boot persistent-media work
- checkpoint/restore
- ExternalOperation publication lifecycle

## 3. Target refactor

Add `DurabilityObligationsV1`, `DurabilityGuaranteesV1` and explicit persist/confirm receipts. Model at least `VolatileOnly`, `PersistOrdered`, `CrashConsistent`, `FailureAtomicBounded`. Define the chain `Visible -> Published -> Persisted -> DurableConfirmed` when requested. Persistent state stores durable identity/policy/data only; live authority is always recreated after restart.

## 4. Authority boundaries

Durable-state format/integrity is owned by the storage/checkpoint subsystem; Region ownership remains `RegionAuthority`; persistence provider supplies evidence/guarantee; publication owner decides application visibility.

## 5. Mandatory invariants

- publication is insufficient for durability;
- media type alone is insufficient for durability;
- crash recovery never resurrects old capability/provider/session generations;
- persistent metadata uses journal/generation/anti-rollback where needed;
- ambiguous persistence state requires reconciliation, not success inference.

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

Crash-point matrix around write/flush/publish/confirm; torn metadata; stale durable generation; reboot/fresh admission; CXL Type-3 disappearance; duplicate replay; failure-atomic bound tests.

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

Gate `V6-DURABLE-OUTPUT`; initial contour staged copy to a qualified persistent provider.
