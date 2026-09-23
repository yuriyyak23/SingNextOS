# P04 — Translation and DMA Authority Composition

**Depends on:** P01.  
**Status:** proposed v6 implementation phase.  
**Baseline:** SingNextOS `b06ec5b5980acdad3143393a72a3b69c4edb5cfb`; HybridCPU-v2 `794c4a53494f503855ac8cf209efab23fde083b2`.

## 1. Objective

Make DMA/shared-address-space execution an exact composition of existing memory, device, address-space and provider owners.

## 2. Existing anchors to reuse

- DeviceLease / platform authority paths
- Region external-use reservations
- CXL.io/DMA provider paths
- external operation generation checks

## 3. Target refactor

Add a non-authoritative `DmaExecutionBindingV1` correlating RegionUse, process/address-space generation, device lease, translation/mapping generation and provider operation identity. Support provider-neutral SVA/PASID-style process identity as opaque correlation evidence when a backend has it. Translation creation/destruction remains provider/platform-owned.

## 4. Authority boundaries

`RegionAuthority` owns bytes; process/VM owner owns address-space incarnation; device/platform owner owns device lease and translation mapping; provider owns IOMMU/backend execution; no address value owns authority.

## 5. Mandatory invariants

- VA/IOVA/PASID/domain ID are evidence only;
- stale translation blocks the next DMA effect;
- ambiguous in-flight DMA prevents Region release/reuse;
- device page fault handling revalidates live Region/use/mapping generations;
- DMA zero-copy is an optimization, not ABI guarantee.

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

map/unmap vs submit races; PASID/process reuse; device reset; page fault after revoke; wrong device/domain; stale IOVA; DMA completion without visibility; IOMMU fault injection; guest + secure-domain composition.

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

Gate `V6-DMA-TRANSLATION-BINDING`; direct SVA/zero-copy contours remain provider-specific.
