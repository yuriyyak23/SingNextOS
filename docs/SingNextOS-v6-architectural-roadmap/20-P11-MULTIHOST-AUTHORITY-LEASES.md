# P11 — Multi-Host Delegated Authority Leases

**Depends on:** P01-P10 safety foundations.  
**Status:** proposed v6 implementation phase.  
**Baseline:** SingNextOS `b06ec5b5980acdad3143393a72a3b69c4edb5cfb`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

## 1. Objective

Extend one-logical-owner semantics across a fabric without turning every authority decision into distributed consensus.

## 2. Existing anchors to reuse

- capability derivation/revocation lineage
- resource lease/split/settlement
- CXL pooling/reconfiguration concepts
- restart/reconciliation

## 3. Target refactor

Define `RemoteAuthorityLeaseV1` as an epoch-bound monotonic delegation from one logical owner. Separate control-plane lease issuance/renewal/revocation from data-plane use. Where quantitative resources require distributed consumption, use explicit escrow/sub-allocation rather than duplicated global counters. Partition behavior defaults to lease expiry/fail-closed.

## 4. Authority boundaries

Original host/realm remains logical owner. Remote host owns only its delegated lease state. Fabric manager supplies topology/binding evidence, not authority. Reconciliation never upgrades an expired/stale lease.

## 5. Mandatory invariants

- remote delegation cannot amplify rights/quota/range;
- network/fabric reachability != lease liveness;
- partition cannot silently extend authority;
- durable lease logs do not restore live leases after epoch change;
- reclaim waits for expiry/closure/containment according to the resource class.

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

partition during use; renewal race; owner reboot; remote reboot; duplicate lease ID; split/merge conservation; fabric reconfiguration; stale route; remote effect ambiguity; clock-skew-free expiry strategy where possible.

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

Gate `V6-MULTIHOST-LEASES`; explicitly non-critical-path and last-wave.
