# 19 — Implementation Roadmap

Status: proposed implementation sequence. Architectural contracts are frozen by [24-DECISION-RECORD.md](24-DECISION-RECORD.md); phases below are deliberately small enough to land independently.

## Rules for every phase

Every phase MUST preserve the following invariants: no runtime CXL enumeration identifier is semantic boot identity; no pre-OS mapping becomes SingNextOS authority; reset behavior is deterministic; all persistent formats are versioned and length-bounded; new CXL-specific ISA instructions are out of scope; model-only conveniences MUST stay behind architecture-neutral interfaces.

A phase is complete only when its exit criteria and negative tests pass. Later phases may replace backends but MUST NOT silently change `BootVolumeId`, manifest, BootInfo, reset, or authority semantics.

## R0 — Freeze executable contracts and characterize current reset behavior

**Goal.** Turn this roadmap into compile-time contract definitions before changing the CPU runtime.

**Prerequisites.** Existing `HybridCPU_ISE`, current execution model, SingNextOS provider/authority contracts.

**Files/modules affected.** New `HybridCPU_Boot.Contracts/`; test-only characterization around `CoreRuntimeState`, architectural context and `MemoryCycleController`.

**New interfaces/data.** `ResetReason`, `HybridBootPolicyV1`, `BootTargetV1`, `BootVolumeHeaderV1`, `SingNextBootManifestV1`, `HybridBootInfoV1`, `BootSecurityStatus`, `BootEvidenceRecord`, `TemporaryApertureDescriptor`.

**Implementation work.** Define endian/packing/size rules; UUID byte order; overflow-safe range helpers; CRC/hash/signature algorithm IDs; parser hard limits; compile-time/static assertions for fixed headers; snapshot current constructor/startup behavior without pretending it is the final reset ABI.

**Tests.** Golden byte vectors for every structure; malformed length/offset corpus; round-trip tests; test that unknown optional TLVs are skippable and unknown required features fail closed.

**Exit criteria.** Stable v1 binary layouts exist; no production boot behavior changes; all parsers reject overflow/truncation deterministically.

**Risks.** Prematurely baking implementation object layout into ABI. Mitigation: wire format uses explicit-width fields and offsets, never C# object serialization.

**Explicitly deferred.** ROM execution, CXL, cryptographic key storage, OS handoff.

## R1 — Reset controller and platform physical address map

**Goal.** Make reset an explicit platform operation with a defined PC/state and local ROM region.

**Prerequisites.** R0.

**Files/modules affected.** `HybridCPU_ISE/CloseToHSL/Core/State/CoreRuntimeState.cs`; architectural register/PC owner; `Processor.CPU_Core`/cycle owner; frontend fetch path; memory subsystem; new `Platform/Reset/HybridResetController.cs` and `Platform/Memory/PlatformPhysicalAddressMap.cs`.

**New interfaces.** `IHybridResetController.Reset(ResetReason)`, `IPlatformPhysicalAddressMap.Resolve(pa, access)`, region attributes `{RAM, ROM, MMIO, Reserved}`, reset-observer hooks for devices.

**Implementation work.** Implement cold/warm/watchdog/recovery reset causes; force reset PC to `0x0000_0000_FFFC_0000`; clear/initialize architectural and microarchitectural state per [04](04-RESET-ABI.md); disable interrupt acceptance; select one boot virtual thread; map 256 KiB immutable ROM; reject writes to ROM; reset or preserve platform devices according to reset class rather than blindly recreating all CXL state.

**Tests.** PC/register/interrupt reset vectors; ROM execute/read/write permissions; stale pipeline/replay work cannot retire after reset; warm reset leaves only explicitly retained device state; fetch from unmapped address traps deterministically.

**Exit criteria.** Every reset class starts at the same architectural vector with specified state and a machine-readable reset cause.

**Risks.** Current runtime constructor semantics may be relied upon by tests. Keep compatibility test harness separate from product reset.

**Explicitly deferred.** Any CXL-specific action; secure boot.

## R2 — Local Stage-0 execution and non-CXL recovery boot

**Goal.** Prove that immutable/local reset execution can reach a signed local test payload without CXL.

**Prerequisites.** R1.

**Files/modules affected.** New `Platform/Firmware/Stage0Runtime.cs`, ROM image builder, model local-recovery medium, memory loader.

**New interfaces.** Minimal `IStage0Platform` for console/event log, monotonic timer, local RAM allocation, protected policy access and recovery dispatch.

**Implementation work.** Build a 256 KiB ROM image containing reset stub, bounded Stage-0 dispatcher and format verifier; establish private boot SRAM/stack; locate a local recovery manifest; copy a Stage-1 test image into RAM; jump only after range/hash checks.

**Tests.** ROM size gate; corrupted local payload; invalid entry point; no usable RAM; recovery reset always enters recovery policy.

**Exit criteria.** Simulator cold reset executes real Stage-0 control flow from ROM and enters a copied Stage-1 test stub.

**Risks.** Stage-0 feature creep. Enforce binary-size and dependency allowlist gates.

**Explicitly deferred.** PCIe/CXL discovery, A/B policy.

## R3 — Trust store, protected policy and secure local chain

**Goal.** Establish production-shaped trust and rollback semantics independent of CXL.

**Prerequisites.** R2.

**Files/modules affected.** New `Boot/BootPolicyStore.cs`, `Boot/BootStateStore.cs`, `Security/IBootTrustStore.cs`, model OTP/fuses and protected NVRAM, `BootImageVerifier.cs`.

**New interfaces.** `IBootTrustStore.GetTrustEpoch/Keys`, `IProtectedBootState.Read/AtomicUpdate`, `IRollbackFloorStore.Read/Advance`.

**Implementation work.** Provision root-key hash/trust epoch; verify signed manifests; verify Stage-1 component hash after copy; support production/dev policy bits; model recovery signing key; implement rollback floor keyed by `RollbackDomainId`; make floor advancement atomic and confirmation-driven.

**Tests.** Bad signature, unknown key, revoked/old trust epoch, rollback generation, power-loss during protected-state update, unsigned image accepted only under explicitly provisioned development mode.

**Exit criteria.** Local chain is `ROM -> manifest signature -> copied Stage1 hash -> Stage1`, and downgrade is blocked by protected state outside boot media.

**Risks.** Simulator trust store accidentally treated as real secure storage. Name/model types explicitly and document backend replacement.

**Explicitly deferred.** CXL transport and real OTP implementation.

## R4 — Semantic CXL boot-device model, no PCIe details

**Goal.** Validate boot identity and selection semantics before importing transport complexity.

**Prerequisites.** R3.

**Files/modules affected.** New `Platform/Cxl/Preboot/ICxlBootDiscovery.cs`, `ModelCxlType3BootDevice.cs`, `Boot/BootSelector.cs`.

**New interfaces.** `ICxlBootDiscovery.EnumerateCandidates(policy, deadline)` returning physical evidence plus media-reader capability; candidate order is explicitly non-semantic.

**Implementation work.** Model DSN optionality, persistent capacity, LSA bytes, `BootVolumeId`, `ReplicaId`, generations, manifest slots and faults. Implement deterministic selection rules from [07](07-CXL-BOOT-DISCOVERY.md): policy target -> logical ID -> valid signed candidate -> allowed generation -> protected trial/confirmed image state -> deterministic replica tie-break independent of enumeration index.

**Tests.** Device order permutation; DSN replacement with same logical volume; duplicate `BootVolumeId`; stale replica; one invalid + one valid; two identical physical models; required-property filtering.

**Exit criteria.** No API or test asserts “device 0 is OS”; the same logical selection result holds under enumeration permutations.

**Risks.** Semantic model may hide real CXL constraints. R5/R6 explicitly replace the transport/mapping beneath it.

**Explicitly deferred.** Config space, mailbox, HDM programming.

## R5 — Modeled PCIe enumeration, CXL capability parsing and mailbox/LSA

**Goal.** Make discovery path structurally resemble real hardware while preserving R4 semantics.

**Prerequisites.** R4.

**Files/modules affected.** New `Platform/Pci/IPciConfigAccess.cs`, `Platform/Cxl/Preboot/ICxlPrebootTransport.cs`, `ModelPciConfigAccess.cs`, `ModelCxlMailbox.cs`.

**New interfaces.** Bounded config-space reads, capability iterator, CXL component/device capability view, mailbox command with deadline/maximum payload, optional LSA read.

**Implementation work.** Enumerate only policy-relevant root hierarchy within probe budget; parse PCIe/CXL capabilities defensively; read DSN when present; identify Type-3/persistent-capacity candidate; read HBLR locator from LSA when supported; treat absent/unreadable LSA as optimization failure and fall back to canonical persistent-capacity header path.

**Tests.** Malformed capability loops, duplicate/ext-cap corruption, mailbox timeout, unsupported LSA, invalid HBLR CRC/version, DSN absent, mixed CXL/non-CXL devices.

**Exit criteria.** Semantic selector consumes real-looking evidence without depending on BDF/DSN/order for identity.

**Risks.** Overfitting to one CXL revision. Keep capability adapters versioned and transport-private.

**Explicitly deferred.** Real PCI host bridge and hardware mailbox.

## R6 — Temporary HDM boot aperture

**Goal.** Read canonical boot metadata and payload bytes through a firmware-owned CXL.mem aperture.

**Prerequisites.** R5, platform memory-map reservations.

**Files/modules affected.** New `CxlTemporaryBootApertureManager.cs`; platform address map; model host-bridge/root-port/endpoint decoder objects.

**New interfaces.** `CreateSingleTarget(candidate, dpaOffset, length) -> TemporaryBootMapping`, `Read(mapping, offset, length)`, `Destroy(mapping)`.

**Implementation work.** Reserve model 256 MiB HPA aperture selected by platform map; validate decoder granularity/alignment; program required upstream/endpoint decoder chain transactionally; v1 forbids interleave and pooled multi-device striping on boot path; publish mapping attributes as firmware temporary/evidence only; tear down or mark relinquished at handoff.

**Tests.** Decoder unavailable, misalignment, capacity too small, range overflow, read fault, link loss during read, cleanup after every failure, stale mapping cannot be reused across reset generation.

**Exit criteria.** Stage-0 can locate volume header/manifest and copy Stage-1 through the aperture; no consumer receives a provider authority object.

**Risks.** Platform-specific HDM ownership/CFMW details. Encapsulate behind backend and keep absolute aperture HPA out of persistent ABI.

**Explicitly deferred.** Interleave; fabric pooling; OS reuse of firmware decoder state.

## R7 — Stage-1, kernel payload loading and `HybridBootInfo`

**Goal.** Complete the CXL-to-RAM boot flow and stable kernel entry ABI.

**Prerequisites.** R6.

**Files/modules affected.** `Stage0Runtime`, new `Stage1Runtime`, `HybridBootInfoV1` builder, RAM range allocator, kernel test stub.

**New interfaces.** `IStage1BootServices` restricted to verified manifest/payload reader, memory map, log sink and final handoff; no generic mutable firmware runtime.

**Implementation work.** Stage-0 copies/verifies Stage-1, transfers control; Stage-1 verifies kernel/services descriptors and copies to normal RAM; builds checksummed immutable BootInfo; marks hardware identifiers as evidence; registers: `PC=KernelEntry`, `x10=BootInfo`, `x11=size`, `x12=firmwareAbi`, `x13=bootCpuOrVt`, `x14=resetReason`, `x15=flags`; enter kernel with MMU off/physical addressing and interrupts disabled per v1 reset/handoff ABI.

**Tests.** Component overlap, executable outside loaded range, bad hash, corrupted BootInfo, unsupported firmware/CPU ABI, exact register entry values, CXL failure after completed copy does not corrupt RAM-resident Stage-1/kernel execution.

**Exit criteria.** A SingNextOS early-boot test kernel is entered from RAM with valid `HybridBootInfo` after CXL-backed image verification.

**Risks.** Existing compiler calling convention conflict. Boot-entry registers are a dedicated ABI and must be reserved/documented independently of ordinary function calls.

**Explicitly deferred.** Runtime firmware calls and XIP.

## R8 — SingNextOS fresh discovery and authority takeover

**Goal.** Demonstrate the architectural cut between boot evidence and runtime CXL authority.

**Prerequisites.** R7 plus SingNextOS CXL provider contracts.

**Files/modules affected.** SingNextOS early boot adapter/ingestion; existing discovery/provider path; integration tests spanning both projects. Avoid changes to public `OwnedRegion` authority semantics.

**New interfaces.** A narrow BootInfo evidence importer that produces diagnostics/hints only; provider admission remains existing/narrow provider API.

**Implementation work.** Parse BootInfo copy; perform fresh CXL discovery; match logical boot volume only for continuity/diagnostics; establish provider/device/fabric generations; create fresh provider-private mappings; create/use normal `OwnedRegion`/`RegionUse` under existing SingNextOS authority; invalidate or release firmware aperture; mark firmware evidence stale if topology changed.

**Tests.** Same device/new BDF; same BootVolume/new DSN; firmware mapping absent at takeover; device disappears before admission; mapping address changes; stale generation; provider refuses admission; verify no BootInfo physical ID can call an authority-requiring API.

**Exit criteria.** End-to-end test proves OS authority is newly minted after provider admission and does not wrap/reuse firmware mapping identity.

**Risks.** Temptation to “adopt” firmware decoder for faster boot. v1 forbids authority adoption; later optimization requires a new validated adoption protocol/ADR.

**Explicitly deferred.** General fabric manager boot dependency.

## R9 — A/B trial/confirmation, anti-rollback and local recovery

**Goal.** Make updates power-loss safe and CXL failure non-fatal to serviceability.

**Prerequisites.** R8.

**Files/modules affected.** BootState, update writer tooling/model, recovery image, OS confirmation path.

**New interfaces.** `SetTrial(ImageId, attempts)`, `ConfirmBoot(ImageId,generation)`, recovery-reason record; authenticated OS-to-protected-store confirmation primitive.

**Implementation work.** Write inactive slot payloads first, manifests last; verify read-back; atomically set protected trial state; bounded trial attempts; confirmation advances rollback floor only after OS health checkpoint; fallback A↔B/replica; final signed local recovery; ROM monitor exposes bounded diagnostics and recovery dispatch only.

**Tests.** A/B success, failed trial, interrupted payload/manifest/state writes, power loss before/after protected state commit, floor advancement replay, all CXL absent, bad recovery image.

**Exit criteria.** No tested interruption can leave both confirmed image and local recovery unreachable; trial failure returns to last confirmed eligible image without lowering rollback floor.

**Risks.** Confirmation spoofing. Bind confirmation to boot nonce/image/generation or trusted early-OS channel in production backend.

**Explicitly deferred.** Network recovery in ROM.

## R10 — Multi-device, replica and fabric-aware selection

**Goal.** Validate logical identity across topology changes and replicas without making fabric identifiers authoritative.

**Prerequisites.** R9.

**Files/modules affected.** Model fabric topology, BootSelector, SingNextOS fabric-provider integration tests.

**New interfaces.** Policy property constraints and optional preferred physical evidence; no semantic fabric route field.

**Implementation work.** Model 2+ Type-3 devices, replicas, MLD/logical-device evidence, switch paths and replacement; handle duplicate logical IDs deterministically; reject ambiguous same-generation conflicting manifests; prefer valid protected trial/confirmed ImageId and highest policy-eligible generation according to signed/rollback rules, not discovery order.

**Tests.** Hotplug/order permutations, missing preferred DSN, same volume/different generations, exact duplicate replica, conflicting same generation, fabric path change, device replacement, stale FM generation after handoff.

**Exit criteria.** Selection and post-handoff authority remain invariant to BDF/port/route renumbering.

**Risks.** Real FM may require pre-boot allocation. Treat that as platform provisioning evidence and backend action, not OS capability; see [23](23-OPEN-QUESTIONS.md).

**Explicitly deferred.** Boot striping/interleave across devices.

## R11 — QEMU CXL protocol/model validation

**Goal.** Validate PCIe/CXL Type-3, LSA and HDM assumptions against an independent mature model even though HybridCPU ISA is not a QEMU target.

**Prerequisites.** R10 formats and transport adapters.

**Files/modules affected.** External QEMU launch scripts/test fixtures; CXL metadata image builder; protocol trace comparator.

**New interfaces.** None in architectural ABI; optional host-side adapter/test harness.

**Implementation work.** Create QEMU x86/arm host topology with CXL host bridge/root ports/Type-3 devices, file-backed persistent memory and LSA; exercise decoder setup, multiple devices, switches/interleave as negative/non-boot cases, hotplug where supported; compare parsed discovery evidence and media layouts with simulator.

**Tests.** Type-3 persistence across QEMU restart; LSA reads; HDM HPA→DPA mapping; duplicate/multiple devices; device omission; malformed boot media; mailbox failure injection where feasible.

**Exit criteria.** No architectural format/API change is required to run the same logical boot-media fixtures against model and QEMU protocol tests.

**Risks.** QEMU behavior is not silicon proof. Record model-vs-spec-vs-hardware assumptions separately.

**Explicitly deferred.** Executing HybridCPU machine code under QEMU.

## R12 — Real platform firmware/backend

**Goal.** Replace model transport/security stores with real platform implementations without changing v1 ABIs.

**Prerequisites.** R11; selected hardware/host bridge; resolved items in [23](23-OPEN-QUESTIONS.md).

**Files/modules affected.** Hardware `IPciConfigAccess`, CXL CCI/mailbox adapter, host/root/endpoint decoder manager, timer/watchdog, OTP/trust store, protected NVRAM/monotonic counter, local recovery storage, DMA/IOMMU quiesce.

**New interfaces.** Ideally none; if hardware proves a missing semantic capability, add versioned optional backend feature rather than leaking raw IDs into cross-project ABI.

**Implementation work.** Enumerate real hierarchy; honor firmware/ACPI/platform resource ownership; configure boot aperture; enforce DMA containment; verify persistence/flush semantics; wire reset causes; implement hardware event log; validate recovery under physical removal/link reset.

**Tests.** Power-cycle matrix, warm/watchdog reset, device replacement, link flap, poison/read errors where supported, protected-state wear/power-loss, firmware update compatibility, scope/fabric reconfiguration.

**Exit criteria.** Same signed boot volume created for simulator boots on target hardware using unchanged `BootVolumeId`, manifest and BootInfo v1 semantics.

**Risks.** Host firmware may retain ownership of decoders or resource windows. Backend must fail closed or negotiate via a platform-specific mechanism outside OS authority ABI.

**Explicitly deferred.** Cross-platform universal firmware standard.

## R13 — Production hardening and ABI freeze gate

**Goal.** Qualify the subsystem for long-lived images and field recovery.

**Prerequisites.** R12.

**Files/modules affected.** All boot components, CI, fuzz targets, production provisioning/update tools.

**New interfaces.** Only backwards-compatible v1 extensions/TLVs; incompatible changes require v2 and migration rules.

**Implementation work.** Parser fuzzing; constant-time/side-channel review appropriate to selected crypto library; DMA threat review; key rotation drill; rollback counter exhaustion strategy; boot latency budgets; bounded parallel probing; telemetry/event schema; ABI compatibility corpus; recovery manufacturing/service procedure; reproducible ROM build and binary-size gate.

**Tests.** Full [20-TEST-AND-VALIDATION-PLAN.md](20-TEST-AND-VALIDATION-PLAN.md), long-running fault injection, reset storms, corrupted protected store, arbitrary device reorder, update/recovery soak, old firmware/new image and new firmware/old eligible image.

**Exit criteria.** All mandatory v1 conformance tests pass on simulator and chosen hardware; threat-model controls are verified; recovery drill succeeds with all CXL devices removed; ABI fixtures are frozen in CI.

**Risks.** Operational provisioning becomes the weakest link. Treat provisioning artifacts/versioning as release inputs, not ad-hoc lab state.

**Explicitly deferred.** CXL XIP, boot interleave, runtime firmware services, generalized UEFI compatibility, new CXL ISA instructions.

## Dependency graph

```text
R0 contracts
  |
R1 reset/map
  |
R2 local ROM boot
  |
R3 trust/policy
  |
R4 semantic CXL model
  |
R5 PCI/CXL.io/LSA model
  |
R6 temporary HDM aperture
  |
R7 Stage1 + BootInfo
  |
R8 SingNext fresh authority
  |
R9 A/B + recovery
  |
R10 multi-device/fabric
  |
R11 QEMU protocol validation
  |
R12 real platform backend
  |
R13 production qualification
```

The intentional ordering is important: identity/authority correctness is testable before real transport complexity, and real hardware can replace backends without reopening fundamental boot identity decisions.
