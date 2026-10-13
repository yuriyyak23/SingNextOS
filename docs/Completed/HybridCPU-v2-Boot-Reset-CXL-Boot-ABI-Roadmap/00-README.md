# HybridCPU-v2 Boot/Reset + CXL Boot ABI Roadmap

**Статус:** самостоятельная архитектурная спецификация и implementation roadmap.  
**Research snapshot:** `HybridCPU-v2` и `SingNextOS`, ветка `master`, просмотрены 2026-09-18. Где существующие roadmap фиксируют baseline SHA, он указан в [25-SOURCE-EVIDENCE.md](25-SOURCE-EVIDENCE.md).  
**Нормативные слова:** MUST / MUST NOT / SHOULD / MAY используются в смысле обязательности контракта.

## 1. Проблема

SingNextOS должен иметь возможность загружаться с persistent memory CXL Type-3, но полноценная CXL authority после старта принадлежит SingNextOS/provider layer. CPU после reset ещё не может использовать эту authority, а до чтения образа с CXL необходимо минимально обнаружить PCIe/CXL endpoint, получить его boot metadata и создать HPA→DPA путь.

Решение — **узкий pre-OS bootstrap**, который владеет только временной firmware authority:

```text
HybridCPU Reset
  |
  v
Immutable local Boot ROM (Stage-0)
  |  root of trust + reset contract
  v
minimal PCIe/CXL.io discovery
  |  IDs are evidence only
  v
optional LSA Boot Locator hint
  |  untrusted locator, never authority
  v
firmware-owned single-target temporary HDM mapping
  |
  v
Boot Volume Header + signed Boot Manifest
  |
  +--> verify generation / signature / stage1 hash
  v
copy Stage-1 to trusted normal RAM
  |
  v
Stage-1 verifies + copies kernel/services to normal RAM
  |
  v
HybridBootInfo (identity + evidence + temporary-state descriptors)
  |
  |===================== HANDOFF =====================|
  v
SingNextOS early boot
  |
  +--> fresh PCIe/CXL discovery
  +--> ICxl* provider admission
  +--> new Device/Fabric/Mapping generations
  +--> normal OwnedRegion / RegionUse authority
  +--> tear down or replace firmware boot aperture
  v
Normal SingNextOS operation
```

Ключевой ответ: **не CXL device index, BDF, DSN, HPA, DPA, port или decoder определяют OS installation.** Semantic boot identity — `BootVolumeId`; physical identifiers являются discovery/evidence/policy hints. Runtime authority создаётся заново SingNextOS после handoff.

## 2. Архитектурные решения

1. **Local Boot ROM is mandatory.** Reset-vector execution из CXL запрещено базовым профилем. Reset PC указывает на immutable local ROM.
2. **Boot ROM ограничен Stage-0.** Он не содержит SingNextOS provider/fabric manager/UEFI-подобный runtime. Его TCB: reset, bounded PCIe/CXL bootstrap, locator, temporary decoder, manifest verification, anti-rollback, copy Stage-1, recovery dispatch.
3. **`BootVolumeId` — logical OS boot identity.** Он UUID/GUID, создаётся при установке и сохраняется при допустимой replacement/replication операции.
4. **`PCIe DSN` — optional hardware evidence, не identity/authority.** Он полезен как policy pin/hint и для diagnostics, но отсутствие/смена DSN не меняют BootVolume semantics.
5. **LSA — optional accelerator, не source of truth.** В LSA MAY находиться compact `HybridBootLocatorRecord`; канонический header и signed manifest лежат в persistent capacity. ROM должен уметь загрузиться без LSA, программируя узкое mapping к boot anchor.
6. **Temporary CXL mapping firmware-owned, single-target, non-interleaved.** Он создаётся только для boot и никогда не превращается автоматически в SingNextOS provider binding.
7. **Stage-1/kernel работают из normal RAM.** XIP из CXL boot aperture не является v1 режимом; это разрывает runtime survival от boot link/topology.
8. **Secure boot цепочка:** ROM trust anchor → signed manifest → Stage-1 → kernel/services. Rollback floor хранится вне CXL media в monotonic protected state.
9. **A/B state authoritative только в protected platform boot state.** CXL volume содержит signed manifests и crash-safe metadata, но attacker-controlled media не выбирает trial/confirmed slot самостоятельно.
10. **SingNextOS performs fresh admission.** BootInfo CXL fields — evidence/diagnostics/temporary firmware state. `OwnedRegion`, RegionAuthority и provider generations создаются только OS/provider layer.
11. **No CXL-specific ISA instructions in v1.** CXL management — platform/config/MMIO problem. ISA меняется только если текущая machine model не может выразить reset state/normal memory fetch; новых CXL opcodes не требуется.
12. **QEMU validates protocol/model, not HybridCPU execution.** До появления QEMU target HybridCPU end-to-end CPU execution остаётся в ISE, а QEMU используется для CXL Type-3/LSA/HDM/topology fixtures.

Полный ADR: [24-DECISION-RECORD.md](24-DECISION-RECORD.md).

## 3. Identity и authority model

| Слой | Тип | Стабильность | Назначение | Authority? |
|---|---|---:|---|---:|
| PCI BDF / enum index | runtime location | низкая | найти endpoint в текущем boot | **нет** |
| port / decoder / HPA / DPA / route | provider-private topology | низкая | materialization текущего mapping | **нет** |
| PCIe DSN / device serial | physical evidence | средняя/высокая | pinning, diagnostics, substitution evidence | **нет** |
| `BootVolumeId` | logical volume identity | высокая | найти SingNextOS installation/replica set | **нет capability** |
| `ReplicaId` | logical replica identity | высокая для реплики | deterministic replica handling | **нет** |
| `ImageId` | image identity | immutable per image | signed selection/telemetry | **нет** |
| `ImageGeneration` | rollback ordering | monotonic per domain | anti-rollback | **нет** |
| `RegionAuthority` / `OwnedRegion` | live OS authority | generation-bound | runtime use/reclaim/ownership | **да, только после OS admission** |

`BootVolumeId` является semantic identifier, но **не capability**. Само знание UUID не даёт права использовать CXL memory.

## 4. Boot flow

```text
RESET_ASSERTED
  -> RESET_ARCH_STATE
  -> FETCH_RESET_VECTOR_FROM_ROM
  -> INIT_BOOT_SCRATCH
  -> READ_PROTECTED_BOOT_POLICY
  -> ENUMERATE_BOUNDED_PCI_CXL
  -> BUILD_CANDIDATE_EVIDENCE
  -> [optional GET_LSA locator]
  -> FOR policy target in priority order:
       filter by BootVolumeId / required properties
       create temporary single-target mapping
       read redundant BootVolumeHeader
       read bounded manifest
       verify compatibility + signature + rollback floor
       resolve trial/confirmed slot
       copy+hash Stage1 into system RAM
       if success -> ENTER_STAGE1
     if none -> LOCAL_RECOVERY
  -> Stage1 verify/copy kernel + services
  -> build immutable HybridBootInfo
  -> enter SingNextOS
  -> OS fresh discovery/provider admission
  -> invalidate/replace firmware mapping
```

Selection is deterministic but does **not** use enumeration order as semantic priority. См. [07-CXL-BOOT-DISCOVERY.md](07-CXL-BOOT-DISCOVERY.md).

## 5. Trust chain

```text
ROM bytes + ROM verification code
      |
      +--> OTP/fuse RootKeyHash / ProductionLock
      +--> protected BootTrustStore / key-set generation
      +--> protected BootPolicy / BootState / RollbackFloor
      |
      v
signed SingNextBootManifest
      |
      +--> hash(Stage1) --copy--> normal RAM --execute
      |
      v
Stage1 uses already-verified manifest
      +--> hash/copy kernel
      +--> hash/copy early services/root payload
      v
SingNextOS
```

Ни LSA, ни BootVolumeHeader, ни DSN не заменяют signature verification. Locator metadata считается attacker-controlled input. Подробности: [12-SECURE-BOOT-AND-ANTI-ROLLBACK.md](12-SECURE-BOOT-AND-ANTI-ROLLBACK.md).

## 6. Reset profile v1

Reference platform profile:

- reset vector: `0x0000_0000_FFFC_0000`;
- immutable ROM window: `0x0000_0000_FFFC_0000..0x0000_0000_FFFF_FFFF` (256 KiB);
- native bundle alignment: 256 bytes;
- Boot SRAM/scratch: platform-local, minimum 256 KiB, address described by platform profile, zeroed on reset;
- one boot CPU / virtual thread runnable; siblings held/parked;
- architectural x1..x31 = 0, x0 = 0; no architectural stack is assumed until Stage-0 initializes it;
- fetch uses physical addresses; address translation disabled/bare;
- interrupts masked; pipeline/replay/backend outstanding state empty;
- caches invalidated/disabled-or-clean according to implementation profile;
- reset reason latched in platform reset status.

The ROM base is a **HybridCPU v1 platform-profile contract**, not a universal CXL address. Future profiles may move it by changing `PlatformId`/Reset ABI major version, never by silently changing an existing profile.

## 7. Boot aperture v1

Boot aperture is **not architecturally fixed at a universal HPA**. Platform map reserves a contiguous `FirmwareCxlBootAperture`; reference simulator uses 256 MiB. Stage-0 may window-map smaller regions. Requirements:

- size >= 16 MiB; 64–256 MiB recommended;
- never overlaps usable RAM, ROM, MMIO, kernel destination or BootInfo;
- single endpoint / single DPA range per active mapping;
- no interleave in v1 boot path;
- programming may require a decoder at each topology hop — API models this as one transaction, not “one physical decoder register”;
- OS receives it as `TemporaryFirmwareMapping`, not as usable RAM/authority;
- default takeover tears it down/recreates mapping after fresh provider discovery.

See [11-TEMPORARY-HDM-BOOT-APERTURE.md](11-TEMPORARY-HDM-BOOT-APERTURE.md).

## 8. Persistent volume model

Canonical metadata is located at offset 0 of the **persistent partition/capacity**, not absolute device DPA 0:

```text
persistent capacity base P0

P0 + 0x000000  BootVolumeHeader copy 0 (4 KiB)
P0 + 0x001000  BootVolumeHeader copy 1 (4 KiB)
P0 + 0x010000  Manifest A region
P0 + 0x020000  Manifest B region
P0 + 0x030000  Recovery manifest / optional
P0 + 0x040000  boot update journal / replica metadata
...            reserved metadata prefix up to 16 MiB
payload extents (Stage1/kernel/services), normally 2 MiB aligned
```

This is a compact boot format rather than GPT, minimizing immutable parser complexity. GPT compatibility may be added outside Stage-0 later. See [09-CXL-PERSISTENT-BOOT-LAYOUT.md](09-CXL-PERSISTENT-BOOT-LAYOUT.md).

## 9. Handoff model

Handoff is a **semantic discontinuity in authority**:

```text
firmware evidence / temporary mapping
                |
                | HybridBootInfo
                v
        SingNextOS early boot
                |
          fresh discovery
                |
      provider admission/generation
                |
 RegionAuthority + OwnedRegion + RegionUse
```

Firmware does not “grant” CXL authority to SingNextOS. It only proves which bytes it booted, from which logical volume/image, and what temporary machine state exists. OS validates the platform state it intends to continue using. See [14-SINGNEXTOS-HANDOFF-AND-AUTHORITY.md](14-SINGNEXTOS-HANDOFF-AND-AUTHORITY.md).

## 10. Implementation phases — summary

1. Freeze Boot/Reset contracts and baseline tests.
2. Add reset controller + ROM/physical-map abstraction, boot local image.
3. Add versioned BootPolicy/BootState/manifest + software secure boot.
4. Add semantic `ModelCxlBootDevice`, BootVolume discovery and faults.
5. Add modeled PCI config/CXL Type-3 capability + optional DSN/LSA mailbox.
6. Add firmware temporary HDM aperture and copy Stage-1.
7. Add Stage-1 + `HybridBootInfo` + register entry ABI.
8. Add SingNextOS fresh rediscovery/provider-admission integration test.
9. Add A/B, trial confirmation, anti-rollback and local recovery.
10. Add multi-device/replica/fabric discovery semantics and deterministic conflicts.
11. Run QEMU CXL protocol/layout fixture validation.
12. Add real firmware/backend adapter and hardware persistence/reset/RAS validation.
13. Production hardening: hardware trust storage, DMA containment, key rotation, fault budget/performance.

Detailed exit criteria are in [19-IMPLEMENTATION-ROADMAP.md](19-IMPLEMENTATION-ROADMAP.md).

## 11. Non-negotiable invariants

- `deviceId == 0`, enumeration order, BDF, decoder ID, HPA, DPA, port and fabric route MUST NOT identify SingNextOS installation.
- A physical ID MUST NOT be interpreted as a SingNextOS capability.
- A firmware mapping MUST NOT be transformed into `OwnedRegion` authority by pointer/handle reuse.
- Stage-0 MUST use bounded parsers, bounded enumeration and bounded mailbox/timeouts.
- Stage-0 MUST verify Stage-1 after copy and before execute.
- Stage-1 MUST verify every executable/data payload it loads against signed manifest hashes.
- Anti-rollback floor MUST be held outside writable CXL boot media.
- Successful “device complete” or readable CXL bytes MUST NOT imply SingNextOS publication/ownership.
- Reset/reconfiguration MUST invalidate stale firmware mapping assumptions even if hardware topology is physically preserved across warm reset.
- Initial implementation MUST NOT add CXL-specific ISA instructions.
- Local recovery MUST remain possible when all CXL paths are unavailable.

## 12. Обязательные 30 ответов

| # | Ответ |
|---:|---|
| 1 | Из immutable local Boot ROM at reset vector; [04](04-RESET-ABI.md). |
| 2 | Reset stub, bounded Stage-0, verification, minimal PCI/CXL bootstrap, recovery dispatch; [05](05-BOOT-ROM-AND-STAGE0.md). |
| 3 | Provisionable policy/boot state in protected NVRAM; trust hash/locks in OTP/fuse; [08](08-BOOT-IDENTITY-AND-POLICY.md). |
| 4 | Bounded PCI/CXL enumeration + optional LSA locator + canonical volume header via temporary mapping; [07](07-CXL-BOOT-DISCOVERY.md). |
| 5 | Device 0 changes with enumeration/topology and is not semantic identity; [07](07-CXL-BOOT-DISCOVERY.md). |
| 6 | PCIe DSN/device serial is optional physical evidence; [08](08-BOOT-IDENTITY-AND-POLICY.md). |
| 7 | `BootVolumeId` is logical boot identity; image identity is separate; [08](08-BOOT-IDENTITY-AND-POLICY.md). |
| 8 | Install-time 128-bit UUID grouping one logical volume/replica set, not a capability. |
| 9 | MAY be used as physical hint/pinning; MUST NOT be required for semantic identity. |
| 10 | Optional locator optimization; not canonical metadata/source of trust; [07](07-CXL-BOOT-DISCOVERY.md), [09](09-CXL-PERSISTENT-BOOT-LAYOUT.md). |
| 11 | Persistent capacity metadata prefix, redundant manifest slots; [09](09-CXL-PERSISTENT-BOOT-LAYOUT.md). |
| 12 | Versioned signed bounded manifest with compatibility, IDs, generation and payload descriptors; [10](10-BOOT-MANIFEST.md). |
| 13 | Firmware programs a single-target transaction across required HDM decoder hops; [11](11-TEMPORARY-HDM-BOOT-APERTURE.md). |
| 14 | Platform-reserved HPA range, not universal hard-coded address; reference model 256 MiB; [11](11-TEMPORARY-HDM-BOOT-APERTURE.md). |
| 15 | Stage-0/Stage-1 firmware owns mapping until OS takeover/release. |
| 16 | It is never Sing authority; it becomes disposable at kernel handoff and is invalidated/replaced after OS fresh admission. |
| 17 | Stage-1 and kernel are copied to normal RAM; v1 rejects XIP. |
| 18 | `HybridBootInfo`: reset/security/selection, memory map, loaded ranges, evidence, temporary mapping; [13](13-HYBRID-BOOT-INFO-ABI.md). |
| 19 | No BootInfo CXL field is live Sing authority; OS-owned state is only newly created post-admission state. |
| 20 | Fresh discovery → narrow CXL provider admission → generation-bound binding → normal OwnedRegion/RegionUse; [14](14-SINGNEXTOS-HANDOFF-AND-AUTHORITY.md). |
| 21 | ROM root → signed manifest → hashes of Stage1/kernel/services; [12](12-SECURE-BOOT-AND-ANTI-ROLLBACK.md). |
| 22 | Monotonic floor per rollback domain in protected storage, raised on confirmation, not trial. |
| 23 | Protected BootState chooses confirmed/trial A/B; bounded attempts; CXL metadata is crash-safe but not authoritative slot policy; [15](15-RECOVERY-AND-A-B-BOOT.md). |
| 24 | Try other replica/target, then signed local recovery, then ROM recovery monitor/halt diagnostics. |
| 25 | Signed local recovery image + immutable minimal monitor; network recovery belongs in signed recovery environment, not ROM. |
| 26 | No CXL-specific ISA change required; only reset/platform semantics need implementation; [04](04-RESET-ABI.md). |
| 27 | Core state/reset, architectural PC/register reset, memory/platform routing, new platform/boot modules and tests; [02](02-CURRENT-STATE-ANALYSIS.md), [19](19-IMPLEMENTATION-ROADMAP.md). |
| 28 | `HybridCPU_Boot.Contracts`, Reset, PlatformMemoryMap, PCI/CXL preboot, verifier, policy/state, simulator backends; [19](19-IMPLEMENTATION-ROADMAP.md). |
| 29 | Model PCI/CCI/HDM, in-memory OTP/NVRAM, fault injector are simulator-only early; ABIs/formats are not. |
| 30 | QEMU/hardware swap backend adapters under same contracts; BootInfo/manifest/identity remain unchanged; [18](18-QEMU-AND-REAL-HARDWARE-PATH.md). |

## 13. Document map

- [01-ARCHITECTURAL-CONTEXT.md](01-ARCHITECTURAL-CONTEXT.md) — constraints inherited from both projects.
- [02-CURRENT-STATE-ANALYSIS.md](02-CURRENT-STATE-ANALYSIS.md) — current code/doc audit and concrete gaps.
- [03-BOOT-RESET-ARCHITECTURE.md](03-BOOT-RESET-ARCHITECTURE.md) — end-to-end states and ownership.
- [04-RESET-ABI.md](04-RESET-ABI.md) — reset vector, machine state and reset causes.
- [05-BOOT-ROM-AND-STAGE0.md](05-BOOT-ROM-AND-STAGE0.md) — immutable TCB boundary.
- [06-STAGE1-LOADER.md](06-STAGE1-LOADER.md) — load/copy/verify kernel and services.
- [07-CXL-BOOT-DISCOVERY.md](07-CXL-BOOT-DISCOVERY.md) — discovery alternatives and selected algorithm.
- [08-BOOT-IDENTITY-AND-POLICY.md](08-BOOT-IDENTITY-AND-POLICY.md) — BootVolume, DSN, policy, protected state.
- [09-CXL-PERSISTENT-BOOT-LAYOUT.md](09-CXL-PERSISTENT-BOOT-LAYOUT.md) — on-media format, LSA and atomic metadata.
- [10-BOOT-MANIFEST.md](10-BOOT-MANIFEST.md) — exact signed manifest semantics.
- [11-TEMPORARY-HDM-BOOT-APERTURE.md](11-TEMPORARY-HDM-BOOT-APERTURE.md) — temporary mapping lifecycle.
- [12-SECURE-BOOT-AND-ANTI-ROLLBACK.md](12-SECURE-BOOT-AND-ANTI-ROLLBACK.md) — trust/security model.
- [13-HYBRID-BOOT-INFO-ABI.md](13-HYBRID-BOOT-INFO-ABI.md) — handoff structures/registers.
- [14-SINGNEXTOS-HANDOFF-AND-AUTHORITY.md](14-SINGNEXTOS-HANDOFF-AND-AUTHORITY.md) — fresh provider authority transition.
- [15-RECOVERY-AND-A-B-BOOT.md](15-RECOVERY-AND-A-B-BOOT.md) — updates/recovery/reset interactions.
- [16-FAILURE-MODEL.md](16-FAILURE-MODEL.md) — failure taxonomy and actions.
- [17-SIMULATOR-BACKEND.md](17-SIMULATOR-BACKEND.md) — deterministic model-first architecture.
- [18-QEMU-AND-REAL-HARDWARE-PATH.md](18-QEMU-AND-REAL-HARDWARE-PATH.md) — backend evolution.
- [19-IMPLEMENTATION-ROADMAP.md](19-IMPLEMENTATION-ROADMAP.md) — incremental phases/exit criteria.
- [20-TEST-AND-VALIDATION-PLAN.md](20-TEST-AND-VALIDATION-PLAN.md) — test matrix.
- [21-CROSS-PROJECT-CONTRACTS.md](21-CROSS-PROJECT-CONTRACTS.md) — responsibilities and non-authority boundaries.
- [22-NON-GOALS.md](22-NON-GOALS.md) — explicit exclusions.
- [23-OPEN-QUESTIONS.md](23-OPEN-QUESTIONS.md) — only hardware/protocol choices that remain open.
- [24-DECISION-RECORD.md](24-DECISION-RECORD.md) — ADR-style frozen decisions.
- [25-SOURCE-EVIDENCE.md](25-SOURCE-EVIDENCE.md) — audited source/doc map and external references.
