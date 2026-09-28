# 23 — Open Questions Requiring Future Hardware or Protocol Decisions

The fundamental architecture is intentionally **not** open here. `BootVolumeId` is the logical boot identity; runtime device order is not; firmware CXL state is evidence/temporary state; SingNextOS performs fresh provider admission; Stage-1 executes from RAM; v1 has no CXL-specific ISA. The questions below require a concrete target platform, CXL generation, security device, or serviceability decision.

## OQ-01 — Exact physical placement of the boot aperture

**Already decided:** the aperture is a platform-reserved HPA range, firmware-owned, temporary, single-target/non-interleaved in v1; persistent metadata never stores its absolute HPA.

**Open:** exact HPA base/size on real HybridCPU platforms; whether a host bridge exposes a pre-reserved CXL fixed memory window; how conflicts with DRAM/MMIO holes are represented; whether 256 MiB remains the preferred reference size.

**Decision input needed:** real platform address width/map and host-bridge decoder/resource model.

## OQ-02 — Real decoder ownership/programming protocol

**Already decided:** Stage-0 needs a backend operation equivalent to transactionally establish an HPA→target persistent-range mapping and must clean it on failure.

**Open:** which decoder levels firmware may program directly; whether platform firmware/ACPI-like resource ownership preconfigures host decoder windows; commit/lock/reset details for chosen CXL revision and host implementation.

**Decision input needed:** host bridge/root port/Type-3 hardware manuals and target firmware ownership model.

## OQ-03 — Minimum real pre-OS mailbox/CCI path

**Already decided:** LSA is optional; boot must work without it by mapping the canonical boot anchor. DSN is optional evidence.

**Open:** which real target exposes mailbox/CCI early enough to read LSA before CXL.mem mapping; transport details; command payload/timeouts; reset behavior across mailbox failures.

**Decision input needed:** chosen device/CXL revision and platform transport.

## OQ-04 — Persistent write/flush domain for updates

**Already decided:** updates are inactive-slot, ordered, verified and crash-safe; manifest becomes valid only after payload persistence; protected trial state commits separately.

**Open:** exact cache flush/fence/device persistence command needed to prove data reached the persistent domain on HybridCPU + chosen CXL Type-3 device; ADR/eADR-like platform assumptions if any.

**Decision input needed:** HybridCPU cache persistence model, memory-controller behavior and Type-3 persistence guarantees.

## OQ-05 — Production trust-root and monotonic-state hardware

**Already decided:** immutable ROM anchors trust; writable CXL media cannot hold the sole rollback floor; protected policy/trial/floor state must fail closed.

**Open:** OTP/fuse layout, secure element vs SoC protected NVRAM, atomic-record primitive, monotonic counter capacity/wear, manufacturing provisioning and RMA reset policy.

**Decision input needed:** SoC security block and manufacturing/service model.

## OQ-06 — Production crypto algorithm profile

**Already decided:** manifest format is algorithm-agile and signatures/hashes are identified explicitly; production does not permit unsigned images.

**Open:** initial mandatory signature/hash algorithm pair, key sizes, acceleration availability, exact key-rotation/trust-epoch storage and deprecation schedule.

**Decision input needed:** platform crypto implementation, compliance/performance requirements and lifecycle horizon.

## OQ-07 — DMA containment before OS IOMMU ownership

**Already decided:** an untrusted/malicious device must not be able to DMA into arbitrary Stage-0/Stage-1/kernel memory during boot; boot mapping authority is narrow.

**Open:** whether the target root complex starts with bus mastering disabled, supplies an early IOMMU/domain, or needs a firmware DMA quarantine primitive; which device classes are enabled during boot.

**Decision input needed:** PCIe root complex/IOMMU reset defaults and hardware isolation features.

## OQ-08 — MLD/fabric preassignment before OS fabric manager starts

**Already decided:** fabric route/MLD identifiers are provider-private evidence, not logical boot identity; v1 does not require cross-device interleaving.

**Open:** whether a fabric-attached boot target is statically assigned to the host before reset, whether an external FM must perform assignment, and how Stage-0 is told that assignment is stable enough to probe without granting OS authority.

**Decision input needed:** selected CXL fabric/MLD deployment and external FM protocol.

## OQ-09 — CXL IDE/link-security boot policy

**Already decided:** media authenticity comes from signed manifest and hashes; IDE does not replace secure boot.

**Open:** whether production policy requires IDE before reading boot media, where link keys originate, and whether Stage-0 or platform hardware establishes the secure link.

**Decision input needed:** threat/environment model and target CXL security implementation.

## OQ-10 — Local recovery physical medium

**Already decided:** a signed local recovery path independent of CXL is mandatory, plus immutable bounded ROM diagnostics/halt behavior.

**Open:** on-chip flash, SPI NOR, eMMC partition, service cartridge, or another local medium; capacity; update/locking policy; whether recovery image is field-updatable separately from ROM.

**Decision input needed:** board/product serviceability design.

## OQ-11 — Reset signal retention matrix

**Already decided:** software invalidates transient mappings/authority across all reset causes even if hardware retains electrical/programmed state.

**Open:** exactly which CXL/PCIe host/device registers, links, decoder locks and error state survive cold/warm/watchdog reset on target silicon; whether Stage-0 must explicitly tear down retained decoders before reprogramming.

**Decision input needed:** HybridCPU SoC reset tree and device reset semantics.

## OQ-12 — Boot confirmation channel

**Already decided:** a trial image cannot advance rollback floor until successful OS confirmation; confirmation must bind to the image/generation being confirmed.

**Open:** protected firmware mailbox, secure monitor call, SoC NVRAM controller, reboot-time signed confirmation record, or another authenticated local primitive; nonce/anti-replay details.

**Decision input needed:** available privilege/security monitor primitives and protected-store access path.

## OQ-13 — Machine-check/poison behavior during pre-OS CXL reads

**Already decided:** any persistent read failure invalidates the in-progress copy and triggers bounded fallback; partial executable data is never run.

**Open:** exact HybridCPU exception/poison reporting path, whether poison is synchronous to load, and which error records need clearing/reset before retry.

**Decision input needed:** future HybridCPU RAS/exception definition and real CXL host/device behavior.

## OQ-14 — Hardware event log persistence

**Already decided:** diagnostics are evidence, not authority, and boot must work even if log persistence fails.

**Open:** size/location of a protected/recovery-readable event log, privacy/service policy, rollover and whether the OS can append confirmation/recovery reason records.

**Decision input needed:** platform NVRAM budget and field-support requirements.

## OQ-15 — Firmware/ROM update mechanism

**Already decided:** ordinary CXL OS A/B update cannot rewrite the immutable root or silently replace its trust policy.

**Open:** ROM truly mask-ROM vs first-stage immutable boot block plus authenticated flash; capsule/service update path; rollback policy for firmware ABI; recovery from interrupted firmware update.

**Decision input needed:** chip/board boot storage technology and product lifecycle requirements.

## Questions explicitly closed by this roadmap

These MUST NOT reappear as implementation-time “open questions” without a new ADR: whether to boot from device index 0 (no); whether DSN is OS identity (no); whether LSA is canonical trust metadata (no); whether Stage-1 XIP is the default (no); whether BootInfo CXL IDs are capabilities (no); whether SingNextOS adopts firmware authority directly (no); whether v1 needs CXL ISA instructions (no); whether anti-rollback floor may live only on CXL media (no); whether local recovery is optional (no).
