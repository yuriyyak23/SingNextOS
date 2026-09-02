# Singularity-derived refactoring plan

## Purpose

This folder turns the useful architectural ideas found in the authentic Microsoft Research Singularity source tree into a concrete, staged refactoring plan for SingNextOS.

It is an **augmentation** of [`docs/hybridcpu-v2-refactoring-roadmap`](../hybridcpu-v2-refactoring-roadmap/README.md), not a competing roadmap. The existing HybridCPU roadmap remains authoritative for the platform-integration order. This plan adds the missing component/contract/driver/service discipline that is worth carrying forward from Singularity.

## Source baseline

Primary historical reference:

- `https://github.com/dz333n/Singularity-OS/tree/master/base/Interfaces`
- `https://github.com/dz333n/Singularity-OS/tree/master/base/Kernel`
- `https://github.com/dz333n/Singularity-OS/tree/master/base/Libraries`
- `https://github.com/dz333n/Singularity-OS/tree/master/base/Drivers`

Specific source patterns used by this plan include:

- `base/Interfaces/Channels/Channels.csi` — channel delivery is an explicit interface boundary and references shared-heap allocation rather than arbitrary cross-process object sharing;
- `base/Interfaces/IoSystem/IoSystem.csi` — device lifecycle, registration, resource yield and service initialization are explicit mechanisms;
- `base/Interfaces/IoSystem/IoConfig.csi` — device configuration is represented as bounded resources/ranges;
- `base/Interfaces/IoSystem/IoIrq.csi` — interrupt registration, waiting, acknowledgment and release are explicit lifecycle operations;
- `base/Libraries/Manifest` — declarative component/job binding and manifest machinery is a first-class library concern;
- the top-level separation of `Interfaces`, `Kernel`, `Libraries` and `Drivers` — implementation and policy are not collapsed into one privileged API surface.

Current SingNextOS baseline for this documentation is `master` at `cc00bdd3a9f7d143044a3a981c755e07b485f873`, which already includes the Phase 2 process-exit orchestration work from the HybridCPU roadmap.

## Architectural interpretation

The goal is **not** to reproduce Singularity's old Sing#/Bartok/x86 implementation. The goal is to preserve its strongest ideas using current SingNextOS primitives:

```text
Singularity idea                 SingNextOS form
------------------------------  ------------------------------------------
contract-first communication    generated typed SIP + protocol metadata
ownership-aware communication   OwnedRegion/OwnedBuffer + MOVE/borrow/grant
resource-oriented drivers       DeviceLease/MMIO/IRQ/DMA bounded authority
component manifests             ServiceManifest + requirements + digests
isolated system services        SIP services with capability-scoped sessions
rich libraries, small kernel    .NET-like libraries over minimal kernel ABI
explicit close/failure          generated/runtime lifecycle + completion
```

The existing cross-platform invariant remains:

```text
local Sing capability
AND local resource/generation/protocol state
AND live opaque platform lease
AND HybridCPU runtime admission
  -> hardware/runtime-visible effect
```

## Phase order

| Phase | File | Outcome |
|---|---|---|
| 0 | [`00_BASELINE_AND_ADOPTION_RULES.md`](00_BASELINE_AND_ADOPTION_RULES.md) | Freeze what is adopted, adapted and explicitly rejected from historical Singularity. |
| 1 | [`01_DEPENDENCY_AND_LAYER_BOUNDARIES.md`](01_DEPENDENCY_AND_LAYER_BOUNDARIES.md) | Make Interfaces/Kernel/Libraries/Drivers-style boundaries enforceable in the modern repository. |
| 2 | [`02_STATEFUL_SIP_CONTRACTS.md`](02_STATEFUL_SIP_CONTRACTS.md) | Evolve typed SIP from message typing into protocol-state and ownership-aware contracts. |
| 3 | [`03_OWNERSHIP_EXCHANGE_MODEL.md`](03_OWNERSHIP_EXCHANGE_MODEL.md) | Make `OwnedRegion` the modern successor to exchange/shared-heap ownership discipline without creating a global exchange heap. |
| 4 | [`04_MANIFESTS_DISCOVERY_AND_SESSIONS.md`](04_MANIFESTS_DISCOVERY_AND_SESSIONS.md) | Add declarative service manifests, authority-neutral discovery and capability-scoped endpoint sessions. |
| 5 | [`05_DRIVER_AND_DEVICE_RESOURCE_MODEL.md`](05_DRIVER_AND_DEVICE_RESOURCE_MODEL.md) | Treat drivers as isolated services with exact MMIO/IRQ/DMA resource grants. |
| 6 | [`06_EVENTS_COMPLETIONS_AND_TEARDOWN.md`](06_EVENTS_COMPLETIONS_AND_TEARDOWN.md) | Generalize Track A/Phase 2 lifecycle discipline to IRQ, DMA, compute, VM, timers and surfaces. |
| 7 | [`07_NATIVE_LIBRARIES_AND_COMPATIBILITY.md`](07_NATIVE_LIBRARIES_AND_COMPATIBILITY.md) | Keep rich source-facing APIs in libraries/services and preserve a small native kernel ABI. |
| 8 | [`08_COMPONENT_MODEL_AND_CONFORMANCE.md`](08_COMPONENT_MODEL_AND_CONFORMANCE.md) | Unify domain/process/manifest/contracts/resources into a component lifecycle and enforce it with conformance tests. |

The normative target is defined in [`TECHNICAL_SPECIFICATION.md`](TECHNICAL_SPECIFICATION.md).

## Relationship to the HybridCPU roadmap

This plan intentionally cross-cuts existing phases rather than renumbering them:

- dependency boundaries: HybridCPU Phases 0, 10 and 12;
- stateful SIP: Phases 2, 5, 7, 8, 10 and 11;
- ownership exchange model: Phases 4, 5 and 11;
- manifests/sessions: Phases 6 and 10;
- driver resources: Phase 5;
- unified events/completions: Phases 2, 5, 6, 7, 8 and 11;
- libraries/compatibility: Phase 10;
- component conformance: Phase 12.

No item in this folder may bypass the existing rule that platform/provider identities remain opaque and distinct from Sing-local identities.

## Non-negotiable rules

1. Contract discovery does not grant authority.
2. A manifest declares requirements; it never grants those requirements.
3. A driver assembly has no ambient hardware privilege.
4. Protocol state and ownership state must agree before an externally visible effect is allowed.
5. `MOVE` transfers logical authority; it does not promise physical zero-copy.
6. Provider tokens, physical addresses, VMCS state, lane IDs and raw opcode encodings do not enter SIP payloads.
7. Rich filesystem/network/UI/virtualization policy stays outside the kernel ABI.
8. Historical x86/HAL/compiler/runtime implementations are references, not migration targets.
