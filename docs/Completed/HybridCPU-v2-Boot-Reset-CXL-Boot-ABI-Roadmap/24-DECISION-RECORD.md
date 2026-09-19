# 24 — Decision Record

These ADR-style decisions are the architecture freeze for v1. “Future escape hatch” describes how a future design may evolve without making the present contract ambiguous.

## BR-001 — Local immutable Boot ROM is mandatory

**Decision.** Reset begins at local immutable executable storage, not CXL.

**Context.** CXL discovery/mapping is unavailable until some trusted code executes; direct reset-vector CXL execution creates a circular dependency and expands failure/security coupling.

**Alternatives.** Reset directly into CXL; external service processor supplies first instruction; mutable SPI firmware as sole root.

**Rationale.** Local ROM breaks the bootstrap cycle and anchors recovery/trust independently of fabric availability.

**Consequences.** A small platform ROM region and reset-vector contract must be added to HybridCPU-v2.

**Future escape hatch.** A hardware authenticated-fetch engine could replace physical ROM only if it preserves equivalent local root/recovery semantics.

## BR-002 — V1 reset vector is `0x0000_0000_FFFC_0000`

**Decision.** Reserve the top 256 KiB of the low 4 GiB reference platform (`0xFFFC_0000..0xFFFF_FFFF`) for immutable Boot ROM; reset PC is its base.

**Context.** Current inspected HybridCPU surfaces do not freeze a product reset vector/ROM map.

**Alternatives.** Address zero; top-of-address-space vector; configurable vector register.

**Rationale.** Fixed local mapping is simple to model and leaves a contiguous ROM budget while keeping v1 reset semantics deterministic.

**Consequences.** Platform memory map must reserve/protect the range; real products may need a profile update if address decode differs.

**Future escape hatch.** A later platform profile can define another vector under a new reset ABI/platform ID; boot media identity remains unchanged.

## BR-003 — Runtime CXL device index is never boot identity

**Decision.** `deviceId=0`, enumeration order and similar runtime indices MUST NOT identify SingNextOS installation.

**Context.** Topology, hotplug, replacement, switches and simulator construction reorder devices.

**Alternatives.** Fixed index or first Type-3 device.

**Rationale.** Such IDs are ephemeral and accidentally turn discovery mechanics into authority.

**Consequences.** All tests must permute enumeration order; boot policy selects semantic targets.

**Future escape hatch.** None for semantic identity. An index may remain an internal ephemeral array key only.

## BR-004 — `BootVolumeId` is the logical boot identity

**Decision.** A 128-bit install-time UUID identifies a logical boot volume/replica set independently of physical device placement.

**Context.** Boot must survive device replacement, BDF renumbering and fabric route changes.

**Alternatives.** DSN, BDF, media offset, filesystem UUID, image ID.

**Rationale.** Separating logical volume from physical evidence and image version cleanly models replicas and updates.

**Consequences.** Canonical persistent metadata and signed manifest bind `BootVolumeId`; policy targets it.

**Future escape hatch.** Namespace/type may be versioned if a future distributed identity scheme is required, while v1 UUIDs remain valid.

## BR-005 — DSN is optional physical evidence/policy constraint only

**Decision.** PCIe/CXL DSN MAY help pin or diagnose a physical device but is neither required logical identity nor capability.

**Context.** DSN may be absent and naturally changes on replacement.

**Alternatives.** Require DSN match; ignore DSN entirely.

**Rationale.** It is useful for security policy (“only this chassis device”) and diagnostics without breaking logical replacement semantics.

**Consequences.** Policy distinguishes preference vs required physical pin; BootInfo labels DSN as evidence.

**Future escape hatch.** Stronger hardware identity/attestation may replace/add evidence without changing `BootVolumeId`.

## BR-006 — CXL LSA is an optional locator, not canonical metadata

**Decision.** A small versioned HBLR record may live in LSA to locate boot metadata; the canonical header/manifest live in persistent capacity and boot works without LSA.

**Context.** LSA is management metadata with mailbox access and ecosystem namespace/label uses; relying on it makes boot hostage to mailbox availability and label semantics.

**Alternatives.** Put full manifest in LSA; never use LSA; vendor-specific label as sole boot record.

**Rationale.** Optional LSA accelerates discovery without making it trust root or conflicting with provider/namespace ownership.

**Consequences.** Stage-0 needs fallback anchor mapping; HBLR corruption only loses optimization.

**Future escape hatch.** Standardized boot labels could become another locator type, still verified against canonical signed metadata.

## BR-007 — Persistent capacity uses a compact boot-volume format, not a general GPT requirement

**Decision.** V1 uses redundant fixed/versioned superblock/header + manifest slots + aligned payload extents.

**Context.** Stage-0 should not carry a general partition/filesystem stack.

**Alternatives.** GPT+filesystem; raw fixed offsets with no versioned header.

**Rationale.** Compact metadata minimizes immutable parser surface while supporting A/B/recovery/versioning.

**Consequences.** Host tooling must build/inspect this format; payloads are described by signed offsets/lengths/hashes.

**Future escape hatch.** GPT may wrap/expose the volume for tooling if Stage-0 still has a bounded direct locator.

## BR-008 — Firmware owns a temporary single-target HDM boot aperture

**Decision.** Stage-0/backend programs a platform-reserved, non-interleaved HPA window to one selected Type-3 persistent range; mapping is firmware-temporary.

**Context.** Canonical boot bytes require CXL.mem access, but runtime decoder authority belongs below the SingNext provider.

**Alternatives.** Permanent firmware map; OS maps before loading itself; multi-device interleave.

**Rationale.** Temporary mapping solves bootstrap without granting it runtime authority.

**Consequences.** Mapping generation/evidence is handed off only for diagnostics/cleanup; SingNext creates fresh provider mappings.

**Future escape hatch.** Explicit validated mapping-adoption protocol may be standardized later, but cannot infer authority from numeric equality.

## BR-009 — Stage-1 and kernel execute from normal RAM in v1

**Decision.** CXL is source media; executable boot components are copied to local/system RAM and verified before execution.

**Context.** XIP couples instruction fetch to link/fabric/reconfiguration/coherency failures during the most sensitive transition.

**Alternatives.** Stage-1 or kernel XIP from persistent CXL memory.

**Rationale.** Copying sharply simplifies fault containment, decoder takeover and recovery.

**Consequences.** Adequate boot RAM is required; hashes are checked after copy; CXL may disappear after copy without invalidating fetched executable bytes.

**Future escape hatch.** Authenticated XIP may be a separate future feature with precise cache/link/fault/authority rules.

## BR-010 — Secure boot chain is ROM → signed manifest → hashed components

**Decision.** Immutable trust anchor validates a versioned signed manifest; manifest authenticates Stage-1/kernel/services by cryptographic hash.

**Context.** Device/volume identifiers establish location, not trust.

**Alternatives.** Sign each payload independently; trust media checksum; trust device identity.

**Rationale.** One bounded signed metadata object gives compatibility, identity, generation and component integrity with algorithm agility.

**Consequences.** Manifest parser/verifier is immutable TCB; payload reads can be streamed/copy-verified.

**Future escape hatch.** Manifest may support delegated/component signatures via versioned extensions without weakening the root chain.

## BR-011 — Anti-rollback floor lives in protected platform state

**Decision.** `minimumAllowedGeneration` is scoped by rollback domain and stored outside writable CXL boot media; it advances only after authenticated successful confirmation policy.

**Context.** An attacker controlling boot media could otherwise restore both image and rollback metadata.

**Alternatives.** Media generation only; highest discovered generation; advance before first trial.

**Rationale.** External protected floor survives media rollback while trial semantics avoid bricking on a bad update.

**Consequences.** Hardware backend needs protected atomic/monotonic storage; store failure is security-significant.

**Future escape hatch.** Remote attestation/policy may supplement but not silently lower the local floor.

## BR-012 — Protected BootState, not CXL media, selects trial/confirmed A/B image

**Decision.** Inactive slot installation is followed by an atomic protected trial record with bounded attempts; OS confirmation promotes it.

**Context.** Device-local “active” flags are writable with the same media and become ambiguous across replicas.

**Alternatives.** GPT active flag; highest generation always wins; per-device active slot.

**Rationale.** Separates crash-safe content storage from product boot policy and works across replicas.

**Consequences.** Boot selector must reconcile protected state with signed manifests; media metadata can describe slots but not authorize activation.

**Future escape hatch.** A quorum/remote policy service may drive protected state in clustered products.

## BR-013 — `HybridBootInfo` contains no SingNextOS CXL authority

**Decision.** BootInfo carries verified selection results, platform facts, physical evidence and temporary firmware state, but no `OwnedRegion`, provider capability or authoritative hardware handle.

**Context.** SingNextOS contracts distinguish evidence from authority and keep provider-private CXL identifiers below region authority.

**Alternatives.** Pass pre-created region handle; pass decoder/HPA as capability.

**Rationale.** Prevents firmware discovery identity from bypassing runtime admission/generation rules.

**Consequences.** OS must perform fresh CXL discovery; BootInfo types/flags explicitly classify evidence.

**Future escape hatch.** New cross-project capability handoff would require explicit cryptographic/type/generation protocol and ADR.

## BR-014 — SingNextOS performs fresh CXL provider admission after handoff

**Decision.** Kernel entry is followed by fresh discovery, provider generation establishment and ordinary `OwnedRegion`/`RegionUse` creation; firmware mapping is disposable.

**Context.** Topology may change between firmware reads and OS runtime; stale external authority must fail closed.

**Alternatives.** Adopt firmware-discovered device/mapping directly.

**Rationale.** Matches post-CXL operability generation/staleness model and preserves provider abstraction.

**Consequences.** Some duplicate discovery cost is accepted for correctness; evidence can optimize correlation but not skip admission.

**Future escape hatch.** Fast-adoption optimization only with revalidation equivalent to fresh admission.

## BR-015 — No CXL-specific ISA extension for v1

**Decision.** Boot uses normal memory/MMIO/config/platform privileged mechanisms; CXL semantics remain platform/backend operations.

**Context.** Existing SingNextOS/HybridCPU CXL direction explicitly avoids a CXL instruction lane and provider details do not belong in application ISA.

**Alternatives.** `CXL_DISCOVER`, mailbox, decoder or persistent-load instructions.

**Rationale.** No identified boot requirement needs a new opcode; ISA coupling would freeze transport details and complicate virtualization.

**Consequences.** HybridCPU changes are reset/platform/memory-map/backend changes, not instruction encoding.

**Future escape hatch.** Only a generic architectural primitive proven necessary across non-CXL platform uses should be considered separately.

## BR-016 — Local signed recovery is mandatory

**Decision.** Product must have a signed local recovery image independent of all CXL paths, backed by an immutable minimal ROM monitor for diagnostics/halt.

**Context.** Otherwise CXL outage can remove OS and all diagnostics simultaneously.

**Alternatives.** CXL-only recovery; network stack in ROM; debug halt only.

**Rationale.** Local recovery provides bounded serviceability without bloating immutable ROM.

**Consequences.** Board needs recovery medium/storage; recovery image shares trust/rollback policy as explicitly defined.

**Future escape hatch.** Signed recovery environment may add network recovery; ROM remains minimal.

## BR-017 — QEMU validates CXL protocol/model, not HybridCPU execution

**Decision.** Until HybridCPU is a QEMU CPU target, use QEMU Type-3/LSA/HDM topologies to validate CXL assumptions and media tooling separately from HybridCPU simulator execution.

**Context.** QEMU has CXL Type-3 modeling but does not thereby execute this custom ISA.

**Alternatives.** Delay all QEMU work; port HybridCPU to QEMU first.

**Rationale.** Independent protocol validation has high value without blocking CPU architecture work.

**Consequences.** Test harness compares logical fixtures/traces rather than one monolithic boot run.

**Future escape hatch.** A future HybridCPU QEMU target can run end-to-end using unchanged boot ABIs.

## BR-018 — Physical state retention across warm reset does not preserve authority

**Decision.** Platform may retain CXL link/decoder electrical state on warm/watchdog reset, but Stage-0 and SingNextOS assign fresh software generations and revalidate before use.

**Context.** Full topology reset may be expensive/unnecessary, yet stale software handles are unsafe.

**Alternatives.** Always hard-reset fabric; blindly reuse retained decoders/handles.

**Rationale.** Separates performance/physical retention from authority freshness.

**Consequences.** Reset backend must know how to inspect/clear retained state; software never assumes retention equals validity.

**Future escape hatch.** A formally versioned retained-state resume protocol could reduce reconfiguration cost while preserving generation checks.
