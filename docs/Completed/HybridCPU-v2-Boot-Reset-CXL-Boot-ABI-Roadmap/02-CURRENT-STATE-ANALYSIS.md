# Current State Analysis

## Research method and confidence

The repositories were inspected through their current GitHub `master` web surfaces on 2026-09-18. Direct runtime cloning was unavailable in the execution environment, so this audit is bound to the files/pages explicitly listed in [25-SOURCE-EVIDENCE.md](25-SOURCE-EVIDENCE.md). Claims about absence are deliberately scoped: “not defined in inspected authoritative surfaces”, not “no line anywhere can contain this word”.

## 1. HybridCPU-v2 current startup/reset state

### What is defined today

The current architecture defines normal execution after a PC exists:

- architectural state includes registers, PC, memory, trap and retire publication;
- `Processor.CPU_Core` has a five-stage pipeline, fetching a 256-byte native VLIW bundle;
- `RetireCoordinator` publishes PC changes with `ArchContextState.CommittedPc = record.Value`;
- `CoreRuntimeState` constructs architectural, frontend, replay, retire, scheduling, execution and memory-pipeline state owners;
- the memory timing controller accepts read/store requests with an address and a `deviceId`, captures a `PhysicalMemoryBankBinding`, and advances a platform edge.

### What is not presently a frozen architecture contract

In the inspected current README, execution-model WhiteBook and current state owners there is no independent normative definition of:

- reset vector;
- initial PC source;
- architectural cold/warm reset state;
- immutable ROM;
- firmware execution environment;
- platform physical address map including ROM/MMIO;
- boot image handoff ABI;
- reset reason latch;
- CXL pre-OS discovery.

Therefore **the current “initial PC” is implementation/harness-provided rather than a documented Reset ABI**. This roadmap does not pretend an existing magic constant is architectural; Phase 0 will add a test that characterizes current harness initialization before changing it.

## 2. Initial PC and architectural PC ownership

`RetireCoordinator.ApplyPcWrite()` demonstrates the existing steady-state rule: PC publication is controlled at retire. Reset is different: it must initialize `CommittedPc` and all frontend/backend aliases *before any instruction retires*. It is incorrect to inject a fake `PcWrite` retire record to model reset because this would:

- make reset depend on active pipeline machinery;
- produce fake retirement evidence;
- leave replay/rename/queues potentially stale;
- make reset ordering ambiguous.

New `HybridResetController` must own an out-of-band reset transaction that establishes all architectural and microarchitectural reset invariants together.

## 3. Firmware/ROM abstraction

No dedicated ROM/firmware abstraction was found in the inspected live surfaces. Add a platform-level address region abstraction rather than hiding ROM inside the instruction loader:

```csharp
interface IPlatformAddressSpace {
    PlatformReadResult Read(ulong physicalAddress, Span<byte> destination, AccessType access);
    PlatformWriteResult Write(ulong physicalAddress, ReadOnlySpan<byte> source, AccessType access);
    PlatformRegionDescriptor Describe(ulong physicalAddress);
}
```

Reference region types: `SystemRam`, `ImmutableRom`, `BootScratch`, `Mmio`, `FirmwareReserved`, `CxlBootAperture`, `Unmapped`.

The normal memory/pipeline code should consume this routing seam for instruction fetch/data access; CXL-specific semantics must stay below the platform/CXL adapter.

## 4. Physical memory and timed memory

`MemoryCycleController` is already a useful integration seam because it has explicit request identity, `deviceId`, `PhysicalMemoryBankBinding`, queues, completions and platform-cycle advancement. However, its `deviceId` is a runtime memory-agent/materialization identifier and MUST NOT be repurposed as BootVolume identity.

Required changes:

- introduce a physical address map in front of or below MemorySubsystem routing;
- make ROM/read-only and MMIO regions explicit;
- represent the temporary CXL aperture as an address-space region backed by an ephemeral mapping object;
- keep `deviceId` internal to the memory backend;
- ensure reset cancels/drains all outstanding controller requests and clears binding snapshots.

## 5. MMIO and platform devices

The inspected root/execution docs do not expose a frozen system-wide MMIO map. Existing runtime has device/DMA/external-accelerator contours, but those are not equivalent to pre-OS PCI ECAM/CXL component-register discovery.

Add a new platform namespace with two layers:

```text
PlatformPhysicalAddressMap
  -> Immutable ROM / RAM / MMIO region routing

Platform bus services
  -> IPciConfigAccess
  -> IMmioRegisterAccess
  -> ICxlPrebootTransport
```

Do not make CXL mailbox access a normal ISA instruction.

## 6. Interrupts, exceptions and traps

The live architecture has stage-aware exception/fault delivery and trap publication at the retire window; the README explicitly avoids claiming a universal precise-exception theorem. Boot reset precedes this steady-state machinery.

v1 reset contract therefore requires:

- external interrupts masked while Stage-0 runs;
- pending interrupt state cleared or held except platform reset-cause latch;
- ROM installs a minimal boot fault vector/host exception callback before enabling any optional interrupts;
- synchronous faults in Stage-0 are classified into boot failures, never injected as normal SingNextOS traps;
- Stage-1 may opt into the normal trap mechanism after it has a stack/memory map.

No changes to normal exception instruction encodings are required.

## 7. Privilege

The current public architecture uses neutral runtime domains/guards and virtualization authority, but the inspected base README does not freeze a conventional machine/supervisor/user privilege ladder for reset. This roadmap avoids inventing a CXL-specific privilege mode.

The Reset ABI introduces a **PlatformFirmwareExecutionDomain** as a platform state, not a new opcode-visible privilege level. It grants only reset-local access to ROM, boot scratch, protected boot-state services and platform PCI/CXL configuration. At kernel entry it is dropped/closed. If future hardware maps this state onto an ISA privilege level, that mapping is profile-specific.

## 8. ISA and frontend

Existing ISA facts useful for boot:

- 32×64-bit x registers per virtual thread;
- x0 is treated as non-writable in retire code;
- 256-byte fixed VLIW bundles;
- ordinary instruction fetch reads memory;
- control/PC publication already exists.

Needed boot change: initial fetch source = ROM physical address. Not needed: `CXL_BOOT`, `HDM_MAP`, `READ_LSA`, `PCI_CFG` opcodes. Stage-0 may be compiled into normal HybridCPU instructions and call privileged platform services modeled outside the guest/application instruction surface.

## 9. Pipeline and replay reset impact

Reset must clear at least:

- IF/ID/EX/MEM/WB latches;
- outstanding scheduler nominations/admission state;
- loop/replay buffers and replay generation;
- rename/commit/free-list to canonical architectural mapping;
- retire queues/records;
- outstanding timed-memory requests/completions;
- assist/DMA/external-runtime pending work that could publish after reset;
- transient cache/SRF state according to reset profile.

This is why reset belongs above `CoreRuntimeState` owners and not in any one stage.

## 10. Loader/simulator/runtime

Current repository includes assemblers/compilers/runtime harnesses but no frozen Boot ROM loader contract. The simulator-first design should add a platform boot harness that feeds ROM bytes through the same fetch path rather than directly setting PC to a test program. Legacy harness direct-load remains a test-only compatibility mode until migrated.

## 11. Existing platform/device abstraction reuse

Reusable ideas/surfaces:

- `CoreRuntimeState` containment for coordinated reset;
- normal physical-memory subsystem and timed controller for RAM fetch/copy;
- runtime/fake backend discipline used by virtualization/CXL roadmap;
- existing evidence vs authority rules;
- `HybridCPU_ExternalRuntime.Contracts` patterns for versioned DTOs.

Do **not** put Boot ABI into external-accelerator contracts. Add a small dedicated `HybridCPU_Boot.Contracts` project so SingNextOS can consume wire-layout definitions without referencing simulator internals.

## 12. Concrete HybridCPU-v2 code impact

### Existing files/areas to modify

| Existing area | Change |
|---|---|
| `HybridCPU_ISE/CloseToHSL/Core/State/CoreRuntimeState.cs` | expose coordinated reset hooks / clear state owners; no CXL policy here |
| architectural register/PC owner and `RetireCoordinator` vicinity | add direct reset initialization path for `CommittedPc`/regs; preserve retire-only mutation during normal execution |
| `Processor.CPU_Core` / owner of `ExecutePipelineCycle()` | gate cycles during reset, first fetch from reset PC, park non-boot VTs/cores |
| frontend fetch path | route physical fetch through platform address map; ROM is ordinary executable memory region |
| `HybridCPU_ISE/CloseToHSL/Memory/Timing/MemoryCycleController.cs` and MemorySubsystem | reset/cancel outstanding requests; platform region routing; temporary aperture backing |
| solution/project wiring | add `HybridCPU_Boot.Contracts`, platform boot runtime and test project references |
| `HybridCPU_ISE.Tests` | reset, ROM, CXL discovery, mapping, ABI, security, A/B/fault deterministic suites |

### New modules

```text
HybridCPU_Boot.Contracts/
  ResetAbi.cs
  HybridBootPolicy.cs
  SingNextBootManifest.cs
  HybridBootInfo.cs
  BootVolumeFormat.cs

HybridCPU_ISE/CloseToHSL/Platform/
  Reset/HybridResetController.cs
  Memory/PlatformPhysicalAddressMap.cs
  Firmware/Stage0Runtime.cs
  Firmware/Stage1Runtime.cs                 # simulator reference, not ROM ABI itself
  Boot/BootPolicyStore.cs
  Boot/BootStateStore.cs
  Boot/BootSelector.cs
  Boot/BootImageVerifier.cs
  Pci/IPciConfigAccess.cs
  Cxl/Preboot/ICxlPrebootTransport.cs
  Cxl/Preboot/CxlBootDiscovery.cs
  Cxl/Preboot/TemporaryBootApertureManager.cs
  Security/IBootTrustStore.cs

HybridCPU_ISE/CloseToHSL/Platform/Model/
  ModelPciConfigAccess.cs
  ModelCxlType3BootDevice.cs
  ModelCxlMailbox.cs
  ModelProtectedNvram.cs
  ModelOtpFuses.cs
  ModelBootFaultInjector.cs
```

Final class names may follow repository naming conventions, but ownership boundaries above are normative.

## 13. SingNextOS code impact boundary

This roadmap intentionally reuses, rather than duplicates:

- `OwnedRegion` / `RegionAuthority` / `RegionUse`;
- `ICxlDiscoveryProvider`, `ICxlMemoryProvider`, `ICxlFabricProvider` semantics;
- provider/private HPA-DPA topology;
- generation invalidation rules;
- reset/reconfiguration fail-closed behavior.

Required integration is a new early-boot consumer of `HybridBootInfo` plus a provider-admission bridge. It must **not** add a parallel “firmware-owned region” authority type.

## 14. What remains outside ISA

- boot target policy;
- PCI config enumeration;
- CXL mailbox/LSA;
- HDM decoder programming;
- crypto verification;
- anti-rollback storage;
- image format and A/B state;
- reset reason persistence;
- platform recovery.

These are platform/firmware contracts. Ordinary CPU loads/stores/fetch plus existing control flow are sufficient for executing verified Stage-0/Stage-1/kernel code.
