# P15.11 — Risks, mitigations and non-goals

## Risk 1 — Capsule accidentally becomes a second firmware OS

**Failure mode:** CXL stack, network, filesystem, generic drivers and runtime services accumulate in pre-kernel capsule.

**Mitigation:** strict `BootCapsule` admission profile; TCB byte/method budget; only one Type-3 baseline; network recovery deferred to signed recovery environment.

## Risk 2 — Boot Core starts carrying authority

**Failure mode:** shared code begins returning runtime provider leases or Region handles.

**Mitigation:** project-reference policy; public-surface tests; contracts use value/evidence DTOs only.

## Risk 3 — Model-to-production semantic drift

**Failure mode:** promoted implementations subtly change split-brain, rollback, compensation or failure semantics.

**Mitigation:** differential property tests before deleting old models.

## Risk 4 — Managed bootstrap TCB is too large

**Failure mode:** full managed runtime/GC becomes required before CXL boot.

**Mitigation:** start with no-heap or bounded boot heap; exact helper/import allow-list; measure reachable code.

## Risk 5 — HDM partial effect cannot be proven closed

**Failure mode:** decoder programming fails after some hops commit.

**Mitigation:** transactional ordering + readback + reverse compensation; ambiguous state => quarantine/reset, never assume rollback succeeded.

## Risk 6 — Pre-IOMMU DMA compromises trust state

**Mitigation:** bus mastering off by default; exact requester + bounce-buffer profile if unavoidable.

## Risk 7 — Cross-repo revision drift

**Failure mode:** SingNext qualification pins an old HybridCPU compiler/runtime revision while master has evolved.

**Mitigation:** P15-00 exact current baseline and generated compatibility manifest; CI fails if unreviewed contract digest changes.

## Risk 8 — Capsule and OS rollback domains get conflated

**Mitigation:** distinct `BootCapsuleGeneration`/rollback domain and `ImageGeneration`/OS rollback domain; no shared numeric generation semantics.

## Risk 9 — Boot evidence gets treated as runtime liveness

**Mitigation:** retain `HybridBootInfoImporter` fresh admission rule; require current provider query and generation creation.

## Explicit non-goals for P15 MVP

- CXL interleaved boot;
- multiple root ports in the first positive profile;
- CXL fabric manager in ROM/capsule;
- MLD / pooled multi-host boot;
- network boot/recovery in immutable ROM;
- CXL-specific HybridCPU ISA instructions;
- boot-time accelerator ownership;
- full UEFI/ACPI compatibility layer;
- booting kernel XIP from temporary CXL aperture;
- treating QEMU evidence as silicon evidence.
