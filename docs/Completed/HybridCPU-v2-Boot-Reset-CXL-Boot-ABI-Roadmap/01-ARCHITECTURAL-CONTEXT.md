# Architectural Context

## Purpose

Этот документ связывает новый boot/reset contract с текущими архитектурными границами HybridCPU-v2 и SingNextOS. Он не переносит CXL runtime provider в firmware и не меняет ownership model SingNextOS.

## Existing HybridCPU-v2 constraints

По текущему README/WhiteBook HybridCPU-v2 — fixed 8-slot VLIW runtime, 4-way SMT, 32×64-bit architectural integer registers per virtual thread, five-stage `IF/ID/EX/MEM/WB` shell. Machine state явно разделяет Architectural/Frontend/Scheduler/Pipeline/Replay/Backend/Evidence; PC и register state становятся архитектурной истиной на publication/retire boundaries. `RetireCoordinator` отдельно публикует `PcWrite` в `ArchContextState.CommittedPc`.

Boot/reset должен поэтому появиться **до** обычного pipeline execution и не моделироваться как synthetic retired instruction. Reset — platform event, который атомарно устанавливает исходное architectural/backend/pipeline state, после чего обычный fetch начинает читать ROM bundle.

Новый слой должен переиспользовать существующее разделение `evidence != authority`. Собственный HybridCPU virtualization/CXL roadmap уже запрещает превращать replay/certificates/device facts в memory/OS authority и не требует CXL lane/opcode.

## Existing SingNextOS constraints

### Region authority

SingNextOS memory authority строится вокруг `OwnedRegion`, `RegionAuthority`, `RegionGeneration`, `MutationEpoch`, `RegionUse` и borrow/MOVE semantics. Mapping/backing не равен ownership. Поэтому boot CXL HPA mapping не может быть передан как “готовый OwnedRegion”.

### Provider decomposition

Существующий CXL roadmap разделяет узкие provider roles: discovery, CXL.io, memory, coherent access, fabric, security evidence. Raw HDM index, DPA, HPA-to-DPA interleave, CCI/mailbox offsets, ports/routes/MLD binding остаются provider-private.

### Type-3 memory provider

CXL Type-3 backing должен оставаться provider detail под обычным `OwnedRegion`. Provider может создавать/менять HPA/HDM/fabric binding и свои generations; region layer продолжает владеть allocation identity и authority.

### Fabric/pooling/reconfiguration

Fabric Manager предоставляет reconfiguration observations и provider-private binding generations. Он не мутирует `RegionAuthority` напрямую. Boot firmware по той же причине не должен обладать authority, которую может “передать” в SingNextOS.

### Post-CXL operability

Post-CXL roadmap требует fail-closed behavior для reset/reconfiguration/stale generation/ambiguous state. Перезапуск создаёт новые runtime generations и не наследует stale external authority. Boot handoff обязан следовать этому правилу.

## Architectural boundary introduced here

```text
                 PRE-OS DOMAIN                         OS DOMAIN

 Boot ROM / Stage1                                     SingNextOS
 ─────────────────────────────────────────────────────────────────────────
 BootPolicy selects BootVolumeId                       parses BootInfo
 PCI/CXL enumeration                                  fresh discovery
 DSN/BDF/topology = evidence                           provider admission
 temporary HPA/HDM mapping                            new mapping generation
 signed image verification                            RegionAuthority
 copy to system RAM                                   OwnedRegion/RegionUse
 BootInfo                                             normal lifetime/reclaim

             no authority continuity across this line
```

Handoff переносит **facts**, не authority objects.

## Architecture contracts versus implementations

### Architecture contract

Stable across simulator/QEMU/hardware:

- Reset ABI and `PlatformId` profile.
- `BootVolumeId`, `ReplicaId`, `ImageId`, generation semantics.
- BootPolicy/BootState logical model.
- BootVolumeHeader + Manifest on-media formats.
- `HybridBootInfo` and register-level entry ABI.
- recovery/A-B state machine.
- provider fresh-admission rule.
- security/rollback invariants.

### Backend implementation

Replaceable:

- `IPciConfigAccess` implementation.
- CXL capability/DVSEC parser source.
- mailbox transport.
- exact HDM register programming.
- ACPI/CEDT/host firmware discovery.
- OTP/NVRAM hardware.
- crypto accelerator.
- persistence flush primitive.
- reset controller hardware wiring.

## Why direct reset XIP from CXL is rejected

Direct reset-vector execution from CXL would require a stable CXL mapping before any trusted software executes. That mapping itself depends on topology/decoder/fabric state and would either be pre-provisioned outside the defined authority model or turn hardware identifiers into boot semantics. It also makes link loss/topology reconfiguration affect instruction fetch before the OS can establish provider lifecycle. v1 therefore requires local ROM and copies verified executable code into normal RAM.

## Why not UEFI

UEFI can solve generic discovery/boot, but importing a full UEFI model would unnecessarily enlarge the immutable/firmware TCB and introduce generic protocols/runtime services not needed by this research CPU. The selected ABI takes useful principles — immutable root, enumerated media, signed manifests, recovery — without making EFI handles, device paths or variables architectural contracts.

## Terminology

- **Stage-0 / Boot ROM** — immutable/local code entered at reset.
- **Stage-1** — signed, replaceable early loader copied to normal RAM.
- **BootVolume** — logical persistent boot volume identified by `BootVolumeId`.
- **Replica** — one physical copy of a BootVolume, unique `ReplicaId`.
- **Boot anchor** — fixed-format metadata prefix relative to persistent capacity base.
- **Boot locator** — optional cheap metadata that points toward canonical boot anchor/manifest; untrusted.
- **Boot aperture** — firmware-reserved HPA range temporarily mapped to one CXL persistent range.
- **Evidence** — observation used for matching/diagnostics/revalidation, not authority.
- **Authority** — live generation-bound permission/ownership accepted by SingNextOS.
- **Confirmed image** — last image explicitly acknowledged successful by SingNextOS.
- **Trial image** — candidate update allowed bounded boot attempts but not yet confirmed.

## Related documents

Reset mechanics are in [04-RESET-ABI.md](04-RESET-ABI.md); SingNextOS transition is in [14-SINGNEXTOS-HANDOFF-AND-AUTHORITY.md](14-SINGNEXTOS-HANDOFF-AND-AUTHORITY.md); existing source evidence is mapped in [25-SOURCE-EVIDENCE.md](25-SOURCE-EVIDENCE.md).
