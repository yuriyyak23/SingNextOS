# SingNextOS CXL 3.x/4.x Refactoring Roadmap

Status: **Staged single-host software/model roadmap complete and audit-hardened.** Phases 0–7, 9–12 and audit-hardening Phases 14–16 are complete; Phase 8 QEMU/physical-hardware/FPGA work is explicitly skipped by user scope and is not claimed as executed. `DirectCoherentWrite` remains `FutureGated`.

The executable baseline, decisions, reusable seams and Phase-1 verification
gate are recorded in [00_PHASE0_BASELINE_AUDIT.md](00_PHASE0_BASELINE_AUDIT.md).
Phase 1 implementation decisions and executable evidence are recorded in
[01_PHASE1_IMPLEMENTATION_EVIDENCE.md](01_PHASE1_IMPLEMENTATION_EVIDENCE.md).
Phase 2 lifecycle decisions and executable evidence are recorded in
[02_PHASE2_IMPLEMENTATION_EVIDENCE.md](02_PHASE2_IMPLEMENTATION_EVIDENCE.md).
Phase 3 planning decisions and executable evidence are recorded in
[03_PHASE3_IMPLEMENTATION_EVIDENCE.md](03_PHASE3_IMPLEMENTATION_EVIDENCE.md).
Phase 4 authority/provider decisions and executable evidence are recorded in
[04_PHASE4_IMPLEMENTATION_EVIDENCE.md](04_PHASE4_IMPLEMENTATION_EVIDENCE.md).
Phase 5 Type-3 model decisions and executable evidence are recorded in
[05_PHASE5_IMPLEMENTATION_EVIDENCE.md](05_PHASE5_IMPLEMENTATION_EVIDENCE.md).
Phase 6 Type-2 accelerator decisions and executable evidence are recorded in
[06_PHASE6_IMPLEMENTATION_EVIDENCE.md](06_PHASE6_IMPLEMENTATION_EVIDENCE.md).
Phase 7 effect/replay decisions and executable evidence are recorded in
[07_PHASE7_IMPLEMENTATION_EVIDENCE.md](07_PHASE7_IMPLEMENTATION_EVIDENCE.md).
Phase 8 environment and acceptance-gate status is recorded in
[08_PHASE8_EXECUTION_STATUS.md](08_PHASE8_EXECUTION_STATUS.md).
Phase 9 fabric/pooling decisions and executable evidence are recorded in
[09_PHASE9_IMPLEMENTATION_EVIDENCE.md](09_PHASE9_IMPLEMENTATION_EVIDENCE.md).
Phase 10 security/multi-host decisions and executable evidence are recorded in
[10_PHASE10_IMPLEMENTATION_EVIDENCE.md](10_PHASE10_IMPLEMENTATION_EVIDENCE.md).
The final requirement-by-requirement software audit is recorded in
[13_FINAL_SOFTWARE_VALIDATION.md](13_FINAL_SOFTWARE_VALIDATION.md).

This directory defines a phased plan for integrating CXL 3.x/4.x into SingNextOS without creating a parallel authority, ownership, completion, publication, or security model.

## Scope boundary

This roadmap changes **SingNextOS only**. It deliberately excludes:

- HybridCPU-v2 source-code refactoring;
- HybridCPU-v2 ISA changes;
- compiler/lowering changes;
- new lane definitions or a `Remote Lane`;
- raw CXL flit/TLP/HDM/IOMMU identities in application-facing Sing APIs.

Where SingNextOS needs behavior from HybridCPU-v2, the requirement is recorded only as an **external contract/dependency gate**. Implementation in HybridCPU-v2 belongs to a separate roadmap and PR series.

## Architectural verdict

CXL is not one SingNextOS subsystem. It is represented through independent provider roles over the existing authority substrate:

```text
CXL.io   -> existing device/MMIO/IRQ/DMA contracts
CXL.mem  -> memory discovery, placement, region backing, hotplug/reclaim
CXL.cache-> coherent-access capability of a device/provider, not ownership
Fabric   -> topology/binding/reconfiguration evidence + platform authority materialization
```

The normal application/SIP authority remains based on `RegionAuthority`, `OwnedRegion` / `OwnedBuffer`, MOVE/borrow semantics, `DeviceLease`, `DeviceResourceSet`, platform-domain/mapping generations, and the common external-operation lifecycle. HDM decoders, DPA, fabric routes, port IDs and similar CXL identities stay provider-private.

The default accelerator publication policy remains **staged**. Direct coherent writes are an optimization admitted only after exact authority, region-use, visibility, mutation, provider, and replay/publication conditions are proven. Coherence by itself never implies ownership, publication, zero-copy safety, replay safety, or authority.

## Phase order

| Phase | Document | Outcome |
|---|---|---|
| 0 | [00-gap-matrix-and-decisions.md](00-gap-matrix-and-decisions.md) | Freeze baseline, non-goals and gap classification |
| 1 | [01-mutation-epoch-and-region-use.md](01-mutation-epoch-and-region-use.md) | Provider-neutral mutation/use authority foundation |
| 2 | [02-common-external-operation-lifecycle.md](02-common-external-operation-lifecycle.md) | One completion/visibility/publication lifecycle |
| 3 | [03-provider-selection-and-dependency-model.md](03-provider-selection-and-dependency-model.md) | Provider-neutral compute/memory planning seam |
| 4 | [04-cxl-authority-and-provider-decomposition.md](04-cxl-authority-and-provider-decomposition.md) | CXL fabric/device/memory authority contracts |
| 5 | [05-cxl-type3-memory-provider.md](05-cxl-type3-memory-provider.md) | Single-host CXL Type-3 model backend |
| 6 | [06-cxl-type2-accelerator-service.md](06-cxl-type2-accelerator-service.md) | Sing-side Type-2 staged accelerator provider |
| 7 | [07-visibility-publication-and-replay-contract.md](07-visibility-publication-and-replay-contract.md) | CXL-aware publication/replay boundary on Sing side |
| 8 | [08-real-hardware-and-qemu-backend.md](08-real-hardware-and-qemu-backend.md) | Real discovery/configuration/backend path |
| 9 | [09-fabric-manager-pooling-and-reconfiguration.md](09-fabric-manager-pooling-and-reconfiguration.md) | Dynamic pooling, binding and reclaim |
| 10 | [10-security-securecompute-and-multihost.md](10-security-securecompute-and-multihost.md) | IDE/security evidence and advanced multi-host gating |

Validation and cross-project rules are centralized in:

- [11-validation-and-negative-test-matrix.md](11-validation-and-negative-test-matrix.md)
- [12-cross-project-contracts-and-non-goals.md](12-cross-project-contracts-and-non-goals.md)
- [14-audit-hardening-and-reclaim-closure.md](14-audit-hardening-and-reclaim-closure.md)
- [15-provider-effect-containment-and-fm-atomicity.md](15-provider-effect-containment-and-fm-atomicity.md)
- [16-fabric-exactness-and-generic-effect-containment.md](16-fabric-exactness-and-generic-effect-containment.md)

## Mandatory invariants

1. `security evidence != authority`.
2. `coherence != ownership`.
3. `coherence != publication`.
4. `completion != visibility`.
5. `visibility != ownership return`.
6. `replay certificate/evidence != runtime permission`.
7. No CXL-specific physical identity is part of ordinary application/SIP authority.
8. Any stale generation/epoch fails closed before the next hardware effect when the stale fact is needed for submission; otherwise it fails closed before publication/release.
9. Direct zero-copy/coherent sharing is never an API guarantee; it is a provider optimization derived from proven conditions.
10. Fabric/security/device observations are evidence until SingNextOS deliberately materializes them into an owned platform binding.

## CXL versioning policy

The software model targets the stable semantic baseline of CXL 3.x (fabric, memory expansion/pooling, shared-memory mechanisms, Type-2/Type-3 device model) and treats CXL 4.0 features as provider capabilities, not new authority concepts. CXL 4.0 preserves the CXL 3.x protocol model while adding, among other changes, 128 GT/s operation, bundled ports and memory RAS enhancements. SingNextOS must therefore avoid hard-coding link width/rate/topology assumptions into authority types.

## Definition of done for the roadmap

The roadmap is complete only when the implementation can support all three SingNextOS paths over a shared authority substrate:

```text
A. OwnedRegion -> placement -> CXL Type-3 memory -> normal CPU/device use
B. Compute intent -> provider selection -> CXL Type-2 service -> staged/validated publication
C. DeviceResourceSet -> MMIO/IRQ/DMA -> CXL.io/PCIe-compatible device
```

These paths may share discovery, leases, generations, fault handling and region authority. They are **not required to share one execution protocol**.

## External reference baseline

Implementation PRs should verify details against current official and upstream implementation references rather than copying protocol constants into roadmap text:

- CXL Consortium CXL Specification page: https://computeexpresslink.org/cxl-specification/
- CXL 3.1 evaluation/public specification PDF: https://computeexpresslink.org/wp-content/uploads/2024/02/CXL-3.1-Specification.pdf
- CXL 4.0 release overview: https://computeexpresslink.org/about-cxl/
- QEMU CXL documentation: https://www.qemu.org/docs/master/system/devices/cxl.html
- Linux CXL documentation/source should be used as an implementation reference, not as the SingNextOS authority model.

PCIe/IOMMU details must be checked against the platform's PCI-SIG/firmware/IOMMU specifications and actual hardware backend. This roadmap intentionally avoids baking protocol register layouts into high-level SingNextOS contracts.
