# 21 — Cross-Project Contracts and Responsibility Boundaries

## 1. The contract boundary

`HybridCPU-v2` owns reset execution and the minimal pre-OS platform substrate. SingNextOS owns runtime OS authority. CXL hardware and fabric identifiers may cross the handoff only as typed evidence/diagnostics; they do not cross as capabilities.

This is intentionally consistent with the existing SingNextOS CXL roadmaps: provider-private DPA/HDM/fabric details stay below provider-neutral authority, while `OwnedRegion` and generation-bound region authority remain OS concepts. The boot architecture adds a locator/trust path, not a parallel ownership model.

## 2. Responsibility table

| Component | Responsibility | MUST NOT do |
|---|---|---|
| **HybridCPU reset logic** | Establish reset PC/state; select boot execution context; quiesce/initialize architectural and microarchitectural state; report reset cause; expose local ROM | Discover CXL; select OS image; embed CXL device index in ISA; preserve stale software authority across reset |
| **Boot ROM / Stage-0** | Bounded policy read; minimal PCI/CXL discovery; optional LSA locator read; temporary boot mapping; manifest authentication; rollback gate; copy/verify Stage-1; dispatch local recovery | Become BIOS/UEFI-style general runtime; enumerate everything indefinitely; own SingNext regions; expose raw HW IDs as authority; execute unsigned production payloads |
| **Stage-1** | Verify/copy kernel/services; validate compatibility; build `HybridBootInfo`; finalize handoff; optional richer diagnostics within signed code | Create SingNext `OwnedRegion`; retain permanent firmware services; XIP kernel from CXL in v1; convert aperture/DSN/BDF to capability |
| **Platform firmware backend** | Implement config access, timer, host/root/endpoint decoder operations, trust/protected state, recovery medium and platform resource arbitration | Change logical boot identity semantics per machine; require a universal hard-coded HPA; silently reuse OS provider generations |
| **CXL Type-3 device** | Provide CXL.io management/config surfaces, persistent capacity, optional LSA and device identity/evidence; respond to decoder/memory accesses | Decide OS authority; authoritatively select active A/B slot; treat DSN as BootVolume; mint SingNext capabilities |
| **SingNextOS early boot** | Validate BootInfo; consume boot evidence as hints/diagnostics; establish ordinary OS runtime; trigger fresh CXL discovery/admission | Trust firmware topology as current authority; create `OwnedRegion` directly from BootInfo HPA/DPA/decoder evidence |
| **SingNextOS CXL provider** | Fresh discovery; placement; provider-private HPA/DPA/HDM/fabric mapping; admission/health; provider generations; invalidation/rebind; back ordinary region authority | Leak DPA/decoder/route as application authority; adopt stale boot mapping without an explicit future protocol |
| **SingNextOS region layer** | Own `RegionId`/generation, `OwnedRegion`, borrow/move/use semantics and publication | Infer ownership from physical address or firmware mapping |
| **Fabric manager** | Topology/resource allocation/reconfiguration; emit provider-consumable reconfiguration evidence and generations | Mint or mutate application/region authority; make switch/port/route ID a long-term OS capability |
| **Boot/update manager** | Write inactive media slot safely; stage protected trial; process authenticated confirmation; advance rollback floor per policy | Mark a slot good before health confirmation; store sole anti-rollback floor on writable CXL media |
| **Trust/protected-state backend** | Protect root/trust epoch, rollback floor and policy/BootState according to platform threat model | Accept writable boot media as root of trust; let malformed device input choose keys/policy |

## 3. Cross-project wire contracts

The stable cross-project artifacts are:

1. `BootVolumeId` semantics: 128-bit logical installation/replica-set identity, never physical device identity or capability.
2. Signed `SingNextBootManifest` v1 and its compatibility fields.
3. `HybridBootInfo` v1 structure and entry register ABI.
4. Reset ABI relevant to OS entry: physical addressing, interrupts disabled, reset reason, boot execution context.
5. Security semantics: verified/development/recovery flags, image generation and rollback result.
6. Evidence classification: hardware identifiers and temporary mapping descriptors are non-authoritative.

The following are deliberately **not** cross-project stable identities: PCI BDF; PCIe/CXL DSN; CXL device/enumeration index; root-port/switch/port IDs; HDM decoder index; DPA; HPA; component-register offset; mailbox endpoint; MLD logical-device assignment; fabric route.

## 4. Handoff authority protocol

```text
Stage-0 / Stage-1                       SingNextOS
------------------                      ----------
physical discovery
policy match
signed manifest verify
firmware HDM boot mapping
copy verified payloads to RAM
build BootInfo evidence
             --------------------->     validate BootInfo
                                         initialize OS platform layer
                                         fresh CXL discovery
                                         provider admission
                                         bind new provider generation
                                         create normal region authority
                                         publish/use OwnedRegion
             <--- optional release ---  dispose/reprogram boot aperture
```

The semantic commit point is **provider admission under the current SingNextOS generation**, not kernel entry and not possession of an HPA. Even when firmware and OS happen to program identical decoder values, identity/equality of those numbers conveys no authority continuity.

## 5. Evidence classes in `HybridBootInfo`

| Class | Examples | OS semantics |
|---|---|---|
| Verified boot result | `BootVolumeId`, `ImageId`, signed generation, manifest digest | Trusted statement about what firmware authenticated, subject to BootInfo integrity; not runtime CXL authority |
| Physical evidence | DSN, BDF, device class/capability digest, path diagnostics | Hint/correlation/diagnostics; must be revalidated |
| Temporary firmware state | aperture HPA/size, decoder evidence, firmware generation | Disposable bootstrap state; not `OwnedRegion` |
| Platform facts | RAM map, reserved ranges, ROM location, loaded kernel range | Authoritative for the boot ABI/platform handoff until OS platform management supersedes where defined |
| Security status | production/dev/recovery, verification algorithm IDs, trust epoch | Input to OS policy/audit; cannot grant CXL region authority |
| OS authority | **none** | BootInfo contains no SingNext capability |

This avoids an overly broad claim that “nothing in BootInfo is authoritative”: the physical memory map and loaded ranges are platform handoff facts. The specific prohibition is that **no CXL boot evidence is SingNext runtime authority**.

## 6. Generation domains must remain separate

Never compare unrelated counters just because they are integers:

```text
ImageGeneration          signed software release / rollback domain
BootState sequence       protected atomic-record freshness
FirmwareMappingGeneration temporary aperture lifecycle
ProviderGeneration       SingNext provider/device admission lifecycle
RegionGeneration         SingNext region authority lifecycle
FabricBindingGeneration  fabric binding/reconfiguration lifecycle
```

No rule such as `ImageGeneration == ProviderGeneration` is valid. Translation between domains requires an explicit event/protocol, not numeric coincidence.

## 7. Reset contract across projects

A reset invalidates transient boot/OS authority domains. Warm reset may leave CXL link/decoder electrical state programmed if platform policy permits, but software treats it as untrusted residue until Stage-0 re-establishes its own temporary generation and SingNext later performs fresh admission.

A SingNext-requested reboot may carry a small authenticated/reset-scratch **request** (normal/recovery/update-trial) if a platform implementation provides it. That request is policy input, not preserved CXL authority. Crash/watchdog reboot MUST NOT depend on orderly provider shutdown.

## 8. Versioning and compatibility

`FirmwareAbi` and `CpuBootAbi` use major/minor semantics: incompatible required major rejects the candidate; additive optional capabilities are feature bits/TLVs with length bounds. Manifest `RequiredFirmwareAbi` describes what the image needs; BootInfo reports what firmware actually provided. A new backend or QEMU path does not justify changing these numbers.

Persistent media structures have independent format versions. This allows a v2 on-media locator, for example, without changing the kernel entry ABI when semantics are compatible.

## 9. Change-control rule

A proposed change requires a cross-project ADR if it does any of the following: changes `BootVolumeId` semantics; exposes a new physical identifier to SingNext as anything stronger than evidence; permits adopting firmware mappings as provider authority; changes reset/entry registers; changes anti-rollback commit semantics; allows CXL XIP; adds a CXL-specific instruction; or makes fabric manager state a prerequisite authority for `OwnedRegion`.

Backend-only changes—different PCI host access, mailbox transport, decoder register programming, aperture HPA placement, secure-element driver—do not require cross-project ABI changes when they preserve these semantics.
