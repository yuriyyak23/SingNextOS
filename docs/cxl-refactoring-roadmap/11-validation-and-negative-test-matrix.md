# Validation and Negative Test Matrix

Status: complete for the software/model scope. Hardware and QEMU qualification remains excluded by the explicit Phase 8 decision; model evidence is tested to fail hardware-attestation policy.

Evidence: `11_PHASE11_IMPLEMENTATION_EVIDENCE.md`.

## Purpose

Centralize the negative-path tests required across all CXL refactoring phases. Happy-path support is insufficient: stale authority, reset, link loss, reconfiguration and partial completion are first-class architecture cases.

## Authority and ownership

Required tests:

- stale `RegionAuthority` generation rejects new admission;
- stale `MutationEpoch` rejects submit or publish according to lifecycle stage;
- revoked MOVE/borrow authority cannot be refreshed in-place;
- device authority cannot widen region authority;
- security evidence cannot substitute for missing region/device authority;
- provider feature discovery cannot create a capability;
- ownership return remains separate from completion/visibility.

## `RegionUse`

Required tests:

- incompatible CPU/device writers reject;
- disjoint compatible uses coexist when the region model permits subdivision;
- direct coherent write requires stronger use mode than staged output;
- release is idempotent;
- stale use after backing/fault/reclaim cannot submit;
- read-only shared use does not permit later publication of writes.

## Lifecycle

For each state:

```text
Prepared
Admitted
Submitted
DeviceComplete
Visible
Published
Released
```

Test every illegal transition and every reset/cancel point.

Particularly:

- no submit from `Prepared`;
- no publish from `DeviceComplete` without `Visible` where visibility is required;
- no publish after stale mutation/binding generation;
- duplicate completion cannot publish twice;
- release cannot cause publication;
- provider disappearance after submit ends in failed/drained state until exact provider closure or independently proven effect containment;
- local resources can be reclaimed after hardware loss only after verified closure/containment, without resurrecting device authority; otherwise they remain quarantined.

## Generation matrix

| Event | Expected invalidation |
|---|---|
| ownership MOVE / incompatible CPU mutation | affected region `MutationEpoch` / authority |
| IOMMU/domain remap for DMA path | affected platform mapping/domain generation |
| device/function reset | affected `DeviceGeneration` |
| link loss making device binding unusable | affected device and/or fabric binding generation |
| HDM decoder/window change | affected `FabricBindingGeneration` |
| FM bind/unbind/reassignment | affected `FabricBindingGeneration` |
| Type-3 backing replacement | affected backing/binding generation + region reclaim path |
| operation ID reuse/cancel | new `OperationGeneration` |
| security session reset | security/evidence generation only; not automatic region-authority mutation |

Tests must ensure unrelated resources remain valid when the backend provides narrow invalidation scope.

## Type-3 memory

- CPU access uses normal memory semantics, not a fake IOMMU abstraction;
- DMA to CXL-backed memory still requires normal DMA/IOMMU authority where applicable;
- hot-remove with live region triggers controlled reclaim/migration;
- provider-private DPA/decoder identity never appears in ordinary `OwnedRegion` API;
- placement fallback preserves ownership semantics;
- unsupported persistence/coherence capability is reported honestly.

## Type-2 accelerator

- staged output survives device completion without becoming published automatically;
- output mutation between completion and publication causes fail-closed discard/quarantine;
- direct coherent request without exact support/use policy rejects or falls back only if semantics allow;
- reset during staged execution -> no publish;
- reset after direct irreversible write -> recovery/barrier behavior, never fake rollback;
- CXL.io device-resource path remains independently usable.

## Visibility/publication/replay contract

- coherence alone cannot move state to `Published`;
- `DeviceComplete` alone cannot move state to `Visible`;
- visibility failure prevents publication;
- direct external write classified with explicit effect class;
- read-only coherent access is not automatically an irreversible replay barrier;
- staged operation remains discardable until publication by contract.

## Fabric/pooling

- reconfiguration stops new admissions before old binding reuse;
- old `FabricBindingGeneration` is never accepted after rebind;
- stale pool release validates the complete assignment before removal and cannot change capacity;
- stale reconfiguration tickets do not consume the current ticket;
- endpoint generation change during reconfiguration cannot roll fabric generation back;
- post-submit Fabric Manager tracking requires explicit provider closure/effect containment;
- ambiguous fabric, memory or pool creation acceptance retains dependent authority and blocks reclaim;
- pool reassignment cannot implicitly transfer region ownership;
- unrelated topology changes do not globally invalidate resources without need;
- P2P route without platform isolation/access authority rejects;
- device/fabric disappearance during each lifecycle state has deterministic recovery.

## Security

- IDE/authentication evidence expiration invalidates SecureCompute readiness;
- trusted device without region authority rejects;
- correct authority without required security evidence rejects secure-required intent;
- QEMU/backend without real security capability cannot advertise trusted hardware evidence;
- stale evidence generation after reset rejects.

## Multi-host gate

Until a distributed authority/fencing design exists:

- writable region binding to two hosts rejects;
- read-only sharing requires explicit policy/provider support;
- rebind to a second host waits for first-host reclaim;
- host disappearance triggers fenced reclaim policy, not silent ownership reassignment.

## Acceptance rule for every implementation phase

A phase is not complete unless:

1. happy-path unit/integration tests pass;
2. stale generation/authority paths are tested;
3. reset/reconfiguration paths are tested when relevant;
4. authority/evidence separation is asserted;
5. no new application-visible CXL physical identity has leaked into public authority types.
