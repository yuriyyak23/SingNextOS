# P09 — Device Trust and Attestation Composition

**Depends on:** P05; P04 for assigned devices.  
**Status:** proposed v6 implementation phase.  
**Baseline:** SingNextOS `b06ec5b5980acdad3143393a72a3b69c4edb5cfb`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

## 1. Objective

Bind measured/trusted device execution into semantic admission without making attestation authority.

## 2. Existing anchors to reuse

- SecureCompute evidence policy
- boot trust-chain discipline
- provider security evidence
- CXL security evidence treated as non-authoritative

## 3. Target refactor

Add `TrustObligationsV1` and `TrustEvidenceV1` describing minimum assurance class, measurement policy/digest, producer type, freshness generation and device/interface isolation class. External provider adapters translate SPDM/TDISP/IDE-like backend evidence into this provider-neutral model where available.

## 4. Authority boundaries

Trust producer owns measurement evidence; SingNext policy owner decides whether it satisfies an obligation; device/memory/execution authority still comes from existing owners; HybridCPU legality remains separate.

## 5. Mandatory invariants

- stale measurement/security generation denies the protected effect;
- model/emulated evidence cannot satisfy hardware-required assurance;
- attestation cannot create Region/device/secure-domain authority;
- event/publication revalidates relevant trust generation when policy requires it.

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

wrong device measurement; replayed evidence; downgraded assurance; device reset after admission; secure guest + device binding mismatch; evidence loss after submit; production-vs-model assurance tests.

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

Gate `V6-DEVICE-ATTESTATION`; no ProductionSecure promotion solely from this phase.
